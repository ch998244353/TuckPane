using TuckPane.Models;

namespace TuckPane.Core;

internal static class OrganizerKinds
{
    internal static bool IsRegular(OrganizerPlacementMode mode) =>
        mode is OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned;

    internal static bool CanChange(OrganizerPlacementMode source, OrganizerPlacementMode target) =>
        Enum.IsDefined(source) && Enum.IsDefined(target) && (source == target || IsRegular(source) && IsRegular(target));

    internal static bool CanCreate(OrganizerPlacementMode mode, IEnumerable<OrganizerDefinition> organizers) =>
        mode == OrganizerPlacementMode.Dock || (IsRegular(mode)
            ? true
            : mode == OrganizerPlacementMode.Station && organizers.Count(item => item.PlacementMode == mode) < OrganizerLimits.MaximumStations);

    internal static ThemeTarget ThemeFor(OrganizerPlacementMode mode) => mode switch
    {
        OrganizerPlacementMode.Station => ThemeTarget.Station,
        OrganizerPlacementMode.Dock => ThemeTarget.Dock,
        _ => ThemeTarget.Organizer
    };
}
