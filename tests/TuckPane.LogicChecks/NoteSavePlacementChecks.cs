using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class NoteSavePlacementChecks
{
    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-note-placement-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            await CheckEditingAsync(root);
            Console.WriteLine("PASS note-save-placement: invalid geometry and minimized events preserve latest text and last valid placement.");
            await CheckDefaultsAsync(root);
            Console.WriteLine("PASS note-save-placement: missing/invalid initial placement recovers defaults; strict file validation remains intact.");
            await CheckWriteRecoveryAsync(root);
            Console.WriteLine("PASS note-save-placement: valid placement changes persist; locked-file failure preserves original and later saves recover. No GUI.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            string fullRoot = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(fullRoot) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) &&
                Path.GetFileName(fullRoot).StartsWith("TuckPane-note-placement-", StringComparison.Ordinal), "Unsafe test cleanup path.");
            Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static async Task CheckEditingAsync(string root)
    {
        NoteWindowPlacement original = ValidPlacement();
        Fixture fixture = await Fixture.CreateAsync(root, original);
        await fixture.SaveAsync("initial");
        NoteWindowPlacement[] invalid =
        [
            new() { HeightDip = 219.2 }, new() { HeightDip = 1200.8 },
            new() { WidthDip = 279.2 }, new() { WidthDip = 1600.8 },
            new() { HeightDip = 0 }, new() { HeightDip = double.NaN },
            new() { XDip = double.PositiveInfinity }, new() { MonitorDevice = null! }
        ];
        for (int i = 0; i < invalid.Length; i++)
        {
            // Exercise the final save boundary even if invalid geometry entered memory earlier.
            fixture.Placement = invalid[i];
            await fixture.SaveAsync($"latest 中文 {i}");
            await fixture.RequireSavedAsync($"latest 中文 {i}", original);
            Require(fixture.Recovery.CaptureWindowChange(invalid[i], isRestored: true) is null,
                "An invalid window event must not replace the last valid placement.");
        }
        Require(fixture.Recovery.CaptureWindowChange(new() { WidthDip = 500, HeightDip = 400 }, isRestored: false) is null,
            "A minimized/non-restored event must be ignored even when its rectangle looks valid.");
        fixture.Placement = null;
        await fixture.SaveAsync("after minimize");
        await fixture.RequireSavedAsync("after minimize", original);
    }

    private static async Task CheckDefaultsAsync(string root)
    {
        foreach (NoteWindowPlacement? initial in new NoteWindowPlacement?[] { null, new() { HeightDip = 0 }, new() { XDip = double.NaN } })
        {
            Fixture fixture = await Fixture.CreateAsync(root, initial);
            await fixture.SaveAsync("default placement body");
            await fixture.RequireSavedAsync("default placement body", new NoteWindowPlacement());
        }

        Fixture strict = await Fixture.CreateAsync(root, null);
        string validJson = await File.ReadAllTextAsync(strict.Path);
        string invalidJson = validJson.Replace("\"heightDip\":300", "\"heightDip\":0", StringComparison.Ordinal);
        Require(invalidJson != validJson, "Strict-reader fixture did not alter the serialized placement.");
        await File.WriteAllTextAsync(strict.Path, invalidJson);
        try
        {
            await strict.Store.LoadPortableAsync(strict.Path);
            throw new InvalidOperationException("Malformed on-disk placement was silently accepted.");
        }
        catch (InvalidDataException) { }
    }

    private static async Task CheckWriteRecoveryAsync(string root)
    {
        Fixture fixture = await Fixture.CreateAsync(root, ValidPlacement());
        NoteWindowPlacement moved = new() { MonitorDevice = "test-display-2", XDip = -25, YDip = 80, WidthDip = 280, HeightDip = 220 };
        fixture.Placement = fixture.Recovery.CaptureWindowChange(moved, isRestored: true);
        Require(fixture.Placement is not null, "A legal move/resize was rejected.");
        await fixture.SaveAsync("before lock");
        await fixture.RequireSavedAsync("before lock", moved);
        // Mutating a previous caller's object must not corrupt the retained snapshot.
        moved.HeightDip = 0;
        byte[] before = await File.ReadAllBytesAsync(fixture.Path);
        using (var locked = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                await fixture.SaveAsync("new text waiting for unlocked file");
                throw new InvalidOperationException("Saving over a non-delete-shared file should fail.");
            }
            catch (IOException) { }
            byte[] after = await File.ReadAllBytesAsync(fixture.Path);
            Require(before.SequenceEqual(after), "Failed replacement damaged the original note.");
        }
        Require(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Failed save left a temporary file.");
        fixture.Placement = new() { HeightDip = 0 };
        await fixture.SaveAsync("new text waiting for unlocked file");
        await fixture.RequireSavedAsync("new text waiting for unlocked file",
            new() { MonitorDevice = "test-display-2", XDip = -25, YDip = 80, WidthDip = 280, HeightDip = 220 });
    }

    private static NoteWindowPlacement ValidPlacement() =>
        new() { MonitorDevice = "test-display", XDip = 50, YDip = 60, WidthDip = 442.4, HeightDip = 296.8 };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture
    {
        internal NoteStore Store { get; } = new();
        internal string Path { get; private set; } = string.Empty;
        internal NotePlacementRecovery Recovery { get; }
        internal NoteWindowPlacement? Placement { get; set; }
        private readonly NoteSaveSequence _sequence;
        private string _text = string.Empty;

        private Fixture(NoteWindowPlacement? initial)
        {
            Placement = initial;
            Recovery = new(initial);
            _sequence = new(_ => Task.CompletedTask, () =>
            {
                Placement = Recovery.CaptureForSave(Placement);
                var snapshot = new PortableNoteDocument
                {
                    Html = _text,
                    Placement = NotePlacementRecovery.ToPortable(Placement)
                };
                return () => Store.SavePortableAsync(Path, snapshot);
            });
        }

        internal static async Task<Fixture> CreateAsync(string root, NoteWindowPlacement? initial)
        {
            var fixture = new Fixture(initial);
            fixture.Path = await fixture.Store.CreatePortableAsync(root, Guid.NewGuid().ToString("N"),
                new PortableNoteDocument { Placement = new PortableNotePlacement() });
            return fixture;
        }

        internal async Task SaveAsync(string text)
        {
            _text = text;
            await _sequence.SaveAsync(readEditor: false).WaitAsync(TimeSpan.FromSeconds(5));
        }

        internal async Task RequireSavedAsync(string text, NoteWindowPlacement expected)
        {
            PortableNoteDocument saved = await Store.LoadPortableAsync(Path);
            Require(saved.Html == text, "The latest editor text was not persisted.");
            Require(saved.Placement is { } p && p.MonitorDevice == expected.MonitorDevice &&
                p.XDip == expected.XDip && p.YDip == expected.YDip &&
                p.WidthDip == expected.WidthDip && p.HeightDip == expected.HeightDip,
                "Saved placement differs from the last valid/default placement.");
        }
    }
}
