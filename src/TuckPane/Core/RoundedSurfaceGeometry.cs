namespace TuckPane.Core;

// Use the arranged XAML bounds for every layer. Rounding each layer's size
// independently can move its edge away from the SystemBackdropElement.
internal readonly record struct RoundedSurfaceGeometry(float Width, float Height, float Radius)
{
    internal static RoundedSurfaceGeometry Create(double width, double height, double radius, double rasterizationScale)
    {
        float w = Dimension(width);
        float h = Dimension(height);
        double scale = double.IsFinite(rasterizationScale) && rasterizationScale > 0 ? rasterizationScale : 1;
        double alignedRadius = double.IsFinite(radius) && radius > 0 ? Math.Round(radius * scale) / scale : 0;
        return new(w, h, (float)Math.Min(alignedRadius, Math.Min(w, h) / 2));
    }

    internal RoundedStrokeGeometry InsetStroke(float thickness, float edgeInset = 0)
    {
        // A stroke straddles its path. Keep its outside on the requested
        // outline, including fractional/half-pixel centres at any DPI.
        float inset = edgeInset + thickness / 2;
        return new(inset, Math.Max(0, Width - 2 * inset), Math.Max(0, Height - 2 * inset), Math.Max(0, Radius - inset));
    }

    private static float Dimension(double value) => double.IsFinite(value) && value > 0 ? (float)value : 0;
}

internal readonly record struct RoundedStrokeGeometry(float Offset, float Width, float Height, float Radius);
