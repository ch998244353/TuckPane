using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.ApplicationModel.DataTransfer;

internal static class StabilityInteractionChecks
{
    internal static async Task DropAsync()
    {
        CheckDropResult();
        foreach (string outcome in new[] { "success", "failure", "cancel" })
        {
            int incoming = 0, mode = 0, reset = 0, complete = 0;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task operation = IncomingDropOperation.RunAsync(() => complete++,
                () => { incoming++; return new Cleanup(() => incoming--); },
                () => { mode++; return new Cleanup(() => mode--); },
                () => release.Task, () => reset++);
            Require(!operation.IsCompleted && incoming == 1 && mode == 1 && reset == 0 && complete == 0,
                "A delayed drop must hold both guards and defer completion.");
            Exception? error = null;
            if (outcome == "failure") release.SetException(new IOException("expected drop failure"));
            else if (outcome == "cancel") release.SetCanceled();
            else release.SetResult();
            try { await operation; }
            catch (Exception ex) { error = ex; }
            Require(incoming == 0 && mode == 0 && reset == 1 && complete == 1,
                "Every drop outcome must release both guards, reset and complete exactly once.");
            Require(outcome switch { "failure" => error is IOException, "cancel" => error is OperationCanceledException, _ => error is null },
                "Drop cleanup must preserve the action's success, failure or cancellation.");
        }
        int completedAfterResetFailure = 0;
        try
        {
            await IncomingDropOperation.RunAsync(() => completedAfterResetFailure++,
                () => new Cleanup(() => { }), () => new Cleanup(() => { }),
                () => Task.CompletedTask, () => throw new IOException("expected reset failure"));
        }
        catch (IOException ex) when (ex.Message == "expected reset failure") { }
        Require(completedAfterResetFailure == 1, "A reset failure must still complete the deferral.");
        Console.WriteLine("PASS --stability drop: actual result operation mapping, delayed success/failure/cancellation guard release and reset-failure deferral completion; no drag or UI automation.");
    }

    private static void CheckDropResult()
    {
        var move = DataPackageOperation.Move;
        var copy = DataPackageOperation.Copy;
        var link = DataPackageOperation.Link;
        var none = DataPackageOperation.None;
        var all = move | copy | link;
        (DataPackageOperation Requested, DataPackageOperation Allowed, TransferStatus[] Statuses, DataPackageOperation Expected)[] cases =
        [
            (move, all, [], none),
            (move, all, [TransferStatus.Moved], move),
            (move, all, [TransferStatus.ShortcutCreated], link),
            (move, move | copy, [TransferStatus.ShortcutCreated], copy),
            (move, move, [TransferStatus.ShortcutCreated], none),
            (copy, all, [TransferStatus.Copied], copy),
            (move, all, [TransferStatus.Moved, TransferStatus.ShortcutCreated], copy),
            (move, move, [TransferStatus.Moved, TransferStatus.ShortcutCreated], none),
            (move, all, [TransferStatus.Moved, TransferStatus.Failed], none),
            (move, all, [TransferStatus.Cancelled], none),
            (move, all, [TransferStatus.CopiedSourceRetained], none),
            (move, all, [TransferStatus.Retained], none)
        ];
        foreach (var sample in cases)
        {
            var outcomes = sample.Statuses.Select(status => new TransferOutcome("fixture-source", "fixture-destination", status, "")).ToArray();
            Require(IncomingDropOperation.ResultOperation(sample.Requested, sample.Allowed, outcomes) == sample.Expected,
                $"Actual drop result mismatch for {string.Join(',', sample.Statuses)} with allowed {sample.Allowed}.");
        }
    }

    internal static async Task LaunchAsync(string root)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<(ApartmentState Apartment, bool Background, int Thread)>(TaskCreationOptions.RunContinuationsAsynchronously);
        int callerThread = Environment.CurrentManagedThreadId;
        int calls = 0;
        using var service = new ShellLaunchService(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult((Thread.CurrentThread.GetApartmentState(), Thread.CurrentThread.IsBackground, Environment.CurrentManagedThreadId));
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Controlled launch was not released by the test.");
        });
        string path = Path.Combine(root, "held.exe");
        Task held = service.OpenAsync(path);
        try
        {
            var thread = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(thread.Apartment == ApartmentState.STA && thread.Background && thread.Thread != callerThread,
                "Shell work must use a background STA distinct from the calling thread.");
            Require(ReferenceEquals(held, service.OpenAsync(path.ToUpperInvariant())),
                "A duplicate path must return the exact pending Task while launch is held.");
            var queued = Enumerable.Range(1, 15).Select(index => service.OpenAsync(Path.Combine(root, $"queued-{index}.exe"))).ToArray();
            bool full = false;
            try { _ = service.OpenAsync(Path.Combine(root, "seventeenth.exe")); }
            catch (InvalidOperationException) { full = true; }
            Require(full && calls == 1, "Sixteen pending requests must bound the queue without executing queued work concurrently.");
            await Task.Run(service.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
            Require(!held.IsCompleted && queued.All(task => task.IsCanceled),
                "Dispose must cancel queued work and return while the native-call substitute remains held.");
            release.Set();
            await held.WaitAsync(TimeSpan.FromSeconds(1));
            Require(calls == 1, "Disposed queued requests must never reach the launcher.");
        }
        finally
        {
            release.Set();
            await held.WaitAsync(TimeSpan.FromSeconds(1));
        }

        int attempts = 0;
        using var retryService = new ShellLaunchService(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("expected launcher failure");
        });
        bool failed = false;
        try { await retryService.OpenAsync(path).WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (IOException ex) when (ex.Message == "expected launcher failure") { failed = true; }
        await retryService.OpenAsync(path).WaitAsync(TimeSpan.FromSeconds(1));
        Require(failed && attempts == 2, "A failed launch must release path ownership so the user can retry.");
        Console.WriteLine("PASS --stability launch: background STA, exact Task deduplication, sixteen-request bound, failure retry and nonblocking disposal; injected launcher only, no software started.");
    }

    internal static async Task WatcherAsync()
    {
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int restarts = 0, refreshes = 0;
        using (var recovery = new WatcherRecovery((_, _) => delay.Task))
        {
            Task first = recovery.RequestAsync(() => { restarts++; return true; }, () => refreshes++);
            Task duplicate = recovery.RequestAsync(() => throw new InvalidOperationException("Duplicate restart used"),
                () => throw new InvalidOperationException("Duplicate refresh used"));
            Require(ReferenceEquals(first, duplicate) && refreshes == 1 && restarts == 0,
                "A notification burst must share one delayed recovery and one initial scan.");
            delay.SetResult();
            await first;
            Require(restarts == 1 && refreshes == 2, "Successful recreation must scan once after the restart.");
        }

        var delays = new List<TimeSpan>();
        restarts = 0;
        using (var recovery = new WatcherRecovery((duration, _) => { delays.Add(duration); return Task.CompletedTask; }))
        {
            await recovery.RequestAsync(() => { restarts++; return false; }, () => { });
            await recovery.RequestAsync(() => { restarts++; return true; }, () => { });
            Require(restarts == 4 && delays.Count == 4 && delays.Zip(delays.Skip(1)).All(pair => pair.Second > pair.First),
                "Repeated failure must stop after four increasing backoffs and reject another error burst.");
            recovery.HealthyChange();
            await recovery.RequestAsync(() => { restarts++; return true; }, () => { });
            Require(restarts == 5 && delays[^1] == delays[0], "A healthy event must reset the recovery budget and backoff.");
        }

        var staleDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken lifetime = default;
        restarts = 0;
        refreshes = 0;
        using (var recovery = new WatcherRecovery((_, token) => { lifetime = token; return staleDelay.Task; }))
        {
            Task pending = recovery.RequestAsync(() => { restarts++; return true; }, () => refreshes++);
            recovery.Dispose();
            Require(lifetime.IsCancellationRequested, "Closing must cancel the recovery lifetime.");
            staleDelay.SetResult(); // Simulate a delay that completes despite cancellation.
            await pending;
            await recovery.RequestAsync(() => { restarts++; return true; }, () => refreshes++);
            Require(restarts == 0 && refreshes == 1, "A stale continuation or later request must do no work after close.");
        }
        CheckWatcherCallbackWiring();
        Console.WriteLine("PASS --stability watcher: coalesced scan/restart, four bounded backoffs with healthy reset and stale completion after close; obsolete sender refresh wiring is source-only; controlled delays, no filesystem watcher or windows.");
    }

    private static void CheckWatcherCallbackWiring()
    {
        string main = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", "MainWindow.xaml.cs"));
        int start = main.IndexOf("private void Watcher_Changed(", StringComparison.Ordinal);
        int end = main.IndexOf("private async void WatcherDebounceTimer_Tick(", start, StringComparison.Ordinal);
        string changed = main[start..end];
        int callbackStart = changed.IndexOf("DispatcherQueue.TryEnqueue(() =>", StringComparison.Ordinal);
        int callbackEnd = changed.IndexOf("}))", callbackStart, StringComparison.Ordinal);
        string callback = changed[callbackStart..callbackEnd];
        Require(callback.Contains("if (_closing)", StringComparison.Ordinal) &&
                callback.Contains("if (ReferenceEquals(sender, _watcher)) _watcherRecovery.HealthyChange();", StringComparison.Ordinal) &&
                callback.Contains("_watcherDebounceTimer.Start();", StringComparison.Ordinal) &&
                !System.Text.RegularExpressions.Regex.IsMatch(callback, @"if\s*\([^\r\n]*sender[^\r\n]*\)[^\r\n]*return"),
            "Source-only: a queued obsolete sender must preserve the merged timer refresh; only the current sender resets recovery health.");
    }

    private sealed class Cleanup(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
