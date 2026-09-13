using TuckPane.Services;

namespace TuckPane.Core;

internal readonly record struct CompactResizeResult(NativeMethods.RECT Bounds, double Scale, double MinimumScale, double MaximumScale);

// Coordinates describe the content panel; the independently sized title is handled by the caller.
internal static class CompactResizeMath
{
    internal static bool IsCorner(CanvasResizeEdge edges) =>
        (edges & (CanvasResizeEdge.Left | CanvasResizeEdge.Right)) != 0 &&
        (edges & (CanvasResizeEdge.Top | CanvasResizeEdge.Bottom)) != 0;

    internal static NativeMethods.RECT FromScale(NativeMethods.RECT start, CanvasResizeEdge edges, double scale)
    {
        int width = Math.Max(1, (int)Math.Round(start.Width * scale));
        int height = Math.Max(1, (int)Math.Round(start.Height * scale));
        int left = edges.HasFlag(CanvasResizeEdge.Left) ? start.Right - width : start.Left;
        int top = edges.HasFlag(CanvasResizeEdge.Top) ? start.Bottom - height : start.Top;
        return new() { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    internal static CompactResizeResult Resize(NativeMethods.RECT start, NativeMethods.RECT work,
        CanvasResizeEdge edges, double dx, double dy, int minimumWidth, int minimumHeight,
        double contentScale, double itemScale)
    {
        double maxWidth = Math.Max(1, edges.HasFlag(CanvasResizeEdge.Left)
            ? start.Right - work.Left : work.Right - start.Left);
        double maxHeight = Math.Max(1, edges.HasFlag(CanvasResizeEdge.Top)
            ? start.Bottom - work.Top : work.Bottom - start.Top);
        if (!IsCorner(edges))
        {
            var bounds = start;
            if ((edges & (CanvasResizeEdge.Left | CanvasResizeEdge.Right)) != 0)
            {
                int width = (int)Math.Round(Math.Clamp(start.Width + (edges.HasFlag(CanvasResizeEdge.Left) ? -dx : dx),
                    Math.Min(Math.Min(start.Width, minimumWidth), maxWidth), maxWidth));
                if (edges.HasFlag(CanvasResizeEdge.Left)) bounds.Left = start.Right - width;
                else bounds.Right = start.Left + width;
            }
            else if ((edges & (CanvasResizeEdge.Top | CanvasResizeEdge.Bottom)) != 0)
            {
                int height = (int)Math.Round(Math.Clamp(start.Height + (edges.HasFlag(CanvasResizeEdge.Top) ? -dy : dy),
                    Math.Min(Math.Min(start.Height, minimumHeight), maxHeight), maxHeight));
                if (edges.HasFlag(CanvasResizeEdge.Top)) bounds.Top = start.Bottom - height;
                else bounds.Bottom = start.Top + height;
            }
            return new(bounds, 1, 1, 1);
        }

        double vx = edges.HasFlag(CanvasResizeEdge.Left) ? -start.Width : start.Width;
        double vy = edges.HasFlag(CanvasResizeEdge.Top) ? -start.Height : start.Height;
        double proposed = 1 + (dx * vx + dy * vy) / (vx * vx + vy * vy);
        var range = OrganizerContentScale.FactorRange(start.Width, start.Height,
            minimumWidth, minimumHeight, maxWidth, maxHeight, contentScale, itemScale);
        double maximum = Math.Min(range.Max, Math.Min(maxWidth / start.Width, maxHeight / start.Height));
        double minimum = Math.Min(range.Min, maximum);
        double scale = Math.Clamp(proposed, minimum, maximum);
        return new(FromScale(start, edges, scale), scale, minimum, maximum);
    }
}
