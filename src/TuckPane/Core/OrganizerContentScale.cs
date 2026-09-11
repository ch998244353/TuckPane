namespace TuckPane.Core;

internal static class OrganizerContentScale
{
    internal static double Normalize(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, .1, 4) : 1;

    // Bounds and content share one factor; a constrained axis must never scale independently.
    internal static (double Min, double Max) FactorRange(double width, double height,
        double minimumWidth, double minimumHeight, double maximumWidth, double maximumHeight,
        double contentScale, double itemScale)
    {
        double min = Math.Max(Math.Max(minimumWidth / width, minimumHeight / height), .5 / (contentScale * itemScale));
        double max = Math.Min(Math.Min(maximumWidth / width, maximumHeight / height), 1.65 / (contentScale * itemScale));
        // Existing windows may already be constrained by a monitor/configuration change.
        return (Math.Min(1, Math.Max(min, .1 / contentScale)), Math.Max(1, Math.Min(max, 4 / contentScale)));
    }

    internal static double DisplayFit(double width, double height, double availableWidth, double availableHeight) =>
        Math.Min(1, Math.Min(availableWidth / width, availableHeight / height));

    // Scale the whole left gutter to 2/3, then subtract the unchanged row padding.
    internal static double CompactLeftInset(double sideInset, double itemScale) =>
        (sideInset * 2 + 8 * itemScale) * (2d / 3) - 4 * itemScale;
}
