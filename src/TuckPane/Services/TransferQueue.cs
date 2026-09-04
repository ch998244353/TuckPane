namespace TuckPane.Services;

public sealed class TransferQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _all = new();
    private CancellationTokenSource? _current;
    private TaskCompletionSource _idle = CompletedSignal();
    private int _pending;

    public event EventHandler? StateChanged;
    public bool IsActive => Volatile.Read(ref _pending) > 0;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_pending++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _all.Token);
        try
        {
            await _gate.WaitAsync(linked.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                lock (_sync) _current = linked;
                return await action(linked.Token);
            }
            finally
            {
                lock (_sync) _current = null;
                _gate.Release();
            }
        }
        finally
        {
            lock (_sync)
            {
                if (--_pending == 0) _idle.TrySetResult();
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void CancelCurrent()
    {
        lock (_sync) _current?.Cancel();
    }

    public void CancelAll()
    {
        _all.Cancel();
        CancelCurrent();
    }

    public Task WaitForIdleAsync()
    {
        lock (_sync) return _idle.Task;
    }

    public async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        Task idle = WaitForIdleAsync();
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            await idle;
            return true;
        }
        Task completed = await Task.WhenAny(idle, Task.Delay(timeout));
        return ReferenceEquals(completed, idle);
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
