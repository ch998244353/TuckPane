using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private bool _itemBounceRenderingSubscribed;
    private long _itemBounceLastFrame;

    private void StartItemBounce(WidgetItem item)
    {
        if (IsCompactList || !UseCustomAnimations || !HoverViewportVisible ||
            !TryGetRealizedItemHost(item.RelativeName, out var host) ||
            !_hoverItems.TryGetValue(host, out var visual)) return;
        if (_hoverGeometryDirty) UpdateHoverGeometry();
        // Sample the old envelope before restarting, including a second launch between frames.
        AdvanceItemBounces();
        visual.Bounce.Restart((float)visual.Icon.ActualHeight, isDock: IsDock);
        // Bounce uses the overflow presentation even when hover magnification is disabled.
        if (HorizontalHover) RenderHoverPresentation();
        if (_itemBounceRenderingSubscribed) return;
        _itemBounceLastFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += ItemBounce_Rendering;
        _itemBounceRenderingSubscribed = true;
    }

    private void ItemBounce_Rendering(object? sender, object args)
    {
        if (IsCompactList || !UseCustomAnimations || !HoverViewportVisible ||
            _itemReorderSession is { IsActive: true } || _shellDragActive ||
            _widgetDragging || _canvasResize is not null)
        {
            ResetItemBounces();
            return;
        }
        AdvanceItemBounces();
        if (!_hoverItems.Values.Any(item => item.Bounce.IsActive)) StopItemBounceRendering();
    }

    private void AdvanceItemBounces()
    {
        if (!_itemBounceRenderingSubscribed) return;
        // Icon grids do not run a hover wave. Scrolling can still move their
        // bounds while launch feedback is active, so this clock owns that refresh.
        if (_hoverGeometryDirty) UpdateHoverGeometry();
        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(_itemBounceLastFrame, now).TotalSeconds;
        _itemBounceLastFrame = now;
        foreach (var item in _hoverItems.Values)
        {
            if (!item.Bounce.IsActive) continue;
            item.Bounce.Step(seconds);
            ApplyItemBounce(item);
        }
        if (HorizontalHover)
        {
            RenderHoverPresentation();
            UpdateDockInputRegion();
        }
        else UpdateDockToolTipPosition();
    }

    private void ApplyItemBounce(HoverItemVisual item)
    {
        float scale = item.Presented ? item.RenderScale : item.Content.Scale.Y;
        var iconBounds = HoverLayoutMath.Transform(item.IconBounds, item.BaseBounds, scale,
            item.Presented ? item.Translation : Vector2.Zero, false);
        float travel = item.Bounce.IsActive
            ? ItemBounceMotion.FitOffsetToBounds(item.Bounce.Offset, (float)item.Icon.ActualHeight,
                scale, iconBounds, _hoverViewportBounds, _hoverSurfaceBounds, _hoverSurfaceRadius,
                item.Bounce.AmplitudeMultiplier)
            : 0;
        if (HorizontalHover && item.Bounce.IsActive)
        {
            float viewportTop = -_dockOverflowPixels / (float)(WindowRoot.XamlRoot?.RasterizationScale ?? 1);
            var screen = new NativeMethods.POINT();
            if (NativeMethods.ClientToScreen(_hwnd, ref screen))
            {
                var display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
                { Left = screen.X, Top = screen.Y + _dockOverflowPixels, Right = screen.X + 1, Bottom = screen.Y + _dockOverflowPixels + 1 });
                Point origin = ItemsScrollView.TransformToVisual(WindowRoot).TransformPoint(new Point());
                viewportTop = Math.Max(viewportTop, (float)((display.Monitor.Top - screen.Y) /
                    (WindowRoot.XamlRoot?.RasterizationScale ?? 1) - origin.Y));
            }
            travel = DockVisualMath.BounceOffset(item.Bounce.Offset,
                (float)item.Icon.ActualHeight * .18f * item.Bounce.AmplitudeMultiplier,
                iconBounds, _hoverSurfaceBounds.Y, viewportTop, scale);
        }
        Vector3 offset = new(0, travel, 0);
        item.Icon.Translation = offset;
        if (item.ProxyIcon is { } icon) icon.Translation = offset;
        // The layer/ScrollView still clips to the viewport. Only the proxy's local
        // content clip must allow the icon to rise above its resting content bounds.
        if (item.Proxy is { } proxy) proxy.Clip = item.Bounce.IsActive ? null : item.Clip;
    }

    private void ResetItemBounces()
    {
        if (!_itemBounceRenderingSubscribed) return;
        foreach (var item in _hoverItems.Values) ResetItemBounce(item);
        StopItemBounceRendering();
        if (HorizontalHover) UpdateDockInputRegion();
    }

    private void ResetItemBounce(HoverItemVisual item)
    {
        item.Bounce.Reset();
        ApplyItemBounce(item);
    }

    private void StopItemBounceRendering()
    {
        if (_itemBounceRenderingSubscribed) CompositionTarget.Rendering -= ItemBounce_Rendering;
        _itemBounceRenderingSubscribed = false;
        _itemBounceLastFrame = 0;
    }
}
