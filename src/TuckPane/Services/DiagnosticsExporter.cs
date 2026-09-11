using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TuckPane.Services;

internal static class DiagnosticsExporter
{
    internal const int MaximumArchiveBytes = 5 * 1024 * 1024;
    internal const int MaximumRuntimeBytes = 4 * 1024 * 1024;
    internal const int MaximumAuxiliaryBytes = 256 * 1024;
    // Smaller budgets are an internal test seam; production callers use the constants above.
    internal sealed record Limits(int ArchiveBytes = MaximumArchiveBytes,
        int RuntimeBytes = MaximumRuntimeBytes, int AuxiliaryBytes = MaximumAuxiliaryBytes);
    private static readonly object EventGate = new();
    private static Task<WindowsCrashCollection>? _eventCollection;

    internal static async Task ExportAsync(string destination, CancellationToken cancellationToken = default,
        DiagnosticLogWriter? writer = null,
        Func<CancellationToken, Task<IReadOnlyList<WindowsCrashEvent>>>? readEvents = null,
        Limits? limits = null)
    {
        limits ??= new();
        if (limits.ArchiveBytes is < 1 or > MaximumArchiveBytes ||
            limits.RuntimeBytes is < 0 or > MaximumRuntimeBytes ||
            limits.AuxiliaryBytes is < 1 or > MaximumAuxiliaryBytes)
            throw new ArgumentOutOfRangeException(nameof(limits));
        cancellationToken.ThrowIfCancellationRequested();
        writer ??= AppLogger.Writer;
        bool flushed = await writer.FlushAsync(TimeSpan.FromSeconds(2));
        WindowsCrashCollection collection = new([], CrashCollectionStatus.Unavailable);
        using var eventDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        eventDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        CancellationToken eventToken = eventDeadline.Token;
        try
        {
            if (readEvents is not null)
            {
                var events = await Task.Run(() => readEvents(eventToken), eventToken).WaitAsync(eventToken);
                collection = new(events, CrashCollectionStatus.Available);
            }
            else
            {
                Task<WindowsCrashCollection> pending;
                lock (EventGate)
                {
                    // A timeout cannot stop a synchronous native start. Never accumulate collectors.
                    if (_eventCollection is null || _eventCollection.IsCompleted)
                        _eventCollection = Task.Run(() => WindowsCrashEvents.CollectAsync(eventToken), eventToken);
                    pending = _eventCollection;
                }
                collection = await pending.WaitAsync(eventToken);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { collection = new([], CrashCollectionStatus.TimedOut); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        var snapshot = await writer.SnapshotWithStatusAsync(cancellationToken);
        await Task.Run(async () =>
        {
            DateTimeOffset exportedAt = DateTimeOffset.UtcNow;
            var records = snapshot.Records.OrderByDescending(record => record.Timestamp).ToArray();
            var retained = new List<(DiagnosticRecord Record, string Json)>();
            int runtimeBytes = 0;
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string json = record.ToJson();
                int bytes = Encoding.UTF8.GetByteCount(json) + 1;
                // Keep a contiguous newest suffix; never replace a newer record with an older small one.
                if (bytes > limits.RuntimeBytes - runtimeBytes) break;
                retained.Add((record, json));
                runtimeBytes += bytes;
            }
            retained.Reverse();
            var events = collection.Events.Select(value => WindowsCrashEvents.Sanitize(value, exportedAt))
                .OfType<WindowsCrashEvent>().OrderByDescending(value => value.Timestamp).Take(32).ToArray();
            int omitted = records.Length - retained.Count;
            bool incomplete = !flushed || collection.Status != CrashCollectionStatus.Available ||
                snapshot.UnreadableFiles != 0 || snapshot.InvalidRecords != 0 || writer.Dropped != 0 ||
                writer.WriteFailures != 0 || records.Any(record => record.Dropped != 0 || record.WriteFailures != 0);
            string environment = JsonSerializer.Serialize(new
            {
                Format = 2, Version = typeof(AppLogger).Assembly.GetName().Version?.ToString(),
                Build = typeof(AppLogger).Module.ModuleVersionId, ExportedAtUtc = exportedAt,
                WindowsVersion = Environment.OSVersion.Version.ToString(),
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                DotNetVersion = Environment.Version.ToString(),
                TimeZone = "UTC", MaximumArchiveBytes = limits.ArchiveBytes,
                RetainedRecords = retained.Count, OmittedForCapacity = omitted, RuntimeBytes = runtimeBytes,
                FirstRecordUtc = retained.Count == 0 ? (DateTimeOffset?)null : retained[0].Record.Timestamp,
                LastRecordUtc = retained.Count == 0 ? (DateTimeOffset?)null : retained[^1].Record.Timestamp,
                NoExceptionsCollected = records.Length == 0 && events.Length == 0,
                CollectionIncomplete = incomplete, Truncated = omitted > 0,
                LogFlushCompleted = flushed, snapshot.UnreadableFiles, snapshot.InvalidRecords,
                writer.Dropped, writer.WriteFailures, CrashCollection = collection.Status.ToString(), CrashEvents = events
            });
            const string readme = "TuckPane local exception diagnostics. ZIP <= 5 MiB; local runtime logs <= 20 MiB.\n" +
                "Only failures, timeouts, unexpected interruptions and available application crash summaries are collected.\n" +
                "Timestamps are ISO 8601 UTC; convert to your local time when reporting an incident (UTC+08:00 = UTC plus 8 hours).\n" +
                "Only approved code identifiers, error types/codes and environment versions are included. No user contents, filenames, paths, usernames, arguments, raw exception messages or stacks.\n" +
                "environment.json describes retained dates/counts, capacity omissions, collection failures and truncation. Newest complete records are retained. Old logs may have no readable code location.\n" +
                "NoExceptionsCollected does not mean a clean run. CollectionIncomplete means some evidence could not be collected. Native crashes, forced termination and rotation may leave no record.\n" +
                "Records describe observed exceptions, not proof of their root cause or successful recovery. Nothing is uploaded automatically.\n" +
                "诊断包仅含异常白名单信息，时间为 UTC；容量不足保留最新异常。未收集到异常不代表软件没有问题。采集不完整及裁剪情况见 environment.json。仅保存在本地，由用户决定是否分享。";
            if (Encoding.UTF8.GetByteCount(environment) + Encoding.UTF8.GetByteCount(readme) > limits.AuxiliaryBytes)
                throw new IOException("Diagnostic metadata budget exceeded.");

            string target = Path.GetFullPath(destination);
            string staging = Path.Combine(Path.GetDirectoryName(target)!, $".diagnostic-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    using (var output = new StreamWriter(archive.CreateEntry("runtime.jsonl").Open(), new UTF8Encoding(false)))
                    {
                        foreach (var item in retained)
                        {
                            await output.WriteAsync(item.Json.AsMemory(), cancellationToken);
                            await output.WriteAsync("\n".AsMemory(), cancellationToken);
                        }
                    }
                    await WriteEntryAsync(archive, "environment.json", environment, cancellationToken);
                    await WriteEntryAsync(archive, "README.txt", readme, cancellationToken);
                }
                // Include the central directory and compression overhead, not just entry payloads.
                if (new FileInfo(staging).Length > limits.ArchiveBytes)
                    throw new IOException("Diagnostic archive budget exceeded.");
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(staging, target, overwrite: true);
            }
            finally { try { File.Delete(staging); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }, cancellationToken);
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content, CancellationToken cancellationToken)
    {
        using var output = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        await output.WriteAsync(content.AsMemory(), cancellationToken);
    }
}
