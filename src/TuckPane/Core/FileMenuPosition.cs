using Windows.Foundation;

namespace TuckPane.Core;

internal readonly record struct FileMenuPopup(Point Position, Rect? Exclusion, uint PositionFlags);

internal static class FileMenuPosition
{
    internal static FileMenuPopup Pointer(Point position, Rect cursorBounds, Rect workArea, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0 || workArea.Width < 1 || workArea.Height < 1)
            throw new ArgumentOutOfRangeException(nameof(scale));
        double gap = Math.Ceiling(4 * scale);
        var exclusion = new Rect(Math.Floor(cursorBounds.Left - gap), Math.Floor(cursorBounds.Top - gap),
            Math.Ceiling(cursorBounds.Right + gap) - Math.Floor(cursorBounds.Left - gap),
            Math.Ceiling(cursorBounds.Bottom + gap) - Math.Floor(cursorBounds.Top - gap));
        // Keep the anchor on the click's monitor even when the cursor straddles an edge.
        var anchor = new Point(Math.Clamp(exclusion.Right, workArea.Left, workArea.Right - 1),
            Math.Clamp(exclusion.Bottom, workArea.Top, workArea.Bottom - 1));
        // LEFTALIGN | TOPALIGN are zero. Preserve vertical alignment before switching sides.
        return new(anchor, exclusion, 0x0040u | 0x10000u); // TPM_VERTICAL | TPM_WORKAREA
    }

    internal static FileMenuPopup Keyboard(Point position, uint alignment) => new(position, null, alignment);

    internal static Rect? CursorBounds(Point position, int bitmapWidth, int bitmapHeight,
        uint hotspotX, uint hotspotY, bool monochrome)
    {
        if (monochrome && bitmapHeight % 2 != 0) return null;
        int height = monochrome ? bitmapHeight / 2 : bitmapHeight;
        if (bitmapWidth <= 0 || height <= 0 || hotspotX >= bitmapWidth || hotspotY >= height) return null;
        return new Rect(position.X - hotspotX, position.Y - hotspotY, bitmapWidth, height);
    }

    internal static Rect FallbackCursorBounds(Point position, int width, int height)
    {
        // Without a hotspot, allow the full cursor size on either side of the click.
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        return new Rect(position.X - width, position.Y - height, 2d * width, 2d * height);
    }

    // Called before any await. Inputs are root-local DIPs and a physical screen origin.
    internal static Point Capture(Point? requested, Rect item, Point rootScreenOrigin, double scale)
    {
        Point local = requested ?? new Point(item.Left, item.Bottom);
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(local.X) || !double.IsFinite(local.Y) ||
            !double.IsFinite(rootScreenOrigin.X) || !double.IsFinite(rootScreenOrigin.Y))
            throw new ArgumentOutOfRangeException(nameof(scale));
        return new Point(checked((int)Math.Round(rootScreenOrigin.X + local.X * scale)),
            checked((int)Math.Round(rootScreenOrigin.Y + local.Y * scale)));
    }
}
