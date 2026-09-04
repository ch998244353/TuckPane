using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using TuckPane.Controls;
using TuckPane.Models;
using TuckPane.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;
using WinRT.Interop;

namespace TuckPane;

public sealed partial class ConsoleWindow : Window
{
    private const double ConsoleCornerRadiusDip = 18;
    private readonly AppHost _host;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _placementTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _themeSaveTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _stateSaveTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hoverDelaySaveTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _uniformCompactScaleSaveTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _nameScaleSaveTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _errorInfoBarTimer;
    private AppWindow? _appWindow;
    private Task<bool>? _manageSaveTask;
    private long _manageChangeVersion;
    private long _savedManageChangeVersion;
    private NativeWindowChromeController? _chrome;
    private readonly ThemeBackdrop _themeBackdrop = new();
    private readonly ThemeSurface _themeSurface;
    private readonly ThemeEdgeSurface _settingsEdgeSurface;
    private bool _closingPermanently;
    private bool _componentReady;
    private bool _initialized;
    private bool _loadingEditor;
    private bool _loadingStartup;
    private bool _loadingOutsideClick;
    private bool _loadingNoteAlwaysOnTop;
    private bool _loadingEdgeGlow;
    private bool _loadingOrganizerTextColor;
    private bool _loadingWindowAlignment;
    private bool _loadingRememberExpandedOrganizerPosition;
    private bool _loadingDeleteBehavior;
    private bool _loadingUniformCompactScales;
    private bool _loadingNameScales;
    private bool _loadingPerformanceProfile;
    private bool _loadingExpandOnHover;
    private bool _loadingCollapseOnPointerLeave;
    private bool _loadingHoverDelays;
    private bool _loadingExclusiveExpansion;
    private bool _loadingLanguage;
    private bool _loadingTheme;
    private bool _loadingDefaultName;
    private bool _addNameWasEdited;
    private bool _adjustingAddControls;
    private bool _adjustingManageControls;
    private bool _suppressSelection;
    private bool _runtimeApplyScheduled;
    private bool _uniformCompactScaleApplyScheduled;
    private bool _uniformCompactScaleSaveInProgress;
    private int _pendingUniformCompactScaleModes;
    private int _uniformCompactScaleRevision;
    private Guid? _selectedId;
    private OrganizerDefinition? _editing;
    private OrganizerVisualChange _pendingVisualChanges;
    private CancellationTokenSource? _pageTransition;
    private string _defaultAddName = string.Empty;
    private Guid _addOrganizerId = Guid.NewGuid();
    private string? _addStoragePath;
    private int _savedHoverExpandDelayMs;
    private int _savedPointerLeaveCollapseDelayMs;
    private int _savedStationPointerLeaveCollapseDelayMs;
    private int _savedStationActivationDistanceDip;
    private int _savedStationHoverExpandDelayMs;
    private double _savedUniformFloatingCompactScale;
    private double _savedUniformPositionedCompactScale;
    private double _savedCompactNameScale;
    private double _savedExpandedNameScale;
    private ThemeTarget _themeTarget = ThemeTarget.Organizer;
    private ThemeValues _savedSettingsTheme;
    private ThemeValues _savedOrganizerTheme;
    private List<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)>? _savedUniformFloatingSnapshots;
    private List<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)>? _savedUniformPositionedSnapshots;

    public ConsoleWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        SystemBackdrop = new TransparentWindowBackdrop();
        ConsoleSurfaceHost.SystemBackdrop = _themeBackdrop;
        _themeSurface = new ThemeSurface(ConsoleSurfaceHost);
        _settingsEdgeSurface = new ThemeEdgeSurface(SettingsEdgeOverlay);
        ConsoleSurfaceHost.CornerRadius = new CornerRadius(ConsoleCornerRadiusDip);
        _errorInfoBarTimer = DispatcherQueue.CreateTimer();
        _errorInfoBarTimer.Interval = TimeSpan.FromSeconds(3);
        _errorInfoBarTimer.IsRepeating = false;
        _errorInfoBarTimer.Tick += (_, _) =>
        {
            if (ConsoleInfoBar.Severity == InfoBarSeverity.Error) ConsoleInfoBar.IsOpen = false;
        };
        _host.ThemeChanged += Host_ThemeChanged;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        RemoveTextBoxUnderline(AddNameBox, DefaultStorageDirectoryBox, ManageNameBox, ManagePathBox);
        _componentReady = true;
        _defaultAddName = AppStrings.DefaultOrganizerName;
        _loadingDefaultName = true;
        AddNameBox.Text = _defaultAddName;
        _loadingDefaultName = false;
        UpdateAddStoragePath();
        UpdateDefaultStorageDirectory();
        ApplyLanguage();
        _savedSettingsTheme = _host.State.GlobalSettings.GetTheme(ThemeTarget.Settings);
        _savedOrganizerTheme = _host.State.GlobalSettings.GetTheme(ThemeTarget.Organizer);
        ApplyTheme();
        _placementTimer = DispatcherQueue.CreateTimer();
        _placementTimer.Interval = TimeSpan.FromMilliseconds(450);
        _placementTimer.IsRepeating = false;
        _placementTimer.Tick += PlacementTimer_Tick;
        _themeSaveTimer = DispatcherQueue.CreateTimer();
        _themeSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _themeSaveTimer.IsRepeating = false;
        _themeSaveTimer.Tick += ThemeSaveTimer_Tick;
        _stateSaveTimer = DispatcherQueue.CreateTimer();
        _stateSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _stateSaveTimer.IsRepeating = false;
        _stateSaveTimer.Tick += StateSaveTimer_Tick;
        _savedHoverExpandDelayMs = _host.State.GlobalSettings.HoverExpandDelayMs;
        _savedPointerLeaveCollapseDelayMs = _host.State.GlobalSettings.PointerLeaveCollapseDelayMs;
        _savedStationPointerLeaveCollapseDelayMs = _host.State.GlobalSettings.StationPointerLeaveCollapseDelayMs;
        _savedStationActivationDistanceDip = _host.State.GlobalSettings.StationActivationDistanceDip;
        _savedStationHoverExpandDelayMs = _host.State.GlobalSettings.StationHoverExpandDelayMs;
        _hoverDelaySaveTimer = DispatcherQueue.CreateTimer();
        _hoverDelaySaveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _hoverDelaySaveTimer.IsRepeating = false;
        _hoverDelaySaveTimer.Tick += HoverDelaySaveTimer_Tick;
        _savedUniformFloatingCompactScale = _host.State.GlobalSettings.UniformFloatingCompactScale;
        _savedUniformPositionedCompactScale = _host.State.GlobalSettings.UniformPositionedCompactScale;
        _uniformCompactScaleSaveTimer = DispatcherQueue.CreateTimer();
        _uniformCompactScaleSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _uniformCompactScaleSaveTimer.IsRepeating = false;
        _uniformCompactScaleSaveTimer.Tick += UniformCompactScaleSaveTimer_Tick;
        CaptureSavedNameScales();
        _nameScaleSaveTimer = DispatcherQueue.CreateTimer();
        _nameScaleSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _nameScaleSaveTimer.IsRepeating = false;
        _nameScaleSaveTimer.Tick += NameScaleSaveTimer_Tick;
        UpdateRememberExpandedOrganizerPositionToggle();
        UpdateDeleteBehaviorToggle();
        UpdateUniformCompactScaleControls();
        UpdateNameScaleControls();
        UpdateHoverDelayControls();
        RootNavigation.SelectedItem = ManageNavItem;
    }

    public IntPtr Hwnd { get; private set; }

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
        // The theme backdrop owns all desktop sampling and blur.  Keep the
        // native chrome from extending any frame into the client area so DWM
        // cannot add an independent sheet-of-glass blur.
        _chrome = new NativeWindowChromeController(Hwnd, DispatcherQueue, extendClientFrame: false);
        Activated += ConsoleWindow_Activated;
        Closed += ConsoleWindow_Closed;
        ApplyNativeWindowChrome();
        RestorePlacement();
        _appWindow.Changed += AppWindow_Changed;
        _appWindow.Closing += AppWindow_Closing;
    }

    public void ApplyTheme()
    {
        ThemeValues theme = _host.State.GlobalSettings.GetTheme(ThemeTarget.Settings);
        ConsoleRoot.RequestedTheme = ThemePalette.IsDark(theme) ? ElementTheme.Dark : ElementTheme.Light;
        ConsoleRoot.Background = new SolidColorBrush(Colors.Transparent);
        ApplyConsoleSurfacePalette(theme);
        bool useEffects = _uiSettings.AdvancedEffectsEnabled;
        _themeBackdrop.SetTheme(theme, useEffects);
        _themeSurface.SetTheme(theme, useEffects);
        _themeSurface.SetCornerRadius(ConsoleCornerRadiusDip);
        _settingsEdgeSurface.SetTheme(theme, useEffects);
        _settingsEdgeSurface.SetCornerRadius(ConsoleCornerRadiusDip);
        _settingsEdgeSurface.SetEnabled(_host.State.GlobalSettings.EdgeGlowEnabled);
        ApplyNativeWindowChrome(refreshFrame: true);
        UpdateThemeControls();
    }

    public void RefreshAll(Guid? selectId = null)
    {
        UpdateThemeControls();
        UpdateStartupToggle();
        UpdateOutsideClickToggle();
        UpdateNoteAlwaysOnTopToggle();
        UpdateEdgeGlowToggle();
        UpdateWindowAlignmentToggle();
        UpdateRememberExpandedOrganizerPositionToggle();
        UpdateDeleteBehaviorToggle();
        UpdateUniformCompactScaleControls();
        UpdateNameScaleControls();
        UpdatePerformanceProfileControls();
        UpdateExpandOnHoverToggle();
        UpdateCollapseOnPointerLeaveToggle();
        UpdateHoverDelayControls();
        UpdateExclusiveExpansionToggle();
        UpdateDefaultStorageDirectory();
        UpdateAddStoragePath();
        PopulateManageList(selectId ?? _selectedId);
        UpdateTransferState();
        UpdateAddControls();
        UpdateOrganizerTextColorControl();
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
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NoteAlwaysOnTopToggle, AppStrings.Get("NoteAlwaysOnTopTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(OrganizerTextColorCombo, AppStrings.Get("OrganizerTextColorTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WindowAlignmentToggle, AppStrings.Get("WindowAlignmentTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MoveOrganizerFilesOnDeleteToggle, AppStrings.Get("MoveOrganizerFilesOnDeleteTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UniformFloatingCompactScaleToggle, AppStrings.Get("UniformFloatingCompactScaleTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UniformFloatingCompactScaleSlider, AppStrings.Get("CompactScaleLabel"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UniformPositionedCompactScaleToggle, AppStrings.Get("UniformPositionedCompactScaleTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UniformPositionedCompactScaleSlider, AppStrings.Get("CompactScaleLabel"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CompactNameScaleSlider, AppStrings.Get("CompactNameScaleTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExpandedNameScaleSlider, AppStrings.Get("ExpandedNameScaleTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PerformanceProfileCombo, AppStrings.Get("PerformanceProfileTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExpandOnHoverToggle, AppStrings.Get("ExpandOnHoverTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CollapseOnPointerLeaveToggle, AppStrings.Get("CollapseOnPointerLeaveTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(HoverExpandDelaySlider, AppStrings.Get("HoverExpandDelayLabel"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PointerLeaveCollapseDelaySlider, AppStrings.Get("PointerLeaveCollapseDelayLabel"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StationActivationDistanceSlider, AppStrings.Get("StationActivationDistanceTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StationHoverExpandDelaySlider, AppStrings.Get("StationHoverExpandDelayTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StationPointerLeaveCollapseDelaySlider, AppStrings.Get("StationPointerLeaveCollapseDelayTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExclusiveExpansionToggle, AppStrings.Get("ExclusiveExpansionTitle"));
        SystemNavItem.Content = AppStrings.Get("NavSystem");
        DisplayNavItem.Content = AppStrings.Get("NavDisplay");
        InteractionNavItem.Content = AppStrings.Get("NavInteraction");
        ThemeNavItem.Content = AppStrings.Get("NavTheme");
        AddNavItem.Content = AppStrings.Get("NavAdd");
        ManageNavItem.Content = AppStrings.Get("NavManage");
        MissingStorageInfo.Title = AppStrings.Get("MissingStorage");
        ApplyLocalizedTree(ConsoleRoot);
        OrganizerTextColorAuto.Content = AppStrings.Get("OrganizerTextColorAuto");
        OrganizerTextColorWhite.Content = AppStrings.Get("OrganizerTextColorWhite");
        OrganizerTextColorBlack.Content = AppStrings.Get("OrganizerTextColorBlack");
        UpdatePerformanceProfileDescription();
        PopulateDisplayCombos();
        ApplyTypography(ConsoleRoot);
        foreach (Control control in new Control[] { SystemNavItem, DisplayNavItem, InteractionNavItem, ThemeNavItem, AddNavItem, ManageNavItem })
        {
            control.FontFamily = new FontFamily(AppStrings.FontFamily);
            control.CharacterSpacing = AppStrings.CharacterSpacing;
        }
        _loadingLanguage = true;
        LanguageCombo.SelectedIndex = (int)_host.State.GlobalSettings.Language;
        _loadingLanguage = false;
        UpdateAddStoragePath();
        _errorInfoBarTimer.Stop();
        ConsoleInfoBar.IsOpen = false;
        PopulateManageList(_selectedId);
        UpdateAddControls();
        UpdateUniformCompactScaleControls();
        UpdateNameScaleControls();
        UpdateDeleteBehaviorToggle();
        UpdateHoverDelayControls();
        UpdateThemeControls();
    }

    public void UpdateTransferState()
    {
        if (DeleteOrganizerButton is not null) DeleteOrganizerButton.IsEnabled = _selectedId is not null && !_host.TransferQueue.IsActive;
    }

    public void ShowTransparencyNotice()
    {
        _errorInfoBarTimer.Stop();
        ConsoleInfoBar.Title = AppStrings.Get("TransparencyTitle");
        ConsoleInfoBar.Message = AppStrings.Get("TransparencyMessage");
        ConsoleInfoBar.Severity = InfoBarSeverity.Informational;
        ConsoleInfoBar.IsOpen = true;
    }

    public void HideToTray() => _ = HideToTrayAsync();

    private async Task HideToTrayAsync()
    {
        if (!await FlushPendingManageChangesAsync()) return;
        _appWindow?.Hide();
    }

    private void ConsoleMinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter) presenter.Minimize();
    }

    private void ConsoleCloseButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void ConsoleMinimizeButton_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetMinimizeButtonState(pressed: false);

    private void ConsoleMinimizeButton_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        SetMinimizeButtonState(pressed: true);

    private void ConsoleMinimizeButton_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        SetMinimizeButtonState(ConsoleMinimizeButton.IsPointerOver, pressed: false);

    private void ConsoleMinimizeButton_PointerExited(object sender, PointerRoutedEventArgs e) =>
        SetMinimizeButtonState(hovered: false, pressed: false);

    private void SetMinimizeButtonState(bool hovered = true, bool pressed = false)
    {
        Color foreground = ((SolidColorBrush)ConsoleRoot.Resources["ConsolePrimaryTextBrush"]).Color;
        ConsoleMinimizeButton.Background = new SolidColorBrush(hovered
            ? ColorHelper.FromArgb(pressed ? (byte)42 : (byte)24, foreground.R, foreground.G, foreground.B)
            : Colors.Transparent);
        ConsoleMinimizeButton.Foreground = new SolidColorBrush(foreground);
    }

    private void ConsoleCloseButton_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetCloseButtonState(hovered: true, pressed: false);

    private void ConsoleCloseButton_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        SetCloseButtonState(hovered: true, pressed: true);

    private void ConsoleCloseButton_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        SetCloseButtonState(ConsoleCloseButton.IsPointerOver, pressed: false);

    private void ConsoleCloseButton_PointerExited(object sender, PointerRoutedEventArgs e) =>
        SetCloseButtonState(hovered: false, pressed: false);

    private void SetCloseButtonState(bool hovered, bool pressed)
    {
        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        ConsoleCloseButton.Background = new SolidColorBrush(hovered
            ? highContrast
                ? _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent)
                : pressed ? ColorHelper.FromArgb(255, 164, 38, 25) : ColorHelper.FromArgb(255, 196, 43, 28)
            : Colors.Transparent);
        ConsoleCloseButton.Foreground = hovered
            ? new SolidColorBrush(highContrast
                ? _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background)
                : Colors.White)
            : (Brush)ConsoleRoot.Resources["ConsolePrimaryTextBrush"];
    }

    public void ShowAndActivate(Guid? organizerId = null)
    {
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

    internal async Task<bool> FlushPendingThemeSaveAsync()
    {
        _themeSaveTimer.Stop();
        GlobalSettings settings = _host.State.GlobalSettings;
        if (settings.GetTheme(ThemeTarget.Settings) == _savedSettingsTheme &&
            settings.GetTheme(ThemeTarget.Organizer) == _savedOrganizerTheme) return true;
        return await SaveThemeAsync();
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
            }
        }
        if (args.DidPositionChange || args.DidSizeChange)
        {
            _placementTimer.Stop();
            _placementTimer.Start();
        }
        ApplyNativeWindowChrome();
    }

    private void ConsoleWindow_Activated(object sender, WindowActivatedEventArgs args) => ApplyNativeWindowChrome();

    private void ConsoleWindow_Closed(object sender, WindowEventArgs args)
    {
        _host.ThemeChanged -= Host_ThemeChanged;
        _placementTimer.Stop();
        _themeSaveTimer.Stop();
        _stateSaveTimer.Stop();
        _hoverDelaySaveTimer.Stop();
        _uniformCompactScaleSaveTimer.Stop();
        _nameScaleSaveTimer.Stop();
        _themeSurface.Dispose();
        _settingsEdgeSurface.Dispose();
        Activated -= ConsoleWindow_Activated;
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

    private void ApplyConsoleSurfacePalette(ThemeValues settings)
    {
        bool dark = ThemePalette.IsDark(settings);
        Color pane = ThemePalette.LayerColor(settings, 24, 36);
        Color page = ThemePalette.LayerColor(settings, 10, 16);
        Color card = ThemePalette.LayerColor(settings, 52, 42);
        Color title = ThemePalette.LayerColor(settings, 18, 22);
        Color manageRow = ThemePalette.LayerColor(settings, 24, 18);
        Color listItem = ThemePalette.LayerColor(settings, 12, 18);
        Color selectedListItem = ThemePalette.LayerColor(settings, 52, 52);
        Color selectionAccent = ThemePalette.LayerColor(settings, 220, 220);
        Color primaryText = ThemePalette.ForegroundColor(settings);
        Color generalRowBorder = ColorHelper.FromArgb(24, primaryText.R, primaryText.G, primaryText.B);
        Color manageBorder = generalRowBorder;
        Color secondaryText = dark
            ? ColorHelper.FromArgb(255, 201, 196, 196)
            : ColorHelper.FromArgb(255, 101, 96, 96);
        Color input = ThemePalette.LayerColor(settings, 52, 48);
        Color sliderThumb = dark
            ? ColorHelper.FromArgb(255, 244, 243, 241)
            : ColorHelper.FromArgb(255, 250, 249, 246);
        Color sliderActive = dark
            ? ColorHelper.FromArgb(255, 115, 118, 121)
            : ColorHelper.FromArgb(255, 136, 139, 142);
        Color sliderInactive = dark
            ? ColorHelper.FromArgb(255, 158, 161, 163)
            : ColorHelper.FromArgb(255, 193, 196, 198);
        Color sliderThumbBorder = dark
            ? ColorHelper.FromArgb(255, 210, 208, 204)
            : ColorHelper.FromArgb(255, 184, 183, 179);
        Color sliderFocusPrimary = dark
            ? ColorHelper.FromArgb(255, 244, 243, 241)
            : ColorHelper.FromArgb(255, 97, 95, 91);
        Color sliderFocusSecondary = dark
            ? ColorHelper.FromArgb(255, 87, 84, 82)
            : ColorHelper.FromArgb(255, 250, 249, 246);

        SetSurfaceBrush("ConsolePaneSurfaceBrush", pane);
        SetSurfaceBrush("NavigationViewDefaultPaneBackground", pane);
        SetSurfaceBrush("ConsolePageSurfaceBrush", page);
        SetSurfaceBrush("ConsoleCardSurfaceBrush", card);
        SetSurfaceBrush("ConsoleGeneralRowBorderBrush", generalRowBorder);
        SetSurfaceBrush("ConsoleTitleBarSurfaceBrush", title);
        SetSurfaceBrush("ConsoleManageRowSurfaceBrush", manageRow);
        SetSurfaceBrush("ConsoleManageRowBorderBrush", manageBorder);
        SetSurfaceBrush("ConsoleListItemSurfaceBrush", listItem);
        SetSurfaceBrush("ConsoleListItemSelectedSurfaceBrush", selectedListItem);
        SetSurfaceBrush("ConsoleSelectionAccentBrush", selectionAccent);
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

    private async void PlacementTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        try
        {
            await SavePlacementAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法保存总控台位置。", ex);
            ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
        }
    }

    private async void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        FrameworkElement page = tag switch
        {
            "system" => SystemPage,
            "display" => DisplayPage,
            "interaction" => InteractionPage,
            "theme" => ThemePage,
            "add" => AddPage,
            _ => ManagePage
        };
        await ShowPageAsync(page);
        if (ReferenceEquals(page, ThemePage))
        {
            _themeTarget = ThemeTarget.Organizer;
            UpdateThemeControls();
        }
        if (ReferenceEquals(page, ManagePage)) PopulateManageList(_selectedId);
    }

    private void ShowPage(FrameworkElement page)
    {
        foreach (FrameworkElement candidate in new FrameworkElement[] { SystemPage, DisplayPage, InteractionPage, ThemePage, AddPage, ManagePage }) candidate.Visibility = ReferenceEquals(candidate, page) ? Visibility.Visible : Visibility.Collapsed;
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

    private void UpdateDefaultStorageDirectory()
    {
        if (DefaultStorageDirectoryBox is not null)
            DefaultStorageDirectoryBox.Text = AppPaths.ResolveDefaultStorageDirectory(_host.State.GlobalSettings);
    }

    private async void ChooseDefaultStorageDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow is null) return;
        try
        {
            var picker = new FolderPicker(_appWindow.Id)
            {
                Title = AppStrings.Get("SelectDefaultStorageDirectoryTitle"),
                CommitButtonText = AppStrings.Get("SelectStorageFolderCommit"),
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            string current = AppPaths.ResolveDefaultStorageDirectory(_host.State.GlobalSettings);
            picker.SuggestedStartFolder = Directory.Exists(current) ? current : AppPaths.WindowsRoot;
            PickFolderResult? result = await picker.PickSingleFolderAsync();
            if (result is null || string.IsNullOrWhiteSpace(result.Path)) return;
            await _host.SetDefaultStorageDirectoryAsync(result.Path);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新默认存储目录。", ex);
            ShowError(AppStrings.Get("DefaultStorageDirectoryErrorTitle"), ex.Message);
        }
    }

    private async void ResetDefaultStorageDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _host.SetDefaultStorageDirectoryAsync(null);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法恢复默认存储目录。", ex);
            ShowError(AppStrings.Get("DefaultStorageDirectoryErrorTitle"), ex.Message);
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

    private void UpdateNoteAlwaysOnTopToggle()
    {
        if (NoteAlwaysOnTopToggle is null) return;
        _loadingNoteAlwaysOnTop = true;
        NoteAlwaysOnTopToggle.IsOn = _host.State.GlobalSettings.NoteAlwaysOnTop;
        _loadingNoteAlwaysOnTop = false;
    }

    private void UpdateOrganizerTextColorControl()
    {
        if (OrganizerTextColorCombo is null) return;
        _loadingOrganizerTextColor = true;
        OrganizerTextColor mode = GlobalSettings.NormalizeOrganizerTextColor(
            _host.State.GlobalSettings.OrganizerTextColor);
        OrganizerTextColorCombo.SelectedItem = mode switch
        {
            OrganizerTextColor.Auto => OrganizerTextColorAuto,
            OrganizerTextColor.Black => OrganizerTextColorBlack,
            _ => OrganizerTextColorWhite
        };
        _loadingOrganizerTextColor = false;
    }

    private async void OrganizerTextColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_componentReady || _loadingOrganizerTextColor ||
            OrganizerTextColorCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        try
        {
            OrganizerTextColor mode = tag switch
            {
                "Auto" => OrganizerTextColor.Auto,
                "Black" => OrganizerTextColor.Black,
                _ => OrganizerTextColor.White
            };
            await _host.SetOrganizerTextColorAsync(mode);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新收纳窗文字颜色。", ex);
            UpdateOrganizerTextColorControl();
            ShowError(AppStrings.Get("OrganizerTextColorErrorTitle"), ex.Message);
        }
    }

    private async void NoteAlwaysOnTopToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingNoteAlwaysOnTop) return;
        try
        {
            await _host.SetNoteAlwaysOnTopAsync(NoteAlwaysOnTopToggle.IsOn);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新便签置顶设置。", ex);
            UpdateNoteAlwaysOnTopToggle();
            ShowError(AppStrings.Get("NoteAlwaysOnTopErrorTitle"), ex.Message);
        }
    }

    private void UpdateEdgeGlowToggle()
    {
        if (EdgeGlowToggle is null) return;
        _loadingEdgeGlow = true;
        EdgeGlowToggle.IsOn = _host.State.GlobalSettings.EdgeGlowEnabled;
        _loadingEdgeGlow = false;
    }

    private async void EdgeGlowToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingEdgeGlow) return;
        try
        {
            await _host.SetEdgeGlowEnabledAsync(EdgeGlowToggle.IsOn);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新边缘弧光设置。", ex);
            UpdateEdgeGlowToggle();
            ShowError(AppStrings.Get("EdgeGlowErrorTitle"), ex.Message);
        }
    }

    private void UpdateWindowAlignmentToggle()
    {
        if (WindowAlignmentToggle is null) return;
        _loadingWindowAlignment = true;
        WindowAlignmentToggle.IsOn = _host.State.GlobalSettings.WindowAlignmentEnabled;
        _loadingWindowAlignment = false;
    }

    private async void WindowAlignmentToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingWindowAlignment) return;
        try
        {
            await _host.SetWindowAlignmentEnabledAsync(WindowAlignmentToggle.IsOn);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新窗口拖动对齐设置。", ex);
            UpdateWindowAlignmentToggle();
            ShowError(AppStrings.Get("WindowAlignmentErrorTitle"), ex.Message);
        }
    }

    private void UpdateRememberExpandedOrganizerPositionToggle()
    {
        if (RememberExpandedOrganizerPositionToggle is null) return;
        _loadingRememberExpandedOrganizerPosition = true;
        RememberExpandedOrganizerPositionToggle.IsOn = _host.State.GlobalSettings.RememberExpandedOrganizerPosition;
        _loadingRememberExpandedOrganizerPosition = false;
    }

    private async void RememberExpandedOrganizerPositionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingRememberExpandedOrganizerPosition) return;
        bool previous = _host.State.GlobalSettings.RememberExpandedOrganizerPosition;
        _host.State.GlobalSettings.RememberExpandedOrganizerPosition = RememberExpandedOrganizerPositionToggle.IsOn;
        try { await _host.SaveStateAsync(); }
        catch (Exception ex)
        {
            _host.State.GlobalSettings.RememberExpandedOrganizerPosition = previous;
            UpdateRememberExpandedOrganizerPositionToggle();
            AppLogger.Error("无法更新收纳窗展开位置记忆设置。", ex);
            ShowError(AppStrings.Get("RememberExpandedOrganizerPositionErrorTitle"), ex.Message);
        }
    }

    private void UpdateDeleteBehaviorToggle()
    {
        if (MoveOrganizerFilesOnDeleteToggle is null) return;
        _loadingDeleteBehavior = true;
        MoveOrganizerFilesOnDeleteToggle.IsOn = _host.State.GlobalSettings.MoveOrganizerFilesToDesktopOnDelete;
        if (DeleteOrganizerButton is not null)
            DeleteOrganizerButton.Content = AppStrings.Get(MoveOrganizerFilesOnDeleteToggle.IsOn ? "ExportDelete" : "DeleteOrganizerOnly");
        _loadingDeleteBehavior = false;
    }

    private async void MoveOrganizerFilesOnDeleteToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingDeleteBehavior) return;
        bool previous = _host.State.GlobalSettings.MoveOrganizerFilesToDesktopOnDelete;
        _host.State.GlobalSettings.MoveOrganizerFilesToDesktopOnDelete = MoveOrganizerFilesOnDeleteToggle.IsOn;
        try { await _host.SaveStateAsync(); }
        catch (Exception ex)
        {
            _host.State.GlobalSettings.MoveOrganizerFilesToDesktopOnDelete = previous;
            UpdateDeleteBehaviorToggle();
            AppLogger.Error("无法更新删除收纳窗文件处理设置。", ex);
            ShowError(AppStrings.Get("DeleteBehaviorErrorTitle"), ex.Message);
        }
    }

    private void UpdateUniformCompactScaleControls()
    {
        if (UniformFloatingCompactScaleSlider is null || UniformPositionedCompactScaleSlider is null) return;
        GlobalSettings settings = _host.State.GlobalSettings;
        _loadingUniformCompactScales = true;
        UniformFloatingCompactScaleToggle.IsOn = settings.UseUniformFloatingCompactScale;
        UniformFloatingCompactScaleSlider.Value = settings.UniformFloatingCompactScale;
        UniformFloatingCompactScaleSlider.IsEnabled = settings.UseUniformFloatingCompactScale;
        SetPercent(UniformFloatingCompactScalePercent, settings.UniformFloatingCompactScale);
        UniformPositionedCompactScaleToggle.IsOn = settings.UseUniformPositionedCompactScale;
        UniformPositionedCompactScaleSlider.Value = settings.UniformPositionedCompactScale;
        UniformPositionedCompactScaleSlider.IsEnabled = settings.UseUniformPositionedCompactScale;
        SetPercent(UniformPositionedCompactScalePercent, settings.UniformPositionedCompactScale);
        _loadingUniformCompactScales = false;
        UpdateAddControls();
        UpdateManageControls();
    }

    private async void UniformCompactScaleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingUniformCompactScales) return;
        if (_uniformCompactScaleApplyScheduled) ApplyPendingUniformCompactScaleChanges(null, EventArgs.Empty);
        _uniformCompactScaleSaveTimer.Stop();
        OrganizerPlacementMode mode = ReferenceEquals(sender, UniformFloatingCompactScaleToggle)
            ? OrganizerPlacementMode.Floating
            : OrganizerPlacementMode.Positioned;
        bool enabled = mode == OrganizerPlacementMode.Floating
            ? UniformFloatingCompactScaleToggle.IsOn
            : UniformPositionedCompactScaleToggle.IsOn;
        UniformFloatingCompactScaleToggle.IsEnabled = false;
        UniformPositionedCompactScaleToggle.IsEnabled = false;
        UniformFloatingCompactScaleSlider.IsEnabled = false;
        UniformPositionedCompactScaleSlider.IsEnabled = false;
        try
        {
            await _host.SetUniformCompactScaleEnabledAsync(mode, enabled);
            CaptureSavedUniformCompactScales();
        }
        catch (Exception ex)
        {
            RestoreSavedUniformCompactScales();
            AppLogger.Error("无法更新统一入口大小开关。", ex);
            ShowError(AppStrings.Get("UniformCompactScaleErrorTitle"), ex.Message);
        }
        finally
        {
            UniformFloatingCompactScaleToggle.IsEnabled = true;
            UniformPositionedCompactScaleToggle.IsEnabled = true;
            UpdateUniformCompactScaleControls();
        }
    }

    private void UniformCompactScaleSlider_ValueChanged(object sender, object e)
    {
        if (!_componentReady || _loadingUniformCompactScales || _uniformCompactScaleSaveTimer is null) return;
        OrganizerPlacementMode mode = ReferenceEquals(sender, UniformFloatingCompactScaleSlider)
            ? OrganizerPlacementMode.Floating
            : OrganizerPlacementMode.Positioned;
        _pendingUniformCompactScaleModes |= 1 << (int)mode;
        if (_uniformCompactScaleApplyScheduled) return;
        _uniformCompactScaleApplyScheduled = true;
        CompositionTarget.Rendering += ApplyPendingUniformCompactScaleChanges;
    }

    private void ApplyPendingUniformCompactScaleChanges(object? sender, object args)
    {
        if (_uniformCompactScaleApplyScheduled) CompositionTarget.Rendering -= ApplyPendingUniformCompactScaleChanges;
        _uniformCompactScaleApplyScheduled = false;
        int pendingModes = _pendingUniformCompactScaleModes;
        _pendingUniformCompactScaleModes = 0;
        bool applied = false;
        foreach (OrganizerPlacementMode mode in new[] { OrganizerPlacementMode.Floating, OrganizerPlacementMode.Positioned })
        {
            if ((pendingModes & 1 << (int)mode) == 0) continue;
            List<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)>? snapshots = mode == OrganizerPlacementMode.Floating
                ? _savedUniformFloatingSnapshots
                : _savedUniformPositionedSnapshots;
            bool startedBatch = snapshots is null;
            if (startedBatch)
            {
                snapshots = _host.CaptureUniformCompactScaleSnapshots(mode);
                if (mode == OrganizerPlacementMode.Floating) _savedUniformFloatingSnapshots = snapshots;
                else _savedUniformPositionedSnapshots = snapshots;
            }
            double scale = mode == OrganizerPlacementMode.Floating
                ? UniformFloatingCompactScaleSlider.Value
                : UniformPositionedCompactScaleSlider.Value;
            string? error = _host.ApplyUniformCompactScale(mode, scale);
            if (error is null)
            {
                applied = true;
                _uniformCompactScaleRevision++;
                continue;
            }
            if (startedBatch)
            {
                if (mode == OrganizerPlacementMode.Floating) _savedUniformFloatingSnapshots = null;
                else _savedUniformPositionedSnapshots = null;
            }
            ShowError(AppStrings.Get("UniformCompactScaleErrorTitle"), error);
        }
        UpdateUniformCompactScaleControls();
        if (!applied) return;
        _uniformCompactScaleSaveTimer.Stop();
        _uniformCompactScaleSaveTimer.Start();
    }

    private async void UniformCompactScaleSaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_uniformCompactScaleSaveInProgress) return;
        if (_uniformCompactScaleApplyScheduled)
        {
            int revisionBeforeApply = _uniformCompactScaleRevision;
            ApplyPendingUniformCompactScaleChanges(null, EventArgs.Empty);
            if (revisionBeforeApply != _uniformCompactScaleRevision) return;
        }
        _uniformCompactScaleSaveInProgress = true;
        UniformFloatingCompactScaleToggle.IsEnabled = false;
        UniformPositionedCompactScaleToggle.IsEnabled = false;
        UniformFloatingCompactScaleSlider.IsEnabled = false;
        UniformPositionedCompactScaleSlider.IsEnabled = false;
        int savingRevision = _uniformCompactScaleRevision;
        try
        {
            await _host.SaveStateAsync();
            if (savingRevision != _uniformCompactScaleRevision)
            {
                _uniformCompactScaleSaveTimer.Stop();
                _uniformCompactScaleSaveTimer.Start();
                return;
            }
            CaptureSavedUniformCompactScales();
        }
        catch (Exception ex)
        {
            _uniformCompactScaleSaveTimer.Stop();
            RestoreSavedUniformCompactScales();
            AppLogger.Error("无法保存统一入口大小。", ex);
            ShowError(AppStrings.Get("UniformCompactScaleErrorTitle"), ex.Message);
        }
        finally
        {
            _uniformCompactScaleSaveInProgress = false;
            UniformFloatingCompactScaleToggle.IsEnabled = true;
            UniformPositionedCompactScaleToggle.IsEnabled = true;
            UpdateUniformCompactScaleControls();
        }
    }

    private void CaptureSavedUniformCompactScales()
    {
        _savedUniformFloatingCompactScale = _host.State.GlobalSettings.UniformFloatingCompactScale;
        _savedUniformPositionedCompactScale = _host.State.GlobalSettings.UniformPositionedCompactScale;
        _savedUniformFloatingSnapshots = null;
        _savedUniformPositionedSnapshots = null;
    }

    private void UpdateNameScaleControls()
    {
        if (CompactNameScaleSlider is null || ExpandedNameScaleSlider is null) return;
        GlobalSettings settings = _host.State.GlobalSettings;
        _loadingNameScales = true;
        CompactNameScaleSlider.Value = settings.UniformFloatingCompactNameScale;
        ExpandedNameScaleSlider.Value = settings.ExpandedNameScale;
        SetPercent(CompactNameScalePercent, settings.UniformFloatingCompactNameScale);
        SetPercent(ExpandedNameScalePercent, settings.ExpandedNameScale);
        _loadingNameScales = false;
    }

    private void NameScaleSlider_ValueChanged(object sender, object e)
    {
        if (!_componentReady || _loadingNameScales) return;
        _host.ApplyNameScales(CompactNameScaleSlider.Value, ExpandedNameScaleSlider.Value);
        SetPercent(CompactNameScalePercent, CompactNameScaleSlider.Value);
        SetPercent(ExpandedNameScalePercent, ExpandedNameScaleSlider.Value);
        _nameScaleSaveTimer.Stop();
        _nameScaleSaveTimer.Start();
    }

    private async void NameScaleSaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        SetNameScaleControlsEnabled(false);
        try
        {
            await _host.SaveStateAsync();
            CaptureSavedNameScales();
        }
        catch (Exception ex)
        {
            RestoreSavedNameScales();
            AppLogger.Error("无法保存名称大小。", ex);
            ShowError(AppStrings.Get("UniformCompactNameScaleErrorTitle"), ex.Message);
        }
        finally
        {
            SetNameScaleControlsEnabled(true);
            UpdateNameScaleControls();
        }
    }

    private void CaptureSavedNameScales()
    {
        GlobalSettings settings = _host.State.GlobalSettings;
        _savedCompactNameScale = settings.UniformFloatingCompactNameScale;
        _savedExpandedNameScale = settings.ExpandedNameScale;
    }

    private void RestoreSavedNameScales() =>
        _host.ApplyNameScales(_savedCompactNameScale, _savedExpandedNameScale);

    private void SetNameScaleControlsEnabled(bool enabled)
    {
        CompactNameScaleSlider.IsEnabled = enabled;
        ExpandedNameScaleSlider.IsEnabled = enabled;
    }

    private void RestoreSavedUniformCompactScales()
    {
        if (_savedUniformFloatingSnapshots is not null)
            _host.RestoreUniformCompactScale(
                OrganizerPlacementMode.Floating,
                _savedUniformFloatingCompactScale,
                _savedUniformFloatingSnapshots);
        if (_savedUniformPositionedSnapshots is not null)
            _host.RestoreUniformCompactScale(
                OrganizerPlacementMode.Positioned,
                _savedUniformPositionedCompactScale,
                _savedUniformPositionedSnapshots);
        _savedUniformFloatingSnapshots = null;
        _savedUniformPositionedSnapshots = null;
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
            UpdateHoverDelayControls();
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新悬浮展开设置。", ex);
            UpdateExpandOnHoverToggle();
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

    private async void CollapseOnPointerLeaveToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingCollapseOnPointerLeave) return;
        try
        {
            await _host.SetCollapseOnPointerLeaveAsync(CollapseOnPointerLeaveToggle.IsOn);
            UpdateHoverDelayControls();
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新鼠标离开收缩设置。", ex);
            UpdateCollapseOnPointerLeaveToggle();
            ShowError(AppStrings.Get("CollapseOnPointerLeaveErrorTitle"), ex.Message);
        }
    }

    private void UpdateHoverDelayControls()
    {
        if (HoverExpandDelaySlider is null || PointerLeaveCollapseDelaySlider is null ||
            StationPointerLeaveCollapseDelaySlider is null || StationActivationDistanceSlider is null ||
            StationHoverExpandDelaySlider is null) return;
        _loadingHoverDelays = true;
        HoverExpandDelaySlider.Value = _host.State.GlobalSettings.HoverExpandDelayMs;
        PointerLeaveCollapseDelaySlider.Value = _host.State.GlobalSettings.PointerLeaveCollapseDelayMs;
        StationPointerLeaveCollapseDelaySlider.Value = _host.State.GlobalSettings.StationPointerLeaveCollapseDelayMs;
        StationActivationDistanceSlider.Value = _host.State.GlobalSettings.StationActivationDistanceDip;
        StationHoverExpandDelaySlider.Value = _host.State.GlobalSettings.StationHoverExpandDelayMs;
        HoverExpandDelaySlider.IsEnabled = _host.State.GlobalSettings.ExpandOnHover;
        PointerLeaveCollapseDelaySlider.IsEnabled = _host.State.GlobalSettings.CollapseOnPointerLeave;
        HoverExpandDelayValue.Text = AppStrings.Format("MillisecondsFormat", _host.State.GlobalSettings.HoverExpandDelayMs);
        PointerLeaveCollapseDelayValue.Text = AppStrings.Format("MillisecondsFormat", _host.State.GlobalSettings.PointerLeaveCollapseDelayMs);
        StationPointerLeaveCollapseDelayValue.Text = AppStrings.Format(
            "MillisecondsFormat",
            _host.State.GlobalSettings.StationPointerLeaveCollapseDelayMs);
        StationActivationDistanceValue.Text = AppStrings.Format(
            "DipFormat",
            _host.State.GlobalSettings.StationActivationDistanceDip);
        StationHoverExpandDelayValue.Text = AppStrings.Format(
            "MillisecondsFormat",
            _host.State.GlobalSettings.StationHoverExpandDelayMs);
        _loadingHoverDelays = false;
    }

    private void HoverDelaySlider_ValueChanged(object sender, object e)
    {
        if (!_componentReady || _loadingHoverDelays || _hoverDelaySaveTimer is null) return;
        _host.SetHoverDelays(
            (int)HoverExpandDelaySlider.Value,
            (int)PointerLeaveCollapseDelaySlider.Value,
            (int)StationPointerLeaveCollapseDelaySlider.Value);
        _host.SetStationActivation(
            (int)StationActivationDistanceSlider.Value,
            (int)StationHoverExpandDelaySlider.Value);
        UpdateHoverDelayControls();
        _hoverDelaySaveTimer.Stop();
        _hoverDelaySaveTimer.Start();
    }

    private async void HoverDelaySaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        int hoverExpandDelayMs = _host.State.GlobalSettings.HoverExpandDelayMs;
        int pointerLeaveCollapseDelayMs = _host.State.GlobalSettings.PointerLeaveCollapseDelayMs;
        int stationPointerLeaveCollapseDelayMs = _host.State.GlobalSettings.StationPointerLeaveCollapseDelayMs;
        int stationActivationDistanceDip = _host.State.GlobalSettings.StationActivationDistanceDip;
        int stationHoverExpandDelayMs = _host.State.GlobalSettings.StationHoverExpandDelayMs;
        try
        {
            await _host.SaveStateAsync();
            _savedHoverExpandDelayMs = hoverExpandDelayMs;
            _savedPointerLeaveCollapseDelayMs = pointerLeaveCollapseDelayMs;
            _savedStationPointerLeaveCollapseDelayMs = stationPointerLeaveCollapseDelayMs;
            _savedStationActivationDistanceDip = stationActivationDistanceDip;
            _savedStationHoverExpandDelayMs = stationHoverExpandDelayMs;
        }
        catch (Exception ex)
        {
            _hoverDelaySaveTimer.Stop();
            _host.SetHoverDelays(
                _savedHoverExpandDelayMs,
                _savedPointerLeaveCollapseDelayMs,
                _savedStationPointerLeaveCollapseDelayMs);
            _host.SetStationActivation(
                _savedStationActivationDistanceDip,
                _savedStationHoverExpandDelayMs);
            UpdateHoverDelayControls();
            AppLogger.Error("无法保存悬浮判定设置。", ex);
            ShowError(AppStrings.Get("HoverDelayErrorTitle"), ex.Message);
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

    private void UpdatePerformanceProfileControls()
    {
        if (PerformanceProfileCombo is null) return;
        _loadingPerformanceProfile = true;
        PerformanceProfileCombo.SelectedIndex = (int)_host.State.GlobalSettings.PerformanceProfile;
        _loadingPerformanceProfile = false;
        UpdatePerformanceProfileDescription();
    }

    private void UpdatePerformanceProfileDescription()
    {
        if (PerformanceProfileDescription is null) return;
        string key = _host.State.GlobalSettings.PerformanceProfile switch
        {
            PerformanceProfile.PowerSaver => "PerformanceProfilePowerSaverDescription",
            PerformanceProfile.HighPerformance => "PerformanceProfileHighPerformanceDescription",
            _ => "PerformanceProfileBalancedDescription"
        };
        PerformanceProfileDescription.Text = AppStrings.Get(key);
    }

    private async void PerformanceProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_componentReady || _loadingPerformanceProfile || PerformanceProfileCombo.SelectedIndex < 0) return;
        try
        {
            await _host.SetPerformanceProfileAsync((PerformanceProfile)PerformanceProfileCombo.SelectedIndex);
            UpdatePerformanceProfileDescription();
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新性能档位。", ex);
            UpdatePerformanceProfileControls();
            ShowError(AppStrings.Get("PerformanceProfileErrorTitle"), ex.Message);
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
        await FlushPendingManageChangesAsync();
        _pageTransition?.Cancel();
        _pageTransition?.Dispose();
        _pageTransition = new CancellationTokenSource();
        CancellationToken token = _pageTransition.Token;
        ShowPage(page);
        if (!_host.State.GlobalSettings.ShouldUseCustomAnimations(_uiSettings.AnimationsEnabled)) return;
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

    private void ThemeColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingTheme || sender is not FrameworkElement { Tag: string hex }) return;
        ApplyThemeChange(colorArgb: ParseThemeColor(hex));
    }

    private void ThemeColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_componentReady || _loadingTheme) return;
        ApplyThemeChange(colorArgb: ToArgb(args.NewColor));
    }

    private void ThemeTransparencySlider_ValueChanged(object sender, object e)
    {
        if (!_componentReady || _loadingTheme) return;
        ThemeValues theme = _host.State.GlobalSettings.GetTheme(_themeTarget);
        if (theme.SolidColorMode)
            ApplyThemeChange(solidOpacity: ThemeTransparencySlider.Value);
        else
            ApplyThemeChange(transparency: ThemeTransparencySlider.Value);
    }

    private void ThemeBlurStrengthSlider_ValueChanged(object sender, object e)
    {
        if (!_componentReady || _loadingTheme) return;
        ApplyThemeChange(blurStrength: ThemeBlurStrengthSlider.Value);
    }

    private void ThemeModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingTheme || sender is not FrameworkElement { Tag: string mode }) return;
        ApplyThemeChange(solidColorMode: mode.Equals("Solid", StringComparison.Ordinal));
        UpdateThemeControls();
    }

    private void ApplyThemeChange(
        uint? colorArgb = null,
        double? transparency = null,
        double? blurStrength = null,
        bool? solidColorMode = null,
        double? solidOpacity = null)
    {
        GlobalSettings settings = _host.State.GlobalSettings;
        ThemeValues theme = settings.GetTheme(_themeTarget);
        _host.UpdateGlobalTheme(
            _themeTarget,
            colorArgb ?? theme.ColorArgb,
            transparency ?? theme.Transparency,
            blurStrength ?? theme.BlurStrength,
            solidColorMode ?? theme.SolidColorMode,
            solidOpacity);
        _themeSaveTimer.Stop();
        _themeSaveTimer.Start();
    }

    private void ThemeTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingTheme || sender is not FrameworkElement { Tag: string name } ||
            !Enum.TryParse(name, out ThemeTarget target)) return;
        _themeTarget = target;
        UpdateThemeControls();
    }

    private void UpdateThemeControls()
    {
        if (ColorSwatchesPanel is null) return;
        ThemeValues theme = _host.State.GlobalSettings.GetTheme(_themeTarget);
        _loadingTheme = true;
        SettingsThemeTargetButton.IsChecked = _themeTarget == ThemeTarget.Settings;
        OrganizerThemeTargetButton.IsChecked = _themeTarget == ThemeTarget.Organizer;
        foreach (ToggleButton button in ColorSwatchesPanel.Children.OfType<ToggleButton>())
        {
            string hex = (string)button.Tag;
            button.IsChecked = ParseThemeColor(hex) == theme.ColorArgb;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button,
                AppStrings.Format("ThemeColorPresetFormat", hex));
        }
        ThemeColorPicker.Color = FromArgb(theme.ColorArgb);
        ThemeTransparencySlider.Maximum = theme.SolidColorMode ? 1 : GlobalSettings.MaximumThemeTransparency;
        double displayedOpacity = theme.SolidColorMode ? theme.SolidOpacity : theme.Transparency;
        ThemeTransparencySlider.Value = displayedOpacity;
        ThemeTransparencyValue.Text = $"{Math.Round(displayedOpacity * 100):0}%";
        ThemeBlurStrengthSlider.Value = theme.BlurStrength;
        ThemeBlurStrengthValue.Text = $"{Math.Round(theme.BlurStrength * 100):0}%";
        ThemeGlassModeButton.IsChecked = !theme.SolidColorMode;
        ThemeSolidModeButton.IsChecked = theme.SolidColorMode;
        // Opacity is independent of the material mode: solid colour can be
        // translucent, while Glass keeps the same 0-100% surface control.
        ThemeTransparencyRow.Visibility = Visibility.Visible;
        ThemeBlurStrengthRow.Visibility = theme.SolidColorMode ? Visibility.Collapsed : Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ThemeColorPicker, AppStrings.Get("ThemeCustomColor"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ThemeTransparencySlider, AppStrings.Get("ThemeTransparencyLabel"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ThemeBlurStrengthSlider, AppStrings.Get("ThemeBlurStrengthLabel"));
        _loadingTheme = false;
    }

    private async void ThemeSaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) =>
        await SaveThemeAsync();

    private async Task<bool> SaveThemeAsync()
    {
        GlobalSettings settings = _host.State.GlobalSettings;
        ThemeValues settingsTheme = settings.GetTheme(ThemeTarget.Settings);
        ThemeValues organizerTheme = settings.GetTheme(ThemeTarget.Organizer);
        try
        {
            await _host.SaveStateAsync();
            _savedSettingsTheme = settingsTheme;
            _savedOrganizerTheme = organizerTheme;
            return true;
        }
        catch (Exception ex)
        {
            _host.UpdateGlobalTheme(
                ThemeTarget.Settings,
                _savedSettingsTheme.ColorArgb,
                _savedSettingsTheme.Transparency,
                _savedSettingsTheme.BlurStrength,
                _savedSettingsTheme.SolidColorMode,
                _savedSettingsTheme.SolidOpacity);
            _host.UpdateGlobalTheme(
                ThemeTarget.Organizer,
                _savedOrganizerTheme.ColorArgb,
                _savedOrganizerTheme.Transparency,
                _savedOrganizerTheme.BlurStrength,
                _savedOrganizerTheme.SolidColorMode,
                _savedOrganizerTheme.SolidOpacity);
            ShowError(AppStrings.Get("ThemeSaveErrorTitle"), ex.Message);
            return false;
        }
    }

    private void Host_ThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private static uint ParseThemeColor(string hex) =>
        0xFF000000 | uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static uint ToArgb(Color color) =>
        0xFF000000 | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private static Color FromArgb(uint argb) => ColorHelper.FromArgb(
        255,
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

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
            string suggested = _addStoragePath ?? AppPaths.ResolveDefaultStorageDirectory(_host.State.GlobalSettings);
            if (!Directory.Exists(suggested)) suggested = AppPaths.WindowsRoot;
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
        _addOrganizerId = Guid.NewGuid();
        UpdateAddStoragePath();
    }

    private void UpdateAddStoragePath()
    {
        if (AddStoragePathBox is not null)
        {
            string path = _addStoragePath ?? AppPaths.CreateDefaultOrganizerStoragePath(
                AppPaths.ResolveDefaultStorageDirectory(_host.State.GlobalSettings),
                _addOrganizerId);
            AddStoragePathBox.Text = _addStoragePath ?? AppStrings.Format("AutomaticStoragePathFormat", path);
        }
    }

    private void UpdateAddControls()
    {
        if (!_componentReady || AddRowsCard is null || _adjustingAddControls) return;
        _adjustingAddControls = true;
        bool positioned = AddPlacementModeCombo.SelectedIndex == (int)OrganizerPlacementMode.Positioned;
        bool station = AddPlacementModeCombo.SelectedIndex == (int)OrganizerPlacementMode.Station;
        if (station) AddExpandedContentModeCombo.SelectedIndex = (int)OrganizerExpandedContentMode.Icon;
        AddExpandedContentModeCombo.IsEnabled = !station;
        bool compactList = !station &&
            AddExpandedContentModeCombo.SelectedIndex == (int)OrganizerExpandedContentMode.CompactList;
        AddNameCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        AddDisplayCard.Visibility = station ? Visibility.Visible : Visibility.Collapsed;
        AddDockEdgeCard.Visibility = station ? Visibility.Visible : Visibility.Collapsed;
        AddCompactScaleCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        AddRowsCard.Visibility = compactList ? Visibility.Collapsed : Visibility.Visible;
        AddColumnsCard.Visibility = compactList ? Visibility.Collapsed : Visibility.Visible;
        AddCompactScaleSlider.Maximum = positioned
            ? OrganizerLimits.MaximumPositionedCompactScale
            : OrganizerLimits.MaximumCompactScale;
        OrganizerPlacementMode placementMode = (OrganizerPlacementMode)Math.Clamp(AddPlacementModeCombo.SelectedIndex, 0, 2);
        bool compactScaleConstrained = _host.State.GlobalSettings.UsesUniformCompactScale(placementMode);
        if (compactScaleConstrained)
            AddCompactScaleSlider.Value = _host.State.GlobalSettings.ResolveCompactScale(placementMode, AddCompactScaleSlider.Value);
        AddCompactScaleSlider.IsEnabled = !compactScaleConstrained;
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
            Id = _addOrganizerId,
            Name = AddPlacementModeCombo.SelectedIndex == (int)OrganizerPlacementMode.Station
                ? AppStrings.Get("StationDefaultName")
                : string.IsNullOrWhiteSpace(AddNameBox.Text) ? AppStrings.DefaultOrganizerName : AddNameBox.Text.Trim(),
            PlacementMode = (OrganizerPlacementMode)Math.Clamp(AddPlacementModeCombo.SelectedIndex, 0, 2),
            DockEdge = (OrganizerDockEdge)Math.Clamp(AddDockEdgeCombo.SelectedIndex, 0, 3),
            Position = new WidgetPosition { MonitorDevice = SelectedDisplayDevice(AddDisplayCombo) ?? string.Empty },
            Layout = new OrganizerLayout { Mode = OrganizerLayoutMode.Grid, Rows = rows, Columns = columns },
            CompactScale = AddCompactScaleSlider.Value,
            CanvasScale = AddCanvasScaleSlider.Value,
            ItemScale = AddItemScaleSlider.Value,
            ExpandedContentMode = station
                ? OrganizerExpandedContentMode.Icon
                : (OrganizerExpandedContentMode)Math.Clamp(AddExpandedContentModeCombo.SelectedIndex, 0, 1)
        };
        try
        {
            OrganizerDefinition created = await _host.CreateOrganizerAsync(definition, _addStoragePath);
            _addStoragePath = null;
            _addOrganizerId = Guid.NewGuid();
            UpdateAddStoragePath();
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

    private async void ManageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateManageListItemSurfaces();
        if (_suppressSelection || ManageList.SelectedItem is not ListViewItem { Tag: Guid nextId } || nextId == _selectedId) return;
        await FlushPendingManageChangesAsync();
        if (ManageList.SelectedItem is ListViewItem { Tag: Guid currentId } && currentId == nextId)
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
        ManageExpandedContentModeCombo.SelectedIndex = (int)source.ExpandedContentMode;
        SelectDisplay(ManageDisplayCombo, source.Position?.MonitorDevice);
        ManageDockEdgeCombo.SelectedIndex = (int)source.DockEdge;
        ManageRowsSlider.Value = source.Layout.Rows;
        ManageColumnsSlider.Value = source.Layout.Columns;
        ManageCompactScaleSlider.Value = source.CompactScale;
        ManageCanvasScaleSlider.Value = source.CanvasScale;
        ManageItemScaleSlider.Value = source.ItemScale;
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
        _manageChangeVersion++;
        ScheduleRuntimeApply(GetVisualChange(sender));
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    private void UpdateManageControls()
    {
        if (ManageRowsCard is null || _adjustingManageControls) return;
        _adjustingManageControls = true;
        bool positioned = ManagePlacementModeCombo.SelectedIndex == (int)OrganizerPlacementMode.Positioned;
        bool station = ManagePlacementModeCombo.SelectedIndex == (int)OrganizerPlacementMode.Station;
        bool stationSource = _editing?.PlacementMode == OrganizerPlacementMode.Station;
        ManageFloatingModeItem.IsEnabled = !stationSource;
        ManagePositionedModeItem.IsEnabled = !stationSource;
        ManageStationModeItem.IsEnabled = stationSource;
        if (station) ManageExpandedContentModeCombo.SelectedIndex = (int)OrganizerExpandedContentMode.Icon;
        ManageExpandedContentModeCombo.IsEnabled = !station;
        bool compactList = !station &&
            ManageExpandedContentModeCombo.SelectedIndex == (int)OrganizerExpandedContentMode.CompactList;
        ManageNameCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        ManageNameError.Visibility = Visibility.Collapsed;
        ManageDisplayCard.Visibility = station ? Visibility.Visible : Visibility.Collapsed;
        ManageDockEdgeCard.Visibility = station ? Visibility.Visible : Visibility.Collapsed;
        ManageCompactScaleCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        ManageRowsCard.Visibility = compactList ? Visibility.Collapsed : Visibility.Visible;
        ManageColumnsCard.Visibility = compactList ? Visibility.Collapsed : Visibility.Visible;
        ManageCompactScaleSlider.Maximum = positioned
            ? OrganizerLimits.MaximumPositionedCompactScale
            : OrganizerLimits.MaximumCompactScale;
        OrganizerPlacementMode placementMode = (OrganizerPlacementMode)Math.Clamp(ManagePlacementModeCombo.SelectedIndex, 0, 2);
        bool compactScaleConstrained = _host.State.GlobalSettings.UsesUniformCompactScale(placementMode);
        if (compactScaleConstrained)
            ManageCompactScaleSlider.Value = _host.State.GlobalSettings.ResolveCompactScale(placementMode, ManageCompactScaleSlider.Value);
        ManageCompactScaleSlider.IsEnabled = !compactScaleConstrained;
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
                double availablePanelHeightDip = Math.Max(
                    1,
                    work.Height / display.Scale - DisplayPlacementService.ExpandedTitleBandDip);
                double fit = Math.Min(1, Math.Min(
                    work.Width / display.Scale / (manualWidth * canvas),
                    availablePanelHeightDip / (manualHeight * canvas)));
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
        _adjustingManageControls = false;
    }

    private OrganizerDefinition? CaptureManageDraft()
    {
        if (_editing is null) return null;
        if (!string.IsNullOrWhiteSpace(ManageNameBox.Text)) _editing.Name = ManageNameBox.Text.Trim();
        _editing.PlacementMode = (OrganizerPlacementMode)Math.Clamp(ManagePlacementModeCombo.SelectedIndex, 0, 2);
        _editing.ExpandedContentMode = _editing.PlacementMode == OrganizerPlacementMode.Station
            ? OrganizerExpandedContentMode.Icon
            : (OrganizerExpandedContentMode)Math.Clamp(ManageExpandedContentModeCombo.SelectedIndex, 0, 1);
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
        _editing.Layout.Mode = OrganizerLayoutMode.Grid;
        (_editing.Layout.Rows, _editing.Layout.Columns) = (rows, columns);
        _editing.CompactScale = ManageCompactScaleSlider.Value;
        _editing.CanvasScale = ManageCanvasScaleSlider.Value;
        _editing.ItemScale = ManageItemScaleSlider.Value;
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
        await FlushPendingManageChangesAsync();
    }

    internal async Task<bool> FlushPendingManageChangesAsync()
    {
        try
        {
            _stateSaveTimer.Stop();
            if (_runtimeApplyScheduled) ApplyPendingRuntimeChanges(null, EventArgs.Empty);

            Task<bool>? inFlight = _manageSaveTask;
            if (inFlight is not null && !await ObserveManageSaveAsync(inFlight)) return false;
            return _savedManageChangeVersion >= _manageChangeVersion || await TrackManageSaveAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法保存收纳窗设置。", ex);
            ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
            return false;
        }
    }

    private Task<bool> TrackManageSaveAsync()
    {
        Task<bool> saveTask = SaveManageChangesAsync(_manageChangeVersion);
        _manageSaveTask = saveTask;
        return ObserveManageSaveAsync(saveTask);
    }

    private async Task<bool> ObserveManageSaveAsync(Task<bool> saveTask)
    {
        bool saved = await saveTask;
        if (ReferenceEquals(_manageSaveTask, saveTask)) _manageSaveTask = null;
        return saved;
    }

    private async Task<bool> SaveManageChangesAsync(long changeVersion)
    {
        try
        {
            await _host.SaveStateAsync();
            _savedManageChangeVersion = Math.Max(_savedManageChangeVersion, changeVersion);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法保存收纳窗设置。", ex);
            ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
            return false;
        }
    }

    private async void DeleteOrganizerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedId is not Guid id) return;
        OrganizerDefinition definition = _host.State.Organizers.First(item => item.Id == id);
        MainWindow? window = _host.Windows.FirstOrDefault(item => item.OrganizerId == id);
        string storagePath = AppPaths.ResolveStoragePath(definition);
        bool directStorage = !definition.StorageOwnedByApp;
        bool moveFiles = _host.State.GlobalSettings.MoveOrganizerFilesToDesktopOnDelete;
        var dialog = new ContentDialog
        {
            XamlRoot = ConsoleRoot.XamlRoot,
            Title = AppStrings.Format("DeleteTitleFormat", definition.Name),
            Content = !moveFiles
                ? AppStrings.Format("DeleteKeepFilesFormat", storagePath)
                : directStorage
                ? window?.FileCount > 0
                    ? AppStrings.Format("DeleteDirectNonEmptyFormat", storagePath, AppStrings.FormatItemCount(window.FileCount), definition.Name)
                    : AppStrings.Format("DeleteDirectEmptyFormat", storagePath)
                : window?.FileCount > 0
                    ? AppStrings.Format("DeleteNonEmptyFormat", AppStrings.FormatItemCount(window.FileCount), definition.Name)
                    : AppStrings.Get("DeleteEmpty"),
            PrimaryButtonText = AppStrings.Get(moveFiles ? "ExportDelete" : "DeleteOrganizerOnly"),
            CloseButtonText = AppStrings.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        TransferOutcome outcome = await _host.DeleteOrganizerAsync(id);
        if (outcome.Status is not (TransferStatus.Moved or TransferStatus.Retained))
            ShowError(AppStrings.Get("DeleteErrorTitle"), outcome.Message);
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
        _errorInfoBarTimer.Stop();
        ConsoleInfoBar.Title = title;
        ConsoleInfoBar.Message = message;
        ConsoleInfoBar.Severity = InfoBarSeverity.Error;
        ConsoleInfoBar.IsOpen = true;
        _errorInfoBarTimer.Start();
    }

    private OrganizerVisualChange GetVisualChange(object sender)
    {
        if (ReferenceEquals(sender, ManageNameBox)) return OrganizerVisualChange.Name;
        if (ReferenceEquals(sender, ManagePlacementModeCombo)) return OrganizerVisualChange.PlacementMode | OrganizerVisualChange.CompactScale | OrganizerVisualChange.Docking | OrganizerVisualChange.ExpandedContentMode;
        if (ReferenceEquals(sender, ManageExpandedContentModeCombo)) return OrganizerVisualChange.ExpandedContentMode;
        if (ReferenceEquals(sender, ManageDisplayCombo) || ReferenceEquals(sender, ManageDockEdgeCombo)) return OrganizerVisualChange.Docking;
        if (ReferenceEquals(sender, ManageCompactScaleSlider)) return OrganizerVisualChange.CompactScale;
        if (ReferenceEquals(sender, ManageCanvasScaleSlider)) return OrganizerVisualChange.CanvasScale | OrganizerVisualChange.ItemScale;
        if (ReferenceEquals(sender, ManageItemScaleSlider)) return OrganizerVisualChange.ItemScale;
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

    private static OrganizerDefinition Clone(OrganizerDefinition source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        CreatedAtUtc = source.CreatedAtUtc,
        PlacementMode = source.PlacementMode,
        DockEdge = source.DockEdge,
        Layout = new OrganizerLayout { Mode = source.Layout.Mode, Rows = source.Layout.Rows, Columns = source.Layout.Columns },
        CompactScale = source.CompactScale,
        CanvasScale = source.CanvasScale,
        ItemScale = source.ItemScale,
        NameScale = source.NameScale,
        CompactListItemScale = source.CompactListItemScale,
        ExpandedContentMode = source.ExpandedContentMode,
        CompactListCanvasWidthDip = source.CompactListCanvasWidthDip,
        CompactListCanvasHeightDip = source.CompactListCanvasHeightDip,
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
