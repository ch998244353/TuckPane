using System.Runtime.InteropServices;

namespace TuckPane.Core;

/// <summary>Optional platform events must not prevent the owning window from opening or closing.</summary>
internal sealed class OptionalEventSubscription : IDisposable
{
    private Action? _unsubscribe;
    private readonly Action<Exception> _log;

    private OptionalEventSubscription(Action unsubscribe, Action<Exception> log)
    {
        _unsubscribe = unsubscribe;
        _log = log;
    }

    internal static OptionalEventSubscription? TryCreate(Action subscribe, Action unsubscribe, Action<Exception> log)
    {
        try
        {
            subscribe();
            return new OptionalEventSubscription(unsubscribe, log);
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException)
        {
            log(ex);
            return null;
        }
    }

    public void Dispose()
    {
        Action? unsubscribe = Interlocked.Exchange(ref _unsubscribe, null);
        if (unsubscribe is null) return;
        try { unsubscribe(); }
        catch (Exception ex) when (ex is COMException or NotSupportedException) { _log(ex); }
    }
}
