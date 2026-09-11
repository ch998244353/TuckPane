namespace TuckPane.Core;

internal static class CanvasResizeHitZones
{
    internal const double BorderDip = 28;
    internal static double LeftBorderDip(bool compactList) => compactList ? 6 : BorderDip;

    internal static CanvasResizeEdge HitTest(double x, double y, double width, double height, bool compactList)
    {
        if (x < 0 || y < 0 || x > width || y > height) return CanvasResizeEdge.None;
        CanvasResizeEdge edge = CanvasResizeEdge.None;
        if (x <= LeftBorderDip(compactList)) edge |= CanvasResizeEdge.Left;
        else if (x >= width - BorderDip) edge |= CanvasResizeEdge.Right;
        if (y <= BorderDip) edge |= CanvasResizeEdge.Top;
        else if (y >= height - BorderDip) edge |= CanvasResizeEdge.Bottom;
        return edge;
    }

    // These native child windows intercept input before XAML; their left width must
    // match the logical hit zone or the freed compact-list gutter still cannot drag.
    internal static (int X, int Y, int Width, int Height)[] EdgeRectangles(
        int width, int height, double scale, bool compactList)
    {
        int band = Math.Max(1, (int)Math.Ceiling(BorderDip * scale));
        int leftBand = Math.Max(1, (int)Math.Ceiling(LeftBorderDip(compactList) * scale));
        return
        [
            (0, 0, width, Math.Min(band, height)),
            (0, Math.Max(0, height - band), width, Math.Min(band, height)),
            (0, band, Math.Min(leftBand, width), Math.Max(1, height - 2 * band)),
            (Math.Max(0, width - band), band, Math.Min(band, width), Math.Max(1, height - 2 * band))
        ];
    }
}
