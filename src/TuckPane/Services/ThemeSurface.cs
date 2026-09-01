using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using TuckPane.Models;
using Windows.Graphics.Effects;
using Windows.UI;

namespace TuckPane.Services;

internal sealed class ThemeSurface : IDisposable
{
    private readonly FrameworkElement _host;
    private readonly ContainerVisual _root;
    private readonly SpriteVisual _visual;
    private readonly RectangleClip _clip;
    private readonly ShapeVisual _edgeVisual;
    private readonly CompositionRoundedRectangleGeometry _outerEdgeGeometry;
    private readonly CompositionRoundedRectangleGeometry _innerEdgeGeometry;
    private readonly CompositionSpriteShape _outerEdgeShape;
    private readonly CompositionSpriteShape _innerEdgeShape;
    private CompositionBrush? _brush;
    private CompositionBrush? _outerEdgeBrush;
    private CompositionBrush? _innerEdgeBrush;
    private CanvasDevice? _canvasDevice;
    private CompositionGraphicsDevice? _graphicsDevice;
    private CompositionDrawingSurface? _noiseSurface;
    private CompositionSurfaceBrush? _noiseBrush;
    private bool _disposed;
    private double _cornerRadius;

    // Child visuals render above their host's XAML content, so this must be an empty background host.
    internal ThemeSurface(FrameworkElement emptyBackgroundHost)
    {
        _host = emptyBackgroundHost;
        Visual hostVisual = ElementCompositionPreview.GetElementVisual(emptyBackgroundHost);
        Compositor compositor = hostVisual.Compositor;
        _root = compositor.CreateContainerVisual();
        _visual = compositor.CreateSpriteVisual();
        _visual.BorderMode = CompositionBorderMode.Soft;
        _clip = compositor.CreateRectangleClip();
        _visual.Clip = _clip;
        _edgeVisual = compositor.CreateShapeVisual();
        _outerEdgeGeometry = compositor.CreateRoundedRectangleGeometry();
        _innerEdgeGeometry = compositor.CreateRoundedRectangleGeometry();
        _outerEdgeShape = compositor.CreateSpriteShape(_outerEdgeGeometry);
        _innerEdgeShape = compositor.CreateSpriteShape(_innerEdgeGeometry);
        _outerEdgeShape.StrokeThickness = ThemePalette.OuterEdgeThickness;
        _innerEdgeShape.StrokeThickness = ThemePalette.InnerEdgeThickness;
        _edgeVisual.Shapes.Add(_outerEdgeShape);
        _edgeVisual.Shapes.Add(_innerEdgeShape);
        _root.Children.InsertAtBottom(_visual);
        _root.Children.InsertAtTop(_edgeVisual);
        ElementCompositionPreview.SetElementChildVisual(emptyBackgroundHost, _root);
        _host.SizeChanged += Host_SizeChanged;
        UpdateGeometry();
    }

    internal void SetTheme(ThemeValues theme, bool useEffects) =>
        SetTheme(theme, theme.Material, useEffects);

    internal void SetTheme(ThemeValues theme, ThemeMaterial material, bool useEffects)
    {
        if (_disposed) return;
        CompositionBrush next;
        try
        {
            next = useEffects
                ? CreateMaterialBrush(theme, material)
                : _visual.Compositor.CreateColorBrush(ThemePalette.MaterialTintColor(theme, material));
        }
        catch (Exception ex)
        {
            AppLogger.Error("GPU 主题材质不可用，已切换为主题纯色。", ex);
            next = _visual.Compositor.CreateColorBrush(ThemePalette.MaterialTintColor(theme, material));
        }
        CompositionBrush? nextOuterEdge = null;
        CompositionBrush? nextInnerEdge = null;
        try
        {
            nextOuterEdge = CreateEdgeBrush(ThemePalette.OuterEdgeStops(material));
            nextInnerEdge = CreateEdgeBrush(ThemePalette.InnerEdgeStops(material));
        }
        catch (Exception ex)
        {
            AppLogger.Error("材质物理边缘不可用，已保留基础主题材质。", ex);
            nextOuterEdge?.Dispose();
            nextOuterEdge = null;
            nextInnerEdge?.Dispose();
            nextInnerEdge = null;
        }
        _visual.Brush = next;
        _outerEdgeShape.StrokeBrush = nextOuterEdge;
        _innerEdgeShape.StrokeBrush = nextInnerEdge;
        _brush?.Dispose();
        _outerEdgeBrush?.Dispose();
        _innerEdgeBrush?.Dispose();
        _brush = next;
        _outerEdgeBrush = nextOuterEdge;
        _innerEdgeBrush = nextInnerEdge;
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
        _host.SizeChanged -= Host_SizeChanged;
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _brush?.Dispose();
        _outerEdgeBrush?.Dispose();
        _innerEdgeBrush?.Dispose();
        _noiseBrush?.Dispose();
        _noiseSurface?.Dispose();
        _graphicsDevice?.Dispose();
        _canvasDevice?.Dispose();
        _clip.Dispose();
        _visual.Dispose();
        _outerEdgeShape.Dispose();
        _innerEdgeShape.Dispose();
        _outerEdgeGeometry.Dispose();
        _innerEdgeGeometry.Dispose();
        _edgeVisual.Dispose();
        _root.Dispose();
    }

    private CompositionBrush CreateMaterialBrush(ThemeValues theme, ThemeMaterial materialKind)
    {
        Compositor compositor = _visual.Compositor;
        ThemeEffectParameters parameters = ThemePalette.Effect(materialKind);
        float tintOpacity = ThemePalette.EffectiveTintOpacity(theme, materialKind);
        var backdrop = new CompositionEffectSourceParameter("Backdrop");
        var material = new CompositeEffect { Mode = CanvasComposite.SourceOver };
        IGraphicsEffectSource filteredBackdrop = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = parameters.BlurAmount,
            BorderMode = EffectBorderMode.Hard,
            Optimization = EffectOptimization.Balanced,
            Source = backdrop
        };
        filteredBackdrop = new SaturationEffect
        {
            Saturation = parameters.Saturation,
            Source = filteredBackdrop
        };
        material.Sources.Add(filteredBackdrop);
        material.Sources.Add(new OpacityEffect
        {
            Opacity = parameters.LuminosityOpacity * tintOpacity,
            Source = new ColorSourceEffect { Color = ThemePalette.LuminosityColor(theme) }
        });
        material.Sources.Add(new OpacityEffect
        {
            Opacity = tintOpacity,
            Source = new ColorSourceEffect { Color = ThemePalette.MaterialTintColor(theme, materialKind) }
        });

        CompositionSurfaceBrush? noiseBrush = parameters.NoiseOpacity > 0 ? TryCreateNoiseBrush(compositor) : null;
        if (noiseBrush is not null)
        {
            material.Sources.Add(new OpacityEffect
            {
                Opacity = parameters.NoiseOpacity,
                Source = new BorderEffect
                {
                    ExtendX = CanvasEdgeBehavior.Wrap,
                    ExtendY = CanvasEdgeBehavior.Wrap,
                    Source = new CompositionEffectSourceParameter("Noise")
                }
            });
        }

        CompositionEffectFactory factory = compositor.CreateEffectFactory(material);
        CompositionEffectBrush effectBrush = factory.CreateBrush();
        effectBrush.SetSourceParameter("Backdrop", compositor.CreateBackdropBrush());
        if (noiseBrush is not null) effectBrush.SetSourceParameter("Noise", noiseBrush);
        factory.Dispose();
        return effectBrush;
    }

    private CompositionLinearGradientBrush CreateEdgeBrush(
        IReadOnlyList<(float Offset, Color Color)> stops)
    {
        CompositionLinearGradientBrush brush = _visual.Compositor.CreateLinearGradientBrush();
        brush.StartPoint = Vector2.Zero;
        brush.EndPoint = Vector2.One;
        foreach ((float offset, Color color) in stops)
            brush.ColorStops.Add(_visual.Compositor.CreateColorGradientStop(offset, color));
        return brush;
    }

    private CompositionSurfaceBrush? TryCreateNoiseBrush(Compositor compositor)
    {
        if (_noiseBrush is not null) return _noiseBrush;
        try
        {
            _canvasDevice = CanvasDevice.GetSharedDevice();
            _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _canvasDevice);
            _noiseSurface = _graphicsDevice.CreateDrawingSurface(
                new Windows.Foundation.Size(64, 64),
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied);
            using (CanvasDrawingSession drawing = CanvasComposition.CreateDrawingSession(_noiseSurface))
            {
                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        uint hash = (uint)(x * 374761393 + y * 668265263);
                        hash = (hash ^ (hash >> 13)) * 1274126177;
                        byte value = (byte)(hash >> 24);
                        drawing.FillRectangle(x, y, 1, 1, ColorHelper.FromArgb(255, value, value, value));
                    }
                }
            }
            _noiseBrush = compositor.CreateSurfaceBrush(_noiseSurface);
            _noiseBrush.Stretch = CompositionStretch.None;
            return _noiseBrush;
        }
        catch (Exception ex)
        {
            AppLogger.Error("细噪点材质创建失败；主题将继续使用模糊与色调层。", ex);
            _noiseBrush?.Dispose();
            _noiseBrush = null;
            _noiseSurface?.Dispose();
            _noiseSurface = null;
            _graphicsDevice?.Dispose();
            _graphicsDevice = null;
            _canvasDevice?.Dispose();
            _canvasDevice = null;
            return null;
        }
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateGeometry();

    private void UpdateGeometry()
    {
        if (_disposed) return;
        double scale = Math.Max(1, _host.XamlRoot?.RasterizationScale ?? 1);
        float width = (float)(Math.Round(Math.Max(0, _host.ActualWidth) * scale) / scale);
        float height = (float)(Math.Round(Math.Max(0, _host.ActualHeight) * scale) / scale);
        float radius = (float)(Math.Round(_cornerRadius * scale) / scale);
        radius = Math.Min(radius, Math.Min(width, height) / 2);

        var size = new Vector2(width, height);
        _root.Size = size;
        _visual.Size = size;
        _edgeVisual.Size = size;
        _clip.Left = 0;
        _clip.Top = 0;
        _clip.Right = width;
        _clip.Bottom = height;
        var corner = new Vector2(Math.Max(0, radius));
        _clip.TopLeftRadius = corner;
        _clip.TopRightRadius = corner;
        _clip.BottomLeftRadius = corner;
        _clip.BottomRightRadius = corner;
        float outerOffset = ThemePalette.OuterEdgeThickness / 2;
        _outerEdgeGeometry.Offset = new Vector2(outerOffset);
        _outerEdgeGeometry.Size = new Vector2(
            Math.Max(0, width - outerOffset * 2),
            Math.Max(0, height - outerOffset * 2));
        _outerEdgeGeometry.CornerRadius = new Vector2(Math.Max(0, radius - outerOffset));

        float innerOffset = ThemePalette.InnerEdgeInset + ThemePalette.InnerEdgeThickness / 2;
        _innerEdgeGeometry.Offset = new Vector2(innerOffset);
        _innerEdgeGeometry.Size = new Vector2(
            Math.Max(0, width - innerOffset * 2),
            Math.Max(0, height - innerOffset * 2));
        _innerEdgeGeometry.CornerRadius = new Vector2(Math.Max(0, radius - innerOffset));
    }
}
