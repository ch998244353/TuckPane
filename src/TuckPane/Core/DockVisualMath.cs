using System.Numerics;

namespace TuckPane.Core;

internal enum DockTipSide { Top, Bottom, Left, Right }
internal readonly record struct DockTipPlacement(HoverRect Bounds, DockTipSide Side);

internal static class DockVisualMath
{
    // All coordinates are in the hover viewport. Only the layout displacement
    // moves a vertical running dot; magnification and launch bounce do not.
    internal static Vector2 VerticalRunningDotCenter(float surfaceLeft, float leftInset,
        HoverRect baselineIcon, float translationY) =>
        new(surfaceLeft + leftInset / 2, baselineIcon.Center.Y + translationY);

    internal static HoverRect TipWorkArea(HoverRect workArea)
    {
        float insetX = Math.Min(8, Math.Max(0, (workArea.Width - 1) / 2));
        float insetY = Math.Min(8, Math.Max(0, (workArea.Height - 1) / 2));
        return new(workArea.X + insetX, workArea.Y + insetY,
            workArea.Width - 2 * insetX, workArea.Height - 2 * insetY);
    }

    internal static (int Width, int Height) TipClientPixels(Vector2 desiredSize, double scale) =>
        (Math.Max(1, (int)Math.Ceiling(desiredSize.X * scale)),
         Math.Max(1, (int)Math.Ceiling(desiredSize.Y * scale)));

    internal static DockTipPlacement PlaceTip(HoverRect icon, Vector2 bodySize, HoverRect workArea,
        bool horizontal)
    {
        HoverRect area = TipWorkArea(workArea);
        float width = Math.Min(bodySize.X, area.Width);
        float height = Math.Min(bodySize.Y, area.Height);
        DockTipSide side = horizontal ? DockTipSide.Top : DockTipSide.Right;
        // Measure the visible body, not the removed arrow: its old 12-DIP
        // clearance plus the requested 12-DIP lift gives 24 DIP above the icon.
        float x = horizontal ? icon.Center.X - width / 2 : icon.Right + 12;
        float y = horizontal ? icon.Y - 24 - height : icon.Center.Y - height / 2 - 12;
        if (horizontal && y < area.Y)
        {
            side = DockTipSide.Bottom;
            y = icon.Bottom + 12;
        }
        if (!horizontal && x + width > area.Right)
        {
            side = DockTipSide.Left;
            x = icon.X - 12 - width;
        }
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height));
        return new(new(x, y, width, height), side);
    }

    // renderedIcon is the current scaled resting rectangle, WITHOUT the bounce translation.
    // Returning local coordinates prevents scaling the requested screen-space travel twice.
    internal static float BounceOffset(float envelopeOffset, float envelopePeak, HoverRect renderedIcon,
        float surfaceTop, float viewportTop, float scale)
    {
        if (envelopePeak <= 0 || scale <= 0) return 0;
        float desired = Math.Max(0, renderedIcon.Y - surfaceTop + renderedIcon.Height / 3);
        float available = Math.Max(0, renderedIcon.Y - viewportTop);
        return envelopeOffset / envelopePeak * Math.Min(desired, available) / scale;
    }
}
