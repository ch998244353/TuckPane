using System.Numerics;
using TuckPane.Models;

namespace TuckPane.Core;

internal sealed class DockIncomingGap(int index)
{
    internal int Index { get; private set; } = index;
    internal bool Frozen { get; private set; }

    internal bool Update(Vector2 point, DockGeometry previewGeometry, int itemCount)
    {
        if (Frozen) return false;
        int previous = Index;
        Index = Math.Clamp(Index, 0, itemCount);
        double axis = previewGeometry.Orientation == DockOrientation.Horizontal ? point.X : point.Y;
        double Center(int slot)
        {
            Vector2 center = previewGeometry.ItemCenter(slot);
            return previewGeometry.Orientation == DockOrientation.Horizontal ? center.X : center.Y;
        }
        while (Index > 0 && axis < Center(Index - 1) - ItemReorderSession.SlotHysteresisDip) Index--;
        while (Index < itemCount && axis > Center(Index + 1) + ItemReorderSession.SlotHysteresisDip) Index++;
        return Index != previous;
    }

    internal DockGapAnchor Freeze(IReadOnlyList<string> order)
    {
        Frozen = true;
        int index = Math.Clamp(Index, 0, order.Count);
        return new(index, index > 0 ? order[index - 1] : null, index < order.Count ? order[index] : null);
    }
}

internal sealed record DockGapAnchor(int Index, string? Before, string? After)
{
    internal List<string> Insert(IReadOnlyList<string> order, IEnumerable<string> incoming)
    {
        string[] added = incoming.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var addedSet = new HashSet<string>(added, StringComparer.OrdinalIgnoreCase);
        var result = order.Where(item => !addedSet.Contains(item)).ToList();
        int next = After is null ? -1 : result.FindIndex(item => item.Equals(After, StringComparison.OrdinalIgnoreCase));
        int previous = Before is null ? -1 : result.FindIndex(item => item.Equals(Before, StringComparison.OrdinalIgnoreCase));
        int index = next >= 0 ? next : previous >= 0 ? previous + 1 : Math.Clamp(Index, 0, result.Count);
        result.InsertRange(index, added);
        return result;
    }

    internal static IEnumerable<string> SuccessfulNames(IEnumerable<TransferOutcome> outcomes) => outcomes
        .Where(outcome => outcome.Status is TransferStatus.Moved or TransferStatus.Copied or
            TransferStatus.ShortcutCreated or TransferStatus.CopiedSourceRetained)
        .Where(outcome => !string.IsNullOrWhiteSpace(outcome.DestinationPath))
        .Select(outcome => Path.GetFileName(outcome.DestinationPath)!);
}
