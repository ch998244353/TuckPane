using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using TuckPane.Core;
using TuckPane.Models;
using Windows.UI;

namespace TuckPane.Services;

/// <summary>Static optical layers between the background and content. The same renderer is used by the settings sample.</summary>
internal sealed class ThemeSurface : IDisposable
{
    private readonly FrameworkElement _host;
    private readonly FrameworkElement? _classicHost;
    private FrameworkElement? _attachedHost;
    private readonly FrameworkElement _geometryHost;
    private readonly ContainerVisual _root;
    private readonly SpriteVisual _highlightVisual;
    private readonly RectangleClip _clip;
    private CompositionLinearGradientBrush? _highlightBrush;
    private GlassMaterialKind? _highlightKind;
    private GlassMaterialSettings? _lastMaterial;
    private GlassFinishPlan? _lastFinish;
    private SpriteVisual? _cornerA;
    private SpriteVisual? _cornerB;
    private SpriteVisual? _grainVisual;
    private GlassGrainTile? _grainTile;
    private XamlRoot? _xamlRoot;
    private double _cornerRadius;
    private bool _disposed;

    internal ThemeSurface(FrameworkElement emptyBackgroundHost, FrameworkElement? geometryHost = null,
        FrameworkElement? classicHost = null)
    {
        _host = emptyBackgroundHost;
        _classicHost = classicHost;
        _geometryHost = geometryHost ?? emptyBackgroundHost;
        Compositor compositor = ElementCompositionPreview.GetElementVisual(emptyBackgroundHost).Compositor;
        _root = compositor.CreateContainerVisual();
        _root.BorderMode = CompositionBorderMode.Soft;
        _clip = compositor.CreateRectangleClip();
        _root.Clip = _clip;
        _highlightVisual = NewLayer();

        _geometryHost.Loaded += Host_Loaded;
        _geometryHost.SizeChanged += Host_SizeChanged;
        AttachXamlRoot(_geometryHost.XamlRoot);
        UpdateGeometry();
    }

    // The original desktop finish was drawn on the SystemBackdropElement.
    // Keep that exact stacking order until a different finish is requested.
    // Preview cards and dialogs each have just one host.
    private void SelectHost(GlassMaterialSettings material)
    {
        FrameworkElement next = material == GlassMaterialSettings.Classic ? _classicHost ?? _host : _host;
        if (ReferenceEquals(_attachedHost, next)) return;
        DetachVisual();
        // Once connected, WinUI can own the backdrop element's child visual.
        // Preserve its ownership when switching back to the legacy finish.
        if (ReferenceEquals(next, _classicHost) && ElementCompositionPreview.GetElementChildVisual(next) is not null)
            return;
        ElementCompositionPreview.SetElementChildVisual(next, _root);
        _attachedHost = next;
    }

    private void DetachVisual()
    {
        if (_attachedHost is not null &&
            ReferenceEquals(ElementCompositionPreview.GetElementChildVisual(_attachedHost), _root))
            ElementCompositionPreview.SetElementChildVisual(_attachedHost, null);
        _attachedHost = null;
    }

    private SpriteVisual NewLayer(bool aboveCorners = false)
    {
        SpriteVisual visual = _root.Compositor.CreateSpriteVisual();
        visual.BorderMode = CompositionBorderMode.Soft;
        visual.RelativeSizeAdjustment = Vector2.One;
        if (aboveCorners) _root.Children.InsertBelow(visual, _highlightVisual);
        else _root.Children.InsertAtBottom(visual);
        return visual;
    }

    internal void SetTheme(ThemeValues theme, bool useEffects)
    {
        if (_disposed) return;
        var finish = GlassMaterialOptics.Resolve(theme, useEffects);
        var material = theme.Material;
        if (_lastFinish == finish && _lastMaterial == material) return;
        // Keep a failed attempt retryable, even when returning to the last good settings.
        _lastFinish = null;
        SelectHost(material);
        _root.IsVisible = finish.ShowFinish;
        if (!finish.ShowFinish)
        {
            SetGrain(0);
            _lastFinish = finish;
            _lastMaterial = material;
            return;
        }

        try
        {
            SetHighlight(material.Kind, finish.LightOrigin, finish.Reflection);
            SetCorner(ref _cornerA, finish.LightOrigin, material.ColorAArgb, finish.ColorOpacity);
            SetCorner(ref _cornerB, Vector2.One - finish.LightOrigin, material.ColorBArgb, finish.ColorOpacity * .88f);
        }
        catch (Exception ex)
        {
            _root.IsVisible = false;
            SetGrain(0);
            AppLogger.Error("玻璃装饰不可用，保留主题背景。", ex);
            return;
        }

        try
        {
            SetGrain(finish.GrainOpacity);
            _lastFinish = finish;
            _lastMaterial = material;
        }
        catch (Exception ex)
        {
            SetGrain(0);
            AppLogger.Error("磨砂颗粒不可用，保留其余玻璃材质。", ex);
        }
    }

    private void SetCorner(ref SpriteVisual? visual, Vector2 origin, uint argb, float intensity)
    {
        if (intensity <= 0)
        {
            if (visual is not null) visual.IsVisible = false;
            return;
        }

        visual ??= NewLayer();
        Compositor compositor = _root.Compositor;
        if (visual.Brush is not CompositionRadialGradientBrush brush)
        {
            brush = compositor.CreateRadialGradientBrush();
            brush.EllipseRadius = new Vector2(.69f, .84f);
            foreach (float offset in new[] { 0f, .35f, .7f, 1f })
                brush.ColorStops.Add(compositor.CreateColorGradientStop(offset, default));
            visual.Brush = brush;
        }

        // A broad illuminated corner and a smooth tail leave the central labels clear.
        brush.EllipseCenter = Vector2.Lerp(origin, new Vector2(.5f), .13f);
        Color tint = GlassMaterialOptics.Rgb(argb);
        brush.ColorStops[0].Color = tint;
        brush.ColorStops[1].Color = ThemePalette.WithOpacity(tint, .72f);
        brush.ColorStops[2].Color = ThemePalette.WithOpacity(tint, .18f);
        brush.ColorStops[3].Color = ThemePalette.WithOpacity(tint, 0);
        visual.Opacity = intensity;
        visual.IsVisible = true;
    }

    private void SetHighlight(GlassMaterialKind kind, Vector2 origin, float opacity)
    {
        _highlightVisual.IsVisible = opacity > 0;
        if (opacity <= 0) return;

        if (_highlightBrush is null || _highlightKind != kind)
        {
            CompositionLinearGradientBrush next = CreateHighlightBrush(kind);
            _highlightVisual.Brush = next;
            _highlightBrush?.Dispose();
            _highlightBrush = next;
            _highlightKind = kind;
        }
        _highlightBrush.StartPoint = origin;
        _highlightBrush.EndPoint = Vector2.One - origin;
        _highlightVisual.Opacity = opacity;
    }

    private CompositionLinearGradientBrush CreateHighlightBrush(GlassMaterialKind kind)
    {
        IReadOnlyList<(float Offset, Color Color)> stops = kind switch
        {
            GlassMaterialKind.Classic => ThemePalette.GlassHighlightStops,
            GlassMaterialKind.Frosted => FrostedStops,
            GlassMaterialKind.Mist => ThemePalette.GlassArcStops,
            _ => PearlStops
        };
        Compositor compositor = _root.Compositor;
        var brush = compositor.CreateLinearGradientBrush();
        try
        {
            foreach (var (offset, color) in stops)
                brush.ColorStops.Add(compositor.CreateColorGradientStop(offset, color));
            return brush;
        }
        catch
        {
            brush.Dispose();
            throw;
        }
    }

    private static Color White(byte alpha) => Color.FromArgb(alpha, 255, 255, 255);
    private static readonly (float, Color)[] FrostedStops =
        [(0, White(75)), (.19f, White(42)), (.45f, White(9)), (.75f, White(0)), (1, White(0))];
    private static readonly (float, Color)[] PearlStops =
        [(0, White(90)), (.055f, White(63)), (.17f, White(15)), (.28f, White(0)), (1, White(0))];

    private void SetGrain(float amount)
    {
        if (amount <= 0)
        {
            if (_grainVisual is not null) _grainVisual.Brush = null;
            _grainTile?.Dispose();
            _grainTile = null;
            return;
        }
        if (_grainTile is null)
        {
            _grainVisual ??= NewLayer(aboveCorners: true);
            _grainTile = GlassGrainTile.Acquire(_root.Compositor);
            _grainVisual.Brush = _grainTile.Brush;
        }
        _grainVisual!.Opacity = amount;
    }

    internal void SetCornerRadius(double radius)
    {
        _cornerRadius = Math.Max(0, radius);
        UpdateGeometry();
    }

    internal void SetRoundedGeometry(RoundedSurfaceGeometry geometry)
    {
        _cornerRadius = geometry.Radius;
        ApplyGeometry(geometry);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _geometryHost.Loaded -= Host_Loaded;
        _geometryHost.SizeChanged -= Host_SizeChanged;
        AttachXamlRoot(null);
        DetachVisual();
        SetGrain(0);
        _root.Children.RemoveAll();
        _grainVisual?.Dispose();
        _cornerA?.Brush?.Dispose();
        _cornerB?.Brush?.Dispose();
        _cornerA?.Dispose();
        _cornerB?.Dispose();
        _highlightBrush?.Dispose();
        _highlightVisual.Dispose();
        _clip.Dispose();
        _root.Dispose();
    }

    private void Host_Loaded(object sender, RoutedEventArgs e)
    {
        AttachXamlRoot(_geometryHost.XamlRoot);
        UpdateGeometry();
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateGeometry();

    private void AttachXamlRoot(XamlRoot? next)
    {
        if (ReferenceEquals(_xamlRoot, next)) return;
        if (_xamlRoot is not null) _xamlRoot.Changed -= XamlRoot_Changed;
        _xamlRoot = next;
        if (_xamlRoot is not null) _xamlRoot.Changed += XamlRoot_Changed;
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateGeometry();

    private void UpdateGeometry() =>
        ApplyGeometry(RoundedSurfaceGeometry.Create(_geometryHost.ActualWidth, _geometryHost.ActualHeight,
            _cornerRadius, _geometryHost.XamlRoot?.RasterizationScale ?? 1));

    private void ApplyGeometry(RoundedSurfaceGeometry surface)
    {
        if (_disposed) return;
        _root.Size = new Vector2(surface.Width, surface.Height);
        _clip.Right = surface.Width;
        _clip.Bottom = surface.Height;
        Vector2 corner = new(surface.Radius);
        _clip.TopLeftRadius = corner;
        _clip.TopRightRadius = corner;
        _clip.BottomLeftRadius = corner;
        _clip.BottomRightRadius = corner;
    }
}
