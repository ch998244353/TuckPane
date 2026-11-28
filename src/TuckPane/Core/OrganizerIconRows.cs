using TuckPane.Models;

namespace TuckPane.Core;

internal static class OrganizerIconRows
{
    internal static List<int> DisplayRows(OrganizerDefinition organizer, bool autoFill, int columns, int count) =>
        IconRowLayout.Normalize(autoFill ? null : organizer.IconRowCounts, columns, count);

    internal static void RemoveItem(OrganizerDefinition organizer, string key)
    {
        int index = organizer.ItemOrder.FindIndex(name => name.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        if (organizer.PlacementMode != OrganizerPlacementMode.Dock)
        {
            var rows = IconRowLayout.Normalize(organizer.IconRowCounts, organizer.Layout.Columns, organizer.ItemOrder.Count);
            organizer.IconRowCounts = IconRowLayout.Remove(rows, index);
        }
        organizer.ItemOrder.RemoveAt(index);
    }

    // The catalog keeps surviving names in their original order and appends new names.
    internal static List<int> Reconcile(OrganizerDefinition organizer, IReadOnlyList<string> nextOrder)
    {
        if (organizer.PlacementMode == OrganizerPlacementMode.Dock) return [];
        int columns = organizer.Layout.Columns;
        var rows = IconRowLayout.Normalize(organizer.IconRowCounts, columns, organizer.ItemOrder.Count);
        if (organizer.ItemOrder.SequenceEqual(nextOrder, StringComparer.OrdinalIgnoreCase))
            return rows;
        var remaining = nextOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = new List<int>(rows.Count);
        int index = 0;
        foreach (int size in rows)
        {
            int count = 0;
            for (int end = index + size; index < end; index++)
                if (remaining.Remove(organizer.ItemOrder[index])) count++;
            if (count > 0) kept.Add(count);
        }
        return IconRowLayout.Normalize(kept, columns, nextOrder.Count);
    }
}
