using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using TuckPane.Services;

internal static class StabilityDiagnosticLimitChecks
{
    private const string Private = @"C:\Users\PRIVATE-USER\private-note.txt PRIVATE-BODY PRIVATE-EXCEPTION";
    private const string UnknownIdentifier = "PrivateCustomerDocument";
    private static Task<IReadOnlyList<WindowsCrashEvent>> NoEvents(CancellationToken _) =>
        Task.FromResult<IReadOnlyList<WindowsCrashEvent>>([]);
    private static DiagnosticRecord Record(int count = 0) => DiagnosticRecord.Create(
        DiagnosticArea.Transfer, DiagnosticStage.Failed, count: count, origin: nameof(AppLogger.FlushAsync));

    internal static async Task RunAsync(string root)
    {
        await FilteringAndTimeAsync(root);
        await HistoryAndPrivacyAsync(root);
        await CapacityAsync(root);
        await RotationAndFailuresAsync(root);
        Console.WriteLine("PASS --stability diagnostics-limits: 4 focused groups (filter/time, history/privacy, capacity/newest retention, rotation/failure/status); isolated files and synthetic events only; no GUI, input automation or legacy suites.");
    }

    private static async Task FilteringAndTimeAsync(string root)
    {
        await using var writer = new DiagnosticLogWriter(Path.Combine(root, "filter"));
        foreach (DiagnosticStage stage in new[] { DiagnosticStage.Notice, DiagnosticStage.Started,
            DiagnosticStage.Completed, DiagnosticStage.Cancelled, DiagnosticStage.Exit, DiagnosticStage.Recovered })
            writer.Write(DiagnosticRecord.Create(DiagnosticArea.Runtime, stage));
        writer.Write(DiagnosticRecord.Create(DiagnosticArea.Runtime, DiagnosticStage.Failed,
            exception: new OperationCanceledException(Private)));
        writer.Write(Record(1));
        writer.Write(DiagnosticRecord.Create(DiagnosticArea.Runtime, DiagnosticStage.Notice,
            exception: new TimeoutException(Private)));
        writer.Write(DiagnosticRecord.Create(DiagnosticArea.Runtime, DiagnosticStage.Interrupted));
        Require(await writer.FlushAsync(TimeSpan.FromSeconds(2)), "Filtering sample did not flush.");
        var records = await writer.SnapshotAsync();
        Require(records.Count == 3 && records.Any(r => r.Fault == DiagnosticFault.Timeout) &&
            records.Any(r => r.Stage == DiagnosticStage.Interrupted) && writer.Dropped == 0,
            "Only failure, timeout and interruption may persist; normal filtering must not count as dropped.");
        Require(records.All(r => r.Timestamp.Offset == TimeSpan.Zero && r.Timestamp.Year >= 2026 &&
            r.Session != Guid.Empty && r.Build != Guid.Empty), "Records must retain valid UTC time and identifiers.");

        long droppedBefore = AppLogger.Writer.Dropped;
        using (var success = AppLogger.Begin(DiagnosticArea.Transfer)) success.Complete();
        using (var cancelled = AppLogger.Begin(DiagnosticArea.Transfer)) cancelled.Fail(new OperationCanceledException(Private));
        AppLogger.Info(Private);
        AppLogger.Lifecycle("exit-application");
        AppLogger.Error(Private, new OperationCanceledException(Private));
        using (AppLogger.Begin(DiagnosticArea.Transfer)) { }
        AppLogger.Error(Private, new IOException(Private), caller: nameof(AppLogger.FlushAsync));
        await AppLogger.FlushAsync();
        var logged = await AppLogger.Writer.SnapshotAsync();
        Require(logged.Count == 2 && logged.Count(r => r.Stage == DiagnosticStage.Interrupted) == 1 &&
            logged.Any(r => r.ExceptionType == nameof(IOException) && r.CodeLocation == nameof(AppLogger.FlushAsync)) &&
            AppLogger.Writer.Dropped == droppedBefore,
            "Logger must suppress completion/cancellation/notice and record unfinished Dispose plus a safe error.");
        foreach (var record in logged) RequirePrivateAbsent(record.ToJson());
    }

    private static async Task HistoryAndPrivacyAsync(string root)
    {
        string directory = Path.Combine(root, "privacy");
        await using (var writer = new DiagnosticLogWriter(directory))
        {
            writer.Write(Record(1) with { CodeLocation = Private, ExceptionType = Private });
            writer.Write(Record(2) with { CodeLocation = UnknownIdentifier, ExceptionType = UnknownIdentifier });
            writer.Write(DiagnosticRecord.Create(DiagnosticArea.Shell, DiagnosticStage.Failed,
                exception: new IOException(Private), origin: nameof(AppLogger.FlushAsync)));
            Require(await writer.FlushAsync(TimeSpan.FromSeconds(2)), "Privacy sample did not flush.");
            string persisted = await File.ReadAllTextAsync(Path.Combine(directory, "runtime.0.jsonl"));
            RequirePrivateAbsent(persisted);
            var written = await writer.SnapshotAsync();
            Require(written.Count == 3 && written.Take(2).All(r => r.CodeLocation is null && r.ExceptionType is null) &&
                written[2].CodeLocation == nameof(AppLogger.FlushAsync) && written[2].ExceptionType == nameof(IOException),
                "Writer must apply membership allowlists while preserving known code and exception types.");
        }

        var oldError = JsonNode.Parse((Record(3) with { Format = 1 }).ToJson())!.AsObject();
        oldError.Remove("CodeLocation");
        oldError.Remove("ExceptionType");
        oldError["Message"] = Private;
        oldError["Path"] = Private;
        oldError["Extra"] = new JsonObject { ["body"] = Private };
        var unknown = JsonNode.Parse(Record(4).ToJson())!.AsObject();
        unknown["CodeLocation"] = UnknownIdentifier;
        unknown["ExceptionType"] = UnknownIdentifier;
        unknown["StackTrace"] = Private;
        string oldNormal = (Record() with { Format = 1, Stage = DiagnosticStage.Completed }).ToJson();
        await File.WriteAllTextAsync(Path.Combine(directory, "runtime.1.jsonl"),
            oldNormal + "\n" + oldError.ToJsonString() + "\n" + unknown.ToJsonString() + "\nmalformed\n");
        await using var historyWriter = new DiagnosticLogWriter(directory);
        string destination = Path.Combine(root, "privacy.zip");
        await DiagnosticsExporter.ExportAsync(destination, writer: historyWriter, readEvents: NoEvents);
        var package = await ReadPackageAsync(destination);
        foreach (string content in package.Values) RequirePrivateAbsent(content);
        var history = ReadRecords(package["runtime.jsonl"]);
        Require(history.Length == 5 && history.All(r => r.IsAbnormal) &&
            history.Single(r => r.Format == 1) is { CodeLocation: null, ExceptionType: null } &&
            !package["runtime.jsonl"].Contains("\"StackTrace\"") && !package["runtime.jsonl"].Contains("\"Extra\"") &&
            !package["runtime.jsonl"].Contains("\"Path\"") && !package["runtime.jsonl"].Contains("\"Message\""),
            "Historical export must filter normal entries, strip unknown fields and retain format-1 errors without invented metadata.");
        var metadata = JsonNode.Parse(package["environment.json"])!;
        Require(metadata["InvalidRecords"]!.GetValue<int>() == 1 && metadata["CollectionIncomplete"]!.GetValue<bool>(),
            "A malformed historical line must mark collection incomplete.");
    }

    private static async Task CapacityAsync(string root)
    {
        Require(DiagnosticsExporter.MaximumArchiveBytes == 5 * 1024 * 1024 &&
            DiagnosticsExporter.MaximumRuntimeBytes == 4 * 1024 * 1024 &&
            DiagnosticsExporter.MaximumAuxiliaryBytes == 256 * 1024,
            "Production ZIP/runtime/auxiliary budgets changed.");
        string directory = Path.Combine(root, "capacity");
        Directory.CreateDirectory(directory);
        DateTimeOffset time = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        var records = Enumerable.Range(0, 6).Select(i => Record(i) with { Timestamp = time.AddMinutes(i) }).ToArray();
        // Deliberately unordered history: selection must use UTC timestamps, not file order.
        await File.WriteAllTextAsync(Path.Combine(directory, "runtime.0.jsonl"),
            string.Join("\n", new[] { 5, 0, 3, 1, 4, 2 }.Select(i => records[i].ToJson())) + "\n");
        int runtimeBudget = records.Skip(4).Sum(r => Encoding.UTF8.GetByteCount(r.ToJson()) + 1);
        var limits = new DiagnosticsExporter.Limits(ArchiveBytes: 8192, RuntimeBytes: runtimeBudget, AuxiliaryBytes: 8192);
        await using var writer = new DiagnosticLogWriter(directory);
        string target = Path.Combine(root, "limited.zip");
        await DiagnosticsExporter.ExportAsync(target, writer: writer, readEvents: NoEvents, limits: limits);
        var package = await ReadPackageAsync(target);
        var retained = ReadRecords(package["runtime.jsonl"]);
        var metadata = JsonNode.Parse(package["environment.json"])!;
        Require(retained.Select(r => r.Count).SequenceEqual(new int?[] { 4, 5 }) &&
            Encoding.UTF8.GetByteCount(package["runtime.jsonl"]) == runtimeBudget && new FileInfo(target).Length <= limits.ArchiveBytes,
            "ZIP must retain the newest complete records in chronological order within both byte limits.");
        Require(metadata["RetainedRecords"]!.GetValue<int>() == 2 && metadata["OmittedForCapacity"]!.GetValue<int>() == 4 &&
            metadata["Truncated"]!.GetValue<bool>() && metadata["FirstRecordUtc"]!.GetValue<DateTimeOffset>() == records[4].Timestamp &&
            metadata["LastRecordUtc"]!.GetValue<DateTimeOffset>() == records[5].Timestamp &&
            metadata["ExportedAtUtc"]!.GetValue<DateTimeOffset>().Offset == TimeSpan.Zero,
            "Capacity metadata must disclose exact counts, retained range and export UTC time.");
        await using var empty = new DiagnosticLogWriter(Path.Combine(root, "empty"));
        string emptyTarget = Path.Combine(root, "empty.zip");
        await DiagnosticsExporter.ExportAsync(emptyTarget, writer: empty, readEvents: NoEvents, limits: limits);
        var emptyPackage = await ReadPackageAsync(emptyTarget);
        var emptyMetadata = JsonNode.Parse(emptyPackage["environment.json"])!;
        Require(emptyPackage["runtime.jsonl"].Length == 0 && emptyMetadata["NoExceptionsCollected"]!.GetValue<bool>() &&
            !emptyMetadata["CollectionIncomplete"]!.GetValue<bool>() && new FileInfo(emptyTarget).Length <= limits.ArchiveBytes,
            "Empty diagnostics must produce a valid bounded package and distinguish available empty collection.");
    }

    private static async Task RotationAndFailuresAsync(string root)
    {
        Require(DiagnosticLogWriter.MaximumFileCount == 4 && DiagnosticLogWriter.MaximumFileBytes == 5 * 1024 * 1024,
            "Production local retention must remain four files totalling at most 20 MiB.");
        string directory = Path.Combine(root, "rotation-limits");
        await using var writer = new DiagnosticLogWriter(directory, maximumBytes: 512, capacity: 32);
        for (int i = 0; i < 12; i++) writer.Write(Record(i));
        Require(await writer.FlushAsync(TimeSpan.FromSeconds(2)), "Rotation sample did not flush.");
        var files = Directory.GetFiles(directory, "runtime.*.jsonl").Select(p => new FileInfo(p)).ToArray();
        var latest = await writer.SnapshotAsync();
        Require(files.Length == 4 && files.All(f => f.Length <= 512) && files.Sum(f => f.Length) <= 4 * 512 &&
            latest[^1].Count == 11 && writer.Dropped == 0, "Rotation must bound file count and aggregate size while keeping latest errors.");

        string target = Path.Combine(root, "preserved.zip");
        await File.WriteAllTextAsync(target, "original-target");
        try
        {
            await DiagnosticsExporter.ExportAsync(target, writer: writer, readEvents: NoEvents,
                limits: new DiagnosticsExporter.Limits(ArchiveBytes: 1));
            throw new InvalidOperationException("The actual closed ZIP must fail a one-byte archive budget.");
        }
        catch (IOException) { }
        await RequirePreservedAsync(root, target);
        using var cancelled = new CancellationTokenSource();
        try
        {
            await DiagnosticsExporter.ExportAsync(target, cancelled.Token, writer, _ =>
            {
                cancelled.Cancel();
                return NoEvents(default);
            });
            throw new InvalidOperationException("Cancellation during event collection must stop publication.");
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        await RequirePreservedAsync(root, target);

        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var heldWriter = new DiagnosticLogWriter(Path.Combine(root, "flush-timeout"), beforeWrite: () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled writer not released.");
        });
        try
        {
            heldWriter.Write(Record());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            string incompleteTarget = Path.Combine(root, "incomplete.zip");
            await DiagnosticsExporter.ExportAsync(incompleteTarget, writer: heldWriter, readEvents: NoEvents);
            var package = await ReadPackageAsync(incompleteTarget);
            var metadata = JsonNode.Parse(package["environment.json"])!;
            Require(!metadata["LogFlushCompleted"]!.GetValue<bool>() && metadata["CollectionIncomplete"]!.GetValue<bool>() &&
                metadata["CrashCollection"]!.GetValue<string>() == "Available" && metadata["NoExceptionsCollected"]!.GetValue<bool>(),
                "A flush timeout must disclose incomplete collection even when no records were collected.");
        }
        finally { release.Set(); }
        string unavailableTarget = Path.Combine(root, "events-unavailable.zip");
        await DiagnosticsExporter.ExportAsync(unavailableTarget, writer: writer,
            readEvents: _ => throw new UnauthorizedAccessException(Private));
        var unavailable = await ReadPackageAsync(unavailableTarget);
        var status = JsonNode.Parse(unavailable["environment.json"])!;
        Require(status["CrashCollection"]!.GetValue<string>() == "Unavailable" && status["CollectionIncomplete"]!.GetValue<bool>() &&
            ReadRecords(unavailable["runtime.jsonl"]).Length == latest.Count, "Unavailable events must not discard runtime evidence.");
        foreach (string content in unavailable.Values) RequirePrivateAbsent(content);
    }

    private static async Task RequirePreservedAsync(string root, string target)
    {
        Require(await File.ReadAllTextAsync(target) == "original-target" &&
            Directory.GetFiles(root, ".diagnostic-*.tmp").Length == 0,
            "Failed or cancelled export must preserve the original destination and remove temporary files.");
    }

    private static async Task<Dictionary<string, string>> ReadPackageAsync(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        Require(archive.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "README.txt", "environment.json", "runtime.jsonl" }.Order()),
            "Only the three approved package entries are allowed.");
        var result = new Dictionary<string, string>();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            result.Add(entry.FullName, await reader.ReadToEndAsync());
        }
        return result;
    }

    private static DiagnosticRecord[] ReadRecords(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => DiagnosticRecord.ReadSafe(line) ?? throw new InvalidOperationException("Invalid exported record.")).ToArray();
    private static void RequirePrivateAbsent(string text) => Require(
        !text.Contains("PRIVATE-", StringComparison.Ordinal) && !text.Contains("private-note.txt", StringComparison.Ordinal) &&
        !text.Contains(UnknownIdentifier, StringComparison.Ordinal), "Private text or unknown identifiers escaped the allowlist.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
