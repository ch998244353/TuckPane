using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace TuckPane.Services;

internal enum FolderContextMenuStatus { Disabled, Enabled, Broken, OtherCopy }

internal sealed record FolderContextMenuState(FolderContextMenuStatus Status, string? Executable);

// Narrow storage seam: tests never touch Explorer's registration or the user's preferences.
internal interface IFolderContextMenuStore
{
    IReadOnlyDictionary<string, object>? Read(string key);
    void Write(string key, IReadOnlyDictionary<string, object> values);
    void Delete(string key);
}

internal sealed class FolderContextMenuService
{
    internal const string VerbKey = @"Software\Classes\Directory\shell\TuckPane.CreateOrganizerHere";
    internal const string CommandKey = VerbKey + @"\command";
    internal const string PreferenceKey = @"Software\TuckPane\FolderContextMenu";
    internal const string DesktopVerbKey = @"Software\Classes\DesktopBackground\Shell\TuckPane.CreateOrganizer";
    internal const string DesktopPreferenceKey = @"Software\TuckPane\DesktopContextMenu";
    private readonly bool _desktop;
    private string VerbPath => _desktop ? DesktopVerbKey : VerbKey;
    private string CommandPath => VerbPath + @"\command";
    private string PreferencePath => _desktop ? DesktopPreferenceKey : PreferenceKey;
    private readonly IFolderContextMenuStore _store;
    private readonly Func<string, bool> _fileExists;
    private readonly Action _notifyShell;
    internal string Executable { get; }

    internal FolderContextMenuService(IFolderContextMenuStore store, string executable,
        Func<string, bool> fileExists, Action notifyShell, bool desktop = false)
    {
        _store = store;
        Executable = Path.GetFullPath(executable);
        _fileExists = fileExists;
        _notifyShell = notifyShell;
        _desktop = desktop;
    }

    internal static FolderContextMenuService CreateForCurrentProcess(bool desktop = false) => new(
        new CurrentUserFolderContextMenuStore(),
        Path.Combine(AppContext.BaseDirectory, "TuckPane.exe"), File.Exists, NotifyShell, desktop);

    internal static string BuildCommand(string executable, bool desktop = false) => desktop
        ? $"\"{Path.GetFullPath(executable)}\" --create-organizer"
        : $"\"{Path.GetFullPath(executable)}\" --create-organizer-in \"%1\"";

    internal static string? ReadCommandExecutable(string? command, bool desktop = false)
    {
        if (string.IsNullOrEmpty(command) || command[0] != '"') return null;
        int end = command.IndexOf('"', 1);
        if (end < 2) return null;
        string executable = command[1..end];
        try
        {
            return Path.IsPathFullyQualified(executable) &&
                string.Equals(command, BuildCommand(executable, desktop), StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(executable) : null;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    // Startup only; explicit enable/repair in settings is the ownership takeover.
    internal void InitializeDesktop(string label, bool installedCopy)
    {
        if (!_desktop) return;
        if (installedCopy && _store.Read(PreferencePath) is null && _store.Read(VerbPath) is null)
            Enable(label);
        else
            RepairIfOwned(label);
        UpdateLabelIfOwned(label);
    }

    internal static bool IsInstalledCopy()
    {
        if (AppPaths.IsTestMode) return false;
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{2B7D4C50-0148-4D5C-A097-D8D7E5C64FCB}_is1");
        string? location = key?.GetValue("InstallLocation") as string;
        return location is not null && string.Equals(
            Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    internal FolderContextMenuState ReadState()
    {
        var verb = _store.Read(VerbPath);
        var preference = _store.Read(PreferencePath);
        string? target = ReadCommandExecutable(Text(_store.Read(CommandPath), ""), _desktop);
        if (verb is null)
            return new(EnabledPreference(preference) ? FolderContextMenuStatus.Broken : FolderContextMenuStatus.Disabled,
                Text(preference, "OwnerExecutable"));
        if (target is null || !_fileExists(target) || (!_desktop && Text(verb, "MultiSelectModel") != "Single") ||
            string.IsNullOrWhiteSpace(Text(verb, "")))
            return new(FolderContextMenuStatus.Broken, target ?? Text(verb, "OwnerExecutable"));
        return new(SameExecutable(target, Executable) ? FolderContextMenuStatus.Enabled : FolderContextMenuStatus.OtherCopy, target);
    }

    internal bool RepairIfOwned(string label)
    {
        var preference = _store.Read(PreferencePath);
        if (!EnabledPreference(preference) || !SameExecutable(Text(preference, "OwnerExecutable"), Executable))
            return false;
        var verb = _store.Read(VerbPath);
        string? target = ReadCommandExecutable(Text(_store.Read(CommandPath), ""), _desktop);
        // A valid command or surviving ownership marker from another copy wins.
        if (target is not null && !SameExecutable(target, Executable)) return false;
        if (verb is not null && !SameExecutable(Text(verb, "OwnerExecutable"), Executable)) return false;
        if (ReadState().Status == FolderContextMenuStatus.Enabled) return false;
        Enable(label);
        return true;
    }

    internal void Enable(string label)
    {
        if (!_fileExists(Executable)) throw new FileNotFoundException(AppStrings.Get("FolderMenuExecutableMissing"), Executable);
        Change(() =>
        {
            _store.Write(VerbPath, new Dictionary<string, object>
            {
                [""] = label, ["Icon"] = $"\"{Executable}\",0", ["MultiSelectModel"] = "Single",
                ["OwnerExecutable"] = Executable
            });
            _store.Write(CommandPath, new Dictionary<string, object> { [""] = BuildCommand(Executable, _desktop) });
            _store.Write(PreferencePath, new Dictionary<string, object> { ["Enabled"] = 1, ["OwnerExecutable"] = Executable });
        });
    }

    internal void Disable()
    {
        var verb = _store.Read(VerbPath);
        string? target = ReadCommandExecutable(Text(_store.Read(CommandPath), ""), _desktop);
        // A missing/corrupt command may be cleaned only when our ownership marker survives.
        if (verb is not null && !SameExecutable(target ?? Text(verb, "OwnerExecutable"), Executable))
            throw new InvalidOperationException(AppStrings.Get("FolderMenuOtherCopy"));
        Change(() =>
        {
            _store.Delete(VerbPath);
            _store.Write(PreferencePath, new Dictionary<string, object> { ["Enabled"] = 0, ["OwnerExecutable"] = Executable });
        });
    }

    internal void UpdateLabelIfOwned(string label)
    {
        if (ReadState().Status != FolderContextMenuStatus.Enabled) return;
        var verb = _store.Read(VerbPath)!;
        if (Text(verb, "") == label) return;
        Change(() => _store.Write(VerbPath, new Dictionary<string, object>(verb) { [""] = label }));
    }

    private void Change(Action action)
    {
        string[] keys = [VerbPath, CommandPath, PreferencePath];
        var snapshots = keys.Select(key => (Key: key, Values: _store.Read(key))).ToArray();
        try { action(); }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var snapshot in snapshots)
            {
                try
                {
                    if (snapshot.Values is null) _store.Delete(snapshot.Key);
                    else _store.Write(snapshot.Key, snapshot.Values);
                }
                catch (Exception rollback) { failures.Add(rollback); }
            }
            if (failures.Count > 1) throw new AggregateException(failures);
            throw;
        }
        _notifyShell();
    }

    private static bool EnabledPreference(IReadOnlyDictionary<string, object>? values) =>
        values is not null && values.TryGetValue("Enabled", out object? value) && value is int enabled && enabled == 1;
    private static string? Text(IReadOnlyDictionary<string, object>? values, string name) =>
        values is not null && values.TryGetValue(name, out object? value) ? value as string : null;
    private static bool SameExecutable(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}

internal sealed class CurrentUserFolderContextMenuStore : IFolderContextMenuStore
{
    public IReadOnlyDictionary<string, object>? Read(string key)
    {
        if (AppPaths.IsTestMode) return null;
        using RegistryKey? registry = Registry.CurrentUser.OpenSubKey(key);
        return registry?.GetValueNames().ToDictionary(name => name,
            name => registry.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, StringComparer.OrdinalIgnoreCase);
    }

    public void Write(string key, IReadOnlyDictionary<string, object> values)
    {
        if (AppPaths.IsTestMode) throw new InvalidOperationException("Shell registration is disabled in test mode.");
        using RegistryKey registry = Registry.CurrentUser.CreateSubKey(key, writable: true);
        foreach (string name in registry.GetValueNames())
            if (!values.ContainsKey(name)) registry.DeleteValue(name, throwOnMissingValue: false);
        foreach (var value in values)
            registry.SetValue(value.Key, value.Value, value.Value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
    }

    public void Delete(string key)
    {
        if (AppPaths.IsTestMode) throw new InvalidOperationException("Shell registration is disabled in test mode.");
        Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
    }
}
