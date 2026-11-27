using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane.Core;

// Immutable screen-space snapshot, safe to read from the mouse hook.
internal sealed record IconDragBoundary(NativeMethods.RECT Canvas, double Left, double Top, double Right, double Bottom)
{
    internal static IconDragBoundary Create(NativeMethods.RECT canvas, Rect iconInHost,
        Point hostCenter, double dragScale, Point grabOffset, double dpiScale) => new(canvas,
        (hostCenter.X + (iconInHost.Left - hostCenter.X) * dragScale - grabOffset.X) * dpiScale,
        (hostCenter.Y + (iconInHost.Top - hostCenter.Y) * dragScale - grabOffset.Y) * dpiScale,
        (hostCenter.X + (iconInHost.Right - hostCenter.X) * dragScale - grabOffset.X) * dpiScale,
        (hostCenter.Y + (iconInHost.Bottom - hostCenter.Y) * dragScale - grabOffset.Y) * dpiScale);

    internal bool IsFullyOutside(double pointerX, double pointerY) =>
        pointerX + Right <= Canvas.Left || pointerX + Left >= Canvas.Right ||
        pointerY + Bottom <= Canvas.Top || pointerY + Top >= Canvas.Bottom;
}
