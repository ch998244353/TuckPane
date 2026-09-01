using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinUIEx;
using WinRT.Interop;

namespace TuckPane;

public sealed partial class NoteWindow : Window
{
    private readonly AppHost _host;
    private readonly NoteDefinition _definition;
    private readonly NoteStore _store;
    private readonly Guid? _organizerId;
    private string? _externalPath;
    private readonly PortableNoteDocument? _portableDocument;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private readonly AccessibilitySettings _accessibility = new();
    private AppWindow _appWindow = null!;
    private InputNonClientPointerSource? _titleInput;
    private NativeWindowChromeController? _chrome;
    private IntPtr _hwnd;
    private bool _initialized;
    private bool _editorReady;
    private bool _editorInitializing;
    private bool _permanentClose;
    private bool _visible;
    private bool _restoringPlacement;
    private NoteDocument _document = new();
    private Task<NoteDocument>? _documentLoadTask;
    private Uri? _editorUri;
    private bool _renaming;
    private bool _renameCommitInProgress;

    internal NoteWindow(
        AppHost host,
        NoteDefinition definition,
        NoteStore store,
        string? externalPath = null,
        PortableNoteDocument? portableDocument = null,
        Guid? organizerId = null)
    {
        _host = host;
        _definition = definition;
        _store = store;
        _organizerId = organizerId;
        _externalPath = externalPath is null ? null : Path.GetFullPath(externalPath);
        _portableDocument = portableDocument;
        if (_externalPath is not null)
        {
            ArgumentNullException.ThrowIfNull(portableDocument);
            _document = new NoteDocument { Html = portableDocument.Html };
            _documentLoadTask = Task.FromResult(_document);
        }
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragSurface);
        Title = definition.Name;
        NoteTitleText.Text = definition.Name;
        RuledLinesButton.IsChecked = definition.ShowRuledLines;
        SystemBackdrop = new TransparentTintBackdrop(Colors.Transparent);

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await FlushAsync(readEditor: false);

        Activated += NoteWindow_Activated;
        Closed += NoteWindow_Closed;
        WindowRoot.ActualThemeChanged += WindowRoot_ActualThemeChanged;
        ApplyLanguage();
        ApplyTheme();
    }

    internal Guid Id => _definition.Id;
    internal bool IsVisible => _visible;
    internal string? ExternalPath => _externalPath;

    internal async Task ApplyGlobalThemeAsync(NoteTheme theme)
    {
        _definition.Theme = theme;
        if (_portableDocument is not null) _portableDocument.Theme = theme;
        ApplyTheme();
        if (_editorReady && Editor.CoreWebView2 is not null)
        {
            try
            {
                await Editor.CoreWebView2.ExecuteScriptAsync(
                    $"window.__tuckpane?.setTheme({JsonSerializer.Serialize(GetCssPalette())})");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"无法同步便签主题：{Id}", ex);
                ShowError(AppStrings.Format("NoteEditorErrorFormat", ex.Message));
            }
        }
        if (_externalPath is not null) await FlushAsync(readEditor: true);
    }

    internal void RebindExternalPath(string path)
    {
        if (_externalPath is null) throw new InvalidOperationException("Only portable notes can be rebound.");
        _externalPath = Path.GetFullPath(path);
        _definition.Name = Path.GetFileNameWithoutExtension(_externalPath);
        UpdateTitle();
    }

    internal void InitializeHostWindow()
    {
        if (_initialized) return;
        _initialized = true;
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _titleInput = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
        _appWindow.IsShownInSwitchers = true;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsAlwaysOnTop = _host.State.GlobalSettings.NoteAlwaysOnTop;
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }
        long extendedStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        extendedStyle = (extendedStyle | NativeMethods.WS_EX_APPWINDOW) & ~NativeMethods.WS_EX_TOOLWINDOW;
        _ = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(extendedStyle));
        _ = NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        _chrome = new NativeWindowChromeController(_hwnd, DispatcherQueue);
        RestorePlacement();
        _appWindow.Changed += AppWindow_Changed;
        _appWindow.Closing += AppWindow_Closing;
    }

    internal void ApplyAlwaysOnTopSetting()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _host.State.GlobalSettings.NoteAlwaysOnTop;
    }

    internal void ShowAndActivate()
    {
        InitializeHostWindow();
        _visible = true;
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore(activateWindow: true);
        _appWindow.Show(activateWindow: true);
        Activate();
        _ = NativeMethods.SetWindowPos(
            _hwnd,
            _host.State.GlobalSettings.NoteAlwaysOnTop ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW |
            NativeMethods.SWP_NOOWNERZORDER);
        _ = NativeMethods.SetForegroundWindow(_hwnd);
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTitlePassthrough();
            _ = FocusEditorAsync();
        });
    }

    internal void HideForTray()
    {
        if (!_visible) return;
        _visible = false;
        _appWindow.Hide();
    }

    internal void RestoreFromTray()
    {
        if (_permanentClose) return;
        _visible = true;
        _appWindow.Show(activateWindow: false);
    }

    internal async Task HideAsync()
    {
        if (_permanentClose) return;
        await FlushAsync(readEditor: true);
        _visible = false;
        _appWindow.Hide();
    }

    internal async Task<bool> FlushAndHideForDragAsync()
    {
        bool wasVisible = _visible;
        if (!await FlushAsync(readEditor: true))
            throw new IOException(AppStrings.Get("NoteDragSaveFailed"));
        if (wasVisible)
        {
            _visible = false;
            _appWindow.Hide();
        }
        return wasVisible;
    }

    internal void RestoreAfterDrag()
    {
        if (_permanentClose) return;
        _visible = true;
        _appWindow.Show(activateWindow: false);
    }

    internal async Task ClosePermanentlyAsync()
    {
        if (_permanentClose) return;
        await FlushAsync(readEditor: true);
        _permanentClose = true;
        Close();
    }

    internal Task<bool> FlushForExitAsync() =>
        _permanentClose ? Task.FromResult(true) : FlushAsync(readEditor: true);

    internal void ClosePermanentlyWithoutSave()
    {
        if (_permanentClose) return;
        _saveTimer.Stop();
        _permanentClose = true;
        Close();
    }

    internal void UpdateTitle()
    {
        Title = _definition.Name;
        NoteTitleText.Text = _definition.Name;
        AutomationProperties.SetName(NoteTitleText, _definition.Name);
        AutomationProperties.SetAutomationId(NoteTitleText, $"NoteTitle-{Id:N}");
        AutomationProperties.SetHelpText(NoteTitleText, AppStrings.Get("NoteRenameHint"));
        AutomationProperties.SetName(NoteTitleEditor, AppStrings.Get("ContextRename"));
        AutomationProperties.SetAutomationId(NoteTitleEditor, $"NoteTitleEditor-{Id:N}");
    }

    internal void ApplyLanguage()
    {
        UpdateTitle();
        AutomationProperties.SetName(ColorButton, AppStrings.Get("NoteColor"));
        AutomationProperties.SetAutomationId(ColorButton, $"NoteColor-{Id:N}");
        ToolTipService.SetToolTip(ColorButton, AppStrings.Get("NoteColor"));
        string ruledLinesLabel = AppStrings.Get("NoteRuledLines");
        AutomationProperties.SetName(RuledLinesButton, ruledLinesLabel);
        AutomationProperties.SetAutomationId(RuledLinesButton, $"NoteRuledLines-{Id:N}");
        ToolTipService.SetToolTip(RuledLinesButton, ruledLinesLabel);
        AutomationProperties.SetName(CloseButton, AppStrings.Get("CloseNote"));
        AutomationProperties.SetAutomationId(CloseButton, $"CloseNote-{Id:N}");
        ToolTipService.SetToolTip(CloseButton, AppStrings.Get("CloseNote"));
        if (_editorReady) _ = SendEditorLoadAsync();
    }

    private async void NoteWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_editorReady || _editorInitializing) return;
        _editorInitializing = true;
        try
        {
            await EnsureDocumentLoadedAsync();
            await Editor.EnsureCoreWebView2Async();
            if (Editor.CoreWebView2 is not { } core) throw new InvalidOperationException("WebView2 did not initialize.");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, eventArgs) => eventArgs.Handled = true;
            core.NavigationStarting += (_, eventArgs) =>
            {
                if (_editorUri is null || !Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out Uri? target) ||
                    !target.IsFile || !Path.GetFullPath(target.LocalPath)
                        .Equals(Path.GetFullPath(_editorUri.LocalPath), StringComparison.OrdinalIgnoreCase))
                    eventArgs.Cancel = true;
            };
            core.WebMessageReceived += CoreWebView2_WebMessageReceived;
            string htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "NoteEditor.html");
            _editorUri = new Uri(htmlPath);
            Editor.Source = _editorUri;
            _editorReady = true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法初始化便签编辑器：{Id}", ex);
            ShowError(AppStrings.Format("NoteEditorErrorFormat", ex.Message));
        }
        finally
        {
            _editorInitializing = false;
        }
    }

    private async void CoreWebView2_WebMessageReceived(
        Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (_editorUri is null || !Uri.TryCreate(args.Source, UriKind.Absolute, out Uri? source) ||
                !source.IsFile || !Path.GetFullPath(source.LocalPath)
                    .Equals(Path.GetFullPath(_editorUri.LocalPath), StringComparison.OrdinalIgnoreCase)) return;
            using JsonDocument message = JsonDocument.Parse(args.WebMessageAsJson);
            string type = message.RootElement.GetProperty("type").GetString() ?? string.Empty;
            if (type == "shellReady")
            {
                await SendEditorLoadAsync();
                return;
            }
            if (type == "copyText")
            {
                if (!message.RootElement.TryGetProperty("text", out JsonElement textElement) ||
                    textElement.GetString() is not { Length: > 0 } text ||
                    text.Length > NoteStore.MaximumHtmlLength) return;
                var content = new DataPackage();
                content.SetText(text);
                bool copied = false;
                int[] retryDelaysMs = [0, 50, 100, 200, 400];
                foreach (int delayMs in retryDelaysMs)
                {
                    if (delayMs > 0) await Task.Delay(delayMs);
                    copied = Clipboard.SetContentWithOptions(
                        content,
                        new ClipboardContentOptions());
                    if (copied) break;
                }
                if (!copied)
                {
                    AppLogger.Error($"便签文字在 {retryDelaysMs.Length} 次尝试后仍无法写入剪贴板。");
                    ShowError(AppStrings.Get("NoteClipboardWriteError"));
                }
                return;
            }
            if (type is not ("changed" or "fontSize")) return;
            if (message.RootElement.TryGetProperty("fontSize", out JsonElement size) && size.TryGetDouble(out double fontSize))
                _definition.FontSize = Math.Clamp(fontSize, OrganizerNoteRules.MinimumFontSize, OrganizerNoteRules.MaximumFontSize);
            if (type == "changed" && message.RootElement.TryGetProperty("html", out JsonElement html))
            {
                string value = html.GetString() ?? string.Empty;
                if (value.Length <= NoteStore.MaximumHtmlLength) _document.Html = value;
                else ShowError(AppStrings.Get("NoteTooLarge"));
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法处理便签编辑消息：{Id}", ex);
        }
    }

    private async Task SendEditorLoadAsync()
    {
        if (!_editorReady || Editor.CoreWebView2 is null) return;
        _ = Editor.Focus(FocusState.Programmatic);
        string script = $"window.__tuckpane?.load({JsonSerializer.Serialize(_document.Html)}," +
            $"{_definition.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
            $"{JsonSerializer.Serialize(GetCssPalette())},{JsonSerializer.Serialize(_definition.ShowRuledLines)}," +
            $"{JsonSerializer.Serialize(AppStrings.Get("NotePlaceholder"))}," +
            $"{JsonSerializer.Serialize(AppStrings.GetLanguageTag(AppStrings.Language))}," +
            $"{JsonSerializer.Serialize(AppStrings.Get("NotePastedImage"))}," +
            $"{JsonSerializer.Serialize(AppStrings.Get("NoteResizeImage"))})";
        await Editor.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async Task FocusEditorAsync()
    {
        if (!_editorReady || Editor.CoreWebView2 is not { } core) return;
        _ = Editor.Focus(FocusState.Programmatic);
        await core.ExecuteScriptAsync("document.getElementById('editor')?.focus()");
    }

    private async Task ReadEditorStateAsync()
    {
        if (!_editorReady || Editor.CoreWebView2 is null) return;
        string json = await Editor.CoreWebView2.ExecuteScriptAsync("window.__tuckpane?.getState()");
        if (string.IsNullOrWhiteSpace(json) || json == "null") return;
        using JsonDocument state = JsonDocument.Parse(json);
        if (state.RootElement.TryGetProperty("html", out JsonElement html))
        {
            string value = html.GetString() ?? string.Empty;
            if (value.Length <= NoteStore.MaximumHtmlLength) _document.Html = value;
        }
        if (state.RootElement.TryGetProperty("fontSize", out JsonElement size) && size.TryGetDouble(out double fontSize))
            _definition.FontSize = Math.Clamp(fontSize, OrganizerNoteRules.MinimumFontSize, OrganizerNoteRules.MaximumFontSize);
    }

    private async Task<bool> FlushAsync(bool readEditor)
    {
        _saveTimer.Stop();
        try
        {
            await EnsureDocumentLoadedAsync();
            if (readEditor) await ReadEditorStateAsync();
            if (_externalPath is null)
            {
                await _store.SaveAsync(Id, _document);
                await _host.SaveStateAsync();
            }
            else
            {
                PortableNoteDocument portable = _portableDocument!;
                portable.Theme = _definition.Theme;
                portable.FontSize = _definition.FontSize;
                portable.ShowRuledLines = _definition.ShowRuledLines;
                portable.Placement = ToPortablePlacement(_definition.Placement);
                portable.Html = _document.Html;
                await _store.SavePortableAsync(_externalPath, portable);
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法保存便签：{Id}", ex);
            ShowError(AppStrings.Format("NoteSaveErrorFormat", ex.Message));
            return false;
        }
    }

    private async Task EnsureDocumentLoadedAsync()
    {
        _documentLoadTask ??= _store.LoadAsync(Id);
        _document = await _documentLoadTask;
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        foreach (NoteThemeColors colors in NoteThemePalette.All)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = AppStrings.Get(colors.NameKey),
                Tag = colors.Theme,
                IsChecked = colors.Theme == _definition.Theme
            };
            AutomationProperties.SetAutomationId(item, $"NoteTheme-{colors.Theme}");
            item.Click += ThemeItem_Click;
            flyout.Items.Add(item);
        }
        flyout.ShowAt(ColorButton);
    }

    private async void ThemeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: NoteTheme theme }) return;
        try
        {
            await _host.SetNoteThemeAsync(theme);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法保存全局便签主题：{theme}", ex);
            ShowError(AppStrings.Format("NoteSaveErrorFormat", ex.Message));
        }
    }

    private async void RuledLinesButton_Click(object sender, RoutedEventArgs e)
    {
        _definition.ShowRuledLines = RuledLinesButton.IsChecked == true;
        if (_editorReady)
            await Editor.CoreWebView2.ExecuteScriptAsync(
                $"window.__tuckpane?.setRuledLines({JsonSerializer.Serialize(_definition.ShowRuledLines)})");
        await PersistMetadataAsync();
        await FocusEditorAsync();
    }

    private async Task PersistMetadataAsync()
    {
        if (_externalPath is null) await _host.SaveStateAsync();
        else await FlushAsync(readEditor: false);
    }

    private static PortableNotePlacement? ToPortablePlacement(NoteWindowPlacement? placement) => placement is null
        ? null
        : new PortableNotePlacement
        {
            MonitorDevice = placement.MonitorDevice,
            XDip = placement.XDip,
            YDip = placement.YDip,
            WidthDip = placement.WidthDip,
            HeightDip = placement.HeightDip
        };

    private void ApplyTheme()
    {
        IReadOnlyDictionary<string, string> css = GetCssPalette();
        Color surface = ParseColor(css["surface"]);
        Color editor = ParseColor(css["editor"]);
        Color accent = ParseColor(css["accent"]);
        Color text = ParseColor(css["text"]);
        WindowFrame.Background = new SolidColorBrush(editor);
        DragTitleBar.Background = new SolidColorBrush(surface);
        NoteTitleText.Foreground = new SolidColorBrush(text);
        NoteTitleEditor.Foreground = new SolidColorBrush(text);
        ColorButton.Foreground = new SolidColorBrush(accent);
        ColorButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(
            ColorHelper.FromArgb(30, accent.R, accent.G, accent.B));
        ColorButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(
            ColorHelper.FromArgb(52, accent.R, accent.G, accent.B));
        ColorButton.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(accent);
        ColorButton.Resources["ButtonForegroundPressed"] = new SolidColorBrush(accent);
        RuledLinesButton.Foreground = new SolidColorBrush(text);
        RuledLinesButton.Resources["ToggleButtonBackgroundPointerOver"] = new SolidColorBrush(
            ColorHelper.FromArgb(24, accent.R, accent.G, accent.B));
        RuledLinesButton.Resources["ToggleButtonBackgroundPressed"] = new SolidColorBrush(
            ColorHelper.FromArgb(42, accent.R, accent.G, accent.B));
        RuledLinesButton.Resources["ToggleButtonBackgroundChecked"] = new SolidColorBrush(
            ColorHelper.FromArgb(68, accent.R, accent.G, accent.B));
        RuledLinesButton.Resources["ToggleButtonBackgroundCheckedPointerOver"] = new SolidColorBrush(
            ColorHelper.FromArgb(88, accent.R, accent.G, accent.B));
        RuledLinesButton.Resources["ToggleButtonBackgroundCheckedPressed"] = new SolidColorBrush(
            ColorHelper.FromArgb(108, accent.R, accent.G, accent.B));
        RuledLinesButton.Resources["ToggleButtonBackgroundCheckedDisabled"] = new SolidColorBrush(
            ColorHelper.FromArgb(34, accent.R, accent.G, accent.B));
        SolidColorBrush checkedForeground = new(text);
        RuledLinesButton.Resources["ToggleButtonForegroundChecked"] = checkedForeground;
        RuledLinesButton.Resources["ToggleButtonForegroundCheckedPointerOver"] = checkedForeground;
        RuledLinesButton.Resources["ToggleButtonForegroundCheckedPressed"] = checkedForeground;
        RuledLinesButton.Resources["ToggleButtonForegroundCheckedDisabled"] = checkedForeground;
        CloseButton.Foreground = new SolidColorBrush(text);
        Color closeHover = _accessibility.HighContrast ? accent : ColorHelper.FromArgb(255, 196, 43, 28);
        Color closePressed = _accessibility.HighContrast ? accent : ColorHelper.FromArgb(255, 164, 38, 25);
        Color closeForeground = _accessibility.HighContrast ? surface : Colors.White;
        CloseButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(closeHover);
        CloseButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(closePressed);
        CloseButton.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(closeForeground);
        CloseButton.Resources["ButtonForegroundPressed"] = new SolidColorBrush(closeForeground);
    }

    private IReadOnlyDictionary<string, string> GetCssPalette()
    {
        if (!_accessibility.HighContrast)
        {
            IReadOnlyDictionary<string, string> theme = NoteThemePalette.Get(_definition.Theme).Css;
            Color editor = ParseColor(theme["editor"]);
            return new Dictionary<string, string>(theme)
            {
                ["rule"] = theme["border"],
                ["caret"] = IsDark(editor) ? "#FFFFFF" : "#000000",
                ["scroll-thumb"] = Darken(editor, .22),
                ["scroll-thumb-hover"] = Darken(editor, .30)
            };
        }
        var settings = new UISettings();
        string foreground = ToHex(settings.GetColorValue(UIColorType.Foreground));
        return new Dictionary<string, string>
        {
            ["surface"] = ToHex(settings.GetColorValue(UIColorType.Background)),
            ["editor"] = ToHex(settings.GetColorValue(UIColorType.Background)),
            ["accent"] = ToHex(settings.GetColorValue(UIColorType.Accent)),
            ["border"] = foreground,
            ["text"] = foreground,
            ["muted"] = foreground,
            ["rule"] = foreground,
            ["caret"] = foreground,
            ["scroll-thumb"] = foreground,
            ["scroll-thumb-hover"] = foreground
        };
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private static bool IsDark(Color color) => color.R * 299 + color.G * 587 + color.B * 114 < 128000;
    private static string Darken(Color color, double amount) => ToHex(ColorHelper.FromArgb(255,
        (byte)Math.Round(color.R * (1 - amount)),
        (byte)Math.Round(color.G * (1 - amount)),
        (byte)Math.Round(color.B * (1 - amount))));
    private static Color ParseColor(string value) => ColorHelper.FromArgb(255,
        Convert.ToByte(value.Substring(1, 2), 16),
        Convert.ToByte(value.Substring(3, 2), 16),
        Convert.ToByte(value.Substring(5, 2), 16));

    private async void WindowRoot_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyTheme();
        if (_editorReady)
            await Editor.CoreWebView2.ExecuteScriptAsync($"window.__tuckpane?.setTheme({JsonSerializer.Serialize(GetCssPalette())})");
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e) => await HideAsync();

    private void TitleDragSurface_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateTitlePassthrough();

    private void UpdateTitlePassthrough()
    {
        FrameworkElement target = _renaming ? TitleDragSurface : NoteTitleText;
        if (_titleInput is null || target.ActualWidth <= 0 || target.ActualHeight <= 0) return;
        Rect bounds = target.TransformToVisual(WindowRoot)
            .TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        _titleInput.SetRegionRects(
            NonClientRegionKind.Passthrough,
            [new RectInt32(
                (int)Math.Floor(bounds.X * scale),
                (int)Math.Floor(bounds.Y * scale),
                Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(bounds.Height * scale))) ]);
    }

    private void NoteTitleText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        BeginRename();
        e.Handled = true;
    }

    private void BeginRename()
    {
        if (_renaming) return;
        _renaming = true;
        ErrorBar.IsOpen = false;
        NoteTitleEditor.Text = _definition.Name;
        NoteTitleText.Visibility = Visibility.Collapsed;
        NoteTitleEditor.Visibility = Visibility.Visible;
        UpdateTitlePassthrough();
        NoteTitleEditor.Focus(FocusState.Programmatic);
        NoteTitleEditor.SelectAll();
    }

    private async void NoteTitleEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelRename();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitRenameAsync();
        }
    }

    private async void NoteTitleEditor_LostFocus(object sender, RoutedEventArgs e) => await CommitRenameAsync();

    private async Task CommitRenameAsync()
    {
        if (!_renaming || _renameCommitInProgress) return;
        _renameCommitInProgress = true;
        try
        {
            string candidate = NoteTitleEditor.Text;
            if (_externalPath is null)
            {
                if (_organizerId is not Guid organizerId) throw new InvalidOperationException(AppStrings.Get("NoteSaveErrorFormat"));
                await _host.RenameNoteAsync(organizerId, Id, candidate);
            }
            else
            {
                if (!await FlushAsync(readEditor: true)) return;
                _externalPath = await _host.RenameExternalNoteAsync(_externalPath, candidate);
                _definition.Name = candidate.Trim();
                UpdateTitle();
            }
            EndRename();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法重命名便签：{Id}", ex);
            ShowError(ex.Message);
        }
        finally
        {
            _renameCommitInProgress = false;
            if (_renaming)
            {
                NoteTitleEditor.Focus(FocusState.Programmatic);
                NoteTitleEditor.SelectAll();
            }
        }
    }

    private void CancelRename()
    {
        if (!_renaming) return;
        NoteTitleEditor.Text = _definition.Name;
        EndRename();
    }

    private void EndRename()
    {
        _renaming = false;
        NoteTitleEditor.Visibility = Visibility.Collapsed;
        NoteTitleText.Visibility = Visibility.Visible;
        UpdateTitlePassthrough();
    }

    private void RestorePlacement()
    {
        NoteWindowPlacement placement = _definition.Placement ?? new NoteWindowPlacement();
        DisplayInfo display = DisplayPlacementService.GetDisplay(placement.MonitorDevice);
        int width = Math.Max(1, (int)Math.Round(placement.WidthDip * display.Scale));
        int height = Math.Max(1, (int)Math.Round(placement.HeightDip * display.Scale));
        int left = string.IsNullOrWhiteSpace(placement.MonitorDevice)
            ? display.Work.Left + (display.Work.Width - width) / 2
            : display.Work.Left + (int)Math.Round(placement.XDip * display.Scale);
        int top = string.IsNullOrWhiteSpace(placement.MonitorDevice)
            ? display.Work.Top + (display.Work.Height - height) / 2
            : display.Work.Top + (int)Math.Round(placement.YDip * display.Scale);
        NativeMethods.RECT bounds = DisplayPlacementService.Clamp(new NativeMethods.RECT
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        }, display.Work);
        _restoringPlacement = true;
        _appWindow.MoveAndResize(new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
        _restoringPlacement = false;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_restoringPlacement || (!args.DidPositionChange && !args.DidSizeChange) ||
            !NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT bounds)) return;
        DisplayInfo display = DisplayPlacementService.ForBounds(bounds);
        _definition.Placement = new NoteWindowPlacement
        {
            MonitorDevice = display.Device,
            XDip = (bounds.Left - display.Work.Left) / display.Scale,
            YDip = (bounds.Top - display.Work.Top) / display.Scale,
            WidthDip = bounds.Width / display.Scale,
            HeightDip = bounds.Height / display.Scale
        };
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_permanentClose) return;
        args.Cancel = true;
        _ = HideAsync();
    }

    private void NoteWindow_Closed(object sender, WindowEventArgs args)
    {
        WindowRoot.ActualThemeChanged -= WindowRoot_ActualThemeChanged;
        _saveTimer.Stop();
        _chrome?.Dispose();
        _chrome = null;
        Editor.Close();
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
