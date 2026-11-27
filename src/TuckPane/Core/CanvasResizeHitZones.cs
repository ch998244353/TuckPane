namespace TuckPane.Core;

internal static class CanvasResizeHitZones
{
    internal const double BorderDip = 6;
    internal const double CornerDip = 28;

    internal static CanvasResizeEdge HitTest(double x, double y, double width, double height, bool compactList)
        => HitTestCore(x, y, width, height, BorderDip, CornerDip);

    internal static CanvasResizeEdge HitTest(CanvasResizeFrame frame, double clientX, double clientY)
        => !frame.IsValid ? CanvasResizeEdge.None : HitTestCore(
            clientX - frame.X, clientY - frame.Y, frame.Width, frame.Height,
            Math.Ceiling(BorderDip * frame.Scale), CornerDip * frame.Scale);

    private static CanvasResizeEdge HitTestCore(double x, double y, double width, double height,
        double band, double corner)
    {
        // Half-open pixel bounds match HWND rectangles. Round the band once, in
        // physical pixels, so fractional DPI cannot leave a logical/input gap.
        if (x < 0 || y < 0 || x >= width || y >= height) return CanvasResizeEdge.None;
        if (x >= band && x < width - band && y >= band && y < height - band)
            return CanvasResizeEdge.None;
        CanvasResizeEdge edge = CanvasResizeEdge.None;
        if (x <= corner) edge |= CanvasResizeEdge.Left;
        else if (x >= width - corner) edge |= CanvasResizeEdge.Right;
        if (y <= corner) edge |= CanvasResizeEdge.Top;
        else if (y >= height - corner) edge |= CanvasResizeEdge.Bottom;
        return edge;
    }

    internal static (int X, int Y, int Width, int Height)[] EdgeRectangles(
        int width, int height, double scale, bool compactList)
        => EdgeRectangles(new CanvasResizeFrame(0, 0, width, height, scale));

    internal static (int X, int Y, int Width, int Height)[] EdgeRectangles(CanvasResizeFrame frame)
    {
        if (!frame.IsValid) return [];
        int band = (int)Math.Ceiling(BorderDip * frame.Scale);
        int width = frame.Width, height = frame.Height;
        int top = Math.Min(band, height), bottom = Math.Max(top, height - band);
        int side = Math.Min(band, width);
        return
        [
            (frame.X, frame.Y, width, top),
            (frame.X, frame.Y + bottom, width, height - bottom),
            (frame.X, frame.Y + top, side, bottom - top),
            (frame.X + width - side, frame.Y + top, side, bottom - top)
        ];
    }
}

internal readonly record struct CanvasResizeFrame(int X, int Y, int Width, int Height, double Scale)
{
    internal bool IsValid => Width > 0 && Height > 0 && double.IsFinite(Scale) && Scale >= 1;
}
