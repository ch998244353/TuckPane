using TuckPane.Models;

namespace TuckPane.Core;

// Transient native rectangles must not prevent the current editor body from being saved.
internal sealed class NotePlacementRecovery(NoteWindowPlacement? initial)
{
    private NoteWindowPlacement _lastValid = Copy(IsValid(initial) ? initial! : new NoteWindowPlacement());

    internal NoteWindowPlacement? CaptureWindowChange(NoteWindowPlacement candidate, bool isRestored)
    {
        if (!isRestored || !IsValid(candidate)) return null;
        return CaptureForSave(candidate);
    }

    internal NoteWindowPlacement CaptureForSave(NoteWindowPlacement? current)
    {
        if (IsValid(current)) _lastValid = Copy(current!);
        return Copy(_lastValid);
    }

    private static bool IsValid(NoteWindowPlacement? value) => value is not null &&
        IsValidGeometry(value.MonitorDevice, value.XDip, value.YDip, value.WidthDip, value.HeightDip);

    // Also used by the strict portable reader/writer; recovery only happens in the window's snapshot.
    internal static bool IsValidGeometry(string? monitorDevice, double xDip, double yDip, double widthDip, double heightDip) =>
        monitorDevice is not null && double.IsFinite(xDip) && double.IsFinite(yDip) &&
        double.IsFinite(widthDip) && double.IsFinite(heightDip) &&
        widthDip is >= 280 and <= 1600 && heightDip is >= 220 and <= 1200;

    internal static PortableNotePlacement ToPortable(NoteWindowPlacement value) => new()
    {
        MonitorDevice = value.MonitorDevice,
        XDip = value.XDip,
        YDip = value.YDip,
        WidthDip = value.WidthDip,
        HeightDip = value.HeightDip
    };

    private static NoteWindowPlacement Copy(NoteWindowPlacement value) => new()
    {
        MonitorDevice = value.MonitorDevice,
        XDip = value.XDip,
        YDip = value.YDip,
        WidthDip = value.WidthDip,
        HeightDip = value.HeightDip
    };
}
