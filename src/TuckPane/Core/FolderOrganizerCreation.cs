using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane.Core;

internal sealed class FolderOrganizerCreation
{
    internal const string Argument = "--create-organizer-in";
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal static bool IsRequest(IEnumerable<string> arguments) =>
        arguments.Contains(Argument, StringComparer.OrdinalIgnoreCase);

    internal static string ParseArguments(string[] arguments)
    {
        if (arguments.Length != 2 || !string.Equals(arguments[0], Argument, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(arguments[1]) || !Path.IsPathFullyQualified(arguments[1]))
            throw new ArgumentException(AppStrings.Get("FolderMenuInvalidArguments"));
        return arguments[1];
    }

    internal static string BindExistingStorage(OrganizerDefinition draft, string path,
        IEnumerable<OrganizerDefinition> organizers)
    {
        string normalized = AppPaths.ValidateCustomStoragePath(path);
        ValidateUnoccupiedStorage(normalized, organizers);
        draft.StorageRelativePath = string.Empty;
        draft.StorageAbsolutePath = normalized;
        draft.StorageOwnedByApp = false;
        return normalized;
    }

    private static void ValidateUnoccupiedStorage(string normalized, IEnumerable<OrganizerDefinition> organizers)
    {
        foreach (OrganizerDefinition organizer in organizers)
        {
            string occupied = AppPaths.ResolveStoragePath(organizer);
            if (string.Equals(normalized, occupied, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(AppStrings.Format("FolderMenuAlreadyExistsFormat", normalized, organizer.Name, occupied));
            if (AppPaths.PathsOverlap(normalized, occupied))
                throw new InvalidOperationException(AppStrings.Format("FolderMenuOverlapFormat", normalized, organizer.Name, occupied));
        }
    }

    // Both argument parsing and creation run inside this boundary; presenting an error never retries creation.
    internal static async Task RunWithFeedbackAsync(Func<Task> create, Func<string, Task> present,
        Action<Exception> log, Action<string> fallback)
    {
        try { await create(); }
        catch (Exception failure)
        {
            log(failure);
            try { await present(failure.Message); }
            catch (Exception presentationFailure)
            {
                log(presentationFailure);
                fallback(failure.Message);
            }
        }
    }

    internal async Task CreateAsync(string? storagePath, GlobalSettings settings,
        IReadOnlyCollection<OrganizerDefinition> organizers, Func<OrganizerDefinition, string?, Task> create)
    {
        await _gate.WaitAsync();
        try
        {
            string? normalized = storagePath is null ? null : AppPaths.ValidateCustomStoragePath(storagePath);
            if (normalized is not null) ValidateUnoccupiedStorage(normalized, organizers);
            var draft = new OrganizerDefinition
            {
                Name = normalized is null ? AppStrings.DefaultOrganizerName : Path.GetFileName(normalized),
                PlacementMode = OrganizerPlacementMode.Floating,
                Layout = new OrganizerLayout { Mode = OrganizerLayoutMode.Grid, Rows = 3, Columns = 3 },
                ExpandedContentMode = OrganizerExpandedContentMode.Icon,
                CompactScale = settings.ResolveCompactScale(OrganizerPlacementMode.Floating, OrganizerLimits.DefaultCompactScale)
            };
            // The existing host owns persistence, rollback and collapsed window placement.
            await create(draft, normalized);
        }
        finally { _gate.Release(); }
    }
}
