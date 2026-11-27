namespace TuckPane.Core;

// Filename-only rules, independent of WinUI and the shell operation.
internal static class FileRenameRules
{
    internal static string InitialName(string path, bool fixedExtension) => fixedExtension
        ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);

    internal static int SelectionLength(string name, bool directory, bool fixedExtension)
    {
        int extensionLength = Path.GetExtension(name).Length;
        return directory || fixedExtension || extensionLength == name.Length
            ? name.Length : name.Length - extensionLength;
    }

    internal static string? Validate(string name, string extension, bool portableDocument)
    {
        if (string.IsNullOrWhiteSpace(name)) return "ItemNameRequired";
        if (name.Length + extension.Length > 255 || (portableDocument && name.Length > 120) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
            return "FolderNameInvalidError";
        // Windows treats the part before the first period as the device name.
        string stem = name.Split('.', 2)[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                "123456789¹²³".Contains(stem[3]))) return "FolderNameReservedError";
        return null;
    }
}
