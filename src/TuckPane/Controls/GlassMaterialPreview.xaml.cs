using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using TuckPane.Models;
using TuckPane.Services;
using Windows.UI.ViewManagement;

namespace TuckPane.Controls;

public sealed partial class GlassMaterialPreview : UserControl
{
    private ThemeValues _theme;
    private bool _active;
    private bool _effects;
    private bool _edgeGlow;
    private bool _highContrast;
    private ThemeSurface? _surface;
    private ThemeEdgeSurface? _edge;
    private SampleBrush? _backdrop;

    public GlassMaterialPreview()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (_active) ShowMaterial(); };
        Unloaded += (_, _) => ReleaseMaterial();
    }

    internal void Update(ThemeValues theme, bool effects, bool edgeGlow, bool highContrast, string sampleName)
    {
        SampleLabel.Text = sampleName;
        if (_active && !highContrast && _theme == theme && _effects == effects && _edgeGlow == edgeGlow && _highContrast == highContrast)
            return;
        _active = true;
        _theme = theme;
        _effects = effects;
        _edgeGlow = edgeGlow;
        _highContrast = highContrast;
        if (IsLoaded) ShowMaterial();
    }

    private void ShowMaterial()
    {
        Landscape.Visibility = _highContrast ? Visibility.Collapsed : Visibility.Visible;
        if (_highContrast)
        {
            ReleaseMaterial();
            UISettings system = new();
            SampleBackdrop.Background = new SolidColorBrush(system.UIElementColor(UIElementType.Window));
            SampleLabel.Foreground = new SolidColorBrush(system.UIElementColor(UIElementType.WindowText));
            return;
        }

        if (_backdrop is null)
        {
            Compositor compositor = ElementCompositionPreview.GetElementVisual(SampleBackdrop).Compositor;
            _backdrop = new SampleBrush(compositor);
            _surface = new ThemeSurface(SurfaceHost);
            _edge = new ThemeEdgeSurface(EdgeHost);
            _surface.SetCornerRadius(8);
            _edge.SetCornerRadius(8);
            SampleLabel.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        }
        _backdrop.SetPlan(ThemePalette.BuildCompositionPlan(_theme, _effects));
        SampleBackdrop.Background = _backdrop;
        _surface!.SetTheme(_theme, _effects);
        _edge!.SetTheme(_theme, _effects);
        _edge.SetEnabled(_edgeGlow);
    }

    internal void Suspend()
    {
        if (!_active) return;
        _active = false;
        ReleaseMaterial();
    }

    private void ReleaseMaterial()
    {
        SampleBackdrop.Background = null;
        _backdrop?.Release();
        _backdrop = null;
        _surface?.Dispose();
        _surface = null;
        _edge?.Dispose();
        _edge = null;
    }

    // The settings window is opaque. Sample the miniature landscape directly behind
    // this brush, using the same tint/neutral/blur graph as the desktop window.
    private sealed class SampleBrush(Compositor compositor) : XamlCompositionBrushBase
    {
        private const float ThumbnailScale = .25f;
        private CompositionBackdropBrush? _sample;
        private CompositionEffectBrush? _glass;
        private CompositionColorBrush? _flat;
        private ThemeCompositionPlan _plan;
        private bool _connected;
        private bool _glassUnavailable;

        internal void SetPlan(ThemeCompositionPlan plan)
        {
            if (_plan == plan) return;
            FallbackColor = ThemePalette.WithOpacity(plan.TintColor, plan.TintOpacity);
            _plan = plan;
            if (_connected) Apply();
        }

        protected override void OnConnected()
        {
            _connected = true;
            Apply();
        }

        protected override void OnDisconnected() => Release();

        private void Apply()
        {
            if (_plan.RequiresHostBackdrop && !_glassUnavailable)
            {
                try
                {
                    if (_glass is null)
                    {
                        var source = new CompositionEffectSourceParameter("Backdrop");
                        var graph = ThemeEffectGraph.Create(_plan, source);
                        using var factory = compositor.CreateEffectFactory(graph,
                            new[] { "Blur.BlurAmount", "TintOpacity.Opacity", "TintColor.Color" });
                        _sample = compositor.CreateBackdropBrush();
                        _glass = factory.CreateBrush();
                        _glass.SetSourceParameter("Backdrop", _sample);
                    }
                    _glass.Properties.InsertScalar("Blur.BlurAmount", _plan.BlurAmount * ThumbnailScale);
                    _glass.Properties.InsertScalar("TintOpacity.Opacity", _plan.TintOpacity);
                    _glass.Properties.InsertColor("TintColor.Color", _plan.TintColor);
                    CompositionBrush = _glass;
                    return;
                }
                catch (Exception ex)
                {
                    _glassUnavailable = true;
                    AppLogger.Error("主题小样模糊不可用，改用主题色。", ex);
                }
            }
            _flat ??= compositor.CreateColorBrush();
            _flat.Color = ThemePalette.WithOpacity(_plan.TintColor, _plan.TintOpacity);
            CompositionBrush = _flat;
        }

        internal void Release()
        {
            _connected = false;
            CompositionBrush = null;
            _glass?.Dispose();
            _glass = null;
            _sample?.Dispose();
            _sample = null;
            _flat?.Dispose();
            _flat = null;
            _glassUnavailable = false;
        }
    }
}
