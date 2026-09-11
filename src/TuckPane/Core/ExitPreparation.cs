using TuckPane.Services;

namespace TuckPane.Core;

internal sealed class ExitPreparation
{
    private int _active;
    internal bool IsActive => Volatile.Read(ref _active) != 0;

    internal Attempt? TryBegin(TransferQueue queue) =>
        Interlocked.CompareExchange(ref _active, 1, 0) == 0 ? new(this, queue) : null;

    internal sealed class Attempt(ExitPreparation owner, TransferQueue queue) : IDisposable
    {
        private bool _committed, _disposed, _paused;
        internal bool IsCommitted => _committed;
        internal Task<bool> WaitForTransfersAsync(TimeSpan timeout)
        {
            _paused = true;
            return queue.PauseAndCancelAsync(timeout);
        }
        internal void Commit() { _committed = true; queue.CancelAll(); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_committed) return;
            if (_paused) _ = queue.ResumeWhenIdleAsync();
            Volatile.Write(ref owner._active, 0);
        }
    }
}
