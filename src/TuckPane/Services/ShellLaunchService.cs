using System.Collections.Concurrent;
using System.Diagnostics;

namespace TuckPane.Services;

internal sealed class ShellLaunchService : IDisposable
{
    private readonly object _sync = new();
    private readonly BlockingCollection<(string Path, TaskCompletionSource Completion)> _requests = new(16);
    private readonly Dictionary<string, TaskCompletionSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _launch;
    private Thread? _worker;
    private bool _disposed;

    internal ShellLaunchService(Action<string>? launch = null) => _launch = launch ?? (path =>
    {
        using Process? process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    });

    internal Task OpenAsync(string path)
    {
        string key = Path.GetFullPath(path);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending.TryGetValue(key, out var existing)) return existing.Task;
            if (_pending.Count >= 16) throw new InvalidOperationException(AppStrings.Get("LaunchQueueFull"));
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(key, completion);
            _requests.Add((key, completion));
            if (_worker is null)
            {
                _worker = new Thread(Work) { IsBackground = true, Name = "TuckPane Shell launch" };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();
            }
            return completion.Task;
        }
    }

    private void Work()
    {
        foreach (var request in _requests.GetConsumingEnumerable())
        {
            lock (_sync) { if (_disposed) { request.Completion.TrySetCanceled(); continue; } }
            Exception? error = null;
            using var operation = AppLogger.Begin(DiagnosticArea.Shell);
            try { _launch(request.Path); operation.Complete(); }
            catch (Exception ex) { error = ex; operation.Fail(ex); }
            lock (_sync) _pending.Remove(request.Path);
            if (error is null) request.Completion.TrySetResult();
            else request.Completion.TrySetException(error);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _requests.CompleteAdding();
            while (_requests.TryTake(out var request)) request.Completion.TrySetCanceled();
            _pending.Clear();
        }
        // A native Shell call cannot safely be aborted. Never join a stuck STA from the UI.
    }
}
