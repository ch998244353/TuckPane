using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TuckPane.Models;
using TuckPane.Core;
using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private Canvas? _dockDotsLayer;
    private readonly Dictionary<Border, Ellipse> _dockDots = [];
    internal bool WantsDockRunningState => IsDock && HoverViewportVisible && _appWindow is { IsVisible: true };
    internal bool IsOpenForDock => !_closing && IsExpanded && _appWindow is { IsVisible: true };

    internal void UpdateDockRunningVisuals()
    {
        if (!WantsDockRunningState)
        {
            if (_dockDotsLayer is not null) _dockDotsLayer.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (Border removed in _dockDots.Keys.Where(host => !_hoverItems.ContainsKey(host)).ToArray())
        {
            _dockDotsLayer?.Children.Remove(_dockDots[removed]);
            _dockDots.Remove(removed);
        }
        if (_dockDotsLayer is not null) _dockDotsLayer.Visibility = Visibility.Visible;
        ThemeValues theme = _host.State.GlobalSettings.GetTheme(OrganizerKinds.ThemeFor(_definition.PlacementMode));
        var color = ThemePalette.IsDark(theme) ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        Point origin = ItemsScrollView.TransformToVisual(ExpandedContentLayer).TransformPoint(new Point());
        foreach ((Border host, HoverItemVisual visual) in _hoverItems)
        {
            bool open = host.DataContext is WidgetItem item && _host.IsDockItemOpen(item);
            if (!_dockDots.TryGetValue(host, out Ellipse? dot))
            {
                if (!open) continue;
                if (_dockDotsLayer is null)
                {
                    _dockDotsLayer = new Canvas { IsHitTestVisible = false };
                    Canvas.SetZIndex(_dockDotsLayer, 2000);
                    ExpandedContentLayer.Children.Add(_dockDotsLayer);
                }
                dot = new Ellipse { Width = 4, Height = 4, IsHitTestVisible = false, Opacity = .78 };
                _dockDots[host] = dot;
                _dockDotsLayer.Children.Add(dot);
            }
            dot.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (!open) continue;
            if (dot.Fill is not SolidColorBrush brush || brush.Color != color) dot.Fill = new SolidColorBrush(color);
            if (HorizontalHover)
            {
                var icon = GetRenderedDockIcon(visual, includeBounce: false);
                Canvas.SetLeft(dot, origin.X + icon.Center.X - 2);
                double centerY = _hoverSurfaceBounds.Bottom - Math.Max(5, _dockGeometry.BottomInset / 2);
                Canvas.SetTop(dot, origin.Y + centerY - 2);
            }
            else
            {
                var center = DockVisualMath.VerticalRunningDotCenter(_hoverSurfaceBounds.X,
                    (float)_dockGeometry.LeftInset, visual.IconBounds, visual.Presented ? visual.Translation.Y : 0);
                Canvas.SetLeft(dot, origin.X + center.X - 2);
                Canvas.SetTop(dot, origin.Y + center.Y - 2);
            }
        }
    }
}
