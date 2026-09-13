using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TuckPane.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TuckPane;

public sealed partial class NoteWindow
{
    private MenuFlyout? _imageContextMenu;
    private readonly SemaphoreSlim _imageClipboardGate = new(1, 1);

    private async Task<bool> HandleImageMessageAsync(string type, JsonElement message)
    {
        if (type == "imageEditError")
        {
            ShowError(AppStrings.Get(message.TryGetProperty("tooLarge", out var large) && large.ValueKind == JsonValueKind.True
                ? "NoteTooLarge" : "NoteImageEditError"));
            return true;
        }
        if (type is not ("imageMenu" or "imageClipboard")) return false;
        if (!message.TryGetProperty("token", out var id) || id.GetString() is not { Length: > 0 and <= 24 } token) return true;
        if (type == "imageMenu")
        {
            if (!message.TryGetProperty("x", out var x) || !x.TryGetDouble(out double left) || !double.IsFinite(left) ||
                !message.TryGetProperty("y", out var y) || !y.TryGetDouble(out double top) || !double.IsFinite(top)) return true;
            ShowImageContextMenu(token, new Point(Math.Clamp(left, 0, Editor.ActualWidth), Math.Clamp(top, 0, Editor.ActualHeight)));
            return true;
        }

        string action = message.GetProperty("action").GetString() ?? string.Empty;
        if (action is not ("copy" or "cut" or "paste")) return true;
        bool ok = false;
        string? dataUrl = null, text = null;
        await _imageClipboardGate.WaitAsync();
        try
        {
            if (!_visible || _permanentClose) return true;
            if (action == "paste")
            {
                DataPackageView clipboard = Clipboard.GetContent();
                if (clipboard.Contains(StandardDataFormats.Bitmap))
                {
                    using var input = await (await clipboard.GetBitmapAsync()).OpenReadAsync();
                    using var png = await EncodeClipboardPngAsync(input);
                    if (png.Size * 4 / 3 + 32 > NoteStore.MaximumHtmlLength) throw new InvalidDataException("Image exceeds note limit.");
                    using var reader = new DataReader(png.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)png.Size);
                    byte[] bytes = new byte[(int)png.Size];
                    reader.ReadBytes(bytes);
                    dataUrl = "data:image/png;base64," + Convert.ToBase64String(bytes);
                }
                else if (clipboard.Contains(StandardDataFormats.Text))
                {
                    text = await clipboard.GetTextAsync();
                    if (text.Length > NoteStore.MaximumHtmlLength) throw new InvalidDataException("Text exceeds note limit.");
                }
                ok = true;
            }
            else
            {
                byte[] bytes = NoteImageData.Decode(message.GetProperty("dataUrl").GetString() ?? string.Empty);
                using var input = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(input))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }
                input.Seek(0);
                using var png = await EncodeClipboardPngAsync(input);
                var content = new DataPackage { RequestedOperation = action == "cut" ? DataPackageOperation.Move : DataPackageOperation.Copy };
                content.SetBitmap(RandomAccessStreamReference.CreateFromStream(png));
                foreach (int delay in new[] { 0, 50, 100, 200, 400 })
                {
                    if (delay > 0) await Task.Delay(delay);
                    if (_permanentClose || !_visible) break;
                    ok = Clipboard.SetContentWithOptions(content, new ClipboardContentOptions { IsAllowedInHistory = true });
                    if (ok) break;
                }
                // Materialize the bitmap before disposing its backing stream and acknowledging a cut.
                if (ok) Clipboard.Flush();
                else ShowError(AppStrings.Get("NoteClipboardWriteError"));
            }
        }
        catch (Exception ex)
        {
            ok = false;
            AppLogger.Error("便签图片剪贴板操作失败。", ex);
            if (!_permanentClose) ShowError(AppStrings.Get(action == "paste" ? "NoteImagePasteError" : "NoteClipboardWriteError"));
        }
        finally
        {
            _imageClipboardGate.Release();
            if (!_permanentClose && Editor.CoreWebView2 is { } core)
            {
                ok = ok && _visible;
                string result = JsonSerializer.Serialize(new { token, ok, dataUrl, text });
                await core.ExecuteScriptAsync($"window.__tuckpane?.completeImageClipboard({result})");
            }
        }
        return true;
    }

    private static async Task<InMemoryRandomAccessStream> EncodeClipboardPngAsync(IRandomAccessStream input)
    {
        if (input.Size > NoteStore.MaximumHtmlLength) throw new InvalidDataException("Clipboard image exceeds note limit.");
        var output = new InMemoryRandomAccessStream();
        try
        {
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
            using var bitmap = await decoder.GetSoftwareBitmapAsync();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            output.Seek(0);
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    private void ShowImageContextMenu(string token, Point position)
    {
        _imageContextMenu?.Hide();
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        _imageContextMenu = menu;
        foreach (var (action, key, shortcut) in new[]
        {
            ("copy", "NoteCopyImage", "Ctrl+C"), ("cut", "NoteCutImage", "Ctrl+X"),
            ("paste", "ContextPaste", "Ctrl+V"), ("delete", "NoteDeleteImage", "Delete")
        })
        {
            var item = new MenuFlyoutItem { Text = AppStrings.Get(key), KeyboardAcceleratorTextOverride = shortcut,
                FontFamily = new FontFamily(AppStrings.FontFamily), CharacterSpacing = AppStrings.CharacterSpacing };
            if (action == "paste")
            {
                try
                {
                    var content = Clipboard.GetContent();
                    item.IsEnabled = content.Contains(StandardDataFormats.Bitmap) || content.Contains(StandardDataFormats.Text);
                }
                catch { item.IsEnabled = false; }
            }
            item.Click += async (_, _) =>
            {
                try
                {
                    if (!_permanentClose && Editor.CoreWebView2 is { } core)
                        await core.ExecuteScriptAsync($"window.__tuckpane?.imageCommand({JsonSerializer.Serialize(token)},{JsonSerializer.Serialize(action)})");
                }
                catch (Exception ex) { AppLogger.Error("便签图片菜单操作失败。", ex); }
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => { if (ReferenceEquals(_imageContextMenu, menu)) _imageContextMenu = null; };
        menu.ShowAt(Editor, new FlyoutShowOptions { Position = position });
    }
}
