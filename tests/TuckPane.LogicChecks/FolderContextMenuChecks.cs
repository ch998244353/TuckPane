using TuckPane;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class FolderContextMenuChecks
{
    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-folder-menu-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            CheckArguments();
            CheckRegistration();
            await CheckCreationAsync(root);
            CheckInstaller();
            Console.WriteLine("PASS --folder-context-menu: arguments, registration/ownership/repair/rollback, defaults, serialized creation and installer contracts. No UI or real registry changes.");
        }
        finally
        {
            // The only deletion is the freshly created, uniquely named test directory.
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckArguments()
    {
        foreach (string folder in new[] { @"C:\资料 & 工作\项目", @"D:\with spaces\folder", @"D:\資料\子目录\" })
        {
            Require(FolderOrganizerCreation.ParseArguments([FolderOrganizerCreation.Argument, folder]) == folder,
                "Folder path must remain one unchanged argument.");
        }
        string exe = Path.Combine(AppContext.BaseDirectory, "TuckPane.exe");
        string command = FolderContextMenuService.BuildCommand(exe);
        string path = @"D:\资料 & 工作\One folder";
        Require(command == $"\"{exe}\" --create-organizer-in \"%1\"", "Command must quote both paths and invoke the EXE directly.");
        string[] redirected = App.ParseRedirectedArguments(command.Replace("%1", path));
        Require(FolderOrganizerCreation.ParseArguments(redirected) == path, "Redirected canonical EXE argument was not removed.");
        string alias = Path.Combine(AppContext.BaseDirectory, "00-启动 TuckPane.exe");
        Require(FolderOrganizerCreation.ParseArguments(App.ParseRedirectedArguments($"\"{alias}\" --create-organizer-in \"{path}\"")) == path,
            "Portable alias redirection lost the folder.");
        foreach (string otherCopy in new[] { @"C:\Other install\TuckPane.exe", @"D:\另一份\00-启动 TuckPane.exe" })
            Require(FolderOrganizerCreation.ParseArguments(App.ParseRedirectedArguments($"\"{otherCopy}\" --create-organizer-in \"{path}\"")) == path,
                "A redirected launch from a different copy must strip its EXE argument.");
        Expect<ArgumentException>(() => FolderOrganizerCreation.ParseArguments(
            App.ParseRedirectedArguments($"\"C:\\Other app.exe\" --create-organizer-in \"{path}\"")));
        foreach (string[] invalid in new string[][]
        {
            [], [FolderOrganizerCreation.Argument], [FolderOrganizerCreation.Argument, ""],
            [FolderOrganizerCreation.Argument, "relative"], [FolderOrganizerCreation.Argument, path, "extra"],
            ["--create-note-in", FolderOrganizerCreation.Argument, path],
            [FolderOrganizerCreation.Argument, path, ""]
        }) Expect<ArgumentException>(() => FolderOrganizerCreation.ParseArguments(invalid));
        Require(FolderOrganizerCreation.IsRequest(["--CREATE-ORGANIZER-IN", path]), "Switch is case insensitive.");
    }

    private static void CheckRegistration()
    {
        var store = new MemoryStore();
        string installed = @"C:\Apps\TuckPane.exe", portable = @"D:\便携 & Apps\TuckPane.exe";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { installed, portable };
        int notifications = 0;
        var first = new FolderContextMenuService(store, installed, files.Contains, () => notifications++);
        var second = new FolderContextMenuService(store, portable, files.Contains, () => notifications++);
        Require(first.ReadState().Status == FolderContextMenuStatus.Disabled, "New portable copy must default off.");
        first.UpdateLabelIfOwned("中文");
        Require(store.Values.Count == 0, "Startup must not register a disabled menu.");
        first.Enable("使用 TuckPane 创建收纳窗");
        first.Enable("使用 TuckPane 创建收纳窗");
        Require(store.Values.Count == 3 && first.ReadState().Status == FolderContextMenuStatus.Enabled, "Repeated enable must be idempotent.");
        Require((string)store.Read(FolderContextMenuService.VerbKey)!["MultiSelectModel"] == "Single", "Menu must be single-select.");
        Require((string)store.Read(FolderContextMenuService.VerbKey)!["Icon"] == $"\"{installed}\",0", "Icon must use quoted executable.");
        first.UpdateLabelIfOwned("Create organizer with TuckPane");
        Require((string)store.Read(FolderContextMenuService.VerbKey)![""] == "Create organizer with TuckPane", "Language change did not update label.");
        second.UpdateLabelIfOwned("Other language");
        Require(first.ReadState().Status == FolderContextMenuStatus.Enabled, "Another copy's startup stole the registration.");
        Require(second.ReadState().Status == FolderContextMenuStatus.OtherCopy, "Settings must expose another copy's ownership.");
        Expect<InvalidOperationException>(second.Disable);

        files.Remove(installed);
        Require(second.ReadState().Status == FolderContextMenuStatus.Broken, "Moved/deleted executable must be repairable.");
        second.Enable("Repair");
        Require(second.ReadState().Status == FolderContextMenuStatus.Enabled, "Repair must bind the current copy.");
        Expect<InvalidOperationException>(first.Disable);
        second.Disable();
        second.Disable();
        Require(store.Values.Count == 1 && second.ReadState().Status == FolderContextMenuStatus.Disabled &&
            (int)store.Read(FolderContextMenuService.PreferenceKey)!["Enabled"] == 0, "Disable must persist the explicit off choice.");
        second.UpdateLabelIfOwned("No automatic re-enable");
        Require(second.ReadState().Status == FolderContextMenuStatus.Disabled, "Language refresh re-enabled the menu.");

        // A failed write must restore the disabled preference, without leaving a partial verb.
        store.FailWriteAfter = 1;
        Expect<IOException>(() => second.Enable("Fail"));
        Require(store.Values.Count == 1 && second.ReadState().Status == FolderContextMenuStatus.Disabled, "Partial registration survived rollback.");
        second.Enable("Good");
        store.FailWriteAfter = 2;
        files.Add(installed);
        Expect<IOException>(() => first.Enable("Failed takeover"));
        Require(second.ReadState().Status == FolderContextMenuStatus.Enabled &&
            (string)store.Read(FolderContextMenuService.VerbKey)![""] == "Good", "Rollback lost the previous owner.");
        files.Remove(portable);
        Expect<FileNotFoundException>(() => second.Enable("Missing exe"));
        Require(notifications > 0, "Registration changes must notify Shell.");
    }

    private static async Task CheckCreationAsync(string root)
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, "资料 & 工作")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(folder, "Child")).FullName;
        string file = Path.Combine(folder, "keep.txt");
        await File.WriteAllTextAsync(file, "unchanged");
        var creator = new FolderOrganizerCreation();
        var organizers = new List<OrganizerDefinition>();
        var settings = new GlobalSettings();
        OrganizerDefinition? draft = null;
        Task Capture(OrganizerDefinition value, string? path)
        {
            Require(path == folder, "Creation must use the original directory.");
            draft = value;
            return Task.CompletedTask;
        }
        await creator.CreateAsync(folder, settings, organizers, Capture);
        Require(draft is { Name: "资料 & 工作", PlacementMode: OrganizerPlacementMode.Floating,
            ExpandedContentMode: OrganizerExpandedContentMode.Icon, Layout: { Mode: OrganizerLayoutMode.Grid, Rows: 3, Columns: 3 },
            ExpandedPosition: null, ContainerOrganizerId: null }, "Shell draft defaults changed.");
        Require(draft!.CompactScale == OrganizerLimits.DefaultCompactScale, "Uniform-off must use default entry size.");
        settings.UseUniformFloatingCompactScale = true;
        settings.UniformFloatingCompactScale = 2.25;
        await creator.CreateAsync(folder, settings, organizers, Capture);
        Require(draft!.CompactScale == 2.25, "Uniform-on must use configured floating size.");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbacks = 0;
        Task first = creator.CreateAsync(folder, settings, organizers, async (value, path) =>
        {
            callbacks++;
            entered.SetResult();
            await release.Task;
            value.StorageAbsolutePath = path;
            organizers.Add(value);
        });
        await entered.Task;
        Task repeated = creator.CreateAsync(folder, settings, organizers, (_, _) => { callbacks++; return Task.CompletedTask; });
        Require(!repeated.IsCompleted && callbacks == 1, "Concurrent request bypassed the gate.");
        release.SetResult();
        await first;
        await ExpectAsync<InvalidOperationException>(() => repeated);
        Require(callbacks == 1 && organizers.Count == 1, "Repeated folder created a duplicate.");
        await ExpectAsync<InvalidOperationException>(() => creator.CreateAsync(child, settings, organizers, Capture));
        await ExpectAsync<InvalidOperationException>(() => creator.CreateAsync(root, settings, organizers, Capture));
        organizers.Clear();
        organizers.AddRange(Enumerable.Range(0, 15).Select(_ => new OrganizerDefinition
        { StorageAbsolutePath = Path.Combine(root, "Other" + Guid.NewGuid().ToString("N")) }));
        await creator.CreateAsync(folder, settings, organizers, Capture);
        organizers.Clear();
        await ExpectAsync<IOException>(() => creator.CreateAsync(folder, settings, organizers, (_, _) => throw new IOException("save failed")));
        await creator.CreateAsync(folder, settings, organizers, Capture);
        await ExpectAsync<DirectoryNotFoundException>(() => creator.CreateAsync(Path.Combine(root, "missing"), settings, organizers, Capture));
        Require(await File.ReadAllTextAsync(file) == "unchanged" && organizers.Count == 0, "Failure changed the source or retained a draft.");
    }

    private static void CheckInstaller()
    {
        string script = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "installer", "TuckPane.iss"));
        Require(!script.Split('\n').Any(line => line.Contains("Subkey: \"Software\\Classes\\Directory\\Shell\\TuckPane.CreateOrganizerHere\"", StringComparison.OrdinalIgnoreCase)),
            "Legacy unconditional deletion/uninstall would erase the new registration.");
        Require(script.Contains("if Enabled = 0 then Exit;") && script.Contains("CompareText(Command, FolderMenuCommand) = 0") &&
            script.Contains("CompareText(Owner, ExpandConstant('{app}\\TuckPane.exe')) = 0") &&
            script.Contains("else if Command = '' then begin") && script.Contains("RegQueryStringValue(HKCU, FolderMenuKey, 'OwnerExecutable', Owner)"),
            "Installer must preserve off preferences and check ownership before uninstalling.");
        Require(script.Contains("PrivilegesRequired=lowest") && script.Contains("--create-organizer-in \"%1\""),
            "Installer must use the same current-user command contract.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class MemoryStore : IFolderContextMenuStore
    {
        internal Dictionary<string, IReadOnlyDictionary<string, object>> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal int FailWriteAfter { get; set; } = -1;
        public IReadOnlyDictionary<string, object>? Read(string key) => Values.GetValueOrDefault(key);
        public void Write(string key, IReadOnlyDictionary<string, object> values)
        {
            if (FailWriteAfter >= 0 && FailWriteAfter-- == 0) throw new IOException("Simulated write failure");
            Values[key] = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase);
        }
        public void Delete(string key)
        {
            foreach (string candidate in Values.Keys.Where(candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToArray()) Values.Remove(candidate);
        }
    }
}
