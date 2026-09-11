using System.Security.Cryptography;
using System.Text.Json;

namespace TuckPane.Updates;

public static class UpdateFiles
{
    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Hash(string path)
    {
        using Stream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    public static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void NoLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update paths cannot contain filesystem links.");
            current = Path.GetDirectoryName(current);
        }
    }

    public static string BackupConfiguration(string statePath, string backupRoot, string version)
    {
        string destination = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + version + "-" + Guid.NewGuid().ToString("N")[..8]);
        NoLinks(destination);
        Directory.CreateDirectory(destination);
        // The saved state contains window positions and all storage associations.
        // Document bodies and actual organizer files remain in their original directories.
        File.Copy(statePath, Path.Combine(destination, "state.json"));
        if (File.Exists(statePath + ".bak")) File.Copy(statePath + ".bak", Path.Combine(destination, "state.json.bak"));
        WriteJson(Path.Combine(destination, "backup.json"), new { version, source = statePath, createdUtc = DateTimeOffset.UtcNow });
        if (Hash(statePath) != Hash(Path.Combine(destination, "state.json"))) throw new IOException("Configuration backup verification failed.");
        return destination;
    }
}

public sealed record UpdateCheckCache(DateTimeOffset LastAttemptUtc, string? ReleaseJson);
public sealed record UpdateRequest(int ParentId, long ParentStartTicks, string TargetDirectory, string PackagePath,
    string PackageHash, string Version, bool Installed, string ResultPath, string[] ProtectedPaths, string? TestRoot);
public sealed record UpdateResult(string Status, string Version, string Message, string BackupDirectory);


