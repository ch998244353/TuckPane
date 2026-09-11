using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class AppHost
{
    // Only model mutation + persistence is shared. Animations never own this gate.
    private readonly SemaphoreSlim _expansionModeSaveGate = new(1, 1);
    private readonly Dictionary<Guid, ExpansionModeCoordinator> _expansionModes = [];

    internal OrganizerExpansionMode GetRequestedExpansionMode(Guid id) =>
        _expansionModes.TryGetValue(id, out var coordinator) && coordinator.IsPending
            ? coordinator.DesiredMode
            : State.Organizers.First(item => item.Id == id).ExpansionMode;

    internal async Task<string?> SetOrganizerExpansionModeAsync(Guid id, OrganizerExpansionMode? requestedMode = null,
        Func<CancellationToken, Task<bool>>? prepare = null)
    {
        OrganizerDefinition? current = State.Organizers.FirstOrDefault(item => item.Id == id);
        if (current is null) return null;
        OrganizerExpansionMode mode = requestedMode ?? (GetRequestedExpansionMode(id) == OrganizerExpansionMode.AlwaysExpanded
            ? OrganizerExpansionMode.Collapsible : OrganizerExpansionMode.AlwaysExpanded);
        if (!OrganizerExpansion.CanEnable(current))
            return AppStrings.Get("ExpansionModeUnavailable");
        if (!_expansionModes.TryGetValue(id, out var coordinator) ||
            !coordinator.IsPending && coordinator.CommittedMode != current.ExpansionMode)
        {
            _windows.TryGetValue(id, out MainWindow? window);
            coordinator = new ExpansionModeCoordinator(current.ExpansionMode, async (target, token) =>
            {
                await _expansionModeSaveGate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    OrganizerDefinition definition = State.Organizers.First(item => item.Id == id);
                    await OrganizerExpansion.SetAsync(definition, target, SaveStateAsync);
                }
                finally { _expansionModeSaveGate.Release(); }
            }, window);
            _expansionModes[id] = coordinator;
        }
        try { await coordinator.RequestAsync(mode, prepare); }
        finally
        {
            if (_expansionModes.TryGetValue(id, out var active) && ReferenceEquals(active, coordinator) &&
                State.Organizers.Any(item => item.Id == id)) Console.RefreshExpansionModeSelection(id);
        }
        return null;
    }

    internal void CloseExpansionModeCoordinator(Guid id)
    {
        if (_expansionModes.Remove(id, out var coordinator)) coordinator.Close();
    }

    internal void NotifyPermanentExpanded(MainWindow window)
    {
        if (ReferenceEquals(_expandedWindow, window)) _expandedWindow = null;
    }
}
