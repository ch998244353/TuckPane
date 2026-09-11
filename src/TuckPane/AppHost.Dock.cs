using TuckPane.Core;
using TuckPane.Models;

namespace TuckPane;

public sealed partial class AppHost
{
    internal DockSettingsChanges DockSettings { get; } = new();

    public Task SaveStateAsync() => SaveStateAsync(null);

    internal async Task SaveStateAsync(Action? snapshotCaptured)
    {
        IReadOnlyList<DockSettingsSave> pending = [];
        try
        {
            // Capture after StateStore's save gate opens, alongside the actual JSON snapshot.
            // Position/theme saves may also persist a pending Dock size before the debounce fires.
            await _stateStore.SaveAsync(State, () =>
            {
                snapshotCaptured?.Invoke();
                pending = DockSettings.Capture();
            });
            DockSettings.Commit(pending);
        }
        catch
        {
            foreach (OrganizerDefinition restored in DockSettings.Rollback(pending))
            {
                if (!State.Organizers.Contains(restored)) continue;
                if (_windows.TryGetValue(restored.Id, out MainWindow? window))
                    window.ApplyDefinition(OrganizerVisualChange.Layout);
                Console?.RefreshDockSettings(restored);
            }
            throw;
        }
    }
}
