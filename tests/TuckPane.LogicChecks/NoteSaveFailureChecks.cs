using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class NoteSaveFailureChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-note-save-failure-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            await CheckRenameAsync(Path.Combine(root, "rename"));
            Console.WriteLine("PASS note-save-failure: renamed note saves only to the new path.");
            await CheckInterleavingAsync(Path.Combine(root, "interleaving"));
            Console.WriteLine("PASS note-save-failure: active save, rename and queued save remain ordered.");
            foreach (string failure in new[] { "conflict", "persist", "rollback" })
                await CheckFailureAsync(Path.Combine(root, failure), failure);
            Console.WriteLine("PASS note-save-failure: conflict and persistence rollback preserve binding, content and save access. No GUI.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            string fullRoot = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(fullRoot) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) &&
                Path.GetFileName(fullRoot).StartsWith("TuckPane-note-save-failure-", StringComparison.Ordinal),
                "Refusing cleanup outside the isolated fixture directory.");
            Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static async Task CheckRenameAsync(string root)
    {
        Fixture fixture = await Fixture.CreateAsync(root);
        await fixture.RenameAsync().WaitAsync(Timeout);
        fixture.Text = "edited after rename";
        await fixture.Sequence.SaveAsync(false).WaitAsync(Timeout);
        fixture.RequireBinding(fixture.Target);
        Require(!File.Exists(fixture.Source), "Saving after rename recreated the old note path.");
        await fixture.RequireContentAsync(fixture.Target, fixture.Text);
        fixture.RequireNoTemporaryFiles();
    }

    private static async Task CheckInterleavingAsync(string root)
    {
        Fixture fixture = await Fixture.CreateAsync(root);
        var firstWriteStarted = Signal();
        var releaseFirstWrite = Signal();
        var renameStarted = Signal();
        var renameHeld = Signal();
        var releaseRename = Signal();
        int writes = 0;
        fixture.BeforeWrite = async () =>
        {
            if (++writes != 1) return;
            firstWriteStarted.SetResult();
            await releaseFirstWrite.Task;
        };
        fixture.BeforeBegin = () => renameStarted.SetResult();
        fixture.AfterBegin = async () =>
        {
            renameHeld.SetResult();
            await releaseRename.Task;
        };
        Task active = fixture.Sequence.SaveAsync(false);
        Task? rename = null;
        Task? queued = null;
        try
        {
            await firstWriteStarted.Task.WaitAsync(Timeout);
            rename = fixture.RenameAsync();
            await renameStarted.Task.WaitAsync(Timeout);
            Require(!rename.IsCompleted && File.Exists(fixture.Source) && !File.Exists(fixture.Target),
                "Rename moved the note before the in-flight save completed.");
            releaseFirstWrite.SetResult();
            await renameHeld.Task.WaitAsync(Timeout);
            fixture.Text = "latest queued edit";
            queued = fixture.Sequence.SaveAsync(false);
            Require(!queued.IsCompleted && writes == 2, "Queued save bypassed the rename hold.");
            releaseRename.SetResult();
            await Task.WhenAll(active, rename, queued).WaitAsync(Timeout);
            fixture.RequireBinding(fixture.Target);
            Require(fixture.WrittenPaths.SequenceEqual(new[] { fixture.Source, fixture.Source, fixture.Target }),
                "A queued save captured the stale path before rename completed.");
            Require(!File.Exists(fixture.Source), "An interleaved save recreated the old path.");
            await fixture.RequireContentAsync(fixture.Target, fixture.Text);
            fixture.RequireNoTemporaryFiles();
        }
        finally
        {
            releaseFirstWrite.TrySetResult();
            releaseRename.TrySetResult();
            // Release fixture barriers even when an assertion fails; never drive a real window.
        }
    }

    private static async Task CheckFailureAsync(string root, string failure)
    {
        Fixture fixture = await Fixture.CreateAsync(root);
        byte[]? conflictBytes = null;
        if (failure == "conflict")
        {
            await File.WriteAllTextAsync(fixture.Target, "existing destination");
            conflictBytes = await File.ReadAllBytesAsync(fixture.Target);
        }
        int persistCalls = 0;
        fixture.Persist = async () =>
        {
            if (failure == "conflict" || ++persistCalls != 1) return;
            if (failure == "rollback") await File.WriteAllTextAsync(fixture.Source, "do not overwrite");
            throw new IOException("Injected state persistence failure.");
        };
        Exception? error = null;
        try { await fixture.RenameAsync().WaitAsync(Timeout); }
        catch (Exception ex) when (ex is not TimeoutException) { error = ex; }
        Require(error is not null, $"The {failure} fixture unexpectedly succeeded.");
        string expectedPath = failure == "rollback" ? fixture.Target : fixture.Source;
        fixture.RequireBinding(expectedPath);
        await fixture.RequireContentAsync(expectedPath, "original");
        if (failure == "rollback")
        {
            Require(error is IOException { InnerException: AggregateException },
                "Rollback failure did not retain both errors in an IOException.");
            Require(await File.ReadAllTextAsync(fixture.Source) == "do not overwrite", "Rollback overwrote the old-path occupant.");
        }
        else if (failure == "conflict")
            Require((await File.ReadAllBytesAsync(fixture.Target)).SequenceEqual(conflictBytes!), "Rename replaced the conflicting target.");
        else
            Require(!File.Exists(fixture.Target), "Successful rollback left the renamed file behind.");
        fixture.Text = "saved after failure";
        await fixture.Sequence.SaveAsync(false).WaitAsync(Timeout);
        await fixture.RequireContentAsync(expectedPath, fixture.Text);
        if (failure == "rollback")
            Require(await File.ReadAllTextAsync(fixture.Source) == "do not overwrite", "Post-failure save used the occupied old path.");
        fixture.RequireNoTemporaryFiles();
    }

    private sealed class Fixture
    {
        private readonly NoteStore _store;
        private readonly HashSet<string> _index = new(StringComparer.OrdinalIgnoreCase);
        private string _boundPath;
        private string _title;
        internal string Source { get; }
        internal string Target { get; }
        internal string Text = "original";
        internal readonly List<string> WrittenPaths = [];
        internal readonly NoteSaveSequence Sequence;
        internal Func<Task> BeforeWrite = () => Task.CompletedTask;
        internal Action BeforeBegin = () => { };
        internal Func<Task> AfterBegin = () => Task.CompletedTask;
        internal Func<Task> Persist = () => Task.CompletedTask;

        private Fixture(string root, NoteStore store, string source)
        {
            _store = store;
            Source = _boundPath = source;
            Target = Path.Combine(root, "renamed.tucknote");
            _title = Path.GetFileNameWithoutExtension(source);
            _index.Add(source);
            Sequence = new NoteSaveSequence(_ => Task.CompletedTask, () =>
            {
                string capturedPath = _boundPath;
                var snapshot = new PortableNoteDocument { Html = Text };
                return async () =>
                {
                    await BeforeWrite();
                    await _store.SavePortableAsync(capturedPath, snapshot);
                    WrittenPaths.Add(capturedPath);
                };
            });
        }

        internal static async Task<Fixture> CreateAsync(string root)
        {
            Directory.CreateDirectory(root);
            var store = new NoteStore(Path.Combine(root, "legacy"));
            string source = await store.CreatePortableAsync(root, "original", new PortableNoteDocument { Html = "original" });
            return new Fixture(root, store, source);
        }

        internal Task RenameAsync() => NoteRenameTransaction.RenameAsync(Source, Target,
            async () => { BeforeBegin(); await Sequence.SaveAsync(true, true); await AfterBegin(); },
            Sequence.EndDirectoryRename,
            (from, to) => { _index.Remove(from); _index.Add(to); },
            path => { _boundPath = path; _title = Path.GetFileNameWithoutExtension(path); },
            () => Persist());

        internal void RequireBinding(string expected) => Require(_boundPath == expected &&
            _title == Path.GetFileNameWithoutExtension(expected) && _index.SetEquals([expected]),
            "The save path, title and open-note index diverged.");
        internal async Task RequireContentAsync(string path, string expected) =>
            Require((await _store.LoadPortableAsync(path)).Html == expected, "The saved note content diverged.");
        internal void RequireNoTemporaryFiles() => Require(!Directory.EnumerateFiles(Path.GetDirectoryName(Source)!, "*.tmp").Any(),
            "The rename/save operation left a temporary file behind.");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
