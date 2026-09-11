using TuckPane.Services;

namespace TuckPane.Core;

internal static class RenameDialogPlacement
{
    // Resolve at dialog-open time, before activation can move focus to another screen.
    internal static DisplayInfo Resolve(NativeMethods.RECT? currentBounds, string? savedDevice,
        Func<NativeMethods.RECT, DisplayInfo> forBounds, Func<string?, DisplayInfo> getDisplay) =>
        currentBounds is { } bounds ? forBounds(bounds) : getDisplay(savedDevice);
}
