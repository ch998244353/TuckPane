namespace TuckPane.Services;

public sealed class TransferQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource _batch = new();
    private CancellationTokenSource? _current;
    private TaskCompletionSource _idle = CompletedSignal();
    private int _pending;
    private bool _paused, _closed;
    private long _pauseVersion;

    public event EventHandler? StateChanged;
    public bool IsActive => Volatile.Read(ref _pending) > 0;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        CancellationTokenSource linked;
        lock (_sync)
        {
            if (_paused || _closed) throw new OperationCanceledException("Transfer queue is stopping.");
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _batch.Token);
            if (_pending++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using (linked)
        try
        {
            NotifyStateChanged();
            await _gate.WaitAsync(linked.Token);
            using var operation = AppLogger.Begin(DiagnosticArea.Transfer);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                lock (_sync) _current = linked;
                T result = await action(linked.Token);
                operation.Complete();
                return result;
            }
            catch (Exception ex) { operation.Fail(ex); throw; }
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
            NotifyStateChanged();
        }
    }

    private void NotifyStateChanged()
    {
        foreach (EventHandler handler in StateChanged?.GetInvocationList() ?? [])
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { AppLogger.Error("Transfer state subscriber failed", ex); }
        }
    }

    public void CancelCurrent()
    {
        lock (_sync)
        {
            // CancelAsync marks the token now, invoking callbacks outside the queue lock.
            if (_current is not null) ObserveCancellation(_current.CancelAsync());
        }
    }

    public void CancelAll()
    {
        lock (_sync)
        {
            _closed = true;
            _paused = true;
            ObserveCancellation(_batch.CancelAsync());
        }
    }

    internal async Task<bool> PauseAndCancelAsync(TimeSpan timeout)
    {
        lock (_sync)
        {
            _pauseVersion++;
            _paused = true;
            ObserveCancellation(_batch.CancelAsync());
        }
        return await WaitForIdleAsync(timeout);
    }

    internal async Task ResumeWhenIdleAsync()
    {
        long version;
        lock (_sync) version = _pauseVersion;
        await WaitForIdleAsync().ConfigureAwait(false);
        lock (_sync)
        {
            if (_closed || !_paused || version != _pauseVersion) return;
            _batch.Dispose();
            _batch = new();
            _paused = false;
        }
        NotifyStateChanged();
    }

    private static async void ObserveCancellation(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { AppLogger.Error("Transfer cancellation callback failed", ex); }
    }

    public Task WaitForIdleAsync()
    {
        lock (_sync) return _idle.Task;
    }

    public async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        try { await WaitForIdleAsync().WaitAsync(timeout); return true; }
        catch (TimeoutException) { return false; }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
