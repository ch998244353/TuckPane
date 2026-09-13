using System.Text.Json;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class NoteTodo42Checks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync()
    {
        CheckComposer();
        Console.WriteLine("PASS note-todo-42: composer drafts, blur, cancel and single submission.");
        await CheckSynchronizationAsync();
        Console.WriteLine("PASS note-todo-42: theme queue ordering, failure isolation and settings rollback.");
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-note-todo-42-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            await CheckStoreAsync(root);
            Console.WriteLine("PASS note-todo-42: real portable note/todo theme persistence and exclusions. No GUI.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            string fullRoot = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(fullRoot) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) &&
                Path.GetFileName(fullRoot).StartsWith("TuckPane-note-todo-42-", StringComparison.Ordinal),
                "Refusing cleanup outside the isolated fixture directory.");
            Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static void CheckComposer()
    {
        var document = new PortableTodoDocument();
        var composer = new TodoComposer();
        Require(!composer.IsEditing && composer.Text == string.Empty, "Composer did not start collapsed.");
        composer.Begin();
        composer.Text = " \t ";
        Require(composer.Submit(document) is null && document.Tasks.Count == 0, "Whitespace created a task.");
        composer.Blur();
        Require(!composer.IsEditing && composer.Text == string.Empty, "Blank blur retained the editor.");
        composer.Begin();
        composer.Text = "unfinished draft";
        composer.Blur();
        Require(composer.IsEditing && composer.Text == "unfinished draft" && document.Tasks.Count == 0,
            "Nonblank blur lost or committed the draft.");
        composer.Cancel();
        Require(!composer.IsEditing && composer.Text == string.Empty && document.Tasks.Count == 0,
            "Cancel retained or committed a draft.");
        composer.Begin();
        composer.Text = "  one\t task  ";
        PortableTodoTask? task = composer.Submit(document);
        Require(task is not null && task.Text == "one task" && document.Tasks.Count == 1 &&
            ReferenceEquals(document.Tasks[0], task) && !composer.IsEditing && composer.Text == string.Empty,
            "Submit failed to append exactly one normalized task and reset the composer.");
        Require(composer.Submit(document) is null && document.Tasks.Count == 1, "Repeated submit duplicated the task.");
    }

    private static async Task CheckSynchronizationAsync()
    {
        var settings = new GlobalSettings { NoteTheme = NoteTheme.RainBlue, TodoShowSeparators = false };
        NoteDefinition[] notes = [new() { Theme = NoteTheme.Graphite, ShowRuledLines = true },
            new() { Theme = NoteTheme.WheatPaper, ShowRuledLines = false }];
        var synchronization = new NoteThemeSynchronization();
        var held = Signal();
        var release = Signal();
        var events = new List<string>();
        Task<IReadOnlyList<NoteThemeFailure>> Select(NoteTheme theme, bool hold) => synchronization.SetAsync(
            settings, () => notes, theme, () => { events.Add($"save:{theme}"); return Task.CompletedTask; },
            () => [new("first", async () =>
            {
                events.Add($"first:{theme}");
                if (hold) { held.SetResult(); await release.Task.WaitAsync(Timeout); }
                Require(settings.NoteTheme == theme && notes.All(note => note.Theme == theme), "Themes interleaved.");
                if (hold) throw new IOException("Injected target failure.");
            }), new("last", () => { events.Add($"last:{theme}"); return Task.CompletedTask; })]);
        Task<IReadOnlyList<NoteThemeFailure>> first = Select(NoteTheme.SunYellow, true);
        try
        {
            await held.Task.WaitAsync(Timeout);
            Task<IReadOnlyList<NoteThemeFailure>> second = Select(NoteTheme.InkBlack, false);
            Require(!second.IsCompleted && events.Count == 2, "Queued selection bypassed the active target.");
            release.TrySetResult();
            var results = await Task.WhenAll(first, second).WaitAsync(Timeout);
            Require(results[0].Count == 1 && results[0][0].Target == "first" && results[0][0].Error is IOException &&
                results[1].Count == 0 && events.SequenceEqual(new[] { "save:SunYellow", "first:SunYellow", "last:SunYellow",
                    "save:InkBlack", "first:InkBlack", "last:InkBlack" }), "Target failure stopped or interleaved selections.");
        }
        finally { release.TrySetResult(); }

        settings.NoteTheme = NoteTheme.RainBlue;
        notes[0].Theme = NoteTheme.Graphite;
        notes[1].Theme = NoteTheme.WheatPaper;
        var saving = Signal();
        var failSave = Signal();
        bool targetCalled = false, rollbackObserved = false;
        Task<IReadOnlyList<NoteThemeFailure>> failed = synchronization.SetAsync(settings, () => notes, NoteTheme.CloudPaper,
            async () => { saving.SetResult(); await failSave.Task.WaitAsync(Timeout); throw new IOException("Injected settings failure."); },
            () => { targetCalled = true; return []; });
        try
        {
            await saving.Task.WaitAsync(Timeout);
            Task<IReadOnlyList<NoteThemeFailure>> recovered = synchronization.SetAsync(settings, () =>
            {
                rollbackObserved = settings.NoteTheme == NoteTheme.RainBlue && notes[0].Theme == NoteTheme.Graphite &&
                    notes[1].Theme == NoteTheme.WheatPaper;
                return notes;
            }, NoteTheme.SunYellow, () => Task.CompletedTask, () => []);
            failSave.TrySetResult();
            bool rejected = false;
            try { await failed.WaitAsync(Timeout); } catch (IOException) { rejected = true; }
            await recovered.WaitAsync(Timeout);
            Require(rejected && !targetCalled && rollbackObserved && settings.NoteTheme == NoteTheme.SunYellow &&
                notes.All(note => note.Theme == NoteTheme.SunYellow), "Settings failure did not roll back and release the queue.");
            Require(!settings.TodoShowSeparators && notes[0].ShowRuledLines && !notes[1].ShowRuledLines,
                "Theme synchronization changed separator or ruled-line preferences.");
        }
        finally { failSave.TrySetResult(); }
    }

    private static async Task CheckStoreAsync(string root)
    {
        var store = new NoteStore(Path.Combine(root, "legacy"));
        string nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        var note = new PortableNoteDocument { Html = "<p>保留正文 <b>bold</b></p>", FontSize = 19,
            ShowRuledLines = true, Theme = NoteTheme.Graphite };
        var todo = new PortableTodoDocument { FontSize = 21, Theme = NoteTheme.WheatPaper, Tasks =
            [new() { Text = "pending" }, new() { Text = "finished", Done = true,
                CompletedAtUtc = new DateTimeOffset(2026, 9, 1, 1, 2, 3, TimeSpan.Zero) }] };
        string notePath = await store.CreatePortableAsync(root, "note", note).WaitAsync(Timeout);
        string todoPath = await store.CreateTodoAsync(root, "todo", todo).WaitAsync(Timeout);
        string[] untouched = [await store.CreatePortableAsync(root, "open-note", note).WaitAsync(Timeout),
            await store.CreateTodoAsync(root, "open-todo", todo).WaitAsync(Timeout),
            await store.CreatePortableAsync(nested, "nested-note", note).WaitAsync(Timeout),
            await store.CreateTodoAsync(nested, "nested-todo", todo).WaitAsync(Timeout)];
        var snapshots = new Dictionary<string, byte[]>();
        foreach (string path in untouched) snapshots[path] = await File.ReadAllBytesAsync(path).WaitAsync(Timeout);
        var excluded = new HashSet<string>(untouched.Take(2), StringComparer.OrdinalIgnoreCase);
        string badNote = Path.Combine(root, "invalid.tucknote"), badTodo = Path.Combine(root, "invalid.tucktodo");
        await File.WriteAllTextAsync(badNote, "invalid note").WaitAsync(Timeout);
        await File.WriteAllTextAsync(badTodo, "invalid todo").WaitAsync(Timeout);
        var noteFailures = await store.ApplyThemeToTopLevelPortableFilesAsync(root, NoteTheme.CloudPaper, excluded).WaitAsync(Timeout);
        var todoFailures = await store.ApplyThemeToTopLevelTodoFilesAsync(root, NoteTheme.CloudPaper, excluded).WaitAsync(Timeout);
        Require(noteFailures.SequenceEqual(new[] { badNote }) && todoFailures.SequenceEqual(new[] { badTodo }),
            "Invalid portable files were not reported independently.");
        note.Theme = todo.Theme = NoteTheme.CloudPaper;
        Require(JsonSerializer.Serialize(await store.LoadPortableAsync(notePath).WaitAsync(Timeout)) == JsonSerializer.Serialize(note),
            "Note theme update changed content, font size or ruled lines.");
        Require(JsonSerializer.Serialize(await store.LoadTodoAsync(todoPath).WaitAsync(Timeout)) == JsonSerializer.Serialize(todo),
            "Todo theme update changed task IDs, order, completion data or font size.");
        foreach (var entry in snapshots)
            Require((await File.ReadAllBytesAsync(entry.Key).WaitAsync(Timeout)).SequenceEqual(entry.Value),
                "Theme update touched an excluded open file or a nested file.");
        Require(await File.ReadAllTextAsync(badNote).WaitAsync(Timeout) == "invalid note" &&
            await File.ReadAllTextAsync(badTodo).WaitAsync(Timeout) == "invalid todo", "Theme update overwrote invalid input.");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
