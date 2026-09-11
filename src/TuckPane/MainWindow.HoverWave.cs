using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace TuckPane;

public sealed partial class MainWindow
{
    // Internal opt-in: keep the icon wave implementation available without a UI setting.
    internal const bool IconHoverWaveEnabled = false;
    private bool UseHoverWave => UseCustomAnimations && OrganizerHoverWave.IsEnabledFor(_definition.PlacementMode, IsCompactList, IconHoverWaveEnabled,
        _host.State.GlobalSettings.CompactHoverMagnificationEnabled, _host.State.GlobalSettings.DockHoverMagnificationEnabled);
    private float HoverMaximumScale => (float)GlobalSettings.NormalizeHoverMagnificationScale(IsDock
        ? _host.State.GlobalSettings.DockHoverMagnificationScale
        : IsCompactList ? _host.State.GlobalSettings.CompactHoverMagnificationScale : OrganizerHoverWave.MaximumScale);

    internal void RefreshHoverPreference()
    {
        if (HorizontalHover) ApplyDockLayout();
        if (!UseHoverWave) ResetHoverWave();
        RefreshHoverAvailability();
    }

    private readonly OrganizerHoverWave _hoverWave = new();
    private readonly Dictionary<Border, HoverItemVisual> _hoverItems = new();
    private Border? _hoverPressedHost;
    private bool _hoverRenderingSubscribed;
    private bool _hoverGeometryDirty = true;
    private bool _hoverLayoutPending;
    private bool _hoverTargetsDirty = true;
    private long _hoverLastFrame;
    private readonly HoverAnchorMotion _hoverAnchor = new();
    private readonly DockHoverMotion _dockHoverMotion = new();
    private KeyValuePair<Border, HoverItemVisual>[] _hoverLayout = [];
    private HoverLayoutItem[] _hoverLayoutInput = [];
    private HoverLayoutPose[] _hoverLayoutPoses = [];
    private HoverHitRegion[] _hoverHitRegions = [];
    private float[] _hoverLayoutCenters = [], _hoverLayoutScales = [];
    private DispatcherQueueTimer? _hoverPointerGuard;

    private sealed class HoverItemVisual(StackPanel content, Grid icon)
    {
        internal readonly StackPanel Content = content;
        internal readonly Grid Icon = icon;
        internal readonly HoverWaveMotion Motion = new();
        internal readonly ItemBounceMotion Bounce = new();
        internal FrameworkElement? ProxyIcon;
        internal readonly RectangleGeometry Clip = new();
        internal Vector2 Center;
        internal Vector2 Pitch;
        internal bool NearViewport;
        internal bool Raised;
        internal HoverRect BaseBounds;
        internal HoverRect RowBounds;
        internal HoverRect IconBounds;
        internal HoverRect VisibleBaseBounds;
        internal HoverRect VisibleBounds;
        internal Vector2 Translation;
        internal Border? Proxy;
        internal string? DockToolTipText;
        internal bool Presented;
        internal float RenderScale = 1;
    }

    private void InitializeHoverWave()
    {
        WindowRoot.AddHandler(UIElement.PointerEnteredEvent,
            new PointerEventHandler(HoverViewport_PointerMoved), handledEventsToo: true);
        WindowRoot.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(HoverViewport_PointerMoved), handledEventsToo: true);
        WindowRoot.AddHandler(UIElement.PointerExitedEvent,
            new PointerEventHandler(HoverViewport_PointerExited), handledEventsToo: true);
        ItemsScrollView.PointerExited += HoverViewport_PointerExited;
        ItemsScrollView.ViewChanged += HoverViewport_ViewChanged;
        ItemsScrollView.StateChanged += HoverViewport_StateChanged;
        ItemsRepeater.LayoutUpdated += HoverLayout_Updated;
        _uiSettings.AnimationsEnabledChanged += HoverAnimationsEnabledChanged;
        _hoverPointerGuard = DispatcherQueue.CreateTimer();
        _hoverPointerGuard.Tick += HoverPointerGuard_Tick;
    }

    private bool HoverViewportVisible => !_closing && _expanded && !_animating &&
        ExpandedView.Visibility == Visibility.Visible &&
        (_definition.PlacementMode == OrganizerPlacementMode.Station ? _stationVisible : _runtimeVisible || IsContained);

    private bool HoverInteractionBusy => _changingExpansionMode || _pressActive || _widgetDragging ||
        _canvasResize is not null || _itemReorderSession is not null || _shellDragActive || _shellDropFinalizing ||
        _shellPromotionPending || _itemDragLanding || _nativeItemMotionRenderingSubscribed ||
        _dragHasLocalFile || _shellContextMenuOpen || _contextMenuActivated || _overlayOpenCount > 0 || _addingItem;

    private void RefreshHoverAvailability()
    {
        bool enabled = UseCustomAnimations;
        if (!enabled || !HoverViewportVisible || IsCompactList) ResetItemBounces();
        if (enabled && HoverViewportVisible && UseHoverPresentation &&
            _itemReorderSession is { State: ItemDragState.Pressed }) return;
        _hoverWave.SetAvailability(UseHoverWave, !HoverViewportVisible || HoverInteractionBusy);
        _hoverWave.SetScrolling(_smoothScrollRenderingSubscribed || ItemsScrollView.State != ScrollingInteractionState.Idle,
            preservePointer: IsCompactList);
        if (!enabled || !HoverViewportVisible || UseHoverPresentation && HoverInteractionBusy) ResetHoverWave();
        else
        {
            _hoverTargetsDirty = true;
            WakeHoverWave();
        }
        RefreshCompactRowHighlight();
    }

    private void HoverAnimationsEnabledChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (!_closing) RefreshHoverAvailability(); });

    private void RegisterHoverItem(Border host)
    {
        UnregisterHoverItem(host);
        if (FindItemPart<StackPanel>(host, "ItemContent") is not StackPanel content ||
            FindItemPart<Grid>(host, "ItemIconContainer") is not Grid icon) return;
        content.ScaleTransition = null;
        content.TranslationTransition = null;
        content.Scale = Vector3.One;
        content.Translation = Vector3.Zero;
        // Compact feedback is rendered once, against the visible row geometry.
        host.BackgroundTransition = IsCompactList ? null : new BrushTransition { Duration = TimeSpan.FromMilliseconds(120) };
        host.Background = _transparentItemBrush;
        _hoverItems.Add(host, new HoverItemVisual(content, icon));
        host.SizeChanged += HoverElement_SizeChanged;
        content.SizeChanged += HoverElement_SizeChanged;
        InvalidateHoverGeometry();
    }

    private void UnregisterHoverItem(Border host)
    {
        if (!_hoverItems.Remove(host, out HoverItemVisual? item)) return;
        if (ReferenceEquals(host, _compactHighlightedHost)) ClearCompactRowHighlight();
        ResetItemBounce(item);
        ClearDockToolTip(host, item);
        host.SizeChanged -= HoverElement_SizeChanged;
        item.Content.SizeChanged -= HoverElement_SizeChanged;
        ResetHoverVisual(host, item);
        RemoveHoverProxy(item);
        _hoverGeometryDirty = true;
        if (ReferenceEquals(_hoverPressedHost, host)) _hoverPressedHost = null;
        if (_hoverItems.Count == 0) ResetHoverWave();
    }

    private void HoverElement_SizeChanged(object sender, SizeChangedEventArgs args) => InvalidateHoverGeometry();

    private void InvalidateHoverGeometry()
    {
        _hoverGeometryDirty = _hoverTargetsDirty = true;
        WakeHoverWave();
    }

    private void HoverLayout_Updated(object? sender, object args)
    {
        if (_applyingDockLayout) return;
        if (_hoverLayoutPending)
        {
            if (IsDock) return; // The queued current-layout commit owns this transition.
            _hoverLayoutPending = false;
            _hoverGeometryDirty = true;
            if (IsDock) UpdateDockInputRegion();
        }
        if (!_hoverGeometryDirty || _closing || !_expanded || !UseHoverPresentation ||
            !UseCustomAnimations && !IsCompactList || HoverInteractionBusy || !WindowRoot.IsLoaded) return;
        UpdateHoverGeometry();
        if (UseCustomAnimations) RenderHoverPresentation();
        else RefreshCompactRowHighlight();
    }

    private void HoverViewport_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        UpdateCompactRowHighlight(args.GetCurrentPoint(ItemsScrollView).Position);
        if (UseHoverPresentation && _itemReorderSession is { State: ItemDragState.Pressed }) return;
        RefreshHoverAvailability();
        Point pointer = args.GetCurrentPoint(ItemsScrollView).Position;
        // A proxy becoming hit-testable can synthesize a routed pointer event.
        // Use the fixed viewport and physical cursor for Dock input, never a
        // transformed presentation as a new source of pointer coordinates.
        if (IsDock && NativeMethods.GetCursorPos(out var screen) && TryScreenToHoverViewport(screen, out Point fixedPoint))
        {
            pointer = fixedPoint;
            if (!IsPointerOverDockOwner(NativeMethods.WindowFromPoint(screen), screen))
            {
                CloseDockToolTip();
                ReturnHoverWave();
                return;
            }
        }
        UpdateDockToolTip(pointer);
        if (!CanActivateHover(pointer))
        {
            ReturnHoverWave();
            return;
        }
        if (!_hoverWave.MovePointer(new Vector2((float)pointer.X, (float)pointer.Y))) return;
        _hoverAnchor.Retarget(new((float)pointer.X, (float)pointer.Y), atRest: !HasHoverMotionToFinish());
        _hoverTargetsDirty = true;
        WakeHoverWave();
    }

    private void HoverViewport_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        Point pointer = args.GetCurrentPoint(ItemsScrollView).Position;
        if (IsDock && NativeMethods.GetCursorPos(out var screen) && TryScreenToHoverViewport(screen, out Point fixedPoint))
            pointer = fixedPoint;
        if (args.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
            UpdateCompactRowHighlight(pointer);
        UpdateDockToolTip(pointer);
        // Child exits also route here; crossing a cell must not erase the wave.
        if (!CanActivateHover(pointer)) ReturnHoverWave();
    }

    private void HoverViewport_ViewChanged(ScrollView sender, object args)
    {
        // Compact rows move beneath the same viewport pointer; keep spring progress and update targets.
        if (!IsCompactList) ResetHoverWave();
        _hoverGeometryDirty = true;
        RefreshHoverAvailability();
    }

    private void HoverViewport_StateChanged(ScrollView sender, object args)
    {
        if (!IsCompactList && sender.State != ScrollingInteractionState.Idle) ResetHoverWave();
        RefreshHoverAvailability();
    }

    private void UpdateCompactScrollHover(Point pointer)
    {
        UpdateCompactRowHighlight(pointer);
        RefreshHoverAvailability();
        if (!CanActivateHover(pointer))
        {
            ReturnHoverWave();
            return;
        }
        if (!_hoverWave.MovePointer(new((float)pointer.X, (float)pointer.Y))) return;
        _hoverAnchor.Retarget(new((float)pointer.X, (float)pointer.Y), atRest: !HasHoverMotionToFinish());
        _hoverTargetsDirty = true;
        WakeHoverWave();
    }

    private void ReturnHoverWave()
    {
        if (!_hoverWave.HasPointer && _hoverPressedHost is null && !HasHoverMotionToFinish()) return;
        _hoverWave.Leave();
        _hoverPressedHost = null;
        _hoverTargetsDirty = true;
        WakeHoverWave();
    }

    private void SetHoverPressed(Border host, bool pressed)
    {
        if (UseHoverPresentation)
        {
            // Preserve the clicked presentation until drag activation. The original host
            // owns capture, so quick taps/double taps do not chase a moving visual.
            if (pressed) _hoverPressedHost = host;
            else if (ReferenceEquals(_hoverPressedHost, host)) _hoverPressedHost = null;
            _hoverTargetsDirty = true;
            WakeHoverWave();
            return;
        }
        if (pressed)
        {
            _hoverWave.Leave();
            _hoverPressedHost = host;
        }
        else if (ReferenceEquals(_hoverPressedHost, host)) _hoverPressedHost = null;
        _hoverTargetsDirty = true;
        WakeHoverWave();
    }

    private void WakeHoverWave()
    {
        if (_closing || _hoverItems.Count == 0 || !UseCustomAnimations) return;
        // Icon mode still animates a press and its release, but ordinary pointer
        // movement must not subscribe a frame just to render an unchanged scale.
        if (!UseHoverWave && _hoverPressedHost is null && !HasHoverMotionToFinish()) return;
        if (!_hoverGeometryDirty && !_hoverWave.HasPointer && _hoverPressedHost is null && !HasHoverMotionToFinish()) return;
        if (!_hoverRenderingSubscribed)
        {
            _hoverLastFrame = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += HoverWave_Rendering;
            _hoverRenderingSubscribed = true;
        }
        UpdateHoverPointerGuard();
    }

    private bool HasHoverMotionToFinish()
    {
        if (IsDock) return _dockHoverMotion.IsActive || _dockHoverMotion.Scale != 1;
        foreach (HoverItemVisual item in _hoverItems.Values)
        {
            if (item.Motion.IsActive || item.Motion.Scale != 1) return true;
        }
        return false;
    }

    private void HoverWave_Rendering(object? sender, object args)
    {
        if (!HoverViewportVisible || !UseCustomAnimations)
        {
            ResetHoverWave();
            return;
        }
        if (UseHoverPresentation && _itemReorderSession is { State: ItemDragState.Pressed })
        {
            StopHoverRendering();
            return;
        }
        if (UseHoverPresentation && HoverInteractionBusy)
        {
            ResetHoverWave();
            return;
        }
        // Also guard against an interaction starting between input and this frame.
        _hoverWave.SetAvailability(UseHoverWave, HoverInteractionBusy);
        long now = Stopwatch.GetTimestamp();
        double seconds = Math.Min(.1, Stopwatch.GetElapsedTime(_hoverLastFrame, now).TotalSeconds);
        _hoverLastFrame = now;
        if (_hoverGeometryDirty) UpdateHoverGeometry();
        bool active;
        if (IsDock)
        {
            _dockHoverMotion.Retarget(_hoverWave.Pointer, HoverMaximumScale);
            active = !_dockHoverMotion.Step(seconds);
        }
        else active = !_hoverAnchor.Step(seconds);
        foreach ((Border host, HoverItemVisual item) in _hoverItems)
        {
            if (IsDock) continue; // Dock scales and translations are sampled together below.
            if (!item.NearViewport)
            {
                ResetHoverVisual(host, item);
                continue;
            }
            if (_hoverTargetsDirty || HoverInteractionBusy)
            {
                bool pressed = ReferenceEquals(host, _hoverPressedHost) && !_shellDragActive &&
                    _itemReorderSession is { State: ItemDragState.Pressed };
                item.Motion.Retarget(pressed && !IsCompactList ? .97f :
                    _hoverWave.GetTargetScale(item.Center, item.Pitch, IsCompactList, HoverMaximumScale));
            }
            active |= !item.Motion.Step(seconds);
            float scale = item.Motion.Scale;
            if (!UseHoverPresentation) item.Content.Scale = new Vector3(scale, scale, 1);
            if (_itemReorderSession is null && !_shellDragActive && !_nativeItemMotionRenderingSubscribed)
            {
                int layer = (int)Math.Round(Math.Max(0, scale - 1) * 1000);
                Canvas.SetZIndex(host, layer);
                item.Raised = layer > 0;
            }
        }
        if (UseHoverPresentation) RenderHoverPresentation();
        _hoverTargetsDirty = false;
        if (!active) StopHoverRendering();
        UpdateHoverPointerGuard();
    }

    private void RenderHoverPresentation()
    {
        if (_hoverLayoutPending) return;
        for (int i = 0; i < _hoverLayout.Length; i++)
            _hoverLayoutInput[i] = new(_hoverLayout[i].Value.BaseBounds, _hoverLayout[i].Value.Motion.Scale);
        if (IsDock)
            DockHoverLayout.CalculateInto(_hoverLayoutInput, _dockHoverMotion.Position, (float)_dockGeometry.Pitch,
                _dockHoverMotion.Scale, HorizontalHover, _hoverLayoutPoses);
        else
            HoverLayoutMath.CalculateInto(_hoverLayoutInput,
                HorizontalHover ? _hoverAnchor.Position.X : _hoverAnchor.Position.Y, HorizontalHover, IsCompactList,
                _hoverLayoutPoses, _hoverLayoutCenters, _hoverLayoutScales);
        if (IsDock) HoverLayoutMath.ConstrainDockPoses(_hoverLayoutPoses, _hoverSurfaceBounds, _hoverSurfaceRadius, HorizontalHover);
        for (int i = 0; i < _hoverLayout.Length; i++)
        {
            var (host, item) = _hoverLayout[i];
            if (item.Proxy is not Border proxy) continue;
            if (proxy.Child is not FrameworkElement child || !HoverLayoutMath.IsPresentationReady(
                item.BaseBounds.Width, item.BaseBounds.Height, proxy.ActualWidth, proxy.ActualHeight,
                child.ActualWidth, child.ActualHeight))
            {
                HideHoverProxy(item);
                _hoverGeometryDirty = true;
                continue;
            }
            item.Translation = _hoverLayoutPoses[i].Translation;
            item.RenderScale = _hoverLayoutPoses[i].Scale;
            item.VisibleBounds = HoverLayoutMath.VisibleAfterTransform(item.BaseBounds,
                item.RenderScale, item.Translation, IsCompactList, _hoverViewportBounds);
            proxy.Scale = new Vector3(item.RenderScale, item.RenderScale, 1);
            proxy.Translation = new Vector3(item.Translation, 0);
            HoverRect clip = HoverLayoutMath.LocalClip(item.VisibleBounds, item.BaseBounds,
                item.RenderScale, item.Translation, IsCompactList);
            if (IsDock && !_dockLayoutCommit.CanPresent(_hoverGeometryVersion,
                dimensionsReady: true, item.VisibleBounds))
            {
                HideHoverProxy(item);
                continue;
            }
            item.Clip.Rect = new Rect(clip.X, clip.Y, clip.Width, clip.Height);
            Canvas.SetZIndex(proxy, (int)Math.Round((item.RenderScale - 1) * 1000));
            if (!item.Presented)
            {
                Mirror(proxy, UIElement.OpacityProperty, host, nameof(UIElement.Opacity));
                proxy.IsHitTestVisible = true;
                item.Content.Opacity = 0;
                item.Presented = true;
            }
            if (item.Bounce.IsActive) ApplyItemBounce(item);
        }
        RefreshCompactRowHighlight();
        UpdateDockToolTipPosition();
        UpdateDockRunningVisuals();
    }

    private void UpdateHoverGeometry()
    {
        if (_hoverLayoutPending) return;
        // A reorder/FLIP temporarily transforms the host itself. Keep the cache
        // dirty until its layout is stable; hover must never retain that offset.
        if (_itemReorderSession is { State: not ItemDragState.Pressed } ||
            _nativeItemMotionRenderingSubscribed || _itemDragLanding || _shellDragActive ||
            _shellPromotionPending || _shellDropFinalizing) return;
        _hoverViewportBounds = new(0, 0, (float)ItemsScrollView.ActualWidth, (float)ItemsScrollView.ActualHeight);
        _hoverViewportOrigin = ItemsScrollView.TransformToVisual(ExpandedContentLayer).TransformPoint(new Point());
        _hoverSurfaceBounds = new(-(float)_hoverViewportOrigin.X, -(float)_hoverViewportOrigin.Y,
            (float)ExpandedContentLayer.ActualWidth, (float)ExpandedContentLayer.ActualHeight);
        _hoverSurfaceRadius = IsDock ? CurrentDockCornerRadius : (float)SnapDip(ExpandedCornerRadiusDip);
        _hoverViewportClip.Rect = new Rect(_hoverViewportOrigin.X, _hoverViewportOrigin.Y,
            _hoverViewportBounds.Width, _hoverViewportBounds.Height);
        _compactHighlightClip.Rect = _hoverViewportClip.Rect;
        if (HorizontalHover)
        {
            double gutter = _dockOverflowPixels / (WindowRoot.XamlRoot?.RasterizationScale ?? 1);
            _hoverViewportClip.Rect = new Rect(_hoverViewportOrigin.X, _hoverViewportOrigin.Y - gutter,
                _hoverViewportBounds.Width, _hoverViewportBounds.Height + gutter);
        }
        _hoverButtonBounds = default;
        if (CollapseButton.Visibility == Visibility.Visible)
        {
            Point button = CollapseButton.TransformToVisual(ItemsScrollView).TransformPoint(new Point());
            _hoverButtonBounds = new((float)button.X, (float)button.Y,
                (float)CollapseButton.ActualWidth, (float)CollapseButton.ActualHeight);
        }
        _compactHoverBounds = default;
        foreach ((Border host, HoverItemVisual item) in _hoverItems)
        {
            item.NearViewport = false;
            if (host.ActualWidth <= 0 || host.ActualHeight <= 0 || host.XamlRoot is null)
            {
                HideHoverProxy(item);
                continue;
            }
            Point origin;
            try { origin = host.TransformToVisual(ItemsScrollView).TransformPoint(new Point()); }
            catch (InvalidOperationException) { HideHoverProxy(item); continue; }
            double width = host.ActualWidth;
            double height = host.ActualHeight;
            HoverRect baseline = new((float)(origin.X + item.Content.ActualOffset.X),
                (float)(origin.Y + item.Content.ActualOffset.Y), (float)item.Content.ActualWidth, (float)item.Content.ActualHeight);
            item.BaseBounds = baseline;
            item.RowBounds = new((float)origin.X, (float)origin.Y, (float)width, (float)height);
            if (IsCompactList)
            {
                HoverRect row = item.RowBounds.Intersect(_hoverViewportBounds);
                if (!row.IsEmpty)
                {
                    float left = _compactHoverBounds.IsEmpty ? row.X : Math.Min(_compactHoverBounds.X, row.X);
                    float top = _compactHoverBounds.IsEmpty ? row.Y : Math.Min(_compactHoverBounds.Y, row.Y);
                    float right = _compactHoverBounds.IsEmpty ? row.Right : Math.Max(_compactHoverBounds.Right, row.Right);
                    float bottom = _compactHoverBounds.IsEmpty ? row.Bottom : Math.Max(_compactHoverBounds.Bottom, row.Bottom);
                    _compactHoverBounds = new(left, top, right - left, bottom - top);
                }
            }
            item.IconBounds = new(baseline.X + item.Icon.ActualOffset.X, baseline.Y + item.Icon.ActualOffset.Y,
                (float)item.Icon.ActualWidth, (float)item.Icon.ActualHeight);
            item.VisibleBaseBounds = baseline.Intersect(_hoverViewportBounds);
            item.NearViewport = !item.VisibleBaseBounds.IsEmpty;
            if (UseHoverPresentation && item.NearViewport)
            {
                EnsureHoverProxy(host, item);
                Border proxy = item.Proxy!;
                proxy.DataContext = host.DataContext;
                proxy.Tag = host.Tag;
                if (!IsDock) ToolTipService.SetToolTip(proxy, ToolTipService.GetToolTip(host));
                proxy.Width = baseline.Width;
                proxy.Height = baseline.Height;
                if (proxy.Child is FrameworkElement content)
                {
                    content.Width = baseline.Width;
                    content.Height = baseline.Height;
                }
                Canvas.SetLeft(proxy, _hoverViewportOrigin.X + baseline.X);
                Canvas.SetTop(proxy, _hoverViewportOrigin.Y + baseline.Y);
                proxy.CenterPoint = new Vector3(IsCompactList ? 0 : baseline.Width / 2, baseline.Height / 2, 0);
            }
            else HideHoverProxy(item);
            item.Center = new Vector2((float)(origin.X + width / 2), (float)(origin.Y + height / 2));
            item.Pitch = new Vector2((float)(width + GetItemLayoutGapDip()), (float)(height + GetItemLayoutGapDip()));
            item.Content.CenterPoint = new Vector3(IsCompactList ? 0 : (float)(item.Content.ActualWidth / 2),
                (float)(item.Content.ActualHeight / 2), 0);
        }
        _hoverLayout = _hoverItems.Where(pair => pair.Value.NearViewport)
            .OrderBy(pair => HorizontalHover ? pair.Value.BaseBounds.Center.X : pair.Value.BaseBounds.Center.Y).ToArray();
        if (_hoverLayoutInput.Length != _hoverLayout.Length)
        {
            _hoverLayoutInput = new HoverLayoutItem[_hoverLayout.Length];
            _hoverLayoutPoses = new HoverLayoutPose[_hoverLayout.Length];
            _hoverHitRegions = new HoverHitRegion[_hoverLayout.Length];
            _hoverLayoutCenters = new float[_hoverLayout.Length];
            _hoverLayoutScales = new float[_hoverLayout.Length];
        }
        _hoverGeometryDirty = false;
        _hoverGeometryVersion = _dockLayoutCommit.Version;
        _hoverTargetsDirty = true;
    }

    private void ResetHoverWave()
    {
        ClearCompactRowHighlight(forgetPointer: !HoverViewportVisible || !IsCompactList);
        if (!HoverViewportVisible || IsCompactList || !UseCustomAnimations ||
            _itemReorderSession is { IsActive: true } || _widgetDragging || _canvasResize is not null)
            ResetItemBounces();
        CloseDockToolTip();
        _hoverWave.Leave();
        _hoverAnchor.Reset();
        _dockHoverMotion.Reset();
        _hoverPressedHost = null;
        foreach ((Border host, HoverItemVisual item) in _hoverItems) ResetHoverVisual(host, item);
        _hoverGeometryDirty = _hoverTargetsDirty = true;
        StopHoverRendering();
        _hoverPointerGuard?.Stop();
        UpdateHoverInputRegion();
    }

    private void ResetHoverVisual(Border host, HoverItemVisual item)
    {
        HideHoverProxy(item);
        item.Motion.Reset();
        item.Content.Scale = Vector3.One;
        item.Content.Translation = Vector3.Zero;
        if (item.Raised && Canvas.GetZIndex(host) < 1000) Canvas.SetZIndex(host, 0);
        item.Raised = false;
    }

    private void StopHoverRendering()
    {
        if (_hoverRenderingSubscribed) CompositionTarget.Rendering -= HoverWave_Rendering;
        _hoverRenderingSubscribed = false;
        _hoverLastFrame = 0;
    }

    private void UpdateHoverPointerGuard()
    {
        if (_hoverPointerGuard is null) return;
        TimeSpan interval = TimeSpan.FromMilliseconds(_host.State.GlobalSettings.PerformanceTuning.PointerPollMilliseconds);
        if (_hoverPointerGuard.Interval != interval) _hoverPointerGuard.Interval = interval;
        if (UseHoverPresentation && (_hoverWave.HasPointer || HasHoverMotionToFinish() || _visibleDockToolTip is { IsOpen: true }))
        {
            if (!_hoverPointerGuard.IsRunning) _hoverPointerGuard.Start();
        }
        else _hoverPointerGuard.Stop();
    }

    private void HoverPointerGuard_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!HoverViewportVisible || (!UseHoverWave && _visibleDockToolTip is not { IsOpen: true }) || HoverInteractionBusy)
        {
            if (HoverViewportVisible && UseHoverWave && _itemReorderSession is { State: ItemDragState.Pressed }) return;
            ResetHoverWave();
            return;
        }
        bool hadPointer = _hoverWave.HasPointer;
        bool sampled = NativeMethods.GetCursorPos(out var screen);
        bool inside = sampled && TryScreenToHoverViewport(screen, out Point point) && CanActivateHover(point);
        IntPtr hit = sampled ? NativeMethods.WindowFromPoint(screen) : IntPtr.Zero;
        bool overOwner = IsPointerOverDockOwner(hit, screen);
        if (!overOwner || !inside) CloseDockToolTip();
        if (hadPointer && !_hoverWave.ObservePointerPresence(overOwner, inside)) ReturnHoverWave();
        UpdateHoverPointerGuard();
    }

    private bool IsInsideBaseItemContent(Border host, Point pointer)
    {
        if (!_hoverItems.TryGetValue(host, out HoverItemVisual? item)) return false;
        // ActualOffset/ActualSize describe layout, unaffected by the visual scale.
        Vector3 offset = item.Content.ActualOffset;
        return pointer.X >= offset.X && pointer.Y >= offset.Y &&
            pointer.X <= offset.X + item.Content.ActualWidth && pointer.Y <= offset.Y + item.Content.ActualHeight;
    }

    private void DisposeHoverWave()
    {
        _visibleDockToolTip?.Dispose();
        _visibleDockToolTip = null;
        ResetItemBounces();
        ResetHoverWave();
        WindowRoot.RemoveHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(HoverViewport_PointerMoved));
        WindowRoot.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(HoverViewport_PointerMoved));
        WindowRoot.RemoveHandler(UIElement.PointerExitedEvent, new PointerEventHandler(HoverViewport_PointerExited));
        ItemsScrollView.PointerExited -= HoverViewport_PointerExited;
        ItemsScrollView.ViewChanged -= HoverViewport_ViewChanged;
        ItemsScrollView.StateChanged -= HoverViewport_StateChanged;
        ItemsRepeater.LayoutUpdated -= HoverLayout_Updated;
        _uiSettings.AnimationsEnabledChanged -= HoverAnimationsEnabledChanged;
        foreach ((Border host, HoverItemVisual item) in _hoverItems)
        {
            ClearDockToolTip(host, item);
            host.SizeChanged -= HoverElement_SizeChanged;
            item.Content.SizeChanged -= HoverElement_SizeChanged;
        }
        _hoverItems.Clear();
        if (_hoverPointerGuard is not null) _hoverPointerGuard.Tick -= HoverPointerGuard_Tick;
        _hoverPointerGuard = null;
        if (_hoverPresentationLayer is not null) ExpandedContentLayer.Children.Remove(_hoverPresentationLayer);
        _hoverPresentationLayer = null;
        if (_compactHighlightLayer is not null) ExpandedContentLayer.Children.Remove(_compactHighlightLayer);
        _compactHighlightLayer = null;
        _compactHighlight = null;
    }
}
