using TuckPane.Core;
using TuckPane.Services;
using Windows.Foundation;
using Point = Windows.Foundation.Point;

internal static class FileMenuRecoveryChecks
{
    internal static async Task RunAsync()
    {
        CheckPosition();
        await CheckOperationRecovery();
        await CheckInvocation();
        await CheckInitialization();
        await CheckNativeGate();
        FontFileMenu422Checks.CheckModernMenuSource();
        Console.WriteLine("PASS file menu recovery: captured coordinates, cancellation/reentry, single opaque invocation and controlled initialization deadline (fake sessions; no GUI or native Shell acceptance).");
    }

    private static void CheckPosition()
    {
        Rect item = new(20, 30, 80, 40);
        Require(FileMenuPosition.Capture(new Point(42, 65), item, new Point(100, 200), 1) == new Point(142, 265),
            "Mouse position must use the event coordinate, not the item edge.");
        Require(FileMenuPosition.Capture(new Point(11, 13), item, new Point(-1920, -200), 1.5) == new Point(-1904, -180),
            "150% scaling must round physical coordinates while preserving a negative screen origin.");
        Require(FileMenuPosition.Capture(null, item, new Point(100, 200), 1.5) == new Point(130, 305),
            "Keyboard invocation must use the item's left/bottom edge.");
    }

    private static async Task CheckOperationRecovery()
    {
        var gate = new FileContextMenuGate();
        foreach (bool cancel in new[] { true, false })
        {
            var trace = new List<string>();
            var session = new FakeSession(trace);
            using var cancellation = new CancellationTokenSource();
            Task run = Run(gate, trace, session, cancellation.Token);
            Require(gate.IsActive && !run.IsCompleted && string.Join(',', trace) == "enter,show", "Pending show must hold protection.");
            await RejectDuplicate(gate);
            if (cancel)
            {
                cancellation.Cancel();
                await ExpectFailure<OperationCanceledException>(run);
            }
            else
            {
                var failure = new ExpectedFailure();
                session.Completion.SetException(failure);
                await ExpectFailure<ExpectedFailure>(run, failure);
            }
            Require(!gate.IsActive && string.Join(',', trace) == "enter,show,dispose,leave",
                "Cancellation or show failure must dispose and release once, even when show ignores its token.");
            if (cancel)
            {
                session.Completion.SetResult(new FileMenuSelection(777, null));
                await session.Completion.Task;
                Require(session.InvocationCount == 0 && string.Join(',', trace) == "enter,show,dispose,leave",
                    "Late completion after cancellation must not invoke, refresh or repeat cleanup.");
            }
            var nextTrace = new List<string>();
            var next = new FakeSession(nextTrace);
            Task nextRun = Run(gate, nextTrace, next);
            Require(gate.IsActive && !nextRun.IsCompleted, "Recovery must permit a new operation to enter.");
            next.Completion.SetResult(null);
            await nextRun.WaitAsync(TimeSpan.FromSeconds(2));
            Require(!gate.IsActive && string.Join(',', nextTrace) == "enter,show,dispose,leave", "The next dismissed menu must release its own resources.");
        }
    }

    private static async Task CheckInvocation()
    {
        foreach (bool fail in new[] { false, true })
        {
            var trace = new List<string>();
            var gate = new FileContextMenuGate();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            FileMenuSelection expected = new(932, null);
            var session = new FakeSession(trace, (selection, token) =>
            {
                Require(selection == expected && token == cancellation.Token, "Opaque command ID/token changed before invocation.");
                entered.SetResult();
                return finish.Task;
            });
            session.Completion.SetResult(expected);
            Task run = Run(gate, trace, session, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(gate.IsActive && !run.IsCompleted && session.InvocationCount == 1, "Command execution must remain asynchronous and hold protection.");
            await RejectDuplicate(gate);
            if (fail)
            {
                var failure = new ExpectedFailure();
                finish.SetException(failure);
                await ExpectFailure<ExpectedFailure>(run, failure);
            }
            else
            {
                finish.SetResult();
                await run.WaitAsync(TimeSpan.FromSeconds(2));
            }
            Require(!gate.IsActive && session.InvocationCount == 1 && string.Join(',', trace) ==
                (fail ? "enter,show,invoke,dispose,leave" : "enter,show,invoke,dispose,refresh,leave"),
                "An opaque command must run once and release protection on success or failure.");
        }
    }

    private static async Task CheckInitialization()
    {
        foreach (string outcome in new[] { "timeout", "cancel", "failure", "success" })
        {
            var clock = new ManualTimeProvider();
            var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            int abandoned = 0, shown = 0;
            CancellationToken initializationToken = default;
            async Task InitializeThenShow()
            {
                await FileMenuInitialization.RunAsync(token =>
                {
                    initializationToken = token;
                    return initialized.Task; // Deliberately ignore cancellation, like an unresponsive extension.
                }, () => abandoned++, cancellation.Token, clock);
                shown++;
            }
            Task run = InitializeThenShow();
            Require(clock.DueTime == TimeSpan.FromSeconds(5) && !run.IsCompleted, "Only initialization must receive the five-second deadline.");
            if (outcome == "timeout")
            {
                clock.Fire();
                await ExpectFailure<TimeoutException>(run);
            }
            else if (outcome == "cancel")
            {
                cancellation.Cancel();
                await ExpectFailure<OperationCanceledException>(run);
            }
            else if (outcome == "failure")
            {
                var failure = new ExpectedFailure();
                initialized.SetException(failure);
                await ExpectFailure<ExpectedFailure>(run, failure);
            }
            else
            {
                initialized.SetResult();
                await run.WaitAsync(TimeSpan.FromSeconds(2));
            }
            clock.Fire();
            Require(clock.IsDisposed && abandoned == (outcome == "success" ? 0 : 1) && shown == (outcome == "success" ? 1 : 0),
                "Initialization must dispose its deadline and abandon failures exactly once.");
            if (outcome is "timeout" or "cancel")
            {
                Require(initializationToken.IsCancellationRequested, "Abandoned initialization must receive cancellation.");
                initialized.SetResult();
                await initialized.Task;
                Require(shown == 0 && abandoned == 1, "A late initialization result must never enter show or repeat cleanup.");
            }
            if (outcome == "success")
                Require(!initializationToken.IsCancellationRequested, "The disposed preparation deadline must not cancel the visible menu.");
        }
    }

    private static async Task CheckNativeGate()
    {
        var gate = new FileContextMenuGate();
        IDisposable lease = gate.TryEnter() ?? throw new InvalidOperationException("Native gate entry failed.");
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task ReleaseWhenTerminated() { await terminated.Task; lease.Dispose(); }
        Task release = ReleaseWhenTerminated();
        Require(gate.IsActive && gate.TryEnter() is null, "Returning to the UI must not admit another native worker before the owner has terminated.");
        terminated.SetResult();
        await release.WaitAsync(TimeSpan.FromSeconds(2));
        using IDisposable? next = gate.TryEnter();
        Require(next is not null, "Native worker termination must permit another session.");
        lease.Dispose();
        Require(gate.IsActive, "Disposing the old native lease again must not release the new owner.");
    }

    private static Task Run(FileContextMenuGate gate, List<string> trace, FakeSession session, CancellationToken token = default) =>
        FileContextMenuOperation.RunAsync(() => trace.Add("enter"), () => Task.FromResult(true), () => session,
            _ => Task.FromResult(false), () => { trace.Add("refresh"); return Task.CompletedTask; },
            () => trace.Add("leave"), cancellationToken: token, gate: gate);

    private static Task RejectDuplicate(FileContextMenuGate gate) => FileContextMenuOperation.RunAsync(
        () => throw new InvalidOperationException("Duplicate entered protection."),
        () => throw new InvalidOperationException("Duplicate prepared documents."),
        () => throw new InvalidOperationException("Duplicate created a session."),
        _ => throw new InvalidOperationException("Duplicate dispatched a command."),
        () => throw new InvalidOperationException("Duplicate refreshed files."),
        () => throw new InvalidOperationException("Duplicate released protection."), gate: gate);

    private static async Task ExpectFailure<T>(Task task, Exception? expected = null) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (T error) when (task.IsCompleted && (expected is null || ReferenceEquals(error, expected))) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name} to propagate.");
    }

    private sealed class FakeSession(List<string> trace, Func<FileMenuSelection, CancellationToken, Task>? invoke = null) : IFileContextMenuSession
    {
        internal TaskCompletionSource<FileMenuSelection?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int InvocationCount { get; private set; }
        public Task<FileMenuSelection?> ShowAsync(CancellationToken cancellationToken = default)
        {
            trace.Add("show");
            return Completion.Task;
        }
        public Task InvokeAsync(FileMenuSelection selection, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            trace.Add("invoke");
            return invoke?.Invoke(selection, cancellationToken) ?? throw new InvalidOperationException("Unexpected command invocation.");
        }
        public void Dispose() => trace.Add("dispose");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private ManualTimer? _timer;
        internal TimeSpan DueTime { get; private set; }
        internal bool IsDisposed => _timer?.IsDisposed == true;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Require(_timer is null && period == Timeout.InfiniteTimeSpan, "Initialization deadline must use one one-shot timer.");
            DueTime = dueTime;
            return _timer = new ManualTimer(callback, state);
        }
        internal void Fire() => _timer?.Fire();
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            internal bool IsDisposed { get; private set; }
            internal void Fire() { if (!IsDisposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
            public void Dispose() => IsDisposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ExpectedFailure : Exception { }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
