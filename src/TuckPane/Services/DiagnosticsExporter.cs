using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TuckPane.Services;

internal static class DiagnosticsExporter
{
    private static readonly object EventGate = new();
    private static Task<IReadOnlyList<WindowsCrashEvent>>? _eventCollection;
    internal static async Task ExportAsync(string destination, CancellationToken cancellationToken = default,
        DiagnosticLogWriter? writer = null,
        Func<CancellationToken, Task<IReadOnlyList<WindowsCrashEvent>>>? readEvents = null)
    {
        writer ??= AppLogger.Writer;
        await writer.FlushAsync(TimeSpan.FromSeconds(2));
        IReadOnlyList<WindowsCrashEvent> events = [];
        using var eventDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        eventDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        CancellationToken eventToken = eventDeadline.Token;
        try
        {
            // Start the entire collector off the caller's UI thread, including Process.Start.
            Task<IReadOnlyList<WindowsCrashEvent>> collection;
            if (readEvents is not null) collection = Task.Run(() => readEvents(eventToken), eventToken);
            else lock (EventGate)
            {
                // A timeout cannot stop a synchronous native start. Do not accumulate collectors.
                if (_eventCollection is null || _eventCollection.IsCompleted)
                    _eventCollection = Task.Run(() => WindowsCrashEvents.ReadAsync(eventToken), eventToken);
                collection = _eventCollection;
            }
            events = await collection.WaitAsync(eventToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        IReadOnlyList<DiagnosticRecord> records = await writer.SnapshotAsync(cancellationToken);
        await Task.Run(async () =>
        {
            string target = Path.GetFullPath(destination);
            string staging = Path.Combine(Path.GetDirectoryName(target)!, $".diagnostic-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    await WriteEntryAsync(archive, "runtime.jsonl", string.Join("\n", records.Select(record => record.ToJson())), cancellationToken);
                    await WriteEntryAsync(archive, "environment.json", JsonSerializer.Serialize(new
                    {
                        Format = 1, Version = typeof(AppLogger).Assembly.GetName().Version?.ToString(),
                        Build = typeof(AppLogger).Module.ModuleVersionId,
                        WindowsVersion = Environment.OSVersion.Version.ToString(),
                        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        DotNetVersion = Environment.Version.ToString(),
                        writer.Dropped, writer.WriteFailures,
                        CrashEvents = events
                    }), cancellationToken);
                    await WriteEntryAsync(archive, "README.txt",
                        "TuckPane local diagnostics. No document contents, file paths, raw exception messages or historical raw logs are included.\n" +
                        "Missing final records or unavailable Windows events do not prove a clean exit. Native crashes and forced termination may leave no final record.\n" +
                        "Started without a terminal stage identifies the last observed operation, not a proven cause. A Shell completion means Windows accepted the request, not that a target window opened.\n" +
                        "记录缺失不能证明正常退出；原生崩溃或强制结束可能不留下最后记录。诊断包仅保存在本地，由用户决定是否分享。", cancellationToken);
                }
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
