using System.Diagnostics;
using TuckPane.Core;

internal static class AnimationDiagnosticChecks
{
    internal static void Run()
    {
        StageTotals();
        RenderingAndWaits();
        CompletionFreezesMetrics();
        Console.WriteLine("PASS organizer-animation: 3 diagnostic checks (no UI or presented-FPS measurement).");
    }

    private static long At(int milliseconds) =>
        Stopwatch.Frequency * 10L + (long)Math.Round(milliseconds * (double)Stopwatch.Frequency / 1000);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, string message) =>
        Require(Math.Abs(actual - expected) <= 0.001, $"{message}: expected {expected}, got {actual}.");

    private static void StageTotals()
    {
        var metrics = new AnimationTraceMetrics(At(0));
        metrics.Advance(AnimationTraceStage.Layout, At(10));
        metrics.Advance(AnimationTraceStage.Animation, At(30));
        metrics.Advance(AnimationTraceStage.Layout, At(90));
        metrics.Advance(AnimationTraceStage.Handoff, At(100));
        metrics.Complete(At(105));

        Near(metrics.Milliseconds(AnimationTraceStage.Prepare), 10, "Prepare duration");
        Near(metrics.Milliseconds(AnimationTraceStage.Layout), 30, "Repeated layout durations accumulate");
        Near(metrics.Milliseconds(AnimationTraceStage.Animation), 60, "Animation duration");
        Near(metrics.Milliseconds(AnimationTraceStage.Handoff), 5, "Complete closes the final stage");
        Near(metrics.TotalMilliseconds, 105, "Total duration");
        Near(Enum.GetValues<AnimationTraceStage>().Sum(metrics.Milliseconds),
            metrics.TotalMilliseconds, "Stage durations sum to total");
    }

    private static void RenderingAndWaits()
    {
        var metrics = new AnimationTraceMetrics(At(0));
        metrics.RecordRendering(At(0));
        Require(metrics.RenderingCallbacks == 1 && metrics.RenderingIntervals == 0,
            "First Rendering callback is not an interval.");
        Near(metrics.RenderingMeanMilliseconds, 0, "Mean without intervals");
        metrics.RecordRendering(At(20));
        metrics.RecordRendering(At(60));
        metrics.RecordRendering(At(120));
        Require(metrics.RenderingCallbacks == 4 && metrics.RenderingIntervals == 3,
            "Rendering callback and interval counts are distinct.");
        Near(metrics.RenderingMeanMilliseconds, 40, "Rendering interval mean");
        Near(metrics.RenderingMaxMilliseconds, 60, "Rendering interval maximum");
        Require(metrics.RenderingIntervalsOver33Ms == 2 && metrics.RenderingIntervalsOver50Ms == 1,
            "Rendering interval thresholds must count 40/60 ms and 60 ms respectively.");

        metrics.RecordWait(rendered: false, completed: true);
        metrics.RecordWait(rendered: false, completed: false);
        metrics.RecordWait(rendered: true, completed: false);
        metrics.RecordWait(rendered: true, completed: false);
        metrics.RecordWait(rendered: true, completed: true);
        Require(metrics.RenderingWaits == 2 && metrics.RenderingTimeouts == 1 &&
                metrics.RenderedWaits == 3 && metrics.RenderedTimeouts == 2,
            "Rendering and Rendered waits/timeouts must remain separate.");
    }

    private static void CompletionFreezesMetrics()
    {
        var metrics = new AnimationTraceMetrics(At(0));
        metrics.RecordRendering(At(0));
        metrics.RecordRendering(At(60));
        metrics.RecordWait(rendered: false, completed: false);
        metrics.RecordWait(rendered: true, completed: true);
        foreach (var work in Enum.GetValues<AnimationTraceWork>()) metrics.RecordWork(work);
        metrics.Complete(At(70));

        metrics.Complete(At(1000));
        metrics.Advance(AnimationTraceStage.Handoff, At(1000));
        metrics.RecordRendering(At(1000));
        metrics.RecordWait(rendered: false, completed: true);
        metrics.RecordWait(rendered: true, completed: false);
        foreach (var work in Enum.GetValues<AnimationTraceWork>()) metrics.RecordWork(work);

        Near(metrics.TotalMilliseconds, 70, "Complete is idempotent");
        foreach (var stage in Enum.GetValues<AnimationTraceStage>())
            Near(metrics.Milliseconds(stage), stage == AnimationTraceStage.Prepare ? 70 : 0,
                $"Completed stage {stage} is frozen");
        foreach (var work in Enum.GetValues<AnimationTraceWork>())
            Require(metrics.Count(work) == 1, $"Completed work counter {work} is frozen.");
        Require(metrics.RenderingCallbacks == 2 && metrics.RenderingIntervals == 1 &&
                metrics.RenderingIntervalsOver33Ms == 1 && metrics.RenderingIntervalsOver50Ms == 1,
            "Completed Rendering counters are frozen.");
        Near(metrics.RenderingMeanMilliseconds, 60, "Completed interval mean is frozen");
        Near(metrics.RenderingMaxMilliseconds, 60, "Completed interval maximum is frozen");
        Require(metrics.RenderingWaits == 1 && metrics.RenderingTimeouts == 1 &&
                metrics.RenderedWaits == 1 && metrics.RenderedTimeouts == 0,
            "Completed wait and timeout counters are frozen.");
    }
}
