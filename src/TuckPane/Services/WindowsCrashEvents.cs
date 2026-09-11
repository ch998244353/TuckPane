using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TuckPane.Services;

internal sealed record WindowsCrashEvent(int EventId, DateTimeOffset Timestamp, string Provider, string? ExceptionCode);
internal enum CrashCollectionStatus { Available, Unavailable, TimedOut }
internal sealed record WindowsCrashCollection(IReadOnlyList<WindowsCrashEvent> Events, CrashCollectionStatus Status);

internal static class WindowsCrashEvents
{
    private const int MaximumBytes = 256 * 1024;
    private static readonly XNamespace EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    internal static async Task<IReadOnlyList<WindowsCrashEvent>> ReadAsync(CancellationToken cancellationToken) =>
        (await CollectAsync(cancellationToken)).Events;

    internal static async Task<WindowsCrashCollection> CollectAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return new([], CrashCollectionStatus.Unavailable);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var process = new Process();
        try
        {
            string executable = Path.Combine(Environment.SystemDirectory, "wevtutil.exe");
            if (!File.Exists(executable)) return new([], CrashCollectionStatus.Unavailable);
            process.StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[]
            {
                "qe", "Application", "/f:xml", "/e:Events", "/uni:true", "/rd:true", "/c:32",
                "/q:*[System[(EventID=1000 or EventID=1001) and TimeCreated[timediff(@SystemTime) <= 604800000]]]"
            }) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return new([], CrashCollectionStatus.Unavailable);

            Task<byte[]> output = ReadBoundedAsync(process.StandardOutput.BaseStream, MaximumBytes, deadline.Token);
            Task<byte[]> errors = ReadBoundedAsync(process.StandardError.BaseStream, 16 * 1024, deadline.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(deadline.Token)).WaitAsync(deadline.Token);
            if (process.ExitCode != 0) return new([], CrashCollectionStatus.Unavailable);
            using var bytes = new MemoryStream(await output, writable: false);
            using var reader = new StreamReader(bytes, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
            return ParseCollection(await reader.ReadToEndAsync(deadline.Token));
        }
        catch (OperationCanceledException) { return new([], CrashCollectionStatus.TimedOut); }
        catch (Exception)
        {
            // Optional evidence only. Never log event payloads, paths or process error text.
            return new([], CrashCollectionStatus.Unavailable);
        }
        finally
        {
            deadline.Cancel();
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception) { }
        }
    }

    internal static IReadOnlyList<WindowsCrashEvent> Parse(string xml) => ParseCollection(xml).Events;

    private static WindowsCrashCollection ParseCollection(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaximumBytes) return new([], CrashCollectionStatus.Unavailable);
        try
        {
            using var input = new StringReader(xml);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumBytes
            });
            XDocument document = XDocument.Load(reader);
            var result = new List<WindowsCrashEvent>();
            foreach (XElement entry in document.Descendants(EventNamespace + "Event").Take(32))
            {
                XElement? system = entry.Element(EventNamespace + "System");
                if (!int.TryParse(system?.Element(EventNamespace + "EventID")?.Value, out int eventId) ||
                    eventId is not (1000 or 1001)) continue;
                string? provider = CanonicalProvider(system?.Element(EventNamespace + "Provider")?.Attribute("Name")?.Value);
                if (provider is null || !DateTimeOffset.TryParse(
                        system?.Element(EventNamespace + "TimeCreated")?.Attribute("SystemTime")?.Value,
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset timestamp)) continue;

                XElement[] fields = entry.Element(EventNamespace + "EventData")?.Elements(EventNamespace + "Data").ToArray() ?? [];
                if (!fields.Any(field => IsApplicationField(field.Attribute("Name")?.Value) && IsApplication(field.Value))) continue;
                string? exceptionCode = fields.Where(field =>
                        string.Equals(field.Attribute("Name")?.Value, "ExceptionCode", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(field.Attribute("Name")?.Value, "P7", StringComparison.OrdinalIgnoreCase))
                    .Select(field => field.Value.Trim()).FirstOrDefault(IsExceptionCode);
                result.Add(new(eventId, timestamp, provider, exceptionCode));
            }
            return new(result, CrashCollectionStatus.Available);
        }
        catch (Exception)
        {
            return new([], CrashCollectionStatus.Unavailable);
        }
    }

    internal static WindowsCrashEvent? Sanitize(WindowsCrashEvent value, DateTimeOffset now)
    {
        string? provider = CanonicalProvider(value.Provider);
        if (value.EventId is not (1000 or 1001) || provider is null ||
            value.Timestamp < now.AddDays(-7) || value.Timestamp > now) return null;
        return value with
        {
            Timestamp = value.Timestamp.ToUniversalTime(), Provider = provider,
            ExceptionCode = value.ExceptionCode is { } code && IsExceptionCode(code) ? code : null
        };
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken cancellationToken)
    {
        using var result = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (result.Length + read > maximum) throw new IOException("Event output limit exceeded.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }

    private static bool IsApplicationField(string? name) =>
        string.Equals(name, "AppName", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "AppPath", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "P1", StringComparison.OrdinalIgnoreCase);

    private static bool IsApplication(string value)
    {
        string path = value.Trim();
        int separator = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        string name = path[(separator + 1)..];
        return name.Equals("TuckPane.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("00-启动 TuckPane.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string? CanonicalProvider(string? value) =>
        string.Equals(value, "Application Error", StringComparison.OrdinalIgnoreCase) ? "Application Error" :
        string.Equals(value, "Windows Error Reporting", StringComparison.OrdinalIgnoreCase) ? "Windows Error Reporting" : null;

    private static bool IsExceptionCode(string value) =>
        value.Length is >= 3 and <= 18 && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        value.AsSpan(2).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
}
