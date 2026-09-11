using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Controls;
using TuckPane.Core;
using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private Canvas? _hoverPresentationLayer;
    private readonly RectangleGeometry _hoverViewportClip = new();
    private HoverRect _hoverViewportBounds, _hoverSurfaceBounds, _hoverButtonBounds;
    private HoverRect _compactHoverBounds;
    private float _hoverSurfaceRadius;
    private Point _hoverViewportOrigin;
    private Point? _hoverPressOffset;
    private Vector3 _hoverDragOriginOffset;
    private UIElement? _itemPointerCaptureOwner;
    private Microsoft.UI.Xaml.Input.Pointer? _itemCapturedPointer;
    private readonly DockLayoutCommit _dockLayoutCommit = new();
    private long _hoverGeometryVersion;
    private bool UseHoverPresentation => IsDock || IsCompactList;
    private bool HorizontalHover => IsDock && _definition.DockOrientation == Models.DockOrientation.Horizontal;
    private bool HasHoverPresentation => _hoverItems.Values.Any(item => item.Presented);

    private void BeginHoverLayoutChange()
    {
        ResetItemBounces();
        _hoverLayoutPending = true;
        ResetHoverWave();
        if (!IsDock) return;
        long version = _dockLayoutCommit.Request();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_closing || version != _dockLayoutCommit.Version) return;
            // Finish source and viewport layout before measuring the replacement tree.
            // SizeChanged may request a newer layout while this call drains layout.
            WindowRoot.UpdateLayout();
            if (!_dockLayoutCommit.Complete(version)) return;
            _hoverLayoutPending = false;
            InvalidateHoverGeometry();
            UpdateDockInputRegion();
            if (!HoverViewportVisible || !UseCustomAnimations || HoverInteractionBusy) return;
            UpdateHoverGeometry();
            RenderHoverPresentation();
        });
    }

    private static void Mirror(FrameworkElement target, DependencyProperty property, FrameworkElement source, string path) =>
        target.SetBinding(property, new Binding { Source = source, Path = new PropertyPath(path), Mode = BindingMode.OneWay });

    // This small presentation tree shares the existing image sources. No catalog/icon requests or item ownership.
    private static FrameworkElement CloneHoverContent(FrameworkElement source, HoverItemVisual item)
    {
        FrameworkElement copy;
        switch (source)
        {
            case OrganizerPreviewControl preview:
                var previewCopy = new OrganizerPreviewControl();
                previewCopy.SetCompactVisual(preview.IsCompactVisual);
                for (int i = 0; i < preview.Images.Count; i++)
                    Mirror(previewCopy.Images[i], Image.SourceProperty, preview.Images[i], nameof(Image.Source));
                Mirror(previewCopy.EmptyStateIcon, UIElement.VisibilityProperty, preview.EmptyStateIcon, nameof(Visibility));
                Mirror(previewCopy.EmptyStateIcon, FontIcon.FontSizeProperty, preview.EmptyStateIcon, nameof(FontIcon.FontSize));
                copy = previewCopy;
                break;
            case StackPanel stack:
                var stackCopy = new StackPanel { Orientation = stack.Orientation, Spacing = stack.Spacing };
                Mirror(stackCopy, StackPanel.OrientationProperty, stack, nameof(StackPanel.Orientation));
                Mirror(stackCopy, StackPanel.SpacingProperty, stack, nameof(StackPanel.Spacing));
                foreach (FrameworkElement child in stack.Children.OfType<FrameworkElement>())
                    stackCopy.Children.Add(CloneHoverContent(child, item));
                copy = stackCopy;
                break;
            case Grid grid:
                var gridCopy = new Grid();
                foreach (FrameworkElement child in grid.Children.OfType<FrameworkElement>())
                    gridCopy.Children.Add(CloneHoverContent(child, item));
                copy = gridCopy;
                break;
            case Image image:
                var imageCopy = new Image { Stretch = image.Stretch };
                Mirror(imageCopy, Image.SourceProperty, image, nameof(Image.Source));
                copy = imageCopy;
                break;
            case FontIcon icon:
                var iconCopy = new FontIcon { FontFamily = icon.FontFamily, FontSize = icon.FontSize };
                Mirror(iconCopy, FontIcon.FontSizeProperty, icon, nameof(FontIcon.FontSize));
                Mirror(iconCopy, FontIcon.GlyphProperty, icon, nameof(FontIcon.Glyph));
                Mirror(iconCopy, FontIcon.ForegroundProperty, icon, nameof(FontIcon.Foreground));
                copy = iconCopy;
                break;
            case TextBlock text:
                var textCopy = new TextBlock
                {
                    FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight,
                    TextAlignment = text.TextAlignment, TextTrimming = text.TextTrimming,
                    TextWrapping = text.TextWrapping, MaxWidth = text.MaxWidth
                };
                Mirror(textCopy, TextBlock.TextProperty, text, nameof(TextBlock.Text));
                Mirror(textCopy, TextBlock.ForegroundProperty, text, nameof(TextBlock.Foreground));
                Mirror(textCopy, TextBlock.FontSizeProperty, text, nameof(TextBlock.FontSize));
                Mirror(textCopy, TextBlock.MaxWidthProperty, text, nameof(TextBlock.MaxWidth));
                Mirror(textCopy, TextBlock.TextAlignmentProperty, text, nameof(TextBlock.TextAlignment));
                copy = textCopy;
                break;
            default:
                throw new InvalidOperationException($"Unsupported hover content: {source.GetType().Name}");
        }
        Mirror(copy, FrameworkElement.WidthProperty, source, nameof(FrameworkElement.Width));
        Mirror(copy, FrameworkElement.HeightProperty, source, nameof(FrameworkElement.Height));
        Mirror(copy, FrameworkElement.MarginProperty, source, nameof(FrameworkElement.Margin));
        Mirror(copy, FrameworkElement.HorizontalAlignmentProperty, source, nameof(FrameworkElement.HorizontalAlignment));
        Mirror(copy, FrameworkElement.VerticalAlignmentProperty, source, nameof(FrameworkElement.VerticalAlignment));
        copy.IsHitTestVisible = false;
        Mirror(copy, UIElement.VisibilityProperty, source, nameof(Visibility));
        if (ReferenceEquals(source, item.Icon))
        {
            item.ProxyIcon = copy;
            copy.Translation = item.Icon.Translation;
        }
        return copy;
    }

    private void EnsureHoverProxy(Border host, HoverItemVisual item)
    {
        if (item.Proxy is not null) return;
        if (_hoverPresentationLayer is null)
        {
            _hoverPresentationLayer = new Canvas { Clip = _hoverViewportClip };
            Canvas.SetZIndex(_hoverPresentationLayer, 1);
            ExpandedContentLayer.Children.Add(_hoverPresentationLayer);
        }
        var content = CloneHoverContent(item.Content, item);
        content.Width = item.BaseBounds.Width;
        content.Height = item.BaseBounds.Height;
        content.HorizontalAlignment = HorizontalAlignment.Left;
        content.VerticalAlignment = VerticalAlignment.Top;
        var proxy = new Border
        {
            Child = content, Width = item.BaseBounds.Width, Height = item.BaseBounds.Height,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            DataContext = host.DataContext, Tag = host.Tag, Clip = item.Clip,
            Opacity = 0, IsHitTestVisible = false
        };
        // Prepare and measure once before replacing the original. Subsequent hover
        // enter/leave frames keep this same tree, including the resting frame.
        proxy.SizeChanged += (_, _) =>
        {
            if (_hoverItems.TryGetValue(host, out var current) && ReferenceEquals(current, item))
                InvalidateHoverGeometry();
        };
        if (!IsDock) ToolTipService.SetToolTip(proxy, ToolTipService.GetToolTip(host));
        proxy.PointerPressed += (_, e) => ForwardHoverPress(host, item, e);
        proxy.PointerMoved += (_, e) => Item_PointerMoved(host, e);
        proxy.PointerReleased += (_, e) => Item_PointerReleased(host, e);
        proxy.PointerCanceled += (_, e) => Item_PointerCanceled(host, e);
        proxy.PointerCaptureLost += (_, e) =>
        {
            if (ReferenceEquals(_itemPointerCaptureOwner, proxy)) Item_PointerCaptureLost(host, e);
        };
        proxy.PointerWheelChanged += ExpandedView_PointerWheelChanged;
        proxy.DoubleTapped += (_, e) => ItemHost_DoubleTapped(host, e);
        proxy.ContextRequested += (_, e) =>
        {
            if (e.Handled || !_hoverItems.ContainsKey(host)) return;
            if (e.TryGetPosition(ItemsScrollView, out Point point) && HoverItemAtViewportPoint(point) != host) return;
            ResetHoverWave();
            Item_ContextRequested(host, e);
        };
        item.Proxy = proxy;
        ApplyItemBounce(item);
        _hoverPresentationLayer.Children.Add(proxy);
    }

    private void ForwardHoverPress(Border host, HoverItemVisual item, PointerRoutedEventArgs args)
    {
        if (args.Handled || !args.GetCurrentPoint(WindowRoot).Properties.IsLeftButtonPressed) return;
        Point point = args.GetCurrentPoint(ItemsScrollView).Position;
        if (HoverItemAtViewportPoint(point) != host) return;
        Vector2 pivot = IsCompactList ? new(item.BaseBounds.X, item.BaseBounds.Center.Y) : item.BaseBounds.Center;
        Vector2 baseline = pivot + (new Vector2((float)point.X, (float)point.Y) - item.Translation - pivot) / item.RenderScale;
        _hoverPressOffset = new Point(baseline.X - item.BaseBounds.X + item.Content.ActualOffset.X,
            baseline.Y - item.BaseBounds.Y + item.Content.ActualOffset.Y);
        try { BeginItemPointerPress(host, args, item.Proxy!); }
        finally { _hoverPressOffset = null; }
        args.Handled = true;
    }

    private void RemoveHoverProxy(HoverItemVisual item)
    {
        HideHoverProxy(item);
        if (item.Proxy is not null) _hoverPresentationLayer?.Children.Remove(item.Proxy);
        item.Proxy = null;
        item.ProxyIcon = null;
    }

    private static void HideHoverProxy(HoverItemVisual item)
    {
        if (item.Proxy is { } proxy)
        {
            proxy.Opacity = 0;
            proxy.IsHitTestVisible = false;
        }
        item.Presented = false;
        item.Content.Opacity = 1;
        item.VisibleBounds = default;
        item.Translation = Vector2.Zero;
        item.RenderScale = 1;
    }

    // Exclude the fixed bounce gutter from persisted position and drag geometry.
    private bool TryGetLogicalWindowRect(out NativeMethods.RECT bounds)
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out bounds)) return false;
        if (IsDock) bounds.Top += _dockOverflowPixels;
        return true;
    }

    private Border? HoverItemAtViewportPoint(Point point)
    {
        if (_hoverGeometryDirty) UpdateHoverGeometry();
        Vector2 local = new((float)point.X, (float)point.Y);
        if (!InsideHoverRegion(point)) return null;
        for (int i = 0; i < _hoverLayout.Length; i++)
        {
            // A sibling's ready proxy must not make an original item unclickable.
            // Hit testing follows the representation that is actually visible.
            HoverItemVisual item = _hoverLayout[i].Value;
            _hoverHitRegions[i] = new(item.RowBounds, item.BaseBounds, item.VisibleBounds,
                item.Translation, item.RenderScale, item.Presented);
        }
        int index = HoverLayoutMath.HitTestItems(_hoverHitRegions, IsCompactList,
            _hoverViewportBounds, _hoverButtonBounds, local);
        return index < 0 ? null : _hoverLayout[index].Key;
    }

    private bool InsideHoverRegion(Point point)
    {
        Vector2 local = new((float)point.X, (float)point.Y);
        return _hoverViewportBounds.Contains(local) && !_hoverButtonBounds.Contains(local) &&
            HoverLayoutMath.ContainsRounded(_hoverSurfaceBounds, _hoverSurfaceRadius, local);
    }

    private bool CanActivateHover(Point point)
    {
        if (_hoverGeometryDirty) UpdateHoverGeometry();
        if (!InsideHoverRegion(point)) return false;
        if (!IsCompactList) return true;
        // RowBounds are aggregated after layout, including only the gaps between
        // actual rows. Pointer X does not change the vertical wave's strength.
        return HoverLayoutMath.CanActivateCompact(_compactHoverBounds, _hoverViewportBounds,
            _hoverButtonBounds, new((float)point.X, (float)point.Y));
    }

    private float CurrentDockCornerRadius => RoundedSurfaceGeometry.Create(_dockGeometry.Width, _dockGeometry.Height,
        _dockGeometry.CornerRadius, WindowRoot.XamlRoot?.RasterizationScale ?? 1).Radius;

    private bool TryScreenToHoverViewport(NativeMethods.POINT screen, out Point point)
    {
        point = default;
        if (!NativeMethods.ScreenToClient(_hwnd, ref screen)) return false;
        double scale = WindowRoot.XamlRoot?.RasterizationScale ?? 1;
        Point origin = ItemsScrollView.TransformToVisual(WindowRoot).TransformPoint(new Point());
        point = new Point(screen.X / scale - origin.X, screen.Y / scale - origin.Y);
        return true;
    }

    private void UpdateHoverInputRegion()
    {
        if (_hwnd == IntPtr.Zero || !WindowRoot.IsLoaded || _updatingDockInputRegion) return;
        _updatingDockInputRegion = true;
        try { ApplyHoverInputRegion(); }
        finally { _updatingDockInputRegion = false; }
    }

    private void ApplyHoverInputRegion()
    {
        if (!IsDock)
        {
            if (_dockRegionKey is not null) DockNative.SetWindowRgn(_hwnd, IntPtr.Zero, true);
            _dockRegionKey = null;
            return;
        }
        if (!_expanded) return;
        if (_itemReorderSession is { IsActive: true } || _shellDragActive)
        {
            if (_dockRegionKey != "drag") DockNative.SetWindowRgn(_hwnd, IntPtr.Zero, true);
            _dockRegionKey = "drag";
            return;
        }
        double scale = WindowRoot.XamlRoot?.RasterizationScale ?? 1;
        var clientOrigin = new NativeMethods.POINT();
        if (!NativeMethods.GetWindowRect(_hwnd, out var windowBounds) ||
            !NativeMethods.ClientToScreen(_hwnd, ref clientOrigin)) return;
        int offsetX = clientOrigin.X - windowBounds.Left, offsetY = clientOrigin.Y - windowBounds.Top;
        NativeMethods.RECT WindowRegionPixels(HoverRect rect, Vector2 origin)
        {
            // XAML coordinates are client-relative; HRGN coordinates are HWND-relative.
            var pixels = HoverLayoutMath.ToPixels(rect, scale, origin, fringe: 1);
            pixels.Left += offsetX; pixels.Right += offsetX;
            pixels.Top += offsetY; pixels.Bottom += offsetY;
            return pixels;
        }
        Point surfaceOrigin = ExpandedPanel.TransformToVisual(WindowRoot).TransformPoint(new Point());
        RoundedSurfaceGeometry body = RoundedSurfaceGeometry.Create(ExpandedPanel.ActualWidth,
            ExpandedPanel.ActualHeight, CurrentDockCornerRadius, scale);
        HoverRect surface = new((float)surfaceOrigin.X, (float)surfaceOrigin.Y,
            body.Width, body.Height);
        var frame = WindowRegionPixels(surface, Vector2.Zero);
        int diameter = (int)Math.Round(2 * body.Radius * scale) + 2;
        string key = $"{frame.Left},{frame.Top},{frame.Right},{frame.Bottom},{diameter}";
        var bounceRects = new List<NativeMethods.RECT>();
        if (HorizontalHover)
        {
            Point origin = ItemsScrollView.TransformToVisual(WindowRoot).TransformPoint(new Point());
            foreach (HoverItemVisual item in _hoverItems.Values)
            {
                if (!item.Bounce.IsActive) continue;
                HoverRect icon = GetRenderedDockIcon(item, includeBounce: true);
                var pixels = WindowRegionPixels(icon, new Vector2((float)origin.X, (float)origin.Y));
                bounceRects.Add(pixels);
                key += $"/{pixels.Left},{pixels.Top},{pixels.Right},{pixels.Bottom}";
            }
        }
        if (key == _dockRegionKey) return;
        IntPtr region = DockNative.CreateRoundRectRgn(frame.Left, frame.Top, frame.Right, frame.Bottom, diameter, diameter);
        if (region == IntPtr.Zero) return;
        foreach (var icon in bounceRects)
        {
            IntPtr iconRegion = DockNative.CreateRectRgn(icon.Left, icon.Top, icon.Right, icon.Bottom);
            if (iconRegion == IntPtr.Zero) continue;
            DockNative.CombineRgn(region, region, iconRegion, 2);
            DockNative.DeleteObject(iconRegion);
        }
        // A visible region change must redraw the newly exposed client pixels.
        // TransparentWindowBackdrop handles that erase; skipping it leaves stale
        // native backing pixels visible under the animated transparent content.
        if (DockNative.SetWindowRgn(_hwnd, region, true) == 0) DockNative.DeleteObject(region);
        else
        {
            _dockRegionKey = key;
            // Region changes send native window-position notifications. Apply the
            // border suppression last, without another frame/size change.
            _desktopLayer?.ApplyTransparentChrome();
        }
    }
}
