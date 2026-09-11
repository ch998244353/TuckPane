using TuckPane.Core;
using TuckPane.Models;

namespace TuckPane;

public sealed partial class AppHost
{
    private readonly SemaphoreSlim _nameVisibilityGate = new(1, 1);

    internal async Task SetOrganizerNameVisibilityAsync(Guid id, bool hideName)
    {
        await _nameVisibilityGate.WaitAsync();
        try
        {
            OrganizerDefinition organizer = State.Organizers.First(item => item.Id == id);
            try { await OrganizerNameVisibility.SetAsync(organizer, hideName, SaveStateAsync); }
            finally
            {
                if (_windows.TryGetValue(id, out MainWindow? window)) window.RefreshOrganizerName();
            }
        }
        finally { _nameVisibilityGate.Release(); }
    }
}
