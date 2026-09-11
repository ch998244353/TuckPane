using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using TuckPane.Core;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class MainWindow
{
    private AnimationTraceSession? _animationTrace;
    private int _animationTraceSequence;

    private AnimationTraceSession? BeginAnimationTrace(string direction)
    {
        if (!AppLogger.PerformanceTraceEnabled) return null;
        try
        {
            _animationTrace?.Finish("superseded");
            var settings = _host.State.GlobalSettings;
            object context = new
            {
                schema = 1,
                build = "animation-baseline-20260907.1",
                binary = typeof(MainWindow).Module.ModuleVersionId,
                version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                process = Environment.ProcessId,
                startedUtc = DateTimeOffset.UtcNow,
                organizer = OrganizerId,
                sequence = ++_animationTraceSequence,
                direction,
                mode = _definition.PlacementMode.ToString(),
                contentMode = _definition.ExpandedContentMode.ToString(),
                dockEdge = _definition.DockEdge.ToString(),
                contained = IsContained,
                profile = settings.PerformanceProfile.ToString(),
                customAnimations = UseCustomAnimations,
                systemAnimations = _uiSettings.AnimationsEnabled,
                advancedEffects = _uiSettings.AdvancedEffectsEnabled,
                theme = new { settings.ThemeColorArgb, settings.ThemeTransparency, settings.SolidThemeOpacity,
                    settings.ThemeBlurStrength, settings.SolidColorMode, settings.EdgeGlowEnabled },
                itemsAtStart = _items.Count,
                realizedAtStart = _realizedItemHosts.Count,
                dpi = NativeMethods.GetDpiForWindow(_hwnd),
                savedMonitor = _definition.Position?.MonitorDevice
            };
            return _animationTrace = new AnimationTraceSession(this, context);
        }
        catch (Exception ex)
        {
            // Opt-in diagnostics must never make an otherwise valid interaction fail.
            AppLogger.Error("无法开始收纳窗动画诊断。", ex);
            return null;
        }
    }

    private sealed class AnimationTraceSession(MainWindow owner, object context) : IDisposable
    {
        internal AnimationTraceMetrics Metrics { get; } = new(Stopwatch.GetTimestamp());
        internal string Outcome { get; set; } = "incomplete";
        private bool _finished;

        internal void Advance(AnimationTraceStage stage) => Metrics.Advance(stage, Stopwatch.GetTimestamp());

        internal void Finish(string outcome)
        {
            if (_finished) return;
            _finished = true;
            Metrics.Complete(Stopwatch.GetTimestamp());
            if (ReferenceEquals(owner._animationTrace, this)) owner._animationTrace = null;
            try
            {
                // Serialization/log I/O is outside the measured interaction. Each
                // sample is one summary; waiter callbacks only update counters.
                AppLogger.Performance("organizer-animation " + JsonSerializer.Serialize(new
                {
                    context,
                    outcome,
                    itemsAtEnd = owner._items.Count,
                    realizedAtEnd = owner._realizedItemHosts.Count,
                    expandedWidthDip = owner.ExpandedView.ActualWidth,
                    expandedHeightDip = owner.ExpandedView.ActualHeight,
                    totalMs = Metrics.TotalMilliseconds,
                    stagesMs = Enum.GetValues<AnimationTraceStage>().ToDictionary(stage => stage.ToString(), Metrics.Milliseconds),
                    work = Enum.GetValues<AnimationTraceWork>().ToDictionary(work => work.ToString(), Metrics.Count),
                    uiWaiterCallbacks = new
                    {
                        meaning = "XAML Rendering callbacks observed by existing waiters; NOT presented FPS",
                        count = Metrics.RenderingCallbacks,
                        intervalCount = Metrics.RenderingIntervals,
                        meanMs = Metrics.RenderingMeanMilliseconds,
                        maxMs = Metrics.RenderingMaxMilliseconds,
                        over33Ms = Metrics.RenderingIntervalsOver33Ms,
                        over50Ms = Metrics.RenderingIntervalsOver50Ms
                    },
                    waits = new
                    {
                        rendering = Metrics.RenderingWaits,
                        renderingTimeouts = Metrics.RenderingTimeouts,
                        rendered = Metrics.RenderedWaits,
                        renderedTimeouts = Metrics.RenderedTimeouts
                    }
                }));
            }
            catch (Exception ex) { AppLogger.Error("无法写入收纳窗动画诊断。", ex); }
        }

        public void Dispose() => Finish(Outcome);
    }
}
