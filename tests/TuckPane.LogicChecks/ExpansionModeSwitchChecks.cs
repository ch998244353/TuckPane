using TuckPane.Core;
using TuckPane.Models;

internal static class ExpansionModeSwitchChecks
{
    private const OrganizerExpansionMode Compact = OrganizerExpansionMode.Collapsible;
    private const OrganizerExpansionMode Expanded = OrganizerExpansionMode.AlwaysExpanded;

    internal static async Task RunAsync()
    {
        await CheckTargetsAsync();
        await CheckLatestRequestAsync();
        await CheckReadinessAndCloseAsync();
        await CheckFailuresAsync();
        CheckHoverAndPositionPath();
        Console.WriteLine("PASS --expansion-mode-switch: 5 focused groups (targets, asynchronous ownership, deferred readiness/close, failure recovery, hover guard/position continuity). Production coordinator with controlled boundary callbacks; no windows or input automation. Native lifecycle wiring and animation appearance remain manual acceptance.");
    }

    private static async Task CheckTargetsAsync()
    {
        foreach (var initial in new[] { Compact, Expanded })
        foreach (var target in new[] { Compact, Expanded })
        {
            var view = new View { Appearance = initial == Compact ? Expanded : Compact };
            var coordinator = Create(initial, view);
            await coordinator.RequestAsync(target);
            Require(view.Appearance == target && coordinator.CommittedMode == target && !coordinator.IsPending,
                "Every initial/target pair must settle, including repair of same-mode mismatched appearance.");
            Require(view.Applied.SequenceEqual([target]) && view.EndCount == 1,
                "A same-mode request must still apply, then release the interaction guard exactly once.");
        }
    }

    private static async Task CheckLatestRequestAsync()
    {
        // A save already in progress may finish; the new request must then save and apply its own target.
        var save = new Hold();
        var writes = new List<OrganizerExpansionMode>();
        var view = new View();
        var coordinator = Create(Compact, view, async (mode, _) =>
        {
            writes.Add(mode);
            if (writes.Count == 1) await save.WaitAsync();
        });
        Task old = coordinator.RequestAsync(Expanded);
        await save.Entered;
        Task latest = coordinator.RequestAsync(Compact);
        Require(coordinator.DesiredMode == Compact && view.Applied.Count == 0,
            "A new target must register during a pending save without applying obsolete state.");
        save.Release();
        await Finish(old, latest);
        Require(writes.SequenceEqual([Expanded, Compact]) && view.Applied.SequenceEqual([Compact]) &&
            coordinator.CommittedMode == Compact, "A stale successful save must be superseded before visual application.");

        // Ignore cancellation deliberately: obsolete preparation/animation may still deliver completion.
        foreach (bool duringAnimation in new[] { false, true })
        {
            var hold = new Hold();
            var newHold = new Hold();
            view = new View();
            if (duringAnimation) view.OnApply = (_, ordinal, _) => (ordinal == 1 ? hold : newHold).WaitAsync();
            coordinator = Create(Compact, view);
            old = coordinator.RequestAsync(Expanded, duringAnimation ? null : async _ => { await hold.WaitAsync(); return true; });
            await hold.Entered;
            latest = coordinator.RequestAsync(Compact, duringAnimation ? null : async _ => { await newHold.WaitAsync(); return true; });
            await newHold.Entered;
            hold.Release();
            await Finish(old);
            Require(coordinator.IsPending && !latest.IsCompleted && view.EndCount == 0 && view.Restored.Count == 0,
                "Obsolete completion must leave the newer request and its interaction guard active.");
            newHold.Release();
            await Finish(latest);
            Require(coordinator.DesiredMode == Compact && coordinator.CommittedMode == Compact &&
                !coordinator.IsPending && view.EndCount == 1 && view.Restored.Count == 0,
                "Old preparation or animation completion must not end or restore the newer request.");
            Require(view.Applied.Last() == Compact && (duringAnimation || view.Applied.Count == 1),
                "Obsolete preparation must never reach apply; reverse animation must accept the latest target.");
        }

        var animation = new Hold();
        var firstView = new View { OnApply = (_, _, _) => animation.WaitAsync() };
        var secondView = new View();
        var firstWindow = Create(Compact, firstView);
        var secondWindow = Create(Compact, secondView);
        Task first = firstWindow.RequestAsync(Expanded);
        await animation.Entered;
        await secondWindow.RequestAsync(Expanded).WaitAsync(TimeSpan.FromSeconds(5));
        Require(secondView.Applied.SequenceEqual([Expanded]) && !first.IsCompleted,
            "Another window must finish while the first window's animation is pending.");
        animation.Release();
        await Finish(first);
    }

    private static async Task CheckReadinessAndCloseAsync()
    {
        // Readiness models the production boundary for drag/resize cleanup and hidden windows.
        // No native window visibility is inferred from this test.
        var operation = new Hold();
        var view = new View { OnReady = (_, token) => operation.WaitAsync().WaitAsync(token) };
        var coordinator = Create(Compact, view);
        Task old = coordinator.RequestAsync(Expanded);
        await operation.Entered;
        Task latest = coordinator.RequestAsync(Compact);
        Task duplicate = coordinator.RequestAsync(Compact);
        Require(ReferenceEquals(latest, duplicate) && view.Applied.Count == 0 && coordinator.IsPending,
            "Pending duplicate targets must coalesce, and readiness must block all visual application.");
        operation.Release();
        await Finish(old, latest, duplicate);
        Require(view.Applied.SequenceEqual([Compact]), "Operation cleanup must release only the final requested target.");

        foreach (bool closeDuringSave in new[] { false, true })
        {
            var hold = new Hold();
            view = new View();
            if (!closeDuringSave) view.OnReady = (_, _) => hold.WaitAsync();
            coordinator = Create(Compact, view, closeDuringSave ? (_, _) => hold.WaitAsync() : null);
            Task pending = coordinator.RequestAsync(Expanded);
            await hold.Entered;
            coordinator.Close();
            hold.Release();
            await Finish(pending);
            Require(view.Applied.Count == 0 && !coordinator.IsPending,
                "Closing during preparation or persistence must prevent later visual callbacks.");
        }
    }

    private static async Task CheckFailuresAsync()
    {
        bool failSave = true;
        var view = new View();
        var coordinator = Create(Compact, view, (_, _) => failSave
            ? Task.FromException(new IOException("isolated save failure")) : Task.CompletedTask);
        await ExpectFailure(() => coordinator.RequestAsync(Expanded));
        Require(coordinator.CommittedMode == Compact && !coordinator.IsPending &&
            view.Applied.Count == 0 && view.Restored.SequenceEqual([Compact]),
            "Save failure must restore the committed target and release pending state before retry.");
        failSave = false;
        await coordinator.RequestAsync(Expanded);
        Require(view.Applied.SequenceEqual([Expanded]), "A request after save failure must still run.");

        view = new View { OnApply = (_, ordinal, _) => ordinal == 1
            ? Task.FromException(new IOException("isolated visual failure")) : Task.CompletedTask };
        coordinator = Create(Compact, view);
        await ExpectFailure(() => coordinator.RequestAsync(Expanded));
        Require(coordinator.CommittedMode == Expanded && view.Restored.SequenceEqual([Expanded]) && !coordinator.IsPending,
            "Visual failure must restore the successfully saved target, then allow another request.");
        await coordinator.RequestAsync(Compact);
        Require(view.Applied.Last() == Compact && view.EndCount == 2,
            "Visual failure must release guards so the next mode switch can finish.");

        var obsoleteSave = new Hold();
        int attempts = 0;
        view = new View();
        coordinator = Create(Compact, view, async (_, _) =>
        {
            if (++attempts == 1) { await obsoleteSave.WaitAsync(); throw new IOException("obsolete save failure"); }
        });
        Task old = coordinator.RequestAsync(Expanded);
        await obsoleteSave.Entered;
        Task latest = coordinator.RequestAsync(Compact);
        obsoleteSave.Release();
        await Finish(old, latest);
        Require(view.Restored.Count == 0 && view.Applied.SequenceEqual([Compact]) &&
            coordinator.DesiredMode == Compact && !coordinator.IsPending,
            "An obsolete save failure must not restore or cancel the new request.");

        // A rejected editor flush must not restore an older mode while an atomic save is still finishing.
        var committing = new Hold();
        view = new View();
        coordinator = Create(Compact, view, (_, _) => committing.WaitAsync());
        old = coordinator.RequestAsync(Expanded);
        await committing.Entered;
        latest = coordinator.RequestAsync(Compact, _ => Task.FromResult(false));
        Require(!latest.IsCompleted && view.Restored.Count == 0,
            "Recovery must wait for an earlier in-flight write to establish the committed mode.");
        committing.Release();
        await Finish(old, latest);
        Require(coordinator.CommittedMode == Expanded && coordinator.DesiredMode == Expanded &&
            view.Restored.SequenceEqual([Expanded]) && !coordinator.IsPending,
            "Rejected preparation must restore the actual committed target, without applying the refused request.");
    }

    private static void CheckHoverAndPositionPath()
    {
        var hover = new ExpansionHoverGuard();
        hover.SuppressUntilExit();
        hover.ObservePointer(insideEntry: true);
        hover.ObservePointer(insideEntry: true);
        Require(!hover.CanHover, "Explicit collapse must reject stale hover and a pointer that remains over the entry.");
        hover.ObservePointer(insideEntry: false);
        hover.ObservePointer(insideEntry: true);
        Require(hover.CanHover, "Hover must rearm after leaving the entry and entering again.");
        hover.SuppressUntilExit();
        Require(!hover.CanHover, "A later explicit collapse must suppress hover again after it was rearmed.");

        // Unequal diagonal anchors represent a remembered expanded position. Direction must
        // use one common progress coordinate, including near both ends of a reversal.
        var path = new TransitionPositionPath(723, -182, 61, 409);
        foreach (double progress in new[] { 0.001, 0.37, 0.999 })
        {
            var before = path.PositionAt(progress);
            var after = path.PositionAt(progress - 0.000001);
            Require(Math.Abs(before.X - after.X) < 0.001 && Math.Abs(before.Y - after.Y) < 0.001,
                "Changing direction at interior progress must stay on a continuous remembered-position path.");
            Require(before.X > 61 && before.X < 723 && before.Y > -182 && before.Y < 409,
                "An in-flight reversal must retain interior geometry rather than jump to either anchor.");
        }
    }

    private static ExpansionModeCoordinator Create(OrganizerExpansionMode initial, View view,
        Func<OrganizerExpansionMode, CancellationToken, Task>? save = null) =>
        new(initial, save ?? ((_, _) => Task.CompletedTask), view);

    private static async Task Finish(params Task[] requests)
    {
        foreach (Task task in requests)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { /* A superseded request may be represented by cancellation. */ }
        }
    }

    private static async Task ExpectFailure(Func<Task> action)
    {
        try { await action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("Expected the current request's failure to propagate.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Hold
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Entered => _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        internal Task WaitAsync() { _entered.TrySetResult(); return _release.Task; }
        internal void Release() => _release.TrySetResult();
    }

    private sealed class View : IExpansionModeView
    {
        internal OrganizerExpansionMode Appearance;
        internal readonly List<OrganizerExpansionMode> Applied = [];
        internal readonly List<OrganizerExpansionMode> Restored = [];
        internal int EndCount;
        private int _readyCount;
        internal Func<int, CancellationToken, Task>? OnReady;
        internal Func<OrganizerExpansionMode, int, CancellationToken, Task>? OnApply;
        public void Begin(OrganizerExpansionMode mode) { }
        public Task WaitUntilReadyAsync(CancellationToken token) => OnReady?.Invoke(++_readyCount, token) ?? Task.CompletedTask;
        public async Task ApplyAsync(OrganizerExpansionMode mode, CancellationToken token)
        {
            Applied.Add(mode);
            if (OnApply is not null) await OnApply(mode, Applied.Count, token);
            if (!token.IsCancellationRequested) Appearance = mode;
        }
        public void Restore(OrganizerExpansionMode committedMode) { Restored.Add(committedMode); Appearance = committedMode; }
        public void End() => EndCount++;
    }
}
