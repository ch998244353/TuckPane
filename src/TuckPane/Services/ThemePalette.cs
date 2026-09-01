using Microsoft.UI;
using TuckPane.Models;
using Windows.UI;

namespace TuckPane.Services;

internal readonly record struct ThemeEffectParameters(
    float BlurAmount,
    float Saturation,
    float TintOpacityScale,
    float NoiseOpacity,
    float LuminosityOpacity);

internal static class ThemePalette
{
    internal const float OuterEdgeThickness = 1.25f;
    internal const float InnerEdgeThickness = .75f;
    internal const float InnerEdgeInset = 1.5f;

    internal static Color SurfaceColor(ThemeValues theme) => FromArgb(theme.ColorArgb);

    internal static Color MaterialTintColor(ThemeValues theme, ThemeMaterial material)
    {
        Color selected = SurfaceColor(theme);
        if (material != ThemeMaterial.Acrylic) return selected;
        return ColorHelper.FromArgb(
            255,
            Mix(255, selected.R, .35f),
            Mix(255, selected.G, .35f),
            Mix(255, selected.B, .35f));
    }

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

    internal static bool IsDark(ThemeValues theme) => ForegroundColor(theme).R > 128;

    internal static float TintOpacity(ThemeValues theme) =>
        (float)(1 - GlobalSettings.NormalizeThemeTransparency(theme.Transparency));

    internal static Color LuminosityColor(ThemeValues theme) =>
        IsDark(theme) ? Colors.Black : Colors.White;

    internal static ThemeEffectParameters Effect(ThemeMaterial material) => material switch
    {
        ThemeMaterial.Glass => new(10, 2f, .90f, 0, .06f),
        ThemeMaterial.Matte => new(18, .75f, .92f, .035f, .50f),
        _ => new(30, 1f, .80f, 0, .90f)
    };

    internal static float EffectiveTintOpacity(ThemeValues theme, ThemeMaterial material)
    {
        float opacity = TintOpacity(theme);
        return opacity >= 1 ? 1 : opacity * Effect(material).TintOpacityScale;
    }

    internal static IReadOnlyList<(float Offset, Color Color)> GlassEdgeStops { get; } =
    [
        (0f, ColorHelper.FromArgb(150, 255, 255, 255)),
        (.18f, ColorHelper.FromArgb(82, 255, 59, 48)),
        (.34f, ColorHelper.FromArgb(76, 255, 149, 0)),
        (.50f, ColorHelper.FromArgb(72, 52, 199, 89)),
        (.67f, ColorHelper.FromArgb(76, 0, 122, 255)),
        (.84f, ColorHelper.FromArgb(82, 175, 82, 222)),
        (1f, ColorHelper.FromArgb(96, 16, 19, 21))
    ];

    internal static IReadOnlyList<(float Offset, Color Color)> OuterEdgeStops(ThemeMaterial material) => material switch
    {
        ThemeMaterial.Glass => GlassEdgeStops,
        ThemeMaterial.Matte => MatteOuterEdgeStops,
        _ => AcrylicOuterEdgeStops
    };

    internal static IReadOnlyList<(float Offset, Color Color)> InnerEdgeStops(ThemeMaterial material) => material switch
    {
        ThemeMaterial.Glass => GlassInnerEdgeStops,
        ThemeMaterial.Matte => MatteInnerEdgeStops,
        _ => AcrylicInnerEdgeStops
    };

    internal static bool HasPrismaticEdge(ThemeMaterial material) => material == ThemeMaterial.Glass;

    internal static Color LayerColor(ThemeValues theme, byte lightAlpha, byte darkAlpha) =>
        IsDark(theme)
            ? ColorHelper.FromArgb(darkAlpha, 255, 255, 255)
            : ColorHelper.FromArgb(lightAlpha, 255, 255, 255);

    private static Color FromArgb(uint argb) => ColorHelper.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    private static byte Mix(byte from, byte to, float amount) =>
        (byte)Math.Clamp(Math.Round(from + (to - from) * amount), 0, 255);

    private static IReadOnlyList<(float Offset, Color Color)> AcrylicOuterEdgeStops { get; } =
    [
        (0f, ColorHelper.FromArgb(102, 255, 255, 255)),
        (.55f, ColorHelper.FromArgb(16, 255, 255, 255)),
        (1f, ColorHelper.FromArgb(36, 0, 0, 0))
    ];

    private static IReadOnlyList<(float Offset, Color Color)> MatteOuterEdgeStops { get; } =
    [
        (0f, ColorHelper.FromArgb(68, 255, 255, 255)),
        (.55f, ColorHelper.FromArgb(13, 255, 255, 255)),
        (1f, ColorHelper.FromArgb(32, 0, 0, 0))
    ];

    private static IReadOnlyList<(float Offset, Color Color)> AcrylicInnerEdgeStops { get; } =
    [
        (0f, ColorHelper.FromArgb(56, 255, 255, 255)),
        (.45f, ColorHelper.FromArgb(8, 255, 255, 255)),
        (1f, ColorHelper.FromArgb(28, 0, 0, 0))
    ];

    private static IReadOnlyList<(float Offset, Color Color)> GlassInnerEdgeStops { get; } =
    [
        (0f, ColorHelper.FromArgb(48, 255, 255, 255)),
        (.45f, ColorHelper.FromArgb(8, 255, 255, 255)),
        (1f, ColorHelper.FromArgb(32, 0, 0, 0))
    ];

    private static IReadOnlyList<(float Offset, Color Color)> MatteInnerEdgeStops { get; } =
    [
        (0f, ColorHelper.FromArgb(40, 255, 255, 255)),
        (.45f, ColorHelper.FromArgb(8, 255, 255, 255)),
        (1f, ColorHelper.FromArgb(24, 0, 0, 0))
    ];

    private static double RelativeLuminance(Color color) =>
        .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);

    private static double Linear(byte channel)
    {
        double value = channel / 255d;
        return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    }
}
