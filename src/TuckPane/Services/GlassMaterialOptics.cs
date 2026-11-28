using System.Numerics;
using TuckPane.Models;
using Windows.UI;

namespace TuckPane.Services;

internal readonly record struct GlassFinishPlan(
    bool ShowFinish,
    float ColorOpacity,
    float Reflection,
    float GrainOpacity,
    Vector2 LightOrigin);

internal static class GlassMaterialOptics
{
    internal static GlassFinishPlan Resolve(ThemeValues theme, bool useEffects)
    {
        if (!useEffects || theme.SolidColorMode || theme.FullyTransparent)
            return default;

        var material = theme.Material;
        float color = material.Kind switch
        {
            // Pearl concentrates its strongest colour on the rim.
            GlassMaterialKind.Mist => .8f * (float)material.ColorIntensity,
            GlassMaterialKind.Pearl => .32f * (float)material.ColorIntensity,
            _ => 0
        };
        float reflection = (float)material.Reflection;
        if (material.Kind == GlassMaterialKind.Classic)
            reflection *= ThemePalette.HighlightOpacity(theme);
        return new(true, color, reflection, (float)material.Grain * .52f, LightOrigin(material.LightDirection));
    }

    internal static Vector2 LightOrigin(GlassLightDirection direction) => direction switch
    {
        GlassLightDirection.TopRight => Vector2.UnitX,
        GlassLightDirection.BottomLeft => Vector2.UnitY,
        GlassLightDirection.BottomRight => Vector2.One,
        _ => Vector2.Zero
    };

    internal static Color Rgb(uint argb) =>
        Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    // Pearl keeps a white glint at the light source; the two hues gradually
    // emerge on its shoulders, becoming neutral when colour intensity is zero.
    internal static Color PearlStop(uint color, double colorIntensity, byte alpha)
    {
        Color tint = Rgb(color);
        byte Mix(byte channel) => (byte)Math.Round(255 + (channel - 255) * colorIntensity);
        return Color.FromArgb(alpha, Mix(tint.R), Mix(tint.G), Mix(tint.B));
    }
}
