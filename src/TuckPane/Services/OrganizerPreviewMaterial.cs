using TuckPane.Models;
using Windows.UI;

namespace TuckPane.Services;

internal enum ThemeBackdropMaterial
{
    Theme,
    OrganizerPreview
}

// A local preview material, never persisted into the user's window themes.
internal static class OrganizerPreviewMaterial
{
    internal static ThemeValues Theme { get; } = new(0xFFFFFFFF, 0, .05);
    internal const double EdgeThickness = 1;

    internal static ThemeCompositionPlan BuildPlan(bool useEffects) =>
        ThemePalette.BuildCompositionPlan(Theme, useEffects) with
        {
            LuminosityOpacity = 0,
            HighlightOpacity = 0
        };

    internal static IReadOnlyList<(double Offset, Color Color)> EdgeStops { get; } =
    [
        (0, Color.FromArgb(56, 255, 255, 255)),
        (.28, Color.FromArgb(32, 255, 255, 255)),
        (.55, Color.FromArgb(8, 255, 255, 255)),
        (1, Color.FromArgb(26, 0, 0, 0))
    ];
}
