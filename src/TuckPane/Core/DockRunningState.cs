namespace TuckPane.Core;

internal enum DockIdentityKind { Unknown, Executable, AppId, Folder }
internal readonly record struct DockItemIdentity(DockIdentityKind Kind, string Value);
internal readonly record struct DockOpenWindow(string? Executable, string? AppId, bool Visible, bool Cloaked = false);

internal static class DockRunningState
{
    // An argument-bearing shortcut can target a document, tab, game or folder.
    // Without a content-specific adapter its host process cannot prove it is open.
    internal static bool CanMatchExecutableShortcut(string? arguments) => string.IsNullOrWhiteSpace(arguments);

    internal static bool HasOpenPathChanges(IReadOnlyDictionary<string, bool> before, IReadOnlyDictionary<string, bool> after)
    {
        // Missing paths and explicit false values have the same visual state.
        foreach (var (path, open) in before)
            if (open && (!after.TryGetValue(path, out bool current) || !current)) return true;
        foreach (var (path, open) in after)
            if (open && (!before.TryGetValue(path, out bool previous) || !previous)) return true;
        return false;
    }

    internal static bool IsOpen(DockItemIdentity identity, IReadOnlyList<DockOpenWindow> windows,
        IReadOnlyCollection<string> folders)
    {
        if (identity.Kind == DockIdentityKind.Unknown || string.IsNullOrEmpty(identity.Value)) return false;
        if (identity.Kind == DockIdentityKind.Folder)
            return folders.Any(path => SamePath(path, identity.Value));
        return windows.Any(window => window.Visible && !window.Cloaked && (identity.Kind switch
        {
            DockIdentityKind.Executable => SamePath(window.Executable, identity.Value),
            DockIdentityKind.AppId => string.Equals(window.AppId, identity.Value, StringComparison.OrdinalIgnoreCase),
            _ => false
        }));
    }

    private static bool SamePath(string? a, string b) => !string.IsNullOrEmpty(a) &&
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

// UI-thread owned generation; worker results only commit against the unchanged subscription set.
internal sealed class DockSnapshotVersion
{
    internal long Current { get; private set; }
    internal long Invalidate() => ++Current;
    internal bool Accepts(long version) => version == Current;
}
