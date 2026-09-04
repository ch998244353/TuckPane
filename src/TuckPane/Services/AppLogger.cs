using System.Threading.Channels;

namespace TuckPane.Services;

public static class AppLogger
{
    private static readonly object Gate = new();
    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private static readonly object FlushGate = new();
    private static Task? _writerTask;
    private static TaskCompletionSource _drained = CompletedSignal();
    private static int _pending;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);
    public static bool PerformanceTraceEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("TUCKPANE_PERF_TRACE"), "1", StringComparison.Ordinal);

    public static void Performance(string message)
    {
        if (PerformanceTraceEnabled) Info($"[PERF] {message}");
    }

    public static async Task FlushAsync()
    {
        Task drained;
        lock (FlushGate) drained = _drained.Task;
        await drained.ConfigureAwait(false);
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            AppPaths.EnsureCreated();
            string line = $"{DateTimeOffset.Now:O} [{level}] {message}{(exception is null ? string.Empty : Environment.NewLine + exception)}{Environment.NewLine}";
            bool queued;
            lock (FlushGate)
            {
                EnsureWriter();
                queued = Queue.Writer.TryWrite(line);
                if (queued && _pending++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (!queued && level == "ERROR")
            {
                // Never lose an error solely because the bounded queue is full.
                lock (Gate) File.AppendAllText(AppPaths.LogPath, line);
            }
        }
        catch
        {
            // Logging must never take down the widget.
        }
    }

    private static void EnsureWriter()
    {
        if (_writerTask is { IsCompleted: false }) return;
        _writerTask = Task.Run(WriterLoopAsync);
    }

    private static async Task WriterLoopAsync()
    {
        try
        {
            await foreach (string first in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var entries = new List<string>(8) { first };
                int length = first.Length;
                while (length < 32 * 1024 && Queue.Reader.TryRead(out string? next))
                {
                    entries.Add(next);
                    length += next.Length;
                }
                try
                {
                    lock (Gate) File.AppendAllText(AppPaths.LogPath, string.Concat(entries));
                }
                catch
                {
                    // Logging must never take down the widget.
                }

                lock (FlushGate)
                {
                    _pending = Math.Max(0, _pending - entries.Count);
                    if (_pending == 0) _drained.TrySetResult();
                }
            }
        }
        catch
        {
            // Logging must never take down the widget.
            lock (FlushGate)
            {
                _pending = 0;
                _drained.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
