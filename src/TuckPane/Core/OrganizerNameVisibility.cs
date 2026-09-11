using TuckPane.Models;

namespace TuckPane.Core;

internal static class OrganizerNameVisibility
{
    internal static async Task SetAsync(OrganizerDefinition organizer, bool hideName, Func<Task> save)
    {
        bool previous = organizer.HideName;
        if (previous == hideName) return;
        organizer.HideName = hideName;
        try { await save(); }
        catch { organizer.HideName = previous; throw; }
    }
}
