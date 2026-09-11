using TuckPane.Core;

internal static class BackdropLifetimeChecks
{
    internal static void Run()
    {
        DeferredCloseKeepsOwnership();
        ReconnectInvalidatesOldWork();
        DifferentTargetsKeepIndependentOwnership();
        FailedInitializationAndPermanentShutdown();
        CleanupFailuresRemainRetryable();
        Console.WriteLine("PASS backdrop-lifetime: 5 focused lifecycle groups (simulated queue/thread; no UI or GC).");
    }

    private static void DeferredCloseKeepsOwnership()
    {
        var fixture = new Fixture();
        var target = new Target();
        fixture.Lifetime.Connect(target);
        Require(fixture.Lifetime.RetainedCount == 1, "Connecting must retain the target.");
        fixture.Lifetime.Disconnect(target);
        Require(fixture.CloseAttempts == 0 && fixture.Lifetime.RetainedCount == 1,
            "Disconnect must defer close and retain the disconnected target.");
        Require(fixture.Queue.Count > 0, "Disconnect must schedule deferred cleanup.");

        fixture.HasThreadAccess = false;
        Throws<InvalidOperationException>(fixture.Lifetime.DrainDisconnected);
        Require(fixture.CloseAttempts == 0 && fixture.Lifetime.RetainedCount == 1,
            "An off-thread drain must neither close nor release a target.");
        fixture.HasThreadAccess = true;
        fixture.RunQueued();
        Require(fixture.Closed.Count == 1 && fixture.Lifetime.RetainedCount == 0,
            "The owner queue must close and then release the disconnected target.");
    }

    private static void ReconnectInvalidatesOldWork()
    {
        var fixture = new Fixture();
        var target = new Target();
        fixture.Lifetime.Connect(target);
        fixture.Lifetime.Disconnect(target);
        fixture.Lifetime.Disconnect(target);
        fixture.Lifetime.Connect(target);
        fixture.RunQueued();
        Require(fixture.CloseAttempts == 0 && fixture.Lifetime.RetainedCount == 1,
            "Stale disconnect work must not close a reconnected target.");

        fixture.Lifetime.Disconnect(target);
        fixture.Lifetime.Disconnect(target);
        fixture.Lifetime.DrainDisconnected();
        fixture.RunQueued();
        fixture.Lifetime.Disconnect(target);
        fixture.Lifetime.DrainDisconnected();
        Require(fixture.CloseAttempts == 1 && fixture.Lifetime.RetainedCount == 0,
            "Repeated disconnect, drain and stale callbacks must close exactly once.");
        Throws<ObjectDisposedException>(() => fixture.Lifetime.Connect(target));
    }

    private static void DifferentTargetsKeepIndependentOwnership()
    {
        var fixture = new Fixture();
        var oldTarget = new Target();
        var newTarget = new Target();
        fixture.Lifetime.Connect(oldTarget);
        fixture.Lifetime.Disconnect(oldTarget);
        fixture.Lifetime.Connect(newTarget);
        fixture.RunQueued();
        fixture.Lifetime.DrainDisconnected();
        Require(fixture.Closed.Count == 1 && ReferenceEquals(fixture.Closed[0], oldTarget) &&
                fixture.Lifetime.RetainedCount == 1,
            "Old target cleanup must preserve a distinct active target, even when Equals matches.");

        fixture.Lifetime.Disconnect(newTarget);
        fixture.Lifetime.DrainDisconnected();
        fixture.RunQueued();
        Require(fixture.Closed.Count == 2 && ReferenceEquals(fixture.Closed[1], newTarget) &&
                fixture.Lifetime.RetainedCount == 0,
            "Each target must retain its own cleanup identity.");
    }

    private static void FailedInitializationAndPermanentShutdown()
    {
        var fixture = new Fixture();
        var partiallyInitialized = new Target();
        Throws<ApplicationException>(() =>
        {
            fixture.Lifetime.Connect(partiallyInitialized);
            throw new ApplicationException("Simulated material initialization failure.");
        });
        fixture.Lifetime.DrainDisconnected();
        Require(fixture.CloseAttempts == 0 && fixture.Lifetime.RetainedCount == 1,
            "Material initialization failure must preserve the target while the framework connection remains active.");

        // Cleanup becomes eligible only when the framework actually disconnects.
        fixture.Lifetime.Disconnect(partiallyInitialized);
        var permanentClose = new Target();
        fixture.Lifetime.Connect(permanentClose);
        fixture.Lifetime.Disconnect(permanentClose);
        Require(fixture.CloseAttempts == 0 && fixture.Lifetime.RetainedCount == 2,
            "Failed initialization and permanent detach must retain resources until cleanup.");
        fixture.Lifetime.DrainDisconnected();
        Require(fixture.Closed.Count == 2 && fixture.Lifetime.RetainedCount == 0,
            "Permanent shutdown must synchronously finish all disconnected cleanup on the owner thread.");
        fixture.RunQueued();
        Require(fixture.CloseAttempts == 2, "Callbacks left after shutdown must not close again.");
    }

    private static void CleanupFailuresRemainRetryable()
    {
        var closeFailure = new Fixture { FailNextClose = true };
        var target = new Target();
        closeFailure.Lifetime.Connect(target);
        closeFailure.Lifetime.Disconnect(target);
        Throws<ApplicationException>(closeFailure.RunQueued);
        Require(closeFailure.CloseAttempts == 1 && closeFailure.Closed.Count == 0 &&
                closeFailure.Lifetime.RetainedCount == 1,
            "A failed close must propagate and retain the target for retry.");
        closeFailure.Lifetime.DrainDisconnected();
        Require(closeFailure.CloseAttempts == 2 && closeFailure.Closed.Count == 1 &&
                closeFailure.Lifetime.RetainedCount == 0,
            "An owner-thread drain must retry the failed close.");

        var queueFailure = new Fixture { AcceptQueue = false };
        var rejectedTarget = new Target();
        queueFailure.Lifetime.Connect(rejectedTarget);
        Throws<InvalidOperationException>(() => queueFailure.Lifetime.Disconnect(rejectedTarget));
        Require(queueFailure.Queue.Count == 0 && queueFailure.CloseAttempts == 0 &&
                queueFailure.Lifetime.RetainedCount == 1,
            "Queue rejection must be reported without abandoning the disconnected target.");
        queueFailure.Lifetime.DrainDisconnected();
        Require(queueFailure.Closed.Count == 1 && queueFailure.Lifetime.RetainedCount == 0,
            "Owner-thread shutdown must clean up even when the queue is unavailable.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    // Deliberately equal by value: ownership must use reference identity.
    private sealed class Target
    {
        public override bool Equals(object? other) => other is Target;
        public override int GetHashCode() => 0;
    }

    private sealed class Fixture
    {
        internal bool HasThreadAccess = true;
        internal bool AcceptQueue = true;
        internal bool FailNextClose;
        internal int CloseAttempts;
        internal Queue<Action> Queue { get; } = new();
        internal List<Target> Closed { get; } = new();
        internal BackdropTargetLifetime<Target> Lifetime { get; }

        internal Fixture()
        {
            Lifetime = new BackdropTargetLifetime<Target>(
                () => HasThreadAccess,
                callback =>
                {
                    if (!AcceptQueue) return false;
                    Queue.Enqueue(callback);
                    return true;
                },
                Close);
        }

        private void Close(Target target)
        {
            Require(HasThreadAccess, "Close must execute on the owner thread.");
            Require(Lifetime.RetainedCount > 0, "Ownership must remain held while close executes.");
            CloseAttempts++;
            if (FailNextClose)
            {
                FailNextClose = false;
                throw new ApplicationException("Simulated close failure.");
            }
            Require(!Closed.Any(previous => ReferenceEquals(previous, target)),
                "A successfully closed target must not be closed again.");
            Closed.Add(target);
        }

        internal void RunQueued()
        {
            while (Queue.TryDequeue(out var callback)) callback();
        }
    }
}
