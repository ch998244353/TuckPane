using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TuckPane.Services;

internal enum DiagnosticArea { Runtime, Lifecycle, Drop, Transfer, Shell, Watcher, Export }
internal enum DiagnosticStage { Notice, Started, DataRead, Completed, Failed, Cancelled, Interrupted, Released, Slow, Recovered, Exit }
internal enum DiagnosticFault { None, Cancelled, Timeout, IO, AccessDenied, Com, InvalidOperation, Other }

// Deliberately no message, path, filename, command line, exception text or arbitrary properties.
internal sealed record DiagnosticRecord(int Format, DateTimeOffset Timestamp, Guid Session, Guid Build,
    DiagnosticArea Area, DiagnosticStage Stage, Guid? Operation, string Origin,
    double? ElapsedMs, int? Count, DiagnosticFault Fault, int? HResult, long Dropped = 0, long WriteFailures = 0,
    string? CodeLocation = null, string? ExceptionType = null)
{
    // Membership, not a character regex: arbitrary user text that looks like an identifier is still excluded.
    private static readonly Lazy<HashSet<string>> CodeMembers = new(() =>
    {
        try
        {
            return typeof(AppLogger).Assembly.DefinedTypes
                .Where(type => type.Namespace is "TuckPane" or "TuckPane.Core" or "TuckPane.Services")
                .SelectMany(type => type.DeclaredMethods).Select(method => method.Name)
                .Where(name => name.Length <= 128 && !name.Contains('<'))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch { return new(StringComparer.Ordinal); }
    });
    private static readonly HashSet<string> ExceptionTypes = new(StringComparer.Ordinal)
    {
        nameof(Exception), nameof(IOException), nameof(UnauthorizedAccessException), nameof(TimeoutException),
        nameof(InvalidOperationException), nameof(ArgumentException), nameof(ArgumentNullException),
        nameof(ArgumentOutOfRangeException), nameof(ObjectDisposedException), nameof(FileNotFoundException),
        nameof(DirectoryNotFoundException), nameof(NotSupportedException), nameof(NullReferenceException),
        nameof(OutOfMemoryException), nameof(System.Runtime.InteropServices.COMException),
        nameof(OperationCanceledException), nameof(TaskCanceledException), nameof(AggregateException)
    };
    internal bool IsAbnormal => ShouldRecord(Stage, Fault);
    internal static bool ShouldRecord(DiagnosticStage stage, DiagnosticFault fault) => fault != DiagnosticFault.Cancelled &&
        (stage is DiagnosticStage.Failed or DiagnosticStage.Interrupted || fault == DiagnosticFault.Timeout);
    internal static readonly Guid SessionId = Guid.NewGuid();
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static DiagnosticRecord Create(DiagnosticArea area, DiagnosticStage stage, Guid? operation = null,
        double? elapsedMs = null, int? count = null, Exception? exception = null, string origin = "") => new(
            2, DateTimeOffset.UtcNow, SessionId, typeof(AppLogger).Module.ModuleVersionId, area, stage, operation,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin)))[..16],
            elapsedMs is double time && double.IsFinite(time) ? Math.Max(0, time) : null,
            count is int total ? Math.Max(0, total) : null,
            ClassifyFault(exception), exception?.HResult, CodeLocation: SafeCodeLocation(origin), ExceptionType: SafeExceptionType(exception));

    internal static DiagnosticFault ClassifyFault(Exception? exception) => exception switch
    {
        null => DiagnosticFault.None,
        OperationCanceledException => DiagnosticFault.Cancelled,
        TimeoutException => DiagnosticFault.Timeout,
        UnauthorizedAccessException => DiagnosticFault.AccessDenied,
        IOException => DiagnosticFault.IO,
        System.Runtime.InteropServices.COMException => DiagnosticFault.Com,
        InvalidOperationException => DiagnosticFault.InvalidOperation,
        _ => DiagnosticFault.Other
    };

    private static string? SafeCodeLocation(string? value) =>
        value is { Length: <= 128 } && CodeMembers.Value.Contains(value) ? value : null;

    private static string? SafeExceptionType(Exception? exception)
    {
        Type? type = exception?.GetType();
        // Only framework types, never a custom type whose name could contain user data.
        return type is not null && type.Assembly == typeof(Exception).Assembly && ExceptionTypes.Contains(type.Name)
            ? type.Name : null;
    }

    internal DiagnosticRecord? Sanitize()
    {
        if (Format is not (1 or 2) || !Enum.IsDefined(Area) || !Enum.IsDefined(Stage) ||
            !Enum.IsDefined(Fault) || Session == Guid.Empty || Build == Guid.Empty || Timestamp == default ||
            Origin is not { Length: 16 } || Origin.AsSpan().IndexOfAnyExcept("0123456789ABCDEF") >= 0 ||
            ElapsedMs is double elapsed && (!double.IsFinite(elapsed) || elapsed < 0) || Count < 0 ||
            Dropped < 0 || WriteFailures < 0) return null;
        return this with
        {
            Timestamp = Timestamp.ToUniversalTime(),
            CodeLocation = Format == 2 ? SafeCodeLocation(CodeLocation) : null,
            ExceptionType = Format == 2 && ExceptionType is not null && ExceptionTypes.Contains(ExceptionType) ? ExceptionType : null
        };
    }

    internal string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    internal static DiagnosticRecord? ReadSafe(string json)
    {
        if (json.Length > 8192) return null;
        try
        {
            var value = JsonSerializer.Deserialize<DiagnosticRecord>(json, JsonOptions);
            return value?.Sanitize();
        }
        catch (JsonException) { return null; }
    }
}
