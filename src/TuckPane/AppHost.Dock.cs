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
        UnifiedExpandedSave? expandedPending = null;
        try
        {
            // Capture after StateStore's save gate opens, alongside the actual JSON snapshot.
            // Position/theme saves may also persist a pending Dock size before the debounce fires.
            await _stateStore.SaveAsync(State, () =>
            {
                snapshotCaptured?.Invoke();
                pending = DockSettings.Capture();
                expandedPending = _unifiedExpandedChanges.Capture(State);
            }, async () =>
            {
                // Recheck inside the serialization gate: an interaction may have begun while queued.
                while (true)
                {
                    Task[] resizing = _windows.Values.Select(w => w.WaitForUnifiedResizeAsync()).Where(t => !t.IsCompleted).ToArray();
                    if (resizing.Length == 0) break;
                    await Task.WhenAll(resizing);
                }
                foreach (MainWindow window in _windows.Values.ToArray()) window.CommitUnifiedWheelEdit();
            });
            DockSettings.Commit(pending);
            _unifiedExpandedChanges.Commit(expandedPending);
        }
        catch
        {
            if (_unifiedExpandedChanges.Rollback(State, expandedPending)) RestoreUnifiedExpandedWindows();
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
