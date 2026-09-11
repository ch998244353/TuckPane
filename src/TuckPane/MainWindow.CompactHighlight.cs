using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private Point? _compactHighlightPointer;
    private Border? _compactHighlightedHost;
    private Canvas? _compactHighlightLayer;
    private Border? _compactHighlight;
    // A XAML Geometry has one owner. Share Rect values with the proxy layer, never its Clip instance.
    private readonly RectangleGeometry _compactHighlightClip = new();

    private void UpdateCompactRowHighlight(Point pointer)
    {
        if (!IsCompactList) return;
        _compactHighlightPointer = pointer;
        RefreshCompactRowHighlight();
    }

    private void RefreshCompactRowHighlight()
    {
        bool pressed = _itemReorderSession is { State: ItemDragState.Pressed };
        if (!IsCompactList || !HoverViewportVisible || _hoverLayoutPending ||
            HoverInteractionBusy && !pressed || _compactHighlightPointer is not { } pointer)
        {
            ClearCompactRowHighlight(forgetPointer: !IsCompactList || !HoverViewportVisible);
            return;
        }
        // This is independent of the wave's enabled flag and smoothed pointer.
        Border? host = pressed && _hoverPressedHost is { } captured
            ? captured : HoverItemAtViewportPoint(pointer);
        if (host is null || !_hoverItems.TryGetValue(host, out var item))
        {
            ClearCompactRowHighlight();
            return;
        }
        _compactHighlightedHost = host;
        host.Background = _transparentItemBrush;
        if (_compactHighlight is null)
        {
            var layer = new Canvas { IsHitTestVisible = false, Clip = _compactHighlightClip };
            var highlight = new Border { IsHitTestVisible = false };
            try
            {
                Canvas.SetZIndex(layer, -2);
                layer.Children.Add(highlight);
                ExpandedContentLayer.Children.Add(layer);
            }
            catch
            {
                // Failed attachment must not leave the reusable clip owned by an abandoned Canvas.
                layer.Clip = null;
                throw;
            }
            _compactHighlightLayer = layer;
            _compactHighlight = highlight;
        }
        HoverRect row = item.Presented
            ? HoverLayoutMath.Transform(item.RowBounds, item.BaseBounds, item.RenderScale, item.Translation, true)
            : item.RowBounds;
        row = row.Intersect(_hoverViewportBounds);
        _compactHighlight.Width = row.Width;
        _compactHighlight.Height = row.Height;
        _compactHighlight.Translation = new Vector3((float)_hoverViewportOrigin.X + row.X,
            (float)_hoverViewportOrigin.Y + row.Y, 0);
        _compactHighlight.CornerRadius = host.CornerRadius;
        _compactHighlight.Background = pressed && ReferenceEquals(host, _hoverPressedHost)
            ? _pressedItemBrush : _hoveredItemBrush;
        _compactHighlight.Visibility = row.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearCompactRowHighlight(bool forgetPointer = false)
    {
        _compactHighlightedHost = null;
        if (_compactHighlight is not null) _compactHighlight.Visibility = Visibility.Collapsed;
        if (forgetPointer) _compactHighlightPointer = null;
    }
}
