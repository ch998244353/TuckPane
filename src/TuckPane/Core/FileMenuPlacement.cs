using Windows.Foundation;

namespace TuckPane.Core;

// All geometry is in physical screen pixels. XAML conversion happens only at
// the presentation boundary, so monitors with negative origins work identically.
internal readonly record struct FileMenuPlacementResult(
    Rect Bounds, Rect Exclusion, bool OpensLeft, bool OpensUp, double MaximumHeight);

internal static class FileMenuPlacement
{
    internal const double GapDip = 6;

    internal static FileMenuPlacementResult Calculate(Rect item, Rect work, Size menu, double scale)
    {
        if (!Valid(item) || !Valid(work) || !double.IsFinite(scale) || scale <= 0 ||
            !double.IsFinite(menu.Width) || !double.IsFinite(menu.Height) || menu.Width <= 0 || menu.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(item), "Menu placement requires finite, positive bounds.");

        double gap = GapDip * scale;
        double below = Math.Max(0, work.Bottom - Math.Max(work.Top, item.Bottom + gap));
        double above = Math.Max(0, Math.Min(work.Bottom, item.Top - gap) - work.Top);
        bool up = below < menu.Height && (above >= menu.Height || above > below);
        double available = up ? above : below;
        if (available <= 0) throw new InvalidOperationException("No vertical space outside the item for a menu.");
        double width = Math.Min(menu.Width, work.Width);
        double height = Math.Min(menu.Height, available);
        bool left = item.Right + gap + width > work.Right;
        double x = left ? item.Left - gap - width : item.Right + gap;
        x = Math.Clamp(x, work.Left, work.Right - width);
        double y = up ? Math.Min(work.Bottom, item.Top - gap) - height : Math.Max(work.Top, item.Bottom + gap);
        return new(new Rect(x, y, width, height),
            new Rect(item.Left - gap, item.Top - gap, item.Width + gap * 2, item.Height + gap * 2),
            left, up, available);
    }

    internal static Rect ToLocal(Rect screen, Point screenOrigin, double scale) =>
        new((screen.X - screenOrigin.X) / scale, (screen.Y - screenOrigin.Y) / scale,
            screen.Width / scale, screen.Height / scale);

    private static bool Valid(Rect rect) => double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) && rect.Width > 0 && rect.Height > 0;
}

internal sealed record FileMenuAnchor(Rect Item, Rect Work, Point RootOrigin, double Scale);
