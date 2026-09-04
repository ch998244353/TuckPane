namespace TuckPane.Core;

using TuckPane.Models;

internal enum OrganizerContainmentFailure
{
    None,
    MissingOrganizer,
    SameOrganizer,
    StationCannotBeContained,
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

    internal OrganizerContainmentSnapshot(IReadOnlyList<OrganizerDefinition> organizers)
    {
        _containers = organizers.ToDictionary(candidate => candidate.Id, candidate => candidate.ContainerOrganizerId);
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
        int insertionIndex)
    {
        OrganizerDefinition? organizer = organizers.FirstOrDefault(candidate => candidate.Id == organizerId);
        OrganizerDefinition? container = organizers.FirstOrDefault(candidate => candidate.Id == containerId);
        if (organizer is null || container is null)
            return new(false, OrganizerContainmentFailure.MissingOrganizer, null);
        if (organizer.Id == container.Id)
            return new(false, OrganizerContainmentFailure.SameOrganizer, organizer.ContainerOrganizerId);
        if (organizer.PlacementMode == OrganizerPlacementMode.Station)
            return new(false, OrganizerContainmentFailure.StationCannotBeContained, organizer.ContainerOrganizerId);
        if (IsAncestor(organizers, organizer.Id, container.Id))
            return new(false, OrganizerContainmentFailure.TargetIsDescendant, organizer.ContainerOrganizerId);

        string key = ItemKey(organizer.Id);
        int previousIndex = organizer.ContainerOrganizerId == container.Id
            ? container.ItemOrder.FindIndex(item => item.Equals(key, StringComparison.OrdinalIgnoreCase))
            : -1;
        Guid? previousContainerId = organizer.ContainerOrganizerId;
        foreach (OrganizerDefinition candidate in organizers)
            candidate.ItemOrder.RemoveAll(item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (previousIndex >= 0 && insertionIndex > previousIndex) insertionIndex--;
        organizer.ContainerOrganizerId = container.Id;
        container.ItemOrder.Insert(Math.Clamp(insertionIndex, 0, container.ItemOrder.Count), key);
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
            candidate.ItemOrder.RemoveAll(item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
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
