using System.Diagnostics;
using System.Numerics;
using TuckPane.Controls;
using TuckPane.Models;
using TuckPane.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics;
using WinRT.Interop;

namespace TuckPane;

public sealed partial class ConsoleWindow : Window
{
    private readonly AppHost _host;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _placementTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _stateSaveTimer;
    private AppWindow? _appWindow;
    private NativeWindowChromeController? _chrome;
    private bool _closingPermanently;
    private bool _componentReady;
    private bool _initialized;
    private bool _loadingEditor;
    private bool _loadingStartup;
    private bool _loadingOutsideClick;
    private bool _loadingExpandOnHover;
    private bool _loadingCollapseOnPointerLeave;
    private bool _loadingExclusiveExpansion;
    private bool _loadingLanguage;
    private bool _loadingDefaultName;
    private bool _addNameWasEdited;
    private bool _adjustingAddControls;
    private bool _adjustingManageControls;
    private bool _suppressSelection;
    private bool _runtimeApplyScheduled;
    private Guid? _selectedId;
    private OrganizerDefinition? _editing;
    private OrganizerVisualChange _pendingVisualChanges;
    private CancellationTokenSource? _pageTransition;
    private string _defaultAddName = string.Empty;
    private string? _addStoragePath;

    public ConsoleWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        RemoveTextBoxUnderline(AddNameBox, ManageNameBox, ManagePathBox);
        _componentReady = true;
        _defaultAddName = AppStrings.DefaultOrganizerName;
        _loadingDefaultName = true;
        AddNameBox.Text = _defaultAddName;
        _loadingDefaultName = false;
        UpdateAddStoragePath();
        ApplyLanguage();
        ConsoleRoot.RequestedTheme = ElementTheme.Light;
        ApplyTheme();
        SetStartupLoading(true);
        _placementTimer = DispatcherQueue.CreateTimer();
        _placementTimer.Interval = TimeSpan.FromMilliseconds(450);
        _placementTimer.IsRepeating = false;
        _placementTimer.Tick += async (_, _) => await SavePlacementAsync();
        _stateSaveTimer = DispatcherQueue.CreateTimer();
        _stateSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _stateSaveTimer.IsRepeating = false;
        _stateSaveTimer.Tick += StateSaveTimer_Tick;
        RootNavigation.SelectedItem = ManageNavItem;
        AddThemeCombo.SelectedIndex = 0;
    }

    public IntPtr Hwnd { get; private set; }

    internal int ChromeApplyCount => _chrome?.ApplyCount ?? 0;

    internal RectInt32? CurrentBounds => _appWindow is null
        ? null
        : new RectInt32(_appWindow.Position.X, _appWindow.Position.Y, _appWindow.Size.Width, _appWindow.Size.Height);

    public void InitializeHostWindow()
    {
        if (_initialized) return;
        _initialized = true;
        Hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(Hwnd));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }
        _chrome = new NativeWindowChromeController(Hwnd, DispatcherQueue);
        Closed += ConsoleWindow_Closed;
        ApplyNativeWindowChrome();
        RestorePlacement();
        _appWindow.Changed += AppWindow_Changed;
        _appWindow.Closing += AppWindow_Closing;
    }

    public void ApplyTheme()
    {
        GlassTheme theme = _host.State.GlobalSettings.Theme;
        ConsoleRoot.RequestedTheme = GlassThemePalette.IsDark(theme) ? ElementTheme.Dark : ElementTheme.Light;
        ApplyConsoleSurfacePalette(theme);
        if (GlassThemePalette.IsSolid(theme))
        {
            SystemBackdrop = null;
            ConsoleRoot.Background = new SolidColorBrush(GlassThemePalette.SurfaceColor(theme));
        }
        else
        {
            ConsoleRoot.Background = new SolidColorBrush(ColorHelper.FromArgb(1, 255, 255, 255));
            SystemBackdrop = new NeutralAcrylicBackdrop(theme);
        }
        ApplyNativeWindowChrome(refreshFrame: true);
        UpdateThemeCards(theme);
        StartupLoadingOverlay.Background = new SolidColorBrush(GlassThemePalette.SurfaceColor(theme));
    }

    public void RefreshAll(Guid? selectId = null)
    {
        UpdateThemeCards(_host.State.GlobalSettings.Theme);
        UpdateStartupToggle();
        UpdateOutsideClickToggle();
        UpdateShowConsoleToggle();
        UpdateOrganizerOpacitySlider();
        UpdateOutsideClickToggle();
        UpdateExpandOnHoverToggle();
        UpdateCollapseOnPointerLeaveToggle();
        UpdateExclusiveExpansionToggle();
        UpdateExpandOnHoverDelaySlider();
        UpdateCollapseOnPointerLeaveDelaySlider();
        CreateOrganizerButton.IsEnabled = _host.State.Organizers.Count < OrganizerLimits.MaximumOrganizers;
        CreateLimitText.Visibility = _host.State.Organizers.Count >= OrganizerLimits.MaximumOrganizers ? Visibility.Visible : Visibility.Collapsed;
        PopulateManageList(selectId ?? _selectedId);
        UpdateTransferState();
        UpdateAddControls();
    }

    public void ApplyLanguage()
    {
        if (!_componentReady) return;
        bool replaceDefaultName = !_addNameWasEdited;
        _defaultAddName = AppStrings.DefaultOrganizerName;
        if (replaceDefaultName)
        {
            _loadingDefaultName = true;
            AddNameBox.Text = _defaultAddName;
            _loadingDefaultName = false;
        }

        Title = AppStrings.Get("AppTitle");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ConsoleMinimizeButton, AppStrings.Get("WindowMinimize"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ConsoleCloseButton, AppStrings.Get("WindowClose"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExpandOnHoverToggle, AppStrings.Get("ExpandOnHoverTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CollapseOnPointerLeaveToggle, AppStrings.Get("CollapseOnPointerLeaveTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExclusiveExpansionToggle, AppStrings.Get("ExclusiveExpansionTitle"));
        GeneralNavItem.Content = AppStrings.Get("NavGeneral");
        ThemeNavItem.Content = AppStrings.Get("NavTheme");
        AddNavItem.Content = AppStrings.Get("NavAdd");
        ManageNavItem.Content = AppStrings.Get("NavManage");
        MissingStorageInfo.Title = AppStrings.Get("MissingStorage");
        ApplyLocalizedTree(ConsoleRoot);
        PopulateDisplayCombos();
        ApplyTypography(ConsoleRoot);
        foreach (Control control in new Control[] { GeneralNavItem, ThemeNavItem, AddNavItem, ManageNavItem })
        {
            control.FontFamily = new FontFamily(AppStrings.FontFamily);
            control.CharacterSpacing = AppStrings.CharacterSpacing;
        }
        _loadingLanguage = true;
        LanguageCombo.SelectedIndex = (int)_host.State.GlobalSettings.Language;
        _loadingLanguage = false;
        UpdateAddStoragePath();
        ConsoleInfoBar.IsOpen = false;
        PopulateManageList(_selectedId);
        UpdateAddControls();
    }

    public void UpdateTransferState()
    {
        if (DeleteOrganizerButton is not null) DeleteOrganizerButton.IsEnabled = _selectedId is not null && !_host.TransferQueue.IsActive;
    }

    public void ShowTransparencyNotice()
    {
        ConsoleInfoBar.Title = AppStrings.Get("TransparencyTitle");
        ConsoleInfoBar.Message = AppStrings.Get("TransparencyMessage");
        ConsoleInfoBar.Severity = InfoBarSeverity.Informational;
        ConsoleInfoBar.IsOpen = true;
    }

    public void HideToTray()
    {
        FlushPendingManageChanges();
        _appWindow?.Hide();
    }

    public async Task WaitFirstRenderAsync()
    {
        if (!ConsoleRoot.IsLoaded)
        {
            TaskCompletionSource loaded = new();
            RoutedEventHandler handler = null!;
            handler = (_, _) =>
            {
                ConsoleRoot.Loaded -= handler;
                loaded.TrySetResult();
            };
            ConsoleRoot.Loaded += handler;
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        for (int i = 0; i < 8; i++) await Task.Yield();
    }

    public void SetStartupLoading(bool visible)
    {
        StartupLoadingOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            SystemBackdrop = null;
            ConsoleRoot.Background = new SolidColorBrush(GlassThemePalette.SurfaceColor(_host.State.GlobalSettings.Theme));
        }
        else
        {
            ApplyTheme();
        }
    }

    private void ConsoleMinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter) presenter.Minimize();
    }

    private void ConsoleCloseButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void ConsoleCloseButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ConsoleCloseButton.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 43, 28));
        ConsoleCloseButton.Foreground = new SolidColorBrush(Colors.White);
    }

    private void ConsoleCloseButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ConsoleCloseButton.Background = new SolidColorBrush(Colors.Transparent);
        ConsoleCloseButton.Foreground = (Brush)ConsoleRoot.Resources["ConsolePrimaryTextBrush"];
    }

    public void ShowAndActivate(Guid? organizerId = null)
    {
        SetStartupLoading(false);
        _appWindow?.Show();
        Activate();
        _ = NativeMethods.SetForegroundWindow(Hwnd);
        RootNavigation.SelectedItem = ManageNavItem;
        ShowPage(ManagePage);
        PopulateManageList(organizerId ?? _selectedId);
    }

    public void ClosePermanently()
    {
        _closingPermanently = true;
        Close();
    }

    public async Task<bool> ConfirmCancelTransferAndExitAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ConsoleRoot.XamlRoot,
            Title = AppStrings.Get("FilesMovingTitle"),
            Content = AppStrings.Get("FilesMovingMessage"),
            PrimaryButtonText = AppStrings.Get("CancelTransferAndExit"),
            CloseButtonText = AppStrings.Get("ContinueWaiting"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingPermanently) return;
        args.Cancel = true;
        HideToTray();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange)
        {
            DisplayInfo display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
            {
                Left = sender.Position.X,
                Top = sender.Position.Y,
                Right = sender.Position.X + sender.Size.Width,
                Bottom = sender.Position.Y + sender.Size.Height
            });
            int minimumWidth = (int)Math.Round(860 * display.Scale);
            int minimumHeight = (int)Math.Round(600 * display.Scale);
            if (sender.Size.Width < minimumWidth || sender.Size.Height < minimumHeight)
            {
                sender.Resize(new SizeInt32(Math.Max(minimumWidth, sender.Size.Width), Math.Max(minimumHeight, sender.Size.Height)));
                ApplyNativeWindowChrome();
            }
        }
        if (args.DidPositionChange || args.DidSizeChange)
        {
            _placementTimer.Stop();
            _placementTimer.Start();
        }
    }

    private void ConsoleWindow_Closed(object sender, WindowEventArgs args)
    {
        Closed -= ConsoleWindow_Closed;
        _chrome?.Dispose();
        _chrome = null;
    }

    private void ApplyNativeWindowChrome(bool refreshFrame = false)
    {
        if (Hwnd == IntPtr.Zero) return;
        _chrome?.Apply(refreshFrame);
        if (_appWindow is null) return;
        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private static void RemoveTextBoxUnderline(params TextBox[] textBoxes)
    {
        foreach (TextBox textBox in textBoxes)
        {
            textBox.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
            textBox.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
            textBox.Resources["TextControlBorderBrush"] = new SolidColorBrush(Colors.Transparent);
            textBox.Resources["TextControlBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);
            textBox.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(Colors.Transparent);
        }
    }

    private void ApplyConsoleSurfacePalette(GlassTheme theme)
    {
        Windows.UI.Color pane;
        Windows.UI.Color page;
        Windows.UI.Color card;
        Windows.UI.Color title;
        Windows.UI.Color manageRow;
        Windows.UI.Color manageBorder;
        Windows.UI.Color listItem;
        Windows.UI.Color selectedListItem;
        Windows.UI.Color primaryText;
        Windows.UI.Color secondaryText;
        Windows.UI.Color input;
        Windows.UI.Color sliderThumb;
        Windows.UI.Color sliderActive;
        Windows.UI.Color sliderInactive;
        Windows.UI.Color sliderThumbBorder;
        Windows.UI.Color sliderFocusPrimary;
        Windows.UI.Color sliderFocusSecondary;
        switch (theme)
        {
            case GlassTheme.SolidLight:
                pane = ColorHelper.FromArgb(255, 229, 226, 226);
                page = ColorHelper.FromArgb(255, 222, 220, 220);
                card = ColorHelper.FromArgb(255, 245, 243, 238);
                title = ColorHelper.FromArgb(255, 225, 222, 222);
                manageRow = ColorHelper.FromArgb(255, 245, 243, 238);
                manageBorder = ColorHelper.FromArgb(255, 184, 178, 168);
                listItem = ColorHelper.FromArgb(255, 245, 243, 238);
                selectedListItem = ColorHelper.FromArgb(255, 232, 228, 218);
                primaryText = ColorHelper.FromArgb(255, 31, 31, 31);
                secondaryText = ColorHelper.FromArgb(255, 101, 96, 96);
                input = ColorHelper.FromArgb(255, 250, 248, 242);
                sliderThumb = ColorHelper.FromArgb(255, 250, 248, 242);
                sliderActive = ColorHelper.FromArgb(255, 137, 132, 123);
                sliderInactive = ColorHelper.FromArgb(255, 196, 190, 179);
                sliderThumbBorder = ColorHelper.FromArgb(255, 184, 178, 168);
                sliderFocusPrimary = ColorHelper.FromArgb(255, 97, 95, 91);
                sliderFocusSecondary = ColorHelper.FromArgb(255, 250, 248, 242);
                break;
            case GlassTheme.SolidDark:
                pane = ColorHelper.FromArgb(255, 41, 39, 39);
                page = ColorHelper.FromArgb(255, 36, 35, 35);
                card = ColorHelper.FromArgb(255, 59, 57, 57);
                title = ColorHelper.FromArgb(255, 43, 41, 41);
                manageRow = ColorHelper.FromArgb(255, 71, 68, 68);
                manageBorder = ColorHelper.FromArgb(255, 119, 112, 112);
                listItem = ColorHelper.FromArgb(255, 71, 68, 68);
                selectedListItem = ColorHelper.FromArgb(255, 85, 81, 81);
                primaryText = ColorHelper.FromArgb(255, 245, 245, 245);
                secondaryText = ColorHelper.FromArgb(255, 201, 196, 196);
                input = ColorHelper.FromArgb(255, 79, 76, 76);
                sliderThumb = ColorHelper.FromArgb(255, 242, 240, 236);
                sliderActive = ColorHelper.FromArgb(255, 113, 110, 108);
                sliderInactive = ColorHelper.FromArgb(255, 157, 153, 150);
                sliderThumbBorder = ColorHelper.FromArgb(255, 205, 202, 198);
                sliderFocusPrimary = ColorHelper.FromArgb(255, 242, 240, 236);
                sliderFocusSecondary = ColorHelper.FromArgb(255, 87, 84, 82);
                break;
            case GlassTheme.Gray:
            case GlassTheme.FrostedDark:
                pane = ColorHelper.FromArgb(36, 255, 255, 255);
                page = ColorHelper.FromArgb(16, 255, 255, 255);
                card = ColorHelper.FromArgb(42, 255, 255, 255);
                title = ColorHelper.FromArgb(22, 255, 255, 255);
                manageRow = card;
                manageBorder = Colors.Transparent;
                listItem = ColorHelper.FromArgb(18, 255, 255, 255);
                selectedListItem = ColorHelper.FromArgb(52, 255, 255, 255);
                primaryText = ColorHelper.FromArgb(255, 245, 245, 245);
                secondaryText = ColorHelper.FromArgb(255, 201, 196, 196);
                input = ColorHelper.FromArgb(48, 255, 255, 255);
                sliderThumb = ColorHelper.FromArgb(255, 244, 243, 241);
                sliderActive = ColorHelper.FromArgb(255, 115, 118, 121);
                sliderInactive = ColorHelper.FromArgb(255, 158, 161, 163);
                sliderThumbBorder = ColorHelper.FromArgb(255, 210, 208, 204);
                sliderFocusPrimary = ColorHelper.FromArgb(255, 244, 243, 241);
                sliderFocusSecondary = ColorHelper.FromArgb(255, 87, 84, 82);
                break;
            case GlassTheme.FrostedLight:
            default:
                pane = ColorHelper.FromArgb(24, 255, 255, 255);
                page = ColorHelper.FromArgb(10, 255, 255, 255);
                card = ColorHelper.FromArgb(52, 255, 255, 255);
                title = ColorHelper.FromArgb(18, 255, 255, 255);
                manageRow = card;
                manageBorder = Colors.Transparent;
                listItem = ColorHelper.FromArgb(12, 255, 255, 255);
                selectedListItem = ColorHelper.FromArgb(52, 255, 255, 255);
                primaryText = ColorHelper.FromArgb(255, 31, 31, 31);
                secondaryText = ColorHelper.FromArgb(255, 101, 96, 96);
                input = ColorHelper.FromArgb(52, 255, 255, 255);
                sliderThumb = ColorHelper.FromArgb(255, 250, 249, 246);
                sliderActive = ColorHelper.FromArgb(255, 136, 139, 142);
                sliderInactive = ColorHelper.FromArgb(255, 193, 196, 198);
                sliderThumbBorder = ColorHelper.FromArgb(255, 184, 183, 179);
                sliderFocusPrimary = ColorHelper.FromArgb(255, 97, 95, 91);
                sliderFocusSecondary = ColorHelper.FromArgb(255, 250, 249, 246);
                break;
        }

        SetSurfaceBrush("ConsolePaneSurfaceBrush", pane);
        SetSurfaceBrush("NavigationViewDefaultPaneBackground", pane);
        SetSurfaceBrush("ConsolePageSurfaceBrush", page);
        SetSurfaceBrush("ConsoleCardSurfaceBrush", card);
        SetSurfaceBrush("ConsoleTitleBarSurfaceBrush", title);
        SetSurfaceBrush("ConsoleManageRowSurfaceBrush", manageRow);
        SetSurfaceBrush("ConsoleManageRowBorderBrush", manageBorder);
        SetSurfaceBrush("ConsoleListItemSurfaceBrush", listItem);
        SetSurfaceBrush("ConsoleListItemSelectedSurfaceBrush", selectedListItem);
        SetSurfaceBrush("ConsolePrimaryTextBrush", primaryText);
        SetSurfaceBrush("ConsoleSecondaryTextBrush", secondaryText);
        SetSurfaceBrush("ConsoleInputSurfaceBrush", input);
        SetSurfaceBrush("ConsoleSliderActiveBrush", sliderActive);
        SetSurfaceBrush("ConsoleSliderInactiveBrush", sliderInactive);
        SetSurfaceBrush("ConsoleSliderThumbBorderBrush", sliderThumbBorder);
        SetSurfaceBrush("ConsoleSliderFocusPrimaryBrush", sliderFocusPrimary);
        SetSurfaceBrush("ConsoleSliderFocusSecondaryBrush", sliderFocusSecondary);
        SetSurfaceBrush("SliderThumbBackground", sliderThumb);
        SetSurfaceBrush("SliderThumbBackgroundPointerOver", sliderThumb);
        SetSurfaceBrush("SliderThumbBackgroundPressed", sliderThumb);
        SetSurfaceBrush("SliderThumbBackgroundDisabled", ColorHelper.FromArgb(115, sliderThumb.R, sliderThumb.G, sliderThumb.B));
        SetSurfaceBrush("SliderThumbBorderBrush", sliderThumbBorder);
        SetSurfaceBrush("SliderTrackFill", sliderInactive);
        SetSurfaceBrush("SliderTrackFillPointerOver", sliderInactive);
        SetSurfaceBrush("SliderTrackFillPressed", sliderInactive);
        SetSurfaceBrush("SliderTrackFillDisabled", ColorHelper.FromArgb(115, sliderInactive.R, sliderInactive.G, sliderInactive.B));
        SetSurfaceBrush("SliderTrackValueFill", sliderActive);
        SetSurfaceBrush("SliderTrackValueFillPointerOver", sliderActive);
        SetSurfaceBrush("SliderTrackValueFillPressed", sliderActive);
        SetSurfaceBrush("SliderTrackValueFillDisabled", ColorHelper.FromArgb(115, sliderActive.R, sliderActive.G, sliderActive.B));
        if (_componentReady)
        {
            VisitTree(ConsoleRoot, element =>
            {
                if (element is ConsoleSlider slider)
                {
                    slider.Background = GetSurfaceBrush("ConsoleSliderInactiveBrush");
                    slider.Foreground = GetSurfaceBrush("ConsoleSliderActiveBrush");
                    slider.SetThumbPalette(sliderThumb, sliderThumbBorder);
                }
            });
        }
    }

    private void SetSurfaceBrush(string key, Windows.UI.Color color)
    {
        if (ConsoleRoot.Resources[key] is SolidColorBrush brush) brush.Color = color;
    }

    private SolidColorBrush GetSurfaceBrush(string key) => (SolidColorBrush)ConsoleRoot.Resources[key];

    private void RestorePlacement()
    {
        if (_appWindow is null) return;
        ConsolePlacement? saved = _host.State.ConsolePlacement;
        IReadOnlyList<DisplayInfo> displays = DisplayPlacementService.GetDisplays();
        DisplayInfo display = displays.FirstOrDefault(item => string.Equals(item.Device, saved?.MonitorDevice, StringComparison.OrdinalIgnoreCase))
            ?? displays.FirstOrDefault(item => item.Monitor.Left == 0 && item.Monitor.Top == 0)
            ?? displays.First();
        int width = (int)Math.Round(Math.Max(860, saved?.WidthDip ?? 960) * display.Scale);
        int height = (int)Math.Round(Math.Max(600, saved?.HeightDip ?? 680) * display.Scale);
        int x = saved is null ? display.Work.Left + (display.Work.Width - width) / 2 : display.Work.Left + (int)Math.Round(saved.XDip * display.Scale);
        int y = saved is null ? display.Work.Top + (display.Work.Height - height) / 2 : display.Work.Top + (int)Math.Round(saved.YDip * display.Scale);
        NativeMethods.RECT bounds = DisplayPlacementService.Clamp(new NativeMethods.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height }, display.Work);
        _appWindow.MoveAndResize(new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
        AppLogger.Info($"控制台位置恢复：{bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height}px。");
    }

    private async Task SavePlacementAsync()
    {
        if (_appWindow is null || _closingPermanently) return;
        DisplayInfo display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
        {
            Left = _appWindow.Position.X,
            Top = _appWindow.Position.Y,
            Right = _appWindow.Position.X + _appWindow.Size.Width,
            Bottom = _appWindow.Position.Y + _appWindow.Size.Height
        });
        _host.State.ConsolePlacement = new ConsolePlacement
        {
            MonitorDevice = display.Device,
            XDip = (_appWindow.Position.X - display.Work.Left) / display.Scale,
            YDip = (_appWindow.Position.Y - display.Work.Top) / display.Scale,
            WidthDip = _appWindow.Size.Width / display.Scale,
            HeightDip = _appWindow.Size.Height / display.Scale
        };
        await _host.SaveStateAsync();
    }

    private async void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        FrameworkElement page = tag switch
        {
            "general" => GeneralPage,
            "theme" => ThemePage,
            "add" => AddPage,
            _ => ManagePage
        };
        await ShowPageAsync(page);
        if (ReferenceEquals(page, ManagePage)) PopulateManageList(_selectedId);
    }

    private void ShowPage(FrameworkElement page)
    {
        foreach (FrameworkElement candidate in new FrameworkElement[] { GeneralPage, ThemePage, AddPage, ManagePage }) candidate.Visibility = ReferenceEquals(candidate, page) ? Visibility.Visible : Visibility.Collapsed;
        page.Opacity = 1;
        page.Translation = Vector3.Zero;
    }

    private void UpdateStartupToggle()
    {
        if (StartupToggle is null) return;
        _loadingStartup = true;
        StartupToggle.IsOn = _host.State.GlobalSettings.StartWithWindows;
        _loadingStartup = false;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingStartup) return;
        try
        {
            await _host.SetStartupAsync(StartupToggle.IsOn);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新开机启动设置。", ex);
            UpdateStartupToggle();
            ShowError(AppStrings.Get("StartupErrorTitle"), ex.Message);
        }
    }


    private void UpdateOutsideClickToggle()
    {
        if (CollapseOutsideToggle is null) return;
        _loadingOutsideClick = true;
        CollapseOutsideToggle.IsOn = _host.State.GlobalSettings.CollapseOnOutsideClick;
        _loadingOutsideClick = false;
    }

    private async void CollapseOutsideToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingOutsideClick) return;
        try
        {
            await _host.SetCollapseOnOutsideClickAsync(CollapseOutsideToggle.IsOn);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新外部点击收缩设置。", ex);
            UpdateOutsideClickToggle();
            ShowError(AppStrings.Get("CollapseOutsideErrorTitle"), ex.Message);
        }
    }

    private bool _loadingShowConsole;

    private void UpdateShowConsoleToggle()
    {
        if (ShowConsoleToggle is null) return;
        _loadingShowConsole = true;
        ShowConsoleToggle.IsOn = _host.State.GlobalSettings.ShowConsoleOnLaunch;
        _loadingShowConsole = false;
    }

    private async void ShowConsoleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingShowConsole) return;
        try
        {
            await _host.SetShowConsoleOnLaunchAsync(ShowConsoleToggle.IsOn);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新启动显示控制台设置。", ex);
            UpdateShowConsoleToggle();
            ShowError(AppStrings.Get("ShowConsoleErrorTitle"), ex.Message);
        }
    }

    private bool _loadingOrganizerOpacity;

    private void UpdateOrganizerOpacitySlider()
    {
        if (OrganizerOpacitySlider is null) return;
        _loadingOrganizerOpacity = true;
        OrganizerOpacitySlider.Value = Math.Clamp(_host.State.GlobalSettings.OrganizerSurfaceOpacity, 0d, 1d);
        OrganizerOpacityPercent.Text = $"{Math.Round(OrganizerOpacitySlider.Value * 100)}%";
        _loadingOrganizerOpacity = false;
    }

    private void OrganizerOpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_componentReady || _loadingOrganizerOpacity) return;
        double value = Math.Clamp(OrganizerOpacitySlider.Value, 0d, 1d);
        OrganizerOpacityPercent.Text = $"{Math.Round(value * 100)}%";
        _host.ApplyOrganizerSurfaceOpacity(value);
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    private void UpdateExpandOnHoverToggle()
    {
        if (ExpandOnHoverToggle is null) return;
        _loadingExpandOnHover = true;
        ExpandOnHoverToggle.IsOn = _host.State.GlobalSettings.ExpandOnHover;
        _loadingExpandOnHover = false;
    }

    private async void ExpandOnHoverToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingExpandOnHover) return;
        try
        {
            await _host.SetExpandOnHoverAsync(ExpandOnHoverToggle.IsOn);
            UpdateExpandOnHoverDelaySlider();
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新悬浮展开设置。", ex);
            UpdateExpandOnHoverToggle();
            UpdateExpandOnHoverDelaySlider();
            ShowError(AppStrings.Get("ExpandOnHoverErrorTitle"), ex.Message);
        }
    }

    private void UpdateCollapseOnPointerLeaveToggle()
    {
        if (CollapseOnPointerLeaveToggle is null) return;
        _loadingCollapseOnPointerLeave = true;
        CollapseOnPointerLeaveToggle.IsOn = _host.State.GlobalSettings.CollapseOnPointerLeave;
        _loadingCollapseOnPointerLeave = false;
    }

    private bool _loadingExpandOnHoverDelay;

    private void UpdateExpandOnHoverDelaySlider()
    {
        if (ExpandOnHoverDelaySlider is null) return;
        _loadingExpandOnHoverDelay = true;
        bool enabled = _host.State.GlobalSettings.ExpandOnHover;
        ExpandOnHoverDelayCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ExpandOnHoverDelaySlider.Value = Math.Clamp(_host.State.GlobalSettings.ExpandOnHoverMs, 100, 1500);
        ExpandOnHoverDelayText.Text = $"{Math.Round(ExpandOnHoverDelaySlider.Value)}ms";
        _loadingExpandOnHoverDelay = false;
    }

    private void ExpandOnHoverDelaySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_componentReady || _loadingExpandOnHoverDelay) return;
        int value = (int)Math.Round(Math.Clamp(ExpandOnHoverDelaySlider.Value, 100, 1500));
        ExpandOnHoverDelayText.Text = $"{value}ms";
        _host.ApplyExpandOnHoverDelay(value);
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    private bool _loadingPointerLeaveDelay;

    private void UpdateCollapseOnPointerLeaveDelaySlider()
    {
        if (CollapseOnPointerLeaveDelaySlider is null) return;
        _loadingPointerLeaveDelay = true;
        bool enabled = _host.State.GlobalSettings.CollapseOnPointerLeave;
        CollapseOnPointerLeaveDelayCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        CollapseOnPointerLeaveDelaySlider.Value = Math.Clamp(_host.State.GlobalSettings.CollapseOnPointerLeaveMs, 200, 2000);
        CollapseOnPointerLeaveDelayText.Text = $"{Math.Round(CollapseOnPointerLeaveDelaySlider.Value)}ms";
        _loadingPointerLeaveDelay = false;
    }

    private void CollapseOnPointerLeaveDelaySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_componentReady || _loadingPointerLeaveDelay) return;
        int value = (int)Math.Round(Math.Clamp(CollapseOnPointerLeaveDelaySlider.Value, 200, 2000));
        CollapseOnPointerLeaveDelayText.Text = $"{value}ms";
        _host.ApplyPointerLeaveDelay(value);
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    private async void CollapseOnPointerLeaveToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingCollapseOnPointerLeave) return;
        try
        {
            await _host.SetCollapseOnPointerLeaveAsync(CollapseOnPointerLeaveToggle.IsOn);
            UpdateCollapseOnPointerLeaveDelaySlider();
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新鼠标离开收缩设置。", ex);
            UpdateCollapseOnPointerLeaveToggle();
            UpdateCollapseOnPointerLeaveDelaySlider();
            ShowError(AppStrings.Get("CollapseOnPointerLeaveErrorTitle"), ex.Message);
        }
    }

    private void UpdateExclusiveExpansionToggle()
    {
        if (ExclusiveExpansionToggle is null) return;
        _loadingExclusiveExpansion = true;
        ExclusiveExpansionToggle.IsOn = _host.State.GlobalSettings.ExclusiveExpansion;
        _loadingExclusiveExpansion = false;
    }

    private async void ExclusiveExpansionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingExclusiveExpansion) return;
        try
        {
            await _host.SetExclusiveExpansionAsync(ExclusiveExpansionToggle.IsOn);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新单窗展开设置。", ex);
            UpdateExclusiveExpansionToggle();
            ShowError(AppStrings.Get("ExclusiveExpansionErrorTitle"), ex.Message);
        }

    }

    private async void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_componentReady || _loadingLanguage || LanguageCombo.SelectedIndex < 0) return;
        try
        {
            await _host.SetLanguageAsync((AppLanguage)LanguageCombo.SelectedIndex);
        }
        catch (Exception ex)
        {
            _loadingLanguage = true;
            LanguageCombo.SelectedIndex = (int)_host.State.GlobalSettings.Language;
            _loadingLanguage = false;
            ShowError(AppStrings.Get("LanguageErrorTitle"), ex.Message);
        }
    }

    private static void ApplyLocalizedTree(DependencyObject root)
    {
        VisitTree(root, element =>
        {
            if (element is not FrameworkElement { Tag: string tag } || !tag.StartsWith("loc:", StringComparison.Ordinal)) return;
            string value = AppStrings.Get(tag[4..]);
            if (element is TextBlock textBlock) textBlock.Text = value;
            else if (element is ContentControl contentControl) contentControl.Content = value;
        });
    }

    private static void ApplyTypography(DependencyObject root)
    {
        FontFamily family = new(AppStrings.FontFamily);
        VisitTree(root, element =>
        {
            bool localized = element is FrameworkElement { Tag: string tag } && tag.StartsWith("loc:", StringComparison.Ordinal);
            if (localized && element is TextBlock text)
            {
                text.FontFamily = family;
                text.CharacterSpacing = AppStrings.CharacterSpacing;
            }
            else if ((localized || element is ComboBox) && element is Control control and not TextBox)
            {
                control.FontFamily = family;
                control.CharacterSpacing = AppStrings.CharacterSpacing;
            }
        });
    }

    private static void VisitTree(DependencyObject root, Action<DependencyObject> visitor)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        Visit(root);
        void Visit(DependencyObject current)
        {
            if (!visited.Add(current)) return;
            visitor(current);
            if (current is ItemsControl items)
            {
                foreach (object item in items.Items)
                    if (item is DependencyObject dependencyObject) Visit(dependencyObject);
            }
            if (current is ContentControl { Content: DependencyObject content }) Visit(content);
            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int index = 0; index < count; index++) Visit(VisualTreeHelper.GetChild(current, index));
        }
    }

    private async Task ShowPageAsync(FrameworkElement page)
    {
        FlushPendingManageChanges();
        _pageTransition?.Cancel();
        _pageTransition?.Dispose();
        _pageTransition = new CancellationTokenSource();
        CancellationToken token = _pageTransition.Token;
        ShowPage(page);
        if (!_uiSettings.AnimationsEnabled) return;
        page.Opacity = .01;
        page.Translation = new Vector3(0, 6, 0);
        long started = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                double raw = Math.Clamp(Stopwatch.GetElapsedTime(started).TotalMilliseconds / 180, 0, 1);
                double eased = 1 - Math.Pow(1 - raw, 4);
                page.Opacity = Math.Max(.01, eased);
                page.Translation = new Vector3(0, (float)(6 * (1 - eased)), 0);
                if (raw >= 1) break;
                await Task.Delay(16, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async void LightThemeCard_Click(object sender, RoutedEventArgs e) => await _host.SetGlobalThemeAsync(GlassTheme.Light);
    private async void GrayThemeCard_Click(object sender, RoutedEventArgs e) => await _host.SetGlobalThemeAsync(GlassTheme.Gray);
    private async void SolidLightThemeCard_Click(object sender, RoutedEventArgs e) => await _host.SetGlobalThemeAsync(GlassTheme.SolidLight);
    private async void SolidDarkThemeCard_Click(object sender, RoutedEventArgs e) => await _host.SetGlobalThemeAsync(GlassTheme.SolidDark);
    private async void FrostedLightThemeCard_Click(object sender, RoutedEventArgs e) => await _host.SetGlobalThemeAsync(GlassTheme.FrostedLight);
    private async void FrostedDarkThemeCard_Click(object sender, RoutedEventArgs e) => await _host.SetGlobalThemeAsync(GlassTheme.FrostedDark);

    private void UpdateThemeCards(GlassTheme theme)
    {
        LightThemeCard.IsChecked = theme == GlassTheme.Light;
        GrayThemeCard.IsChecked = theme == GlassTheme.Gray;
        SolidLightThemeCard.IsChecked = theme == GlassTheme.SolidLight;
        SolidDarkThemeCard.IsChecked = theme == GlassTheme.SolidDark;
        FrostedLightThemeCard.IsChecked = theme == GlassTheme.FrostedLight;
        FrostedDarkThemeCard.IsChecked = theme == GlassTheme.FrostedDark;
    }

    private void AddControl_Changed(object sender, object e)
    {
        if (_componentReady) UpdateAddControls();
    }

    private void AddNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_componentReady && !_loadingDefaultName) _addNameWasEdited = true;
        AddControl_Changed(sender, e);
    }

    private async void ChooseAddStorageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow is null) return;
        try
        {
            var picker = new FolderPicker(_appWindow.Id)
            {
                Title = AppStrings.Get("SelectStorageFolderTitle"),
                CommitButtonText = AppStrings.Get("SelectStorageFolderCommit"),
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            string suggested = _addStoragePath ?? AppPaths.WindowsRoot;
            if (Directory.Exists(suggested)) picker.SuggestedStartFolder = suggested;
            PickFolderResult? result = await picker.PickSingleFolderAsync();
            if (result is null || string.IsNullOrWhiteSpace(result.Path)) return;
            _addStoragePath = _host.ValidateStoragePath(result.Path);

            UpdateAddStoragePath();
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法选择收纳窗保存位置。", ex);
            ShowError(AppStrings.Get("StorageFolderPickerError"), ex.Message);
        }
    }

    private void ResetAddStorageButton_Click(object sender, RoutedEventArgs e)
    {
        _addStoragePath = null;
        UpdateAddStoragePath();
    }

    private void UpdateAddStoragePath()
    {
        if (AddStoragePathBox is not null)
            AddStoragePathBox.Text = _addStoragePath ?? AppStrings.Format("AutomaticStoragePathFormat", AppPaths.WindowsRoot);
    }

    private void UpdateAddControls()
    {
        if (!_componentReady || AddRowsCard is null || _adjustingAddControls) return;
        _adjustingAddControls = true;
        bool positioned = AddPlacementModeCombo.SelectedIndex == (int)OrganizerPlacementMode.Positioned;
        bool station = AddPlacementModeCombo.SelectedIndex == (int)OrganizerPlacementMode.Station;
        AddNameCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        AddDisplayCard.Visibility = station ? Visibility.Visible : Visibility.Collapsed;
        AddDockEdgeCard.Visibility = station ? Visibility.Visible : Visibility.Collapsed;
        AddCompactScaleCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        AddCanvasScaleCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        AddNameScaleCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;

        AddCompactScaleSlider.Maximum = positioned
            ? OrganizerLimits.MaximumPositionedCompactScale
            : OrganizerLimits.MaximumCompactScale;
        AddCompactScaleSlider.Value = Math.Clamp(
            AddCompactScaleSlider.Value,
            OrganizerLimits.MinimumCompactScale,
            AddCompactScaleSlider.Maximum);
        ConfigureGridSliders(AddRowsSlider, AddColumnsSlider, station);
        (int rows, int columns) = ReadGridDimensions(AddRowsSlider, AddColumnsSlider, station);

        var layout = new OrganizerLayout
        {
            Mode = OrganizerLayoutMode.Grid,
            Rows = rows,
            Columns = columns
        };
        DisplayInfo display = station
            ? DisplayPlacementService.GetDisplay(SelectedDisplayDevice(AddDisplayCombo))
            : GetPrimaryDisplay();
        if (station)
        {
            AddItemScaleSlider.Maximum = DisplayPlacementService.CalculateMaximumStationItemScale(display, layout);
        }
        else
        {
            AddCanvasScaleSlider.Minimum = DisplayPlacementService.CalculateMinimumCanvasScale(display, layout);
            if (AddCanvasScaleSlider.Value < AddCanvasScaleSlider.Minimum) AddCanvasScaleSlider.Value = AddCanvasScaleSlider.Minimum;
            AddItemScaleSlider.Maximum = DisplayPlacementService.CalculateMaximumItemScale(display, layout, AddCanvasScaleSlider.Value);
        }
        if (AddItemScaleSlider.Value > AddItemScaleSlider.Maximum) AddItemScaleSlider.Value = AddItemScaleSlider.Maximum;
        SetPercent(AddCompactPercent, AddCompactScaleSlider.Value);
        SetPercent(AddCanvasPercent, AddCanvasScaleSlider.Value);
        SetPercent(AddItemPercent, AddItemScaleSlider.Value);
        SetPercent(AddNamePercent, AddNameScaleSlider.Value);
        bool available = station
            ? _host.State.Organizers.Count(item => item.PlacementMode == OrganizerPlacementMode.Station) < OrganizerLimits.MaximumStations &&
                !_host.State.Organizers.Any(item => item.PlacementMode == OrganizerPlacementMode.Station &&
                    item.DockEdge == (OrganizerDockEdge)Math.Clamp(AddDockEdgeCombo.SelectedIndex, 0, 3))
            : _host.State.Organizers.Count(item => item.PlacementMode != OrganizerPlacementMode.Station) < OrganizerLimits.MaximumOrganizers;
        CreateOrganizerButton.IsEnabled = available;
        CreateLimitText.Text = station ? AppStrings.Get("StationEdgeOccupiedError") : AppStrings.Get("OrganizerLimit");
        CreateLimitText.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        _adjustingAddControls = false;
    }

    private async void CreateOrganizerButton_Click(object sender, RoutedEventArgs e)
    {
        bool station = AddPlacementModeCombo.SelectedIndex == (int)OrganizerPlacementMode.Station;
        (int rows, int columns) = ReadGridDimensions(AddRowsSlider, AddColumnsSlider, station);
        var definition = new OrganizerDefinition
        {
            Name = AddPlacementModeCombo.SelectedIndex == (int)OrganizerPlacementMode.Station
                ? AppStrings.Get("StationDefaultName")
                : string.IsNullOrWhiteSpace(AddNameBox.Text) ? AppStrings.DefaultOrganizerName : AddNameBox.Text.Trim(),
            ThemeOverride = ThemeFromCombo(AddThemeCombo.SelectedIndex),
            PlacementMode = (OrganizerPlacementMode)Math.Clamp(AddPlacementModeCombo.SelectedIndex, 0, 2),
            DockEdge = (OrganizerDockEdge)Math.Clamp(AddDockEdgeCombo.SelectedIndex, 0, 3),
            Position = new WidgetPosition { MonitorDevice = SelectedDisplayDevice(AddDisplayCombo) ?? string.Empty },
            Layout = new OrganizerLayout { Mode = OrganizerLayoutMode.Grid, Rows = rows, Columns = columns },
            CompactScale = AddCompactScaleSlider.Value,
            CanvasScale = AddCanvasScaleSlider.Value,
            ItemScale = AddItemScaleSlider.Value,
            NameScale = AddNameScaleSlider.Value
        };
        try
        {
            OrganizerDefinition created = await _host.CreateOrganizerAsync(definition, _addStoragePath);
            RootNavigation.SelectedItem = ManageNavItem;
            ShowAndActivate(created.Id);
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("CreateErrorTitle"), ex.Message);
        }
    }

    private void PopulateManageList(Guid? selectId)
    {
        if (ManageList is null) return;
        _suppressSelection = true;
        IEnumerable<OrganizerDefinition> definitions = _host.State.Organizers
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id);
        ManageList.Items.Clear();
        foreach (OrganizerDefinition definition in definitions)
        {
            MainWindow? window = _host.Windows.FirstOrDefault(item => item.OrganizerId == definition.Id);
            string layout = AppStrings.Format("GridLayoutFormat", definition.Layout.Columns, definition.Layout.Rows);
            var panel = new StackPanel { Spacing = 3 };
            string displayName = definition.PlacementMode == OrganizerPlacementMode.Station
                ? AppStrings.Format("StationListNameFormat", DockEdgeName(definition.DockEdge))
                : definition.Name;
            panel.Children.Add(new TextBlock { Text = displayName, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = GetSurfaceBrush("ConsolePrimaryTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(new TextBlock { Text = AppStrings.Format("ManageItemSummaryFormat", layout, AppStrings.FormatItemCount(window?.FileCount ?? 0), AppStrings.FormatDate(definition.CreatedAtUtc)), FontFamily = new FontFamily(AppStrings.FontFamily), CharacterSpacing = AppStrings.CharacterSpacing, FontSize = 12, Foreground = GetSurfaceBrush("ConsoleSecondaryTextBrush") });
            var content = new Grid { ColumnSpacing = 7 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var indicator = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = GetSurfaceBrush("ConsoleSelectionAccentBrush"), Visibility = Visibility.Collapsed };
            Grid.SetColumn(panel, 1);
            content.Children.Add(indicator);
            content.Children.Add(panel);
            ApplyTypography(content);
            var item = new ListViewItem { Tag = definition.Id, Content = content, Padding = new Thickness(7, 8, 10, 8), Background = GetSurfaceBrush("ConsoleListItemSurfaceBrush") };
            ManageList.Items.Add(item);
            if (definition.Id == selectId) ManageList.SelectedItem = item;
        }
        ManageEmptyState.Visibility = ManageList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ManageEditor.Visibility = ManageList.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ManageDetailCard.Visibility = ManageList.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (ManageList.SelectedItem is null && ManageList.Items.Count > 0) ManageList.SelectedIndex = 0;
        UpdateManageListItemSurfaces();
        _suppressSelection = false;
        if (ManageList.SelectedItem is ListViewItem { Tag: Guid id }) LoadManageEditor(id);
    }

    private void ManageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateManageListItemSurfaces();
        if (_suppressSelection || ManageList.SelectedItem is not ListViewItem { Tag: Guid nextId } || nextId == _selectedId) return;
        FlushPendingManageChanges();
        LoadManageEditor(nextId);
    }

    private void UpdateManageListItemSurfaces()
    {
        if (ManageList is null) return;
        SolidColorBrush normal = GetSurfaceBrush("ConsoleListItemSurfaceBrush");
        SolidColorBrush selected = GetSurfaceBrush("ConsoleListItemSelectedSurfaceBrush");
        foreach (ListViewItem item in ManageList.Items.OfType<ListViewItem>())
        {
            bool isSelected = ReferenceEquals(item, ManageList.SelectedItem);
            item.Background = isSelected ? selected : normal;
            if (item.Content is Grid content && content.Children.OfType<Border>().FirstOrDefault() is { } indicator)
            {
                indicator.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void LoadManageEditor(Guid id)
    {
        OrganizerDefinition source = _host.State.Organizers.First(item => item.Id == id);
        _selectedId = id;
        _editing = Clone(source);
        _loadingEditor = true;
        ManageNameBox.Text = source.Name;
        ManagePlacementModeCombo.SelectedIndex = (int)source.PlacementMode;
        ManagePositionLockToggle.IsOn = source.PositionLocked;
        SelectDisplay(ManageDisplayCombo, source.Position?.MonitorDevice);
        ManageDockEdgeCombo.SelectedIndex = (int)source.DockEdge;
        ManageRowsSlider.Value = source.Layout.Rows;
        ManageColumnsSlider.Value = source.Layout.Columns;
        ManageThemeCombo.SelectedIndex = ComboFromTheme(source.ThemeOverride);
        ManageCompactScaleSlider.Value = source.CompactScale;
        ManageCanvasScaleSlider.Value = source.CanvasScale;
        ManageItemScaleSlider.Value = source.ItemScale;
        ManageNameScaleSlider.Value = source.NameScale;
        string path = AppPaths.ResolveStoragePath(source);
        ManagePathBox.Text = path;
        bool missing = !Directory.Exists(path);
        MissingStorageInfo.IsOpen = missing;
        RecreateStorageButton.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        UpdateManageControls();
        _loadingEditor = false;
        UpdateTransferState();
    }

    private void ManageEditor_Changed(object sender, object e)
    {
        if (_loadingEditor || _adjustingManageControls || _editing is null) return;
        UpdateManageControls();
        ManageNameError.Visibility = ManagePlacementModeCombo.SelectedIndex != (int)OrganizerPlacementMode.Station &&
            string.IsNullOrWhiteSpace(ManageNameBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ScheduleRuntimeApply(GetVisualChange(sender));
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    private void UpdateManageControls()
    {
        if (ManageRowsCard is null || _adjustingManageControls) return;
        _adjustingManageControls = true;
        bool positioned = ManagePlacementModeCombo.SelectedIndex == (int)OrganizerPlacementMode.Positioned;
        ManagePositionLockCard.Visibility = positioned ? Visibility.Visible : Visibility.Collapsed;
        bool station = ManagePlacementModeCombo.SelectedIndex == (int)OrganizerPlacementMode.Station;
        ManageNameCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        ManageNameError.Visibility = Visibility.Collapsed;
        ManageDisplayCard.Visibility = station ? Visibility.Visible : Visibility.Collapsed;
        ManageDockEdgeCard.Visibility = station ? Visibility.Visible : Visibility.Collapsed;
        ManageCompactScaleCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        ManageCanvasScaleCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        ManageNameScaleCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;

        ManageCompactScaleSlider.Maximum = positioned
            ? OrganizerLimits.MaximumPositionedCompactScale
            : OrganizerLimits.MaximumCompactScale;
        ManageCompactScaleSlider.Value = Math.Clamp(
            ManageCompactScaleSlider.Value,
            OrganizerLimits.MinimumCompactScale,
            ManageCompactScaleSlider.Maximum);
        ConfigureGridSliders(ManageRowsSlider, ManageColumnsSlider, station);
        (int rows, int columns) = ReadGridDimensions(ManageRowsSlider, ManageColumnsSlider, station);

        var layout = new OrganizerLayout { Mode = OrganizerLayoutMode.Grid, Rows = rows, Columns = columns };
        MainWindow? window = _selectedId is Guid id ? _host.Windows.FirstOrDefault(item => item.OrganizerId == id) : null;
        DisplayInfo display = station
            ? DisplayPlacementService.GetDisplay(SelectedDisplayDevice(ManageDisplayCombo))
            : window is null ? GetPrimaryDisplay() : DisplayPlacementService.ForBounds(window.CompactBounds);
        double canvas = ManageCanvasScaleSlider.Value;
        double maximumItemScale;
        if (station)
        {
            maximumItemScale = DisplayPlacementService.CalculateMaximumStationItemScale(display, layout);
        }
        else
        {
            if (_editing?.ManualCanvasBaseWidthDip is double baseWidth &&
                _editing.ManualCanvasBaseHeightDip is double baseHeight)
            {
                (double minimumWidth, double minimumHeight) =
                    DisplayPlacementService.CalculateMinimumExpandedSizeDip(layout, .5);
                ManageCanvasScaleSlider.Minimum = Math.Min(1.2,
                    Math.Max(.1, Math.Max(minimumWidth / baseWidth, minimumHeight / baseHeight)));
            }
            else
            {
                ManageCanvasScaleSlider.Minimum = DisplayPlacementService.CalculateMinimumCanvasScale(display, layout);
            }
            if (ManageCanvasScaleSlider.Value < ManageCanvasScaleSlider.Minimum) ManageCanvasScaleSlider.Value = ManageCanvasScaleSlider.Minimum;
            canvas = ManageCanvasScaleSlider.Value;
            if (_editing?.ManualCanvasBaseWidthDip is double manualWidth &&
                _editing.ManualCanvasBaseHeightDip is double manualHeight)
            {
                NativeMethods.RECT work = DisplayPlacementService.GetExpandedWorkArea(display);
                double fit = Math.Min(1, Math.Min(
                    work.Width / display.Scale / (manualWidth * canvas),
                    work.Height / display.Scale / (manualHeight * canvas)));
                maximumItemScale = DisplayPlacementService.CalculateMaximumItemScaleForExpandedSize(
                    layout,
                    manualWidth * canvas * fit,
                    manualHeight * canvas * fit);
            }
            else
            {
                maximumItemScale = DisplayPlacementService.CalculateMaximumItemScale(display, layout, canvas);
            }
        }
        ManageItemScaleSlider.Maximum = maximumItemScale;
        if (ManageItemScaleSlider.Value > ManageItemScaleSlider.Maximum) ManageItemScaleSlider.Value = ManageItemScaleSlider.Maximum;
        SetPercent(ManageCompactPercent, ManageCompactScaleSlider.Value);
        SetPercent(ManageCanvasPercent, ManageCanvasScaleSlider.Value);
        SetPercent(ManageItemPercent, ManageItemScaleSlider.Value);
        SetPercent(ManageNamePercent, ManageNameScaleSlider.Value);
        _adjustingManageControls = false;
    }

    private OrganizerDefinition? CaptureManageDraft()
    {
        if (_editing is null) return null;
        if (!string.IsNullOrWhiteSpace(ManageNameBox.Text)) _editing.Name = ManageNameBox.Text.Trim();
        _editing.PlacementMode = (OrganizerPlacementMode)Math.Clamp(ManagePlacementModeCombo.SelectedIndex, 0, 2);
        _editing.DockEdge = (OrganizerDockEdge)Math.Clamp(ManageDockEdgeCombo.SelectedIndex, 0, 3);
        if (_editing.PlacementMode == OrganizerPlacementMode.Station)
        {
            _editing.Position ??= new WidgetPosition();
            _editing.Position.MonitorDevice = SelectedDisplayDevice(ManageDisplayCombo) ?? string.Empty;
        }
        bool station = _editing.PlacementMode == OrganizerPlacementMode.Station;
        (int rows, int columns) = ReadGridDimensions(ManageRowsSlider, ManageColumnsSlider, station);
        if (station || _editing.Layout.Rows != rows || _editing.Layout.Columns != columns)
        {
            _editing.ManualCanvasBaseWidthDip = null;
            _editing.ManualCanvasBaseHeightDip = null;
        }
        _editing.PositionLocked = ManagePositionLockToggle.IsOn;
        _editing.Layout.Mode = OrganizerLayoutMode.Grid;
        (_editing.Layout.Rows, _editing.Layout.Columns) = (rows, columns);
        _editing.ThemeOverride = ThemeFromCombo(ManageThemeCombo.SelectedIndex);
        _editing.CompactScale = ManageCompactScaleSlider.Value;
        _editing.CanvasScale = ManageCanvasScaleSlider.Value;
        _editing.ItemScale = ManageItemScaleSlider.Value;
        _editing.NameScale = ManageNameScaleSlider.Value;
        return Clone(_editing);
    }

    private void ScheduleRuntimeApply(OrganizerVisualChange changes)
    {
        _pendingVisualChanges |= changes;
        if (_runtimeApplyScheduled) return;
        _runtimeApplyScheduled = true;
        CompositionTarget.Rendering += ApplyPendingRuntimeChanges;
    }

    private void ApplyPendingRuntimeChanges(object? sender, object args)
    {
        if (_runtimeApplyScheduled) CompositionTarget.Rendering -= ApplyPendingRuntimeChanges;
        _runtimeApplyScheduled = false;
        OrganizerVisualChange changes = _pendingVisualChanges;
        _pendingVisualChanges = OrganizerVisualChange.None;
        if (changes != OrganizerVisualChange.None && CaptureManageDraft() is { } draft)
        {
            string? error = _host.ApplyOrganizerRuntime(draft, changes);
            if (error is not null)
            {
                ShowError(AppStrings.Get("OrganizerModeErrorTitle"), error);
                LoadManageEditor(draft.Id);
            }
        }
    }

    private async void StateSaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_runtimeApplyScheduled) ApplyPendingRuntimeChanges(null, args);
        await _host.SaveStateAsync();
    }

    private void FlushPendingManageChanges()
    {
        bool shouldSave = _runtimeApplyScheduled || _stateSaveTimer.IsRunning;
        _stateSaveTimer.Stop();
        if (_runtimeApplyScheduled) ApplyPendingRuntimeChanges(null, EventArgs.Empty);
        if (shouldSave) _ = _host.SaveStateAsync();
    }

    private async void DeleteOrganizerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedId is not Guid id) return;
        OrganizerDefinition definition = _host.State.Organizers.First(item => item.Id == id);
        MainWindow? window = _host.Windows.FirstOrDefault(item => item.OrganizerId == id);
        string storagePath = AppPaths.ResolveStoragePath(definition);
        bool directStorage = !string.IsNullOrWhiteSpace(definition.StorageAbsolutePath);

        var dialog = new ContentDialog
        {
            XamlRoot = ConsoleRoot.XamlRoot,
            Title = AppStrings.Format("DeleteTitleFormat", definition.Name),
            Content = directStorage
                ? window?.FileCount > 0
                    ? AppStrings.Format("DeleteDirectNonEmptyFormat", storagePath, AppStrings.FormatItemCount(window.FileCount), definition.Name)
                    : AppStrings.Format("DeleteDirectEmptyFormat", storagePath)
                : window?.FileCount > 0
                    ? AppStrings.Format("DeleteNonEmptyFormat", AppStrings.FormatItemCount(window.FileCount), definition.Name)

                    : AppStrings.Get("DeleteEmpty"),
            PrimaryButtonText = AppStrings.Get("ExportDelete"),
            CloseButtonText = AppStrings.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        TransferOutcome outcome = await _host.DeleteOrganizerAsync(id);
        if (outcome.Status != TransferStatus.Moved) ShowError(AppStrings.Get("DeleteErrorTitle"), outcome.Message);
    }

    private void RecreateStorageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedId is not Guid id) return;
        try { _host.RecreateStorage(id); }
        catch (Exception ex)
        {
            AppLogger.Error("无法重建收纳目录。", ex);
            ShowError(AppStrings.Get("RecreateStorageErrorTitle"), ex.Message);
        }
    }

    private void OpenManagedFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ManagePathBox.Text)) return;
        if (!Directory.Exists(ManagePathBox.Text))
        {
            ShowError(AppStrings.Get("OpenFolderErrorTitle"), AppStrings.Get("MissingStorageMessage"));
            return;
        }
        Process.Start(new ProcessStartInfo(ManagePathBox.Text) { UseShellExecute = true });
    }

    private void EmptyAddButton_Click(object sender, RoutedEventArgs e) => RootNavigation.SelectedItem = AddNavItem;

    private void ShowError(string title, string message)
    {
        ConsoleInfoBar.Title = title;
        ConsoleInfoBar.Message = message;
        ConsoleInfoBar.Severity = InfoBarSeverity.Error;
        ConsoleInfoBar.IsOpen = true;
    }

    private OrganizerVisualChange GetVisualChange(object sender)
    {
        if (ReferenceEquals(sender, ManageNameBox)) return OrganizerVisualChange.Name;
        if (ReferenceEquals(sender, ManagePlacementModeCombo)) return OrganizerVisualChange.PlacementMode | OrganizerVisualChange.CompactScale | OrganizerVisualChange.Docking;
        if (ReferenceEquals(sender, ManageDisplayCombo) || ReferenceEquals(sender, ManageDockEdgeCombo)) return OrganizerVisualChange.Docking;
        if (ReferenceEquals(sender, ManagePositionLockToggle)) return OrganizerVisualChange.PositionLock;
        if (ReferenceEquals(sender, ManageThemeCombo)) return OrganizerVisualChange.Theme;
        if (ReferenceEquals(sender, ManageCompactScaleSlider)) return OrganizerVisualChange.CompactScale;
        if (ReferenceEquals(sender, ManageCanvasScaleSlider)) return OrganizerVisualChange.CanvasScale | OrganizerVisualChange.ItemScale;
        if (ReferenceEquals(sender, ManageItemScaleSlider)) return OrganizerVisualChange.ItemScale;
        if (ReferenceEquals(sender, ManageNameScaleSlider)) return OrganizerVisualChange.NameScale | OrganizerVisualChange.CompactScale;
        return OrganizerVisualChange.Layout | OrganizerVisualChange.ItemScale | OrganizerVisualChange.CanvasScale;
    }

    private void PopulateDisplayCombos()
    {
        string? addDevice = SelectedDisplayDevice(AddDisplayCombo);
        string? manageDevice = SelectedDisplayDevice(ManageDisplayCombo);
        IReadOnlyList<DisplayInfo> displays = DisplayPlacementService.GetDisplays();
        PopulateDisplayCombo(AddDisplayCombo, displays, addDevice);
        PopulateDisplayCombo(ManageDisplayCombo, displays, manageDevice);
    }

    private static void PopulateDisplayCombo(ComboBox combo, IReadOnlyList<DisplayInfo> displays, string? selectedDevice)
    {
        combo.Items.Clear();
        for (int index = 0; index < displays.Count; index++)
        {
            DisplayInfo display = displays[index];
            combo.Items.Add(new ComboBoxItem
            {
                Tag = display.Device,
                Content = AppStrings.Format("DisplayItemFormat", index + 1, display.Monitor.Width, display.Monitor.Height)
            });
        }
        SelectDisplay(combo, selectedDevice);
    }

    private static void SelectDisplay(ComboBox combo, string? device)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, device, StringComparison.OrdinalIgnoreCase));
        if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string? SelectedDisplayDevice(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private static string DockEdgeName(OrganizerDockEdge edge) => AppStrings.Get(edge switch
    {
        OrganizerDockEdge.Left => "DockLeft",
        OrganizerDockEdge.Top => "DockTop",
        OrganizerDockEdge.Bottom => "DockBottom",
        _ => "DockRight"
    });

    private static DisplayInfo GetPrimaryDisplay() => DisplayPlacementService.GetDisplays()
        .FirstOrDefault(display => display.Monitor.Left == 0 && display.Monitor.Top == 0)
        ?? DisplayPlacementService.GetDisplays().First();

    private static void SetPercent(TextBlock target, double value) => target.Text = $"{Math.Round(value * 100):0}%";

    private static void ConfigureGridSliders(Slider rows, Slider columns, bool station)
    {
        rows.Minimum = station ? OrganizerLimits.MinimumStationRows : OrganizerLimits.MinimumGridDimension;
        rows.Maximum = station ? OrganizerLimits.MaximumStationRows : OrganizerLimits.MaximumLayoutDimension;
        columns.Minimum = station ? OrganizerLimits.MinimumStationColumns : OrganizerLimits.MinimumGridDimension;
        columns.Maximum = station ? OrganizerLimits.MaximumStationColumns : OrganizerLimits.MaximumLayoutDimension;
        rows.Value = Math.Clamp(rows.Value, rows.Minimum, rows.Maximum);
        columns.Value = Math.Clamp(columns.Value, columns.Minimum, columns.Maximum);
    }

    private static (int Rows, int Columns) ReadGridDimensions(Slider rows, Slider columns, bool station) => (
        Math.Clamp((int)Math.Round(rows.Value),
            station ? OrganizerLimits.MinimumStationRows : OrganizerLimits.MinimumGridDimension,
            station ? OrganizerLimits.MaximumStationRows : OrganizerLimits.MaximumLayoutDimension),
        Math.Clamp((int)Math.Round(columns.Value),
            station ? OrganizerLimits.MinimumStationColumns : OrganizerLimits.MinimumGridDimension,
            station ? OrganizerLimits.MaximumStationColumns : OrganizerLimits.MaximumLayoutDimension));

    private static GlassTheme? ThemeFromCombo(int selectedIndex) => selectedIndex switch
    {
        1 => GlassTheme.Light,
        2 => GlassTheme.Gray,
        3 => GlassTheme.SolidLight,
        4 => GlassTheme.SolidDark,
        5 => GlassTheme.FrostedLight,
        6 => GlassTheme.FrostedDark,
        _ => null
    };

    private static int ComboFromTheme(GlassTheme? theme) => theme switch
    {
        GlassTheme.Light => 1,
        GlassTheme.Gray => 2,
        GlassTheme.SolidLight => 3,
        GlassTheme.SolidDark => 4,
        GlassTheme.FrostedLight => 5,
        GlassTheme.FrostedDark => 6,
        _ => 0
    };

    private static OrganizerDefinition Clone(OrganizerDefinition source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        CreatedAtUtc = source.CreatedAtUtc,
        ThemeOverride = source.ThemeOverride,
        PlacementMode = source.PlacementMode,
        PositionLocked = source.PositionLocked,
        DockEdge = source.DockEdge,
        Layout = new OrganizerLayout { Mode = source.Layout.Mode, Rows = source.Layout.Rows, Columns = source.Layout.Columns },
        CompactScale = source.CompactScale,
        CanvasScale = source.CanvasScale,
        ItemScale = source.ItemScale,
        NameScale = source.NameScale,
        ManualCanvasBaseWidthDip = source.ManualCanvasBaseWidthDip,
        ManualCanvasBaseHeightDip = source.ManualCanvasBaseHeightDip,
        Position = source.Position is null ? null : new WidgetPosition
        {
            MonitorDevice = source.Position.MonitorDevice,
            XDip = source.Position.XDip,
            YDip = source.Position.YDip,
            SavedWorkAreaWidthDip = source.Position.SavedWorkAreaWidthDip,
            SavedWorkAreaHeightDip = source.Position.SavedWorkAreaHeightDip
        },
        StorageRelativePath = source.StorageRelativePath,
        StorageAbsolutePath = source.StorageAbsolutePath,
        ItemOrder = source.ItemOrder.ToList()
    };
}
