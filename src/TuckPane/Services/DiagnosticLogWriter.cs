using System.Text;
using System.Threading.Channels;

namespace TuckPane.Services;

internal sealed class DiagnosticLogWriter : IAsyncDisposable
{
    internal const int MaximumFileBytes = 5 * 1024 * 1024;
    internal const int MaximumFileCount = 4;
    internal sealed record Snapshot(IReadOnlyList<DiagnosticRecord> Records, int UnreadableFiles, int InvalidRecords);
    private sealed record Entry(DiagnosticRecord? Record, TaskCompletionSource? Barrier);
    private readonly Channel<Entry> _queue;
    private readonly string _directory;
    private readonly int _maximumBytes, _fileCount;
    private readonly Action? _beforeWrite;
    private readonly object _files = new();
    private readonly Task _worker;
    private long _dropped, _writeFailures;

    internal DiagnosticLogWriter(string directory, int capacity = 2048, int maximumBytes = MaximumFileBytes,
        int fileCount = MaximumFileCount, Action? beforeWrite = null)
    {
        if (capacity < 1 || maximumBytes is < 512 or > MaximumFileBytes || fileCount is < 1 or > MaximumFileCount)
            throw new ArgumentOutOfRangeException();
        _directory = directory;
        _maximumBytes = maximumBytes;
        _fileCount = fileCount;
        _beforeWrite = beforeWrite;
        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(WriteLoopAsync);
    }

    internal long Dropped => Interlocked.Read(ref _dropped);
    internal long WriteFailures => Interlocked.Read(ref _writeFailures);
    internal void Write(DiagnosticRecord record)
    {
        DiagnosticRecord? safe = record.Sanitize();
        if (safe is null) { Interlocked.Increment(ref _dropped); return; }
        if (!safe.IsAbnormal) return;
        if (!_queue.Writer.TryWrite(new(safe, null))) Interlocked.Increment(ref _dropped);
    }

    internal async Task<bool> FlushAsync(TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _queue.Writer.WriteAsync(new(null, barrier), deadline.Token);
            await barrier.Task.WaitAsync(deadline.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (ChannelClosedException) { return _worker.IsCompleted; }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (Entry entry in _queue.Reader.ReadAllAsync())
        {
            if (entry.Barrier is not null) { entry.Barrier.TrySetResult(); continue; }
            try
            {
                _beforeWrite?.Invoke();
                DiagnosticRecord record = entry.Record! with { Dropped = Dropped, WriteFailures = WriteFailures };
                byte[] bytes = Encoding.UTF8.GetBytes(record.ToJson() + "\n");
                if (bytes.Length > _maximumBytes) { Interlocked.Increment(ref _dropped); continue; }
                lock (_files)
                {
                    Directory.CreateDirectory(_directory);
                    string path = FilePath(0);
                    if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > _maximumBytes)
                    {
                        for (int index = _fileCount - 1; index >= 1; index--)
                        {
                            string previous = FilePath(index - 1);
                            if (File.Exists(previous)) File.Move(previous, FilePath(index), overwrite: true);
                        }
                        File.Delete(path);
                    }
                    using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    output.Write(bytes);
                }
            }
            catch
            {
                Interlocked.Increment(ref _dropped);
                Interlocked.Increment(ref _writeFailures);
            }
        }
    }

    private string FilePath(int index) => Path.Combine(_directory, $"runtime.{index}.jsonl");

    internal async Task<IReadOnlyList<DiagnosticRecord>> SnapshotAsync(CancellationToken cancellationToken = default) =>
        (await SnapshotWithStatusAsync(cancellationToken)).Records;

    internal Task<Snapshot> SnapshotWithStatusAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var records = new List<DiagnosticRecord>();
        int unreadableFiles = 0, invalidRecords = 0;
        lock (_files)
        {
            for (int index = _fileCount - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = FilePath(index);
                try
                {
                    if (new FileInfo(path).Length > _maximumBytes) { unreadableFiles++; continue; }
                    foreach (string line in File.ReadLines(path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (DiagnosticRecord.ReadSafe(line) is { } record)
                        {
                            if (record.IsAbnormal) records.Add(record);
                        }
                        else invalidRecords++;
                    }
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadableFiles++; }
            }
        }
        return new Snapshot(records, unreadableFiles, invalidRecords);
    }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
    }
}
