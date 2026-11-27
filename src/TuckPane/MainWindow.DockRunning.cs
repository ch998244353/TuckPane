using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TuckPane.Models;
using TuckPane.Core;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private Canvas? _dockDotsLayer;
    private SolidColorBrush? _dockDotBrush;
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
        if (_dockLayoutCommit.Pending) return;
        foreach (Border removed in _dockDots.Keys.Where(host => !_hoverItems.ContainsKey(host)).ToArray())
        {
            _dockDotsLayer?.Children.Remove(_dockDots[removed]);
            _dockDots.Remove(removed);
        }
        if (_dockDotsLayer is not null) _dockDotsLayer.Visibility = Visibility.Visible;
        float surfaceHeight = (float)ExpandedContentLayer.ActualHeight;
        foreach ((Border host, HoverItemVisual visual) in _hoverItems)
        {
            _dockDots.TryGetValue(host, out Ellipse? dot);
            if (host.DataContext is not WidgetItem item || !_host.IsDockItemOpen(item) ||
                DockRunningIconCenter(visual) is not Vector2 iconCenter)
            {
                if (dot is not null) dot.Visibility = Visibility.Collapsed;
                continue;
            }
            if (dot is null)
            {
                if (_dockDotsLayer is null)
                {
                    _dockDotsLayer = new Canvas { IsHitTestVisible = false };
                    Canvas.SetZIndex(_dockDotsLayer, 2000);
                    ExpandedContentLayer.Children.Add(_dockDotsLayer);
                }
                _dockDotBrush ??= new SolidColorBrush(WindowRoot.RequestedTheme == ElementTheme.Dark
                    ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
                dot = new Ellipse { Width = 4, Height = 4, IsHitTestVisible = false, Opacity = .78, Fill = _dockDotBrush };
                _dockDots[host] = dot;
                _dockDotsLayer.Children.Add(dot);
            }
            Vector2 center = DockVisualMath.RunningDotCenter(_dockGeometry, surfaceHeight, iconCenter);
            Canvas.SetLeft(dot, center.X - 2);
            Canvas.SetTop(dot, center.Y - 2);
            dot.Visibility = Visibility.Visible;
        }
    }

    private Vector2? DockRunningIconCenter(HoverItemVisual visual)
    {
        if (visual.Presented)
        {
            return HorizontalHover
                ? new((float)_hoverViewportOrigin.X + GetRenderedDockIcon(visual, includeBounce: false).Center.X, 0)
                : new(0, (float)_hoverViewportOrigin.Y + visual.IconBounds.Center.Y + visual.Translation.Y);
        }

        // The source stays laid out when the hover renderer is disabled. Measure from
        // its parent so the launch bounce on the icon itself does not displace the dot.
        if (visual.Content.XamlRoot is null || visual.Icon.ActualWidth <= 0 || visual.Icon.ActualHeight <= 0) return null;
        try
        {
            Point center = visual.Content.TransformToVisual(ExpandedContentLayer).TransformPoint(new Point(
                visual.Icon.ActualOffset.X + visual.Icon.ActualWidth / 2,
                visual.Icon.ActualOffset.Y + visual.Icon.ActualHeight / 2));
            return new((float)center.X, (float)center.Y);
        }
        catch (InvalidOperationException) { return null; }
    }
}
