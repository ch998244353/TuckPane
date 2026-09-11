using System.Diagnostics;

namespace TuckPane.Core;

internal enum AnimationTraceStage { Prepare, Layout, Animation, Handoff }
internal enum AnimationTraceWork { ApplyBounds, CollapseMove, ConfigureLayout, UpdateLayout, SurfaceGeometry, AnimatedCorner, VisualFrame, ThemeApplication }

// Counters only: no UI, allocation or logging on the per-frame path. Timestamps
// are explicit so the diagnostic itself can be checked without running a window.
internal sealed class AnimationTraceMetrics(long startedAt)
{
    private readonly double[] _stageMilliseconds = new double[4];
    private readonly int[] _work = new int[8];
    private AnimationTraceStage _stage;
    private readonly long _startedAt = startedAt;
    private long _stageStartedAt = startedAt;
    private long? _lastRenderingAt;
    private double _renderIntervalTotal;
    private bool _completed;

    internal double TotalMilliseconds { get; private set; }
    internal int RenderingCallbacks { get; private set; }
    internal int RenderingIntervals { get; private set; }
    internal double RenderingMeanMilliseconds => RenderingIntervals == 0 ? 0 : _renderIntervalTotal / RenderingIntervals;
    internal double RenderingMaxMilliseconds { get; private set; }
    internal int RenderingIntervalsOver33Ms { get; private set; }
    internal int RenderingIntervalsOver50Ms { get; private set; }
    internal int RenderingWaits { get; private set; }
    internal int RenderingTimeouts { get; private set; }
    internal int RenderedWaits { get; private set; }
    internal int RenderedTimeouts { get; private set; }

    internal double Milliseconds(AnimationTraceStage stage) => _stageMilliseconds[(int)stage];
    internal int Count(AnimationTraceWork work) => _work[(int)work];

    internal void Advance(AnimationTraceStage stage, long now)
    {
        if (_completed) return;
        _stageMilliseconds[(int)_stage] += Math.Max(0, Stopwatch.GetElapsedTime(_stageStartedAt, now).TotalMilliseconds);
        _stageStartedAt = now;
        _stage = stage;
    }

    internal void RecordWork(AnimationTraceWork work)
    {
        if (!_completed) _work[(int)work]++;
    }

    internal void RecordRendering(long now)
    {
        if (_completed) return;
        RenderingCallbacks++;
        if (_lastRenderingAt is long previous)
        {
            double milliseconds = Math.Max(0, Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
            RenderingIntervals++;
            _renderIntervalTotal += milliseconds;
            RenderingMaxMilliseconds = Math.Max(RenderingMaxMilliseconds, milliseconds);
            if (milliseconds > 33) RenderingIntervalsOver33Ms++;
            if (milliseconds > 50) RenderingIntervalsOver50Ms++;
        }
        _lastRenderingAt = now;
    }

    internal void RecordWait(bool rendered, bool completed)
    {
        if (_completed) return;
        if (rendered)
        {
            RenderedWaits++;
            if (!completed) RenderedTimeouts++;
        }
        else
        {
            RenderingWaits++;
            if (!completed) RenderingTimeouts++;
        }
    }

    internal void Complete(long now)
    {
        if (_completed) return;
        Advance(_stage, now);
        TotalMilliseconds = Math.Max(0, Stopwatch.GetElapsedTime(_startedAt, now).TotalMilliseconds);
        _completed = true;
    }
}
