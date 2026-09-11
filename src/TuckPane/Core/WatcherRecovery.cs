using TuckPane.Services;

namespace TuckPane.Core;

// All entry points and continuations belong to the owning window's dispatcher.
internal sealed class WatcherRecovery : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private Task? _running;
    private int _attempts;
    private bool _disposed;

    internal WatcherRecovery(Func<TimeSpan, CancellationToken, Task>? delay = null) => _delay = delay ?? Task.Delay;
    internal void HealthyChange() { if (!_disposed) _attempts = 0; }

    internal Task RequestAsync(Func<bool> restart, Action refresh)
    {
        if (_disposed || _attempts >= 4) return Task.CompletedTask;
        if (_running is { IsCompleted: false }) return _running;
        return _running = RecoverAsync(restart, refresh);
    }

    private async Task RecoverAsync(Func<bool> restart, Action refresh)
    {
        using var operation = AppLogger.Begin(DiagnosticArea.Watcher);
        try
        {
            // One full scan covers a burst of lost notifications.
            refresh();
            while (!_disposed && _attempts < 4)
            {
                int attempt = _attempts++;
                await _delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), _lifetime.Token);
                if (_disposed) return;
                try { if (restart()) { refresh(); operation.Complete(_attempts); return; } }
                catch (Exception ex) { AppLogger.Error("Directory watcher restart failed", ex); }
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { AppLogger.Error("Directory watcher recovery failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        // Continuations may still inspect the token after cancellation.
    }
}
