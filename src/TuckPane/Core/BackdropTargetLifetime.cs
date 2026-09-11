using System.Runtime.CompilerServices;

namespace TuckPane.Core;

// Holds a projected native target until its thread-affine Close has completed.
// Disconnect only queues work: the framework must first finish unlinking it.
internal sealed class BackdropTargetLifetime<T>(
    Func<bool> hasThreadAccess,
    Func<Action, bool> enqueue,
    Action<T> close) where T : class
{
    private sealed class Connection
    {
        internal long Version;
        internal bool Connected;
        internal bool Closing;
    }

    private readonly Dictionary<T, Connection> _targets = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<T, object> _closed = new();

    internal int RetainedCount => _targets.Count;

    internal bool IsConnected(T target)
    {
        VerifyThread();
        return _targets.TryGetValue(target, out Connection? connection) && connection.Connected;
    }

    internal void Connect(T target)
    {
        VerifyThread();
        if (_closed.TryGetValue(target, out _)) throw new ObjectDisposedException(nameof(target));
        if (!_targets.TryGetValue(target, out Connection? connection))
            _targets.Add(target, connection = new());
        if (connection.Closing) throw new InvalidOperationException("The backdrop target is being closed.");
        connection.Connected = true;
        connection.Version++;
    }

    internal void Disconnect(T target)
    {
        VerifyThread();
        if (!_targets.TryGetValue(target, out Connection? connection) || !connection.Connected) return;
        connection.Connected = false;
        long version = ++connection.Version;
        if (!enqueue(() => CloseDisconnected(target, connection, version)))
            throw new InvalidOperationException("Backdrop cleanup could not be queued; the owner must drain it before shutdown.");
    }

    internal void DrainDisconnected()
    {
        VerifyThread();
        List<Exception>? failures = null;
        foreach ((T target, Connection connection) in _targets.ToArray())
        {
            try { CloseDisconnected(target, connection, connection.Version); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
        }
        if (failures is not null) throw new AggregateException("Backdrop targets could not be closed.", failures);
    }

    private void CloseDisconnected(T target, Connection connection, long version)
    {
        VerifyThread();
        if (connection.Connected || connection.Closing || connection.Version != version ||
            !_targets.TryGetValue(target, out Connection? current) || !ReferenceEquals(current, connection)) return;
        connection.Closing = true;
        try
        {
            close(target);
            _closed.Add(target, new object());
            _targets.Remove(target);
        }
        finally { connection.Closing = false; }
    }

    private void VerifyThread()
    {
        if (!hasThreadAccess())
            throw new InvalidOperationException("Backdrop lifecycle operations require the owning UI thread.");
    }
}
