using System.Text.Json;
using TuckPane.Services;

internal static class StabilityWindowsEventChecks
{
    internal static void Run()
    {
        const string timestamp = "2026-09-11T01:02:03.0000000Z";
        string Event(int id, string provider, string fields) => $"""
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><Provider Name="{provider}"/><EventID>{id}</EventID><TimeCreated SystemTime="{timestamp}"/></System>
              <EventData>{fields}<Data Name="PrivatePayload">SECRET-BODY account@example.com</Data></EventData>
            </Event>
            """;
        string valid = Event(1000, "Application Error", """
            <Data Name="AppPath">C:\Users\PRIVATE-USER\secret-project\TuckPane.exe</Data>
            <Data Name="ExceptionCode">0xc0000005</Data>
            """);
        string launcher = Event(1001, "Windows Error Reporting", """
            <Data Name="P1">00-启动 TuckPane.exe</Data><Data Name="P7">0x80004005</Data>
            """);
        string other = Event(1000, "Application Error", """
            <Data Name="AppName">Other.exe</Data><Data Name="PrivatePayload">TuckPane.exe</Data>
            """);
        string lookalike = Event(1000, "Application Error", "<Data Name=\"AppName\">OtherTuckPane.exe</Data>");
        string unrelatedProvider = Event(1000, "Private Provider", "<Data Name=\"AppName\">TuckPane.exe</Data>");
        string unexpectedEvent = Event(42, "Application Error", "<Data Name=\"AppName\">TuckPane.exe</Data>");
        var events = WindowsCrashEvents.Parse($"<Events>{valid}{launcher}{other}{lookalike}{unrelatedProvider}{unexpectedEvent}</Events>");
        Require(events.Count == 2 && events[0].EventId == 1000 && events[1].EventId == 1001,
            "Only exact application filenames, allowed providers and allowed event IDs may be retained.");
        Require(events[0].ExceptionCode == "0xc0000005" && events[1].ExceptionCode == "0x80004005" &&
                events[0].Timestamp == DateTimeOffset.Parse(timestamp), "Allowed metadata must survive parsing.");
        string exported = JsonSerializer.Serialize(events);
        Require(!exported.Contains("PRIVATE-USER") && !exported.Contains("secret-project") &&
                !exported.Contains("SECRET-BODY") && !exported.Contains("account@example.com") && !exported.Contains("AppPath"),
            "The returned records must not contain raw event fields, paths or body text.");

        var invalidCode = WindowsCrashEvents.Parse("<Events>" + Event(1000, "Application Error",
            "<Data Name=\"AppName\">TuckPane.exe</Data><Data Name=\"ExceptionCode\">0x123 SECRET-BODY</Data>") + "</Events>");
        Require(invalidCode.Count == 1 && invalidCode[0].ExceptionCode is null, "Exception codes must contain only hexadecimal data.");
        Require(WindowsCrashEvents.Parse("<!DOCTYPE Events [<!ENTITY private 'SECRET-BODY'>]><Events>&private;</Events>").Count == 0,
            "DTDs and entities must be rejected.");
        Require(WindowsCrashEvents.Parse("<Events>").Count == 0 && WindowsCrashEvents.Parse(new string('x', 256 * 1024 + 1)).Count == 0,
            "Malformed and excessive input must return no evidence.");
        Require(WindowsCrashEvents.Parse("<Events>" + string.Concat(Enumerable.Repeat(valid, 33)) + "</Events>").Count == 32,
            "Event processing must stay bounded.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
