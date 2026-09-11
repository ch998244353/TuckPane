namespace TuckPane.Core;

internal static class DockHoverOwnership
{
    // Only a positively identified Dock tooltip may be skipped. An unrelated
    // window underneath that tooltip must still stop the hover.
    internal static bool IsOverDock(nint hit, nint dock, bool currentToolTipHit,
        Func<nint, nint> nextWindow, Func<nint, bool> coversPointer)
    {
        if (hit == dock && dock != 0) return true;
        if (hit == 0 || dock == 0 || !currentToolTipHit) return false;
        nint window = hit;
        for (int remaining = 256; remaining > 0; remaining--)
        {
            nint next = nextWindow(window);
            if (next == 0 || next == window) return false;
            window = next;
            if (coversPointer(window)) return window == dock;
        }
        return false;
    }
}
