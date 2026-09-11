using System.Numerics;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane.Core;

internal readonly record struct DockGeometry(double Width, double Height, double IconSize, double Pitch,
    double EndInset, double CrossInset, double CornerRadius, int Count, DockOrientation Orientation)
{
    internal double HorizontalInset => Orientation == DockOrientation.Horizontal ? EndInset : CrossInset;
    internal double VerticalInset => Orientation == DockOrientation.Vertical ? EndInset : CrossInset;
    internal double TopInset => Orientation == DockOrientation.Horizontal ? CrossInset * .75 : EndInset;
    internal double BottomInset => Orientation == DockOrientation.Horizontal ? CrossInset * 1.25 : EndInset;

    internal Vector2 ItemCenter(int index) => Orientation == DockOrientation.Horizontal
        ? new((float)(EndInset + IconSize / 2 + index * Pitch), (float)(TopInset + IconSize / 2))
        : new((float)(Width / 2), (float)(EndInset + IconSize / 2 + index * Pitch));

    internal bool Contains(Vector2 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;
        double radius = CornerRadius;
        double x = Math.Clamp(point.X, radius, Width - radius);
        double y = Math.Clamp(point.Y, radius, Height - radius);
        return Math.Pow(point.X - x, 2) + Math.Pow(point.Y - y, 2) <= radius * radius;
    }

    internal bool ContainsIcon(Vector2 point, int index, double scale)
    {
        Vector2 center = ItemCenter(index);
        double half = IconSize * Math.Clamp(scale, 1, OrganizerHoverWave.MaximumScale) / 2;
        return Math.Abs(point.X - center.X) <= half && Math.Abs(point.Y - center.Y) <= half;
    }

    internal int InsertionIndex(Vector2 point) => Math.Clamp((int)Math.Floor(
        ((Orientation == DockOrientation.Horizontal ? point.X : point.Y) - EndInset - IconSize / 2) / Pitch) + 1, 0, Count);
}

internal static class DockLayoutMath
{
    internal static NativeMethods.RECT CalculateBounds(DockGeometry geometry, Vector2 centerDip, double scale)
    {
        int width = Math.Max(1, (int)Math.Round(geometry.Width * scale));
        int height = Math.Max(1, (int)Math.Round(geometry.Height * scale));
        int left = (int)Math.Round(centerDip.X * scale - width / 2d);
        int top = (int)Math.Round(centerDip.Y * scale - height / 2d);
        return new() { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    internal static double NormalizeIconSize(double size) => double.IsFinite(size)
        ? Math.Clamp(Math.Round(size / 4, MidpointRounding.AwayFromZero) * 4, 32, 128) : 64;

    internal static double NormalizeSpacing(double factor) => double.IsFinite(factor)
        ? Math.Round(Math.Clamp(factor, .5, 2) * 20, MidpointRounding.AwayFromZero) / 20 : .75;

    internal static double ApplyWheelDelta(double size, int delta, bool controlPressed,
        bool interactionBusy, ref int remainder)
    {
        if (!controlPressed || interactionBusy)
        {
            remainder = 0;
            return size;
        }
        long accumulated = (long)remainder + delta;
        long steps = accumulated / 120;
        remainder = (int)(accumulated % 120);
        return NormalizeIconSize(size + steps * 4);
    }

    internal static DockGeometry Calculate(int count, double iconSize, DockOrientation orientation, double spacingFactor = .75)
    {
        double size = NormalizeIconSize(iconSize);
        int slots = Math.Max(1, count);
        double gap = size / 3 * NormalizeSpacing(spacingFactor);
        double pitch = size + gap;
        // Preserve the fixed surface. The continuous hover field has bounded
        // cumulative displacement, independent of the number of items.
        double endInset = count > 1 ? HoverLayoutMath.DockExpansionBudget * pitch + size * .125 + 2 : size * .375;
        double crossInset = size / 4;
        if (orientation == DockOrientation.Horizontal) endInset /= 2;

        double length = size + (slots - 1) * pitch + 2 * endInset;
        double thickness = size + 2 * crossInset;
        double radius = size * .28125;
        // The raised singleton's maximum-scale upper corners must remain inside
        // the rounded surface without widening or recentering the Dock.
        if (orientation == DockOrientation.Horizontal && count <= 1)
            radius = Math.Min(radius, size * .2);
        if (orientation == DockOrientation.Vertical && count <= 1)
            radius = Math.Min(radius, endInset - size * .125);
        return new(orientation == DockOrientation.Vertical ? thickness : length,
            orientation == DockOrientation.Vertical ? length : thickness,
            size, pitch, endInset, crossInset, radius, Math.Max(0, count), orientation);
    }
}
