namespace TuckPane.Core;

// Captures redirects before XAML exists and drains them once, in order, on the UI dispatcher.
internal sealed class ActivationInbox<T>
{
    private readonly object _gate = new();
    private readonly Queue<T> _pending = new();
    private Func<T, Task>? _handle;
    private Func<Func<Task>, bool>? _dispatch;
    private Action<Exception>? _report;
    private bool _scheduled;

    internal void Enqueue(T activation)
    {
        lock (_gate) _pending.Enqueue(activation);
        Schedule();
    }

    internal void Start(Func<T, Task> handle, Func<Func<Task>, bool> dispatch, Action<Exception> report)
    {
        lock (_gate)
        {
            if (_handle is not null) throw new InvalidOperationException("Activation inbox already started.");
            _handle = handle;
            _dispatch = dispatch;
            _report = report;
        }
        Schedule();
    }

    private void Schedule()
    {
        Func<Func<Task>, bool> dispatch;
        lock (_gate)
        {
            if (_scheduled || _dispatch is null || _pending.Count == 0) return;
            _scheduled = true;
            dispatch = _dispatch;
        }
        if (dispatch(DrainAsync)) return;
        lock (_gate) _scheduled = false;
        _report?.Invoke(new InvalidOperationException("The activation dispatcher is unavailable."));
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            T activation;
            lock (_gate)
            {
                if (_pending.Count == 0) { _scheduled = false; return; }
                activation = _pending.Dequeue();
            }
            try { await _handle!(activation); }
            catch (Exception ex) { _report?.Invoke(ex); }
        }
    }
}
