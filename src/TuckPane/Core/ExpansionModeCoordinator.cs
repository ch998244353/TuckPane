using TuckPane.Models;

namespace TuckPane.Core;

// Owned by the UI context. Saving may finish after cancellation; rendering must not.
internal interface IExpansionModeView
{
    void Begin(OrganizerExpansionMode mode);
    Task WaitUntilReadyAsync(CancellationToken cancellationToken);
    Task ApplyAsync(OrganizerExpansionMode mode, CancellationToken cancellationToken);
    void Restore(OrganizerExpansionMode committedMode);
    void End();
}

internal sealed class ExpansionModeCoordinator(
    OrganizerExpansionMode initialMode,
    Func<OrganizerExpansionMode, CancellationToken, Task> save,
    IExpansionModeView? view)
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Request? _latest;
    private bool _closed;

    internal OrganizerExpansionMode DesiredMode { get; private set; } = initialMode;
    internal OrganizerExpansionMode CommittedMode { get; private set; } = initialMode;
    internal bool IsPending => _latest is not null;

    internal Task RequestAsync(OrganizerExpansionMode mode, Func<CancellationToken, Task<bool>>? prepare = null)
    {
        if (_closed) return Task.CompletedTask;
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (_latest is { } pending && DesiredMode == mode && prepare is null) return pending.Completion.Task;
        Request? previous = _latest;
        var request = new Request();
        _latest = request;
        DesiredMode = mode;
        previous?.Cancellation.Cancel();
        _ = ExecuteAsync(request, mode, prepare);
        return request.Completion.Task;
    }

    internal void Close()
    {
        _closed = true;
        _latest?.Cancellation.Cancel();
    }

    private bool IsCurrent(Request request) => !_closed && ReferenceEquals(_latest, request);

    private async Task ExecuteAsync(Request request, OrganizerExpansionMode mode, Func<CancellationToken, Task<bool>>? prepare)
    {
        Exception? failure = null;
        CancellationToken token = request.Cancellation.Token;
        try
        {
            view?.Begin(mode);
            if (prepare is not null)
            {
                bool ready = await prepare(token);
                token.ThrowIfCancellationRequested();
                if (!ready)
                {
                    throw new PreparationDeclinedException();
                }
            }
            if (view is not null) await view.WaitUntilReadyAsync(token);
            token.ThrowIfCancellationRequested();
            await _saveGate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (CommittedMode != mode)
                {
                    await save(mode, token);
                    // An atomic write cannot be withdrawn halfway through. The next request
                    // saves after it, even when this request has become obsolete meanwhile.
                    CommittedMode = mode;
                }
            }
            finally { _saveGate.Release(); }
            token.ThrowIfCancellationRequested();
            if (view is not null) await view.ApplyAsync(mode, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrent(request))
            {
                // A superseded atomic write may still be committing. Recover only
                // after it finishes, using the mode that actually reached storage.
                await _saveGate.WaitAsync();
                try
                {
                    if (IsCurrent(request))
                    {
                        failure = ex is PreparationDeclinedException ? null : ex;
                        DesiredMode = CommittedMode;
                        try { view?.Restore(CommittedMode); }
                        catch (Exception recoveryError) { failure = new AggregateException(ex, recoveryError); }
                    }
                }
                finally { _saveGate.Release(); }
            }
        }
        finally
        {
            if (ReferenceEquals(_latest, request))
            {
                _latest = null;
                if (!_closed)
                {
                    try { view?.End(); }
                    catch (Exception ex) { failure ??= ex; }
                }
            }
            request.Cancellation.Dispose();
            if (failure is null) request.Completion.TrySetResult();
            else request.Completion.TrySetException(failure);
        }
    }

    private sealed class Request
    {
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PreparationDeclinedException : Exception { }
}

internal sealed class ExpansionHoverGuard
{
    internal bool CanHover { get; private set; } = true;
    internal void SuppressUntilExit() => CanHover = false;
    internal void ObservePointer(bool insideEntry)
    {
        if (!insideEntry) CanHover = true;
    }
}

// One path is retained through direction changes; progress and velocity are not reset.
internal readonly record struct TransitionPositionPath(double ExpandedX, double ExpandedY, double CompactX, double CompactY)
{
    internal (double X, double Y) PositionAt(double progress)
    {
        double p = Math.Clamp(progress, 0, 1);
        return (CompactX + (ExpandedX - CompactX) * p, CompactY + (ExpandedY - CompactY) * p);
    }
}
