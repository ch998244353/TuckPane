using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TuckPane.Updates;

public sealed record PackageManifest(string Version, Dictionary<string, string> Files);
public sealed record ReplacementFile(string Name, bool Existed);

public static class PortablePackage
{
    public const string ManifestName = "update-files.json";

    public static string Resolve(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains(':') || name.StartsWith('/') ||
            name.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM\d|LPT\d)(\.|$)", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Unsafe package path: " + name);
        string result = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!UpdateFiles.Within(result, root)) throw new InvalidDataException("Package path escapes target.");
        UpdateFiles.NoLinks(result);
        return result;
    }

    private static void ValidateManifest(PackageManifest manifest, string root)
    {
        ReleaseRules.StableVersion(manifest.Version);
        if (manifest.Files.Count is < 3 or > 6000 || !manifest.Files.ContainsKey("TuckPane.exe") || !manifest.Files.ContainsKey("TuckPane.dll"))
            throw new InvalidDataException("Incomplete program manifest.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            Resolve(root, entry.Key);
            string leaf = Path.GetFileName(entry.Key);
            if (!names.Add(entry.Key) || entry.Key.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ||
                leaf.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("state.json", StringComparison.OrdinalIgnoreCase) ||
                leaf.EndsWith(".tucknote", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".tucktodo", StringComparison.OrdinalIgnoreCase) ||
                entry.Key.Contains(".WebView2/", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(entry.Value, "^[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("Manifest includes duplicate or non-program files.");
        }
    }

    public static PackageManifest Stage(string zipPath, string staging, string expectedVersion)
    {
        UpdateFiles.NoLinks(staging);
        if (Directory.Exists(staging)) throw new IOException("Staging directory already exists.");
        Directory.CreateDirectory(staging);
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > 6500) throw new InvalidDataException("Too many package entries.");
        var manifestEntries = archive.Entries.Where(e => e.FullName == ManifestName).ToArray();
        if (manifestEntries.Length != 1 || manifestEntries[0].Length > 2_000_000) throw new InvalidDataException("Missing package manifest.");
        using Stream input = manifestEntries[0].Open();
        var manifest = JsonSerializer.Deserialize<PackageManifest>(input) ?? throw new InvalidDataException("Invalid package manifest.");
        ValidateManifest(manifest, staging);
        if (manifest.Version != expectedVersion) throw new InvalidDataException("Package version mismatch.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) { Resolve(staging, entry.FullName.TrimEnd('/')); continue; }
            string path = Resolve(staging, entry.FullName);
            if (!seen.Add(entry.FullName) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Duplicate or linked ZIP entry.");
            total += entry.Length;
            if (total > 2_500_000_000 || (entry.FullName != ManifestName && !manifest.Files.ContainsKey(entry.FullName)))
                throw new InvalidDataException("Unlisted or oversized package payload.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
            if (entry.FullName != ManifestName && !UpdateFiles.Hash(path).Equals(manifest.Files[entry.FullName], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Manifest checksum mismatch.");
        }
        if (seen.Count != manifest.Files.Count + 1) throw new InvalidDataException("Package files are missing.");
        return manifest;
    }

    public static void ValidateTarget(string target, string[] protectedPaths)
    {
        UpdateFiles.NoLinks(target);
        if (!File.Exists(Path.Combine(target, "TuckPane.exe"))) throw new IOException("Target is not a TuckPane program directory.");
        foreach (string path in protectedPaths)
            if (Path.GetFullPath(path).TrimEnd('\\', '/').Equals(Path.GetFullPath(target).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                UpdateFiles.Within(path, target) || UpdateFiles.Within(target, path))
                throw new IOException("Program and protected data directories overlap. Move the portable program to a separate directory before updating.");
    }

    public static void Apply(string staging, string target, string backup, string[] protectedPaths, Action<string>? beforeWrite = null)
    {
        ValidateTarget(target, protectedPaths);
        PackageManifest next = Read(staging);
        PackageManifest previous = Read(target);
        if (ReleaseRules.StableVersion(next.Version) <= ReleaseRules.StableVersion(previous.Version))
            throw new InvalidDataException("Refusing a downgrade or identical version.");
        UpdateFiles.NoLinks(backup);
        if (Directory.Exists(backup)) throw new IOException("Backup directory already exists.");
        Directory.CreateDirectory(backup);
        string[] names = next.Files.Keys.Concat(previous.Files.Keys).Append(ManifestName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var journal = new List<ReplacementFile>();
        foreach (string name in names)
        {
            string destination = Resolve(target, name);
            bool existed = File.Exists(destination);
            if (Directory.Exists(destination) || (existed && name != ManifestName && !previous.Files.ContainsKey(name)))
                throw new IOException("An extra user file occupies a new program path: " + name);
            if (name != ManifestName && next.Files.TryGetValue(name, out string? hash) && !UpdateFiles.Hash(Resolve(staging, name)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged file was changed.");
            if (existed)
            {
                string saved = Resolve(backup, name);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.Copy(destination, saved);
            }
            journal.Add(new(name, existed));
        }
        UpdateFiles.WriteJson(Path.Combine(backup, "replacement-journal.json"), journal);
        var touched = new List<ReplacementFile>();
        try
        {
            // The manifest is installed last and acts as the committed version marker.
            foreach (ReplacementFile file in journal.OrderBy(f => f.Name == ManifestName))
            {
                string destination = Resolve(target, file.Name);
                beforeWrite?.Invoke(file.Name);
                touched.Add(file);
                if (file.Name == ManifestName || next.Files.ContainsKey(file.Name))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(Resolve(staging, file.Name), destination, true);
                }
                else if (File.Exists(destination)) File.Delete(destination);
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (ReplacementFile file in touched.AsEnumerable().Reverse())
            {
                try
                {
                    string destination = Resolve(target, file.Name);
                    if (file.Existed) File.Copy(Resolve(backup, file.Name), destination, true);
                    else if (File.Exists(destination)) File.Delete(destination);
                }
                catch (Exception restoreFailure) { errors.Add(restoreFailure); }
            }
            throw new AggregateException(errors.Count == 1 ? "Update failed; previous program files restored." : "Update and recovery failed; keep the program backup for manual recovery.", errors);
        }
    }

    public static PackageManifest Read(string root)
    {
        string path = Resolve(root, ManifestName);
        if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("Oversized manifest.");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing manifest.");
        ValidateManifest(manifest, root);
        return manifest;
    }
}
