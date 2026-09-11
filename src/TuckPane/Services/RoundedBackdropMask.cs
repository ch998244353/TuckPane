using System.Numerics;
using TuckPane.Core;
using Windows.UI;
using Windows.UI.Composition;

namespace TuckPane.Services;

// The local system backdrop uses the Windows compositor, independently of
// XAML's Microsoft compositor. Give that brush an explicit antialiased alpha
// boundary instead of relying on an external backdrop link's clip mode.
internal sealed class RoundedBackdropMask : IDisposable
{
    private CompositionRoundedRectangleGeometry _geometry = null!;
    private CompositionSpriteShape _shape = null!;
    private CompositionColorBrush _fill = null!;
    private ShapeVisual _visual = null!;
    private CompositionVisualSurface _surface = null!;
    private CompositionSurfaceBrush _mask = null!;
    private bool _disposed;
    private RoundedSurfaceGeometry? _lastGeometry;
    private double _lastScale;

    internal RoundedBackdropMask(Compositor compositor)
    {
        try
        {
            _geometry = compositor.CreateRoundedRectangleGeometry();
            _shape = compositor.CreateSpriteShape(_geometry);
            _fill = compositor.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));
            _shape.FillBrush = _fill;
            _visual = compositor.CreateShapeVisual();
            _visual.BorderMode = CompositionBorderMode.Soft;
            _visual.Shapes.Add(_shape);
            _surface = compositor.CreateVisualSurface();
            _surface.SourceVisual = _visual;
            _mask = compositor.CreateSurfaceBrush(_surface);
            _mask.Stretch = CompositionStretch.Fill;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Update(RoundedSurfaceGeometry geometry, double rasterizationScale)
    {
        if (_lastGeometry == geometry && _lastScale == rasterizationScale) return;
        float scale = (float)(double.IsFinite(rasterizationScale) && rasterizationScale > 0
            ? rasterizationScale : 1);
        // Capture at physical-pixel resolution; a 96-DPI mask stretched by
        // the target on a high-DPI monitor would resample the edge again.
        Vector2 size = new(geometry.Width * scale, geometry.Height * scale);
        _geometry.Size = size;
        _geometry.CornerRadius = new Vector2(geometry.Radius * scale);
        _visual.Size = size;
        _surface.SourceSize = Vector2.Max(Vector2.One, size);
        _lastGeometry = geometry;
        _lastScale = rasterizationScale;
    }

    // The caller owns the returned brush and its source. This object only
    // owns the reusable mask resources; dispose it after disconnecting them.
    internal CompositionMaskBrush Wrap(CompositionBrush source)
    {
        CompositionMaskBrush brush = _visual.Compositor.CreateMaskBrush();
        try
        {
            brush.Source = source;
            brush.Mask = _mask;
            return brush;
        }
        catch
        {
            brush.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mask?.Dispose();
        _surface?.Dispose();
        _visual?.Dispose();
        _shape?.Dispose();
        _fill?.Dispose();
        _geometry?.Dispose();
    }
}
