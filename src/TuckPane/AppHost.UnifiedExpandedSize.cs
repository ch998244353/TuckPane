using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class AppHost
{
    private readonly UnifiedExpandedChanges _unifiedExpandedChanges = new();
    private bool _applyingUnifiedExpanded;
    private bool _unifiedDisplayRefreshQueued;
    private readonly Dictionary<OrganizerExpandedContentMode, UnifiedExpandedResult> _unifiedExpandedResults = [];
    internal event EventHandler? UnifiedExpandedChanged;

    internal bool UsesUnifiedExpanded(OrganizerDefinition item) => OrganizerKinds.IsRegular(item.PlacementMode) &&
        UnifiedExpandedSize.Get(State.GlobalSettings, item.ExpandedContentMode).Enabled;

    private UnifiedExpandedTarget[] GetUnifiedExpandedTargets()
    {
        IReadOnlyList<DisplayInfo> displays = DisplayPlacementService.GetDisplays();
        return State.Organizers.Where(item => OrganizerKinds.IsRegular(item.PlacementMode)).Select(item =>
        {
            OrganizerDefinition anchor = item.ContainerOrganizerId is Guid parent
                ? State.Organizers.FirstOrDefault(o => o.Id == parent) ?? item : item;
            bool remember = OrganizerInteractionMath.ShouldRememberExpandedPosition(
                State.GlobalSettings.RememberExpandedOrganizerPosition, item.PlacementMode, OrganizerExpansion.IsPermanent(item));
            DisplayInfo display;
            if (_windows.TryGetValue(item.Id, out var ownWindow) && ownWindow.IsExpanded)
                display = ownWindow.UnifiedExpandedDisplay;
            else if (remember && item.ExpandedPosition is { } saved)
                display = displays.FirstOrDefault(d => d.Device == saved.MonitorDevice) ?? displays[0];
            else if (_windows.TryGetValue(anchor.Id, out MainWindow? window)) display = window.UnifiedExpandedDisplay;
            else display = displays.FirstOrDefault(d => d.Device == anchor.Position?.MonitorDevice) ?? displays[0];
            NativeMethods.RECT work = DisplayPlacementService.GetExpandedWorkArea(display);
            return new UnifiedExpandedTarget(item, work.Width / display.Scale,
                Math.Max(1, work.Height / display.Scale - DisplayPlacementService.ExpandedTitleBandDip));
        }).ToArray();
    }

    internal UnifiedExpandedResult GetUnifiedExpandedResult(OrganizerExpandedContentMode mode)
    {
        if (_unifiedExpandedResults.TryGetValue(mode, out var result)) return result;
        // Read limits without changing persisted values or any organizer geometry.
        var settings = new GlobalSettings();
        UnifiedExpandedSize.Set(settings, mode, UnifiedExpandedSize.Get(State.GlobalSettings, mode) with { Enabled = false });
        return UnifiedExpandedSize.Apply(settings, mode, GetUnifiedExpandedTargets());
    }

    internal void BeginUnifiedExpandedEdit(OrganizerDefinition item)
    {
        if (UsesUnifiedExpanded(item)) _unifiedExpandedChanges.Begin(State, item.ExpandedContentMode);
    }

    internal void ChangeUnifiedExpanded(OrganizerExpandedContentMode mode, UnifiedExpandedValues value)
    {
        _unifiedExpandedChanges.Begin(State, mode);
        UnifiedExpandedSize.Set(State.GlobalSettings, mode, value);
        ReconcileUnifiedExpanded();
    }

    internal void CommitUnifiedExpandedEdit(OrganizerDefinition item)
    {
        if (!UsesUnifiedExpanded(item)) return;
        UnifiedExpandedSize.Set(State.GlobalSettings, item.ExpandedContentMode,
            UnifiedExpandedSize.FromOrganizer(State.GlobalSettings, item));
        ReconcileUnifiedExpanded(forceRefresh: item.Id);
    }

    internal void ReconcileUnifiedExpanded(Guid? forceRefresh = null)
    {
        if (_applyingUnifiedExpanded) return;
        _applyingUnifiedExpanded = true;
        try
        {
            var targets = GetUnifiedExpandedTargets();
            HashSet<Guid> changed = [];
            foreach (var mode in new[] { OrganizerExpandedContentMode.Icon, OrganizerExpandedContentMode.CompactList })
            {
                var before = UnifiedExpandedSnapshot.Capture(State);
                var result = UnifiedExpandedSize.Apply(State.GlobalSettings, mode, targets);
                if (result.Changed.Length != 0 || result.Values != (mode == OrganizerExpandedContentMode.Icon ? before.Icon : before.Compact))
                {
                    // The baseline must precede normalization, including lifecycle-driven reductions.
                    var after = UnifiedExpandedSnapshot.Capture(State);
                    before.Restore(State, 1 << (int)mode);
                    _unifiedExpandedChanges.Begin(State, mode);
                    after.Restore(State, 1 << (int)mode);
                }
                _unifiedExpandedResults[mode] = result;
                changed.UnionWith(result.Changed);
            }
            if (forceRefresh is Guid forcedId) changed.Add(forcedId);
            foreach (Guid id in changed)
                if (_windows.TryGetValue(id, out var window)) window.RefreshUnifiedExpandedSize();
            UnifiedExpandedChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _applyingUnifiedExpanded = false; }
    }

    internal void QueueUnifiedExpandedDisplayRefresh()
    {
        if (_unifiedDisplayRefreshQueued || _applyingUnifiedExpanded ||
            !(State.GlobalSettings.UseUniformIconExpandedSize || State.GlobalSettings.UseUniformCompactExpandedSize)) return;
        _unifiedDisplayRefreshQueued = true;
        _dispatcher.TryEnqueue(async () =>
        {
            _unifiedDisplayRefreshQueued = false;
            try
            {
                if (_windows.Values.Any(w => w.IsResizingExpanded)) return;
                ReconcileUnifiedExpanded();
                if (_unifiedExpandedChanges.Capture(State) is not null) await SaveStateAsync();
            }
            catch (Exception ex) { AppLogger.Error("无法更新统一展开大小。", ex); Console?.ShowUnifiedExpandedError(ex); }
        });
    }

    private void RestoreUnifiedExpandedWindows()
    {
        _unifiedExpandedResults.Clear();
        foreach (var item in State.Organizers.Where(o => OrganizerKinds.IsRegular(o.PlacementMode)))
            if (_windows.TryGetValue(item.Id, out var window)) window.RefreshUnifiedExpandedSize();
        UnifiedExpandedChanged?.Invoke(this, EventArgs.Empty);
    }
}
