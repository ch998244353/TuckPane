using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TuckPane.Services;

public static class AppLogger
{
    internal static DiagnosticLogWriter Writer { get; } = new(Path.Combine(AppPaths.LocalRoot, "diagnostics"));

    // Compatibility callers may format user content. It is intentionally never persisted.
    public static void Info(string message, [CallerMemberName] string caller = "") { }
    public static void Error(string message, Exception? exception = null, [CallerMemberName] string caller = "") =>
        Record(DiagnosticArea.Runtime, DiagnosticStage.Failed, exception: exception, origin: caller);

    public static void Lifecycle(string name, string details = "") => Record(DiagnosticArea.Lifecycle, name switch
    {
        "startup" => DiagnosticStage.Started,
        "exit-application" or "message-loop-ended" => DiagnosticStage.Exit,
        "exit-cancelled" => DiagnosticStage.Cancelled,
        "ui-exception" => DiagnosticStage.Failed,
        "exit-failed" => DiagnosticStage.Failed,
        _ => DiagnosticStage.Notice
    }, origin: name);

    public static bool PerformanceTraceEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("TUCKPANE_PERF_TRACE"), "1", StringComparison.Ordinal);
    public static void Performance(string message, [CallerMemberName] string caller = "")
    {
        // Normal performance samples are intentionally not persisted, including opt-in legacy tracing.
    }

    public static async Task FlushAsync() => await Writer.FlushAsync(TimeSpan.FromSeconds(2));

    internal static void Record(DiagnosticArea area, DiagnosticStage stage, Guid? operation = null,
        double? elapsedMs = null, int? count = null, Exception? exception = null, [CallerMemberName] string origin = "")
    {
        if (!DiagnosticRecord.ShouldRecord(stage, DiagnosticRecord.ClassifyFault(exception))) return;
        try { Writer.Write(DiagnosticRecord.Create(area, stage, operation, elapsedMs, count, exception, origin)); }
        catch { /* Diagnostic failures must not change application behavior. */ }
    }

    internal static Operation Begin(DiagnosticArea area, [CallerMemberName] string origin = "") => new(area, origin);
    internal sealed class Operation : IDisposable
    {
        internal Guid Id { get; } = Guid.NewGuid();
        private readonly DiagnosticArea _area;
        private readonly string _origin;
        private readonly long _started = Stopwatch.GetTimestamp();
        private DiagnosticStage _result = DiagnosticStage.Interrupted;
        private Exception? _exception;
        private int? _count;
        private bool _disposed;
        internal Operation(DiagnosticArea area, string origin) { _area = area; _origin = origin; }
        internal void Complete(int? count = null) { _result = DiagnosticStage.Completed; _count = count; }
        internal void Fail(Exception exception) { _result = exception is OperationCanceledException ? DiagnosticStage.Cancelled : DiagnosticStage.Failed; _exception = exception; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Record(_area, _result, Id, Stopwatch.GetElapsedTime(_started).TotalMilliseconds, _count, _exception, _origin);
        }
    }
}
