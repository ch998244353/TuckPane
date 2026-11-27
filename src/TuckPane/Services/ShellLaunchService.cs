using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TuckPane.Services;

internal sealed class ShellLaunchService : IDisposable
{
    private readonly object _sync = new();
    private readonly BlockingCollection<(string Path, string Key, TaskCompletionSource Completion)> _requests = new(16);
    private readonly BlockingCollection<(string Path, string Key, TaskCompletionSource Completion)> _propertyRequests = new(16);
    private readonly Dictionary<string, TaskCompletionSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _launch;
    private readonly Action<string> _properties;
    private Thread? _worker;
    private Thread? _propertyWorker;
    private bool _disposed;

    internal ShellLaunchService(Action<string>? launch = null, Action<string>? properties = null)
    {
        _launch = launch ?? (path =>
        {
            using Process? process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        });
        _properties = properties ?? ShowProperties;
    }

    internal Task OpenAsync(string path) => QueueAsync(path, properties: false);
    internal Task PropertiesAsync(string path) => QueueAsync(path, properties: true);

    private Task QueueAsync(string path, bool properties)
    {
        string fullPath = Path.GetFullPath(path);
        string key = (properties ? "properties\0" : "open\0") + fullPath;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending.TryGetValue(key, out var existing)) return existing.Task;
            if (_pending.Count >= 16) throw new InvalidOperationException(AppStrings.Get("LaunchQueueFull"));
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(key, completion);
            if (properties)
            {
                _propertyRequests.Add((fullPath, key, completion));
                if (_propertyWorker is null)
                {
                    _propertyWorker = new Thread(() => Work(_propertyRequests, _properties))
                        { IsBackground = true, Name = "TuckPane Shell properties" };
                    _propertyWorker.SetApartmentState(ApartmentState.STA);
                    _propertyWorker.Start();
                }
            }
            else
            {
                _requests.Add((fullPath, key, completion));
                if (_worker is null)
                {
                    _worker = new Thread(() => Work(_requests, _launch)) { IsBackground = true, Name = "TuckPane Shell launch" };
                    _worker.SetApartmentState(ApartmentState.STA);
                    _worker.Start();
                }
            }
            return completion.Task;
        }
    }

    private void Work(BlockingCollection<(string Path, string Key, TaskCompletionSource Completion)> requests, Action<string> invoke)
    {
        foreach (var request in requests.GetConsumingEnumerable())
        {
            lock (_sync) { if (_disposed) { request.Completion.TrySetCanceled(); continue; } }
            Exception? error = null;
            using var operation = AppLogger.Begin(DiagnosticArea.Shell);
            try { invoke(request.Path); operation.Complete(); }
            catch (Exception ex) { error = ex; operation.Fail(ex); }
            lock (_sync) _pending.Remove(request.Key);
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
            _propertyRequests.CompleteAdding();
            while (_requests.TryTake(out var request)) request.Completion.TrySetCanceled();
            while (_propertyRequests.TryTake(out var request)) request.Completion.TrySetCanceled();
            _pending.Clear();
        }
        // A native Shell call cannot safely be aborted. Never join a stuck STA from the UI.
    }

    private static void ShowProperties(string path)
    {
        // A property sheet can run its own modal message loop. Keep it on a
        // separate STA so neither the WinUI thread nor normal opens wait for it.
        int initialized = OleInitialize(IntPtr.Zero);
        Marshal.ThrowExceptionForHR(initialized);
        try
        {
            const uint SHOP_FILEPATH = 2;
            if (!SHObjectProperties(IntPtr.Zero, SHOP_FILEPATH, path, null))
                throw new InvalidOperationException(AppStrings.Get("FilePropertiesFailed"));
        }
        finally { OleUninitialize(); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(IntPtr owner, uint objectType, string path, string? propertyPage);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void OleUninitialize();
}
