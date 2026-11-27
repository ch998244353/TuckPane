using TuckPane.Models;

namespace TuckPane.Core;

internal sealed record UnifiedExpandedSave(long Revision, int Modes, UnifiedExpandedSnapshot Snapshot);

// UI-thread edits; capture is called inside StateStore's serialization gate, not when saving is queued.
internal sealed class UnifiedExpandedChanges
{
    private UnifiedExpandedSnapshot? _baseline;
    private long _revision;
    private long _committed;
    private int _modes;

    internal void Begin(AppStateV2 state, OrganizerExpandedContentMode mode)
    {
        _baseline ??= UnifiedExpandedSnapshot.Capture(state);
        _modes |= 1 << (int)mode;
        ++_revision;
    }

    internal UnifiedExpandedSave? Capture(AppStateV2 state) => _baseline is null ? null :
        new(_revision, _modes, UnifiedExpandedSnapshot.Capture(state));

    internal void Commit(UnifiedExpandedSave? saved)
    {
        if (saved is null || saved.Revision < _committed) return;
        _committed = saved.Revision;
        if (saved.Revision == _revision) { _baseline = null; _modes = 0; }
        else _baseline = saved.Snapshot;
    }

    internal bool Rollback(AppStateV2 state, UnifiedExpandedSave? saved)
    {
        if (saved is null || saved.Revision != _revision || saved.Revision <= _committed || _baseline is null) return false;
        _baseline.Restore(state, _modes);
        _baseline = null;
        _modes = 0;
        return true;
    }
}
