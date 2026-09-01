using System.Numerics;
using TuckPane.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics.Effects;
using Microsoft.Graphics.DirectX;
using Windows.UI;

namespace TuckPane.Services;

internal sealed class SoftAcrylicSurface : IDisposable
{
    private const float BlurAmount = 30;
    private readonly FrameworkElement _host;
    private readonly SpriteVisual _visual;
    private readonly RectangleClip _clip;
    private CompositionBrush? _brush;
    private CanvasDevice? _canvasDevice;
    private CompositionGraphicsDevice? _graphicsDevice;
    private CompositionDrawingSurface? _noiseSurface;
    private CompositionSurfaceBrush? _noiseBrush;
    private bool _disposed;
    private double _cornerRadius;
    private float _extraOpacity;
    private GlassTheme _lastTheme = GlassTheme.Light;
    private bool _lastUseAcrylic;

    internal SoftAcrylicSurface(FrameworkElement host)
    {
        _host = host;
        Visual hostVisual = ElementCompositionPreview.GetElementVisual(host);
        Compositor compositor = hostVisual.Compositor;
        _visual = compositor.CreateSpriteVisual();
        _visual.BorderMode = CompositionBorderMode.Soft;
        _clip = compositor.CreateRectangleClip();
        _visual.Clip = _clip;
        ElementCompositionPreview.SetElementChildVisual(host, _visual);
        _host.SizeChanged += Host_SizeChanged;
        UpdateGeometry();
    }

    internal void SetTheme(GlassTheme theme, bool useAcrylic)
    {
        if (_disposed) return;
        _lastTheme = theme;
        _lastUseAcrylic = useAcrylic;
        ApplyBrush();
    }

    internal void SetExtraSurfaceOpacity(float opacity)
    {
        if (_disposed) return;
        opacity = Math.Clamp(opacity, 0f, 1f);
        if (!_lastUseAcrylic && _extraOpacity <= 0f && opacity <= 0f) return;
        bool changed = Math.Abs(_extraOpacity - opacity) > 0.0001f;
        _extraOpacity = opacity;
        if (changed || _visual.Brush is null) ApplyBrush();
    }

    private void ApplyBrush()
    {
        CompositionBrush next;
        try
        {
            next = _lastUseAcrylic
                ? CreateAcrylicBrush(_lastTheme)
                : _visual.Compositor.CreateColorBrush(GlassThemePalette.OrganizerSurfaceColor(_lastTheme));
        }
        catch (Exception ex)
        {
            AppLogger.Error("GPU 毛玻璃材质不可用，已切换为主题纯色。", ex);
            next = _visual.Compositor.CreateColorBrush(GlassThemePalette.OrganizerSurfaceColor(_lastTheme));
        }
        _visual.Brush = next;
        _brush?.Dispose();
        _brush = next;
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
        _noiseBrush?.Dispose();
        _noiseSurface?.Dispose();
        _graphicsDevice?.Dispose();
        _canvasDevice?.Dispose();
        _clip.Dispose();
        _visual.Dispose();
    }

    private CompositionBrush CreateAcrylicBrush(GlassTheme theme)
    {
        Compositor compositor = _visual.Compositor;
        (Color tint, Color luminosity, float tintOpacity, float luminosityOpacity) = GlassThemePalette.OrganizerAcrylic(theme);
        var backdrop = new CompositionEffectSourceParameter("Backdrop");
        var blur = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = BlurAmount,
            BorderMode = EffectBorderMode.Hard,
            Optimization = EffectOptimization.Balanced,
            Source = backdrop
        };
        var luminosityLayer = new OpacityEffect
        {
            Opacity = luminosityOpacity,
            Source = new ColorSourceEffect { Color = luminosity }
        };
        var tintLayer = new OpacityEffect
        {
            Opacity = tintOpacity,
            Source = new ColorSourceEffect { Color = tint }
        };
        var acrylic = new CompositeEffect { Mode = CanvasComposite.SourceOver };
        acrylic.Sources.Add(blur);
        acrylic.Sources.Add(luminosityLayer);
        acrylic.Sources.Add(tintLayer);
        if (_extraOpacity > 0f)
        {
            acrylic.Sources.Add(new OpacityEffect
            {
                Opacity = _extraOpacity,
                Source = new ColorSourceEffect { Color = GlassThemePalette.SurfaceColor(theme) }
            });
        }
        CompositionSurfaceBrush? noiseBrush = TryCreateNoiseBrush(compositor);
        if (noiseBrush is not null)
        {
            acrylic.Sources.Add(new OpacityEffect
            {
                Opacity = .018f,
                Source = new BorderEffect
                {
                    ExtendX = CanvasEdgeBehavior.Wrap,
                    ExtendY = CanvasEdgeBehavior.Wrap,
                    Source = new CompositionEffectSourceParameter("Noise")
                }
            });
        }

        CompositionEffectFactory factory = compositor.CreateEffectFactory(acrylic);
        CompositionEffectBrush effectBrush = factory.CreateBrush();
        effectBrush.SetSourceParameter("Backdrop", compositor.CreateBackdropBrush());
        if (noiseBrush is not null) effectBrush.SetSourceParameter("Noise", noiseBrush);
        factory.Dispose();
        return effectBrush;
    }

    private CompositionSurfaceBrush? TryCreateNoiseBrush(Compositor compositor)
    {
        if (_noiseBrush is not null) return _noiseBrush;
        try
        {
            _canvasDevice = CanvasDevice.GetSharedDevice();
            _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _canvasDevice);
            _noiseSurface = _graphicsDevice.CreateDrawingSurface(
                new Windows.Foundation.Size(32, 32),
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied);
            using (CanvasDrawingSession drawing = CanvasComposition.CreateDrawingSession(_noiseSurface))
            {
                drawing.Clear(Colors.Transparent);
                for (int y = 0; y < 32; y++)
                {
                    for (int x = 0; x < 32; x++)
                    {
                        byte alpha = (byte)(18 + ((x * 17 + y * 29) & 15));
                        byte value = ((x * 13 + y * 7) & 1) == 0 ? (byte)255 : (byte)0;
                        drawing.FillRectangle(x, y, 1, 1, ColorHelper.FromArgb(alpha, value, value, value));
                    }
                }
            }
            _noiseBrush = compositor.CreateSurfaceBrush(_noiseSurface);
            _noiseBrush.Stretch = CompositionStretch.None;
            return _noiseBrush;
        }
        catch (Exception ex)
        {
            AppLogger.Error("细噪点材质创建失败；毛玻璃将继续使用模糊与色调层。", ex);
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

        _visual.Size = new Vector2(width, height);
        _clip.Left = 0;
        _clip.Top = 0;
        _clip.Right = width;
        _clip.Bottom = height;
        var corner = new Vector2(Math.Max(0, radius));
        _clip.TopLeftRadius = corner;
        _clip.TopRightRadius = corner;
        _clip.BottomLeftRadius = corner;
        _clip.BottomRightRadius = corner;
    }
}
