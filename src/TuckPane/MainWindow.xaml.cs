using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.VisualBasic.FileIO;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using WinUIEx;
using WinRT.Interop;

namespace TuckPane;

public sealed partial class MainWindow : Window
{
    private const double CompactWidthDip = OrganizerLimits.CompactWindowWidthDip;
    private const double CompactHeightDip = OrganizerLimits.CompactWindowHeightDip;
    private const int CompactPreviewRows = 2;
    private const int CompactPreviewColumns = 2;
    private const int CompactPreviewItemCount = CompactPreviewRows * CompactPreviewColumns;
    private const double CompactCornerRadiusDip = 8;
    private const double ExpandedCornerRadiusDip = 36;
    private const double TransitionResponseSeconds = .30;
    private const double StationTransitionResponseSeconds = .24;
    private const int ReducedMotionDurationMs = 120;
    private const int LongPressMs = 350;
    private const double LongPressMoveLimitDip = 8;
    private const int NoteDragShellCompletionGraceMs = 750;
    private const double CanvasResizeBorderDip = 28;
    private const double ItemGapDip = DisplayPlacementService.ItemGapDip;
    private const double CompactListItemHeightDip = 36;
    private const double CompactListIconSizeDip = 20;
    private const double CompactListFontSizeDip = 14;
    private static readonly long OleMouseMoveMinimumTicks = Math.Max(1, Stopwatch.Frequency / 120);

    private readonly AppHost _host;
    private readonly StorageService _storage;
    private readonly IconCacheService _iconCache = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _longPressTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _externalHoverTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _desktopRepairTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _stationPointerTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _watcherDebounceTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _interactionSaveTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _canvasResizeInputTimer;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly LongPressGesture _longPressGesture = new(LongPressMoveLimitDip);
    private readonly UniformGridLayout _gridLayout = new() { Orientation = Orientation.Horizontal };
    private readonly SolidColorBrush _transparentItemBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _hoveredItemBrush = new(ColorHelper.FromArgb(20, 24, 38, 56));
    private readonly SolidColorBrush _pressedItemBrush = new(ColorHelper.FromArgb(36, 24, 38, 56));
    private readonly SolidColorBrush _draggedItemBrush = new(ColorHelper.FromArgb(28, 24, 38, 56));
    private readonly SolidColorBrush _collapseHoverBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _collapsePressedBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _folderFallbackBrush = new(ColorHelper.FromArgb(255, 106, 210, 255));
    private readonly SolidColorBrush _fileFallbackBrush = new(ColorHelper.FromArgb(255, 236, 239, 245));
    private readonly BitmapImage _noteIcon = new(new Uri("ms-appx:///Assets/Note.png"))
    {
        DecodePixelWidth = 128
    };
    private readonly BitmapImage _todoIcon = new(new Uri("ms-appx:///Assets/Todo.png"))
    {
        DecodePixelWidth = 128
    };
    private readonly NativeMethods.SubclassProc _gestureWindowProc;
    private readonly NativeMethods.WindowProc _canvasResizeWindowProc;
    private readonly ThemeSurface _compactSurface;
    private readonly ThemeSurface _expandedSurface;
    private static readonly UIntPtr GestureSubclassId = new(0x47464452UL);

    private OrganizerDefinition _definition;
    private readonly ObservableCollection<WidgetItem> _items = [];
    private IntPtr _hwnd;
    private AppWindow? _appWindow;
    private bool _hostWindowInitialized;
    private readonly List<IntPtr> _canvasResizeEdgeWindows = [];
    private readonly Dictionary<IntPtr, IntPtr> _canvasResizeOriginalWindowProcs = [];
    private DesktopLayerService? _desktopLayer;
    private OutsideClickHook? _outsideClickHook;
    private FileSystemWatcher? _watcher;
    private NativeMethods.RECT _compactBounds;
    private NativeMethods.RECT _containedAnchorBounds;
    private DisplayInfo? _stationDisplay;
    private bool _expanded;
    private bool _animating;
    private bool _closing;
    private bool _stationVisible = true;
    private bool _runtimeVisible = true;
    private bool _stationTransitionPending;
    private long _stationHotSince;
    private long _stationOutsideSince;
    private long _ordinaryOutsideSince;
    private int _overlayOpenCount;
    private CancellationTokenSource? _transitionCancellation;
    private RectangleClip? _compactClip;
    private RectangleClip? _expandedClip;
    private RectangleClip? _stationTransitionClip;
    private Visual? _expandedCompositionVisual;
    private int _compactPreviewVersion;
    private double _transitionProgress;
    private double _transitionVelocity;
    private Vector2 _transitionAnchorDip;
    private Vector2 _transitionStartScale = Vector2.One;
    private Vector3 _transitionCompactTranslation;
    private CollapseTransitionGeometry? _collapseTransitionGeometry;
    private int _lastTransitionWindowLeft = int.MinValue;
    private int _lastTransitionWindowTop = int.MinValue;

    private uint _pressedPointerId;
    private Point _pressPointDip;
    private NativeMethods.POINT _pressCursorPx;
    private NativeMethods.RECT _pressWindowBounds;
    private NativeMethods.RECT _positionedDragOriginBounds;
    private NativeMethods.RECT _dragCurrentBounds;
    private bool _pressActive;
    private bool _widgetDragging;
    private bool _widgetDragTopmost;
    private bool _draggingExpanded;
    private bool _nativeMouseCapture;
    private bool _dragRenderingSubscribed;
    private bool _dragClockBoosted;
    private DisplayInfo? _dragDisplay;
    private WindowAlignmentInsets? _dragAlignmentInsets;
    private WindowAlignmentState _windowAlignmentState;
    private WindowAlignmentGuideOverlay? _windowAlignmentGuide;
    private long _dragStartedAt;
    private long _pressStartedAt;
    private int _dragInputCount;
    private int _dragCommitCount;
    private int _dragRenderTickCount;
    private bool _hasLastDragCursor;
    private NativeMethods.POINT _lastDragCursor;
    private CanvasResizeSession? _canvasResize;
    private bool _canvasResizeCommitQueued;
    private bool _hasPendingCanvasResizeCursor;
    private NativeMethods.POINT _pendingCanvasResizeCursor;
    private int _wheelDeltaRemainder;
    private bool _canvasResizeProbeRunning;
    private bool _canvasResizeLeftButtonDown;
    private bool _shellContextMenuOpen;
    private bool _contextMenuActivated;
    private bool _contextMenuCounted;
    private NativeMethods.POINT _contextMenuScreenPoint;
    private bool _hoverExpandScrollToEnd;

    private string? _draggedRelativeName;
    private bool _shellDragActive;
    private ItemReorderSession? _itemReorderSession;
    private Border? _itemDragHost;
    private uint _itemDragPointerId;
    private PointerPoint? _itemDragLastPointerPoint;
    private PointerDeviceType _itemDragPointerType;
    private readonly NativeMethods.HookProc _itemDragBoundaryHookProc;
    private IntPtr _itemDragBoundaryHook;
    private Thread? _itemDragBoundaryHookThread;
    private readonly ManualResetEventSlim _itemDragBoundaryHookReady = new(false);
    private uint _itemDragBoundaryHookThreadId;
    private int _itemDragBoundaryArmed;
    private int _itemDragBoundaryPromotionPosted;
    private long _itemDragBoundaryDetectedAt;
    private NativeMethods.RECT _itemDragBoundaryBounds;
    private int _itemDragOleMouseMovePending;
    private long _itemDragLastOleMouseMoveForwardedAt;
    private bool _itemDragRenderingSubscribed;
    private bool _itemDragLanding;
    private bool _itemCollectionMoveInProgress;
    private bool _shellPromotionPending;
    private bool _shellDropFinalizing;
    private bool _internalOleDropAccepted;
    private Guid? _noteDragPreparationId;
    private Task<(string Path, bool RestoreWindow, IStorageItem StorageItem)>? _noteDragPreparationTask;
    private Task _noteDragCleanupTask = Task.CompletedTask;
    private long _shellPromotionRequestedAt;
    private long _itemDragPressedAt;
    private double _nativeDragCellWidth;
    private double _nativeDragCellHeight;
    private bool _nativeItemMotionRenderingSubscribed;
    private long _nativeItemMotionRenderingStartedAt;
    private bool _itemReorderProbeRunning;
    private readonly bool _itemDragTraceEnabled = Environment.GetEnvironmentVariable("GLASSFOLDER_TEST_ITEM_DRAG_TRACE") == "1";
    private bool _catalogRefreshPending;
    private bool _catalogRefreshIconsPending;
    private bool _catalogNotifyUnsupportedPending;
    private Task? _catalogRefreshTask;
    private long _itemDragLastFrame;
    private bool _itemReorderUsesAnimations;
    private TaskCompletionSource? _itemMotionSettled;
    private int[]? _gapTransitionFrom;
    private int[]? _gapTransitionTo;
    private long _gapTransitionStartedAt;
    private int _gapTransitionRevision;
    private int _gapMaterializedRevision;
    private bool _gapTransitionCompleting;
    private TaskCompletionSource? _gapTransitionSettled;
    private readonly RealizedItemRegistry<Border> _realizedItemHosts = new();
    private readonly HashSet<string> _itemDragIdentityWarnings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Border, ItemMotionState> _itemMotionStates = new(ReferenceEqualityComparer.Instance);
    private int _itemElementsPrepared;
    private int _itemElementsCleared;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _itemTouchHoldTimer;
    private HashSet<string> _lastUnsupported = new(StringComparer.OrdinalIgnoreCase);
    private double _appliedCompactScale = 1;
    private double _positionedCompactWidthDip;
    private double _positionedCompactHeightDip;

    private sealed class ItemMotionState
    {
        internal Vector3 Translation;
        internal Vector3 TranslationTarget;
        internal Vector3 TranslationVelocity;
        internal Vector3 Scale = Vector3.One;
        internal Vector3 ScaleTarget = Vector3.One;
        internal Vector3 ScaleVelocity;
    }

    private sealed record CanvasResizeSession(
        CanvasResizeEdge Edge,
        NativeMethods.POINT StartCursor,
        NativeMethods.RECT StartBounds,
        bool CompactList,
        double StartCanvasScale,
        double BaseWidthDip,
        double BaseHeightDip,
        double MinimumCanvasScale,
        double MaximumCanvasScale,
        double DisplayScale,
        NativeMethods.RECT WorkArea);

    public MainWindow(AppHost host, OrganizerDefinition definition)
    {
        _host = host;
        _definition = definition;
        _storage = new StorageService(AppPaths.ResolveStoragePath(definition), createIfMissing: false);
        _gestureWindowProc = GestureWindowProc;
        _canvasResizeWindowProc = CanvasResizeWindowProc;
        _itemDragBoundaryHookProc = ItemDragBoundaryHookProc;
        InitializeComponent();
        ApplyExpandedContentInset();
        ItemsRepeater.ItemsSource = _items;
        _compactSurface = new ThemeSurface(CompactSurfaceHost);
        _expandedSurface = new ThemeSurface(ExpandedSurfaceHost);
        CompactThumbnailHost.SizeChanged += (_, _) => UpdateSurfaceClips();
        ExpandedPanel.SizeChanged += (_, _) => UpdateSurfaceClips();
        ExpandedView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ExpandedView_PointerPressed), handledEventsToo: true);
        ExpandedView.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ExpandedView_PointerMoved), handledEventsToo: true);
        ExpandedView.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ExpandedView_PointerReleased), handledEventsToo: true);
        ExpandedView.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(ExpandedView_PointerCanceled), handledEventsToo: true);
        ExpandedView.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ExpandedView_PointerCaptureLost), handledEventsToo: true);
        ExpandedView.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(ExpandedView_PointerWheelChanged), handledEventsToo: true);
        WindowRoot.PointerEntered += WindowRoot_PointerEntered;
        WindowRoot.PointerExited += WindowRoot_PointerExited;
        Title = "TuckPane";
        SystemBackdrop = new TransparentTintBackdrop(Colors.Transparent);
        _host.ThemeChanged += Host_ThemeChanged;
        ApplyTheme();
        ApplyLanguage();

        _longPressTimer = DispatcherQueue.CreateTimer();
        _longPressTimer.Interval = TimeSpan.FromMilliseconds(LongPressMs);
        _longPressTimer.IsRepeating = false;
        _longPressTimer.Tick += LongPressTimer_Tick;

        _externalHoverTimer = DispatcherQueue.CreateTimer();
        _externalHoverTimer.Interval = TimeSpan.FromMilliseconds(_host.State.GlobalSettings.HoverExpandDelayMs);
        _externalHoverTimer.IsRepeating = false;
        _externalHoverTimer.Tick += ExternalHoverTimer_Tick;

        _desktopRepairTimer = DispatcherQueue.CreateTimer();
        _desktopRepairTimer.Interval = TimeSpan.FromMilliseconds(
            _host.State.GlobalSettings.PerformanceTuning.DesktopRepairMilliseconds);
        _desktopRepairTimer.IsRepeating = true;
        _desktopRepairTimer.Tick += DesktopRepairTimer_Tick;

        _stationPointerTimer = DispatcherQueue.CreateTimer();
        _stationPointerTimer.Interval = TimeSpan.FromMilliseconds(
            _host.State.GlobalSettings.PerformanceTuning.PointerPollMilliseconds);
        _stationPointerTimer.IsRepeating = true;
        _stationPointerTimer.Tick += StationPointerTimer_Tick;

        _watcherDebounceTimer = DispatcherQueue.CreateTimer();
        _watcherDebounceTimer.Interval = TimeSpan.FromMilliseconds(250);
        _watcherDebounceTimer.IsRepeating = false;
        _watcherDebounceTimer.Tick += WatcherDebounceTimer_Tick;

        _interactionSaveTimer = DispatcherQueue.CreateTimer();
        _interactionSaveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _interactionSaveTimer.IsRepeating = false;
        _interactionSaveTimer.Tick += async (_, _) => await SaveStateAsync();

        _canvasResizeInputTimer = DispatcherQueue.CreateTimer();
        _canvasResizeInputTimer.Interval = TimeSpan.FromMilliseconds(16);
        _canvasResizeInputTimer.IsRepeating = true;
        _canvasResizeInputTimer.Tick += (_, _) => PollCanvasResizeInput();

        _itemTouchHoldTimer = DispatcherQueue.CreateTimer();
        _itemTouchHoldTimer.Interval = TimeSpan.FromMilliseconds(LongPressMs);
        _itemTouchHoldTimer.IsRepeating = false;
        _itemTouchHoldTimer.Tick += ItemTouchHoldTimer_Tick;

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
    }

    public void ApplyLanguage()
    {
        LocalizeContextMenu(CompactTileContextMenu);
        LocalizeContextMenu(ExpandedViewContextMenu);
        UpdateContentModeMenuItems();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CollapseButton, AppStrings.Get("Collapse"));
    }

    private void LocalizeContextMenu(MenuFlyout flyout)
    {
        FontFamily family = new(AppStrings.FontFamily);
        bool station = _definition.PlacementMode == OrganizerPlacementMode.Station;
        bool expandedMenu = ReferenceEquals(flyout, ExpandedViewContextMenu);
        foreach (MenuFlyoutItem item in flyout.Items.OfType<MenuFlyoutItem>())
        {
            if (item.Tag is not string key) continue;
            item.Text = AppStrings.Get(key);
            item.FontFamily = family;
            item.CharacterSpacing = AppStrings.CharacterSpacing;
            item.Visibility = key switch
            {
                "ContextNewNote" or "ContextNewTodo" or "ContextPaste" or "ContextNewFolder" => expandedMenu ? Visibility.Visible : Visibility.Collapsed,
                "ContextDuplicate" or "ContextSwitchMode" or "ContextRename" => station ? Visibility.Collapsed : Visibility.Visible,
                _ => Visibility.Visible
            };
        }
        StationActionSeparator.Visibility = expandedMenu ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateContentModeMenuItems()
    {
        Visibility visibility = _definition.PlacementMode == OrganizerPlacementMode.Station
            ? Visibility.Collapsed
            : Visibility.Visible;
        string targetMode = AppStrings.Get(_definition.ExpandedContentMode == OrganizerExpandedContentMode.Icon
            ? "ExpandedContentModeCompactList"
            : "ExpandedContentModeIcon");
        string text = AppStrings.Format("ContextSwitchContentFormat", targetMode);
        FontFamily family = new(AppStrings.FontFamily);
        foreach (MenuFlyoutItem item in new[] { CompactToggleContentModeMenuItem, ExpandedToggleContentModeMenuItem })
        {
            item.Visibility = visibility;
            item.Text = text;
            item.FontFamily = family;
            item.CharacterSpacing = AppStrings.CharacterSpacing;
        }
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        try
        {
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("初始化失败。", ex);
            _host.Notify(AppStrings.Get("AppStartupErrorTitle"), ex.Message, warning: true);
        }
    }

    internal void InitializeHostWindow()
    {
        if (_hostWindowInitialized) return;
        _hostWindowInitialized = true;
        _hwnd = WindowNative.GetWindowHandle(this);
        _outsideClickHook = new OutsideClickHook(_hwnd, DispatcherQueue, () => _ = CollapseAsync());
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        if (_definition.PlacementMode == OrganizerPlacementMode.Station) _appWindow.Hide();
        IntPtr desktopIconView = DesktopLayerService.FindDesktopIconView();
        IntPtr initialOwner = desktopIconView != IntPtr.Zero ? desktopIconView : _host.Console.Hwnd;
        _ = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, initialOwner);
    }

    private async Task InitializeAsync()
    {
        AppPaths.EnsureCreated();
        InitializeHostWindow();
        if (NativeMethods.SupportsWindows11DwmAttributes)
        {
            int useHostBackdrop = 1;
            _ = NativeMethods.DwmSetWindowAttribute(
                _hwnd,
                NativeMethods.DWMWA_USE_HOSTBACKDROPBRUSH,
                ref useHostBackdrop,
                sizeof(int));
        }
        _ = NativeMethods.SetWindowSubclass(_hwnd, _gestureWindowProc, GestureSubclassId, IntPtr.Zero);

        await WaitForLoadedAsync();

        UpdateOrganizerName();
        ApplyCompactScale();

        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            DisplayInfo stationDisplay = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
            _stationDisplay = stationDisplay;
            bool stationScalesNormalized = NormalizeVisualScales(stationDisplay);
            _compactBounds = DisplayPlacementService.CalculateStationAnchor(stationDisplay, _definition.DockEdge, _definition.Position);
            WidgetPosition stationPosition = DisplayPlacementService.Capture(_compactBounds);
            bool correctedDisplay = !PositionsEqual(_definition.Position, stationPosition);
            _definition.Position = stationPosition;
            _desktopLayer = new DesktopLayerService(_hwnd, IntPtr.Zero);
            _appWindow?.Hide();
            CompactView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Collapsed;
            if (correctedDisplay || stationScalesNormalized) await SaveStateAsync();
            RefreshPerformanceSettings();
            if (_storage.Exists) StartWatcher();
            await RefreshCatalogAsync(notifyUnsupported: true, refreshIcons: true);
            return;
        }

        int initialWidth = DipToPx(GetCompactWidthDip(), 1);
        int initialHeight = DipToPx(GetCompactHeightDip(), 1);
        NativeMethods.RECT firstPass = DisplayPlacementService.Restore(_definition.Position, initialWidth, initialHeight);
        ApplyBounds(firstPass, show: !IsContained);
        DisplayInfo display = DisplayPlacementService.ForBounds(firstPass);
        double windowScale = Math.Max(1, WindowRoot.XamlRoot.RasterizationScale);
        if (display.Monitor.Left == 0 && display.Monitor.Top == 0)
        {
            windowScale = Math.Max(windowScale, NativeMethods.GetDpiForSystem() / 96d);
        }
        display = display with { Scale = Math.Max(display.Scale, windowScale) };
        bool normalizedVisualScales = NormalizeVisualScales(display);
        int compactWidth = DipToPx(GetCompactWidthDip(), display.Scale);
        int compactHeight = DipToPx(GetCompactHeightDip(), display.Scale);
        bool restoreAlignmentFrame = _definition.PlacementMode == OrganizerPlacementMode.Floating &&
            (_host.State.GlobalSettings.WindowAlignmentEnabled ||
             _definition.Position is { } savedPosition &&
             (savedPosition.XDip < 0 || savedPosition.YDip < 0 ||
              savedPosition.XDip * display.Scale + compactWidth > display.Work.Width ||
              savedPosition.YDip * display.Scale + compactHeight > display.Work.Height));
        _compactBounds = DisplayPlacementService.Restore(
            _definition.Position,
            compactWidth,
            compactHeight);
        ApplyBounds(_compactBounds, show: !IsContained);
        WindowRoot.UpdateLayout();
        if (restoreAlignmentFrame &&
            TryGetCompactAlignmentInsets(_compactBounds, out WindowAlignmentInsets restoreInsets))
        {
            _compactBounds = DisplayPlacementService.Restore(
                _definition.Position,
                compactWidth,
                compactHeight,
                restoreInsets);
        }
        if (!IsContained && _definition.PlacementMode == OrganizerPlacementMode.Positioned &&
            _host.FindCurrentPositionedPlacement(_definition.Id, _compactBounds) is { } positionedPlacement)
        {
            MoveToPositionedPlacement(positionedPlacement.Bounds, positionedPlacement.CompactScale);
            _definition.Position = DisplayPlacementService.Capture(_compactBounds);
            normalizedVisualScales = true;
        }
        AppLogger.Info($"初始化 DPI={display.Scale:0.##}，收起窗口={_compactBounds.Width}x{_compactBounds.Height}px。");
        ApplyBounds(_compactBounds, show: !IsContained);
        if (normalizedVisualScales) await SaveStateAsync();

        _desktopLayer = new DesktopLayerService(_hwnd, IntPtr.Zero);
        _runtimeVisible = !IsContained;
        ApplyBounds(_compactBounds, show: !IsContained);
        if (!_uiSettings.AdvancedEffectsEnabled)
        {
            _host.NotifyTransparencyFallback();
        }
        RefreshPerformanceSettings();
        if (_storage.Exists) StartWatcher();
        await RefreshCatalogAsync(notifyUnsupported: true, refreshIcons: true);
        if (!IsContained) WindowRoot.Focus(FocusState.Programmatic);
        if (Environment.GetEnvironmentVariable("GLASSFOLDER_TEST_EXPANDED") == "1")
        {
            await ExpandAsync();
        }
        if (Environment.GetEnvironmentVariable("TUCKPANE_TEST_RESIZE_AUTORUN") == "1")
        {
            await RunCanvasResizeProbeAsync();
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("GLASSFOLDER_TEST_ITEM_REORDER_CYCLES"), out int reorderCycles))
        {
            if (!_expanded) await ExpandAsync();
            await RunItemReorderProbeAsync(Math.Clamp(reorderCycles, 0, 30));
        }
        if (Environment.GetEnvironmentVariable("GLASSFOLDER_TEST_ITEM_REORDER_MATRIX") == "1")
        {
            if (!_expanded) await ExpandAsync();
            await RunItemReorderMatrixProbeAsync();
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("GLASSFOLDER_TEST_TRANSITION_CYCLES"), out int testCycles))
        {
            for (int cycle = 0; cycle < Math.Clamp(testCycles, 0, 60); cycle++)
            {
                if (!_expanded) await ExpandAsync();
                await CollapseAsync();
            }
        }
    }

    private Task WaitForLoadedAsync()
    {
        if (WindowRoot.IsLoaded) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? handler = null;
        handler = (_, _) =>
        {
            WindowRoot.Loaded -= handler;
            completion.TrySetResult();
        };
        WindowRoot.Loaded += handler;
        return completion.Task;
    }

    private async Task RunItemReorderProbeAsync(int cycles)
    {
        _itemReorderProbeRunning = true;
        try
        {
            for (int cycle = 0; cycle < cycles && _items.Count > 1; cycle++)
            {
                ConfigureItemsLayout();
                Border? host = null;
                for (int attempt = 0; attempt < 12 && host is null; attempt++)
                {
                    ItemsRepeater.UpdateLayout();
                    host = ItemsRepeater.TryGetElement(0) as Border ?? ItemsRepeater.GetOrCreateElement(0) as Border;
                    if (host is null) await WaitForNextRenderAsync(CancellationToken.None);
                }
                if (host?.DataContext is not WidgetItem item)
                    throw new InvalidOperationException("The reorder verification item was not realized.");

                double width = Math.Max(1, host.Width);
                double height = Math.Max(1, host.Height);
                Point press = new(width / 2, height / 2);
                var session = new ItemReorderSession(item.RelativeName, 0, press, press);
                session.Activate(press);
                _itemReorderSession = session;
                _itemDragHost = host;
                _itemDragPointerType = PointerDeviceType.Mouse;
                StartItemReorder();
                session.UpdateTarget(
                    new Point(press.X + width + GetItemLayoutGapDip(), press.Y),
                    width,
                    height,
                    GetItemLayoutGapDip(),
                    GetItemLayoutColumnCount(),
                    _items.Count);
                session.TryBeginPreviewTransition(_items.Count, out _, out _);
                ApplyAllProvisionalItemVisuals(animate: true);
                await CommitItemReorderAsync(session);
                AppLogger.Info($"内部换序探针完成 {cycle + 1}/{cycles}。");
            }
        }
        finally
        {
            _itemReorderProbeRunning = false;
        }
    }

    private async Task RunItemReorderMatrixProbeAsync()
    {
        int rows = Math.Max(1, _definition.Layout.Rows);
        int columns = GetItemLayoutColumnCount();
        int expectedCount = rows * columns;
        if (_items.Count != expectedCount)
            throw new InvalidOperationException($"The {columns}x{rows} reorder matrix requires exactly {expectedCount} items.");

        _itemReorderProbeRunning = true;
        try
        {
            ConfigureItemsLayout();
            ItemsRepeater.UpdateLayout();
            await WaitForNextRenderAsync(CancellationToken.None);
            var slotOrigins = new Point[expectedCount];
            for (int index = 0; index < expectedCount; index++)
            {
                if (ItemsRepeater.TryGetElement(index) is not Border host)
                    throw new InvalidOperationException($"The {columns}x{rows} reorder element {index} was not realized.");
                slotOrigins[index] = host.TransformToVisual(ItemsRepeater).TransformPoint(new Point());
            }
            for (int source = 0; source < expectedCount; source++)
            {
                for (int target = 0; target < expectedCount; target++)
                {
                    if (source == target) continue;
                    if (ItemsRepeater.TryGetElement(source) is not Border sourceHost || sourceHost.DataContext is not WidgetItem item ||
                        ItemsRepeater.TryGetElement(target) is not Border targetHost)
                    {
                        throw new InvalidOperationException($"The {columns}x{rows} reorder element {source}->{target} was not realized.");
                    }

                    Point sourceOrigin = sourceHost.TransformToVisual(ItemsRepeater).TransformPoint(new Point());
                    Point targetOrigin = targetHost.TransformToVisual(ItemsRepeater).TransformPoint(new Point());
                    Point press = new(sourceOrigin.X + sourceHost.Width / 2, sourceOrigin.Y + sourceHost.Height / 2);
                    Point destination = new(targetOrigin.X + targetHost.Width / 2, targetOrigin.Y + targetHost.Height / 2);
                    var session = new ItemReorderSession(item.RelativeName, source, press, new Point(sourceHost.Width / 2, sourceHost.Height / 2));
                    session.Activate(press);
                    _itemReorderSession = session;
                    _itemDragHost = sourceHost;
                    session.UpdateTarget(destination, sourceHost.Width, sourceHost.Height, GetItemLayoutGapDip(), columns, _items.Count);
                    if (session.TargetIndex != target)
                        throw new InvalidOperationException($"The {columns}x{rows} target resolved as {session.TargetIndex}, expected {target}.");

                    session.TryBeginPreviewTransition(_items.Count, out _, out _);
                    ApplyAllProvisionalItemVisuals(animate: false);
                    int[] mapping = session.GetVisualIndices(_items.Count);
                    if (mapping.Distinct().Count() != _items.Count)
                        throw new InvalidOperationException($"The {columns}x{rows} mapping {source}->{target} contains duplicate slots.");
                    for (int index = 0; index < _items.Count; index++)
                    {
                        if (index == source || ItemsRepeater.TryGetElement(index) is not Border host) continue;
                        int visualIndex = mapping[index];
                        Vector2 expected = new(
                            (float)(slotOrigins[visualIndex].X - slotOrigins[index].X),
                            (float)(slotOrigins[visualIndex].Y - slotOrigins[index].Y));
                        Vector3 actual = GetItemMotion(host).TranslationTarget;
                        if (Vector2.DistanceSquared(expected, new Vector2(actual.X, actual.Y)) > 1f)
                        {
                            throw new InvalidOperationException(
                                $"The {columns}x{rows} visual target {source}->{target} is incorrect for item {index}: " +
                                $"expected ({expected.X:0.##},{expected.Y:0.##}), actual ({actual.X:0.##},{actual.Y:0.##}).");
                        }
                    }
                    CancelItemReorder(runPendingRefresh: false);
                }
            }
            AppLogger.Info($"满{columns}x{rows}换序矩阵完成{expectedCount * (expectedCount - 1)}/{expectedCount * (expectedCount - 1)}。");
        }
        finally
        {
            CancelItemReorder(runPendingRefresh: false);
            _itemReorderProbeRunning = false;
        }
    }

    private void StartWatcher()
    {
        if (!_storage.Exists || _watcher is not null) return;
        _watcher = new FileSystemWatcher(_storage.ItemsRoot)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += Watcher_Changed;
        _watcher.Created += Watcher_Changed;
        _watcher.Deleted += Watcher_Changed;
        _watcher.Renamed += Watcher_Changed;
    }

    private void Watcher_Changed(object sender, FileSystemEventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            _watcherDebounceTimer.Stop();
            _watcherDebounceTimer.Start();
        });
    }

    private async void WatcherDebounceTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        try
        {
            await RefreshCatalogAsync(notifyUnsupported: true, refreshIcons: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法刷新收纳目录：{_storage.ItemsRoot}", ex);
            if (!_closing)
                ShowMessage(AppStrings.Format("DirectoryReadErrorFormat", Path.GetFileName(_storage.ItemsRoot), ex.Message), InfoBarSeverity.Warning);
        }
    }

    private Task RefreshCatalogAsync(bool notifyUnsupported, bool refreshIcons = false)
    {
        _catalogRefreshPending = true;
        _catalogNotifyUnsupportedPending |= notifyUnsupported;
        _catalogRefreshIconsPending |= refreshIcons;
        return StartCatalogRefreshIfReady();
    }

    private bool IsCatalogRefreshBlocked() =>
        _itemReorderSession is not null || _shellDragActive || _shellPromotionPending ||
        _shellDropFinalizing || _itemDragLanding || _itemCollectionMoveInProgress ||
        _nativeItemMotionRenderingSubscribed;

    private Task StartCatalogRefreshIfReady()
    {
        if (_closing || IsCatalogRefreshBlocked()) return Task.CompletedTask;
        if (_catalogRefreshTask is { IsCompleted: false }) return _catalogRefreshTask;
        _catalogRefreshTask = FlushCatalogRefreshesAsync();
        return _catalogRefreshTask;
    }

    private async Task FlushCatalogRefreshesAsync()
    {
        while (_catalogRefreshPending && !_closing && !IsCatalogRefreshBlocked())
        {
            bool notifyUnsupported = _catalogNotifyUnsupportedPending;
            bool refreshIcons = _catalogRefreshIconsPending;
            _catalogRefreshPending = false;
            _catalogNotifyUnsupportedPending = false;
            _catalogRefreshIconsPending = false;
            await RefreshCatalogCoreAsync(notifyUnsupported, refreshIcons);
        }
    }

    private async Task RefreshCatalogCoreAsync(bool notifyUnsupported, bool refreshIcons)
    {
        IReadOnlyList<WidgetItem> diskItems = _storage.ReadItems();
        IEnumerable<WidgetItem> containedOrganizers = _definition.PlacementMode == OrganizerPlacementMode.Station
            ? _host.GetContainedOrganizerItems(_definition.Id)
            : Array.Empty<WidgetItem>();
        var byName = diskItems
            .Concat(_definition.Notes.Select(OrganizerNoteRules.CreateItem))
            .Concat(containedOrganizers)
            .ToDictionary(item => item.RelativeName, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<WidgetItem>();
        foreach (string relativeName in _definition.ItemOrder)
        {
            if (byName.Remove(relativeName, out WidgetItem? item))
            {
                ordered.Add(item);
            }
        }

        ordered.AddRange(byName.Values.OrderBy(item => item.RelativeName, StringComparer.CurrentCultureIgnoreCase));
        bool orderChanged = !_definition.ItemOrder.SequenceEqual(ordered.Select(item => item.RelativeName), StringComparer.OrdinalIgnoreCase);
        ReconcileCatalogItems(ordered);
        _definition.ItemOrder = ordered.Select(item => item.RelativeName).ToList();
        if (orderChanged)
        {
            await SaveStateAsync();
        }

        if (refreshIcons)
        {
            var refreshed = new Dictionary<string, BitmapImage?>(StringComparer.OrdinalIgnoreCase);
            foreach (WidgetItem item in _items.Where(item => !IsDocumentItem(item) && item.Kind != WidgetItemKind.Organizer))
            {
                refreshed[item.RelativeName] = await _iconCache.GetIconAsync(item.FullPath, refresh: true);
            }
            for (int index = 0; index < _items.Count; index++)
            {
                WidgetItem item = _items[index];
                if (TryGetRealizedItemHost(index, out Border host) &&
                    FindItemPart<Image>(host, "ItemImage") is Image image &&
                    string.Equals(image.Tag as string, item.FullPath, StringComparison.OrdinalIgnoreCase) &&
                    refreshed.TryGetValue(item.RelativeName, out BitmapImage? bitmap))
                {
                    image.Source = bitmap;
                }
            }
        }

        RenderAll();
        _host.NotifyOrganizerPreviewChanged(_definition.Id);

        var unsupported = _storage.ReadUnsupportedNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] addedUnsupported = unsupported.Except(_lastUnsupported, StringComparer.OrdinalIgnoreCase).ToArray();
        _lastUnsupported = unsupported;
        if (notifyUnsupported && addedUnsupported.Length > 0)
        {
            string message = AppStrings.Format("UnsupportedItemsFormat", string.Join(", ", addedUnsupported.Take(3)));
            ShowMessage(message, InfoBarSeverity.Warning);
        }
    }

    private void ReconcileCatalogItems(IReadOnlyList<WidgetItem> desired)
    {
        _itemCollectionMoveInProgress = true;
        try
        {
            CatalogCollectionSync.Apply(_items, desired);
        }
        finally
        {
            _itemCollectionMoveInProgress = false;
        }
    }

    private void RenderAll()
    {
        UpdateOrganizerName();
        RenderCompactPreview();
        RenderItems();
    }

    private void UpdateOrganizerName()
    {
        CompactNameText.Text = _definition.Name;
        ExpandedNameText.Text = _definition.Name;
        ExpandedNameText.FontSize = 42 * _host.State.GlobalSettings.ResolveExpandedNameScale(_definition.PlacementMode);
        ExpandedNameText.Visibility = _definition.PlacementMode == OrganizerPlacementMode.Station
            ? Visibility.Collapsed
            : Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExpandedNameText, _definition.Name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CompactTile, _definition.Name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(CompactTile, $"OrganizerCompact-{_definition.Id:N}");
    }

    private void RenderCompactPreview()
    {
        bool storageExists = _storage.Exists;
        CompactWarningBadge.Visibility = storageExists ? Visibility.Collapsed : Visibility.Visible;
        string compactHelp = storageExists ? _definition.Name : AppStrings.Get("MissingStorage");
        ToolTipService.SetToolTip(CompactTile, compactHelp);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(CompactTile, storageExists ? string.Empty : compactHelp);
        UpdateCompactThumbnailMetrics();
        UpdateCompactPreviewItemScale();
        _ = RenderCompactPreviewAsync(++_compactPreviewVersion);
    }

    private async Task RenderCompactPreviewAsync(int version)
    {
        IReadOnlyList<WidgetItem> preview = _items.Take(CompactPreviewItemCount).ToArray();
        var bitmaps = new BitmapImage?[preview.Count];
        for (int index = 0; index < preview.Count; index++)
        {
            bitmaps[index] = IsDocumentItem(preview[index])
                ? GetDocumentIcon(preview[index])
                : await _iconCache.GetIconAsync(preview[index].FullPath);
        }
        if (version != _compactPreviewVersion || _closing) return;

        Image[] images = CompactPreviewGrid.Children.OfType<Image>().ToArray();
        for (int index = 0; index < images.Length; index++)
        {
            images[index].Source = index < bitmaps.Length ? bitmaps[index] : null;
            images[index].Visibility = index < preview.Count ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateCompactPreviewItemScale()
    {
        Grid grid = CompactPreviewGrid;
        grid.Margin = new Thickness(Math.Max(2, 3 * _appliedCompactScale));
        grid.ColumnSpacing = Math.Max(1, _appliedCompactScale);
        grid.RowSpacing = Math.Max(1, _appliedCompactScale);
        double iconSize = CalculateCompactPreviewIconSize(
            grid,
            Math.Max(1, grid.RowDefinitions.Count),
            Math.Max(1, grid.ColumnDefinitions.Count));
        foreach (Image image in grid.Children.OfType<Image>())
        {
            image.Width = iconSize;
            image.Height = iconSize;
        }
    }

    private double CalculateCompactPreviewIconSize(Grid grid, int rows, int columns)
    {
        double width = CompactThumbnailHost.Width - grid.Margin.Left - grid.Margin.Right - grid.ColumnSpacing * Math.Max(0, columns - 1);
        double height = CompactThumbnailHost.Height - grid.Margin.Top - grid.Margin.Bottom - grid.RowSpacing * Math.Max(0, rows - 1);
        double slot = Math.Min(width / columns, height / rows);
        return Math.Max(2, slot * OrganizerLimits.CalculateCompactPreviewIconFraction(_definition.ItemScale));
    }

    private void RenderItems()
    {
        ConfigureItemsLayout();
    }

    private void ConfigureItemsLayout(bool updateItems = true)
    {
        Size viewport = GetItemsViewportSize();
        double width = viewport.Width;
        double height = viewport.Height;
        if (width <= 1 || height <= 1) return;
        ItemsScrollView.HorizontalScrollMode = ScrollingScrollMode.Disabled;
        ItemsScrollView.VerticalScrollMode = ScrollingScrollMode.Enabled;
        ItemsRepeater.Width = double.NaN;
        ItemsRepeater.Height = double.NaN;
        (double cellWidth, double cellHeight) = GetItemCellSizeDip(width, height);
        ItemsRepeater.Width = width;
        _gridLayout.MinItemWidth = cellWidth;
        _gridLayout.MinItemHeight = cellHeight;
        _gridLayout.MinColumnSpacing = IsCompactList ? 0 : ItemGapDip;
        _gridLayout.MinRowSpacing = IsCompactList ? 0 : ItemGapDip;
        _gridLayout.MaximumRowsOrColumns = GetItemLayoutColumnCount();
        if (!ReferenceEquals(ItemsRepeater.Layout, _gridLayout)) ItemsRepeater.Layout = _gridLayout;
        if (updateItems) UpdateRealizedItems();
    }

    private void ApplyExpandedContentInset()
    {
        bool station = _definition.PlacementMode == OrganizerPlacementMode.Station;
        ExpandedTitleRow.Height = new GridLength(
            station ? 0 : DisplayPlacementService.ExpandedTitleBandDip);
        ExpandedNameText.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        double side = DisplayPlacementService.ResolveExpandedSideInset(
            _definition.PlacementMode,
            _definition.ExpandedContentMode);
        ItemsRepeater.Margin = new Thickness(
            side,
            station ? DisplayPlacementService.StationTopInsetDip : DisplayPlacementService.ExpandedTopInsetDip,
            side,
            station ? DisplayPlacementService.StationBottomInsetDip : DisplayPlacementService.ExpandedBottomInsetDip);
    }

    private Size GetItemsViewportSize()
    {
        Thickness margin = ItemsRepeater.Margin;
        return new Size(
            Math.Max(0, ItemsScrollView.ActualWidth - margin.Left - margin.Right),
            Math.Max(0, ItemsScrollView.ActualHeight - margin.Top - margin.Bottom));
    }

    private bool IsCompactList =>
        _definition.PlacementMode != OrganizerPlacementMode.Station &&
        _definition.ExpandedContentMode == OrganizerExpandedContentMode.CompactList;

    private int GetItemLayoutColumnCount() => IsCompactList ? 1 : Math.Max(1, _definition.Layout.Columns);

    private double GetItemLayoutGapDip() => IsCompactList ? 0 : ItemGapDip;

    private (double Width, double Height) GetItemCellSizeDip(double width, double height) => IsCompactList
        ? (width, CompactListItemHeightDip * _definition.CompactListItemScale)
        : DisplayPlacementService.CalculateItemCellSizeDip(width, height, _definition.Layout);

    private void ItemsRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Border host || host.DataContext is not WidgetItem item) return;
        _itemElementsPrepared++;
        _realizedItemHosts.Register(item.RelativeName, host);
        PrepareItemElement(host, item, loadIcon: true);
        ResetItemMotion(host);
        int itemIndex = IndexOfItem(item.RelativeName);
        if (!_itemCollectionMoveInProgress && itemIndex >= 0) ApplyProvisionalItemVisual(host, itemIndex, animate: false);
        if (_shellDragActive && _itemReorderSession is { } session &&
            item.RelativeName.Equals(session.RelativeName, StringComparison.OrdinalIgnoreCase))
        {
            ApplyNativeSourcePlaceholder(host);
        }
    }

    private void ItemsRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not Border host) return;
        _itemElementsCleared++;
        _realizedItemHosts.Unregister(host);
        if (!_itemCollectionMoveInProgress && ReferenceEquals(host, _itemDragHost))
        {
            if (_shellDragActive) _itemDragHost = null;
            else CancelItemReorder();
        }
        _itemMotionStates.Remove(host);
        host.Translation = Vector3.Zero;
        host.Scale = Vector3.One;
        host.Opacity = 1;
        host.Shadow = null;
        host.Background = _transparentItemBrush;
        ResetItemContentTransform(host);
        Canvas.SetZIndex(host, 0);
    }

    private void UpdateRealizedItems()
    {
        for (int index = 0; index < _items.Count; index++)
        {
            if (TryGetRealizedItemHost(index, out Border host) && host.DataContext is WidgetItem item)
            {
                PrepareItemElement(host, item, loadIcon: false);
            }
        }
    }

    private bool TryGetRealizedItemHost(int itemIndex, out Border host)
    {
        if (itemIndex >= 0 && itemIndex < _items.Count &&
            TryGetRealizedItemHost(_items[itemIndex].RelativeName, out host)) return true;

        host = null!;
        return false;
    }

    private bool TryGetRealizedItemHost(string relativeName, out Border host)
    {
        if (_realizedItemHosts.TryGet(relativeName, out Border candidate) &&
            candidate.DataContext is WidgetItem item &&
            item.RelativeName.Equals(relativeName, StringComparison.OrdinalIgnoreCase) &&
            candidate.Tag is string tag &&
            tag.Equals(relativeName, StringComparison.OrdinalIgnoreCase))
        {
            host = candidate;
            return true;
        }

        if (candidate is not null) _realizedItemHosts.Unregister(candidate);
        if (_itemDragTraceEnabled && _itemReorderSession is not null && _itemDragIdentityWarnings.Add(relativeName))
        {
            AppLogger.Error($"换序跟踪：宿主身份解析失败 identity={relativeName} revision={_itemReorderSession.PreviewRevision}。");
        }
        host = null!;
        return false;
    }

    private void PrepareItemElement(Border host, WidgetItem item, bool loadIcon)
    {
        host.Tag = item.RelativeName;
        ToolTipService.SetToolTip(host, IsDocumentItem(item) || item.Kind == WidgetItemKind.Organizer ? item.Name : item.FullPath);
        if (item.NoteId is Guid noteId)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"NoteItem-{noteId:N}");
        }
        else if (item.Kind == WidgetItemKind.PortableNote)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"PortableNoteItem-{item.RelativeName}");
        }
        else if (item.Kind == WidgetItemKind.PortableTodo)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"PortableTodoItem-{item.RelativeName}");
        }
        else if (item is { Kind: WidgetItemKind.Organizer, OrganizerId: Guid organizerId })
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"OrganizerItem-{organizerId:N}");
        }
        else
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            host.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty);
        }
        Size viewport = GetItemsViewportSize();
        SizePreparedElement(host, viewport.Width, viewport.Height);

        if (FindItemPart<Grid>(host, "ItemIconContainer") is Grid iconContainer &&
            FindItemPart<StackPanel>(host, "ItemContent") is StackPanel itemContent &&
            FindItemPart<FontIcon>(host, "ItemFallbackIcon") is FontIcon fallback &&
            FindItemPart<Image>(host, "ItemImage") is Image image &&
            FindItemPart<Grid>(host, "OrganizerPreviewGrid") is Grid organizerPreview &&
            FindItemPart<TextBlock>(host, "ItemNameText") is TextBlock name)
        {
            bool compactList = IsCompactList;
            ConfigureItemInteractionTransitions(itemContent);
            double compactListScale = _definition.CompactListItemScale;
            bool organizerItem = item is { Kind: WidgetItemKind.Organizer, OrganizerId: not null };
            double iconMaximum = _definition.PlacementMode == OrganizerPlacementMode.Station
                ? Math.Max(18, Math.Min(host.Width * .82, host.Height * .68))
                : Math.Max(18, Math.Min(host.Width, host.Height) * .68);
            double iconSize = compactList
                ? CompactListIconSizeDip * compactListScale
                : Math.Clamp(72 * _definition.ItemScale, 18, iconMaximum);
            if (_definition.PlacementMode == OrganizerPlacementMode.Station) iconSize = SnapDip(iconSize);
            iconContainer.Width = iconSize;
            iconContainer.Height = iconSize;
            image.Width = iconSize;
            image.Height = iconSize;
            organizerPreview.Width = iconSize;
            organizerPreview.Height = iconSize;
            if (FindItemPart<FontIcon>(organizerPreview, "OrganizerPreviewEmptyIcon") is FontIcon emptyIcon)
                emptyIcon.FontSize = Math.Max(10, iconSize * .42);
            fallback.FontSize = iconSize * .58;
            fallback.Glyph = item.Kind switch
            {
                WidgetItemKind.Folder => "\uE8B7",
                WidgetItemKind.Note or WidgetItemKind.PortableNote or WidgetItemKind.PortableTodo => "\uE70F",
                WidgetItemKind.Organizer => "\uE8B7",
                _ => "\uE7C3"
            };
            fallback.Foreground = item.Kind == WidgetItemKind.Folder ? _folderFallbackBrush : _fileFallbackBrush;
            host.CornerRadius = new CornerRadius(compactList ? 4 * compactListScale : 16);
            host.Padding = compactList
                ? new Thickness(4 * compactListScale, 0, 4 * compactListScale, 0)
                : new Thickness(Math.Max(2, 5 * _definition.ItemScale));
            itemContent.Orientation = compactList ? Orientation.Horizontal : Orientation.Vertical;
            itemContent.HorizontalAlignment = compactList ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            itemContent.Spacing = compactList ? 8 * compactListScale : Math.Max(2, 6 * _definition.ItemScale);
            name.FontSize = compactList
                ? CompactListFontSizeDip * compactListScale
                : Math.Min(Math.Max(8, 13 * _definition.ItemScale), Math.Max(8, host.Height * .15));
            name.Width = compactList ? Math.Max(24, host.Width - iconSize - 20 * compactListScale) : double.NaN;
            name.MaxWidth = compactList ? name.Width : Math.Max(24, host.Width - 10);
            name.HorizontalAlignment = compactList ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            name.VerticalAlignment = VerticalAlignment.Center;
            name.TextAlignment = compactList ? TextAlignment.Left : TextAlignment.Center;
            name.Foreground = new SolidColorBrush(ThemePalette.ForegroundColor(
                _host.State.GlobalSettings.GetTheme(ThemeTarget.Organizer)));
            name.Visibility = Visibility.Visible;
            fallback.Visibility = organizerItem ? Visibility.Collapsed : Visibility.Visible;
            organizerPreview.Visibility = organizerItem ? Visibility.Visible : Visibility.Collapsed;
            image.Visibility = organizerItem ? Visibility.Collapsed : Visibility.Visible;
            string iconKey = IsDocumentItem(item) || organizerItem ? item.RelativeName : item.FullPath;
            bool itemChanged = !string.Equals(image.Tag as string, iconKey, StringComparison.OrdinalIgnoreCase);
            if (itemChanged)
            {
                image.Tag = iconKey;
                image.ClearValue(Image.SourceProperty);
            }
            if (organizerItem)
            {
                object requestToken = new();
                organizerPreview.Tag = requestToken;
                _ = LoadOrganizerPreviewAsync(organizerPreview, item.OrganizerId!.Value, requestToken);
            }
            else
            {
                organizerPreview.Tag = null;
                if (IsDocumentItem(item)) image.Source = GetDocumentIcon(item);
                else if (loadIcon && (itemChanged || image.Source is null)) _ = LoadIconAsync(image, item.FullPath);
            }
        }
    }

    private void SizePreparedElement(FrameworkElement element, double width, double height)
    {
        (element.Width, element.Height) = GetItemCellSizeDip(width, height);
    }

    private void PrepareReorderedItemIdentity(Border host, WidgetItem item)
    {
        if (item.Kind == WidgetItemKind.Organizer)
        {
            PrepareItemElement(host, item, loadIcon: true);
            return;
        }
        host.Tag = item.RelativeName;
        ToolTipService.SetToolTip(host, IsDocumentItem(item) ? item.Name : item.FullPath);
        if (item.NoteId is Guid noteId)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"NoteItem-{noteId:N}");
        }
        else if (item.Kind == WidgetItemKind.PortableNote)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"PortableNoteItem-{item.RelativeName}");
        }
        else if (item.Kind == WidgetItemKind.PortableTodo)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"PortableTodoItem-{item.RelativeName}");
        }
        else
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            host.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty);
        }
        if (FindItemPart<FontIcon>(host, "ItemFallbackIcon") is FontIcon fallback)
        {
            fallback.Glyph = item.Kind switch
            {
                WidgetItemKind.Folder => "\uE8B7",
                WidgetItemKind.Note or WidgetItemKind.PortableNote or WidgetItemKind.PortableTodo => "\uE70F",
                _ => "\uE7C3"
            };
            fallback.Foreground = item.Kind == WidgetItemKind.Folder ? _folderFallbackBrush : _fileFallbackBrush;
        }
        if (FindItemPart<Image>(host, "ItemImage") is Image image)
        {
            string iconKey = IsDocumentItem(item) ? item.RelativeName : item.FullPath;
            if (!string.Equals(image.Tag as string, iconKey, StringComparison.OrdinalIgnoreCase))
            {
                image.Tag = iconKey;
                image.ClearValue(Image.SourceProperty);
            }
            if (IsDocumentItem(item)) image.Source = GetDocumentIcon(item);
            else _ = LoadIconAsync(image, item.FullPath);
        }
    }

    private static T? FindItemPart<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && match.Name == name) return match;
        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < children; index++)
        {
            if (VisualTreeHelper.GetChild(root, index) is DependencyObject child &&
                FindItemPart<T>(child, name) is { } descendant) return descendant;
        }
        return null;
    }

    private void ConfigureItemInteractionTransitions(StackPanel content)
    {
        TimeSpan duration = UseCustomAnimations ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero;
        if (content.ScaleTransition is not null) content.ScaleTransition.Duration = duration;
        if (content.TranslationTransition is not null) content.TranslationTransition.Duration = duration;
        if (!UseCustomAnimations)
        {
            content.Scale = Vector3.One;
            content.Translation = Vector3.Zero;
        }
    }

    private void SetItemInteractionVisual(Border host, bool pressed)
    {
        host.Background = pressed ? _pressedItemBrush : _hoveredItemBrush;
        if (FindItemPart<StackPanel>(host, "ItemContent") is not StackPanel content) return;
        ConfigureItemInteractionTransitions(content);
        content.CenterPoint = new Vector3((float)(content.ActualWidth / 2), (float)(content.ActualHeight / 2), 0);
        if (!UseCustomAnimations || IsCompactList)
        {
            content.Scale = Vector3.One;
            content.Translation = Vector3.Zero;
            return;
        }
        content.Scale = pressed ? new Vector3(.97f, .97f, 1) : new Vector3(1.02f, 1.02f, 1);
        content.Translation = pressed ? Vector3.Zero : new Vector3(0, -1, 0);
    }

    private void ResetItemInteractionVisual(Border host)
    {
        host.Background = _transparentItemBrush;
        ResetItemContentTransform(host);
    }

    private static void ResetItemContentTransform(Border host)
    {
        if (FindItemPart<StackPanel>(host, "ItemContent") is not StackPanel content) return;
        content.Scale = Vector3.One;
        content.Translation = Vector3.Zero;
    }

    private Border CreateIconHost(WidgetItem item, double iconSize, bool showName)
    {
        var iconContainer = new Grid { Width = iconSize, Height = iconSize };
        var fallback = new FontIcon
        {
            Glyph = item.Kind == WidgetItemKind.Folder ? "\uE8B7" : "\uE71B",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = iconSize * .58,
            Foreground = new SolidColorBrush(item.Kind == WidgetItemKind.Folder ? ColorHelper.FromArgb(255, 106, 210, 255) : ColorHelper.FromArgb(255, 194, 177, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconContainer.Children.Add(fallback);

        var image = new Image
        {
            Tag = item.FullPath,
            Width = iconSize,
            Height = iconSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconContainer.Children.Add(image);
        if (IsDocumentItem(item)) image.Source = GetDocumentIcon(item);
        else _ = LoadIconAsync(image, item.FullPath);

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = showName ? 6 : 0
        };
        stack.Children.Add(iconContainer);
        if (showName)
        {
            stack.Children.Add(new TextBlock
            {
                Text = item.Name,
                MaxWidth = 132,
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Colors.White),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            });
        }

        return new Border { Child = stack };
    }

    private async Task LoadIconAsync(Image image, string path)
    {
        BitmapImage? bitmap = await _iconCache.GetIconAsync(path);
        if (bitmap is not null && image.XamlRoot is not null &&
            string.Equals(image.Tag as string, path, StringComparison.OrdinalIgnoreCase))
        {
            image.Source = bitmap;
        }
    }

    private async Task ExpandAsync(bool scrollToEnd = false)
    {
        if ((_expanded && !_animating) || _hwnd == IntPtr.Zero) return;
        await _host.PrepareToExpandAsync(this);
        bool contained = IsContained;
        NativeMethods.RECT compactOrigin = contained ? _containedAnchorBounds : _compactBounds;
        WidgetPosition? rememberedPosition = ShouldRememberExpandedPosition()
            ? _definition.ExpandedPosition
            : null;
        DisplayInfo targetDisplay = rememberedPosition is not null
            ? DisplayPlacementService.GetDisplay(rememberedPosition.MonitorDevice)
            : (_definition.PlacementMode == OrganizerPlacementMode.Station
            ? DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice)
            : DisplayPlacementService.ForBounds(compactOrigin));
        if (rememberedPosition is null) targetDisplay = targetDisplay with
        {
            Scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d)
        };
        if (NormalizeVisualScales(targetDisplay))
        {
            UpdateRealizedItems();
            RenderCompactPreview();
            await SaveStateAsync();
        }

        _expanded = true;
        _animating = true;
        RefreshPerformanceSettings();
        CancellationTokenSource transition = StartTransition();
        if (_definition.PlacementMode != OrganizerPlacementMode.Station && !contained) _desktopLayer?.SetExpanded(true);

        NativeMethods.RECT currentBounds = compactOrigin;
        if (_definition.PlacementMode != OrganizerPlacementMode.Station && !contained &&
            !NativeMethods.GetWindowRect(_hwnd, out currentBounds))
        {
            currentBounds = compactOrigin;
        }
        if (_definition.PlacementMode != OrganizerPlacementMode.Station && !contained &&
            RectsEqual(currentBounds, _compactBounds)) _compactBounds = currentBounds;
        NativeMethods.RECT expandedBounds = CalculateExpandedBounds(
            compactOrigin,
            rememberedPosition is null ? null : targetDisplay);
        if (rememberedPosition is not null)
        {
            expandedBounds = DisplayPlacementService.RestoreToDisplay(
                rememberedPosition,
                targetDisplay,
                expandedBounds.Width,
                expandedBounds.Height);
            expandedBounds = DisplayPlacementService.Clamp(
                expandedBounds,
                DisplayPlacementService.GetExpandedWorkArea(targetDisplay));
        }

        double initialProgress = Math.Clamp(_transitionProgress, 0, 1);
        bool reducedMotion = !UseCustomAnimations;
        PrepareTransitionAnchor(expandedBounds, compactOrigin);
        _collapseTransitionGeometry = null;
        ExpandedView.Visibility = Visibility.Visible;
        CompactView.Visibility = _definition.PlacementMode == OrganizerPlacementMode.Station || contained
            ? Visibility.Collapsed
            : Visibility.Visible;
        ApplyTransitionFrame(initialProgress, reducedMotion);
        ApplyBounds(expandedBounds, show: true);
        if (_definition.PlacementMode == OrganizerPlacementMode.Station || contained)
        {
            _desktopLayer?.SetExpanded(true, stayTopmost: true);
            if (_definition.PlacementMode == OrganizerPlacementMode.Station)
                _host.RaiseActiveCompactOrganizerDrags(this);
        }
        try
        {
            ConfigureItemsLayout();
            await WaitForNextRenderAsync(transition.Token);
            UpdateSurfaceClips();
            ApplyTransitionFrame(initialProgress, reducedMotion);
            await RunVisualTransitionAsync(1, transition.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (_transitionCancellation == transition && !transition.IsCancellationRequested)
            {
                CompactView.Visibility = Visibility.Collapsed;
                CompactView.Opacity = 1;
                CompactView.Translation = Vector3.Zero;
                ExpandedView.Opacity = 1;
                GetExpandedCompositionVisual().Scale = Vector3.One;
                ClearStationTransitionVisuals();
                _animating = false;
                ApplyOutsideClickSetting();
                if (scrollToEnd) ScrollToEnd(animated: false);
                if (_definition.PlacementMode != OrganizerPlacementMode.Station)
                    WindowRoot.Focus(FocusState.Programmatic);
                UpdateCanvasResizeEdgeWindows(show: true);
                _canvasResizeLeftButtonDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
                _canvasResizeInputTimer.Start();
            }
        }
    }

    private async Task CollapseAsync()
    {
        if ((!_expanded && !_animating) || _hwnd == IntPtr.Zero || _shellDragActive) return;
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            await _host.CollapseContainedChildrenAsync(_definition.Id);
        }
        _outsideClickHook?.Stop();
        if (_itemReorderSession is not null) CancelItemReorder();
        ShutdownItemDragBoundaryHook();

        _expanded = false;
        _animating = true;
        RefreshPerformanceSettings();
        _canvasResizeInputTimer.Stop();
        _canvasResizeLeftButtonDown = false;
        UpdateCanvasResizeEdgeWindows(show: false);
        CancellationTokenSource transition = StartTransition();
        _externalHoverTimer.Stop();
        bool contained = IsContained;
        NativeMethods.RECT compactTarget = contained ? _containedAnchorBounds : _compactBounds;
        if (!NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT expandedBounds)) expandedBounds = CalculateExpandedBounds(compactTarget);
        PrepareTransitionAnchor(expandedBounds, compactTarget, handoffAtCompactOrigin: true);
        _collapseTransitionGeometry = _definition.PlacementMode == OrganizerPlacementMode.Station
            ? null
            : CreateCollapseTransitionGeometry(expandedBounds, compactTarget);
        _lastTransitionWindowLeft = expandedBounds.Left;
        _lastTransitionWindowTop = expandedBounds.Top;
        RenderCompactPreview();
        CompactView.Visibility = _definition.PlacementMode == OrganizerPlacementMode.Station || contained
            ? Visibility.Collapsed
            : Visibility.Visible;

        try
        {
            await RunVisualTransitionAsync(0, transition.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (_transitionCancellation == transition && !transition.IsCancellationRequested)
            {
                CompactView.Opacity = 1;
                if (_definition.PlacementMode == OrganizerPlacementMode.Station)
                {
                    ExpandedView.Visibility = Visibility.Collapsed;
                    ExpandedView.Opacity = 0;
                    GetExpandedCompositionVisual().Scale = Vector3.One;
                    ClearStationTransitionVisuals();
                }
                else if (contained)
                {
                    CompactView.Translation = Vector3.Zero;
                    CompactView.Visibility = Visibility.Collapsed;
                    ExpandedView.Visibility = Visibility.Collapsed;
                    ExpandedView.Opacity = 0;
                    GetExpandedCompositionVisual().Scale = Vector3.One;
                    _appWindow?.Hide();
                }
                else
                {
                    CommitCompactHandoff(compactTarget);
                }
                _collapseTransitionGeometry = null;
                _animating = false;
                _desktopLayer?.SetExpanded(false);
                if (_definition.PlacementMode == OrganizerPlacementMode.Station || contained) _appWindow?.Hide();
                RefreshPerformanceSettings();
                _host.NotifyCollapsed(this);
            }
        }
    }

    private void CommitCompactHandoff(NativeMethods.RECT compactTarget)
    {
        Visual expandedVisual = GetExpandedCompositionVisual();
        _compactBounds = compactTarget;
        ApplyBounds(compactTarget, show: true, preserveZOrder: true);
        CompactView.Translation = Vector3.Zero;
        CompactView.Opacity = 1;
        ExpandedView.Visibility = Visibility.Collapsed;
        ExpandedView.Opacity = 0;
        expandedVisual.Scale = Vector3.One;
    }

    private CancellationTokenSource StartTransition()
    {
        _transitionCancellation?.Cancel();
        _transitionCancellation?.Dispose();
        _transitionCancellation = new CancellationTokenSource();
        return _transitionCancellation;
    }

    private async Task RunVisualTransitionAsync(double target, CancellationToken cancellationToken)
    {
        bool reducedMotion = !UseCustomAnimations;
        long previous = Stopwatch.GetTimestamp();
        if (NativeMethods.TrySetCompositorClockBoost(true)) { }
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long now = Stopwatch.GetTimestamp();
                double measuredMilliseconds = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
                double delta = Math.Clamp(measuredMilliseconds / 1000d, 1d / 240d, 1d / 30d);
                previous = now;
                if (reducedMotion)
                {
                    double step = delta / (ReducedMotionDurationMs / 1000d);
                    _transitionProgress = MoveTowards(_transitionProgress, target, step);
                    _transitionVelocity = 0;
                }
                else
                {
                    double response = _definition.PlacementMode == OrganizerPlacementMode.Station
                        ? StationTransitionResponseSeconds
                        : TransitionResponseSeconds;
                    double omega = 9.2 / response;
                    double displacement = _transitionProgress - target;
                    double helper = _transitionVelocity + omega * displacement;
                    double decay = Math.Exp(-omega * delta);
                    _transitionProgress = Math.Clamp(target + (displacement + helper * delta) * decay, 0, 1);
                    _transitionVelocity = (_transitionVelocity - omega * helper * delta) * decay;
                }
                ApplyTransitionFrame(_transitionProgress, reducedMotion);
                if (Math.Abs(target - _transitionProgress) <= .001 && Math.Abs(_transitionVelocity) <= .01)
                {
                    _transitionProgress = target;
                    _transitionVelocity = 0;
                    ApplyTransitionFrame(target, reducedMotion);
                    if (target == 0 && _definition.PlacementMode != OrganizerPlacementMode.Station)
                        await WaitForNextRenderedAsync(cancellationToken);
                    else
                        await WaitForNextRenderAsync(cancellationToken);
                    break;
                }
                await WaitForNextRenderAsync(cancellationToken);
            }
        }
        finally
        {
            if (_transitionCancellation is null || _transitionCancellation.Token == cancellationToken)
            {
                _ = NativeMethods.TrySetCompositorClockBoost(false);
            }
        }
    }

    private void ApplyTransitionFrame(double progress, bool reducedMotion)
    {
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            ApplyStationTransitionFrame(progress, reducedMotion);
            return;
        }

        ClearStationTransitionVisuals();
        Visual expandedVisual = GetExpandedCompositionVisual();
        expandedVisual.CenterPoint = new Vector3(_transitionAnchorDip, 0);
        CompactView.Translation = _transitionCompactTranslation;
        expandedVisual.Scale = reducedMotion
            ? Vector3.One
            : new Vector3(
                _transitionStartScale.X + (1 - _transitionStartScale.X) * (float)progress,
                _transitionStartScale.Y + (1 - _transitionStartScale.Y) * (float)progress,
                1);
        ExpandedView.Opacity = Math.Clamp((progress - .02) / .82, 0, 1);
        CompactView.Opacity = 1 - Math.Clamp(progress / .34, 0, 1);
        UpdateExpandedClipRadius(progress);
        ApplyCollapseWindowPosition(progress, reducedMotion);
    }

    private void ApplyStationTransitionFrame(double progress, bool reducedMotion)
    {
        double width = Math.Max(1, ExpandedView.ActualWidth);
        double height = Math.Max(1, ExpandedView.ActualHeight);
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        StationTransitionFrame frame = StationTransitionMath.GetFrame(
            _definition.DockEdge,
            width,
            height,
            progress,
            1 / scale,
            reducedMotion);

        Visual expandedVisual = GetExpandedCompositionVisual();
        expandedVisual.Scale = Vector3.One;
        _stationTransitionClip ??= expandedVisual.Compositor.CreateRectangleClip();
        _stationTransitionClip.Left = (float)frame.ClipLeft;
        _stationTransitionClip.Top = (float)frame.ClipTop;
        _stationTransitionClip.Right = (float)frame.ClipRight;
        _stationTransitionClip.Bottom = (float)frame.ClipBottom;
        expandedVisual.Clip = _stationTransitionClip;

        ExpandedContentLayer.Translation = new Vector3((float)frame.TranslationX, (float)frame.TranslationY, 0);
        ExpandedView.Opacity = frame.Opacity;
        CompactView.Opacity = 0;
        CompactView.Translation = Vector3.Zero;
        UpdateExpandedClipRadius(1);
    }

    private void ClearStationTransitionVisuals()
    {
        Visual expandedVisual = GetExpandedCompositionVisual();
        if (_stationTransitionClip is not null && expandedVisual.Clip == _stationTransitionClip)
            expandedVisual.Clip = null;
        expandedVisual.Scale = Vector3.One;
        ExpandedContentLayer.Translation = Vector3.Zero;
    }

    private async Task LoadOrganizerPreviewAsync(Grid preview, Guid organizerId, object requestToken)
    {
        WidgetItem[] items = _host.GetOrganizerPreviewItems(organizerId).Take(CompactPreviewItemCount).ToArray();
        for (int index = 0; index < CompactPreviewItemCount; index++)
        {
            if (FindItemPart<Image>(preview, $"OrganizerPreview{index}") is not Image image) continue;
            image.Tag = null;
            image.Source = null;
        }
        ApplyOrganizerPreviewLayout(preview, items.Length);
        for (int index = 0; index < items.Length; index++)
        {
            if (FindItemPart<Image>(preview, $"OrganizerPreview{index}") is not Image image) continue;
            WidgetItem item = items[index];
            string iconKey = IsDocumentItem(item) ? item.RelativeName : item.FullPath;
            image.Tag = iconKey;
            if (IsDocumentItem(item)) image.Source = GetDocumentIcon(item);
            else await LoadIconAsync(image, item.FullPath);
            if (!ReferenceEquals(preview.Tag, requestToken)) return;
        }
    }

    private static void ApplyOrganizerPreviewLayout(Grid preview, int itemCount)
    {
        Image[] images = Enumerable.Range(0, CompactPreviewItemCount)
            .Select(index => FindItemPart<Image>(preview, $"OrganizerPreview{index}"))
            .Where(image => image is not null)
            .Cast<Image>()
            .ToArray();
        int visibleCount = Math.Clamp(itemCount, 0, images.Length);
        for (int index = 0; index < images.Length; index++)
        {
            Image image = images[index];
            Grid.SetRow(image, index / 2);
            Grid.SetColumn(image, index % 2);
            Grid.SetRowSpan(image, 1);
            Grid.SetColumnSpan(image, 1);
            image.Margin = new Thickness(2);
            image.HorizontalAlignment = HorizontalAlignment.Stretch;
            image.VerticalAlignment = VerticalAlignment.Stretch;
            image.Visibility = index < visibleCount ? Visibility.Visible : Visibility.Collapsed;
        }

        if (FindItemPart<FontIcon>(preview, "OrganizerPreviewEmptyIcon") is FontIcon emptyIcon)
            emptyIcon.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;

        switch (visibleCount)
        {
            case 1:
                Grid.SetRowSpan(images[0], 2);
                Grid.SetColumnSpan(images[0], 2);
                images[0].Margin = new Thickness(5);
                break;
            case 2:
                Grid.SetRowSpan(images[0], 2);
                Grid.SetRowSpan(images[1], 2);
                break;
            case 3:
                Grid.SetColumnSpan(images[2], 2);
                break;
        }
    }

    private static double MoveTowards(double current, double target, double maximumDelta) =>
        Math.Abs(target - current) <= maximumDelta ? target : current + Math.Sign(target - current) * maximumDelta;

    private void PrepareTransitionAnchor(
        NativeMethods.RECT expandedBounds,
        NativeMethods.RECT compactTarget,
        bool handoffAtCompactOrigin = false)
    {
        DisplayInfo display = DisplayPlacementService.ForBounds(expandedBounds) with
        {
            Scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d)
        };
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            _transitionStartScale = Vector2.One;
            int anchorX = compactTarget.Left + compactTarget.Width / 2;
            int anchorY = compactTarget.Top + compactTarget.Height / 2;
            _transitionAnchorDip = new Vector2(
                (float)((anchorX - expandedBounds.Left) / display.Scale),
                (float)((anchorY - expandedBounds.Top) / display.Scale));
            _transitionCompactTranslation = Vector3.Zero;
            return;
        }
        double compactViewWidth = Math.Max(1, GetCompactWidthDip());
        double compactViewHeight = Math.Max(1, GetCompactHeightDip());
        double thumbnailSizeDip = Math.Max(1, 39 * _appliedCompactScale);
        var thumbnailCenterInCompact = new Point(compactViewWidth / 2, thumbnailSizeDip / 2);

        double thumbnailOffsetX = thumbnailCenterInCompact.X - compactViewWidth / 2;
        double thumbnailOffsetY = thumbnailCenterInCompact.Y - compactViewHeight / 2;
        int compactCenterX = compactTarget.Left + DipToPx(
            compactViewWidth / 2 + thumbnailOffsetX,
            display.Scale);
        int compactCenterY = compactTarget.Top + DipToPx(
            compactViewHeight / 2 + thumbnailOffsetY,
            display.Scale);
        double expandedWidthDip = expandedBounds.Width / display.Scale;
        double expandedHeightDip = expandedBounds.Height / display.Scale;
        _transitionStartScale = new Vector2(
            (float)Math.Clamp(thumbnailSizeDip / expandedWidthDip, .025, 1),
            (float)Math.Clamp(thumbnailSizeDip / expandedHeightDip, .025, 1));
        _transitionAnchorDip = handoffAtCompactOrigin
            ? new Vector2((float)thumbnailCenterInCompact.X, (float)thumbnailCenterInCompact.Y)
            : new Vector2(
                (float)((compactCenterX - expandedBounds.Left) / display.Scale),
                (float)((compactCenterY - expandedBounds.Top) / display.Scale));
        double compactCenterInExpandedX = expandedWidthDip / 2 + thumbnailOffsetX;
        double compactCenterInExpandedY = expandedHeightDip / 2 + thumbnailOffsetY;
        _transitionCompactTranslation = handoffAtCompactOrigin
            ? new Vector3(
                (float)(thumbnailCenterInCompact.X - compactCenterInExpandedX),
                (float)(thumbnailCenterInCompact.Y - compactCenterInExpandedY),
                0)
            : new Vector3(
                _transitionAnchorDip.X - (float)compactCenterInExpandedX,
                _transitionAnchorDip.Y - (float)compactCenterInExpandedY,
                0);
    }

    private CollapseTransitionGeometry CreateCollapseTransitionGeometry(
        NativeMethods.RECT expandedBounds,
        NativeMethods.RECT compactTarget)
    {
        return new CollapseTransitionGeometry(
            expandedBounds,
            compactTarget,
            compactTarget.Left,
            compactTarget.Top);
    }

    private void ApplyCollapseWindowPosition(double progress, bool reducedMotion)
    {
        if (reducedMotion || _collapseTransitionGeometry is not { } geometry) return;
        double returnProgress = 1 - progress;
        int left = (int)Math.Round(geometry.ExpandedBounds.Left +
            (geometry.EndExpandedLeft - geometry.ExpandedBounds.Left) * returnProgress);
        int top = (int)Math.Round(geometry.ExpandedBounds.Top +
            (geometry.EndExpandedTop - geometry.ExpandedBounds.Top) * returnProgress);
        if (left == _lastTransitionWindowLeft && top == _lastTransitionWindowTop) return;
        _lastTransitionWindowLeft = left;
        _lastTransitionWindowTop = top;
        _ = NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            left,
            top,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private static Task WaitForNextRenderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? handler = null;
        CancellationTokenRegistration registration = default;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            registration.Dispose();
            completion.TrySetResult();
        };
        CompositionTarget.Rendering += handler;
        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(() =>
            {
                CompositionTarget.Rendering -= handler;
                completion.TrySetCanceled(cancellationToken);
            });
        }
        return completion.Task;
    }

    private static Task WaitForNextRenderedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<RenderedEventArgs>? handler = null;
        CancellationTokenRegistration registration = default;
        handler = (_, _) =>
        {
            CompositionTarget.Rendered -= handler;
            registration.Dispose();
            completion.TrySetResult();
        };
        CompositionTarget.Rendered += handler;
        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(() =>
            {
                CompositionTarget.Rendered -= handler;
                completion.TrySetCanceled(cancellationToken);
            });
        }
        return completion.Task;
    }

    private NativeMethods.RECT CalculateExpandedBounds(NativeMethods.RECT compact, DisplayInfo? targetDisplay = null)
    {
        DisplayInfo display = targetDisplay ?? ((_definition.PlacementMode == OrganizerPlacementMode.Station
                ? DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice)
                : DisplayPlacementService.ForBounds(compact)) with
            {
                Scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d)
            });
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            return DisplayPlacementService.CalculateStationBounds(
                display,
                _definition.DockEdge,
                _definition.Layout,
                _definition.CanvasScale,
                _definition.ItemScale,
                _definition.Position,
                _definition.ManualCanvasBaseWidthDip,
                _definition.ManualCanvasBaseHeightDip);
        }
        if (IsCompactList)
        {
            return DisplayPlacementService.CalculateExpandedBounds(
                compact,
                display,
                _definition.Layout,
                canvasScale: 1,
                _definition.CompactListCanvasWidthDip,
                _definition.CompactListCanvasHeightDip);
        }
        return DisplayPlacementService.CalculateExpandedBounds(
            compact,
            display,
            _definition.Layout,
            _definition.CanvasScale,
            _definition.ManualCanvasBaseWidthDip,
            _definition.ManualCanvasBaseHeightDip);
    }

    private bool ShouldRememberExpandedPosition() =>
        OrganizerInteractionMath.ShouldRememberExpandedPosition(
            _host.State.GlobalSettings.RememberExpandedOrganizerPosition,
            _definition.PlacementMode);

    private void CaptureExpandedPosition()
    {
        if (ShouldRememberExpandedPosition() && NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT bounds))
            _definition.ExpandedPosition = DisplayPlacementService.Capture(bounds, _hwnd);
    }

    private void CompactTile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || _pressActive || _expanded || _animating) return;
        CompactTile.CenterPoint = new Vector3((float)(CompactTile.ActualWidth / 2), (float)(CompactTile.ActualHeight / 2), 0);
        CompactTile.Scale = UseCustomAnimations ? new Vector3(1.015f, 1.015f, 1) : Vector3.One;
    }

    private void CompactTile_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressActive) CompactTile.Scale = Vector3.One;
    }

    private void CompactTile_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _externalHoverTimer.Stop();
        _hoverExpandScrollToEnd = false;
        if (_expanded || _animating || e.Pointer.PointerDeviceType == PointerDeviceType.Touch && !e.GetCurrentPoint(CompactTile).Properties.IsLeftButtonPressed)
        {
            return;
        }
        PointerPoint point = e.GetCurrentPoint(CompactTile);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginWidgetPress(CompactTile, e, point.Position, expanded: false);
    }

    private void ExpandedView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_itemReorderProbeRunning || !_expanded || _animating) return;
        PointerPoint point = e.GetCurrentPoint(ExpandedView);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && TryBeginCanvasResize())
        {
            e.Handled = true;
            return;
        }
        if (IsExpandedDragBlocked(e.OriginalSource as DependencyObject)) return;

        BeginWidgetPress(ExpandedView, e, point.Position, expanded: true);
    }

    private bool TryBeginCanvasResize(CanvasResizeEdge requestedEdge = CanvasResizeEdge.None)
    {
        if (_definition.PlacementMode == OrganizerPlacementMode.Station || !_expanded || _animating ||
            _canvasResize is not null || _shellDragActive || _itemReorderSession is not null ||
            !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT bounds)) return false;
        CanvasResizeEdge edge = requestedEdge == CanvasResizeEdge.None
            ? GetCanvasResizeEdge(cursor, bounds)
            : requestedEdge;
        if (edge == CanvasResizeEdge.None) return false;

        DisplayInfo display = DisplayPlacementService.ForBounds(bounds) with
        {
            Scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d)
        };
        bool compactList = IsCompactList;
        double startScale = Math.Clamp(_definition.CanvasScale, .1, 1.2);
        int titleHeightPx = GetExpandedTitleBandPx(display.Scale);
        int panelHeightPx = Math.Max(1, bounds.Height - titleHeightPx);
        NativeMethods.RECT work = _definition.PlacementMode == OrganizerPlacementMode.Station
            ? display.Work
            : DisplayPlacementService.GetExpandedWorkArea(display);
        double baseWidth = bounds.Width / display.Scale / (compactList ? 1 : startScale);
        double baseHeight = panelHeightPx / display.Scale / (compactList ? 1 : startScale);
        double minimumScale = startScale;
        double maximumScale = startScale;
        if (!compactList)
        {
            double sideInset = _definition.PlacementMode == OrganizerPlacementMode.Station
                ? DisplayPlacementService.StationSideInsetDip
                : DisplayPlacementService.ExpandedSideInsetDip;
            (double minimumWidth, double minimumHeight) =
                DisplayPlacementService.CalculateMinimumExpandedSizeDip(_definition.Layout, .5, sideInset);
            minimumScale = Math.Min(startScale, Math.Min(1.2,
                Math.Max(.1, Math.Max(minimumWidth / baseWidth, minimumHeight / baseHeight))));
            int centerX = bounds.Left + bounds.Width / 2;
            int centerY = bounds.Top + titleHeightPx + panelHeightPx / 2;
            double maximumWidth = (_definition.PlacementMode == OrganizerPlacementMode.Station
                ? work.Width
                : Math.Max(1, 2d * Math.Min(centerX - work.Left, work.Right - centerX))) / display.Scale;
            double maximumHeight = (_definition.PlacementMode == OrganizerPlacementMode.Station
                ? work.Height
                : Math.Max(1, 2d * Math.Min(centerY - work.Top - titleHeightPx, work.Bottom - centerY))) / display.Scale;
            maximumScale = Math.Max(startScale, Math.Min(1.2,
                Math.Min(maximumWidth / baseWidth, maximumHeight / baseHeight)));
        }

        _ = NativeMethods.SetCapture(_hwnd);
        if (NativeMethods.GetCapture() != _hwnd) return false;
        _dragCurrentBounds = bounds;
        _hasPendingCanvasResizeCursor = false;
        _canvasResize = new(
            edge,
            cursor,
            bounds,
            compactList,
            startScale,
            baseWidth,
            baseHeight,
            minimumScale,
            maximumScale,
            display.Scale,
            work);
        SetCanvasResizeCursor(edge);
        return true;
    }

    private void PollCanvasResizeInput()
    {
        bool leftButtonDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        if (!_expanded || _animating || !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
        {
            _canvasResizeLeftButtonDown = leftButtonDown;
            return;
        }

        CanvasResizeEdge edge = _canvasResize?.Edge ?? GetCanvasResizeEdge(cursor);
        if (_canvasResizeProbeRunning)
        {
            _canvasResizeLeftButtonDown = leftButtonDown;
            return;
        }
        if (_canvasResize is null)
        {
            if (leftButtonDown && !_canvasResizeLeftButtonDown && edge != CanvasResizeEdge.None)
                _ = TryBeginCanvasResize(edge);
        }
        else if (leftButtonDown)
        {
            CommitCanvasResize(cursor);
        }
        else
        {
            _ = FinishCanvasResizeAsync(cursor);
        }
        _canvasResizeLeftButtonDown = leftButtonDown;
    }

    private void AttachCanvasResizeWindowProc(IntPtr window)
    {
        IntPtr resizeWindowProc = Marshal.GetFunctionPointerForDelegate(_canvasResizeWindowProc);
        if (NativeMethods.GetWindowLongPtr(window, NativeMethods.GWLP_WNDPROC) == resizeWindowProc) return;
        IntPtr previous = NativeMethods.SetWindowLongPtr(
            window,
            NativeMethods.GWLP_WNDPROC,
            resizeWindowProc);
        if (previous != IntPtr.Zero) _canvasResizeOriginalWindowProcs[window] = previous;
    }

    private void UpdateCanvasResizeEdgeWindows(bool show)
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd)) return;
        if (!show || _definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            foreach (IntPtr edgeWindow in _canvasResizeEdgeWindows)
            {
                _ = NativeMethods.SetWindowPos(
                    edgeWindow,
                    IntPtr.Zero,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_HIDEWINDOW);
            }
            return;
        }
        if (_canvasResizeEdgeWindows.Count == 0)
        {
            for (int index = 0; index < 4; index++)
            {
                IntPtr edgeWindow = NativeMethods.CreateWindowEx(
                    NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE,
                    "STATIC",
                    null,
                    NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE,
                    0,
                    0,
                    1,
                    1,
                    _hwnd,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
                if (edgeWindow == IntPtr.Zero) continue;
                _ = NativeMethods.SetLayeredWindowAttributes(edgeWindow, 0, 1, NativeMethods.LWA_ALPHA);
                AttachCanvasResizeWindowProc(edgeWindow);
                _canvasResizeEdgeWindows.Add(edgeWindow);
            }
        }

        if (_canvasResizeEdgeWindows.Count != 4 || !NativeMethods.GetClientRect(_hwnd, out NativeMethods.RECT client)) return;
        int width = Math.Max(1, client.Width);
        int height = Math.Max(1, client.Height);
        int band = Math.Max(1, (int)Math.Ceiling(CanvasResizeBorderDip * NativeMethods.GetDpiForWindow(_hwnd) / 96d));
        (int X, int Y, int Width, int Height)[] rectangles =
        [
            (0, 0, width, Math.Min(band, height)),
            (0, Math.Max(0, height - band), width, Math.Min(band, height)),
            (0, band, Math.Min(band, width), Math.Max(1, height - 2 * band)),
            (Math.Max(0, width - band), band, Math.Min(band, width), Math.Max(1, height - 2 * band))
        ];
        for (int index = 0; index < rectangles.Length; index++)
        {
            var rectangle = rectangles[index];
            _ = NativeMethods.SetWindowPos(
                _canvasResizeEdgeWindows[index],
                NativeMethods.HWND_TOP,
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    private void RestoreCanvasResizeWindowProc(IntPtr window)
    {
        if (!_canvasResizeOriginalWindowProcs.Remove(window, out IntPtr previous) || !NativeMethods.IsWindow(window)) return;
        IntPtr current = NativeMethods.GetWindowLongPtr(window, NativeMethods.GWLP_WNDPROC);
        if (current == Marshal.GetFunctionPointerForDelegate(_canvasResizeWindowProc))
            _ = NativeMethods.SetWindowLongPtr(window, NativeMethods.GWLP_WNDPROC, previous);
    }

    private void BeginWidgetPress(UIElement captureTarget, PointerRoutedEventArgs e, Point point, bool expanded)
    {
        _pressedPointerId = e.Pointer.PointerId;
        _draggingExpanded = expanded;
        _pressPointDip = point;
        _longPressGesture.Press(point.X, point.Y);
        _pressActive = true;
        _widgetDragging = false;
        _pressStartedAt = Stopwatch.GetTimestamp();
        _ = NativeMethods.GetCursorPos(out _pressCursorPx);
        _ = NativeMethods.GetWindowRect(_hwnd, out _pressWindowBounds);
        _dragAlignmentInsets = !expanded &&
            TryGetCompactAlignmentInsets(_pressWindowBounds, out WindowAlignmentInsets alignmentInsets)
                ? alignmentInsets
                : null;
        ClearWindowAlignment();
        _dragCurrentBounds = _pressWindowBounds;
        if (!expanded && _definition.PlacementMode == OrganizerPlacementMode.Positioned)
        {
            _positionedDragOriginBounds = _compactBounds;
        }
        _dragDisplay = DisplayPlacementService.ForBounds(_pressWindowBounds);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            _ = NativeMethods.SetCapture(_hwnd);
            _nativeMouseCapture = NativeMethods.GetCapture() == _hwnd;
        }
        else if (!captureTarget.CapturePointer(e.Pointer))
        {
            ResetCompactPress();
            return;
        }

        StartDragClock();
        if (expanded)
        {
            Visual expandedVisual = GetExpandedCompositionVisual();
            expandedVisual.CenterPoint = new Vector3((float)(ExpandedView.ActualWidth / 2), (float)(ExpandedView.ActualHeight / 2), 0);
            expandedVisual.Scale = new Vector3(.992f, .992f, 1);
        }
        else
        {
            CompactTile.CenterPoint = new Vector3(19.5f, 19.5f, 0);
            CompactTile.Scale = UseCustomAnimations ? new Vector3(.98f, .98f, 1) : Vector3.One;
        }
        _longPressTimer.Start();
        e.Handled = true;
    }

    private static bool IsExpandedDragBlocked(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button || current is FrameworkElement { Name: "ItemHost" or "ItemContent" }) return true;
            if (current is Grid { Name: "ExpandedView" }) break;
        }
        return false;
    }

    private void ExpandedView_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasResize is not null)
        {
            e.Handled = true;
            return;
        }
        if (!_pressActive || !_draggingExpanded || e.Pointer.PointerId != _pressedPointerId) return;
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse && !_widgetDragging)
        {
            Point current = e.GetCurrentPoint(ExpandedView).Position;
            _ = _longPressGesture.Move(current.X, current.Y);
        }
        else if (_widgetDragging)
        {
            _dragInputCount++;
            UpdateWidgetDragFromCursor();
            e.Handled = true;
        }
    }

    private async void ExpandedView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasResize is not null)
        {
            await FinishCanvasResizeAsync();
            e.Handled = true;
            return;
        }
        if (_pressActive && _draggingExpanded && e.Pointer.PointerId == _pressedPointerId)
        {
            await FinishCompactPressAsync(allowOpen: false);
            e.Handled = true;
        }
    }

    private async void ExpandedView_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasResize is not null) await FinishCanvasResizeAsync();
        else await FinishCompactPressAsync(allowOpen: false);
    }

    private async void ExpandedView_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasResize is not null && NativeMethods.GetCapture() != _hwnd) await FinishCanvasResizeAsync();
        else if (_pressActive && _draggingExpanded && !_nativeMouseCapture) await FinishCompactPressAsync(allowOpen: false);
    }

    private void ExpandedView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        bool controlPressed = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        if (!OrganizerInteractionMath.ShouldApplyCtrlWheelScale(
                _expanded,
                _animating,
                _canvasResize is not null,
                _itemReorderSession is not null,
                _shellDragActive,
                controlPressed))
        {
            _wheelDeltaRemainder = 0;
            return;
        }

        PointerPoint point = e.GetCurrentPoint(ExpandedView);
        if (point.Position.X < 0 || point.Position.Y < 0 ||
            point.Position.X > ExpandedView.ActualWidth || point.Position.Y > ExpandedView.ActualHeight) return;
        e.Handled = true;
        _wheelDeltaRemainder += point.Properties.MouseWheelDelta;
        int steps = _wheelDeltaRemainder / 120;
        _wheelDeltaRemainder %= 120;
        if (steps == 0) return;

        if (IsCompactList)
        {
            double nextCompactListScale = OrganizerInteractionMath.ApplyWheelSteps(
                _definition.CompactListItemScale,
                steps,
                .5,
                1.65);
            if (Math.Abs(nextCompactListScale - _definition.CompactListItemScale) < .0001) return;
            _definition.CompactListItemScale = nextCompactListScale;
            ApplyDefinition(OrganizerVisualChange.CompactListItemScale);
            _interactionSaveTimer.Stop();
            _interactionSaveTimer.Start();
            return;
        }

        double maximum = _definition.PlacementMode == OrganizerPlacementMode.Station
            ? DisplayPlacementService.CalculateMaximumStationItemScale(
                DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice),
                _definition.Layout)
            : DisplayPlacementService.CalculateMaximumItemScaleForExpandedSize(
                _definition.Layout,
                Math.Max(1, ExpandedPanel.ActualWidth),
                Math.Max(1, ExpandedPanel.ActualHeight));
        double next = OrganizerInteractionMath.ApplyWheelSteps(_definition.ItemScale, steps, .5, maximum);
        if (Math.Abs(next - _definition.ItemScale) < .0001) return;
        _definition.ItemScale = next;
        ApplyDefinition(OrganizerVisualChange.ItemScale);
        _interactionSaveTimer.Stop();
        _interactionSaveTimer.Start();
    }

    private void CommitCanvasResize(NativeMethods.POINT cursor)
    {
        if (_canvasResize is not { } session) return;
        ClearStationTransitionVisuals();
        int titleHeightPx = GetExpandedTitleBandPx(session.DisplayScale);
        int startPanelHeightPx = Math.Max(1, session.StartBounds.Height - titleHeightPx);
        if (session.CompactList)
        {
            (int listLeft, int listTop, int listWidth, int listHeight) = OrganizerInteractionMath.ResizeFixedEdges(
                session.Edge,
                session.StartBounds.Left,
                session.StartBounds.Top,
                session.StartBounds.Width,
                session.StartBounds.Height,
                cursor.X - session.StartCursor.X,
                cursor.Y - session.StartCursor.Y,
                Math.Max(1, (int)Math.Round(OrganizerLimits.MinimumCompactListCanvasWidthDip * session.DisplayScale)),
                Math.Max(1, titleHeightPx + (int)Math.Round(OrganizerLimits.MinimumCompactListCanvasHeightDip * session.DisplayScale)),
                session.WorkArea.Left,
                session.WorkArea.Top,
                session.WorkArea.Right,
                session.WorkArea.Bottom);
            var compactListBounds = new NativeMethods.RECT
            {
                Left = listLeft,
                Top = listTop,
                Right = listLeft + listWidth,
                Bottom = listTop + listHeight
            };
            if (RectsEqual(compactListBounds, _dragCurrentBounds)) return;
            _definition.CompactListCanvasWidthDip = listWidth / session.DisplayScale;
            _definition.CompactListCanvasHeightDip = Math.Max(1, listHeight - titleHeightPx) / session.DisplayScale;
            _dragCurrentBounds = compactListBounds;
            _ = NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                compactListBounds.Left,
                compactListBounds.Top,
                compactListBounds.Width,
                compactListBounds.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            UpdateCanvasResizeEdgeWindows(show: true);
            return;
        }
        double factor = OrganizerInteractionMath.CalculateResizeFactor(
            session.Edge,
            cursor.X - session.StartCursor.X,
            cursor.Y - session.StartCursor.Y,
            session.StartBounds.Width,
            startPanelHeightPx);
        double canvasScale = Math.Clamp(
            session.StartCanvasScale * factor,
            session.MinimumCanvasScale,
            session.MaximumCanvasScale);
        int centerX = session.StartBounds.Left + session.StartBounds.Width / 2;
        int centerY = session.StartBounds.Top + titleHeightPx + startPanelHeightPx / 2;
        (int left, int top, int width, int height) = OrganizerInteractionMath.CreateCenteredBounds(
            centerX,
            centerY,
            session.BaseWidthDip * canvasScale * session.DisplayScale,
            session.BaseHeightDip * canvasScale * session.DisplayScale);
        var bounds = new NativeMethods.RECT
        {
            Left = left,
            Top = top - titleHeightPx,
            Right = left + width,
            Bottom = top + height
        };
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            bounds = DisplayPlacementService.CalculateStationBounds(
                DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice),
                _definition.DockEdge,
                _definition.Layout,
                canvasScale,
                _definition.ItemScale,
                _definition.Position,
                session.BaseWidthDip,
                session.BaseHeightDip);
            width = bounds.Width;
            height = bounds.Height;
        }
        if ((RectsEqual(bounds, _dragCurrentBounds) || RectsEqual(bounds, session.StartBounds)) &&
            Math.Abs(canvasScale - _definition.CanvasScale) < .0001) return;

        _definition.ManualCanvasBaseWidthDip = session.BaseWidthDip;
        _definition.ManualCanvasBaseHeightDip = session.BaseHeightDip;
        _definition.CanvasScale = canvasScale;
        double previousItemScale = _definition.ItemScale;
        double maximumItemScale = DisplayPlacementService.CalculateMaximumItemScaleForExpandedSize(
            _definition.Layout,
            width / session.DisplayScale,
            height / session.DisplayScale,
            _definition.PlacementMode == OrganizerPlacementMode.Station
                ? DisplayPlacementService.StationSideInsetDip
                : DisplayPlacementService.ExpandedSideInsetDip);
        _definition.ItemScale = Math.Clamp(Math.Min(previousItemScale, maximumItemScale), .5, DisplayPlacementService.MaximumItemScale);
        _dragCurrentBounds = bounds;
        _ = NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        UpdateCanvasResizeEdgeWindows(show: true);
        if (previousItemScale - _definition.ItemScale >= .005)
        {
            UpdateRealizedItems();
            UpdateCompactPreviewItemScale();
        }
    }

    private async Task FinishCanvasResizeAsync(NativeMethods.POINT? finalCursor = null)
    {
        if (_canvasResize is null) return;
        StopCanvasResizeRendering();
        NativeMethods.POINT cursor;
        if (finalCursor is { } suppliedCursor) CommitCanvasResize(suppliedCursor);
        else if (NativeMethods.GetCursorPos(out cursor)) CommitCanvasResize(cursor);
        _canvasResize = null;
        if (NativeMethods.GetCapture() == _hwnd) _ = NativeMethods.ReleaseCapture();
        ConfigureItemsLayout();
        UpdateCompactPreviewItemScale();
        UpdateSurfaceClips();
        if (NativeMethods.GetCursorPos(out cursor)) SetCanvasResizeCursor(GetCanvasResizeEdge(cursor));
        _interactionSaveTimer.Stop();
        CaptureExpandedPosition();
        await SaveStateAsync();
        _desktopLayer?.Reattach();
    }

    private async Task RunCanvasResizeProbeAsync()
    {
        await Task.Delay(100);
        if (!NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT startBounds))
            throw new InvalidOperationException("Resize probe could not read the organizer bounds.");

        var startCursor = new NativeMethods.POINT
        {
            X = startBounds.Right - 8,
            Y = startBounds.Top + startBounds.Height / 2
        };
        _ = NativeMethods.SetCursorPos(startCursor.X, startCursor.Y);
        await Task.Delay(50);
        CanvasResizeEdge edge = GetCanvasResizeEdge(startCursor, startBounds);
        _canvasResizeProbeRunning = true;
        if (!TryBeginCanvasResize(edge)) throw new InvalidOperationException("Resize probe could not start resizing.");
        bool cursorMatches = SetCanvasResizeCursor(edge) && GetCanvasResizeHitTest(edge) == NativeMethods.HTRIGHT;

        var samples = new List<NativeMethods.RECT>();
        NativeMethods.POINT finalCursor = startCursor;
        for (int step = 1; step <= 12; step++)
        {
            finalCursor = new NativeMethods.POINT { X = startCursor.X - step * 8, Y = startCursor.Y };
            QueueCanvasResize(finalCursor);
            await Task.Delay(25);
            if (NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT sample)) samples.Add(sample);
        }

        var releaseClock = Stopwatch.StartNew();
        Task finish = FinishCanvasResizeAsync(finalCursor);
        _canvasResizeProbeRunning = false;
        long releaseSettledMs = releaseClock.ElapsedMilliseconds;
        _ = NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT beforeFreeMove);
        _ = NativeMethods.SetCursorPos(finalCursor.X - 120, finalCursor.Y);
        await Task.Delay(200);
        _ = NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT afterFreeMove);
        await finish;
        WindowRoot.UpdateLayout();

        int distinctWidths = samples.Select(sample => sample.Width).Distinct().Count();
        double dpiScale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        int titleHeightPx = GetExpandedTitleBandPx(dpiScale);
        double startRatio = startBounds.Width / (double)Math.Max(1, startBounds.Height - titleHeightPx);
        double maximumAspectErrorPx = samples.Count == 0
            ? double.PositiveInfinity
            : samples.Max(sample => Math.Abs(sample.Width - Math.Max(1, sample.Height - titleHeightPx) * startRatio));
        bool surfaceMatchesClient = NativeMethods.GetClientRect(_hwnd, out NativeMethods.RECT client) &&
            Math.Abs(ExpandedSurfaceHost.ActualWidth - client.Width / dpiScale) <= 1 &&
            Math.Abs(ExpandedSurfaceHost.ActualHeight + DisplayPlacementService.ExpandedTitleBandDip - client.Height / dpiScale) <= 1;
        Visual expandedVisual = GetExpandedCompositionVisual();
        bool transitionVisualReset = (_stationTransitionClip is null || expandedVisual.Clip != _stationTransitionClip) &&
            Vector3.Distance(expandedVisual.Scale, Vector3.One) <= .001f &&
            Vector3.Distance(ExpandedContentLayer.Translation, Vector3.Zero) <= .001f;
        bool followedAfterRelease = !RectsEqual(beforeFreeMove, afterFreeMove);
        bool passed = edge == CanvasResizeEdge.Right &&
            GetCanvasResizeHitTest(edge) == NativeMethods.HTRIGHT &&
            cursorMatches && distinctWidths >= 9 && maximumAspectErrorPx <= 1 &&
            surfaceMatchesClient && transitionVisualReset && releaseSettledMs <= 120 && !followedAfterRelease;
        string resultPath = Path.Combine(AppPaths.LocalRoot, "resize-probe.json");
        string result = JsonSerializer.Serialize(new
        {
            Passed = passed,
            CursorMatchesNativeResize = cursorMatches,
            ResizeHitTest = GetCanvasResizeHitTest(edge),
            DistinctLiveWidths = distinctWidths,
            LiveResponseRatio = Math.Round(distinctWidths / 12d, 3),
            MaximumAspectErrorPx = Math.Round(maximumAspectErrorPx, 3),
            SurfaceMatchesClient = surfaceMatchesClient,
            TransitionVisualReset = transitionVisualReset,
            ReleaseSettledMs = releaseSettledMs,
            FollowedAfterRelease = followedAfterRelease
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(resultPath, result);
    }

    private void QueueCanvasResize(NativeMethods.POINT cursor)
    {
        _pendingCanvasResizeCursor = cursor;
        _hasPendingCanvasResizeCursor = true;
        if (_canvasResizeCommitQueued) return;
        _canvasResizeCommitQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, ProcessQueuedCanvasResize))
        {
            _canvasResizeCommitQueued = false;
        }
    }

    private void ProcessQueuedCanvasResize()
    {
        _canvasResizeCommitQueued = false;
        if (_canvasResize is null) return;
        if (!_hasPendingCanvasResizeCursor) return;
        NativeMethods.POINT cursor = _pendingCanvasResizeCursor;
        _hasPendingCanvasResizeCursor = false;
        CommitCanvasResize(cursor);
    }

    private void StopCanvasResizeRendering()
    {
        _canvasResizeCommitQueued = false;
        _hasPendingCanvasResizeCursor = false;
    }

    private static CanvasResizeEdge GetCanvasResizeEdge(Point point, double width, double height)
    {
        if (point.X < 0 || point.Y < 0 || point.X > width || point.Y > height) return CanvasResizeEdge.None;
        CanvasResizeEdge edge = CanvasResizeEdge.None;
        if (point.X <= CanvasResizeBorderDip) edge |= CanvasResizeEdge.Left;
        else if (point.X >= width - CanvasResizeBorderDip) edge |= CanvasResizeEdge.Right;
        if (point.Y <= CanvasResizeBorderDip) edge |= CanvasResizeEdge.Top;
        else if (point.Y >= height - CanvasResizeBorderDip) edge |= CanvasResizeEdge.Bottom;
        return edge;
    }

    private CanvasResizeEdge GetCanvasResizeEdge(NativeMethods.POINT cursor)
    {
        if (_definition.PlacementMode == OrganizerPlacementMode.Station || !_expanded || _animating ||
            _itemReorderSession is not null || _shellDragActive ||
            !NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT bounds)) return CanvasResizeEdge.None;
        return GetCanvasResizeEdge(cursor, bounds);
    }

    private CanvasResizeEdge GetCanvasResizeEdge(NativeMethods.POINT cursor, NativeMethods.RECT bounds)
    {
        if (_definition.PlacementMode == OrganizerPlacementMode.Station || !_expanded || _animating ||
            _itemReorderSession is not null || _shellDragActive) return CanvasResizeEdge.None;
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        return GetCanvasResizeEdge(
            new Point((cursor.X - bounds.Left) / scale, (cursor.Y - bounds.Top) / scale),
            bounds.Width / scale,
            bounds.Height / scale);
    }

    private bool SetCanvasResizeCursor(CanvasResizeEdge edge)
    {
        uint cursorId = edge switch
        {
            CanvasResizeEdge.None => NativeMethods.IDC_ARROW,
            CanvasResizeEdge.Left or CanvasResizeEdge.Right => NativeMethods.IDC_SIZEWE,
            CanvasResizeEdge.Top or CanvasResizeEdge.Bottom => NativeMethods.IDC_SIZENS,
            CanvasResizeEdge.Left | CanvasResizeEdge.Top or CanvasResizeEdge.Right | CanvasResizeEdge.Bottom => NativeMethods.IDC_SIZENWSE,
            _ => NativeMethods.IDC_SIZENESW
        };
        IntPtr cursor = NativeMethods.LoadCursor(IntPtr.Zero, new UIntPtr(cursorId));
        if (cursor == IntPtr.Zero) return false;
        _ = NativeMethods.SetCursor(cursor);
        return true;
    }

    private static int GetCanvasResizeHitTest(CanvasResizeEdge edge) => edge switch
    {
        CanvasResizeEdge.Left => NativeMethods.HTLEFT,
        CanvasResizeEdge.Right => NativeMethods.HTRIGHT,
        CanvasResizeEdge.Top => NativeMethods.HTTOP,
        CanvasResizeEdge.Bottom => NativeMethods.HTBOTTOM,
        CanvasResizeEdge.Left | CanvasResizeEdge.Top => NativeMethods.HTTOPLEFT,
        CanvasResizeEdge.Right | CanvasResizeEdge.Top => NativeMethods.HTTOPRIGHT,
        CanvasResizeEdge.Left | CanvasResizeEdge.Bottom => NativeMethods.HTBOTTOMLEFT,
        CanvasResizeEdge.Right | CanvasResizeEdge.Bottom => NativeMethods.HTBOTTOMRIGHT,
        _ => NativeMethods.HTCLIENT
    };

    private static CanvasResizeEdge GetCanvasResizeEdgeFromHitTest(int hitTest) => hitTest switch
    {
        NativeMethods.HTLEFT => CanvasResizeEdge.Left,
        NativeMethods.HTRIGHT => CanvasResizeEdge.Right,
        NativeMethods.HTTOP => CanvasResizeEdge.Top,
        NativeMethods.HTBOTTOM => CanvasResizeEdge.Bottom,
        NativeMethods.HTTOPLEFT => CanvasResizeEdge.Left | CanvasResizeEdge.Top,
        NativeMethods.HTTOPRIGHT => CanvasResizeEdge.Right | CanvasResizeEdge.Top,
        NativeMethods.HTBOTTOMLEFT => CanvasResizeEdge.Left | CanvasResizeEdge.Bottom,
        NativeMethods.HTBOTTOMRIGHT => CanvasResizeEdge.Right | CanvasResizeEdge.Bottom,
        _ => CanvasResizeEdge.None
    };

    private async void CollapseButton_Click(object sender, RoutedEventArgs e) => await CollapseAsync();

    private void CollapseButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        CollapseButtonSurface.Background = _collapseHoverBrush;
        CollapseDash.Opacity = 1;
    }

    private void CollapseButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        CollapseDash.CenterPoint = new Vector3(6, 1, 0);
        CollapseDash.Scale = UseCustomAnimations ? new Vector3(.92f, .92f, 1) : Vector3.One;
        CollapseButtonSurface.Background = _collapsePressedBrush;
    }

    private void CollapseButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CollapseDash.Scale = Vector3.One;
        CollapseButtonSurface.Background = CollapseButton.IsPointerOver ? _collapseHoverBrush : _transparentItemBrush;
        CollapseDash.Opacity = CollapseButton.IsPointerOver ? 1 : .78;
    }

    private void CollapseButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        CollapseDash.Scale = Vector3.One;
        CollapseDash.Opacity = .78;
        CollapseButtonSurface.Background = _transparentItemBrush;
    }

    private void CompactTile_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressActive || e.Pointer.PointerId != _pressedPointerId)
        {
            return;
        }

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            return;
        }

        if (!_widgetDragging)
        {
            Point current = e.GetCurrentPoint(CompactTile).Position;
            _ = _longPressGesture.Move(current.X, current.Y);
            return;
        }

        _dragInputCount++;
        UpdateWidgetDragFromCursor();
        e.Handled = true;
    }

    private IntPtr GestureWindowProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, IntPtr referenceData)
    {
        if (message == NativeMethods.WM_MOUSEMOVE && _shellDragActive)
        {
            DragMessageRelay.Complete(ref _itemDragOleMouseMovePending);
        }
        if (TryHandleCanvasResizeWindowMessage(hWnd, message, wParam, out IntPtr resizeResult)) return resizeResult;
        if (message == NativeMethods.WM_APP_START_ITEM_EXTERNAL_DRAG &&
            _itemReorderSession is { IsActive: true, NativeDragStarted: false } externalSession &&
            externalSession.SourceIndex >= 0 && externalSession.SourceIndex < _items.Count &&
            ShellDragService.RequiresNativeDrag(_items[externalSession.SourceIndex].Kind))
        {
            externalSession.StartNativeDrag();
            StartMouseNativeDrag(externalSession);
            return IntPtr.Zero;
        }
        if (_nativeMouseCapture && _pressActive && message == NativeMethods.WM_MOUSEMOVE)
        {
            if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
            {
                if (!_widgetDragging)
                {
                    double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
                    _ = _longPressGesture.Move(
                        _pressPointDip.X + (cursor.X - _pressCursorPx.X) / scale,
                        _pressPointDip.Y + (cursor.Y - _pressCursorPx.Y) / scale);
                }
                else
                {
                    _dragInputCount++;
                    if (!_dragClockBoosted)
                    {
                        CommitWidgetDrag(cursor);
                    }
                }
            }
        }
        else if (_nativeMouseCapture && _pressActive && message == NativeMethods.WM_LBUTTONUP)
        {
            _ = FinishCompactPressAsync(allowOpen: true);
        }
        else if (_nativeMouseCapture && _pressActive && message == NativeMethods.WM_CAPTURECHANGED)
        {
            _ = FinishCompactPressAsync(allowOpen: false);
        }
        return NativeMethods.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private IntPtr CanvasResizeWindowProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (TryHandleCanvasResizeWindowMessage(hWnd, message, wParam, out IntPtr result)) return result;
        return _canvasResizeOriginalWindowProcs.TryGetValue(hWnd, out IntPtr previous)
            ? NativeMethods.CallWindowProc(previous, hWnd, message, wParam, lParam)
            : IntPtr.Zero;
    }

    private bool TryHandleCanvasResizeWindowMessage(IntPtr hWnd, uint message, UIntPtr wParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (message == NativeMethods.WM_NCHITTEST && NativeMethods.GetCursorPos(out NativeMethods.POINT hitCursor))
        {
            if (hWnd != _hwnd)
            {
                result = new IntPtr(NativeMethods.HTCLIENT);
                return true;
            }
            CanvasResizeEdge edge = _canvasResize?.Edge ?? GetCanvasResizeEdge(hitCursor);
            if (edge != CanvasResizeEdge.None)
            {
                result = new IntPtr(GetCanvasResizeHitTest(edge));
                return true;
            }
        }
        if (message == NativeMethods.WM_SETCURSOR && NativeMethods.GetCursorPos(out NativeMethods.POINT hoverCursor))
        {
            CanvasResizeEdge edge = _canvasResize?.Edge ?? GetCanvasResizeEdge(hoverCursor);
            if (edge != CanvasResizeEdge.None)
            {
                SetCanvasResizeCursor(edge);
                result = new IntPtr(1);
                return true;
            }
        }
        if (message == NativeMethods.WM_NCLBUTTONDOWN)
        {
            CanvasResizeEdge edge = GetCanvasResizeEdgeFromHitTest(unchecked((int)wParam.ToUInt64()));
            if (edge != CanvasResizeEdge.None && TryBeginCanvasResize(edge)) return true;
        }
        if (message == NativeMethods.WM_LBUTTONDOWN && NativeMethods.GetCursorPos(out NativeMethods.POINT pressCursor))
        {
            CanvasResizeEdge edge = GetCanvasResizeEdge(pressCursor);
            if (edge != CanvasResizeEdge.None && TryBeginCanvasResize(edge)) return true;
        }
        if (_canvasResize is not null && message == NativeMethods.WM_MOUSEMOVE &&
            NativeMethods.GetCursorPos(out NativeMethods.POINT resizeCursor))
        {
            if (!_canvasResizeProbeRunning)
            {
                if ((wParam.ToUInt64() & NativeMethods.MK_LBUTTON) == 0) _ = FinishCanvasResizeAsync();
                else QueueCanvasResize(resizeCursor);
            }
            return true;
        }
        if (_canvasResize is not null && message is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_NCLBUTTONUP or NativeMethods.WM_CAPTURECHANGED)
        {
            _ = FinishCanvasResizeAsync();
            return true;
        }
        return false;
    }

    private void UpdateWidgetDragFromCursor()
    {
        if (!_widgetDragging || !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)) return;
        CommitWidgetDrag(cursor);
    }

    private void DragRendering(object? sender, object args)
    {
        _dragRenderTickCount++;
        if (_pressActive && !_widgetDragging &&
            Stopwatch.GetElapsedTime(_pressStartedAt).TotalMilliseconds >= LongPressMs)
        {
            TryStartWidgetDrag();
        }
        if (_widgetDragging && NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
        {
            CommitWidgetDrag(cursor);
        }
    }

    private void CommitWidgetDrag(NativeMethods.POINT cursor)
    {
        if (_hasLastDragCursor && cursor.X == _lastDragCursor.X && cursor.Y == _lastDragCursor.Y)
        {
            return;
        }
        _lastDragCursor = cursor;
        _hasLastDragCursor = true;
        var desired = new NativeMethods.RECT
        {
            Left = _pressWindowBounds.Left + cursor.X - _pressCursorPx.X,
            Top = _pressWindowBounds.Top + cursor.Y - _pressCursorPx.Y,
            Right = _pressWindowBounds.Right + cursor.X - _pressCursorPx.X,
            Bottom = _pressWindowBounds.Bottom + cursor.Y - _pressCursorPx.Y
        };
        DisplayInfo display = _dragDisplay ?? DisplayPlacementService.ForBounds(desired);
        bool overStationDropTarget = OrganizerInteractionMath.ShouldUseWindowAlignment(
                _host.State.GlobalSettings.WindowAlignmentEnabled,
                _draggingExpanded,
                _definition.PlacementMode,
                overStationDropTarget: false) &&
            _host.IsOrganizerStationDropTarget(this, cursor);
        WindowAlignmentInsets? alignmentInsets = OrganizerInteractionMath.ShouldUseWindowAlignment(
            _host.State.GlobalSettings.WindowAlignmentEnabled,
            _draggingExpanded,
            _definition.PlacementMode,
            overStationDropTarget)
                ? _dragAlignmentInsets
                : null;
        NativeMethods.RECT nextBounds;
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            nextBounds = DisplayPlacementService.CalculateStationDraggedBounds(
                _pressWindowBounds,
                _pressCursorPx,
                cursor,
                display,
                _definition.DockEdge);
        }
        else
        {
            int centerX = desired.Left + desired.Width / 2;
            int centerY = desired.Top + desired.Height / 2;
            if (centerX < display.Monitor.Left || centerX >= display.Monitor.Right ||
                centerY < display.Monitor.Top || centerY >= display.Monitor.Bottom)
            {
                display = DisplayPlacementService.ForBounds(desired);
                _dragDisplay = display;
                ClearWindowAlignment();
            }
            nextBounds = alignmentInsets is WindowAlignmentInsets frameInsets
                ? WindowAlignmentMath.ClampFrame(desired, display.Work, frameInsets)
                : DisplayPlacementService.CalculateDraggedBounds(_pressWindowBounds, _pressCursorPx, cursor, display.Work);
        }

        if (alignmentInsets is WindowAlignmentInsets frameInsetsForAlignment)
        {
            uint dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(_hwnd));
            WindowAlignmentResult result = WindowAlignmentMath.Align(
                frameInsetsForAlignment.ToFrame(nextBounds),
                display.Work,
                _host.GetWindowAlignmentTargets(this, display),
                WindowAlignmentMath.DipToPx(WindowAlignmentMath.SnapDistanceDip, dpi),
                WindowAlignmentMath.DipToPx(WindowAlignmentMath.ReleaseDistanceDip, dpi),
                _windowAlignmentState);
            _windowAlignmentState = result.State;
            nextBounds = frameInsetsForAlignment.ToWindow(result.Bounds);
            _windowAlignmentGuide ??= new WindowAlignmentGuideOverlay(_hwnd);
            _windowAlignmentGuide.Show(result.XGuide, result.YGuide, dpi);
        }
        else
        {
            ClearWindowAlignment();
        }

        NativeMethods.RECT currentBounds = _draggingExpanded ? _dragCurrentBounds : _compactBounds;
        if (RectsEqual(nextBounds, currentBounds))
        {
            return;
        }
        if (!NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            nextBounds.Left,
            nextBounds.Top,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW))
        {
            AppLogger.Error($"拖动收纳窗失败，Win32={Marshal.GetLastWin32Error()}。");
            ClearWindowAlignment();
            return;
        }
        if (_draggingExpanded)
        {
            _dragCurrentBounds = nextBounds;
        }
        else _compactBounds = nextBounds;
        _dragCommitCount++;
    }

    private async void CompactTile_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressActive || e.Pointer.PointerId != _pressedPointerId)
        {
            return;
        }

        await FinishCompactPressAsync(allowOpen: true);
        e.Handled = true;
    }

    private async void CompactTile_PointerCanceled(object sender, PointerRoutedEventArgs e) => await FinishCompactPressAsync(allowOpen: false);

    private async void CompactTile_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_pressActive && !_nativeMouseCapture)
        {
            await FinishCompactPressAsync(allowOpen: false);
        }
    }

    private void LongPressTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        => TryStartWidgetDrag();

    private void TryStartWidgetDrag()
    {
        if (_pressActive && !_widgetDragging && _longPressGesture.Elapse() == LongPressResult.StartDrag)
        {
            _widgetDragging = true;
            if (!_draggingExpanded && !IsContained && _definition.PlacementMode != OrganizerPlacementMode.Station)
            {
                _desktopLayer?.SetExpanded(true, stayTopmost: true);
                _widgetDragTopmost = true;
            }
            _dragStartedAt = Stopwatch.GetTimestamp();
            _dragInputCount = 0;
            _dragCommitCount = 0;
            _dragRenderTickCount = 0;
            _hasLastDragCursor = false;
            UpdateWidgetDragFromCursor();
        }
    }

    private async Task FinishCompactPressAsync(bool allowOpen)
    {
        if (!_pressActive) return;
        LongPressResult releaseResult = _longPressGesture.Release();
        bool wasDragging = releaseResult == LongPressResult.FinishDrag;
        bool wasExpandedDrag = _draggingExpanded;
        bool canOpen = allowOpen && releaseResult == LongPressResult.Open;
        int offeredFrames = 0;
        int committedFrames = 0;
        int renderTicks = 0;
        bool clockBoosted = false;
        double dragDurationMs = 0;
        if (wasDragging)
        {
            UpdateWidgetDragFromCursor();
            offeredFrames = _dragInputCount;
            committedFrames = _dragCommitCount;
            renderTicks = _dragRenderTickCount;
            clockBoosted = _dragClockBoosted;
            dragDurationMs = Stopwatch.GetElapsedTime(_dragStartedAt).TotalMilliseconds;
        }
        ResetCompactPress();
        if (wasDragging)
        {
            AppLogger.Info($"组件拖动 {dragDurationMs:0}ms，鼠标消息 {offeredFrames} 次，合成节拍 {renderTicks} 次，移动提交 {committedFrames} 次，时钟保活={clockBoosted}。");
            if (!wasExpandedDrag && NativeMethods.GetCursorPos(out NativeMethods.POINT dropPoint))
            {
                try
                {
                    if (await _host.TryContainDraggedOrganizerAsync(this, dropPoint))
                    {
                        _desktopLayer?.Reattach();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("收纳窗拖入中转站失败。", ex);
                    _compactBounds = _definition.PlacementMode == OrganizerPlacementMode.Positioned
                        ? _positionedDragOriginBounds
                        : _pressWindowBounds;
                    ApplyBounds(_compactBounds, show: true);
                    ShowMessage(AppStrings.Format("OrganizerStationDropError", ex.Message), InfoBarSeverity.Error);
                    _desktopLayer?.Reattach();
                    return;
                }
            }
            if (wasExpandedDrag && ShouldRememberExpandedPosition())
            {
                CaptureExpandedPosition();
                await SaveStateAsync();
            }
            else if (_definition.PlacementMode == OrganizerPlacementMode.Station && wasExpandedDrag)
            {
                DisplayInfo display = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
                _stationDisplay = display;
                _definition.Position = DisplayPlacementService.CaptureStationPosition(display, _definition.DockEdge, _dragCurrentBounds);
                _compactBounds = DisplayPlacementService.CalculateStationAnchor(display, _definition.DockEdge, _definition.Position);
                await SaveStateAsync();
                ResetStationPointerDelay();
            }
            else if (_definition.PlacementMode == OrganizerPlacementMode.Positioned)
            {
                if (!wasExpandedDrag)
                {
                    DesktopGridPlacement? placement = _host.FindNearestPositionedPlacement(_definition.Id, _compactBounds);
                    if (placement is null)
                    {
                        _compactBounds = _positionedDragOriginBounds;
                        ApplyBounds(_compactBounds, show: true);
                        ShowMessage(AppStrings.Get("DragReturnToGrid"), InfoBarSeverity.Warning);
                    }
                    else
                    {
                        MoveToPositionedPlacement(placement.Bounds, placement.CompactScale);
                        _definition.Position = DisplayPlacementService.Capture(_compactBounds, _hwnd);
                        await SaveStateAsync();
                    }
                }
            }
            else if (!wasExpandedDrag)
            {
                _definition.Position = DisplayPlacementService.Capture(_compactBounds, _hwnd);
                await SaveStateAsync();
            }
            _desktopLayer?.Reattach();
        }
        else if (canOpen)
        {
            try
            {
                await ExpandAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Expand from compact pointer failed", ex);
                _expanded = false;
                _animating = false;
            }
        }
    }

    private void ResetCompactPress()
    {
        _longPressTimer.Stop();
        _longPressGesture.Reset();
        _pressActive = false;
        StopDragClock();
        if (_pressedPointerId != 0)
        {
            CompactTile.ReleasePointerCaptures();
        }
        if (_nativeMouseCapture)
        {
            _ = NativeMethods.ReleaseCapture();
            _nativeMouseCapture = false;
        }
        _pressedPointerId = 0;
        _widgetDragging = false;
        if (_widgetDragTopmost)
        {
            _desktopLayer?.SetExpanded(false);
            _widgetDragTopmost = false;
        }
        _draggingExpanded = false;
        _dragDisplay = null;
        _dragAlignmentInsets = null;
        ClearWindowAlignment();
        _pressStartedAt = 0;
        _hasLastDragCursor = false;
        CompactTile.Scale = Vector3.One;
        GetExpandedCompositionVisual().Scale = Vector3.One;
    }

    private void ClearWindowAlignment()
    {
        _windowAlignmentState = default;
        _windowAlignmentGuide?.Hide();
    }

    private bool TryGetCompactAlignmentInsets(
        NativeMethods.RECT windowBounds,
        out WindowAlignmentInsets insets)
    {
        if (TryGetCompactAlignmentFrame(out NativeMethods.RECT frame))
        {
            insets = WindowAlignmentInsets.From(windowBounds, frame);
            return true;
        }
        insets = default;
        return false;
    }

    private bool TryGetCompactAlignmentFrame(out NativeMethods.RECT bounds)
    {
        bounds = default;
        double width = CompactThumbnailHost.ActualWidth;
        double height = CompactThumbnailHost.ActualHeight;
        double scale = WindowRoot.XamlRoot?.RasterizationScale ?? 0;
        if (_hwnd == IntPtr.Zero || !CompactThumbnailHost.IsLoaded ||
            !double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(scale) ||
            width <= 0 || height <= 0 || scale <= 0) return false;

        Point origin;
        try
        {
            origin = CompactThumbnailHost.TransformToVisual(WindowRoot).TransformPoint(new Point());
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var clientOrigin = new NativeMethods.POINT();
        if (!NativeMethods.ClientToScreen(_hwnd, ref clientOrigin)) return false;
        bounds = new NativeMethods.RECT
        {
            Left = clientOrigin.X + (int)Math.Round(origin.X * scale),
            Top = clientOrigin.Y + (int)Math.Round(origin.Y * scale),
            Right = clientOrigin.X + (int)Math.Round((origin.X + width) * scale),
            Bottom = clientOrigin.Y + (int)Math.Round((origin.Y + height) * scale)
        };
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private void StartDragClock()
    {
        if (!_dragRenderingSubscribed)
        {
            CompositionTarget.Rendering += DragRendering;
            _dragRenderingSubscribed = true;
        }
        if (_nativeMouseCapture && !_dragClockBoosted && NativeMethods.TrySetCompositorClockBoost(true))
        {
            _dragClockBoosted = true;
        }
    }

    private void StopDragClock()
    {
        if (_dragRenderingSubscribed)
        {
            CompositionTarget.Rendering -= DragRendering;
            _dragRenderingSubscribed = false;
        }
        if (_dragClockBoosted)
        {
            _ = NativeMethods.TrySetCompositorClockBoost(false);
            _dragClockBoosted = false;
        }
    }

    private async void CompactListRow_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!IsCompactList || sender is not FrameworkElement host) return;
        e.Handled = true;
        await OpenTaggedItemAsync(host, doubleTap: true);
    }

    private async void Item_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement host) return;
        e.Handled = true;
        await OpenTaggedItemAsync(host, doubleTap: true);
    }

    private async Task OpenTaggedItemAsync(FrameworkElement host, bool doubleTap = false)
    {
        if (_shellDropFinalizing || host.Tag is not string relativeName || _draggedRelativeName is not null) return;
        WidgetItem? item = _items.FirstOrDefault(candidate => candidate.RelativeName.Equals(relativeName, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;

        if (item is { Kind: WidgetItemKind.Note, NoteId: Guid noteId })
        {
            _host.OpenNote(_definition.Id, noteId);
            return;
        }

        if (item.Kind == WidgetItemKind.PortableNote)
        {
            if (doubleTap && !IsCompactList) return;
            await _host.OpenExternalNoteAsync(item.FullPath);
            return;
        }

        if (item.Kind == WidgetItemKind.PortableTodo)
        {
            if (doubleTap && !IsCompactList) return;
            await _host.OpenExternalTodoAsync(item.FullPath);
            return;
        }

        if (item is { Kind: WidgetItemKind.Organizer, OrganizerId: Guid organizerId })
        {
            if (!TryGetElementScreenBounds(host, out NativeMethods.RECT anchor) &&
                NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
            {
                anchor = new NativeMethods.RECT
                {
                    Left = cursor.X,
                    Top = cursor.Y,
                    Right = cursor.X + 1,
                    Bottom = cursor.Y + 1
                };
            }
            await _host.OpenContainedOrganizerAsync(organizerId, anchor);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法打开：{item.FullPath}", ex);
            ShowMessage(AppStrings.Format("OpenItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
    }

    private void Item_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if (!_expanded || _animating || _shellDragActive || _shellDropFinalizing ||
            _shellContextMenuOpen ||
            sender is not Border { Tag: string relativeName } host)
        {
            return;
        }

        WidgetItem? item = _items.FirstOrDefault(candidate =>
            candidate.RelativeName.Equals(relativeName, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        if (item.Kind == WidgetItemKind.Organizer) return;
        if (item is { Kind: WidgetItemKind.Note, NoteId: Guid noteId })
        {
            ShowNoteContextMenu(host, noteId);
            return;
        }
        ShowFileContextMenu(host, item);
    }

    private bool TryGetElementScreenBounds(FrameworkElement element, out NativeMethods.RECT bounds)
    {
        bounds = default;
        if (_hwnd == IntPtr.Zero || element.XamlRoot is null || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        Point origin;
        try
        {
            origin = element.TransformToVisual(WindowRoot).TransformPoint(new Point());
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        double scale = Math.Max(1, element.XamlRoot.RasterizationScale);
        var clientOrigin = new NativeMethods.POINT();
        if (!NativeMethods.ClientToScreen(_hwnd, ref clientOrigin)) return false;
        bounds = new NativeMethods.RECT
        {
            Left = clientOrigin.X + (int)Math.Round(origin.X * scale),
            Top = clientOrigin.Y + (int)Math.Round(origin.Y * scale),
            Right = clientOrigin.X + (int)Math.Round((origin.X + element.ActualWidth) * scale),
            Bottom = clientOrigin.Y + (int)Math.Round((origin.Y + element.ActualHeight) * scale)
        };
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private void ShowFileContextMenu(FrameworkElement host, WidgetItem item)
    {
        var rename = new MenuFlyoutItem { Text = AppStrings.Get("ContextRenameFile") };
        var copy = new MenuFlyoutItem { Text = AppStrings.Get("ContextCopyFile") };
        var cut = new MenuFlyoutItem { Text = AppStrings.Get("Cut") };
        var delete = new MenuFlyoutItem { Text = AppStrings.Get("Delete") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(rename, "RenameFileMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(copy, "CopyFileMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(cut, "CutFileMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(delete, "DeleteFileMenuItem");
        rename.Click += async (_, _) => await ShowRenameFileDialogAsync(item);
        copy.Click += async (_, _) => await CopyFileItemAsync(item);
        cut.Click += async (_, _) => await CutFileItemAsync(item);
        delete.Click += async (_, _) => await DeleteFileItemAsync(item);
        MenuFlyout flyout = CreateOrganizerContextMenu();
        flyout.Items.Add(rename);
        flyout.Items.Add(copy);
        flyout.Items.Add(cut);
        flyout.Items.Add(delete);
        _shellContextMenuOpen = true;
        flyout.Closed += (_, _) =>
        {
            _shellContextMenuOpen = false;
        };
        try
        {
            flyout.ShowAt(host);
        }
        catch (Exception ex)
        {
            _shellContextMenuOpen = false;
            RestoreContextMenuHost();
            AppLogger.Error($"无法打开文件项目菜单：{item.FullPath}", ex);
            ShowMessage(AppStrings.Format("FileMenuErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
    }

    private MenuFlyout CreateOrganizerContextMenu()
    {
        var flyout = new MenuFlyout
        {
            MenuFlyoutPresenterStyle = (Style)WindowRoot.Resources["OrganizerMenuPresenterStyle"],
            ShouldConstrainToRootBounds = true
        };
        flyout.Opening += ContextMenu_Opening;
        flyout.Opened += ContextMenu_Opened;
        flyout.Closed += ContextMenu_Closed;
        return flyout;
    }

    private async Task CopyFileItemAsync(WidgetItem item)
    {
        try
        {
            if (IsDocumentItem(item) && !await _host.FlushPortableWindowAsync(item.FullPath))
                throw new IOException(AppStrings.Get(item.Kind == WidgetItemKind.PortableTodo
                    ? "TodoDragSaveFailed"
                    : "NoteDragSaveFailed"));
            ShellDragService.SetCopyClipboard(item.FullPath);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法复制文件项目：{item.FullPath}", ex);
            ShowMessage(AppStrings.Format("CopyItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
    }

    private async Task CutFileItemAsync(WidgetItem item)
    {
        try
        {
            if (IsDocumentItem(item) && !await _host.FlushPortableWindowAsync(item.FullPath))
                throw new IOException(AppStrings.Get(item.Kind == WidgetItemKind.PortableTodo
                    ? "TodoDragSaveFailed"
                    : "NoteDragSaveFailed"));
            ShellDragService.SetCutClipboard(item.FullPath);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法剪切文件项目：{item.FullPath}", ex);
            ShowMessage(AppStrings.Format("CutItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
    }

    private async Task ShowRenameFileDialogAsync(WidgetItem item)
    {
        bool station = _definition.PlacementMode == OrganizerPlacementMode.Station;
        bool directory = Directory.Exists(item.FullPath);
        string initialName = directory
            ? Path.GetFileName(item.FullPath)
            : Path.GetFileNameWithoutExtension(item.FullPath);
        string acceptedName = initialName;
        _overlayOpenCount++;
        if (station) _desktopLayer?.SetInputActivation(true);
        try
        {
            DisplayInfo display = _stationDisplay ??= DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
            bool accepted = await OwnedDialogWindow.ShowTextInputAsync(
                _hwnd,
                display,
                _host,
                AppStrings.Get("RenameItemTitle"),
                initialName,
                AppStrings.Get("Rename"),
                AppStrings.Get("Cancel"),
                candidate =>
                {
                    acceptedName = candidate.Trim();
                    return TryGetRenameTarget(item, acceptedName, out _);
                });
            if (accepted) await RenameFileItemAsync(item, acceptedName);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法重命名文件项目：{item.FullPath}", ex);
            ShowMessage(AppStrings.Format("RenameItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _overlayOpenCount = Math.Max(0, _overlayOpenCount - 1);
            if (station) _desktopLayer?.SetInputActivation(false);
            ResetStationPointerDelay();
        }
    }

    private string? TryGetRenameTarget(WidgetItem item, string candidate, out string targetPath)
    {
        targetPath = item.FullPath;
        bool directory = Directory.Exists(item.FullPath);
        string extension = directory ? string.Empty : Path.GetExtension(item.FullPath);
        if (candidate.Length == 0) return AppStrings.Get("ItemNameRequired");
        if (candidate.Length + extension.Length > 255 ||
            candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            candidate.EndsWith('.') || candidate.EndsWith(' '))
        {
            return AppStrings.Get("FolderNameInvalidError");
        }
        if (NoteStore.IsReservedDeviceName(candidate.Split('.', 2)[0]))
            return AppStrings.Get("FolderNameReservedError");
        string directoryPath = Path.GetDirectoryName(item.FullPath) ?? _storage.ItemsRoot;
        targetPath = Path.Combine(directoryPath, candidate + extension);
        if (!targetPath.Equals(item.FullPath, StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(targetPath) || Directory.Exists(targetPath)))
        {
            return AppStrings.Get("ItemNameDuplicate");
        }
        return null;
    }

    private async Task RenameFileItemAsync(WidgetItem item, string candidate)
    {
        string? error = TryGetRenameTarget(item, candidate, out string targetPath);
        if (error is not null) throw new InvalidOperationException(error);
        if (targetPath.Equals(item.FullPath, StringComparison.Ordinal)) return;
        if (item.Kind == WidgetItemKind.PortableNote)
        {
            await _host.RenameExternalNoteAsync(item.FullPath, candidate);
            return;
        }
        if (item.Kind == WidgetItemKind.PortableTodo)
        {
            await _host.RenameExternalTodoAsync(item.FullPath, candidate);
            return;
        }

        bool directory = Directory.Exists(item.FullPath);
        string oldRelativeName = item.RelativeName;
        string newRelativeName = Path.GetFileName(targetPath);
        int orderIndex = _definition.ItemOrder.FindIndex(value =>
            value.Equals(oldRelativeName, StringComparison.OrdinalIgnoreCase));
        if (directory) Directory.Move(item.FullPath, targetPath);
        else File.Move(item.FullPath, targetPath);
        if (orderIndex >= 0) _definition.ItemOrder[orderIndex] = newRelativeName;
        try
        {
            if (orderIndex >= 0) await _host.SaveStateAsync();
        }
        catch
        {
            try
            {
                if (directory) Directory.Move(targetPath, item.FullPath);
                else File.Move(targetPath, item.FullPath);
                if (orderIndex >= 0) _definition.ItemOrder[orderIndex] = oldRelativeName;
            }
            catch (Exception rollbackError)
            {
                AppLogger.Error($"无法回滚文件项目重命名：{targetPath}", rollbackError);
            }
            throw;
        }
        await RefreshCatalogAsync(notifyUnsupported: false);
    }

    private async Task DeleteFileItemAsync(WidgetItem item)
    {
        try
        {
            if (IsDocumentItem(item) && !await _host.FlushPortableWindowAsync(item.FullPath))
                throw new IOException(AppStrings.Get(item.Kind == WidgetItemKind.PortableTodo
                    ? "TodoDragSaveFailed"
                    : "NoteDragSaveFailed"));
            await Task.Run(() =>
            {
                if (Directory.Exists(item.FullPath))
                {
                    FileSystem.DeleteDirectory(
                        item.FullPath,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin,
                        UICancelOption.ThrowException);
                }
                else if (File.Exists(item.FullPath))
                {
                    FileSystem.DeleteFile(
                        item.FullPath,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin,
                        UICancelOption.ThrowException);
                }
            });
            if (IsDocumentItem(item)) _host.ClosePortableWindowWithoutSave(item.FullPath);
            StartWatcher();
            await RefreshCatalogAsync(notifyUnsupported: false);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法将文件项目移入回收站：{item.FullPath}", ex);
            ShowMessage(AppStrings.Format("DeleteItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
    }

    private void ShowNoteContextMenu(FrameworkElement host, Guid noteId)
    {
        var rename = new MenuFlyoutItem { Text = AppStrings.Get("ContextRenameNote") };
        var delete = new MenuFlyoutItem { Text = AppStrings.Get("ContextDeleteNote") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(rename, "RenameNoteMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(delete, "DeleteNoteMenuItem");
        rename.Click += async (_, _) => await ShowRenameNoteDialogAsync(noteId);
        delete.Click += async (_, _) => await ShowDeleteNoteDialogAsync(noteId);
        MenuFlyout flyout = CreateOrganizerContextMenu();
        flyout.Items.Add(rename);
        flyout.Items.Add(delete);
        _shellContextMenuOpen = true;
        flyout.Closed += (_, _) =>
        {
            _shellContextMenuOpen = false;
        };
        try
        {
            flyout.ShowAt(host);
        }
        catch
        {
            _shellContextMenuOpen = false;
            RestoreContextMenuHost();
            throw;
        }
    }

    private async Task ShowRenameNoteDialogAsync(Guid noteId)
    {
        NoteDefinition? note = _definition.Notes.FirstOrDefault(item => item.Id == noteId);
        if (note is null) return;
        bool station = _definition.PlacementMode == OrganizerPlacementMode.Station;
        _overlayOpenCount++;
        if (station) _desktopLayer?.SetInputActivation(true);
        string acceptedName = note.Name;
        try
        {
            DisplayInfo display = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
            bool accepted = await OwnedDialogWindow.ShowTextInputAsync(
                _hwnd,
                display,
                _host,
                AppStrings.Get("NoteRenameTitle"),
                note.Name,
                AppStrings.Get("Rename"),
                AppStrings.Get("Cancel"),
                candidate =>
                {
                    acceptedName = candidate.Trim();
                    if (acceptedName.Length == 0) return AppStrings.Get("NoteNameRequired");
                    return OrganizerNoteRules.IsNameAvailable(
                        _definition.Notes.Where(item => item.Id != noteId).Select(item => item.Name),
                        acceptedName) ? null : AppStrings.Get("NoteNameDuplicate");
                });
            if (accepted) await _host.RenameNoteAsync(_definition.Id, noteId, acceptedName);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法重命名便签。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _overlayOpenCount = Math.Max(0, _overlayOpenCount - 1);
            if (station) _desktopLayer?.SetInputActivation(false);
            ResetStationPointerDelay();
        }
    }

    private async Task ShowDeleteNoteDialogAsync(Guid noteId)
    {
        NoteDefinition? note = _definition.Notes.FirstOrDefault(item => item.Id == noteId);
        if (note is null) return;
        bool station = _definition.PlacementMode == OrganizerPlacementMode.Station;
        _overlayOpenCount++;
        if (station) _desktopLayer?.SetInputActivation(true);
        try
        {
            DisplayInfo display = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
            bool accepted = await OwnedDialogWindow.ShowConfirmationAsync(
                _hwnd,
                display,
                _host,
                AppStrings.Get("NoteDeleteTitle"),
                AppStrings.Format("NoteDeleteMessageFormat", note.Name),
                AppStrings.Get("Delete"),
                AppStrings.Get("Cancel"));
            if (accepted) await _host.DeleteNoteAsync(_definition.Id, noteId);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法删除便签。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _overlayOpenCount = Math.Max(0, _overlayOpenCount - 1);
            if (station) _desktopLayer?.SetInputActivation(false);
            ResetStationPointerDelay();
        }
    }

    private void Item_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse &&
            sender is Border host && !ReferenceEquals(host, _itemDragHost))
        {
            SetItemInteractionVisual(host, pressed: false);
        }
    }

    private void Item_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border host && !ReferenceEquals(host, _itemDragHost))
        {
            ResetItemInteractionVisual(host);
        }
    }

    private void Item_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_itemReorderProbeRunning || !_expanded || _animating || _shellDragActive || _shellDropFinalizing || _itemReorderSession is not null ||
            sender is not Border { Tag: string relativeName } host)
        {
            if (_itemDragTraceEnabled)
                AppLogger.Info($"换序跟踪：忽略按下 probe={_itemReorderProbeRunning} expanded={_expanded} animating={_animating} shell={_shellDragActive} finalizing={_shellDropFinalizing} session={_itemReorderSession is not null}。");
            return;
        }
        PointerPoint point = e.GetCurrentPoint(ItemsRepeater);
        if (!point.Properties.IsLeftButtonPressed) return;
        int sourceIndex = IndexOfItem(relativeName);
        if (sourceIndex < 0) return;
        SetItemInteractionVisual(host, pressed: true);
        if (_itemDragTraceEnabled) AppLogger.Info($"换序跟踪：按下 source={sourceIndex} pointer={e.Pointer.PointerId}。");

        NormalizeItemMotionsToLayout("NewPress");
        Point hostOrigin = host.TransformToVisual(ItemsRepeater).TransformPoint(new Point());
        _itemReorderSession = new ItemReorderSession(
            relativeName,
            sourceIndex,
            point.Position,
            new Point(point.Position.X - hostOrigin.X, point.Position.Y - hostOrigin.Y));
        _itemDragPressedAt = Stopwatch.GetTimestamp();
        _itemDragHost = host;
        _itemDragPointerId = e.Pointer.PointerId;
        _itemDragPointerType = e.Pointer.PointerDeviceType;
        _itemDragLastPointerPoint = e.GetCurrentPoint(host);
        if (!host.CapturePointer(e.Pointer))
        {
            CancelItemReorder();
            return;
        }
        if (_itemDragPointerType != PointerDeviceType.Mouse)
        {
            _itemTouchHoldTimer.Stop();
            _itemTouchHoldTimer.Start();
        }
    }

    private void Item_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_itemReorderSession is not { } session || e.Pointer.PointerId != _itemDragPointerId) return;
        if (sender is Border host) _itemDragLastPointerPoint = e.GetCurrentPoint(host);
        Point pointer = _itemDragPointerType == PointerDeviceType.Mouse && TryGetMousePointerContent(out Point mousePointer)
            ? mousePointer
            : e.GetCurrentPoint(ItemsRepeater).Position;
        session.Track(pointer);
        if (!session.IsActive)
        {
            if (_itemDragPointerType == PointerDeviceType.Mouse)
            {
                if (!session.TryActivate(pointer)) return;
                StartItemReorder();
                e.Handled = true;
                return;
            }
            else
            {
                double x = pointer.X - session.PressPointerContent.X;
                double y = pointer.Y - session.PressPointerContent.Y;
                if (x * x + y * y >= ItemReorderSession.ActivationThresholdDip * ItemReorderSession.ActivationThresholdDip)
                {
                    CancelItemReorder();
                }
                return;
            }
        }
        e.Handled = true;
    }

    private async void Item_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_itemReorderSession is not { } session || e.Pointer.PointerId != _itemDragPointerId) return;
        if (sender is Border releasedHost)
        {
            Point position = e.GetCurrentPoint(releasedHost).Position;
            bool pointerInside = position.X >= 0 && position.Y >= 0 &&
                position.X <= releasedHost.ActualWidth && position.Y <= releasedHost.ActualHeight;
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && pointerInside)
                SetItemInteractionVisual(releasedHost, pressed: false);
            else ResetItemInteractionVisual(releasedHost);
        }
        if (_itemDragTraceEnabled)
            AppLogger.Info($"换序跟踪：释放 source={session.SourceIndex} target={session.TargetIndex} state={session.State}。");
        if (session.IsActive && !session.IsNativeDragging)
        {
            e.Handled = true;
            await CommitItemReorderAsync(session);
        }
        else if (!session.IsNativeDragging)
        {
            WidgetItem? item = _items.FirstOrDefault(candidate =>
                candidate.RelativeName.Equals(session.RelativeName, StringComparison.OrdinalIgnoreCase));
            CancelItemReorder();
            if (item is { Kind: WidgetItemKind.Organizer, OrganizerId: Guid } && sender is FrameworkElement host)
            {
                await OpenTaggedItemAsync(host);
                e.Handled = true;
            }
            else if (!IsCompactList && item is { Kind: WidgetItemKind.Note, NoteId: Guid noteId })
            {
                _host.OpenNote(_definition.Id, noteId);
                e.Handled = true;
            }
            else if (!IsCompactList && item?.Kind == WidgetItemKind.PortableNote)
            {
                await _host.OpenExternalNoteAsync(item.FullPath);
                e.Handled = true;
            }
            else if (!IsCompactList && item?.Kind == WidgetItemKind.PortableTodo)
            {
                await _host.OpenExternalTodoAsync(item.FullPath);
                e.Handled = true;
            }
        }
        else e.Handled = true;
    }

    private void Item_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border host) ResetItemInteractionVisual(host);
        if (_itemDragTraceEnabled) AppLogger.Info("换序跟踪：指针取消。");
        if (_itemReorderSession is null || _itemDragPointerId == 0 || e.Pointer.PointerId != _itemDragPointerId) return;
        if (_itemDragLanding || _shellPromotionPending || _shellDragActive) return;
        CancelItemReorder();
    }

    private void Item_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border host) ResetItemInteractionVisual(host);
        if (_itemDragTraceEnabled) AppLogger.Info($"换序跟踪：捕获丢失 pointer={_itemDragPointerId}。");
        if (_itemDragPointerId != 0) CancelItemReorder();
    }

    private void ItemTouchHoldTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_itemReorderSession is not { IsActive: false } session || _itemDragHost is null) return;
        session.Activate(session.LatestPointerContent);
        StartItemReorder();
    }

    private void StartItemReorder()
    {
        if (_itemReorderSession is not { IsActive: true } session || _itemDragHost is null) return;
        BeginNoteDragPreparation(session);
        _itemTouchHoldTimer.Stop();
        ResetGapTransitionState();
        _itemReorderUsesAnimations = UseCustomAnimations;
        _gapTransitionRevision = session.PreviewRevision;
        _gapMaterializedRevision = session.PreviewRevision;
        _itemDragIdentityWarnings.Clear();
        _itemDragLanding = false;
        ResetItemContentTransform(_itemDragHost);
        _itemDragHost.Background = _draggedItemBrush;
        Canvas.SetZIndex(_itemDragHost, 1000);
        _itemDragHost.CenterPoint = new Vector3((float)(_itemDragHost.ActualWidth / 2), (float)(_itemDragHost.ActualHeight / 2), 0);
        ItemMotionState draggedMotion = GetItemMotion(_itemDragHost);
        draggedMotion.ScaleTarget = _itemReorderUsesAnimations ? new Vector3(1.06f, 1.06f, 1) : Vector3.One;
        draggedMotion.Scale = draggedMotion.ScaleTarget;
        draggedMotion.ScaleVelocity = Vector3.Zero;
        _itemDragHost.Scale = draggedMotion.Scale;
        _itemDragLastFrame = Stopwatch.GetTimestamp();
        if (!_itemDragRenderingSubscribed)
        {
            CompositionTarget.Rendering += ItemDragRendering;
            _itemDragRenderingSubscribed = true;
        }
        TrySetItemDragClockBoost(true);
        if (_itemDragPointerType == PointerDeviceType.Mouse) StartItemDragBoundaryHook();
        ApplyAllProvisionalItemVisuals(animate: false);
    }

    private void ItemDragRendering(object? sender, object args)
    {
        if (_itemReorderSession is not { IsActive: true } session || _itemDragHost is null) return;

        long now = Stopwatch.GetTimestamp();
        double seconds = _itemDragLastFrame == 0 ? 0 : Math.Clamp(Stopwatch.GetElapsedTime(_itemDragLastFrame, now).TotalSeconds, 0, 1d / 20);
        _itemDragLastFrame = now;
        if (_gapTransitionCompleting)
        {
            if (!StepGapTransition(session, now)) _gapTransitionSettled?.TrySetResult();
            return;
        }
        if (_itemDragLanding)
        {
            bool settled = StepItemMotions(seconds, directDragHost: null);
            if (settled) _itemMotionSettled?.TrySetResult();
            return;
        }

        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        Point viewportOrigin = ItemsScrollView.TransformToVisual(WindowRoot).TransformPoint(new Point());
        Point pointerInWindow;
        if (_itemDragPointerType == PointerDeviceType.Mouse)
        {
            if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor) ||
                !NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT window)) return;
            if (!DragBoundaryMath.Contains(_itemDragBoundaryBounds, cursor))
            {
                TryPostItemExternalDragPromotion();
                return;
            }
            pointerInWindow = new((cursor.X - window.Left) / scale, (cursor.Y - window.Top) / scale);
        }
        else
        {
            pointerInWindow = new(
                viewportOrigin.X + session.LatestPointerContent.X - ItemsScrollView.HorizontalOffset,
                viewportOrigin.Y + session.LatestPointerContent.Y - ItemsScrollView.VerticalOffset);
        }
        Point pointerInViewport = new(pointerInWindow.X - viewportOrigin.X, pointerInWindow.Y - viewportOrigin.Y);
        ApplyItemDragAutoScroll(pointerInViewport.Y, seconds);
        Point pointerContent = new(
            pointerInViewport.X + ItemsScrollView.HorizontalOffset,
            pointerInViewport.Y + ItemsScrollView.VerticalOffset);
        session.UpdateTarget(
            pointerContent,
            Math.Max(1, _itemDragHost.Width),
            Math.Max(1, _itemDragHost.Height),
            GetItemLayoutGapDip(),
            GetItemLayoutColumnCount(),
            _items.Count);
        bool transitionRunning = StepGapTransition(session, now);
        if (!transitionRunning) EnsureGapPreviewMaterialized(session);

        ItemMotionState draggedMotion = GetItemMotion(_itemDragHost);
        Vector3 nextTranslation = new(
            (float)(pointerContent.X - session.PressPointerContent.X),
            (float)(pointerContent.Y - session.PressPointerContent.Y),
            16);
        if (seconds > 0) draggedMotion.TranslationVelocity = (nextTranslation - draggedMotion.Translation) / (float)seconds;
        draggedMotion.Translation = nextTranslation;
        draggedMotion.TranslationTarget = nextTranslation;
        _itemDragHost.Translation = nextTranslation;
    }

    private bool TryGetMousePointerContent(out Point pointerContent)
    {
        pointerContent = default;
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT window)) return false;

        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        Point repeaterOrigin = ItemsRepeater.TransformToVisual(WindowRoot).TransformPoint(new Point());
        pointerContent = new Point(
            (cursor.X - window.Left) / scale - repeaterOrigin.X,
            (cursor.Y - window.Top) / scale - repeaterOrigin.Y);
        return true;
    }

    private void StartItemDragBoundaryHook()
    {
        StopItemDragBoundaryHook();
        if (!NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT window)) return;
        _itemDragBoundaryBounds = window;
        DragMessageRelay.Reset(ref _itemDragOleMouseMovePending, ref _itemDragLastOleMouseMoveForwardedAt);
        Volatile.Write(ref _itemDragBoundaryPromotionPosted, 0);
        Interlocked.Exchange(ref _itemDragBoundaryDetectedAt, 0);

        Volatile.Write(ref _itemDragBoundaryArmed, 1);
        if (!EnsureItemDragBoundaryHook())
        {
            AppLogger.Info("项目外拖钩子不可用，将使用渲染循环边界检测。");
            return;
        }
        if (_itemDragTraceEnabled)
            AppLogger.Info($"换序跟踪：边界钩子 hwnd=0x{_itemDragBoundaryHook.ToInt64():X} " +
                $"bounds={_itemDragBoundaryBounds.Left},{_itemDragBoundaryBounds.Top},{_itemDragBoundaryBounds.Right},{_itemDragBoundaryBounds.Bottom}。");
    }

    private bool EnsureItemDragBoundaryHook()
    {
        Thread? existingThread = _itemDragBoundaryHookThread;
        if (existingThread is not null && existingThread.IsAlive && _itemDragBoundaryHook != IntPtr.Zero) return true;
        if (existingThread is not null)
        {
            ShutdownItemDragBoundaryHook();
            if (_itemDragBoundaryHookThread is not null)
            {
                AppLogger.Error("上一个项目外拖边界钩子仍在退出，本次不再重复安装。");
                return false;
            }
        }

        _itemDragBoundaryHookReady.Reset();
        _itemDragBoundaryHookThread = new Thread(ItemDragBoundaryHookThreadMain)
        {
            IsBackground = true,
            Name = "TuckPane.ItemDragBoundary"
        };
        _itemDragBoundaryHookThread.Start();
        if (!_itemDragBoundaryHookReady.Wait(500) || _itemDragBoundaryHook == IntPtr.Zero)
        {
            AppLogger.Error("无法安装项目外拖边界钩子。");
            ShutdownItemDragBoundaryHook();
            return false;
        }
        return true;
    }

    private void ItemDragBoundaryHookThreadMain()
    {
        try
        {
            Volatile.Write(ref _itemDragBoundaryHookThreadId, NativeMethods.GetCurrentThreadId());
            _itemDragBoundaryHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                _itemDragBoundaryHookProc,
                NativeMethods.GetModuleHandle(null),
                0);
            _itemDragBoundaryHookReady.Set();
            if (_itemDragBoundaryHook == IntPtr.Zero) return;
            while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        }
        finally
        {
            IntPtr hook = Interlocked.Exchange(ref _itemDragBoundaryHook, IntPtr.Zero);
            if (hook != IntPtr.Zero) _ = NativeMethods.UnhookWindowsHookEx(hook);
            Volatile.Write(ref _itemDragBoundaryHookThreadId, 0);
            _itemDragBoundaryHookReady.Set();
        }
    }

    private IntPtr ItemDragBoundaryHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        int mouseMessage = wParam.ToInt32();
        NativeMethods.MSLLHOOKSTRUCT? hookData = code >= 0 &&
            mouseMessage is NativeMethods.WM_MOUSEMOVE or NativeMethods.WM_LBUTTONUP
                ? Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam)
                : null;
        if (code >= 0 && mouseMessage == NativeMethods.WM_MOUSEMOVE &&
            Volatile.Read(ref _itemDragBoundaryArmed) != 0 &&
            hookData is { } data &&
            (data.Point.X < _itemDragBoundaryBounds.Left || data.Point.X >= _itemDragBoundaryBounds.Right ||
             data.Point.Y < _itemDragBoundaryBounds.Top || data.Point.Y >= _itemDragBoundaryBounds.Bottom))
        {
            TryPostItemExternalDragPromotion();
        }

        bool shouldForwardToOle = mouseMessage == NativeMethods.WM_LBUTTONUP ||
            mouseMessage == NativeMethods.WM_MOUSEMOVE && Volatile.Read(ref _shellDragActive) && ShouldForwardOleMouseMove();
        if (code >= 0 && Volatile.Read(ref _itemDragBoundaryPromotionPosted) != 0 && shouldForwardToOle &&
            hookData.HasValue)
        {
            UIntPtr keys = mouseMessage == NativeMethods.WM_MOUSEMOVE
                ? new UIntPtr(NativeMethods.MK_LBUTTON)
                : UIntPtr.Zero;
            bool posted = NativeMethods.PostMessage(_hwnd, (uint)mouseMessage, keys, IntPtr.Zero);
            if (!posted && mouseMessage == NativeMethods.WM_MOUSEMOVE)
            {
                DragMessageRelay.Complete(ref _itemDragOleMouseMovePending);
            }
        }
        return NativeMethods.CallNextHookEx(_itemDragBoundaryHook, code, wParam, lParam);
    }

    private void TryPostItemExternalDragPromotion()
    {
        if (Volatile.Read(ref _itemDragBoundaryArmed) == 0 ||
            Interlocked.Exchange(ref _itemDragBoundaryPromotionPosted, 1) != 0) return;
        if (_itemDragTraceEnabled) AppLogger.Info("换序跟踪：命中画布边界。");
        Interlocked.Exchange(ref _itemDragBoundaryDetectedAt, Stopwatch.GetTimestamp());
        if (NativeMethods.PostMessage(
                _hwnd,
                NativeMethods.WM_APP_START_ITEM_EXTERNAL_DRAG,
                UIntPtr.Zero,
                IntPtr.Zero)) return;
        Volatile.Write(ref _itemDragBoundaryPromotionPosted, 0);
        AppLogger.Error("无法投递项目外拖启动消息。");
    }

    private bool ShouldForwardOleMouseMove()
    {
        return DragMessageRelay.TryReserve(
            ref _itemDragOleMouseMovePending,
            ref _itemDragLastOleMouseMoveForwardedAt,
            Stopwatch.GetTimestamp(),
            OleMouseMoveMinimumTicks);
    }

    private void StopItemDragBoundaryHook()
    {
        Volatile.Write(ref _itemDragBoundaryArmed, 0);
        Volatile.Write(ref _itemDragBoundaryPromotionPosted, 0);
        DragMessageRelay.Reset(ref _itemDragOleMouseMovePending, ref _itemDragLastOleMouseMoveForwardedAt);
    }

    private void ShutdownItemDragBoundaryHook()
    {
        StopItemDragBoundaryHook();
        Thread? hookThread = _itemDragBoundaryHookThread;
        if (hookThread is null) return;

        uint threadId = Volatile.Read(ref _itemDragBoundaryHookThreadId);
        if (threadId != 0 && !NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero))
            AppLogger.Error("无法通知项目外拖边界钩子线程退出。");

        if (hookThread == Thread.CurrentThread)
        {
            AppLogger.Error("项目外拖边界钩子线程不能等待自身退出。");
            return;
        }
        if (!hookThread.Join(500))
        {
            AppLogger.Error("项目外拖边界钩子线程未能及时退出。");
            return;
        }
        if (ReferenceEquals(_itemDragBoundaryHookThread, hookThread)) _itemDragBoundaryHookThread = null;
        _itemDragBoundaryHookReady.Reset();
    }

    private void ApplyItemDragAutoScroll(double pointerY, double seconds)
    {
        if (ItemsScrollView.ScrollableHeight <= .5) return;
        const double edge = 48;
        double penetration = pointerY < edge
            ? Math.Clamp((edge - pointerY) / edge, 0, 1)
            : pointerY > ItemsScrollView.ActualHeight - edge
                ? Math.Clamp((pointerY - (ItemsScrollView.ActualHeight - edge)) / edge, 0, 1)
                : 0;
        if (penetration <= 0 || seconds <= 0) return;
        double direction = pointerY < edge ? -1 : 1;
        if (direction < 0 && ItemsScrollView.VerticalOffset <= .5 ||
            direction > 0 && ItemsScrollView.VerticalOffset >= ItemsScrollView.ScrollableHeight - .5) return;
        double speed = 120 + 600 * penetration * penetration;
        ItemsScrollView.ScrollBy(0, direction * speed * seconds,
            new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
    }

    private void ApplyAllProvisionalItemVisuals(bool animate)
    {
        for (int index = 0; index < _items.Count; index++)
        {
            if (TryGetRealizedItemHost(index, out Border host)) ApplyProvisionalItemVisual(host, index, animate);
        }
    }

    private void ApplyProvisionalItemVisual(Border host, int index, bool animate)
    {
        if (_itemReorderSession is not { IsActive: true } session || index == session.SourceIndex) return;
        Vector2 target;
        if (_gapTransitionFrom is { } from && _gapTransitionTo is { } to &&
            index < from.Length && index < to.Length)
        {
            target = GridGapTransitionMath.GetTranslation(
                index,
                from[index],
                to[index],
                GetItemLayoutColumnCount(),
                Math.Max(1, host.Width),
                Math.Max(1, host.Height),
                GetItemLayoutGapDip(),
                GridGapTransitionMath.GetProgress(Stopwatch.GetElapsedTime(_gapTransitionStartedAt)));
        }
        else
        {
            target = session.GetSlotTranslation(
                index,
                GetItemLayoutColumnCount(),
                Math.Max(1, host.Width),
                Math.Max(1, host.Height),
                GetItemLayoutGapDip());
        }
        Vector3 translation = new(target, 0);
        ItemMotionState motion = GetItemMotion(host);
        motion.Translation = translation;
        motion.TranslationTarget = translation;
        motion.TranslationVelocity = Vector3.Zero;
        motion.Scale = Vector3.One;
        motion.ScaleTarget = Vector3.One;
        motion.ScaleVelocity = Vector3.Zero;
        host.Translation = translation;
        host.Scale = Vector3.One;
    }

    private bool StartGapTransitionIfReady(ItemReorderSession session, long now)
    {
        if (_gapTransitionFrom is not null) return true;
        if (!session.TryBeginPreviewTransition(_items.Count, out int[] from, out int[] to)) return false;
        _gapTransitionFrom = from;
        _gapTransitionTo = to;
        _gapTransitionStartedAt = now;
        _gapTransitionRevision = session.PreviewRevision;
        if (_itemDragTraceEnabled)
            AppLogger.Info($"换序跟踪：预览开始 revision={_gapTransitionRevision} target={session.TargetIndex}。");
        if (_itemReorderUsesAnimations) return true;
        ApplyGapTransitionFrame(1);
        _gapMaterializedRevision = _gapTransitionRevision;
        _gapTransitionFrom = null;
        _gapTransitionTo = null;
        _gapTransitionStartedAt = 0;
        return false;
    }

    private bool StepGapTransition(ItemReorderSession session, long now)
    {
        if (_gapTransitionFrom is null && !StartGapTransitionIfReady(session, now)) return false;
        double progress = GridGapTransitionMath.GetProgress(Stopwatch.GetElapsedTime(_gapTransitionStartedAt, now));
        ApplyGapTransitionFrame(progress);
        if (progress < 1) return true;

        _gapMaterializedRevision = _gapTransitionRevision;
        if (_itemDragTraceEnabled)
            AppLogger.Info($"换序跟踪：预览完成 revision={_gapMaterializedRevision} target={session.TargetIndex}。");
        _gapTransitionFrom = null;
        _gapTransitionTo = null;
        _gapTransitionStartedAt = 0;
        return StartGapTransitionIfReady(session, now);
    }

    private void EnsureGapPreviewMaterialized(ItemReorderSession session)
    {
        if (_gapTransitionFrom is not null || _gapMaterializedRevision == session.PreviewRevision) return;
        ApplyAllProvisionalItemVisuals(animate: false);
        _gapMaterializedRevision = session.PreviewRevision;
        if (_itemDragTraceEnabled)
            AppLogger.Info($"换序跟踪：预览收敛 revision={_gapMaterializedRevision} target={session.TargetIndex}。");
    }

    private void ApplyGapTransitionFrame(double progress)
    {
        if (_itemReorderSession is not { } session || _gapTransitionFrom is not { } from || _gapTransitionTo is not { } to) return;
        int columns = GetItemLayoutColumnCount();
        for (int index = 0; index < _items.Count && index < from.Length && index < to.Length; index++)
        {
            if (index == session.SourceIndex || !TryGetRealizedItemHost(index, out Border host)) continue;
            Vector2 value = GridGapTransitionMath.GetTranslation(
                index,
                from[index],
                to[index],
                columns,
                Math.Max(1, host.Width),
                Math.Max(1, host.Height),
                GetItemLayoutGapDip(),
                progress);
            ItemMotionState motion = GetItemMotion(host);
            motion.Translation = new Vector3(value, 0);
            motion.TranslationTarget = motion.Translation;
            motion.TranslationVelocity = Vector3.Zero;
            motion.Scale = Vector3.One;
            motion.ScaleTarget = Vector3.One;
            motion.ScaleVelocity = Vector3.Zero;
            host.Translation = motion.Translation;
            host.Scale = Vector3.One;
        }
    }

    private ItemMotionState GetItemMotion(Border host)
    {
        if (_itemMotionStates.TryGetValue(host, out ItemMotionState? motion)) return motion;
        motion = new ItemMotionState
        {
            Translation = host.Translation,
            TranslationTarget = host.Translation,
            Scale = host.Scale,
            ScaleTarget = host.Scale
        };
        _itemMotionStates.Add(host, motion);
        return motion;
    }

    private void ResetItemMotion(Border host)
    {
        ItemMotionState motion = GetItemMotion(host);
        motion.Translation = Vector3.Zero;
        motion.TranslationTarget = Vector3.Zero;
        motion.TranslationVelocity = Vector3.Zero;
        motion.Scale = Vector3.One;
        motion.ScaleTarget = Vector3.One;
        motion.ScaleVelocity = Vector3.Zero;
        host.Translation = Vector3.Zero;
        host.Scale = Vector3.One;
    }

    private bool StepItemMotions(double seconds, Border? directDragHost)
    {
        if (!_itemReorderUsesAnimations)
        {
            foreach ((Border host, ItemMotionState motion) in _itemMotionStates)
            {
                if (ReferenceEquals(host, directDragHost)) continue;
                motion.Translation = motion.TranslationTarget;
                motion.TranslationVelocity = Vector3.Zero;
                motion.Scale = motion.ScaleTarget;
                motion.ScaleVelocity = Vector3.Zero;
                host.Translation = motion.Translation;
                host.Scale = motion.Scale;
            }
            return true;
        }

        bool settled = true;
        foreach ((Border host, ItemMotionState motion) in _itemMotionStates)
        {
            if (!IsFinite(motion.Translation) || !IsFinite(motion.TranslationTarget) || !IsFinite(motion.TranslationVelocity))
            {
                motion.Translation = Vector3.Zero;
                motion.TranslationTarget = Vector3.Zero;
                motion.TranslationVelocity = Vector3.Zero;
            }
            if (!IsFinite(motion.Scale) || !IsFinite(motion.ScaleTarget) || !IsFinite(motion.ScaleVelocity))
            {
                motion.Scale = Vector3.One;
                motion.ScaleTarget = Vector3.One;
                motion.ScaleVelocity = Vector3.Zero;
            }
            if (!ReferenceEquals(host, directDragHost))
            {
                settled &= ItemMotionMath.StepCriticalSpring(ref motion.Translation, ref motion.TranslationVelocity, motion.TranslationTarget, seconds);
                host.Translation = motion.Translation;
            }
            settled &= ItemMotionMath.StepCriticalSpring(ref motion.Scale, ref motion.ScaleVelocity, motion.ScaleTarget, seconds);
            host.Scale = motion.Scale;
        }
        return settled;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private async Task CompleteGapTransitionsAsync(ItemReorderSession session)
    {
        _gapTransitionCompleting = true;
        _gapTransitionSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long now = Stopwatch.GetTimestamp();
        if (!StepGapTransition(session, now))
        {
            EnsureGapPreviewMaterialized(session);
            _gapTransitionSettled.TrySetResult();
        }
        Task completed = await Task.WhenAny(_gapTransitionSettled.Task, Task.Delay(180));
        if (completed != _gapTransitionSettled.Task && ReferenceEquals(_itemReorderSession, session))
            CompleteGapTransitionsImmediately(session);
        _gapTransitionSettled = null;
        _gapTransitionCompleting = false;
    }

    private void CompleteGapTransitionsImmediately(ItemReorderSession session)
    {
        if (_gapTransitionFrom is not null)
        {
            ApplyGapTransitionFrame(1);
            _gapMaterializedRevision = _gapTransitionRevision;
            _gapTransitionFrom = null;
            _gapTransitionTo = null;
            _gapTransitionStartedAt = 0;
        }
        while (session.TryBeginPreviewTransition(_items.Count, out int[] from, out int[] to))
        {
            _gapTransitionFrom = from;
            _gapTransitionTo = to;
            _gapTransitionRevision = session.PreviewRevision;
            ApplyGapTransitionFrame(1);
            _gapMaterializedRevision = _gapTransitionRevision;
            _gapTransitionFrom = null;
            _gapTransitionTo = null;
        }
        _gapTransitionStartedAt = 0;
        EnsureGapPreviewMaterialized(session);
        _gapTransitionSettled?.TrySetResult();
    }

    private void ResetGapTransitionState()
    {
        _gapTransitionFrom = null;
        _gapTransitionTo = null;
        _gapTransitionStartedAt = 0;
        _gapTransitionRevision = 0;
        _gapMaterializedRevision = 0;
        _gapTransitionCompleting = false;
        _gapTransitionSettled?.TrySetCanceled();
        _gapTransitionSettled = null;
    }

    private async Task CommitItemReorderAsync(ItemReorderSession session, bool nativeDrop = false)
    {
        try
        {
        if (_itemDragTraceEnabled) AppLogger.Info($"换序跟踪：开始提交 {session.SourceIndex}->{session.TargetIndex}。");
        StopItemDragInput(keepRendering: true);
        if (!nativeDrop) await CompleteGapTransitionsAsync(session);
        if (!ReferenceEquals(_itemReorderSession, session)) return;
        int[] visualIndices = session.SealVisualIndices(_items.Count);
        _itemDragLanding = true;
        if (!nativeDrop && _itemDragHost is { } draggedHost)
        {
            Vector2 target = session.GetSlotTranslation(
                session.SourceIndex,
                GetItemLayoutColumnCount(),
                Math.Max(1, draggedHost.Width),
                Math.Max(1, draggedHost.Height),
                GetItemLayoutGapDip());
            if (_itemReorderUsesAnimations)
            {
                ItemMotionState motion = GetItemMotion(draggedHost);
                motion.TranslationTarget = new Vector3(target, 0);
                motion.ScaleTarget = Vector3.One;
                _itemMotionSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await Task.WhenAny(_itemMotionSettled.Task, Task.Delay(260));
            }
        }
        if (!ReferenceEquals(_itemReorderSession, session)) return;

        StopItemDragInput(keepRendering: false);
        session.BeginCommit();
        if (nativeDrop) MaterializeNativeSourceAtTarget(session);
        Dictionary<string, Point>? visualPositions = _itemReorderUsesAnimations ? CaptureRealizedItemPositions() : null;
        ResetItemDragVisuals();
        bool changed = false;
        for (int original = 0; original < visualIndices.Length; original++)
        {
            if (visualIndices[original] == original) continue;
            changed = true;
            break;
        }
        if (changed)
        {
            int preparedBeforeCommit = _itemElementsPrepared;
            int clearedBeforeCommit = _itemElementsCleared;
            WidgetItem[] originalItems = _items.ToArray();
            var desiredItems = new WidgetItem?[originalItems.Length];
            for (int original = 0; original < originalItems.Length; original++)
                desiredItems[visualIndices[original]] = originalItems[original];
            if (desiredItems.Any(item => item is null)) throw new InvalidOperationException("The gap preview is not a complete permutation.");
            WidgetItem[] desired = desiredItems.Select(item => item!).ToArray();
            List<string> previousOrder = _definition.ItemOrder.ToList();
            _definition.ItemOrder = desired.Select(item => item.RelativeName).ToList();
            try
            {
                await _host.SaveStateAsync();
            }
            catch
            {
                _definition.ItemOrder = previousOrder;
                throw;
            }
            var realizedSlots = new Border?[originalItems.Length];
            for (int slot = 0; slot < originalItems.Length; slot++)
            {
                if (TryGetRealizedItemHost(originalItems[slot].RelativeName, out Border host)) realizedSlots[slot] = host;
            }
            int[] affectedSlots = Enumerable.Range(0, visualIndices.Length)
                .Where(index => visualIndices[index] != index)
                .ToArray();
            bool canRotateRealizedContent = affectedSlots.All(slot => realizedSlots[slot]?.Child is not null);
            var originalContent = new UIElement?[originalItems.Length];

            _itemCollectionMoveInProgress = true;
            try
            {
                if (canRotateRealizedContent)
                {
                    foreach (int slot in affectedSlots)
                    {
                        originalContent[slot] = realizedSlots[slot]!.Child;
                        realizedSlots[slot]!.Child = null;
                    }
                    foreach (int original in affectedSlots)
                    {
                        realizedSlots[visualIndices[original]]!.Child = originalContent[original];
                    }
                }
                foreach (Border? host in realizedSlots)
                {
                    if (host is not null) _realizedItemHosts.Unregister(host);
                }
                _ = CatalogCollectionSync.ApplyReorderInPlace(_items, desired);
                for (int slot = 0; slot < realizedSlots.Length; slot++)
                {
                    if (realizedSlots[slot] is not { } host) continue;
                    WidgetItem item = _items[slot];
                    _realizedItemHosts.Register(item.RelativeName, host);
                    host.Tag = item.RelativeName;
                    ToolTipService.SetToolTip(host, IsDocumentItem(item) || item.Kind == WidgetItemKind.Organizer ? item.Name : item.FullPath);
                    if (!canRotateRealizedContent && affectedSlots.Contains(slot)) PrepareReorderedItemIdentity(host, item);
                }
                ItemsRepeater.UpdateLayout();
                if (visualPositions is not null) ApplyFlipFromCapturedPositions(visualPositions);
            }
            finally
            {
                _itemCollectionMoveInProgress = false;
            }
            _host.NotifyOrganizerPreviewChanged(_definition.Id);
            if (_itemDragTraceEnabled)
                AppLogger.Info($"换序跟踪：宿主生命周期 prepared={_itemElementsPrepared - preparedBeforeCommit} " +
                    $"cleared={_itemElementsCleared - clearedBeforeCommit}。");
        }
        session.MarkOutcome(ItemDragState.Committed);
        FinishItemReorderSession();
        if (_itemDragTraceEnabled) AppLogger.Info($"换序跟踪：提交完成 {session.SourceIndex}->{session.TargetIndex}。");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_itemReorderSession, session))
            {
                session.MarkOutcome(ItemDragState.Cancelled);
                StopItemDragInput(keepRendering: false);
                ResetItemDragVisuals();
                FinishItemReorderSession();
            }
            AppLogger.Error("保存项目换序失败。", ex);
            ShowMessage(AppStrings.Get("SaveConfigurationError"), InfoBarSeverity.Warning);
        }
    }

    private Dictionary<string, Point> CaptureRealizedItemPositions()
    {
        var positions = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < _items.Count; index++)
        {
            if (TryGetRealizedItemHost(index, out Border host))
                positions[_items[index].RelativeName] = host.TransformToVisual(ItemsRepeater).TransformPoint(new Point());
        }
        return positions;
    }

    private void ApplyFlipFromCapturedPositions(IReadOnlyDictionary<string, Point> positions)
    {
        bool animated = false;
        for (int index = 0; index < _items.Count; index++)
        {
            string relativeName = _items[index].RelativeName;
            if (!TryGetRealizedItemHost(relativeName, out Border host) ||
                !positions.TryGetValue(relativeName, out Point before)) continue;
            Point after = host.TransformToVisual(ItemsRepeater).TransformPoint(new Point());
            Vector3 inverse = new((float)(before.X - after.X), (float)(before.Y - after.Y), 0);
            if (inverse.LengthSquared() < .01f) continue;
            ItemMotionState motion = GetItemMotion(host);
            motion.Translation = inverse;
            motion.TranslationVelocity = Vector3.Zero;
            motion.TranslationTarget = Vector3.Zero;
            host.Translation = inverse;
            animated = true;
        }
        if (animated) StartNativeItemMotionRendering();
    }

    private void MaterializeNativeSourceAtTarget(ItemReorderSession session)
    {
        if (!TryGetRealizedItemHost(session.RelativeName, out Border host)) return;
        _itemDragHost = host;
        Vector2 target = session.GetSlotTranslation(
            session.SourceIndex,
            GetItemLayoutColumnCount(),
            Math.Max(1, host.Width),
            Math.Max(1, host.Height),
            GetItemLayoutGapDip());
        ItemMotionState motion = GetItemMotion(host);
        motion.Translation = new Vector3(target, 16);
        motion.TranslationTarget = motion.Translation;
        motion.TranslationVelocity = Vector3.Zero;
        motion.Scale = Vector3.One;
        motion.ScaleTarget = Vector3.One;
        motion.ScaleVelocity = Vector3.Zero;
        host.Translation = motion.Translation;
        host.Scale = Vector3.One;
        host.Opacity = 1;
    }

    private void StartNativeItemMotionRendering()
    {
        _itemDragLastFrame = Stopwatch.GetTimestamp();
        _nativeItemMotionRenderingStartedAt = _itemDragLastFrame;
        if (!_nativeItemMotionRenderingSubscribed)
        {
            CompositionTarget.Rendering += NativeItemMotionRendering;
            _nativeItemMotionRenderingSubscribed = true;
        }
        TrySetItemDragClockBoost(true);
    }

    private void NativeItemMotionRendering(object? sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        bool activePreview = _itemReorderSession is { IsActive: true };
        if (_itemReorderSession is { IsActive: true } session)
        {
            bool transitionRunning = StepGapTransition(session, now);
            if (!transitionRunning) EnsureGapPreviewMaterialized(session);
            if (transitionRunning) return;
        }
        double seconds = Math.Clamp(Stopwatch.GetElapsedTime(_itemDragLastFrame, now).TotalSeconds, 0, 1d / 20);
        _itemDragLastFrame = now;
        bool settled = StepItemMotions(seconds, directDragHost: null);
        if (!settled && Stopwatch.GetElapsedTime(_nativeItemMotionRenderingStartedAt, now) < TimeSpan.FromMilliseconds(500)) return;
        if (activePreview) StopNativeItemMotionRendering(snapToTargets: !settled);
        else NormalizeItemMotionsToLayout(settled ? "FlipCompleted" : "FlipTimeout");
        _ = StartCatalogRefreshIfReady();
    }

    private void StopNativeItemMotionRendering(bool snapToTargets)
    {
        if (_nativeItemMotionRenderingSubscribed)
        {
            CompositionTarget.Rendering -= NativeItemMotionRendering;
            _nativeItemMotionRenderingSubscribed = false;
        }
        _nativeItemMotionRenderingStartedAt = 0;
        if (snapToTargets)
        {
            foreach ((Border host, ItemMotionState motion) in _itemMotionStates)
            {
                motion.Translation = IsFinite(motion.TranslationTarget) ? motion.TranslationTarget : Vector3.Zero;
                motion.TranslationVelocity = Vector3.Zero;
                motion.Scale = IsFinite(motion.ScaleTarget) ? motion.ScaleTarget : Vector3.One;
                motion.ScaleVelocity = Vector3.Zero;
                host.Translation = motion.Translation;
                host.Scale = motion.Scale;
            }
        }
        TrySetItemDragClockBoost(false);
    }

    private void NormalizeItemMotionsToLayout(string reason)
    {
        if (_itemReorderSession is not null || _shellDragActive) return;
        StopNativeItemMotionRendering(snapToTargets: false);
        var repaired = new List<object>();
        float maximumOffset = 0;
        foreach ((Border host, ItemMotionState motion) in _itemMotionStates)
        {
            float offset = Math.Max(motion.Translation.Length(), motion.TranslationTarget.Length());
            float diagnosticThreshold = reason == "FlipTimeout" ? .02f : 1f;
            if (offset >= diagnosticThreshold ||
                reason == "FlipTimeout" && motion.TranslationVelocity.LengthSquared() >= .01f)
            {
                maximumOffset = Math.Max(maximumOffset, offset);
                repaired.Add(new
                {
                    identity = host.DataContext is WidgetItem item ? item.RelativeName : host.Tag as string ?? "unknown",
                    x = motion.Translation.X,
                    y = motion.Translation.Y,
                    targetX = motion.TranslationTarget.X,
                    targetY = motion.TranslationTarget.Y
                });
            }
            motion.Translation = Vector3.Zero;
            motion.TranslationTarget = Vector3.Zero;
            motion.TranslationVelocity = Vector3.Zero;
            motion.Scale = Vector3.One;
            motion.ScaleTarget = Vector3.One;
            motion.ScaleVelocity = Vector3.Zero;
            host.Translation = Vector3.Zero;
            host.Scale = Vector3.One;
        }
        _itemMotionStates.Clear();
        if (repaired.Count > 0)
        {
            AppLogger.Info("换序回位修复JSON=" + JsonSerializer.Serialize(new
            {
                reason,
                repairedCount = repaired.Count,
                maximumOffset,
                items = repaired
            }));
        }
    }

    private void BeginNoteDragPreparation(ItemReorderSession session)
    {
        if (_itemDragPointerId == 0 || session.SourceIndex < 0 || session.SourceIndex >= _items.Count ||
            _items[session.SourceIndex] is not { Kind: WidgetItemKind.Note, NoteId: Guid noteId }) return;
        _noteDragPreparationId = noteId;
        _noteDragPreparationTask = PrepareNoteDragDataAsync(noteId, _noteDragCleanupTask);
    }

    private async Task<(string Path, bool RestoreWindow, IStorageItem StorageItem)> PrepareNoteDragDataAsync(
        Guid noteId,
        Task previousCleanup)
    {
        await previousCleanup;
        (string path, bool restoreWindow) = await _host.PrepareNoteDragAsync(_definition.Id, noteId);
        try
        {
            StorageFile storageFile = await StorageFile.GetFileFromPathAsync(path);
            return (path, restoreWindow, storageFile);
        }
        catch
        {
            await _host.CompleteNoteDragAsync(_definition.Id, noteId, path, restoreWindow, moved: false);
            throw;
        }
    }

    private void DiscardNoteDragPreparation()
    {
        Guid? noteId = _noteDragPreparationId;
        Task<(string Path, bool RestoreWindow, IStorageItem StorageItem)>? preparation = _noteDragPreparationTask;
        _noteDragPreparationId = null;
        _noteDragPreparationTask = null;
        if (noteId is Guid id && preparation is not null)
            _noteDragCleanupTask = DiscardNoteDragPreparationAsync(id, preparation);
    }

    private async Task DiscardNoteDragPreparationAsync(
        Guid noteId,
        Task<(string Path, bool RestoreWindow, IStorageItem StorageItem)> preparation)
    {
        try
        {
            (string path, bool restoreWindow, _) = await preparation;
            await _host.CompleteNoteDragAsync(_definition.Id, noteId, path, restoreWindow, moved: false);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法清理未使用的便签拖放预热：{noteId}", ex);
        }
    }

    private void StartMouseNativeDrag(ItemReorderSession session)
    {
        if (_shellPromotionPending) return;
        _shellPromotionPending = true;
        _shellPromotionRequestedAt = Stopwatch.GetTimestamp();
        if (!DispatcherQueue.TryEnqueue(() => CompleteMouseNativeDrag(session)))
        {
            _shellPromotionPending = false;
            CancelItemReorder();
        }
    }

    private async void CompleteMouseNativeDrag(ItemReorderSession session)
    {
        if (!ReferenceEquals(_itemReorderSession, session)) return;
        if (session.SourceIndex < 0 || session.SourceIndex >= _items.Count) { CancelItemReorder(); return; }
        if (!ShellDragService.RequiresNativeDrag(_items[session.SourceIndex].Kind)) { CancelItemReorder(); return; }

        WidgetItem item = _items[session.SourceIndex];
        (string Path, bool RestoreWindow, IStorageItem StorageItem)? preparedNote = null;
        if (item is { Kind: WidgetItemKind.Note, NoteId: Guid noteId })
        {
            Task<(string Path, bool RestoreWindow, IStorageItem StorageItem)>? preparation =
                _noteDragPreparationId == noteId ? _noteDragPreparationTask : null;
            if (preparation is null)
            {
                CancelItemReorder();
                return;
            }
            try
            {
                preparedNote = await preparation;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"无法预热便签拖出：{item.Name}", ex);
                ShowMessage(AppStrings.Format("DragItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
                if (ReferenceEquals(_itemReorderSession, session)) CancelItemReorder();
                return;
            }
            if (!ReferenceEquals(_itemReorderSession, session))
            {
                await _noteDragCleanupTask;
                return;
            }
            if (_noteDragPreparationId != noteId || !ReferenceEquals(_noteDragPreparationTask, preparation))
            {
                CancelItemReorder();
                return;
            }
            _noteDragPreparationId = null;
            _noteDragPreparationTask = null;
        }

        Size viewport = GetItemsViewportSize();
        (double fallbackWidth, double fallbackHeight) = GetItemCellSizeDip(viewport.Width, viewport.Height);
        _nativeDragCellWidth = Math.Max(1, _itemDragHost?.Width ??
            fallbackWidth);
        _nativeDragCellHeight = Math.Max(1, _itemDragHost?.Height ??
            fallbackHeight);
        if (_itemDragHost is not { } host || _itemDragLastPointerPoint is not { } pointerPoint)
        {
            if (item.NoteId is Guid abandonedNoteId && preparedNote is { } abandoned)
            {
                _noteDragCleanupTask = DiscardNoteDragPreparationAsync(abandonedNoteId, Task.FromResult(abandoned));
                await _noteDragCleanupTask;
            }
            CancelItemReorder();
            return;
        }
        if (item is { Kind: WidgetItemKind.Organizer, OrganizerId: not null })
        {
            BeginContainedOrganizerDrag(item, host, session);
            return;
        }
        if (item.Kind is WidgetItemKind.Shortcut or WidgetItemKind.InternetShortcut)
        {
            BeginNativeShellDrag(item, host, session);
            return;
        }
        BeginXamlShellDrag(host, pointerPoint, session, preparedNote);
    }

    private async void BeginContainedOrganizerDrag(WidgetItem item, Border host, ItemReorderSession session)
    {
        bool completed = false;
        _shellPromotionPending = true;
        StopItemDragInput(keepRendering: false, keepBoundaryHook: true);
        ResetItemDragVisuals();
        ApplyNativeSourcePlaceholder(host);
        _draggedRelativeName = item.RelativeName;
        Volatile.Write(ref _shellDragActive, true);
        _shellPromotionPending = false;
        try
        {
            bool cancelled = false;
            while (!_closing && (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0)
            {
                if ((NativeMethods.GetAsyncKeyState(0x1B) & 0x8000) != 0)
                {
                    cancelled = true;
                    break;
                }
                if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
                    _host.UpdateContainedOrganizerDragPreview(item.OrganizerId!.Value, cursor);
                await Task.Delay(16);
            }

            if (!cancelled && !_closing && NativeMethods.GetCursorPos(out NativeMethods.POINT dropPoint))
            {
                string? error = await _host.FinishContainedOrganizerDragAsync(item.OrganizerId!.Value, dropPoint);
                completed = error is null;
                if (error is not null) ShowMessage(error, InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"收纳窗元素拖出失败：{item.Name}", ex);
            ShowMessage(AppStrings.Format("OrganizerStationDropError", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _host.ReconcileContainedOrganizer(item.OrganizerId!.Value);
            FinishShellDragVisualState(session);
            _shellPromotionRequestedAt = 0;
            Interlocked.Exchange(ref _itemDragBoundaryDetectedAt, 0);
            _itemDragPressedAt = 0;
            await _host.ReconcileExclusiveExpansionAsync(completed ? null : this);
            await StartCatalogRefreshIfReady();
        }
    }

    private async void BeginNativeShellDrag(WidgetItem item, Border host, ItemReorderSession session)
    {
        ShellDragService.ShellDragSession? shellSession = null;
        string dragPath = item.FullPath;
        bool internalDropAccepted = false;
        bool dragCompleted = false;
        bool visualStateFinished = false;
        _shellPromotionPending = true;
        StopItemDragInput(keepRendering: false, keepBoundaryHook: true);
        ResetItemDragVisuals();
        ApplyNativeSourcePlaceholder(host);
        _draggedRelativeName = item.RelativeName;
        Volatile.Write(ref _shellDragActive, true);
        _internalOleDropAccepted = false;
        _shellPromotionPending = false;
        StartNativeItemMotionRendering();
        try
        {
            shellSession = ShellDragService.Prepare(
                dragPath,
                session.GrabOffset.X / _nativeDragCellWidth,
                session.GrabOffset.Y / _nativeDragCellHeight);
            ShellDragResult result = ShellDragService.Move(
                _hwnd,
                shellSession,
                default,
                () => _closing);
            dragCompleted = result.Outcome != ShellDragOutcome.Cancelled;
            internalDropAccepted = _internalOleDropAccepted;
            AppLogger.Info($"Shell拖出交接：结果={result.Outcome}，路径={dragPath}。");
            if (internalDropAccepted && ReferenceEquals(_itemReorderSession, session))
            {
                await CommitItemReorderAsync(session, nativeDrop: true);
            }
            else if (ReferenceEquals(_itemReorderSession, session))
            {
                bool desktopMoved = result.Outcome == ShellDragOutcome.DesktopRequested;
                session.MarkOutcome(result.Outcome == ShellDragOutcome.ExternalMoved || desktopMoved
                    ? ItemDragState.ExternalMoved
                    : ItemDragState.Cancelled);
                ResetItemDragVisuals();
                FinishItemReorderSession(runPendingRefresh: false);
            }

            _shellDropFinalizing = true;
            FinishShellDragVisualState(session);
            visualStateFinished = true;
            shellSession.Dispose();
            shellSession = null;

            if (result.Outcome == ShellDragOutcome.DesktopRequested)
            {
                TransferOutcome moved = await _host.TransferQueue.RunAsync(
                    token => _storage.MoveItemToDesktopAsync(dragPath, progress: null, token));
                if (moved.Status != TransferStatus.Moved)
                {
                    dragCompleted = false;
                    ShowMessage(moved.Message, moved.Status == TransferStatus.Cancelled
                        ? InfoBarSeverity.Informational
                        : InfoBarSeverity.Error);
                }
                else
                {
                    if (moved.DestinationPath is { } destination)
                    {
                        _host.RebindPortableWindowAfterMove(dragPath, destination);
                        await Task.Run(() => ShellChangeNotificationService.NotifyMoved(dragPath, destination));
                    }
                    await RefreshCatalogAsync(notifyUnsupported: false);
                }
            }
            else if (result.Outcome == ShellDragOutcome.ExternalMoved)
            {
                await Task.Delay(120);
                await RefreshCatalogAsync(notifyUnsupported: false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"快捷方式Shell拖出失败：{item.FullPath}", ex);
            ShowMessage(AppStrings.Format("DragItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            StopItemDragBoundaryHook();
            if (!visualStateFinished) FinishShellDragVisualState(session);
            shellSession?.Dispose();
            _shellDropFinalizing = false;
            _internalOleDropAccepted = false;
            _shellPromotionRequestedAt = 0;
            Interlocked.Exchange(ref _itemDragBoundaryDetectedAt, 0);
            _itemDragPressedAt = 0;
            await _host.ReconcileExclusiveExpansionAsync(dragCompleted ? null : this);
            await StartCatalogRefreshIfReady();
        }
    }

    private async void BeginXamlShellDrag(
        Border host,
        PointerPoint pointerPoint,
        ItemReorderSession session,
        (string Path, bool RestoreWindow, IStorageItem StorageItem)? preparedNote)
    {
        if (!ReferenceEquals(_itemReorderSession, session) ||
            session.SourceIndex < 0 || session.SourceIndex >= _items.Count) return;

        WidgetItem item = _items[session.SourceIndex];
        if (IsDocumentItem(item) && !await _host.FlushPortableWindowAsync(item.FullPath))
        {
            ShowMessage(AppStrings.Get(item.Kind == WidgetItemKind.PortableTodo
                ? "TodoDragSaveFailed"
                : "NoteDragSaveFailed"), InfoBarSeverity.Error);
            CancelItemReorder();
            return;
        }
        Guid? draggedNoteId = item.Kind == WidgetItemKind.Note ? item.NoteId : null;
        string dragPath = preparedNote?.Path ?? item.FullPath;
        string? noteStagingPath = preparedNote?.Path;
        bool restoreNoteWindow = preparedNote?.RestoreWindow ?? false;
        bool noteDragCompleted = false;
        bool internalDropAccepted = false;
        bool dragCompleted = false;
        bool visualStateFinished = false;
        _shellPromotionPending = true;
        _shellPromotionRequestedAt = Stopwatch.GetTimestamp();
        StopItemDragInput(keepRendering: false, releasePointerCapture: false);
        _draggedRelativeName = item.RelativeName;
        Volatile.Write(ref _shellDragActive, true);
        _internalOleDropAccepted = false;
        _shellPromotionPending = false;
        StartNativeItemMotionRendering();

        async void PopulateDragData(UIElement _, DragStartingEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                DataPackageOperation allowed = OrganizerInteractionMath.ExternalDragAllowedOperations(item.Kind);
                args.Data.RequestedOperation = OrganizerInteractionMath.ExternalDragRequestedOperation(item.Kind);
                IStorageItem storageItem;
                if (draggedNoteId is not null)
                {
                    storageItem = preparedNote?.StorageItem ??
                        throw new InvalidOperationException("The internal note drag was not prepared before shell handoff.");
                }
                else
                {
                    storageItem = Directory.Exists(dragPath)
                        ? await StorageFolder.GetFolderFromPathAsync(dragPath)
                        : await StorageFile.GetFileFromPathAsync(dragPath);
                }
                args.Data.SetStorageItems([storageItem], readOnly: false);
                args.AllowedOperations = allowed;
                ResetItemDragVisuals();
                ApplyNativeSourcePlaceholder(host);
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                AppLogger.Error($"无法准备 WinUI 文件拖出：{dragPath}", ex);
            }
            finally
            {
                deferral.Complete();
            }
        }

        host.DragStarting += PopulateDragData;
        try
        {
            DataPackageOperation operation = await host.StartDragAsync(pointerPoint);
            internalDropAccepted = _internalOleDropAccepted;
            if (draggedNoteId is not null && operation == DataPackageOperation.None && !internalDropAccepted)
                await Task.Delay(NoteDragShellCompletionGraceMs);
            bool sourceItemExists = File.Exists(dragPath) || Directory.Exists(dragPath);
            bool externalSourceMoved = OrganizerInteractionMath.ExternalDragMovedSource(
                operation,
                internalDropAccepted,
                sourceItemExists);
            dragCompleted = operation != DataPackageOperation.None || externalSourceMoved;
            AppLogger.Info($"WinUI拖出交接：结果={operation}，路径={dragPath}。");
            if (internalDropAccepted && ReferenceEquals(_itemReorderSession, session))
            {
                await CommitItemReorderAsync(session, nativeDrop: true);
            }
            else if (ReferenceEquals(_itemReorderSession, session))
            {
                session.MarkOutcome(externalSourceMoved
                    ? ItemDragState.ExternalMoved
                    : ItemDragState.Cancelled);
                ResetItemDragVisuals();
                FinishItemReorderSession(runPendingRefresh: false);
            }

            _shellDropFinalizing = true;
            FinishShellDragVisualState(session);
            visualStateFinished = true;
            if (draggedNoteId is Guid completedNoteId && noteStagingPath is not null)
            {
                try
                {
                    await _host.CompleteNoteDragAsync(
                        _definition.Id,
                        completedNoteId,
                        noteStagingPath,
                        restoreNoteWindow,
                        moved: externalSourceMoved);
                }
                finally
                {
                    noteDragCompleted = true;
                }
            }
            else if (externalSourceMoved)
            {
                if (IsDocumentItem(item)) _host.ClosePortableWindowWithoutSave(item.FullPath);
                await Task.Delay(120);
                await RefreshCatalogAsync(notifyUnsupported: false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"WinUI 文件拖出失败：{dragPath}", ex);
            ShowMessage(AppStrings.Format("DragItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            if (!noteDragCompleted && draggedNoteId is Guid noteId && noteStagingPath is not null)
            {
                try
                {
                    await _host.CompleteNoteDragAsync(
                        _definition.Id,
                        noteId,
                        noteStagingPath,
                        restoreNoteWindow,
                        moved: false);
                }
                catch (Exception cleanupError)
                {
                    AppLogger.Error($"无法恢复取消的便签拖出：{noteId}", cleanupError);
                }
            }
            host.DragStarting -= PopulateDragData;
            host.ReleasePointerCaptures();
            _ = NativeMethods.ReleaseCapture();
            if (!visualStateFinished) FinishShellDragVisualState(session);
            _shellDropFinalizing = false;
            _internalOleDropAccepted = false;
            _shellPromotionRequestedAt = 0;
            Interlocked.Exchange(ref _itemDragBoundaryDetectedAt, 0);
            _itemDragPressedAt = 0;
            await _host.ReconcileExclusiveExpansionAsync(dragCompleted ? null : this);
            await StartCatalogRefreshIfReady();
        }
    }

    private void ApplyNativeSourcePlaceholder(Border host)
    {
        _itemDragHost = host;
        host.Opacity = 0;
        host.Shadow = null;
        Canvas.SetZIndex(host, 0);
        ItemMotionState motion = GetItemMotion(host);
        motion.Scale = Vector3.One;
        motion.ScaleTarget = Vector3.One;
        motion.ScaleVelocity = Vector3.Zero;
        host.Scale = Vector3.One;
    }

    private void CancelItemReorder(bool runPendingRefresh = true)
    {
        if (_itemReorderSession is not { } session) return;
        session.MarkOutcome(ItemDragState.Cancelled);
        StopItemDragInput(keepRendering: false);
        ResetItemDragVisuals();
        FinishItemReorderSession(runPendingRefresh);
    }

    private void StopItemDragInput(bool keepRendering, bool keepBoundaryHook = false, bool releasePointerCapture = true)
    {
        _itemTouchHoldTimer.Stop();
        if (!keepBoundaryHook) StopItemDragBoundaryHook();
        if (!keepRendering && _itemDragRenderingSubscribed)
        {
            CompositionTarget.Rendering -= ItemDragRendering;
            _itemDragRenderingSubscribed = false;
        }
        TrySetItemDragClockBoost(false);
        _itemDragPointerId = 0;
        _itemDragLastPointerPoint = null;
        if (releasePointerCapture)
        {
            _itemDragHost?.ReleasePointerCaptures();
            // OLE drag/drop cannot take over while the XAML island still owns capture.
            _ = NativeMethods.ReleaseCapture();
        }
    }

    private void ResetItemDragVisuals()
    {
        ResetGapTransitionState();
        StopNativeItemMotionRendering(snapToTargets: false);
        _itemMotionSettled?.TrySetCanceled();
        _itemMotionSettled = null;
        _itemDragLanding = false;
        foreach (Border host in _itemMotionStates.Keys.ToArray())
        {
            ResetItemMotion(host);
            host.Shadow = null;
            Canvas.SetZIndex(host, 0);
            ResetItemInteractionVisual(host);
            host.Opacity = 1;
        }
    }

    private void FinishItemReorderSession(bool runPendingRefresh = true)
    {
        DiscardNoteDragPreparation();
        if (_itemReorderSession is not { State: ItemDragState.Committed })
        {
            StopNativeItemMotionRendering(snapToTargets: true);
        }
        if (_itemDragRenderingSubscribed)
        {
            CompositionTarget.Rendering -= ItemDragRendering;
            _itemDragRenderingSubscribed = false;
        }
        TrySetItemDragClockBoost(false);
        _itemReorderSession = null;
        _itemDragIdentityWarnings.Clear();
        _shellPromotionPending = false;
        _itemDragLanding = false;
        _itemDragHost = null;
        _itemDragPointerType = PointerDeviceType.Mouse;
        if (runPendingRefresh) _ = StartCatalogRefreshIfReady();
    }

    private void TrySetItemDragClockBoost(bool enabled)
    {
        if (enabled && !_dragClockBoosted && NativeMethods.TrySetCompositorClockBoost(true)) _dragClockBoosted = true;
        else if (!enabled && _dragClockBoosted && !_dragRenderingSubscribed &&
            !_itemDragRenderingSubscribed && !_nativeItemMotionRenderingSubscribed)
        {
            _ = NativeMethods.TrySetCompositorClockBoost(false);
            _dragClockBoosted = false;
        }
    }

    private int IndexOfItem(string relativeName)
    {
        for (int index = 0; index < _items.Count; index++)
        {
            if (_items[index].RelativeName.Equals(relativeName, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
    }

    private void FinishShellDragVisualState(ItemReorderSession originalSession)
    {
        StopItemDragBoundaryHook();
        if (ReferenceEquals(_itemReorderSession, originalSession))
        {
            ResetItemDragVisuals();
            FinishItemReorderSession(runPendingRefresh: false);
        }
        Volatile.Write(ref _shellDragActive, false);
        _shellPromotionPending = false;
        _draggedRelativeName = null;
    }

    private void ItemsGrid_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedRelativeName is not null && _itemReorderSession is { NativeDragStarted: true } session)
        {
            AutoScrollForDrag(e);
            UpdateInternalOleTarget(e, session);
            e.AcceptedOperation = InternalOleOperation();
            e.Handled = true;
        }
        else if (HasLocalFileDrop(e.DataView))
        {
            AutoScrollForDrag(e);
            e.AcceptedOperation = OrganizerInteractionMath.SelectDropOperation(e.AllowedOperations);
            if (e.AcceptedOperation != DataPackageOperation.None)
                e.DragUIOverride.Caption = AppStrings.Get("DragIntoApp");
        }
    }

    private void ItemsGrid_DragEnter(object sender, DragEventArgs e)
    {
        if (_draggedRelativeName is null || _itemReorderSession is not { NativeDragStarted: true } session) return;
        UpdateInternalOleTarget(e, session);
        e.AcceptedOperation = InternalOleOperation();
        e.Handled = true;
    }

    private void ItemsGrid_DragLeave(object sender, DragEventArgs e)
    {
        if (_draggedRelativeName is null || _itemReorderSession is not { NativeDragStarted: true } session) return;
        session.LeaveInternalPreview();
        ResetGapTransitionState();
        ApplyAllProvisionalItemVisuals(animate: false);
        e.Handled = true;
    }

    private void UpdateInternalOleTarget(DragEventArgs e, ItemReorderSession session)
    {
        session.MarkInternalPreview();
        session.UpdateTarget(
            e.GetPosition(ItemsRepeater),
            _nativeDragCellWidth,
            _nativeDragCellHeight,
            GetItemLayoutGapDip(),
            GetItemLayoutColumnCount(),
            _items.Count);
        _ = StartGapTransitionIfReady(session, Stopwatch.GetTimestamp());
        StartNativeItemMotionRendering();
    }

    private void AutoScrollForDrag(DragEventArgs e)
    {
        if (ItemsScrollView.ScrollableHeight <= .5) return;
        const double edge = 52;
        const double step = 18;
        Point point = e.GetPosition(ItemsScrollView);
        double vertical = 0;
        if (point.Y < edge) vertical = -step;
        else if (point.Y > ItemsScrollView.ActualHeight - edge) vertical = step;
        if (vertical < 0 && ItemsScrollView.VerticalOffset <= .5 ||
            vertical > 0 && ItemsScrollView.VerticalOffset >= ItemsScrollView.ScrollableHeight - .5) return;
        if (vertical != 0)
        {
            ItemsScrollView.ScrollBy(0, vertical,
                new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
        }
    }

    private async void ItemsGrid_Drop(object sender, DragEventArgs e)
    {
        if (_draggedRelativeName is not null)
        {
            if (_itemReorderSession is { NativeDragStarted: true } session)
            {
                UpdateInternalOleTarget(e, session);
                CompleteGapTransitionsImmediately(session);
                session.SealVisualIndices(_items.Count);
                _internalOleDropAccepted = true;
                e.AcceptedOperation = InternalOleOperation();
            }
            e.Handled = true;
            return;
        }
        e.Handled = true;
        await ImportFromDragAsync(e);
    }

    private void WindowRoot_DragEnter(object sender, DragEventArgs e)
    {
        if (!_expanded && _draggedRelativeName is null && HasLocalFileDrop(e.DataView))
        {
            StartHoverExpand(scrollToEnd: true);
        }
    }

    private DataPackageOperation InternalOleOperation() =>
        _draggedRelativeName?.StartsWith("note:", StringComparison.OrdinalIgnoreCase) == true
            ? DataPackageOperation.Move
            : DataPackageOperation.Link;

    private void WindowRoot_DragLeave(object sender, DragEventArgs e)
    {
        _externalHoverTimer.Stop();
        _hoverExpandScrollToEnd = false;
        if (_draggedRelativeName is null || _itemReorderSession is not { NativeDragStarted: true } session) return;
        session.LeaveInternalPreview();
        ResetGapTransitionState();
        ApplyAllProvisionalItemVisuals(animate: false);
    }

    private void WindowRoot_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedRelativeName is not null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }
        if (HasLocalFileDrop(e.DataView))
        {
            e.AcceptedOperation = OrganizerInteractionMath.SelectDropOperation(e.AllowedOperations);
            if (e.AcceptedOperation != DataPackageOperation.None)
                e.DragUIOverride.Caption = AppStrings.Get("DragIntoApp");
        }
    }

    private async void WindowRoot_Drop(object sender, DragEventArgs e)
    {
        _externalHoverTimer.Stop();
        if (_draggedRelativeName is not null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
        }
        else
        {
            e.Handled = true;
            await ImportFromDragAsync(e);
        }
    }

    private async void ExternalHoverTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        bool scrollToEnd = _hoverExpandScrollToEnd;
        _hoverExpandScrollToEnd = false;
        if (_expanded || _animating || _definition.PlacementMode == OrganizerPlacementMode.Station) return;
        if (!scrollToEnd && !ShouldStartIdleHoverExpand()) return;
        await ExpandAsync(scrollToEnd);
    }

    private void WindowRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && ShouldStartIdleHoverExpand())
        {
            StartHoverExpand(scrollToEnd: false);
        }
    }

    private void WindowRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursor) &&
            NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT bounds) &&
            DragBoundaryMath.Contains(bounds, cursor)) return;
        _externalHoverTimer.Stop();
        _hoverExpandScrollToEnd = false;
    }

    private void StartHoverExpand(bool scrollToEnd)
    {
        _hoverExpandScrollToEnd = scrollToEnd;
        _externalHoverTimer.Stop();
        _externalHoverTimer.Interval = TimeSpan.FromMilliseconds(_host.State.GlobalSettings.HoverExpandDelayMs);
        _externalHoverTimer.Start();
    }

    internal void RefreshHoverDelays()
    {
        bool restartExpandTimer = _externalHoverTimer.IsRunning;
        _externalHoverTimer.Stop();
        _externalHoverTimer.Interval = TimeSpan.FromMilliseconds(_host.State.GlobalSettings.HoverExpandDelayMs);
        _ordinaryOutsideSince = 0;
        _stationOutsideSince = 0;
        if (restartExpandTimer) _externalHoverTimer.Start();
    }

    internal void RefreshPerformanceSettings()
    {
        PerformanceTuning tuning = _host.State.GlobalSettings.PerformanceTuning;
        _stationPointerTimer.Interval = TimeSpan.FromMilliseconds(tuning.PointerPollMilliseconds);
        _desktopRepairTimer.Interval = TimeSpan.FromMilliseconds(tuning.DesktopRepairMilliseconds);

        bool visible = _definition.PlacementMode == OrganizerPlacementMode.Station
            ? _stationVisible
            : _runtimeVisible || IsContained && _expanded;
        bool pollPointer = !_closing && OrganizerInteractionMath.ShouldPollPointer(
            _definition.PlacementMode,
            visible,
            IsContained,
            _expanded,
            _host.State.GlobalSettings.ExpandOnHover,
            _host.State.GlobalSettings.CollapseOnPointerLeave);
        SetTimerRunning(_stationPointerTimer, pollPointer);
        if (!pollPointer)
        {
            _externalHoverTimer.Stop();
            _ordinaryOutsideSince = 0;
            ResetStationPointerDelay();
        }

        bool repairDesktop = !_closing && _desktopLayer is not null && !IsContained &&
            (_definition.PlacementMode == OrganizerPlacementMode.Station ? _stationVisible : _runtimeVisible);
        SetTimerRunning(_desktopRepairTimer, repairDesktop);

        TimeSpan iconTransitionDuration = UseCustomAnimations ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero;
        if (CompactTile.ScaleTransition is not null) CompactTile.ScaleTransition.Duration = iconTransitionDuration;
        if (CollapseDash.ScaleTransition is not null) CollapseDash.ScaleTransition.Duration = iconTransitionDuration;
        for (int index = 0; index < _items.Count; index++)
        {
            if (TryGetRealizedItemHost(index, out Border host) &&
                FindItemPart<StackPanel>(host, "ItemContent") is StackPanel content)
                ConfigureItemInteractionTransitions(content);
        }
        if (!UseCustomAnimations)
        {
            CompactTile.Scale = Vector3.One;
            CollapseDash.Scale = Vector3.One;
        }
    }

    private static void SetTimerRunning(
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer,
        bool running)
    {
        if (running && !timer.IsRunning) timer.Start();
        else if (!running && timer.IsRunning) timer.Stop();
    }

    private bool UseCustomAnimations =>
        _host.State.GlobalSettings.ShouldUseCustomAnimations(_uiSettings.AnimationsEnabled);

    private bool ShouldStartIdleHoverExpand()
    {
        bool interactionActive = _pressActive || _widgetDragging || _nativeMouseCapture ||
            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        return OrganizerInteractionMath.ShouldStartHoverExpand(
            _host.State.GlobalSettings.ExpandOnHover,
            _definition.PlacementMode == OrganizerPlacementMode.Station,
            _expanded,
            _animating,
            interactionActive);
    }

    private async void StationPointerTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_definition.PlacementMode != OrganizerPlacementMode.Station)
        {
            bool pointerOverWindow = NativeMethods.GetCursorPos(out NativeMethods.POINT pointer) &&
                IsPointerOverThisWindow(pointer);
            if (!_closing && pointerOverWindow && ShouldStartIdleHoverExpand())
            {
                if (!_externalHoverTimer.IsRunning) StartHoverExpand(scrollToEnd: false);
            }
            else if (!_hoverExpandScrollToEnd)
            {
                _externalHoverTimer.Stop();
            }

            if (!_host.State.GlobalSettings.CollapseOnPointerLeave || !_expanded || _closing ||
                _animating || _host.TransferQueue.IsActive || _shellDragActive || _shellDropFinalizing ||
                _itemReorderSession is not null || _canvasResize is not null || _pressActive ||
                _widgetDragging || _nativeMouseCapture || _shellContextMenuOpen || _overlayOpenCount > 0)
            {
                _ordinaryOutsideSince = 0;
                return;
            }

            if (pointerOverWindow)
            {
                _ordinaryOutsideSince = 0;
                return;
            }

            long ordinaryNow = Stopwatch.GetTimestamp();
            _ordinaryOutsideSince = _ordinaryOutsideSince == 0 ? ordinaryNow : _ordinaryOutsideSince;
            if (Stopwatch.GetElapsedTime(_ordinaryOutsideSince, ordinaryNow).TotalMilliseconds <
                _host.State.GlobalSettings.PointerLeaveCollapseDelayMs) return;
            _ordinaryOutsideSince = 0;
            await CollapseAsync();
            return;
        }

        if (_definition.PlacementMode != OrganizerPlacementMode.Station || !_stationVisible || _closing ||
            _stationTransitionPending || !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
        {
            ResetStationPointerDelay();
            return;
        }

        if (_animating || _host.TransferQueue.IsActive || _shellDragActive || _shellDropFinalizing ||
            _itemReorderSession is not null || _canvasResize is not null || _pressActive || _overlayOpenCount > 0)
        {
            ResetStationPointerDelay();
            return;
        }

        if (_expanded && _host.HasExpandedContainedChild(_definition.Id))
        {
            ResetStationPointerDelay();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (!_expanded)
        {
            DisplayInfo display = _stationDisplay ??=
                DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
            if (!DisplayPlacementService.IsStationHotZone(
                    cursor,
                    display,
                    _definition.DockEdge,
                    _host.State.GlobalSettings.StationActivationDistanceDip))
            {
                _stationHotSince = 0;
                return;
            }

            _stationHotSince = _stationHotSince == 0 ? now : _stationHotSince;
            if (Stopwatch.GetElapsedTime(_stationHotSince, now).TotalMilliseconds <
                _host.State.GlobalSettings.StationHoverExpandDelayMs) return;
            _stationTransitionPending = true;
            ResetStationPointerDelay();
            try { await ExpandAsync(); }
            finally { _stationTransitionPending = false; }
            return;
        }

        if (!NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT bounds))
        {
            _stationOutsideSince = 0;
            return;
        }
        DisplayInfo expandedDisplay = _stationDisplay ??= DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
        if (DisplayPlacementService.IsStationExpandedSafeRegion(
                cursor,
                expandedDisplay,
                _definition.DockEdge,
                bounds,
                _host.State.GlobalSettings.StationActivationDistanceDip))
        {
            _stationOutsideSince = 0;
            return;
        }

        _stationOutsideSince = _stationOutsideSince == 0 ? now : _stationOutsideSince;
        if (Stopwatch.GetElapsedTime(_stationOutsideSince, now).TotalMilliseconds <
            _host.State.GlobalSettings.StationPointerLeaveCollapseDelayMs) return;
        _stationTransitionPending = true;
        ResetStationPointerDelay();
        try { await CollapseAsync(); }
        finally { _stationTransitionPending = false; }
    }

    private void ResetStationPointerDelay()
    {
        _stationHotSince = 0;
        _stationOutsideSince = 0;
    }

    private bool IsPointerOverThisWindow(NativeMethods.POINT pointer)
    {
        IntPtr hit = NativeMethods.WindowFromPoint(pointer);
        if (hit == _hwnd || NativeMethods.IsChild(_hwnd, hit)) return true;
        return _canvasResizeEdgeWindows.Any(edge => hit == edge || NativeMethods.IsChild(edge, hit));
    }

    private void ContextMenu_Opening(object? sender, object e)
    {
        UpdateContentModeMenuItems();
        if (_contextMenuActivated) return;
        _contextMenuActivated = true;
        _desktopLayer?.SetInputActivation(true);
        ResetStationPointerDelay();
    }

    private void ContextMenu_Opened(object? sender, object e)
    {
        if (ReferenceEquals(sender, ExpandedViewContextMenu))
        {
            _ = NativeMethods.GetCursorPos(out _contextMenuScreenPoint);
            try
            {
                DataPackageView data = Clipboard.GetContent();
                PasteMenuItem.IsEnabled = data.Contains(StandardDataFormats.StorageItems) ||
                    ShellDragService.TryGetClipboardPaths(out _, out _) ||
                    data.Contains(StandardDataFormats.Bitmap) ||
                    data.Contains(StandardDataFormats.Text);
            }
            catch
            {
                PasteMenuItem.IsEnabled = false;
            }
        }
        if (!_contextMenuCounted)
        {
            _contextMenuCounted = true;
            _overlayOpenCount++;
        }
    }

    private void ContextMenu_Closed(object? sender, object e)
    {
        RestoreContextMenuHost();
    }

    private void RestoreContextMenuHost()
    {
        if (_contextMenuCounted)
        {
            _contextMenuCounted = false;
            _overlayOpenCount = Math.Max(0, _overlayOpenCount - 1);
        }
        if (_contextMenuActivated)
        {
            _contextMenuActivated = false;
            _desktopLayer?.SetInputActivation(false);
        }
        ResetStationPointerDelay();
    }

    private async Task ImportFromDragAsync(DragEventArgs e)
    {
        if (!HasLocalFileDrop(e.DataView))
        {
            return;
        }

        DataPackageOperation operation = OrganizerInteractionMath.SelectDropOperation(e.AllowedOperations);
        e.AcceptedOperation = operation;
        if (operation == DataPackageOperation.None) return;
        bool move = operation == DataPackageOperation.Move;

        string[] paths = [];
        Exception? storageReadError = null;
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            try
            {
                IReadOnlyList<IStorageItem> storageItems = await e.DataView.GetStorageItemsAsync();
                paths = storageItems.Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                storageReadError = ex;
            }
        }

        if (paths.Length == 0)
        {
            _ = ShellDragService.TryGetFileDropPaths(e.DataView, out paths);
        }

        if (paths.Length == 0)
        {
            string reason = storageReadError is not null && !string.IsNullOrWhiteSpace(storageReadError.Message)
                ? storageReadError.Message
                : AppStrings.Get("NoImportableItems");
            ShowMessage(AppStrings.Format("ReadDropErrorFormat", reason), InfoBarSeverity.Error);
            return;
        }

        if (!_expanded)
        {
            await ExpandAsync(scrollToEnd: true);
        }

        _host.Notify("TuckPane", AppStrings.Format(
            move ? "MovingItemsFormat" : "CopyingItemsFormat",
            AppStrings.FormatItemCount(paths.Length)));
        var progress = new Progress<TransferProgress>(_ => { });

        try
        {
            IReadOnlyList<TransferOutcome> outcomes = await _host.TransferQueue.RunAsync(token => move
                ? _storage.ImportBatchAsync(paths, progress, token)
                : _storage.CopyBatchAsync(paths, progress, token));
            RebindMovedPortableWindows(outcomes);
            StartWatcher();
            await RefreshCatalogAsync(notifyUnsupported: false);
            await WaitForNextRenderAsync(CancellationToken.None);
            ScrollToEnd(animated: true);

            TransferOutcome[] warnings = outcomes.Where(outcome => move
                ? outcome.Status is not (TransferStatus.Moved or TransferStatus.ShortcutCreated)
                : outcome.Status is not (TransferStatus.Copied or TransferStatus.ShortcutCreated)).ToArray();
            if (warnings.Length == 0)
            {
                ShowMessage(AppStrings.Format(
                    move ? "MovedItemsFormat" : "CopiedItemsFormat",
                    AppStrings.FormatItemCount(outcomes.Count)), InfoBarSeverity.Success);
            }
            else
            {
                ShowMessage(string.Join(" ", warnings.Select(outcome => $"{Path.GetFileName(outcome.SourcePath)}：{outcome.Message}")), InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            ShowMessage(AppStrings.Get(move ? "MoveCancelled" : "CopyCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLogger.Error("拖入失败。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private static bool HasLocalFileDrop(DataPackageView dataView) =>
        dataView.Contains(StandardDataFormats.StorageItems) || ShellDragService.HasFileDrop(dataView);

    private async void WindowRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _itemReorderSession is not null)
        {
            e.Handled = true;
            CancelItemReorder();
        }
        else if (e.Key == VirtualKey.Escape && _expanded)
        {
            e.Handled = true;
            await CollapseAsync();
        }
    }

    private void ItemsScrollView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_expanded) return;
        ConfigureItemsLayout(updateItems: _canvasResize is null);
    }

    private void ScrollToEnd(bool animated)
    {
        ItemsScrollView.ScrollTo(0, ItemsScrollView.ScrollableHeight,
            new ScrollingScrollOptions(animated ? ScrollingAnimationMode.Enabled : ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
    }

    private async void RenameMenuItem_Click(object sender, RoutedEventArgs e) => await ShowRenameDialogAsync();

    private async void DuplicateWindowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _host.DuplicateOrganizerAsync(_definition.Id);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法复制收纳窗。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void PasteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _overlayOpenCount++;
        ResetStationPointerDelay();
        bool catalogRefreshed = false;
        try
        {
            DataPackageView data = Clipboard.GetContent();
            bool hasNativeFiles = ShellDragService.TryGetClipboardPaths(out string[] nativePaths, out bool nativeMove);
            if (data.Contains(StandardDataFormats.StorageItems) || hasNativeFiles)
            {
                string[] paths;
                bool move;
                try
                {
                    IReadOnlyList<IStorageItem> items = await data.GetStorageItemsAsync();
                    paths = items.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
                    move = data.RequestedOperation.HasFlag(DataPackageOperation.Move);
                }
                catch when (hasNativeFiles)
                {
                    paths = nativePaths;
                    move = nativeMove;
                }
                if (paths.Length == 0 && hasNativeFiles)
                {
                    paths = nativePaths;
                    move = nativeMove;
                }
                if (paths.Length == 0) return;

                var progress = new Progress<TransferProgress>(_ => { });
                IReadOnlyList<TransferOutcome> outcomes = await _host.TransferQueue.RunAsync(token => move
                    ? _storage.ImportBatchAsync(paths, progress, token)
                    : _storage.CopyBatchAsync(paths, progress, token));
                RebindMovedPortableWindows(outcomes);
                StartWatcher();
                await RefreshCatalogAsync(notifyUnsupported: false);
                await WaitForNextRenderAsync(CancellationToken.None);
                ScrollToEnd(animated: true);
                catalogRefreshed = true;
                TransferOutcome[] warnings = outcomes.Where(outcome => outcome.Status is not (
                    TransferStatus.Moved or TransferStatus.Copied or TransferStatus.ShortcutCreated)).ToArray();
                if (warnings.Length > 0)
                {
                    ShowMessage(string.Join(" ", warnings.Select(outcome => $"{Path.GetFileName(outcome.SourcePath)}：{outcome.Message}")), InfoBarSeverity.Warning);
                    return;
                }

                DataPackageOperation completed = move && outcomes.All(outcome => outcome.Status == TransferStatus.Moved)
                    ? DataPackageOperation.Move
                    : DataPackageOperation.Copy;
                data.ReportOperationCompleted(completed);
                ShowMessage(AppStrings.Format(move ? "MovedItemsFormat" : "CopiedItemsFormat", AppStrings.FormatItemCount(outcomes.Count)), InfoBarSeverity.Success);
            }
            else if (data.Contains(StandardDataFormats.Bitmap))
            {
                string path = await SaveClipboardBitmapAsync(data);
                data.ReportOperationCompleted(DataPackageOperation.Copy);
                ShowMessage(AppStrings.Format("PastedImageFormat", Path.GetFileName(path)), InfoBarSeverity.Success);
            }
            else if (data.Contains(StandardDataFormats.Text))
            {
                string text = await data.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text)) return;
                string notePath = await _host.CreateNoteAsync(_definition.Id, text, _contextMenuScreenPoint);
                await _host.OpenExternalNoteAsync(notePath);
                data.ReportOperationCompleted(DataPackageOperation.Copy);
                await WaitForNextRenderAsync(CancellationToken.None);
                ScrollToEnd(animated: true);
                return;
            }
            else
            {
                return;
            }

            if (!catalogRefreshed)
            {
                StartWatcher();
                await RefreshCatalogAsync(notifyUnsupported: false);
                await WaitForNextRenderAsync(CancellationToken.None);
                ScrollToEnd(animated: true);
            }
        }
        catch (OperationCanceledException)
        {
            ShowMessage(AppStrings.Get("PasteCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLogger.Error("从剪贴板粘贴失败。", ex);
            ShowMessage(AppStrings.Format("PasteFailedFormat", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _overlayOpenCount = Math.Max(0, _overlayOpenCount - 1);
            ResetStationPointerDelay();
        }
    }

    private async void NewNoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            NativeMethods.POINT anchor = _contextMenuScreenPoint;
            if (anchor.X == 0 && anchor.Y == 0 && NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT bounds))
            {
                anchor = new NativeMethods.POINT
                {
                    X = bounds.Left + bounds.Width / 2,
                    Y = bounds.Top + bounds.Height / 2
                };
            }
            await _host.CreateNoteAsync(_definition.Id, null, anchor);
            await WaitForNextRenderAsync(CancellationToken.None);
            ScrollToEnd(animated: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法创建便签。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task<string> SaveClipboardBitmapAsync(DataPackageView data)
    {
        RandomAccessStreamReference reference = await data.GetBitmapAsync();
        _storage.EnsureCreated();
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string destination = StorageService.GetUniquePath(Path.Combine(
            _storage.ItemsRoot,
            AppStrings.Format("PastedImageNameFormat", stamp)));
        string staging = Path.Combine(_storage.ItemsRoot, $".glassfolder-staging-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(staging)) { }
            using IRandomAccessStreamWithContentType input = await reference.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
            StorageFile stagingFile = await StorageFile.GetFileFromPathAsync(staging);
            using (IRandomAccessStream output = await stagingFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }
            File.Move(staging, destination);
            return destination;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private async void NewFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        bool station = _definition.PlacementMode == OrganizerPlacementMode.Station;
        _overlayOpenCount++;
        if (station)
        {
            _desktopLayer?.SetInputActivation(true);
            ResetStationPointerDelay();
        }
        string defaultName = AppStrings.Get("NewFolderDefaultName");
        string? createdPath = null;
        try
        {
            DisplayInfo display = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
            bool accepted = await OwnedDialogWindow.ShowTextInputAsync(
                _hwnd,
                display,
                _host,
                AppStrings.Get("NewFolderTitle"),
                defaultName,
                AppStrings.Get("Create"),
                AppStrings.Get("Cancel"),
                name =>
                {
                    try
                    {
                        createdPath = _storage.CreateUniqueFolder(name);
                        return null;
                    }
                    catch (Exception ex)
                    {
                        return ex.Message;
                    }
                });
            if (!accepted || createdPath is null) return;
            StartWatcher();
            await RefreshCatalogAsync(notifyUnsupported: false);
            await WaitForNextRenderAsync(CancellationToken.None);
            ScrollToEnd(animated: true);
            ShowMessage(AppStrings.Format("FolderCreatedFormat", Path.GetFileName(createdPath)), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLogger.Error("新建文件夹失败。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (station)
            {
                _desktopLayer?.SetInputActivation(false);
            }
            _overlayOpenCount = Math.Max(0, _overlayOpenCount - 1);
            ResetStationPointerDelay();
        }
    }

    private async void ToggleModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? error = await _host.ToggleOrganizerModeAsync(_definition.Id);
            if (error is not null) ShowMessage(error, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法切换收纳窗模式。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OpenStorageMenuItem_Click(object sender, RoutedEventArgs e) => OpenStorageDirectory();

    private async void DeleteWindowMenuItem_Click(object sender, RoutedEventArgs e) => await ShowDeleteDialogAsync();

    private async Task ShowDeleteDialogAsync()
    {
        if (!_expanded) await ExpandAsync();
        if (_host.TransferQueue.IsActive)
        {
            ShowMessage(AppStrings.Get("TransferBeforeDelete"), InfoBarSeverity.Warning);
            return;
        }

        _overlayOpenCount++;
        _desktopLayer?.SetInputActivation(true);
        string storagePath = AppPaths.ResolveStoragePath(_definition);
        bool directStorage = !string.IsNullOrWhiteSpace(_definition.StorageAbsolutePath);
        bool moveFiles = _host.State.GlobalSettings.MoveOrganizerFilesToDesktopOnDelete;
        string title = AppStrings.Format("DeleteTitleFormat", _definition.Name);
        string message = !moveFiles
            ? AppStrings.Format("DeleteKeepFilesFormat", storagePath)
            : directStorage
            ? FileCount > 0
                ? AppStrings.Format("DeleteDirectNonEmptyFormat", storagePath, AppStrings.FormatItemCount(FileCount), _definition.Name)
                : AppStrings.Format("DeleteDirectEmptyFormat", storagePath)
            : FileCount > 0
                ? AppStrings.Format("DeleteNonEmptyFormat", AppStrings.FormatItemCount(FileCount), _definition.Name)
                : AppStrings.Get("DeleteEmpty");
        try
        {
            bool confirmed;
            if (_definition.PlacementMode == OrganizerPlacementMode.Station)
            {
                confirmed = await OwnedDialogWindow.ShowConfirmationAsync(
                    _hwnd,
                    DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice),
                    _host,
                    title,
                    message,
                    AppStrings.Get(moveFiles ? "ExportDelete" : "DeleteOrganizerOnly"),
                    AppStrings.Get("Cancel"));
            }
            else
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = WindowRoot.XamlRoot,
                    Title = title,
                    Content = message,
                    PrimaryButtonText = AppStrings.Get(moveFiles ? "ExportDelete" : "DeleteOrganizerOnly"),
                    CloseButtonText = AppStrings.Get("Cancel"),
                    DefaultButton = ContentDialogButton.Close
                };
                confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            if (!confirmed) return;
            TransferOutcome outcome = await _host.DeleteOrganizerAsync(_definition.Id);
            if (outcome.Status is not (TransferStatus.Moved or TransferStatus.Retained))
            {
                ShowMessage(outcome.Message, InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法删除收纳窗。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (!_closing) _desktopLayer?.SetInputActivation(false);
            _overlayOpenCount = Math.Max(0, _overlayOpenCount - 1);
            ResetStationPointerDelay();
        }
    }

    private async Task ShowRenameDialogAsync()
    {
        if (!_expanded)
        {
            await ExpandAsync();
        }
        _overlayOpenCount++;
        _desktopLayer?.SetInputActivation(true);
        string acceptedName = _definition.Name;
        try
        {
            DisplayInfo display = NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT bounds)
                ? DisplayPlacementService.ForBounds(bounds)
                : DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
            bool accepted = await OwnedDialogWindow.ShowTextInputAsync(
                _hwnd,
                display,
                _host,
                AppStrings.Get("RenameTitle"),
                _definition.Name,
                AppStrings.Get("Save"),
                AppStrings.Get("Cancel"),
                candidate =>
                {
                    acceptedName = candidate.Trim();
                    return null;
                },
                maxLength: 40,
                placeholderText: AppStrings.Get("RenamePlaceholder"));
            if (accepted && acceptedName.Length > 0)
            {
                _definition.Name = acceptedName;
                string? error = _host.ApplyOrganizerRuntime(_definition, OrganizerVisualChange.Name);
                if (error is not null)
                {
                    ShowMessage(error, InfoBarSeverity.Error);
                    return;
                }
                _host.Console.RefreshAll(_definition.Id);
                await SaveStateAsync();
            }
        }
        finally
        {
            _desktopLayer?.SetInputActivation(false);
            _overlayOpenCount = Math.Max(0, _overlayOpenCount - 1);
            ResetStationPointerDelay();
        }
    }

    private void OpenStorageDirectory()
    {
        try
        {
            _storage.EnsureCreated();
            Process.Start(new ProcessStartInfo(_storage.ItemsRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法打开收纳目录：{_storage.ItemsRoot}", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ManageMenuItem_Click(object sender, RoutedEventArgs e) => _host.OpenConsole(_definition.Id);

    private void DesktopRepairTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!_expanded && !_animating && !_pressActive)
        {
            if (_definition.PlacementMode == OrganizerPlacementMode.Station)
            {
                DisplayInfo display = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
                _stationDisplay = display;
                NativeMethods.RECT anchor = DisplayPlacementService.CalculateStationAnchor(display, _definition.DockEdge, _definition.Position);
                WidgetPosition position = DisplayPlacementService.Capture(anchor);
                if (!RectsEqual(anchor, _compactBounds) || !PositionsEqual(_definition.Position, position))
                {
                    _compactBounds = anchor;
                    _definition.Position = position;
                    _ = SaveStateAsync();
                }
                _appWindow?.Hide();
                return;
            }
            if (_definition.PlacementMode == OrganizerPlacementMode.Positioned)
            {
                DesktopGridPlacement? placement = _host.FindCurrentPositionedPlacement(_definition.Id, _compactBounds);
                if (placement is not null && !RectsEqual(placement.Bounds, _compactBounds))
                {
                    MoveToPositionedPlacement(placement.Bounds, placement.CompactScale);
                    _definition.Position = DisplayPlacementService.Capture(_compactBounds, _hwnd);
                    _ = SaveStateAsync();
                }
            }
            else
            {
                NativeMethods.RECT work = DisplayPlacementService.ForBounds(_compactBounds).Work;
                bool hasVisibleEdgePosition = _compactBounds.Left < work.Left || _compactBounds.Top < work.Top ||
                    _compactBounds.Right > work.Right || _compactBounds.Bottom > work.Bottom;
                WindowAlignmentInsets alignmentInsets = default;
                bool clampAlignmentFrame = _definition.PlacementMode == OrganizerPlacementMode.Floating &&
                    (_host.State.GlobalSettings.WindowAlignmentEnabled || hasVisibleEdgePosition) &&
                    TryGetCompactAlignmentInsets(_compactBounds, out alignmentInsets);
                NativeMethods.RECT corrected = clampAlignmentFrame
                    ? WindowAlignmentMath.ClampFrame(_compactBounds, work, alignmentInsets)
                    : DisplayPlacementService.Clamp(_compactBounds, work);
                if (!RectsEqual(corrected, _compactBounds))
                {
                    _compactBounds = corrected;
                    ApplyBounds(_compactBounds, show: true);
                    _definition.Position = DisplayPlacementService.Capture(_compactBounds, _hwnd);
                    _ = SaveStateAsync();
                }
            }
            _desktopLayer?.Reattach();
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closing = true;
        _host.ThemeChanged -= Host_ThemeChanged;
        if (_itemReorderSession is not null) CancelItemReorder(runPendingRefresh: false);
        ShutdownItemDragBoundaryHook();
        StopNativeItemMotionRendering(snapToTargets: true);
        _desktopRepairTimer.Stop();
        _watcherDebounceTimer.Stop();
        _interactionSaveTimer.Stop();
        _canvasResizeInputTimer.Stop();
        _stationPointerTimer.Stop();
        _externalHoverTimer.Stop();
        _longPressTimer.Stop();
        StopCanvasResizeRendering();
        StopDragClock();
        _transitionCancellation?.Cancel();
        _transitionCancellation?.Dispose();
        _compactClip?.Dispose();
        _expandedClip?.Dispose();
        _compactSurface.Dispose();
        _expandedSurface.Dispose();
        _windowAlignmentGuide?.Dispose();
        _windowAlignmentGuide = null;
        _outsideClickHook?.Dispose();
        _watcher?.Dispose();
        foreach (IntPtr edgeWindow in _canvasResizeEdgeWindows)
        {
            RestoreCanvasResizeWindowProc(edgeWindow);
            if (NativeMethods.IsWindow(edgeWindow)) _ = NativeMethods.DestroyWindow(edgeWindow);
        }
        _canvasResizeEdgeWindows.Clear();
        foreach (IntPtr window in _canvasResizeOriginalWindowProcs.Keys.ToArray())
            RestoreCanvasResizeWindowProc(window);
        if (_hwnd != IntPtr.Zero)
        {
            _ = NativeMethods.RemoveWindowSubclass(_hwnd, _gestureWindowProc, GestureSubclassId);
        }
        _desktopLayer?.Dispose();
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        _host.Notify("TuckPane", message, severity is InfoBarSeverity.Warning or InfoBarSeverity.Error);
    }

    private async Task SaveStateAsync()
    {
        try
        {
            await _host.SaveStateAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("保存配置失败。", ex);
            if (!_closing) ShowMessage(AppStrings.Get("SaveConfigurationError"), InfoBarSeverity.Warning);
        }
    }

    internal Guid OrganizerId => _definition.Id;
    internal Guid? ContainerStationId => _definition.ContainerStationId;
    private bool IsContained => _definition.ContainerStationId is not null;
    internal bool IsExpanded => _expanded || _animating;
    internal bool IsShellDragActive => Volatile.Read(ref _shellDragActive);
    internal void ApplyOutsideClickSetting()
    {
        if (_definition.PlacementMode != OrganizerPlacementMode.Station &&
            _expanded && !_animating && _host.State.GlobalSettings.CollapseOnOutsideClick)
            _outsideClickHook?.Start();
        else
            _outsideClickHook?.Stop();
    }
    internal int FileCount => _items.Count(item => item.Kind is not (WidgetItemKind.Note or WidgetItemKind.Organizer));
    internal Task RefreshNotesAsync() => RefreshCatalogAsync(notifyUnsupported: false);
    internal Task RefreshContainedOrganizerItemsAsync() => RefreshCatalogAsync(notifyUnsupported: false);
    internal IReadOnlyList<WidgetItem> ItemSnapshot => _items;
    internal bool StorageExists => _storage.Exists;
    internal NativeMethods.RECT CompactBounds => _compactBounds;
    internal double AppliedCompactScale => _appliedCompactScale;

    internal bool TryGetCollapsedFloatingAlignmentFrame(out NativeMethods.RECT bounds)
    {
        bounds = default;
        if (_closing || _appWindow is not { IsVisible: true } ||
            _definition.PlacementMode != OrganizerPlacementMode.Floating ||
            _expanded || _animating) return false;
        return TryGetCompactAlignmentFrame(out bounds);
    }

    internal void RefreshWindowAlignmentSetting()
    {
        if (!_host.State.GlobalSettings.WindowAlignmentEnabled) ClearWindowAlignment();
    }

    internal bool ContainsScreenPoint(NativeMethods.POINT point) =>
        !_closing && _expanded && NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT bounds) &&
        DragBoundaryMath.Contains(bounds, point);

    internal bool TryGetStationDropIndex(NativeMethods.POINT screenPoint, out int insertionIndex)
    {
        insertionIndex = 0;
        if (_definition.PlacementMode != OrganizerPlacementMode.Station || _animating || !ContainsScreenPoint(screenPoint)) return false;
        NativeMethods.POINT clientPoint = screenPoint;
        if (!NativeMethods.ScreenToClient(_hwnd, ref clientPoint)) return false;
        double scale = Math.Max(1, WindowRoot.XamlRoot?.RasterizationScale ?? 1);
        Point scrollOrigin;
        Point origin;
        try
        {
            scrollOrigin = ItemsScrollView.TransformToVisual(WindowRoot).TransformPoint(new Point());
            origin = ItemsRepeater.TransformToVisual(WindowRoot).TransformPoint(new Point());
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        double clientXDip = clientPoint.X / scale;
        double clientYDip = clientPoint.Y / scale;
        if (clientXDip < scrollOrigin.X || clientYDip < scrollOrigin.Y ||
            clientXDip > scrollOrigin.X + ItemsScrollView.ActualWidth ||
            clientYDip > scrollOrigin.Y + ItemsScrollView.ActualHeight) return false;
        double x = clientXDip - origin.X;
        double y = clientYDip - origin.Y;
        Size viewport = GetItemsViewportSize();
        (double width, double height) = GetItemCellSizeDip(viewport.Width, viewport.Height);
        double gap = GetItemLayoutGapDip();
        int columns = GetItemLayoutColumnCount();
        int column = Math.Clamp((int)Math.Floor(Math.Max(0, x) / Math.Max(1, width + gap)), 0, columns - 1);
        int row = Math.Max(0, (int)Math.Floor(Math.Max(0, y) / Math.Max(1, height + gap)));
        int candidate = row * columns + column;
        if (x - column * (width + gap) > width / 2) candidate++;
        insertionIndex = Math.Clamp(candidate, 0, _items.Count);
        return true;
    }

    internal void RefreshOrganizerPreview(Guid organizerId)
    {
        string key = OrganizerInteractionMath.OrganizerItemKey(organizerId);
        if (TryGetRealizedItemHost(key, out Border host) &&
            _items.FirstOrDefault(item => item.RelativeName.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } item)
        {
            PrepareItemElement(host, item, loadIcon: true);
        }
    }

    private async void NewTodoMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            NativeMethods.POINT anchor = _contextMenuScreenPoint;
            if (anchor.X == 0 && anchor.Y == 0 && NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT bounds))
            {
                anchor = new NativeMethods.POINT
                {
                    X = bounds.Left + bounds.Width / 2,
                    Y = bounds.Top + bounds.Height / 2
                };
            }
            await _host.CreateTodoAsync(_definition.Id, anchor);
            await WaitForNextRenderAsync(CancellationToken.None);
            ScrollToEnd(animated: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法创建待办。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void ToggleContentModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? error = await _host.ToggleOrganizerExpandedContentModeAsync(_definition.Id);
            if (error is not null) ShowMessage(error, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法切换收纳窗内容模式。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private static bool IsDocumentItem(WidgetItem item) =>
        item.Kind is WidgetItemKind.Note or WidgetItemKind.PortableNote or WidgetItemKind.PortableTodo;

    private BitmapImage GetDocumentIcon(WidgetItem item) =>
        item.Kind == WidgetItemKind.PortableTodo ? _todoIcon : _noteIcon;

    internal async Task<bool> ImportFromPeerAsync(string path)
    {
        IReadOnlyList<TransferOutcome> outcomes = await _host.TransferQueue.RunAsync(
            token => _storage.ImportBatchAsync([path], progress: null, token));
        RebindMovedPortableWindows(outcomes);
        bool moved = outcomes.Any(outcome => outcome.Status is TransferStatus.Moved or TransferStatus.ShortcutCreated);
        if (moved)
        {
            StartWatcher();
            await RefreshCatalogAsync(notifyUnsupported: false);
        }
        return moved;
    }

    private void RebindMovedPortableWindows(IEnumerable<TransferOutcome> outcomes)
    {
        foreach (TransferOutcome outcome in outcomes)
        {
            if (outcome.Status == TransferStatus.Moved && outcome.DestinationPath is { } destination)
                _host.RebindPortableWindowAfterMove(outcome.SourcePath, destination);
        }
    }

    internal Task CollapseForPeerAsync() => CollapseAsync();

    internal void SetVisible(bool visible)
    {
        if (_appWindow is null) return;
        if (IsContained)
        {
            if (visible) _appWindow.Hide();
            else SetContained(true);
            RefreshPerformanceSettings();
            return;
        }
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            _stationVisible = visible;
            if (visible && _expanded) ApplyBounds(CalculateExpandedBounds(_compactBounds), show: true);
            else _appWindow.Hide();
            RefreshPerformanceSettings();
            return;
        }
        _runtimeVisible = visible;
        if (visible)
        {
            _desktopLayer?.BringAboveDesktopPeers();
            ApplyBounds(_expanded ? CalculateExpandedBounds(_compactBounds) : _compactBounds, show: true);
        }
        else
        {
            _appWindow.Hide();
        }
        RefreshPerformanceSettings();
    }

    internal Task ExpandContainedAsync(NativeMethods.RECT anchor)
    {
        if (!IsContained || anchor.Width <= 0 || anchor.Height <= 0) return Task.CompletedTask;
        _containedAnchorBounds = anchor;
        return ExpandAsync();
    }

    internal void MoveContainedDragPreview(NativeMethods.POINT cursor, MainWindow station)
    {
        if (!IsContained || _appWindow is null) return;
        DisplayInfo display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
        {
            Left = cursor.X,
            Top = cursor.Y,
            Right = cursor.X + 1,
            Bottom = cursor.Y + 1
        });
        int width = Math.Max(1, _compactBounds.Width);
        int height = Math.Max(1, _compactBounds.Height);
        var desired = new NativeMethods.RECT
        {
            Left = cursor.X - width / 2,
            Top = cursor.Y - height / 2,
            Right = cursor.X - width / 2 + width,
            Bottom = cursor.Y - height / 2 + height
        };
        NativeMethods.RECT bounds = DisplayPlacementService.CalculateDraggedBounds(desired, cursor, cursor, display.Work);
        ExpandedView.Visibility = Visibility.Collapsed;
        CompactView.Visibility = Visibility.Visible;
        UpdateSurfaceClips();
        _desktopLayer?.SetExpanded(true, stayTopmost: true);
        _desktopLayer?.SetTransientOwner(station._hwnd);
        ApplyBounds(bounds, show: true);
    }

    internal void SetContained(bool contained)
    {
        if (_appWindow is null) return;
        if (contained)
        {
            _transitionCancellation?.Cancel();
            _expanded = false;
            _animating = false;
            CompactView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Collapsed;
            _desktopLayer?.SetExpanded(false);
            _appWindow.Hide();
            RefreshPerformanceSettings();
            _host.NotifyCollapsed(this);
            return;
        }
        _runtimeVisible = true;
        ShowCompactPlacement(_compactBounds);
    }

    internal void ClosePermanently()
    {
        _closing = true;
        Close();
    }

    internal void ApplyDefinition(OrganizerVisualChange changes)
    {
        if (_itemReorderSession is not null) CancelItemReorder();
        if ((changes & (OrganizerVisualChange.PlacementMode | OrganizerVisualChange.ExpandedContentMode)) != 0)
        {
            ApplyExpandedContentInset();
            if ((changes & OrganizerVisualChange.PlacementMode) != 0) ApplyLanguage();
        }
        if ((changes & (OrganizerVisualChange.Name | OrganizerVisualChange.NameScale)) != 0) UpdateOrganizerName();
        if ((changes & (OrganizerVisualChange.CompactScale | OrganizerVisualChange.NameScale | OrganizerVisualChange.PlacementMode)) != 0)
        {
            ApplyCompactScale(repositionWindow: !_expanded && !_animating);
        }

        if ((changes & OrganizerVisualChange.ItemScale) != 0) UpdateCompactPreviewItemScale();
        bool changesExpandedGeometry = (changes & (OrganizerVisualChange.Layout | OrganizerVisualChange.CanvasScale | OrganizerVisualChange.ExpandedContentMode)) != 0 ||
            _definition.PlacementMode == OrganizerPlacementMode.Station && (changes & OrganizerVisualChange.ItemScale) != 0;
        if ((_expanded || _animating) && changesExpandedGeometry)
        {
            _transitionCancellation?.Cancel();
            _transitionCancellation?.Dispose();
            _transitionCancellation = null;
            if (!NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT current)) current = CalculateExpandedBounds(_compactBounds);
            NativeMethods.RECT target = _definition.PlacementMode == OrganizerPlacementMode.Station
                ? CalculateExpandedBounds(_compactBounds)
                : CalculateExpandedBoundsAroundCenter(current);
            _expanded = true;
            _animating = false;
            _transitionProgress = 1;
            _transitionVelocity = 0;
            ClearStationTransitionVisuals();
            ApplyBounds(target, show: true);
            CompactView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Visible;
            ExpandedView.Opacity = 1;
            GetExpandedCompositionVisual().Scale = Vector3.One;
            ConfigureItemsLayout();
            UpdateSurfaceClips();
        }
        else if (_expanded && (changes & (OrganizerVisualChange.ItemScale | OrganizerVisualChange.CompactListItemScale)) != 0)
        {
            ClearStationTransitionVisuals();
            ConfigureItemsLayout();
        }
        if ((changes & OrganizerVisualChange.PlacementMode) != 0) RefreshPerformanceSettings();
    }

    internal void RecreateStorage()
    {
        _storage.EnsureCreated();
        StartWatcher();
        _ = RefreshCatalogAsync(notifyUnsupported: false, refreshIcons: true);
    }

    private void ApplyTheme()
    {
        ThemeValues theme = _host.State.GlobalSettings.GetTheme(ThemeTarget.Organizer);
        WindowRoot.RequestedTheme = ThemePalette.IsDark(theme) ? ElementTheme.Dark : ElementTheme.Light;
        bool useEffects = _uiSettings.AdvancedEffectsEnabled;
        _compactSurface.SetTheme(theme, useEffects);
        _expandedSurface.SetTheme(theme, useEffects);
        var foregroundColor = ThemePalette.ForegroundColor(theme);
        var foreground = new SolidColorBrush(foregroundColor);
        _hoveredItemBrush.Color = ColorHelper.FromArgb(22, foregroundColor.R, foregroundColor.G, foregroundColor.B);
        _pressedItemBrush.Color = ColorHelper.FromArgb(38, foregroundColor.R, foregroundColor.G, foregroundColor.B);
        _collapseHoverBrush.Color = ColorHelper.FromArgb(26, foregroundColor.R, foregroundColor.G, foregroundColor.B);
        _collapsePressedBrush.Color = ColorHelper.FromArgb(46, foregroundColor.R, foregroundColor.G, foregroundColor.B);
        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        CompactWarningBadge.Background = new SolidColorBrush(highContrast
            ? _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent)
            : ColorHelper.FromArgb(184, 29, 35, 43));
        CompactWarningIcon.Foreground = new SolidColorBrush(highContrast
            ? _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background)
            : ColorHelper.FromArgb(255, 255, 200, 87));
        CollapseDash.Background = foreground;
        CollapseButtonSurface.Background = CollapseButton.IsPointerOver ? _collapseHoverBrush : _transparentItemBrush;
        UpdateRealizedItems();
    }

    private void Host_ThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    internal void MoveToPositionedPlacement(NativeMethods.RECT bounds, double runtimeScale)
    {
        _runtimeVisible = true;
        DisplayInfo display = DisplayPlacementService.ForBounds(bounds);
        _positionedCompactWidthDip = bounds.Width / display.Scale;
        _positionedCompactHeightDip = bounds.Height / display.Scale;
        ApplyCompactScale(repositionWindow: false, runtimeScale: runtimeScale);
        ShowCompactPlacement(bounds);
    }

    internal void MoveToFloatingPlacement(NativeMethods.RECT bounds)
    {
        _runtimeVisible = true;
        ApplyCompactScale(repositionWindow: false);
        ShowCompactPlacement(bounds);
    }

    internal void MoveToStationPlacement(NativeMethods.RECT anchor)
    {
        ApplyExpandedContentInset();
        UpdateOrganizerName();
        _transitionCancellation?.Cancel();
        _outsideClickHook?.Stop();
        _canvasResizeInputTimer.Stop();
        UpdateCanvasResizeEdgeWindows(show: false);
        _expanded = false;
        _animating = false;
        _transitionProgress = 0;
        _transitionVelocity = 0;
        _compactBounds = anchor;
        _stationDisplay = DisplayPlacementService.ForBounds(anchor);
        CompactView.Visibility = Visibility.Collapsed;
        ExpandedView.Visibility = Visibility.Collapsed;
        ExpandedView.Opacity = 0;
        _desktopLayer?.SetExpanded(false);
        _appWindow?.Hide();
        _host.NotifyCollapsed(this);
        ResetStationPointerDelay();
        RefreshPerformanceSettings();
        ApplyLanguage();
    }

    private void ShowCompactPlacement(NativeMethods.RECT bounds)
    {
        ApplyExpandedContentInset();
        UpdateOrganizerName();
        _transitionCancellation?.Cancel();
        _outsideClickHook?.Stop();
        _canvasResizeInputTimer.Stop();
        UpdateCanvasResizeEdgeWindows(show: false);
        _expanded = false;
        _animating = false;
        _transitionProgress = 0;
        _transitionVelocity = 0;
        _compactBounds = bounds;
        ExpandedView.Visibility = Visibility.Collapsed;
        ExpandedView.Opacity = 0;
        CompactView.Visibility = Visibility.Visible;
        CompactView.Opacity = 1;
        UpdateSurfaceClips();
        _desktopLayer?.SetExpanded(false);
        ApplyBounds(_compactBounds, show: true);
        _host.NotifyCollapsed(this);
        RefreshPerformanceSettings();
        ApplyLanguage();
    }

    internal bool IsCompactOrganizerDragActive => _widgetDragging && _widgetDragTopmost && !_draggingExpanded;

    internal void RaiseActiveCompactOrganizerDrag() => _desktopLayer?.SetExpanded(true, stayTopmost: true);

    private void ApplyCompactScale(bool repositionWindow = true, double? runtimeScale = null)
    {
        bool positioned = _definition.PlacementMode == OrganizerPlacementMode.Positioned;
        if (!positioned)
        {
            _positionedCompactWidthDip = 0;
            _positionedCompactHeightDip = 0;
        }
        double scale = runtimeScale ?? (_definition.PlacementMode == OrganizerPlacementMode.Positioned
            ? _appliedCompactScale
            : _definition.CompactScale);
        double compactNameScale = _host.State.GlobalSettings.ResolveCompactNameScale(_definition.PlacementMode);
        _appliedCompactScale = scale;
        CompactView.Width = GetCompactWidthDip();
        CompactView.Height = GetCompactHeightDip();
        CompactTile.Width = 39 * scale;
        CompactTile.Height = 39 * scale;
        UpdateCompactThumbnailMetrics();
        UpdateCompactPreviewItemScale();
        CompactNameText.MaxWidth = positioned
            ? Math.Max(1, CompactView.Width)
            : 72 * scale * compactNameScale;
        CompactNameText.FontSize = positioned
            ? 13 * compactNameScale
            : 13 * scale * compactNameScale;
        CompactView.RowDefinitions[0].Height = new GridLength(39 * scale);
        CompactView.RowDefinitions[1].Height = new GridLength(positioned ? 4 : 4 * scale);
        CompactView.RowDefinitions[2].Height = new GridLength(positioned
            ? Math.Max(1, CompactView.Height - 39 * scale - 4)
            : 23 * scale * compactNameScale);

        if (_hwnd != IntPtr.Zero)
        {
            if (_definition.PlacementMode == OrganizerPlacementMode.Station)
            {
                DisplayInfo stationDisplay = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
                _compactBounds = DisplayPlacementService.CalculateStationAnchor(stationDisplay, _definition.DockEdge, _definition.Position);
                if (repositionWindow) _appWindow?.Hide();
                UpdateSurfaceClips();
                return;
            }
            DisplayInfo display = DisplayPlacementService.ForBounds(_compactBounds);
            int width = DipToPx(GetCompactWidthDip(), display.Scale);
            int height = DipToPx(GetCompactHeightDip(), display.Scale);
            int iconCenterX = _compactBounds.Left + _compactBounds.Width / 2;
            int iconCenterY = _compactBounds.Top + DipToPx(19.5 * _appliedCompactScale, display.Scale);
            _compactBounds.Left = iconCenterX - width / 2;
            _compactBounds.Top = iconCenterY - DipToPx(19.5 * scale, display.Scale);
            _compactBounds.Right = _compactBounds.Left + width;
            _compactBounds.Bottom = _compactBounds.Top + height;
            _compactBounds = DisplayPlacementService.Clamp(_compactBounds, display.Work);
            if (repositionWindow && !IsContained) ApplyBounds(_compactBounds, show: true);
        }
        UpdateSurfaceClips();
    }

    private double GetCompactWidthDip() =>
        _definition.PlacementMode == OrganizerPlacementMode.Positioned && _positionedCompactWidthDip > 0
            ? _positionedCompactWidthDip
            : OrganizerLimits.CalculateCompactWindowWidthDip(
                _appliedCompactScale,
                _host.State.GlobalSettings.ResolveCompactNameScale(_definition.PlacementMode));

    private double GetCompactHeightDip() =>
        _definition.PlacementMode == OrganizerPlacementMode.Positioned && _positionedCompactHeightDip > 0
            ? _positionedCompactHeightDip
            : OrganizerLimits.CalculateCompactWindowHeightDip(
                _appliedCompactScale,
                _host.State.GlobalSettings.ResolveCompactNameScale(_definition.PlacementMode));

    private int GetExpandedTitleBandPx(double displayScale) =>
        _definition.PlacementMode == OrganizerPlacementMode.Station
            ? 0
            : (int)Math.Round(DisplayPlacementService.ExpandedTitleBandDip * displayScale);

    private NativeMethods.RECT CalculateExpandedBoundsAroundCenter(NativeMethods.RECT current)
    {
        var compactAtCenter = _compactBounds;
        int centerX = current.Left + current.Width / 2;
        DisplayInfo display = DisplayPlacementService.ForBounds(current) with
        {
            Scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d)
        };
        int titleHeightPx = GetExpandedTitleBandPx(display.Scale);
        int panelHeightPx = Math.Max(1, current.Height - titleHeightPx);
        int centerY = current.Top + titleHeightPx + panelHeightPx / 2;
        compactAtCenter.Left = centerX - compactAtCenter.Width / 2;
        compactAtCenter.Right = compactAtCenter.Left + _compactBounds.Width;
        compactAtCenter.Top = centerY - DipToPx(19.5 * _appliedCompactScale, display.Scale);
        compactAtCenter.Bottom = compactAtCenter.Top + _compactBounds.Height;
        return CalculateExpandedBounds(compactAtCenter);
    }

    private bool NormalizeVisualScales(DisplayInfo display)
    {
        bool station = _definition.PlacementMode == OrganizerPlacementMode.Station;
        double canvas;
        double maximumItem;
        if (station)
        {
            canvas = Math.Clamp(_definition.CanvasScale, .1, 1.2);
            maximumItem = DisplayPlacementService.CalculateMaximumStationItemScale(display, _definition.Layout);
        }
        else
        {
            double minimumCanvas;
            if (_definition.ManualCanvasBaseWidthDip is double baseWidth &&
                _definition.ManualCanvasBaseHeightDip is double baseHeight)
            {
                (double minimumWidth, double minimumHeight) =
                    DisplayPlacementService.CalculateMinimumExpandedSizeDip(_definition.Layout, .5);
                minimumCanvas = Math.Min(1.2,
                    Math.Max(.1, Math.Max(minimumWidth / baseWidth, minimumHeight / baseHeight)));
            }
            else
            {
                minimumCanvas = DisplayPlacementService.CalculateMinimumCanvasScale(display, _definition.Layout);
            }
            canvas = Math.Clamp(_definition.CanvasScale, minimumCanvas, 1.2);
            if (_definition.ManualCanvasBaseWidthDip is double manualWidth &&
                _definition.ManualCanvasBaseHeightDip is double manualHeight)
            {
                NativeMethods.RECT work = DisplayPlacementService.GetExpandedWorkArea(display);
                double availablePanelHeightDip = Math.Max(
                    1,
                    work.Height / display.Scale - DisplayPlacementService.ExpandedTitleBandDip);
                double fit = Math.Min(1, Math.Min(
                    work.Width / display.Scale / (manualWidth * canvas),
                    availablePanelHeightDip / (manualHeight * canvas)));
                maximumItem = DisplayPlacementService.CalculateMaximumItemScaleForExpandedSize(
                    _definition.Layout,
                    manualWidth * canvas * fit,
                    manualHeight * canvas * fit);
            }
            else
            {
                maximumItem = DisplayPlacementService.CalculateMaximumItemScale(display, _definition.Layout, canvas);
            }
        }
        double item = Math.Clamp(_definition.ItemScale, .5, maximumItem);
        bool changed = Math.Abs(canvas - _definition.CanvasScale) > .0001 || Math.Abs(item - _definition.ItemScale) > .0001;
        _definition.CanvasScale = canvas;
        _definition.ItemScale = item;
        return changed;
    }

    private void ApplyBounds(NativeMethods.RECT bounds, bool show, bool preserveZOrder = false)
    {
        uint flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER |
            (preserveZOrder ? NativeMethods.SWP_NOZORDER : 0) |
            (show ? NativeMethods.SWP_SHOWWINDOW : 0);
        _ = NativeMethods.SetWindowPos(_hwnd, preserveZOrder ? IntPtr.Zero : NativeMethods.HWND_TOP,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags);
        if (_expanded && !_animating && _canvasResizeEdgeWindows.Count > 0)
            UpdateCanvasResizeEdgeWindows(show);
    }

    private static int DipToPx(double dip, double scale) => Math.Max(1, (int)Math.Round(dip * scale));

    private static bool RectsEqual(NativeMethods.RECT first, NativeMethods.RECT second) =>
        first.Left == second.Left && first.Top == second.Top && first.Right == second.Right && first.Bottom == second.Bottom;

    private static bool PositionsEqual(WidgetPosition? first, WidgetPosition second) =>
        first is not null && string.Equals(first.MonitorDevice, second.MonitorDevice, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs(first.XDip - second.XDip) < .01 && Math.Abs(first.YDip - second.YDip) < .01 &&
        Math.Abs(first.SavedWorkAreaWidthDip - second.SavedWorkAreaWidthDip) < .01 &&
        Math.Abs(first.SavedWorkAreaHeightDip - second.SavedWorkAreaHeightDip) < .01;

    private sealed record CollapseTransitionGeometry(
        NativeMethods.RECT ExpandedBounds,
        NativeMethods.RECT CompactBounds,
        int EndExpandedLeft,
        int EndExpandedTop);

    private void UpdateCompactThumbnailMetrics()
    {
        CompactThumbnailHost.Width = CompactThumbnailHost.Height = 39 * _appliedCompactScale;
    }

    private void UpdateSurfaceClips()
    {
        if (!WindowRoot.IsLoaded) return;
        double compactRadius = SnapDip(CompactCornerRadiusDip * _appliedCompactScale);
        double expandedRadius = SnapDip(ExpandedCornerRadiusDip);
        _compactSurface.SetCornerRadius(compactRadius);
        _expandedSurface.SetCornerRadius(expandedRadius);
        Visual compactContentVisual = ElementCompositionPreview.GetElementVisual(CompactIconPresenter);
        Visual expandedContentVisual = ElementCompositionPreview.GetElementVisual(ExpandedContentLayer);
        _compactClip ??= compactContentVisual.Compositor.CreateRectangleClip();
        _expandedClip ??= expandedContentVisual.Compositor.CreateRectangleClip();
        ApplyPixelAlignedClip(_compactClip, CompactThumbnailHost, compactRadius);
        ApplyPixelAlignedClip(_expandedClip, ExpandedContentLayer, expandedRadius);
        compactContentVisual.Clip = _compactClip;
        expandedContentVisual.Clip = _expandedClip;
    }

    private Visual GetExpandedCompositionVisual() =>
        _expandedCompositionVisual ??= ElementCompositionPreview.GetElementVisual(ExpandedView);

    private void UpdateExpandedClipRadius(double progress)
    {
        if (_expandedClip is null) UpdateSurfaceClips();
        if (_expandedClip is null) return;
        double radius = CompactCornerRadiusDip * _appliedCompactScale +
            (ExpandedCornerRadiusDip - CompactCornerRadiusDip * _appliedCompactScale) * progress;
        double snappedRadius = SnapDip(radius);
        SetClipRadius(_expandedClip, snappedRadius);
        _expandedSurface.SetCornerRadius(snappedRadius);
    }

    private void ApplyPixelAlignedClip(RectangleClip clip, FrameworkElement element, double radius)
    {
        float width = (float)SnapDip(element.ActualWidth > 0 ? element.ActualWidth : element.Width);
        float height = (float)SnapDip(element.ActualHeight > 0 ? element.ActualHeight : element.Height);
        clip.Left = 0;
        clip.Top = 0;
        clip.Right = Math.Max(0, width);
        clip.Bottom = Math.Max(0, height);
        SetClipRadius(clip, Math.Min(SnapDip(radius), Math.Min(width, height) / 2));
    }

    private static void SetClipRadius(RectangleClip clip, double radius)
    {
        var value = new Vector2((float)Math.Max(0, radius));
        clip.TopLeftRadius = value;
        clip.TopRightRadius = value;
        clip.BottomLeftRadius = value;
        clip.BottomRightRadius = value;
    }

    private double SnapDip(double value)
    {
        double scale = Math.Max(1, WindowRoot.XamlRoot?.RasterizationScale ?? 1);
        return Math.Round(value * scale) / scale;
    }
}
