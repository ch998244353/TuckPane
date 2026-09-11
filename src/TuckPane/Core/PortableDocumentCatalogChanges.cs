namespace TuckPane.Core;

internal static class PortableDocumentCatalogChanges
{
    internal const string TemporaryPrefix = ".tuckpane-document-";

    internal static bool IsInternalTemporary(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.StartsWith(TemporaryPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(name[TemporaryPrefix.Length..^4], "N", out _)) return true;
        int replace = name.LastIndexOf("~RF", StringComparison.OrdinalIgnoreCase);
        if (replace < 0 || !IsPortable(name[..replace]) || !name.EndsWith(".TMP", StringComparison.OrdinalIgnoreCase)) return false;
        ReadOnlySpan<char> id = name.AsSpan(replace + 3, name.Length - replace - 7);
        return id.Length > 0 && id.Length <= 16 && id.IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    }

    internal static bool ShouldRefresh(WatcherChangeTypes change, string? name, string? oldName,
        IReadOnlySet<string> knownPortableNames, bool catalogReadInProgress = false)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (IsInternalTemporary(name))
            return change == WatcherChangeTypes.Renamed && !string.IsNullOrEmpty(oldName) &&
                !IsInternalTemporary(oldName) &&
                !(name.Contains("~RF", StringComparison.OrdinalIgnoreCase) && knownPortableNames.Contains(oldName));
        if (!knownPortableNames.Contains(name)) return true;
        // Portable icons/names do not depend on their body, theme or completion data.
        if (change == WatcherChangeTypes.Changed) return false;
        // A scan may already have observed a deletion. Do not let its old name snapshot hide a recreation.
        if (change == WatcherChangeTypes.Renamed && IsInternalTemporary(oldName)) return catalogReadInProgress;
        return true; // Real deletion or renaming must still reconcile the catalog.
    }

    private static bool IsPortable(string name) => name.EndsWith(".tucknote", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tucktodo", StringComparison.OrdinalIgnoreCase);
}
