namespace TuckPane.Core;

internal static class LifecycleDiagnostics
{
    // Interpret only lifecycle broadcasts; the caller must still forward them to Windows.
    internal static string? DescribeWindowMessage(uint message, ulong wParam, long lParam) => message switch
    {
        0x0218 => wParam switch // WM_POWERBROADCAST
        {
            4 => "power-suspend",
            6 => "power-resume-critical",
            7 => "power-resume-suspend",
            18 => "power-resume-automatic",
            _ => null
        },
        0x0011 => $"session-end-query flags=0x{unchecked((uint)lParam):X8}",
        0x0016 => $"session-end confirmed={wParam != 0} flags=0x{unchecked((uint)lParam):X8}",
        0x0010 => "host-close-request",
        0x0002 => "host-destroy",
        _ => null
    };
}
