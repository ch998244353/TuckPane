using TuckPane.Models;

namespace TuckPane.Core;

// A minimized native rectangle must never replace the last normal document placement.
internal sealed class TodoPlacementRecovery(PortableNotePlacement? initial)
{
    private PortableNotePlacement _lastValid = Copy(IsValid(initial)
        ? initial!
        : new PortableNotePlacement { WidthDip = 360, HeightDip = 480 });

    internal PortableNotePlacement? CaptureWindowChange(PortableNotePlacement candidate, bool isRestored) =>
        isRestored && IsValid(candidate) ? CaptureForSave(candidate) : null;

    internal PortableNotePlacement CaptureForSave(PortableNotePlacement? current)
    {
        if (IsValid(current)) _lastValid = Copy(current!);
        return Copy(_lastValid);
    }

    private static bool IsValid(PortableNotePlacement? value) => value is not null &&
        value.MonitorDevice is not null && double.IsFinite(value.XDip) && double.IsFinite(value.YDip) &&
        double.IsFinite(value.WidthDip) && double.IsFinite(value.HeightDip) &&
        value.WidthDip is >= 280 and <= 1600 && value.HeightDip is >= 340 and <= 1200;

    private static PortableNotePlacement Copy(PortableNotePlacement value) => new()
    {
        MonitorDevice = value.MonitorDevice,
        XDip = value.XDip,
        YDip = value.YDip,
        WidthDip = value.WidthDip,
        HeightDip = value.HeightDip
    };
}
