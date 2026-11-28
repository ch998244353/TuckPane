namespace TuckPane.Core;

using TuckPane.Models;

internal enum OrganizerContainmentFailure
{
    None,
    MissingOrganizer,
    SameOrganizer,
    StationCannotBeContained,
    DockCannotBeContained,
    PermanentWindowCannotBeContained,
    TargetIsDescendant
}

internal readonly record struct OrganizerContainmentMoveResult(
    bool Succeeded,
    OrganizerContainmentFailure Failure,
    Guid? PreviousContainerId);

internal sealed class OrganizerContainmentSnapshot
{
    private readonly Dictionary<Guid, Guid?> _containers;
    private readonly Dictionary<Guid, List<string>> _orders;
    private readonly Dictionary<Guid, List<int>> _rows;

    internal OrganizerContainmentSnapshot(IReadOnlyList<OrganizerDefinition> organizers)
    {
        _containers = organizers.ToDictionary(candidate => candidate.Id, candidate => candidate.ContainerOrganizerId);
        _rows = organizers.ToDictionary(candidate => candidate.Id, candidate => candidate.IconRowCounts.ToList());
        _orders = organizers.ToDictionary(candidate => candidate.Id, candidate => candidate.ItemOrder.ToList());
    }

    internal void Restore(IReadOnlyList<OrganizerDefinition> organizers)
    {
        foreach (OrganizerDefinition organizer in organizers)
        {
            if (_containers.TryGetValue(organizer.Id, out Guid? containerId))
                organizer.ContainerOrganizerId = containerId;
            if (_orders.TryGetValue(organizer.Id, out List<string>? order))
                organizer.ItemOrder = order.ToList();
            if (_rows.TryGetValue(organizer.Id, out List<int>? rows))
                organizer.IconRowCounts = rows.ToList();
        }
    }
}

internal static class OrganizerContainment
{
    internal const string ItemPrefix = "organizer:";

    internal static string ItemKey(Guid organizerId) => $"{ItemPrefix}{organizerId:N}";

    internal static OrganizerContainmentSnapshot Capture(IReadOnlyList<OrganizerDefinition> organizers) =>
        new(organizers);

    internal static bool TryParseItemKey(string value, out Guid organizerId)
    {
        organizerId = default;
        return value.StartsWith(ItemPrefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(value[ItemPrefix.Length..], "N", out organizerId);
    }

    internal static IReadOnlyList<OrganizerDefinition> GetDirectChildren(
        IReadOnlyList<OrganizerDefinition> organizers,
        Guid containerId)
    {
        OrganizerDefinition? container = organizers.FirstOrDefault(candidate => candidate.Id == containerId);
        if (container is null) return [];

        var childrenByKey = organizers
            .Where(candidate => candidate.ContainerOrganizerId == containerId)
            .ToDictionary(candidate => ItemKey(candidate.Id), StringComparer.OrdinalIgnoreCase);
        var ordered = new List<OrganizerDefinition>(childrenByKey.Count);
        foreach (string key in container.ItemOrder)
        {
            if (childrenByKey.Remove(key, out OrganizerDefinition? child)) ordered.Add(child);
        }
        ordered.AddRange(organizers.Where(candidate =>
            candidate.ContainerOrganizerId == containerId &&
            childrenByKey.ContainsKey(ItemKey(candidate.Id))));
        return ordered;
    }

    internal static OrganizerContainmentMoveResult TryMove(
        IReadOnlyList<OrganizerDefinition> organizers,
        Guid organizerId,
        Guid containerId,
        int insertionIndex,
        IconRowTarget? rowTarget = null)
    {
        OrganizerDefinition? organizer = organizers.FirstOrDefault(candidate => candidate.Id == organizerId);
        OrganizerDefinition? container = organizers.FirstOrDefault(candidate => candidate.Id == containerId);
        if (organizer is null || container is null)
            return new(false, OrganizerContainmentFailure.MissingOrganizer, null);
        if (organizer.Id == container.Id)
            return new(false, OrganizerContainmentFailure.SameOrganizer, organizer.ContainerOrganizerId);
        if (organizer.PlacementMode == OrganizerPlacementMode.Dock)
            return new(false, OrganizerContainmentFailure.DockCannotBeContained, organizer.ContainerOrganizerId);
        if (organizer.PlacementMode == OrganizerPlacementMode.Station)
            return new(false, OrganizerContainmentFailure.StationCannotBeContained, organizer.ContainerOrganizerId);
        if (OrganizerExpansion.IsPermanent(organizer))
            return new(false, OrganizerContainmentFailure.PermanentWindowCannotBeContained, organizer.ContainerOrganizerId);
        if (IsAncestor(organizers, organizer.Id, container.Id))
            return new(false, OrganizerContainmentFailure.TargetIsDescendant, organizer.ContainerOrganizerId);

        string key = ItemKey(organizer.Id);
        int previousIndex = organizer.ContainerOrganizerId == container.Id
            ? container.ItemOrder.FindIndex(item => item.Equals(key, StringComparison.OrdinalIgnoreCase))
            : -1;
        Guid? previousContainerId = organizer.ContainerOrganizerId;
        int columns = container.Layout.Columns;
        var rows = IconRowLayout.Normalize(container.IconRowCounts, columns, container.ItemOrder.Count);
        IconRowEdit? edit = null;
        if (rowTarget is { } target)
        {
            edit = previousIndex >= 0
                ? IconRowLayout.Move(rows, columns, previousIndex, target)
                : IconRowLayout.Insert(rows, columns, target);
        }
        foreach (OrganizerDefinition candidate in organizers)
            OrganizerIconRows.RemoveItem(candidate, key);
        if (previousIndex >= 0 && insertionIndex > previousIndex) insertionIndex--;
        int index = Math.Clamp(insertionIndex, 0, container.ItemOrder.Count);
        if (edit is { } placed)
        {
            index = placed.Index;
            container.IconRowCounts = placed.Rows;
        }
        else if (container.PlacementMode != OrganizerPlacementMode.Dock)
        {
            // Reordering in the compact list keeps the prior row lengths.
            if (previousIndex >= 0) container.IconRowCounts = rows;
            else if (index < container.ItemOrder.Count)
            {
                var (row, column) = IconRowLayout.Locate(rows, index);
                container.IconRowCounts = IconRowLayout.Insert(rows, columns, new(row, column)).Rows;
            }
            else container.IconRowCounts = IconRowLayout.Normalize(rows, columns, container.ItemOrder.Count + 1);
        }
        organizer.ContainerOrganizerId = container.Id;
        container.ItemOrder.Insert(index, key);
        return new(true, OrganizerContainmentFailure.None, previousContainerId);
    }

    internal static bool IsAncestor(
        IReadOnlyList<OrganizerDefinition> organizers,
        Guid ancestorId,
        Guid descendantId)
    {
        if (ancestorId == descendantId) return true;
        var visited = new HashSet<Guid>();
        Guid? currentId = descendantId;
        while (currentId is Guid id && visited.Add(id))
        {
            OrganizerDefinition? current = organizers.FirstOrDefault(candidate => candidate.Id == id);
            if (current is null) return false;
            if (current.ContainerOrganizerId == ancestorId) return true;
            currentId = current.ContainerOrganizerId;
        }
        return false;
    }

    internal static IReadOnlyList<Guid> GetAncestorIds(
        IReadOnlyList<OrganizerDefinition> organizers,
        Guid organizerId)
    {
        var result = new List<Guid>();
        var visited = new HashSet<Guid>();
        Guid? currentId = organizerId;
        while (currentId is Guid id && visited.Add(id))
        {
            OrganizerDefinition? current = organizers.FirstOrDefault(candidate => candidate.Id == id);
            if (current?.ContainerOrganizerId is not Guid parentId) break;
            result.Add(parentId);
            currentId = parentId;
        }
        return result;
    }

    internal static Guid? Detach(IReadOnlyList<OrganizerDefinition> organizers, Guid organizerId)
    {
        OrganizerDefinition? organizer = organizers.FirstOrDefault(candidate => candidate.Id == organizerId);
        if (organizer is null) return null;

        Guid? previousContainerId = organizer.ContainerOrganizerId;
        string key = ItemKey(organizer.Id);
        foreach (OrganizerDefinition candidate in organizers)
            OrganizerIconRows.RemoveItem(candidate, key);
        organizer.ContainerOrganizerId = null;
        return previousContainerId;
    }

    internal static IReadOnlyList<OrganizerDefinition> ReleaseDirectChildren(
        IReadOnlyList<OrganizerDefinition> organizers,
        Guid containerId)
    {
        IReadOnlyList<OrganizerDefinition> children = GetDirectChildren(organizers, containerId);
        foreach (OrganizerDefinition child in children) Detach(organizers, child.Id);
        return children;
    }
}
