namespace TuckPane.Core;

internal sealed class FileContextMenuGate
{
    private int _active;
    internal bool IsActive => Volatile.Read(ref _active) != 0;

    internal IDisposable? TryEnter() =>
        Interlocked.CompareExchange(ref _active, 1, 0) == 0 ? new Lease(this) : null;

    private sealed class Lease(FileContextMenuGate owner) : IDisposable
    {
        private FileContextMenuGate? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } current)
                Volatile.Write(ref current._active, 0);
        }
    }
}
