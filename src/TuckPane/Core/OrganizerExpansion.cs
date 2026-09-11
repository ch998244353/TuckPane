namespace TuckPane.Core;

using TuckPane.Models;

internal static class OrganizerExpansion
{
    internal static bool CanEnable(OrganizerDefinition organizer) =>
        organizer.PlacementMode is OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned &&
        organizer.ContainerOrganizerId is null;

    internal static bool IsPermanent(OrganizerDefinition organizer) =>
        organizer.ExpansionMode == OrganizerExpansionMode.AlwaysExpanded && CanEnable(organizer);

    internal static void Normalize(OrganizerDefinition organizer)
    {
        if (!Enum.IsDefined(organizer.ExpansionMode) || !CanEnable(organizer))
            organizer.ExpansionMode = OrganizerExpansionMode.Collapsible;
    }

    // Saving happens before the caller changes the live window. A failed write leaves its mode intact.
    internal static async Task SetAsync(
        OrganizerDefinition organizer, OrganizerExpansionMode mode, Func<Task> save)
    {
        if (!Enum.IsDefined(mode) || mode == OrganizerExpansionMode.AlwaysExpanded && !CanEnable(organizer))
            throw new InvalidOperationException("Permanent expansion requires an uncontained floating or positioned organizer.");
        OrganizerExpansionMode previous = organizer.ExpansionMode;
        if (previous == mode) return;
        organizer.ExpansionMode = mode;
        try { await save(); }
        catch { organizer.ExpansionMode = previous; throw; }
    }
}
