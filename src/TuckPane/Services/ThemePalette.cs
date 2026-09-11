using Microsoft.UI;
using TuckPane.Models;
using Windows.UI;

namespace TuckPane.Services;

internal readonly record struct ThemeEffectParameters(
    float BlurAmount,
    float Saturation,
    float LuminosityOpacity);

/// <summary>
/// Pure-data description of the single Glass composition pipeline. Keeping
/// this separate from Composition objects makes transparency, blur and
/// highlight semantics testable without creating a window or GPU graph.
/// </summary>
internal readonly record struct ThemeCompositionPlan(
    // The sampled Glass result is already composited. Only colour-only
    // branches have a translucent output; TintOpacity is the user's weight.
    float SurfaceOpacity,
    float TintOpacity,
    float DesktopOpacity,
    float BlurAmount,
    bool UsesGaussianBlur,
    bool RequiresHostBackdrop,
    float Saturation,
    float LuminosityOpacity,
    float HighlightOpacity,
    Color TintColor,
    Color LuminosityColor,
    bool UseEffects);

internal static class ThemePalette
{
    internal const float OrganizerGlassOuterEdgeThicknessDip = 1.25f;
    internal const float OrganizerGlassInnerEdgeThicknessDip = .75f;
    internal const float OrganizerGlassInnerEdgeInsetDip = 1.5f;
    internal const float GlassEdgeHighlightThicknessDip = 2.75f;
    internal const float GlassEdgeTextureThicknessDip = 1.25f;
    internal const float GlassEdgeMinimumOpacity = .92f;

    private const float GlassBlurAmount = 40;
    private const float GlassSaturation = 1;
    private const float GlassLuminosityOpacity = .35f;

    internal static Color SurfaceColor(ThemeValues theme) => FromArgb(theme.ColorArgb);

    internal static Color TintColor(ThemeValues theme) => SurfaceColor(theme);

    internal static Color ForegroundColor(ThemeValues theme)
    {
        Color background = SurfaceColor(theme);
        double luminance = RelativeLuminance(background);
        double blackContrast = (luminance + .05) / .05;
        double whiteContrast = 1.05 / (luminance + .05);
        return blackContrast >= whiteContrast
            ? ColorHelper.FromArgb(255, 31, 31, 31)
            : ColorHelper.FromArgb(255, 245, 245, 245);
    }

    internal static Color ResolveOrganizerTextColor(OrganizerTextColor mode, ThemeValues theme) =>
        Colors.White;

    internal static bool IsDark(ThemeValues theme) => ForegroundColor(theme).R > 128;

    /// <summary>
    /// The persisted field keeps its legacy name for JSON compatibility, but
    /// its value is the user-facing background opacity: 0 is transparent and
    /// 1 is a fully opaque theme surface.
    /// </summary>
    internal static float TintOpacity(ThemeValues theme) =>
        (float)(theme.SolidColorMode
            ? GlobalSettings.NormalizeSolidThemeOpacity(theme.SolidOpacity)
            : GlobalSettings.NormalizeThemeTransparency(theme.Transparency));

    internal static float DesktopOpacity(ThemeValues theme) =>
        theme.SolidColorMode ? 0f : 1f - TintOpacity(theme);

    internal static float OpticalProgress(ThemeValues theme) =>
        Math.Clamp(
            (float)GlobalSettings.NormalizeThemeBlurStrength(theme.BlurStrength),
            0,
            1);

    internal static float HighlightOpacity(ThemeValues theme) =>
        theme.SolidColorMode ? 0f : 4f * TintOpacity(theme) * (1f - TintOpacity(theme));

    internal static Color LuminosityColor(ThemeValues theme) =>
        ColorHelper.FromArgb(255, 128, 128, 128);

    internal static ThemeEffectParameters Effect() =>
        new(GlassBlurAmount, GlassSaturation, GlassLuminosityOpacity);

    internal static float EffectiveBlurAmount(ThemeValues theme) =>
        Effect().BlurAmount *
        (float)GlobalSettings.NormalizeThemeBlurStrength(theme.BlurStrength);

    internal static ThemeCompositionPlan BuildCompositionPlan(
        ThemeValues theme,
        bool useEffects)
    {
        if (theme.FullyTransparent)
            return new ThemeCompositionPlan(0, 0, 1, 0, false, false, 1, 0, 0,
                TintColor(theme), LuminosityColor(theme), useEffects);
        ThemeEffectParameters parameters = Effect();
        float tintOpacity = TintOpacity(theme);
        float desktopOpacity = DesktopOpacity(theme);
        float blurStrength = (float)GlobalSettings.NormalizeThemeBlurStrength(theme.BlurStrength);
        // Zero tint is still frosted when blur is positive. Fully opaque tint
        // and clear/solid paths need no sampled background.
        bool requiresHostBackdrop = useEffects && !theme.SolidColorMode && tintOpacity < 1 && blurStrength > 0;
        float blurAmount = requiresHostBackdrop ? parameters.BlurAmount * blurStrength : 0;
        return new ThemeCompositionPlan(
            SurfaceOpacity: requiresHostBackdrop ? 1 : tintOpacity,
            TintOpacity: tintOpacity,
            DesktopOpacity: desktopOpacity,
            BlurAmount: blurAmount,
            UsesGaussianBlur: requiresHostBackdrop,
            RequiresHostBackdrop: requiresHostBackdrop,
            Saturation: parameters.Saturation,
            LuminosityOpacity: requiresHostBackdrop ? parameters.LuminosityOpacity : 0,
            HighlightOpacity: useEffects ? HighlightOpacity(theme) : 0,
            TintColor: TintColor(theme),
            LuminosityColor: LuminosityColor(theme),
            UseEffects: useEffects);
    }

    internal static Color WithOpacity(Color color, float opacity) => ColorHelper.FromArgb(
        (byte)Math.Clamp(Math.Round(color.A * Math.Clamp(opacity, 0, 1)), 0, 255),
        color.R,
        color.G,
        color.B);

    internal static float EdgeOpacity(ThemeValues theme) => theme.FullyTransparent ? 0 : GlassEdgeMinimumOpacity;

    internal static IReadOnlyList<(float Offset, Color Color)> GlassHighlightStops { get; } =
    [
        (0f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (.38f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (.49f, ColorHelper.FromArgb(42, 255, 255, 255)),
        (.58f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (1f, ColorHelper.FromArgb(0, 255, 255, 255))
    ];

    // A restrained arc-like sheen used by local glass surfaces.  The broad
    // low-alpha band keeps the effect legible on both light and dark themes
    // without introducing another backdrop or bitmap resource.
    internal static IReadOnlyList<(float Offset, Color Color)> GlassArcStops { get; } =
    [
        (0f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (.16f, ColorHelper.FromArgb(18, 255, 255, 255)),
        (.28f, ColorHelper.FromArgb(34, 255, 255, 255)),
        (.4f, ColorHelper.FromArgb(10, 255, 255, 255)),
        (.62f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (1f, ColorHelper.FromArgb(0, 0, 0, 0))
    ];

    // Deterministic pseudo-noise represented as many tiny gradient bands. It
    // is static, generated from code, and deliberately kept below perceptual
    // threshold so text and controls remain crisp.
    internal static IReadOnlyList<(float Offset, Color Color)> GlassTextureStops { get; } =
    [
        (0f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (.07f, ColorHelper.FromArgb(5, 255, 255, 255)),
        (.13f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (.21f, ColorHelper.FromArgb(4, 255, 255, 255)),
        (.29f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (.37f, ColorHelper.FromArgb(5, 255, 255, 255)),
        (.44f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (.53f, ColorHelper.FromArgb(4, 0, 0, 0)),
        (.61f, ColorHelper.FromArgb(0, 0, 0, 0)),
        (.69f, ColorHelper.FromArgb(5, 255, 255, 255)),
        (.77f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (.86f, ColorHelper.FromArgb(4, 0, 0, 0)),
        (.94f, ColorHelper.FromArgb(0, 0, 0, 0)),
        (1f, ColorHelper.FromArgb(0, 0, 0, 0))
    ];

    internal static IReadOnlyList<(float Offset, Color Color)> OrganizerGlassOuterEdgeStops { get; } =
    [
        (0f, ColorHelper.FromArgb(112, 255, 255, 255)),
        (.55f, ColorHelper.FromArgb(16, 255, 255, 255)),
        (1f, ColorHelper.FromArgb(44, 0, 0, 0))
    ];

    internal static IReadOnlyList<(float Offset, Color Color)> OrganizerGlassInnerEdgeStops { get; } =
    [
        (0f, ColorHelper.FromArgb(64, 255, 255, 255)),
        (.45f, ColorHelper.FromArgb(8, 255, 255, 255)),
        (1f, ColorHelper.FromArgb(32, 0, 0, 0))
    ];

    internal static IReadOnlyList<(float Offset, Color Color)> GlassEdgeHighlightStops { get; } =
    [
        (0f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (.18f, ColorHelper.FromArgb(28, 255, 255, 255)),
        (.38f, ColorHelper.FromArgb(42, 255, 255, 255)),
        (.56f, ColorHelper.FromArgb(12, 255, 255, 255)),
        (.82f, ColorHelper.FromArgb(4, 255, 255, 255)),
        (1f, ColorHelper.FromArgb(0, 255, 255, 255))
    ];

    internal static IReadOnlyList<(float Offset, Color Color)> GlassEdgeTextureStops { get; } =
    [
        (0f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (.08f, ColorHelper.FromArgb(6, 255, 255, 255)),
        (.16f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (.27f, ColorHelper.FromArgb(5, 255, 255, 255)),
        (.39f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (.52f, ColorHelper.FromArgb(4, 255, 255, 255)),
        (.65f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (.78f, ColorHelper.FromArgb(5, 255, 255, 255)),
        (.91f, ColorHelper.FromArgb(0, 255, 255, 255)),
        (1f, ColorHelper.FromArgb(0, 255, 255, 255))
    ];

    internal static Color LayerColor(ThemeValues theme, byte lightAlpha, byte darkAlpha)
    {
        // Surface layers are derived from the selected theme colour instead
        // of fixed white/black overlays so the user's hue stays visible.
        bool dark = IsDark(theme);
        byte layerAlpha = dark ? darkAlpha : lightAlpha;
        float neutralMix = Math.Clamp(layerAlpha / 255f * .75f, .04f, .28f);
        Color selected = SurfaceColor(theme);
        byte target = dark ? (byte)255 : (byte)0;
        return ColorHelper.FromArgb(
            layerAlpha,
            BlendChannel(selected.R, target, neutralMix),
            BlendChannel(selected.G, target, neutralMix),
            BlendChannel(selected.B, target, neutralMix));
    }

    private static Color FromArgb(uint argb) => ColorHelper.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    private static byte BlendChannel(byte value, byte target, float amount) =>
        (byte)Math.Clamp(Math.Round(value + (target - value) * amount), 0, 255);

    private static double RelativeLuminance(Color color) =>
        .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);

    private static double Linear(byte channel)
    {
        double value = channel / 255d;
        return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    }
}
