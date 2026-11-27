namespace TuckPane.Core;

internal static class FileMenuCommands
{
    internal static IReadOnlyList<string> Verbs { get; } =
        Array.AsReadOnly(new[] { "open", "cut", "copy", "rename", "delete", "properties" });

    internal static string? Verb(uint commandId) =>
        commandId is >= 1 and <= 6 ? Verbs[(int)commandId - 1] : null;

    internal static bool IsDark(byte red, byte green, byte blue) =>
        5 * green + 2 * red + blue < 8 * 128;

    internal static string ErrorDetails(Exception exception)
    {
        Exception cause = exception.GetBaseException();
        string message = !string.IsNullOrWhiteSpace(exception.Message) ? exception.Message.Trim()
            : !string.IsNullOrWhiteSpace(cause.Message) ? cause.Message.Trim() : cause.GetType().Name;
        return $"{message} (0x{cause.HResult:X8})";
    }
}
