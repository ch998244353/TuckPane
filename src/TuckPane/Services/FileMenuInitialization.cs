namespace TuckPane.Services;

// Bounds preparation only. A visible menu and an invoked command have no deadline.
internal static class FileMenuInitialization
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync(Func<CancellationToken, Task> initialize, Action abandon,
        CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        using var deadline = new CancellationTokenSource(Timeout, timeProvider ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await initialize(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            abandon();
            throw new TimeoutException(AppStrings.Get("FileMenuInitializationTimeout"));
        }
        catch
        {
            abandon();
            throw;
        }
    }
}
