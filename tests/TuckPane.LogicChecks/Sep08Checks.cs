using TuckPane;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class Sep08Checks
{
    internal static async Task RunAsync(string area)
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-sep08-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            switch (area)
            {
                case "organizer-create-feedback":
                    await CheckOrganizerCreateFeedbackAsync(root);
                    Console.WriteLine("PASS --organizer-create-feedback: parent/child and duplicate conflicts report context once; missing-directory/argument errors, presenter-failure fallback and success without feedback. No UI or real registry changes.");
                    break;
                case "folder-create":
                    CheckOwnedMenuRepair();
                    await CheckActivationInboxAsync();
                    await CheckExistingFolderBindingAsync(root);
                    Console.WriteLine("PASS --sep08-fixes folder-create: owned registration repair, ordered exactly-once activation draining, original-folder binding and conflicts. No UI or real registry changes.");
                    break;
                case "organizer-rename":
                    await CheckOrganizerRenameAsync(root);
                    Console.WriteLine("PASS --sep08-fixes organizer-rename: duplicate/filename-invalid names persist; absolute/relative paths, ownership, files and order stay unchanged; blank input is rejected and failed-save rollback restores runtime and disk. No UI interaction.");
                    break;
                case "note-input":
                    await CheckNoteSaveSequenceAsync(root);
                    await CheckNoteCatalogChangesAsync(root);
                    break;
                case "todo-separators":
                    await CheckTodoSeparatorsAsync(root);
                    Console.WriteLine("PASS --sep08-fixes todo-separators: missing-field compatibility, immediate two-subscriber synchronization, real state reload and failed-save rollback. No UI interaction.");
                    break;
                default:
                    throw new ArgumentException($"Unknown Sep 08 check: {area}.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            // Only this invocation's absolute, freshly created temp tree may be removed.
            string fullRoot = Path.GetFullPath(root);
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(fullRoot), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).StartsWith("TuckPane-sep08-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the isolated test directory.");
            Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static void CheckOwnedMenuRepair()
    {
        const string current = @"C:\TuckPane installed\TuckPane.exe";
        const string other = @"D:\TuckPane portable\TuckPane.exe";
        var store = new MemoryMenuStore();
        int notifications = 0;
        var service = new FolderContextMenuService(store, current, _ => true, () => notifications++);
        store.Write(FolderContextMenuService.PreferenceKey,
            new Dictionary<string, object> { ["Enabled"] = 1, ["OwnerExecutable"] = current });
        Require(service.RepairIfOwned("Create organizer"), "Enabled owner preference did not restore the missing menu.");
        string command = (string)store.Read(FolderContextMenuService.CommandKey)![""];
        const string folder = @"D:\资料 & 工作\One folder";
        Require(FolderOrganizerCreation.ParseArguments(App.ParseRedirectedArguments(command.Replace("%1", folder))) == folder,
            "Repaired command lost its quoted absolute folder argument.");
        Require(service.ReadState().Status == FolderContextMenuStatus.Enabled && !service.RepairIfOwned("Create organizer") && notifications == 1,
            "Healthy repair must be idempotent and must notify Shell only once.");

        service.Disable();
        Require(!service.RepairIfOwned("Create organizer") && store.Read(FolderContextMenuService.VerbKey) is null,
            "Startup repair ignored an explicit disabled preference.");
        store.Write(FolderContextMenuService.PreferenceKey,
            new Dictionary<string, object> { ["Enabled"] = 1, ["OwnerExecutable"] = other });
        Require(!service.RepairIfOwned("Create organizer") && store.Read(FolderContextMenuService.VerbKey) is null,
            "Startup repair took ownership of another copy's missing registration.");

        var otherService = new FolderContextMenuService(store, other, _ => true, () => notifications++);
        otherService.Enable("Other copy");
        store.Write(FolderContextMenuService.PreferenceKey,
            new Dictionary<string, object> { ["Enabled"] = 1, ["OwnerExecutable"] = current });
        Require(!service.RepairIfOwned("Create organizer") &&
            (string)store.Read(FolderContextMenuService.CommandKey)![""] == FolderContextMenuService.BuildCommand(other),
            "A stale current preference overwrote another copy's surviving command.");
    }

    private static async Task CheckActivationInboxAsync()
    {
        var inbox = new ActivationInbox<int>();
        var scheduled = new Queue<Func<Task>>();
        var handled = new List<int>();
        var errors = new List<Exception>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inbox.Enqueue(1);
        inbox.Enqueue(2);
        inbox.Start(async value =>
        {
            handled.Add(value);
            if (value == 1) { entered.SetResult(); await release.Task; }
            if (value == 2) throw new IOException("One bad activation");
        }, action => { scheduled.Enqueue(action); return true; }, errors.Add);
        Require(scheduled.Count == 1, "Startup activations must share one dispatcher drain.");
        Task drain = scheduled.Dequeue()();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        inbox.Enqueue(3);
        Require(scheduled.Count == 0 && handled.SequenceEqual([1]), "Activation handling ran concurrently while the first request awaited.");
        release.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        inbox.Enqueue(4);
        Require(scheduled.Count == 1, "The idle inbox did not schedule its next activation.");
        await scheduled.Dequeue()().WaitAsync(TimeSpan.FromSeconds(5));
        Require(handled.SequenceEqual([1, 2, 3, 4]) && errors is [IOException] && scheduled.Count == 0,
            "Activations were lost, duplicated, reordered, or stopped by a failed request.");
    }

    private static async Task CheckExistingFolderBindingAsync(string root)
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, "资料 & 工作")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(folder, "Child")).FullName;
        string file = Path.Combine(folder, "keep.txt");
        await File.WriteAllTextAsync(file, "unchanged");
        var draft = new OrganizerDefinition { StorageRelativePath = "stale", StorageOwnedByApp = true };
        string bound = FolderOrganizerCreation.BindExistingStorage(draft, folder, []);
        Require(bound == folder && AppPaths.ResolveStoragePath(draft) == folder &&
            draft.StorageAbsolutePath == folder && draft.StorageRelativePath == string.Empty && !draft.StorageOwnedByApp,
            "Host storage binding must use the selected directory without creating an owned wrapper.");
        var rejected = new OrganizerDefinition { StorageRelativePath = "unchanged", StorageOwnedByApp = true };
        foreach (string conflict in new[] { folder, child, root })
            Expect<InvalidOperationException>(() => FolderOrganizerCreation.BindExistingStorage(rejected, conflict, [draft]));
        Expect<DirectoryNotFoundException>(() => FolderOrganizerCreation.BindExistingStorage(rejected, Path.Combine(root, "missing"), []));
        Require(rejected.StorageAbsolutePath is null && rejected.StorageRelativePath == "unchanged" && rejected.StorageOwnedByApp,
            "Rejected binding left partially changed organizer storage.");
        Require(await File.ReadAllTextAsync(file) == "unchanged" &&
            Directory.GetDirectories(root).SequenceEqual([folder]) && Directory.GetDirectories(folder).SequenceEqual([child]),
            "Binding or rejection moved source data or created a wrapper directory.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task CheckOrganizerCreateFeedbackAsync(string root)
    {
        string parent = Directory.CreateDirectory(Path.Combine(root, "Parent")).FullName;
        string occupied = Directory.CreateDirectory(Path.Combine(parent, "资料 & 工作")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(occupied, "Child")).FullName;
        string file = Path.Combine(occupied, "keep.txt");
        await File.WriteAllTextAsync(file, "unchanged");
        var existing = new OrganizerDefinition { Name = "已有关联窗口", StorageAbsolutePath = occupied };
        var creation = new FolderOrganizerCreation();
        var messages = new List<string>();
        var logged = new List<Exception>();
        var fallbacks = new List<string>();
        int created = 0;
        Task Create(string selected) => creation.CreateAsync(selected, new GlobalSettings(), [existing], (draft, path) =>
        {
            created++;
            FolderOrganizerCreation.BindExistingStorage(draft, path!, [existing]);
            return Task.CompletedTask;
        });
        Task Run(Func<Task> action, bool failPresentation = false)
        {
            messages.Clear(); logged.Clear(); fallbacks.Clear();
            return FolderOrganizerCreation.RunWithFeedbackAsync(action, message =>
            {
                messages.Add(message);
                if (failPresentation) throw new IOException("Injected presenter failure");
                return Task.CompletedTask;
            }, logged.Add, fallbacks.Add);
        }
        foreach (string selected in new[] { child, parent, occupied })
        {
            foreach (bool bind in new[] { false, true })
            {
                await Run(() =>
                {
                    if (!bind) return Create(selected);
                    FolderOrganizerCreation.BindExistingStorage(new OrganizerDefinition(), selected, [existing]);
                    return Task.CompletedTask;
                });
                Require(logged is [InvalidOperationException] && messages.Count == 1 && fallbacks.Count == 0 &&
                    messages[0].Contains(selected, StringComparison.Ordinal) && messages[0].Contains(existing.Name, StringComparison.Ordinal) &&
                    messages[0].Contains(occupied, StringComparison.Ordinal),
                    $"{(bind ? "BindExistingStorage" : "CreateAsync")} did not present the complete conflict exactly once: {selected}.");
            }
        }
        Require(created == 0 && await File.ReadAllTextAsync(file) == "unchanged", "Rejected creation changed the original files or invoked creation.");

        await Run(() => Create(Path.Combine(parent, "missing")));
        Require(logged is [DirectoryNotFoundException] && messages.Count == 1 && messages[0] == logged[0].Message && fallbacks.Count == 0,
            "A missing directory did not present its creation error exactly once.");
        await Run(() => { FolderOrganizerCreation.ParseArguments([FolderOrganizerCreation.Argument]); return Task.CompletedTask; });
        Require(logged is [ArgumentException] && messages.Count == 1 && messages[0] == logged[0].Message && fallbacks.Count == 0,
            "Argument parsing did not use the shared single-message error boundary.");

        string independent = Directory.CreateDirectory(Path.Combine(parent, "Independent")).FullName;
        await Run(() => creation.CreateAsync(independent, new GlobalSettings(), [existing], (_, _) =>
            throw new IOException("Injected creation save failure")), failPresentation: true);
        Require(logged is [IOException, IOException] && messages.SequenceEqual(["Injected creation save failure"]) && fallbacks.SequenceEqual(messages),
            "Presenter failure did not log both errors and fall back exactly once with the original creation reason.");
        await Run(() => Create(independent));
        Require(created == 1 && messages.Count == 0 && logged.Count == 0 && fallbacks.Count == 0 && await File.ReadAllTextAsync(file) == "unchanged",
            "An independent folder did not create successfully without an error presentation.");
    }
    private static async Task CheckOrganizerRenameAsync(string root)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "Original")).FullName;
        var external = new OrganizerDefinition { Name = "Original", StorageAbsolutePath = source, StorageOwnedByApp = false };
        var managed = new OrganizerDefinition { Name = "Managed", StorageRelativePath = "Managed", StorageOwnedByApp = true };
        var state = new AppStateV2 { Organizers = [external, managed] };
        string statePath = Path.Combine(root, "rename-state.json");
        var store = new StateStore(statePath);
        var originalPaths = state.Organizers.ToDictionary(item => item.Id,
            item => (item.StorageAbsolutePath, item.StorageRelativePath, item.StorageOwnedByApp, Resolved: AppPaths.ResolveStoragePath(item)));
        foreach (OrganizerDefinition definition in state.Organizers)
        {
            string directory = Directory.CreateDirectory(originalPaths[definition.Id].Resolved).FullName;
            Directory.CreateDirectory(Path.Combine(directory, "Nested"));
            await File.WriteAllTextAsync(Path.Combine(directory, "keep.txt"), "keep final content");
            definition.ItemOrder = ["keep.txt", "Nested"];
        }
        await store.SaveAsync(state);
        Task Persist() => store.SaveAsync(state);
        async Task RequireUnchangedStorageAsync()
        {
            AppStateV2 saved = await new StateStore(statePath).LoadAsync();
            foreach (OrganizerDefinition definition in state.Organizers.Concat(saved.Organizers))
            {
                var original = originalPaths[definition.Id];
                Require(definition.StorageAbsolutePath == original.StorageAbsolutePath &&
                    definition.StorageRelativePath == original.StorageRelativePath && definition.StorageOwnedByApp == original.StorageOwnedByApp &&
                    definition.ItemOrder.SequenceEqual(["keep.txt", "Nested"]) &&
                    Directory.Exists(Path.Combine(original.Resolved, "Nested")) &&
                    await File.ReadAllTextAsync(Path.Combine(original.Resolved, "keep.txt")) == "keep final content",
                    "Display-name change modified original storage paths, ownership, order, or files.");
            }
        }

        const string name = "资料: / 文档?";
        foreach (OrganizerDefinition definition in state.Organizers)
            await OrganizerNameChange.RenameAsync(definition, "  " + name + "  ", Persist);
        Require(state.Organizers.All(item => item.Name == name) &&
            (await new StateStore(statePath).LoadAsync()).Organizers.All(item => item.Name == name) &&
            OrganizerNameChange.NormalizeName(" CON ") == "CON",
            "Display names must trim whitespace, allow duplicates and filename-invalid/reserved text, and survive persistence.");
        await RequireUnchangedStorageAsync();
        await ExpectAsync<InvalidOperationException>(() => OrganizerNameChange.RenameAsync(external, "   ",
            () => throw new Exception("Blank input must not reach persistence.")));

        int saves = 0;
        await ExpectAsync<IOException>(() => OrganizerNameChange.RenameAsync(external, "SaveFailure", async () =>
        {
            await Persist();
            if (++saves == 1) throw new IOException("Injected failure after requested name reached disk");
        }));
        Require(saves == 2 && external.Name == name &&
            (await new StateStore(statePath).LoadAsync()).Organizers.Single(item => item.Id == external.Id).Name == name,
            "Failed persistence did not restore both the runtime and already-saved display name.");
        await RequireUnchangedStorageAsync();
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task CheckNoteCatalogChangesAsync(string root)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "Notes")).FullName;
        var store = new NoteStore(Path.Combine(root, "legacy-notes"));
        var document = new PortableNoteDocument
        {
            Html = "<p>initial</p>", ShowRuledLines = true,
            Placement = new PortableNotePlacement { MonitorDevice = "test-display", XDip = 125, YDip = 240, WidthDip = 400, HeightDip = 310 }
        };
        string note = await store.CreatePortableAsync(directory, "短便签", document);
        string ordinary = Path.Combine(directory, "ordinary.txt");
        await File.WriteAllTextAsync(ordinary, "initial");
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(note) };
        var observed = new System.Collections.Concurrent.ConcurrentQueue<(WatcherChangeTypes Change, string? Name, string? OldName)>();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        long lastEvent = 0;
        void Capture(WatcherChangeTypes change, string? name, string? oldName = null)
        {
            observed.Enqueue((change, name, oldName));
            Interlocked.Exchange(ref lastEvent, Environment.TickCount64);
        }
        using var watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Created += (_, e) => Capture(e.ChangeType, e.Name);
        watcher.Changed += (_, e) => Capture(e.ChangeType, e.Name);
        watcher.Deleted += (_, e) => Capture(e.ChangeType, e.Name);
        watcher.Renamed += (_, e) => Capture(e.ChangeType, e.Name, e.OldName);
        watcher.Error += (_, e) => errors.Enqueue(e.GetException());
        watcher.EnableRaisingEvents = true;

        async Task<(WatcherChangeTypes Change, string? Name, string? OldName)[]> ObserveAsync(Func<Task> action)
        {
            observed.Clear();
            Interlocked.Exchange(ref lastEvent, 0);
            await action();
            long deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(25);
                long last = Interlocked.Read(ref lastEvent);
                if (last > 0 && Environment.TickCount64 - last >= 150) break;
            }
            Require(errors.IsEmpty, "The real filesystem watcher failed or overflowed.");
            Require(!observed.IsEmpty && Environment.TickCount64 - Interlocked.Read(ref lastEvent) >= 150,
                "Filesystem events were absent or did not settle before the deadline.");
            return observed.ToArray();
        }
        bool Refresh((WatcherChangeTypes Change, string? Name, string? OldName) entry) =>
            PortableDocumentCatalogChanges.ShouldRefresh(entry.Change, entry.Name, entry.OldName, known);

        var saves = await ObserveAsync(async () =>
        {
            foreach (string html in new[] { "<p>a</p>", "<p>ab 中文</p>", "<p>ab 中文 final</p>" })
            {
                document.Html = html;
                await store.SavePortableAsync(note, document);
            }
        });
        Require(saves.All(entry => !Refresh(entry)),
            "Saving an already cataloged note still requested a directory refresh: " +
            string.Join("; ", saves.Where(Refresh).Select(entry => $"{entry.Change}: {entry.OldName} -> {entry.Name}")));
        PortableNoteDocument saved = await store.LoadPortableAsync(note);
        Require(saved.Html == "<p>ab 中文 final</p>" && saved.ShowRuledLines &&
            saved.Placement is { XDip: 125, YDip: 240, WidthDip: 400, HeightDip: 310 },
            "Repeated atomic saves lost the final short content or existing presentation metadata.");

        string added = string.Empty;
        var created = await ObserveAsync(async () => added = await store.CreatePortableAsync(directory, "New note", new PortableNoteDocument()));
        Require(created.Any(entry => entry.Name == Path.GetFileName(added) && Refresh(entry)), "A genuinely new note was suppressed by the save-event filter.");
        known.Add(Path.GetFileName(added));
        string renamed = Path.Combine(directory, "Renamed.tucknote");
        var moved = await ObserveAsync(() => { File.Move(added, renamed); return Task.CompletedTask; });
        Require(moved.Any(entry => entry.Change == WatcherChangeTypes.Renamed && entry.OldName == Path.GetFileName(added) && Refresh(entry)),
            "A genuine note rename was suppressed or lost its old name.");
        known.Remove(Path.GetFileName(added));
        known.Add(Path.GetFileName(renamed));
        var deleted = await ObserveAsync(() => { File.Delete(renamed); return Task.CompletedTask; });
        Require(deleted.Any(entry => entry.Change == WatcherChangeTypes.Deleted && entry.Name == Path.GetFileName(renamed) && Refresh(entry)),
            "Deleting a known note did not request catalog reconciliation.");
        var changed = await ObserveAsync(() => File.WriteAllTextAsync(ordinary, "ordinary file really changed"));
        Require(changed.Any(entry => entry.Change == WatcherChangeTypes.Changed && entry.Name == Path.GetFileName(ordinary) && Refresh(entry)),
            "Ordinary file changes were incorrectly treated as portable document saves.");
        string knownName = Path.GetFileName(note);
        string internalName = $"{PortableDocumentCatalogChanges.TemporaryPrefix}{Guid.NewGuid():N}.tmp";
        Require(PortableDocumentCatalogChanges.ShouldRefresh(WatcherChangeTypes.Created, knownName, null, known) &&
            PortableDocumentCatalogChanges.ShouldRefresh(WatcherChangeTypes.Renamed, knownName, internalName, known, catalogReadInProgress: true),
            "Same-name recreation during catalog reconciliation must not disappear behind the previous known-note snapshot.");
        Console.WriteLine($"PASS --sep08-fixes note-input: serialized snapshot/latest-edit/final-read saves and rename hold; 3 real atomic saves / {saves.Length} watcher events / 0 catalog refreshes; final content and metadata preserved; actual create, rename, delete and ordinary-file change still refresh. No UI or input-latency claim.");
    }

    private static async Task CheckNoteSaveSequenceAsync(string root)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "SaveSequence")).FullName;
        var store = new NoteStore(Path.Combine(root, "sequence-legacy"));
        string path = await store.CreatePortableAsync(directory, "sequence", new PortableNoteDocument());
        string liveText = "old", editorText = "old";
        var captures = new List<string>();
        var writes = new List<string>();
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool firstWrite = true;
        var sequence = new NoteSaveSequence(readEditor =>
        {
            if (readEditor) liveText = editorText;
            return Task.CompletedTask;
        }, () =>
        {
            var snapshot = new PortableNoteDocument { Html = liveText };
            captures.Add(snapshot.Html);
            return async () =>
            {
                if (firstWrite) { firstWrite = false; await releaseFirstWrite.Task; }
                await store.SavePortableAsync(path, snapshot);
                writes.Add(snapshot.Html);
            };
        });
        Task oldWrite = sequence.SaveAsync(readEditor: false);
        liveText = "intermediate";
        Task queued = sequence.SaveAsync(readEditor: false);
        liveText = "latest-model";
        editorText = "editor-final";
        Task finalFlush = sequence.SaveAsync(readEditor: true);
        Require(captures.SequenceEqual(["old"]) && !queued.IsCompleted && !finalFlush.IsCompleted && liveText == "latest-model",
            "A save captured stale queued content or read the editor before the active write finished.");
        releaseFirstWrite.SetResult();
        await Task.WhenAll(oldWrite, queued, finalFlush).WaitAsync(TimeSpan.FromSeconds(5));
        Require(captures.SequenceEqual(["old", "latest-model", "editor-final"]) && writes.SequenceEqual(captures) &&
            liveText == "editor-final" && (await store.LoadPortableAsync(path)).Html == "editor-final",
            "Pending saves did not coalesce current content, preserve the live model, or flush the final editor tail in order.");

        await sequence.SaveAsync(readEditor: false, holdForDirectoryRename: true);
        int beforeHeldSave = captures.Count;
        Task afterRename = sequence.SaveAsync(readEditor: false);
        Require(!afterRename.IsCompleted && captures.Count == beforeHeldSave, "A document write ran while the directory rename held the gate.");
        sequence.EndDirectoryRename();
        await afterRename.WaitAsync(TimeSpan.FromSeconds(5));
        Require(captures.Count == beforeHeldSave + 1, "Completing the directory rename did not release the next save.");
    }

    private static async Task CheckTodoSeparatorsAsync(string root)
    {
        string path = Path.Combine(root, "separator-state.json");
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":15,\"GlobalSettings\":{},\"Organizers\":[]}");
        var store = new StateStore(path);
        AppStateV2 state = await store.LoadAsync();
        Require(!state.GlobalSettings.TodoShowSeparators, "Existing settings without the new field must keep separators off.");
        var setting = new TodoSeparatorSetting();
        bool firstView = false, secondView = false;
        Action apply = () => firstView = state.GlobalSettings.TodoShowSeparators;
        apply += () => secondView = state.GlobalSettings.TodoShowSeparators;
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task enabling = setting.SetAsync(state.GlobalSettings, true,
            async () => { await releaseSave.Task; await store.SaveAsync(state); }, apply);
        Require(!enabling.IsCompleted && firstView && secondView,
            "Both open subscribers must update immediately while persistence is still pending.");
        releaseSave.SetResult();
        await enabling.WaitAsync(TimeSpan.FromSeconds(5));
        Require((await new StateStore(path).LoadAsync()).GlobalSettings.TodoShowSeparators,
            "A later session did not inherit the saved global separator setting.");

        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task failing = setting.SetAsync(state.GlobalSettings, false,
            async () => { await releaseFailure.Task; throw new IOException("Injected separator-setting save failure"); }, apply);
        Require(!failing.IsCompleted && !firstView && !secondView,
            "A pending disable must apply to all current subscribers together.");
        releaseFailure.SetResult();
        await ExpectAsync<IOException>(() => failing.WaitAsync(TimeSpan.FromSeconds(5)));
        Require(state.GlobalSettings.TodoShowSeparators && firstView && secondView &&
            (await new StateStore(path).LoadAsync()).GlobalSettings.TodoShowSeparators,
            "Failed persistence must restore both subscriber states and the previous durable preference.");
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class MemoryMenuStore : IFolderContextMenuStore
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, object>> _values = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, object>? Read(string key) => _values.GetValueOrDefault(key);
        public void Write(string key, IReadOnlyDictionary<string, object> values) =>
            _values[key] = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase);
        public void Delete(string key)
        {
            foreach (string candidate in _values.Keys.Where(candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToArray()) _values.Remove(candidate);
        }
    }
}
