namespace TuckPane.Core;

internal readonly record struct SmoothScrollState(
    double CurrentOffset,
    double TargetOffset,
    double Velocity,
    int Remainder)
{
    internal static SmoothScrollState Empty => new(0, 0, 0, 0);
}

internal static class OrganizerScrollMath
{
    internal const double ResponseSeconds = .16;
    internal const int WheelDeltaPerLine = 120;

    internal static SmoothScrollState ConsumeWheelDelta(
        SmoothScrollState state,
        int wheelDelta,
        double rowExtent,
        double scrollableHeight)
    {
        state = ClampState(state, scrollableHeight);
        if (!double.IsFinite(rowExtent) || rowExtent <= 0)
            return state with { Remainder = 0 };

        long total = (long)state.Remainder + wheelDelta;
        int steps = (int)(total / WheelDeltaPerLine);
        int remainder = (int)(total % WheelDeltaPerLine);
        double target = state.TargetOffset - steps * rowExtent;
        return ClampState(state with { TargetOffset = target, Remainder = remainder }, scrollableHeight);
    }

    internal static SmoothScrollState QueueTarget(
        SmoothScrollState state,
        double delta,
        double scrollableHeight) =>
        ClampState(state with { TargetOffset = state.TargetOffset + (double.IsFinite(delta) ? delta : 0) }, scrollableHeight);

    internal static SmoothScrollState ClampState(SmoothScrollState state, double scrollableHeight)
    {
        double max = double.IsFinite(scrollableHeight) ? Math.Max(0, scrollableHeight) : 0;
        double current = double.IsFinite(state.CurrentOffset) ? Math.Clamp(state.CurrentOffset, 0, max) : 0;
        double target = double.IsFinite(state.TargetOffset) ? Math.Clamp(state.TargetOffset, 0, max) : current;
        double velocity = double.IsFinite(state.Velocity) ? state.Velocity : 0;
        if (max <= 0) return new(0, 0, 0, state.Remainder);
        return new(current, target, velocity, Math.Clamp(state.Remainder, -119, 119));
    }

    internal static SmoothScrollState Step(
        SmoothScrollState state,
        double deltaSeconds,
        double scrollableHeight,
        bool reduceMotion)
    {
        state = ClampState(state, scrollableHeight);
        if (reduceMotion || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0)
            return state with { CurrentOffset = state.TargetOffset, Velocity = 0 };

        double dt = Math.Clamp(deltaSeconds, 0, 0.1);
        double omega = 2 / ResponseSeconds;
        double displacement = state.CurrentOffset - state.TargetOffset;
        double helper = state.Velocity + omega * displacement;
        double decay = Math.Exp(-omega * dt);
        double current = state.TargetOffset + (displacement + helper * dt) * decay;
        double velocity = (state.Velocity - omega * helper * dt) * decay;
        if (Math.Abs(current - state.TargetOffset) < .01 && Math.Abs(velocity) < .01)
            return state with { CurrentOffset = state.TargetOffset, Velocity = 0 };
        return ClampState(state with { CurrentOffset = current, Velocity = velocity }, scrollableHeight);
    }

    internal static double ComputeSmoothScrollOffset(
        double currentOffset,
        double targetOffset,
        double velocity,
        double deltaSeconds,
        double scrollableHeight,
        bool reduceMotion) =>
        Step(new(currentOffset, targetOffset, velocity, 0), deltaSeconds, scrollableHeight, reduceMotion).CurrentOffset;
}
