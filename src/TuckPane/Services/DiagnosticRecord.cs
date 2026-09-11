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
    double? ElapsedMs, int? Count, DiagnosticFault Fault, int? HResult, long Dropped = 0, long WriteFailures = 0)
{
    internal static readonly Guid SessionId = Guid.NewGuid();
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static DiagnosticRecord Create(DiagnosticArea area, DiagnosticStage stage, Guid? operation = null,
        double? elapsedMs = null, int? count = null, Exception? exception = null, string origin = "") => new(
            1, DateTimeOffset.UtcNow, SessionId, typeof(AppLogger).Module.ModuleVersionId, area, stage, operation,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin)))[..16],
            elapsedMs is double time && double.IsFinite(time) ? Math.Max(0, time) : null,
            count is int total ? Math.Max(0, total) : null,
            exception switch
            {
                null => DiagnosticFault.None,
                OperationCanceledException => DiagnosticFault.Cancelled,
                TimeoutException => DiagnosticFault.Timeout,
                UnauthorizedAccessException => DiagnosticFault.AccessDenied,
                IOException => DiagnosticFault.IO,
                System.Runtime.InteropServices.COMException => DiagnosticFault.Com,
                InvalidOperationException => DiagnosticFault.InvalidOperation,
                _ => DiagnosticFault.Other
            }, exception?.HResult);

    internal string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    internal static DiagnosticRecord? ReadSafe(string json)
    {
        if (json.Length > 8192) return null;
        try
        {
            var value = JsonSerializer.Deserialize<DiagnosticRecord>(json, JsonOptions);
            if (value is null || value.Format != 1 || !Enum.IsDefined(value.Area) || !Enum.IsDefined(value.Stage) ||
                !Enum.IsDefined(value.Fault) || value.Session == Guid.Empty || value.Build == Guid.Empty ||
                value.Origin is not { Length: 16 } || value.Origin.AsSpan().IndexOfAnyExcept("0123456789ABCDEF") >= 0 ||
                value.ElapsedMs is double elapsed && (!double.IsFinite(elapsed) || elapsed < 0) || value.Count < 0 ||
                value.Dropped < 0 || value.WriteFailures < 0) return null;
            return value;
        }
        catch (JsonException) { return null; }
    }
}
