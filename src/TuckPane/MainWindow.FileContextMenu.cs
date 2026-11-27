using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private readonly FileContextMenuGate _fileContextMenuGate = new();

    private async Task ShowFileContextMenuAsync(FrameworkElement host, WidgetItem item, ContextRequestedEventArgs request)
    {
        try
        {
            FileMenuPopup? screenPosition = GetFileMenuPosition(host, request);
            if (screenPosition is null) return;
            await FileContextMenuOperation.RunAsync(
                enter: () =>
                {
                    _shellContextMenuOpen = true;
                    ContextMenu_Opening(null, EventArgs.Empty);
                },
                prepare: async () =>
                {
                    if (IsDocumentItem(item) && !await _host.FlushPortableWindowAsync(item.FullPath))
                        throw new IOException(AppStrings.Get(item.Kind == WidgetItemKind.PortableTodo
                            ? "TodoDragSaveFailed" : "NoteDragSaveFailed"));
                    return !_closing;
                },
                create: () => ShellContextMenuService.Create(host, item.FullPath, _hwnd, screenPosition.Value),
                handle: _ => Task.FromResult(false),
                refresh: async () =>
                {
                    if (!_closing) await RefreshCatalogAsync(notifyUnsupported: false);
                },
                leave: () =>
                {
                    _shellContextMenuOpen = false;
                    RestoreContextMenuHost();
                },
                cancellationToken: _windowLifetime.Token,
                gate: _fileContextMenuGate,
                handleSelection: selection => HandleFileMenuSelectionAsync(host, item, selection));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Error($"无法打开文件项目菜单：{item.FullPath}", ex);
            if (!_closing) ShowMessage(AppStrings.Format("FileMenuErrorFormat", item.Name, FileMenuCommands.ErrorDetails(ex)), InfoBarSeverity.Error);
        }
    }

    private FileMenuPopup? GetFileMenuPosition(FrameworkElement host, ContextRequestedEventArgs request)
    {
        if (_closing || !host.IsLoaded || host.XamlRoot is null || _hwnd == IntPtr.Zero) return null;
        try
        {
            FrameworkElement content = FindItemPart<FrameworkElement>(host, "ItemContent") ?? host;
            if (content.ActualWidth <= 0 || content.ActualHeight <= 0) content = host;
            Rect local = content.TransformToVisual(WindowRoot).TransformBounds(
                new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            if (local.Width <= 0 || local.Height <= 0) return null;
            var client = new NativeMethods.POINT();
            if (!NativeMethods.ClientToScreen(_hwnd, ref client)) return null;
            double scale = host.XamlRoot.RasterizationScale;
            Point rootOffset = WindowRoot.TransformToVisual(null).TransformPoint(new Point());
            var origin = new Point(client.X + rootOffset.X * scale, client.Y + rootOffset.Y * scale);
            Point? requested = request.TryGetPosition(WindowRoot, out Point position) ? position : null;
            Point screen = FileMenuPosition.Capture(requested, local, origin, scale);
            return requested.HasValue ? FileMenuCursorCapture.Capture(screen, scale)
                : FileMenuPosition.Keyboard(screen, NativeMethods.GetSystemMetrics(40) != 0 ? 0x8u : 0);
        }
        catch (InvalidOperationException) { return null; }
    }

    private async Task<bool> HandleFileMenuSelectionAsync(FrameworkElement host, WidgetItem item, FileMenuSelection selection)
    {
        string? verb = selection.CanonicalVerb?.ToLowerInvariant();
        // Shell's rename verb needs a view host. Keep the application's inline
        // edit and command protection until editing completes.
        if (verb == "rename") { await BeginFileRenameAsync(host, item); return true; }
        if (!selection.IsFallback && !IsDocumentItem(item)) return false;
        switch (verb)
        {
            case "open": await OpenItemAsync(item, doubleTap: false); return true;
            case "copy": await CopyFileItemAsync(item); return true;
            case "cut": await CutFileItemAsync(item); return true;
            case "delete": await DeleteFileItemAsync(item); return true;
            case "properties": await _host.ShellLauncher.PropertiesAsync(item.FullPath).WaitAsync(_windowLifetime.Token); return true;
            default:
                // An opaque extension can move/delete a portable document.
                // Save and detach its editor before handing the path to it, so
                // delayed autosave cannot recreate or overwrite the old path.
                if (IsDocumentItem(item) && verb != "properties")
                {
                    if (!await _host.FlushPortableWindowAsync(item.FullPath))
                        throw new IOException(AppStrings.Get(item.Kind == WidgetItemKind.PortableTodo ? "TodoDragSaveFailed" : "NoteDragSaveFailed"));
                    _host.ClosePortableWindowWithoutSave(item.FullPath);
                }
                return false;
        }
    }
}
