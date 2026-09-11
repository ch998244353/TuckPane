using TuckPane.Core;
using TuckPane.Services;

internal static class StabilityLifecycleChecks
{
    internal static async Task RunAsync()
    {
        await TimeoutReleasesExitGateAndAllowsRecoveryAsync();
        await StaleResumeCannotUndoNewExitPauseAsync();
        await CommitPermanentlyClosesQueueAsync();
        await PrecommitFailureReleasesExitGateAsync();
        Console.WriteLine("PASS --stability lifecycle: held transfer timeout/retry/recovery, stale resume isolation, committed closure and precommit exception cleanup; no windows, power actions or process exit.");
    }

    private static async Task TimeoutReleasesExitGateAndAllowsRecoveryAsync()
    {
        var queue = new TransferQueue();
        var preparation = new ExitPreparation();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken transferToken = default;
        Task<int> transfer = queue.RunAsync(token =>
        {
            transferToken = token;
            return release.Task; // Deliberately does not finish when cancellation is requested.
        });
        try
        {
            using (var attempt = preparation.TryBegin(queue) ?? throw new InvalidOperationException("First exit rejected."))
            {
                Require(preparation.TryBegin(queue) is null, "An active exit must reject duplicate attempts.");
                Require(!await attempt.WaitForTransfersAsync(TimeSpan.Zero), "A held transfer must time out.");
                Require(transferToken.IsCancellationRequested && !transfer.IsCompleted && !attempt.IsCommitted,
                    "Timeout must request cancellation without completing the transfer or committing exit.");
            }
            Require(!preparation.IsActive, "Disposing an uncommitted attempt must release the exit gate.");
            using (var retry = preparation.TryBegin(queue) ?? throw new InvalidOperationException("Exit retry remained blocked."))
            {
                Require(!await retry.WaitForTransfersAsync(TimeSpan.Zero) && !retry.IsCommitted,
                    "A retry must still defer exit while the same transfer is held.");
            }
            release.SetResult(7);
            Require(await transfer.WaitAsync(TimeSpan.FromSeconds(1)) == 7, "The held operation must preserve its result.");
            await queue.ResumeWhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Require(await queue.RunAsync(_ => Task.FromResult(11)) == 11,
                "An abandoned exit must permit a new transfer after the old one becomes idle.");
        }
        finally
        {
            release.TrySetResult(7);
            await transfer.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    private static async Task StaleResumeCannotUndoNewExitPauseAsync()
    {
        var queue = new TransferQueue();
        var preparation = new ExitPreparation();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> transfer = queue.RunAsync(_ => release.Task);
        try
        {
            var first = preparation.TryBegin(queue) ?? throw new InvalidOperationException("First exit rejected.");
            Require(!await first.WaitForTransfersAsync(TimeSpan.Zero), "Held transfer must defer the first exit.");
            Task staleResume = queue.ResumeWhenIdleAsync();
            first.Dispose();
            using var second = preparation.TryBegin(queue) ?? throw new InvalidOperationException("Second exit rejected.");
            Require(!await second.WaitForTransfersAsync(TimeSpan.Zero), "The second exit must install its own pause.");
            release.SetResult(19);
            await transfer.WaitAsync(TimeSpan.FromSeconds(1));
            await staleResume.WaitAsync(TimeSpan.FromSeconds(1));
            bool actionRan = false;
            bool rejected = false;
            try { await queue.RunAsync(_ => { actionRan = true; return Task.FromResult(1); }); }
            catch (OperationCanceledException) { rejected = true; }
            Require(rejected && !actionRan && preparation.IsActive && !second.IsCommitted,
                "An older resume completing after a new exit pause must not admit new transfers.");
        }
        finally
        {
            release.TrySetResult(19);
            await transfer.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    private static async Task CommitPermanentlyClosesQueueAsync()
    {
        var queue = new TransferQueue();
        var preparation = new ExitPreparation();
        using (var attempt = preparation.TryBegin(queue) ?? throw new InvalidOperationException("Exit rejected."))
        {
            Require(await attempt.WaitForTransfersAsync(TimeSpan.Zero), "An idle queue must permit exit preparation.");
            attempt.Commit();
            Require(attempt.IsCommitted, "Commit must publish its terminal state.");
        }
        await queue.ResumeWhenIdleAsync();
        bool actionRan = false;
        bool rejected = false;
        try { await queue.RunAsync(_ => { actionRan = true; return Task.FromResult(1); }); }
        catch (OperationCanceledException) { rejected = true; }
        Require(rejected && !actionRan && preparation.TryBegin(queue) is null,
            "Committed exit must keep the exit gate and refuse new transfers even after Resume.");
    }

    private static async Task PrecommitFailureReleasesExitGateAsync()
    {
        var queue = new TransferQueue();
        var preparation = new ExitPreparation();
        try
        {
            using var attempt = preparation.TryBegin(queue) ?? throw new InvalidOperationException("Exit rejected.");
            Require(await attempt.WaitForTransfersAsync(TimeSpan.Zero), "The empty queue must be ready.");
            throw new IOException("expected document save failure before commit");
        }
        catch (IOException ex) when (ex.Message == "expected document save failure before commit") { }
        Require(!preparation.IsActive, "A precommit exception must release the exit gate through using cleanup.");
        await queue.ResumeWhenIdleAsync();
        Require(await queue.RunAsync(_ => Task.FromResult(13)) == 13, "Precommit failure must not permanently close the queue.");
        using var retry = preparation.TryBegin(queue) ?? throw new InvalidOperationException("Retry after save failure rejected.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
