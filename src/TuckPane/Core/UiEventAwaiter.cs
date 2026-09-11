namespace TuckPane.Core;

// Subscribe and clean up on the caller's UI context, including cancellation/timeout.
// A hidden window is not required to produce a rendering or Loaded event.
internal static class UiEventAwaiter
{
    internal static async Task<bool> WaitAsync(
        Action<Action> subscribe, Action<Action> unsubscribe, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Complete() => completion.TrySetResult();
        try
        {
            subscribe(Complete);
            await completion.Task.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException) { return false; }
        finally { unsubscribe(Complete); }
    }
}
