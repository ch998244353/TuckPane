using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using TuckPane.Models;

namespace TuckPane.Services;

/// <summary>
/// Draws only the interior glass sheen above a ThemeBackdrop. Persistent
/// edge geometry lives in ThemeEdgeSurface so it has an independent host and
/// cannot be covered by content siblings.
/// </summary>
internal sealed class ThemeSurface : IDisposable
{
    private readonly FrameworkElement _host;
    private readonly ContainerVisual _root;
    private readonly SpriteVisual _highlightVisual;
    private readonly RectangleClip _highlightClip;
    private CompositionBrush? _highlightBrush;
    private double _cornerRadius;
    private bool _disposed;

    internal ThemeSurface(FrameworkElement emptyBackgroundHost)
    {
        _host = emptyBackgroundHost;
        Visual hostVisual = ElementCompositionPreview.GetElementVisual(emptyBackgroundHost);
        Compositor compositor = hostVisual.Compositor;
        _root = compositor.CreateContainerVisual();
        _root.BorderMode = CompositionBorderMode.Soft;
        _highlightVisual = compositor.CreateSpriteVisual();
        _highlightVisual.BorderMode = CompositionBorderMode.Soft;
        _highlightClip = compositor.CreateRectangleClip();
        _highlightVisual.Clip = _highlightClip;
        _root.Children.InsertAtTop(_highlightVisual);

        ElementCompositionPreview.SetElementChildVisual(emptyBackgroundHost, _root);
        _host.Loaded += Host_Loaded;
        _host.SizeChanged += Host_SizeChanged;
        UpdateGeometry();
    }

    internal void SetTheme(ThemeValues theme, bool useEffects)
    {
        if (_disposed) return;

        CompositionBrush? nextHighlight = null;
        try
        {
            ThemeCompositionPlan plan = ThemePalette.BuildCompositionPlan(theme, useEffects);
            if (plan.HighlightOpacity > .0001f)
                nextHighlight = CreateHighlightBrush(ThemePalette.GlassHighlightStops, plan.HighlightOpacity);
        }
        catch (Exception ex)
        {
            AppLogger.Error("玻璃内部高光不可用；主题将保留无高光背景。", ex);
            nextHighlight?.Dispose();
            nextHighlight = null;
        }

        _highlightVisual.Brush = nextHighlight;
        _highlightBrush?.Dispose();
        _highlightBrush = nextHighlight;
    }

    internal void SetCornerRadius(double radius)
    {
        _cornerRadius = Math.Max(0, radius);
        UpdateGeometry();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Loaded -= Host_Loaded;
        _host.SizeChanged -= Host_SizeChanged;
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _highlightBrush?.Dispose();
        _highlightClip.Dispose();
        _highlightVisual.Dispose();
        _root.Dispose();
    }

    private CompositionLinearGradientBrush CreateHighlightBrush(
        IReadOnlyList<(float Offset, Windows.UI.Color Color)> stops,
        float opacity)
    {
        CompositionLinearGradientBrush brush = _highlightVisual.Compositor.CreateLinearGradientBrush();
        brush.StartPoint = Vector2.Zero;
        brush.EndPoint = Vector2.One;
        foreach ((float offset, Windows.UI.Color color) in stops)
        {
            brush.ColorStops.Add(_highlightVisual.Compositor.CreateColorGradientStop(
                offset,
                ThemePalette.WithOpacity(color, opacity)));
        }
        return brush;
    }

    private void Host_Loaded(object sender, RoutedEventArgs e) => UpdateGeometry();

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateGeometry();

    private void UpdateGeometry()
    {
        if (_disposed) return;
        double scale = Math.Max(1, _host.XamlRoot?.RasterizationScale ?? 1);
        float width = (float)(Math.Round(Math.Max(0, _host.ActualWidth) * scale) / scale);
        float height = (float)(Math.Round(Math.Max(0, _host.ActualHeight) * scale) / scale);
        float radius = (float)(Math.Round(_cornerRadius * scale) / scale);
        radius = Math.Min(radius, Math.Min(width, height) / 2);

        Vector2 size = new(width, height);
        _root.Size = size;
        _highlightVisual.Size = size;
        _highlightClip.Left = 0;
        _highlightClip.Top = 0;
        _highlightClip.Right = width;
        _highlightClip.Bottom = height;
        Vector2 corner = new(Math.Max(0, radius));
        _highlightClip.TopLeftRadius = corner;
        _highlightClip.TopRightRadius = corner;
        _highlightClip.BottomLeftRadius = corner;
        _highlightClip.BottomRightRadius = corner;
    }
}
