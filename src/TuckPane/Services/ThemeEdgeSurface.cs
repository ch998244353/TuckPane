using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using TuckPane.Models;
using Windows.UI;

namespace TuckPane.Services;

/// <summary>
/// Dedicated neutral glass edge layer. The overlay host is separate from the
/// backdrop host so later XAML content cannot cover the edge visual.
/// </summary>
internal sealed class ThemeEdgeSurface : IDisposable
{
    private readonly FrameworkElement _host;
    private readonly ShapeVisual _visual;
    private readonly CompositionRoundedRectangleGeometry _outerGeometry;
    private readonly CompositionRoundedRectangleGeometry _innerGeometry;
    private readonly CompositionRoundedRectangleGeometry _highlightGeometry;
    private readonly CompositionRoundedRectangleGeometry _textureGeometry;
    private readonly CompositionSpriteShape _outerShape;
    private readonly CompositionSpriteShape _innerShape;
    private readonly CompositionSpriteShape _highlightShape;
    private readonly CompositionSpriteShape _textureShape;
    private readonly CompositionLinearGradientBrush _outerBrush;
    private readonly CompositionLinearGradientBrush _innerBrush;
    private readonly CompositionLinearGradientBrush _highlightBrush;
    private readonly CompositionLinearGradientBrush _textureBrush;
    private XamlRoot? _xamlRoot;
    private double _cornerRadius;
    private bool _disposed;

    internal ThemeEdgeSurface(FrameworkElement host)
    {
        _host = host;
        Visual hostVisual = ElementCompositionPreview.GetElementVisual(host);
        Compositor compositor = hostVisual.Compositor;
        _visual = compositor.CreateShapeVisual();
        _visual.BorderMode = CompositionBorderMode.Soft;

        _outerGeometry = compositor.CreateRoundedRectangleGeometry();
        _innerGeometry = compositor.CreateRoundedRectangleGeometry();
        _highlightGeometry = compositor.CreateRoundedRectangleGeometry();
        _textureGeometry = compositor.CreateRoundedRectangleGeometry();
        _outerShape = compositor.CreateSpriteShape(_outerGeometry);
        _innerShape = compositor.CreateSpriteShape(_innerGeometry);
        _highlightShape = compositor.CreateSpriteShape(_highlightGeometry);
        _textureShape = compositor.CreateSpriteShape(_textureGeometry);

        _outerBrush = CreateBrush(ThemePalette.OrganizerGlassOuterEdgeStops, new(0, 0), new(1, 1));
        _innerBrush = CreateBrush(ThemePalette.OrganizerGlassInnerEdgeStops, new(1, 0), new(0, 1));
        _highlightBrush = CreateBrush(ThemePalette.GlassEdgeHighlightStops, new(0, 0), new(1, 0));
        _textureBrush = CreateBrush(ThemePalette.GlassEdgeTextureStops, new(0, 0), new(1, 0));

        _outerShape.StrokeBrush = _outerBrush;
        _innerShape.StrokeBrush = _innerBrush;
        _highlightShape.StrokeBrush = _highlightBrush;
        _textureShape.StrokeBrush = _textureBrush;
        _outerShape.StrokeThickness = ThemePalette.OrganizerGlassOuterEdgeThicknessDip;
        _innerShape.StrokeThickness = ThemePalette.OrganizerGlassInnerEdgeThicknessDip;
        _highlightShape.StrokeThickness = ThemePalette.GlassEdgeHighlightThicknessDip;
        _textureShape.StrokeThickness = ThemePalette.GlassEdgeTextureThicknessDip;
        _outerShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _innerShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _highlightShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _textureShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _visual.Shapes.Add(_outerShape);
        _visual.Shapes.Add(_innerShape);
        // Keep the wider highlight and fine texture above the base rings so
        // their middle gradient stops remain visible rather than being
        // covered by the later base shapes.
        _visual.Shapes.Add(_textureShape);
        _visual.Shapes.Add(_highlightShape);
        _visual.Opacity = ThemePalette.GlassEdgeMinimumOpacity;

        ElementCompositionPreview.SetElementChildVisual(host, _visual);
        _host.Loaded += Host_Loaded;
        _host.SizeChanged += Host_SizeChanged;
        AttachXamlRoot(_host.XamlRoot);
        RefreshGeometry();
    }

    internal void SetTheme(ThemeValues theme, bool useEffects)
    {
        // These brushes intentionally never read theme colour, opacity or
        // blur settings. A non-zero floor keeps the neutral edge visible in
        // transparent, solid-colour, zero-blur and fallback states.
        if (!_disposed) _visual.Opacity = ThemePalette.GlassEdgeMinimumOpacity;
    }

    internal void SetEnabled(bool enabled)
    {
        if (!_disposed) _visual.IsVisible = enabled;
    }

    internal void SetCornerRadius(double radius)
    {
        _cornerRadius = Math.Max(0, radius);
        RefreshGeometry();
    }

    internal void RefreshGeometry()
    {
        if (_disposed) return;
        double scale = Math.Max(1, _host.XamlRoot?.RasterizationScale ?? 1);
        float width = PixelAligned(_host.ActualWidth > 0 ? _host.ActualWidth : _host.Width, scale);
        float height = PixelAligned(_host.ActualHeight > 0 ? _host.ActualHeight : _host.Height, scale);
        if (width <= 0 || height <= 0)
        {
            _visual.Size = Vector2.Zero;
            return;
        }
        float radius = Math.Min(PixelAligned(_cornerRadius, scale), Math.Min(width, height) / 2);
        _visual.Size = new Vector2(width, height);
        ConfigureGeometry(_outerGeometry, width, height, radius,
            ThemePalette.OrganizerGlassOuterEdgeThicknessDip / 2, scale);
        ConfigureGeometry(_innerGeometry, width, height, radius,
            ThemePalette.OrganizerGlassInnerEdgeInsetDip + ThemePalette.OrganizerGlassInnerEdgeThicknessDip / 2, scale);
        ConfigureGeometry(_highlightGeometry, width, height, radius, 0, scale);
        ConfigureGeometry(_textureGeometry, width, height, radius, 0, scale);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Loaded -= Host_Loaded;
        _host.SizeChanged -= Host_SizeChanged;
        AttachXamlRoot(null);
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _outerBrush.Dispose();
        _innerBrush.Dispose();
        _highlightBrush.Dispose();
        _textureBrush.Dispose();
        _outerShape.Dispose();
        _innerShape.Dispose();
        _highlightShape.Dispose();
        _textureShape.Dispose();
        _outerGeometry.Dispose();
        _innerGeometry.Dispose();
        _highlightGeometry.Dispose();
        _textureGeometry.Dispose();
        _visual.Dispose();
    }

    private CompositionLinearGradientBrush CreateBrush(
        IReadOnlyList<(float Offset, Color Color)> stops,
        Vector2 start,
        Vector2 end)
    {
        CompositionLinearGradientBrush brush = _visual.Compositor.CreateLinearGradientBrush();
        brush.StartPoint = start;
        brush.EndPoint = end;
        foreach ((float offset, Color color) in stops)
            brush.ColorStops.Add(_visual.Compositor.CreateColorGradientStop(offset, color));
        return brush;
    }

    private void Host_Loaded(object sender, RoutedEventArgs e)
    {
        AttachXamlRoot(_host.XamlRoot);
        RefreshGeometry();
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshGeometry();

    private void AttachXamlRoot(XamlRoot? next)
    {
        if (ReferenceEquals(_xamlRoot, next)) return;
        if (_xamlRoot is not null) _xamlRoot.Changed -= XamlRoot_Changed;
        _xamlRoot = next;
        if (_xamlRoot is not null) _xamlRoot.Changed += XamlRoot_Changed;
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => RefreshGeometry();

    private static float PixelAligned(double dip, double scale)
    {
        // Grid overlays normally report NaN for Width/Height until their
        // first arrange pass. Never forward that sentinel into Composition;
        // a NaN Size can make the child visual fail to render permanently.
        if (!double.IsFinite(dip) || dip <= 0 || !double.IsFinite(scale) || scale <= 0)
            return 0;
        return (float)(Math.Round(dip * scale) / scale);
    }

    private static void ConfigureGeometry(
        CompositionRoundedRectangleGeometry geometry,
        float width,
        float height,
        float radius,
        float inset,
        double scale)
    {
        float alignedInset = PixelAligned(inset, scale);
        geometry.Offset = new Vector2(alignedInset);
        geometry.Size = new Vector2(
            Math.Max(0, width - alignedInset * 2),
            Math.Max(0, height - alignedInset * 2));
        geometry.CornerRadius = new Vector2(Math.Max(0, radius - alignedInset));
    }
}
