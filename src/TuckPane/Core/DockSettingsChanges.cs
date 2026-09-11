using TuckPane.Models;

namespace TuckPane.Core;

internal readonly record struct DockSettingsSave(Guid Id, DockOrientation Direction, double Size, double Spacing, long Revision);

// Owned by one AppHost on its UI thread. Save receipts are captured at serialization time.
internal sealed class DockSettingsChanges
{
    private sealed class Entry(OrganizerDefinition definition)
    {
        internal readonly OrganizerDefinition Definition = definition;
        internal DockOrientation SavedDirection = definition.DockOrientation;
        internal double SavedSize = definition.DockIconSizeDip;
        internal double SavedSpacing = definition.DockSpacingFactor;
        internal long Revision;
        internal long SavedRevision;
    }

    private readonly Dictionary<Guid, Entry> _entries = [];

    internal bool Apply(OrganizerDefinition definition, DockOrientation direction, double size) =>
        Apply(definition, direction, size, definition.DockSpacingFactor);

    internal bool Apply(OrganizerDefinition definition, DockOrientation direction, double size, double spacingFactor)
    {
        if (definition.PlacementMode != OrganizerPlacementMode.Dock) return false;
        direction = Enum.IsDefined(direction) ? direction : DockOrientation.Horizontal;
        size = DockLayoutMath.NormalizeIconSize(size);
        spacingFactor = DockLayoutMath.NormalizeSpacing(spacingFactor);
        if (definition.DockOrientation == direction && definition.DockIconSizeDip == size &&
            definition.DockSpacingFactor == spacingFactor) return false;
        if (!_entries.TryGetValue(definition.Id, out Entry? entry))
            _entries.Add(definition.Id, entry = new Entry(definition));
        definition.DockOrientation = direction;
        definition.DockIconSizeDip = size;
        definition.DockSpacingFactor = spacingFactor;
        entry.Revision++;
        return true;
    }

    internal IReadOnlyList<DockSettingsSave> Capture() => _entries.Values
        .Where(entry => entry.Revision > entry.SavedRevision)
        .Select(entry => new DockSettingsSave(entry.Definition.Id, entry.Definition.DockOrientation,
            entry.Definition.DockIconSizeDip, entry.Definition.DockSpacingFactor, entry.Revision)).ToArray();

    internal void Commit(IReadOnlyList<DockSettingsSave> saves)
    {
        foreach (DockSettingsSave saved in saves)
        {
            if (!_entries.TryGetValue(saved.Id, out Entry? entry) || saved.Revision <= entry.SavedRevision) continue;
            entry.SavedDirection = saved.Direction;
            entry.SavedSize = saved.Size;
            entry.SavedSpacing = saved.Spacing;
            entry.SavedRevision = saved.Revision;
        }
    }

    internal IReadOnlyList<OrganizerDefinition> Rollback(IReadOnlyList<DockSettingsSave> saves)
    {
        var restored = new List<OrganizerDefinition>();
        foreach (DockSettingsSave failed in saves)
        {
            if (!_entries.TryGetValue(failed.Id, out Entry? entry) ||
                entry.Revision != failed.Revision || failed.Revision <= entry.SavedRevision) continue;
            entry.Definition.DockOrientation = entry.SavedDirection;
            entry.Definition.DockIconSizeDip = entry.SavedSize;
            entry.Definition.DockSpacingFactor = entry.SavedSpacing;
            entry.SavedRevision = ++entry.Revision;
            restored.Add(entry.Definition);
        }
        return restored;
    }
}
