using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System.Numerics;
using System.Runtime.InteropServices;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    internal bool IsDock => _definition.PlacementMode == OrganizerPlacementMode.Dock;
    internal bool StaysExpanded => IsDock || IsPermanentlyExpanded;
    private DockGeometry _dockGeometry;
    private bool _applyingDockLayout;
    private readonly StackLayout _dockLayout = new();
    private string? _dockRegionKey;
    private bool _updatingDockInputRegion;
    private int _dockOverflowPixels;

    private NativeMethods.RECT DockPhysicalBounds(NativeMethods.RECT logical)
    {
        if (IsDock) logical.Top -= _dockOverflowPixels;
        return logical;
    }

    private void Dock_PointerWheelChanged(PointerRoutedEventArgs e)
    {
        e.Handled = true;
        var point = e.GetCurrentPoint(ExpandedView);
        bool controlPressed = OrganizerInteractionMath.IsControlPressed(
            e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control),
            NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL));
        bool inside = _dockGeometry.Contains(new Vector2((float)point.Position.X, (float)point.Position.Y)) ||
            DockItemAtPoint(point.Position) is not null;
        bool busy = !inside || !_runtimeVisible || !HoverViewportVisible || HoverInteractionBusy ||
            point.Properties.IsHorizontalMouseWheel;
        double next = DockLayoutMath.ApplyWheelDelta(_definition.DockIconSizeDip,
            point.Properties.MouseWheelDelta, controlPressed, busy, ref _wheelDeltaRemainder);
        if (next != _definition.DockIconSizeDip) _host.Console.SetDockSize(OrganizerId, next);
    }

    private async Task InitializeDockAsync()
    {
        await WaitForLoadedAsync();
        if (_closing) return;
        _desktopLayer = new DesktopLayerService(_hwnd, IntPtr.Zero);
        _expanded = true;
        _transitionProgress = 1;
        _animating = false;
        CompactView.Visibility = Visibility.Collapsed;
        ExpandedView.Visibility = Visibility.Visible;
        ExpandedView.Opacity = 1;
        GetExpandedCompositionVisual().Scale = Vector3.One;
        ExpandedView.DoubleTapped += Dock_DoubleTapped;
        ExpandedView.ContextRequested += Dock_ContextRequested;
        _desktopLayer.SetExpanded(true, showWindow: false);
        ApplyDockLayout();
        ApplyTheme();
        RefreshPerformanceSettings();
        if (_storage.Exists) StartWatcher();
        await RefreshCatalogAsync(notifyUnsupported: true, refreshIcons: true);
        await SaveStateAsync();
        _host.RefreshDockRunningSubscription();
    }

    private void ApplyDockLayout()
    {
        if (!IsDock || !_expanded || _hwnd == IntPtr.Zero || _applyingDockLayout || _closing || _pressActive || _widgetDragging) return;
        _applyingDockLayout = true;
        try
        {
            _dockGeometry = DockLayoutMath.Calculate(_items.Count, _definition.DockIconSizeDip,
                _definition.DockOrientation, _definition.DockSpacingFactor);
            DisplayInfo display = DisplayPlacementService.GetDisplay(_definition.DockCenter?.MonitorDevice);
            if (_definition.DockCenter is not { } center || !string.Equals(center.MonitorDevice, display.Device, StringComparison.OrdinalIgnoreCase))
            {
                _definition.DockCenter = new WidgetPosition
                {
                    MonitorDevice = display.Device,
                    XDip = display.Work.Width / display.Scale / 2,
                    YDip = display.Work.Height / display.Scale / 2
                };
            }
            var saved = _definition.DockCenter;
            NativeMethods.RECT bounds = DockLayoutMath.CalculateBounds(_dockGeometry,
                new Vector2((float)(display.Work.Left / display.Scale + saved.XDip),
                    (float)(display.Work.Top / display.Scale + saved.YDip)), display.Scale);
            _compactBounds = bounds;
            _dockOverflowPixels = _definition.DockOrientation == Models.DockOrientation.Horizontal
                ? (int)Math.Ceiling((_dockGeometry.IconSize * HoverMaximumScale / 3 + 2) * display.Scale) : 0;
            ExpandedView.Margin = new Thickness(0, _dockOverflowPixels / display.Scale, 0, 0);
            ApplyDockContentLayout();
            ApplyBounds(bounds, show: _runtimeVisible, preserveZOrder: true);
            UpdateSurfaceClips();
            UpdateDockInputRegion();
            UpdateRealizedItems();
            InvalidateHoverGeometry();
        }
        finally { _applyingDockLayout = false; }
    }

    private void ApplyDockContentLayout()
    {
        BeginHoverLayoutChange();
        _dockGeometry = DockLayoutMath.Calculate(_items.Count, _definition.DockIconSizeDip,
            _definition.DockOrientation, _definition.DockSpacingFactor);
        ExpandedViewContextMenu.ShouldConstrainToRootBounds = false;
        ExpandedTitleRow.Height = new GridLength(0);
        ExpandedNameText.Visibility = Visibility.Collapsed;
        CollapseButton.Visibility = Visibility.Collapsed;
        ItemsScrollView.Margin = new Thickness(0);
        ItemsScrollView.HorizontalScrollMode = ScrollingScrollMode.Disabled;
        ItemsScrollView.VerticalScrollMode = ScrollingScrollMode.Disabled;
        ItemsRepeater.Margin = new Thickness(_dockGeometry.HorizontalInset, _dockGeometry.TopInset,
            _dockGeometry.HorizontalInset, _dockGeometry.BottomInset);
        ItemsRepeater.Width = Math.Max(1, _dockGeometry.Width - 2 * _dockGeometry.HorizontalInset);
        ItemsRepeater.Height = Math.Max(1, _dockGeometry.Height - _dockGeometry.TopInset - _dockGeometry.BottomInset);
        // A Dock is one unbroken axis. UniformGridLayout can wrap the last
        // element after a rounded size/spacing change into a clipped second row.
        _dockLayout.Orientation = _definition.DockOrientation == Models.DockOrientation.Horizontal
            ? Orientation.Horizontal : Orientation.Vertical;
        _dockLayout.Spacing = _dockGeometry.Pitch - _dockGeometry.IconSize;
        if (!ReferenceEquals(ItemsRepeater.Layout, _dockLayout)) ItemsRepeater.Layout = _dockLayout;
        DockEmptyAddButton.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DockEmptyAddButton.Width = DockEmptyAddButton.Height = _dockGeometry.IconSize;
        DockEmptyAddButton.Margin = new Thickness(0, _dockGeometry.TopInset, 0, _dockGeometry.BottomInset);
        DockEmptyAddIcon.FontSize = _dockGeometry.IconSize * .5;
        ToolTipService.SetToolTip(DockEmptyAddButton, AppStrings.Get("DockEmptyHint"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DockEmptyAddButton, AppStrings.Get("ContextAddItem"));
    }

    private void CaptureDockCenter()
    {
        if (!TryGetLogicalWindowRect(out var bounds)) return;
        DisplayInfo display = DisplayPlacementService.ForBounds(bounds);
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        _definition.DockCenter = new WidgetPosition
        {
            MonitorDevice = display.Device,
            XDip = (bounds.Left + bounds.Width / 2d - display.Work.Left) / scale,
            YDip = (bounds.Top + bounds.Height / 2d - display.Work.Top) / scale,
            SavedWorkAreaWidthDip = display.Work.Width / scale,
            SavedWorkAreaHeightDip = display.Work.Height / scale
        };
    }

    private async Task SaveDockCenterAsync()
    {
        WidgetPosition? previous = _definition.DockCenter;
        CaptureDockCenter();
        try { await _host.SaveStateAsync(); }
        catch (Exception ex)
        {
            _definition.DockCenter = previous;
            AppLogger.Error("保存 Dock 位置失败，恢复原位置。", ex);
            ShowMessage(AppStrings.Get("SaveConfigurationError"), InfoBarSeverity.Warning);
        }
        ApplyDockLayout();
    }

    private Border? DockItemAtPoint(Point point)
    {
        Point viewportPoint = ExpandedView.TransformToVisual(ItemsScrollView).TransformPoint(point);
        if (HasHoverPresentation) return HoverItemAtViewportPoint(viewportPoint);
        var local = new Vector2((float)point.X, (float)point.Y);
        foreach ((var host, var visual) in _hoverItems)
        {
            if (host.DataContext is not WidgetItem item) continue;
            int index = IndexOfItem(item.RelativeName);
            if (index >= 0 && _dockGeometry.ContainsIcon(local, index, visual.Motion.Scale)) return host;
        }
        return null;
    }

    private async void Dock_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.Handled || !_expanded || _animating || DockItemAtPoint(e.GetPosition(ExpandedView)) is not { } host) return;
        e.Handled = true;
        await RunSafelyAsync(() => OpenTaggedItemAsync(host, doubleTap: true), "打开 Dock 项目失败");
    }

    private void Dock_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (!e.Handled && e.TryGetPosition(ExpandedView, out Point point) && DockItemAtPoint(point) is { } host)
            Item_ContextRequested(host, e);
    }

    private bool DockContainsScreenPoint(NativeMethods.POINT point)
    {
        return TryScreenToHoverViewport(point, out Point local) && InsideHoverRegion(local);
    }

    private void UpdateDockInputRegion() => UpdateHoverInputRegion();

    private static class DockNative
    {
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] internal static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr handle);
        [DllImport("user32.dll")] internal static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    }
}
