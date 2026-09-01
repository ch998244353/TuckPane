namespace TuckPane.Services;

using TuckPane.Models;

public static class AppPaths
{
    private const string ProductDirectoryName = "TuckPane";
    private const string LegacyProductDirectoryName = "GlassFolder";
    private static readonly string? TestRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
    private static readonly (string UserRoot, string LocalRoot) SelectedRoots = SelectRoots();
    private static int _noteStagingCleanupStarted;

    public static string UserRoot { get; } = SelectedRoots.UserRoot;
    public static string ItemsRoot { get; } = Path.Combine(UserRoot, "Items");
    public static string WindowsRoot { get; } = Path.Combine(UserRoot, "Windows");
    public static string LocalRoot { get; } = SelectedRoots.LocalRoot;
    public static string IconCacheRoot { get; } = Path.Combine(LocalRoot, "icon-cache");
    public static string NotesRoot { get; } = Path.Combine(LocalRoot, "notes");
    public static string NoteStagingRoot { get; } = Path.Combine(LocalRoot, "note-staging");
    public static string StatePath { get; } = Path.Combine(LocalRoot, "state.json");
    public static string BackupStatePath { get; } = Path.Combine(LocalRoot, "state.json.bak");
    public static string LogPath { get; } = Path.Combine(LocalRoot, "TuckPane.log");
    internal static string DesktopRoot { get; } = TestRoot is null
        ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        : Path.Combine(Path.GetFullPath(TestRoot), "Desktop");

    internal static bool IsTestMode => TestRoot is not null;

    internal static bool UsesLegacyRoots { get; } = Path.GetFileName(UserRoot)
        .Equals(LegacyProductDirectoryName, StringComparison.OrdinalIgnoreCase);

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ItemsRoot);
        Directory.CreateDirectory(WindowsRoot);
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(IconCacheRoot);
        Directory.CreateDirectory(NotesRoot);
        Directory.CreateDirectory(NoteStagingRoot);
        if (Interlocked.Exchange(ref _noteStagingCleanupStarted, 1) == 0) CleanupNoteStaging();
    }

    internal static void CleanupNoteStaging()
    {
        string root = Path.GetFullPath(NoteStagingRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The note staging root cannot be a reparse point.");
            foreach (string candidate in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                string fullPath = Path.GetFullPath(candidate)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(Path.GetDirectoryName(fullPath), root, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(Path.GetFileName(fullPath), "N", out _)) continue;
                try
                {
                    bool reparsePoint = (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0;
                    Directory.Delete(fullPath, recursive: !reparsePoint);
                }
                catch (Exception ex)
                {
                    LogNoteStagingCleanupFailure($"无法清理旧便签暂存目录：{fullPath}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            LogNoteStagingCleanupFailure($"无法枚举便签暂存目录：{root}", ex);
        }
    }

    private static void LogNoteStagingCleanupFailure(string message, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LocalRoot);
            File.AppendAllText(LogPath,
                $"{DateTimeOffset.Now:O} [ERROR] {message}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch
        {
            // Staging cleanup and its diagnostics must never prevent startup.
        }
    }

    public static string ResolveStoragePath(string relativePath)
    {
        string normalized = string.IsNullOrWhiteSpace(relativePath) ? "Items" : relativePath;
        string fullPath = Path.GetFullPath(Path.Combine(UserRoot, normalized));
        string root = Path.GetFullPath(UserRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(AppStrings.Get("StorageOutsideRoot"));
        }
        return fullPath;
    }

    public static string ResolveStoragePath(OrganizerDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.StorageAbsolutePath))
        {
            string path = definition.StorageAbsolutePath.Trim();
            if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException(AppStrings.Get("StorageAbsoluteRequired"));
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        return ResolveStoragePath(definition.StorageRelativePath);
    }

    public static string CreateStorageRelativePath(string name, Guid id)
    {
        return Path.Combine("Windows", CreateOwnedContainerName(name, id));
    }

    public static string ValidateCustomStoragePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException(AppStrings.Get("StorageAbsoluteRequired"));
        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (fullPath.Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(AppStrings.Get("StorageProtectedPath"));
        if (new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidOperationException(AppStrings.Get("StorageProtectedPath"));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException(AppStrings.Get("StorageFolderMissing"));

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] protectedPaths =
        [
            userProfile,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(userProfile, "Downloads"),
            UserRoot,
            LocalRoot
        ];
        if (protectedPaths.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeDirectory)
            .Any(protectedPath => SamePath(fullPath, protectedPath) || IsAncestor(fullPath, protectedPath)))
        {
            throw new InvalidOperationException(AppStrings.Get("StorageProtectedPath"));
        }
        return fullPath;
    }

    internal static bool PathsOverlap(string first, string second)
    {
        string left = NormalizeDirectory(first);
        string right = NormalizeDirectory(second);
        return SamePath(left, right) || IsAncestor(left, right) || IsAncestor(right, left);
    }

    
    
    public static string? GetOwnedStorageContainer(OrganizerDefinition definition)
    {
        string itemsPath;
        try { itemsPath = ResolveStoragePath(definition); }
        catch { return null; }
        if (!Path.GetFileName(itemsPath).Equals("Items", StringComparison.OrdinalIgnoreCase)) return null;
        string? container = Path.GetDirectoryName(itemsPath);
        if (container is null) return null;
        string suffix = "-" + definition.Id.ToString("N")[..8];
        return Path.GetFileName(container).EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? container : null;
    }

    private static string CreateOwnedContainerName(string name, Guid id)
    {
        string safeName = string.Concat((string.IsNullOrWhiteSpace(name) ? AppStrings.DefaultOrganizerName : name.Trim())
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        if (safeName.Length > 36) safeName = safeName[..36];
        return $"{safeName}-{id.ToString("N")[..8]}";
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool SamePath(string first, string second) =>
        first.Equals(second, StringComparison.OrdinalIgnoreCase);

    private static bool IsAncestor(string ancestor, string descendant) =>
        descendant.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static (string UserRoot, string LocalRoot) SelectRoots()
    {
        string userProfile = TestRoot is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.Combine(Path.GetFullPath(TestRoot), "UserProfile");
        string localAppData = TestRoot is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(Path.GetFullPath(TestRoot), "LocalAppData");

        string userRoot = Path.Combine(userProfile, ProductDirectoryName);
        string localRoot = Path.Combine(localAppData, ProductDirectoryName);
        if (Directory.Exists(userRoot) || Directory.Exists(localRoot)) return (userRoot, localRoot);

        string legacyUserRoot = Path.Combine(userProfile, LegacyProductDirectoryName);
        string legacyLocalRoot = Path.Combine(localAppData, LegacyProductDirectoryName);
        return Directory.Exists(legacyUserRoot) || Directory.Exists(legacyLocalRoot)
            ? (legacyUserRoot, legacyLocalRoot)
            : (userRoot, localRoot);
    }
}
