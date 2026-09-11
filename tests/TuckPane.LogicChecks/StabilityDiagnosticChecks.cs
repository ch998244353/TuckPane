using System.IO.Compression;
using System.Text.Json.Nodes;
using TuckPane.Services;

internal static class StabilityDiagnosticChecks
{
    private static readonly string[] PrivateMarkers = ["PRIVATE-USER", "sensitive-note.txt", "PRIVATE-BODY", "PRIVATE-EXCEPTION", "EXTRA-PRIVATE"];

    internal static async Task RunAsync(string root)
    {
        await BoundedQueueAndFlushAsync(root);
        await RotationAndWriteFailureAsync(root);
        await PrivacyAndExportAsync(root);
        StabilityWindowsEventChecks.Run();
        Console.WriteLine("PASS --stability diagnostics: bounded queue/drop count/timed flush, four-file rotation, write failure isolation, message/extra-field filtering, ZIP privacy and synthetic Windows event parsing; no real event collection, GUI or upload.");
    }

    private static DiagnosticRecord Record(int count = 0) => DiagnosticRecord.Create(
        DiagnosticArea.Transfer, DiagnosticStage.Completed, count: count, origin: "diagnostic-test");

    private static async Task BoundedQueueAndFlushAsync(string root)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = new DiagnosticLogWriter(Path.Combine(root, "bounded"), capacity: 2, beforeWrite: () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Controlled writer was not released.");
        });
        try
        {
            writer.Write(Record(1));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            writer.Write(Record(2));
            writer.Write(Record(3));
            writer.Write(Record(4));
            Require(writer.Dropped == 1, "A full two-slot queue must reject and count the fourth record without waiting.");
            Require(!await writer.FlushAsync(TimeSpan.FromMilliseconds(25)).WaitAsync(TimeSpan.FromSeconds(1)),
                "Flush must honor its deadline while the worker and queue are held.");
            release.Set();
            Require(await writer.FlushAsync(TimeSpan.FromSeconds(1)), "Flush must recover after the controlled writer is released.");
            var snapshot = await writer.SnapshotAsync();
            Require(snapshot.Count == 3 && writer.WriteFailures == 0,
                "Only the three accepted records must persist after congestion clears.");
            Require(snapshot.All(record => record.Dropped == 1 && record.WriteFailures == 0),
                "Persisted records must carry the observed drop count so it survives a later application restart.");
        }
        finally { release.Set(); }
    }

    private static async Task RotationAndWriteFailureAsync(string root)
    {
        string directory = Path.Combine(root, "rotation");
        await using (var writer = new DiagnosticLogWriter(directory, capacity: 32, maximumBytes: 512, fileCount: 4))
        {
            for (int index = 0; index < 12; index++) writer.Write(Record(index));
            Require(await writer.FlushAsync(TimeSpan.FromSeconds(2)), "The finite rotation sample must drain.");
            string[] files = Directory.GetFiles(directory);
            Require(files.Length == 4 && files.All(path => new FileInfo(path).Length <= 512),
                "Rotation must retain exactly four files without exceeding each configured byte limit.");
            var snapshot = await writer.SnapshotAsync();
            Require(snapshot.Count > 0 && snapshot[^1].Count == 11 && writer.Dropped == 0,
                "Rotation must preserve the latest complete record and avoid discarding valid writes.");
        }
        string blockedDirectory = Path.Combine(root, "blocked-directory");
        await File.WriteAllTextAsync(blockedDirectory, "This file intentionally occupies the configured directory path.");
        await using (var writer = new DiagnosticLogWriter(blockedDirectory))
        {
            writer.Write(Record());
            Require(await writer.FlushAsync(TimeSpan.FromSeconds(1)) && writer.Dropped == 1 && writer.WriteFailures == 1,
                "A filesystem write failure must be counted without faulting the worker or flush barrier.");
            File.Delete(blockedDirectory); // This is the isolated fixture file created immediately above.
            writer.Write(Record());
            Require(await writer.FlushAsync(TimeSpan.FromSeconds(1)), "A recovered log directory must accept a subsequent write.");
            var recovered = await writer.SnapshotAsync();
            Require(recovered.Count == 1 && recovered[0].Dropped == 1 && recovered[0].WriteFailures == 1,
                "The next successful record must preserve prior write-failure counters on disk.");
        }
    }

    private static async Task PrivacyAndExportAsync(string root)
    {
        const string privateText = @"C:\Users\PRIVATE-USER\sensitive-note.txt PRIVATE-BODY PRIVATE-EXCEPTION EXTRA-PRIVATE";
        var record = DiagnosticRecord.Create(DiagnosticArea.Shell, DiagnosticStage.Failed,
            elapsedMs: 12, count: 1, exception: new IOException(privateText), origin: privateText);
        string serialized = record.ToJson();
        RequirePrivateAbsent(serialized, "Created record");
        Require(record.Fault == DiagnosticFault.IO && record.HResult is not null && record.Origin.Length == 16,
            "Safe fault classification, error code and hashed origin must remain available.");
        Require(DiagnosticRecord.ReadSafe((record with { Dropped = -1 }).ToJson()) is null &&
                DiagnosticRecord.ReadSafe((record with { WriteFailures = -1 }).ToJson()) is null,
            "Stored diagnostic counters must reject negative values.");
        JsonObject injected = JsonNode.Parse(serialized)!.AsObject();
        injected["Path"] = privateText;
        injected["Message"] = privateText;
        injected["Extra"] = new JsonObject { ["note"] = privateText };
        string injectedJson = injected.ToJsonString();
        DiagnosticRecord clean = DiagnosticRecord.ReadSafe(injectedJson) ?? throw new InvalidOperationException("A valid record with unknown fields was rejected.");
        RequirePrivateAbsent(clean.ToJson(), "Reconstructed record");
        Require(!clean.ToJson().Contains("\"Path\"") && !clean.ToJson().Contains("\"Message\"") && !clean.ToJson().Contains("\"Extra\""),
            "Reconstruction must remove unknown JSON properties.");
        injected["Origin"] = privateText;
        Require(DiagnosticRecord.ReadSafe(injected.ToJsonString()) is null,
            "A raw path inserted into a known origin field must be rejected.");

        AppLogger.Info(privateText);
        AppLogger.Error(privateText, new IOException(privateText, new InvalidOperationException(privateText)));
        await AppLogger.FlushAsync();
        string appDirectory = Path.Combine(AppPaths.LocalRoot, "diagnostics");
        Require(Path.GetFullPath(appDirectory).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Compatibility logging must remain inside this invocation's isolated root.");
        string[] appFiles = Directory.GetFiles(appDirectory, "runtime.*.jsonl");
        Require(appFiles.Length > 0, "Compatibility logging must emit safe diagnostic records.");
        foreach (string path in appFiles) RequirePrivateAbsent(await File.ReadAllTextAsync(path), "Compatibility log");

        string exportDirectory = Path.Combine(root, "export-source");
        Directory.CreateDirectory(exportDirectory);
        await File.WriteAllTextAsync(Path.Combine(exportDirectory, "runtime.0.jsonl"), injectedJson + "\n" + privateText + "\n");
        await File.WriteAllTextAsync(Path.Combine(exportDirectory, "TuckPane.log"), privateText);
        await File.WriteAllTextAsync(Path.Combine(exportDirectory, "state.json"), privateText);
        await File.WriteAllTextAsync(Path.Combine(exportDirectory, "sensitive-note.txt"), privateText);
        int eventReads = 0;
        string destination = Path.Combine(root, "diagnostic-report.zip");
        await using var exportWriter = new DiagnosticLogWriter(exportDirectory, maximumBytes: 4096);
        await DiagnosticsExporter.ExportAsync(destination, writer: exportWriter, readEvents: _ =>
        {
            eventReads++;
            return Task.FromResult<IReadOnlyList<WindowsCrashEvent>>([]);
        });
        Require(eventReads == 1, "Export must use the injected event reader exactly once.");
        using var archive = ZipFile.OpenRead(destination);
        Require(archive.Entries.Select(entry => entry.FullName).Order().SequenceEqual(new[] { "README.txt", "environment.json", "runtime.jsonl" }.Order()),
            "The ZIP must contain only the three approved entries, never historical logs, state or note files.");
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            string content = await reader.ReadToEndAsync();
            RequirePrivateAbsent(content, "ZIP entry " + entry.FullName);
            if (entry.FullName == "runtime.jsonl")
            {
                Require(DiagnosticRecord.ReadSafe(content)?.Fault == DiagnosticFault.IO &&
                        !content.Contains("\"Extra\"") && !content.Contains("\"Path\"") && !content.Contains("\"Message\""),
                    "Export must retain the safe record while removing injected properties and malformed raw lines.");
            }
        }

        string withoutEvents = Path.Combine(root, "diagnostic-event-failure.zip");
        await DiagnosticsExporter.ExportAsync(withoutEvents, writer: exportWriter,
            readEvents: _ => throw new UnauthorizedAccessException(privateText));
        using var fallbackArchive = ZipFile.OpenRead(withoutEvents);
        Require(fallbackArchive.Entries.Count == 3, "An event-reader exception must still produce the ordinary diagnostic package.");
        using var runtimeReader = new StreamReader(fallbackArchive.GetEntry("runtime.jsonl")!.Open());
        Require(DiagnosticRecord.ReadSafe(await runtimeReader.ReadToEndAsync())?.Fault == DiagnosticFault.IO,
            "An event-reader exception must not discard the available runtime record.");
        using var environmentReader = new StreamReader(fallbackArchive.GetEntry("environment.json")!.Open());
        string environment = await environmentReader.ReadToEndAsync();
        RequirePrivateAbsent(environment, "Event-reader failure export");
        Require(JsonNode.Parse(environment)?["CrashEvents"]?.AsArray().Count == 0,
            "Unavailable events must become an empty safe list rather than an exception payload.");
    }

    private static void RequirePrivateAbsent(string text, string source)
    {
        Require(PrivateMarkers.All(marker => !text.Contains(marker, StringComparison.Ordinal)), source + " contains private text.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
