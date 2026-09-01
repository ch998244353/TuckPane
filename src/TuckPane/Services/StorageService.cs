using TuckPane.Core;
using TuckPane.Models;
using Microsoft.Win32;

namespace TuckPane.Services;

public sealed class StorageService
{
    private readonly string _itemsRoot;
    private readonly string? _ownedContainerPath;
    private readonly bool _exportEmptyDirectory;

    public StorageService(
        string? itemsRoot = null,
        bool createIfMissing = true,
        string? ownedContainerPath = null,
        bool exportEmptyDirectory = false)
    {
        _itemsRoot = Path.GetFullPath(itemsRoot ?? AppPaths.ItemsRoot).TrimEnd(Path.DirectorySeparatorChar);
        _ownedContainerPath = string.IsNullOrWhiteSpace(ownedContainerPath)
            ? null
            : Path.GetFullPath(ownedContainerPath).TrimEnd(Path.DirectorySeparatorChar);
        _exportEmptyDirectory = exportEmptyDirectory;
        if (createIfMissing) Directory.CreateDirectory(_itemsRoot);
    }

    public string ItemsRoot => _itemsRoot;
    public bool Exists => Directory.Exists(_itemsRoot);
    public bool IsEmpty => !Exists || !Directory.EnumerateFileSystemEntries(_itemsRoot).Any();

    public void EnsureCreated() => Directory.CreateDirectory(_itemsRoot);

    public async Task<IReadOnlyList<TransferOutcome>> ImportBatchAsync(
        IReadOnlyList<string> sourcePaths,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        string? targetFolder = null)
    {
        Directory.CreateDirectory(_itemsRoot);
        string targetRoot = ResolveImportTarget(targetFolder);
        DropValidationResult validation = DropValidator.ValidateBatch(sourcePaths, targetRoot);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));

        var outcomes = new List<TransferOutcome>();
        foreach (string sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransferOutcome outcome = DropValidator.IsExecutable(sourcePath)
                ? CreateExecutableShortcut(sourcePath, targetRoot)
                : await MoveOneAsync(sourcePath, progress, cancellationToken, targetRoot);
            outcomes.Add(outcome);
            if (outcome.Status is TransferStatus.Failed or TransferStatus.Cancelled) break;
        }
        return outcomes;
    }

    private string ResolveImportTarget(string? targetFolder)
    {
        if (string.IsNullOrWhiteSpace(targetFolder)) return _itemsRoot;
        string fullRoot = Path.GetFullPath(_itemsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(_itemsRoot, targetFolder));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(candidate))
        {
            throw new InvalidOperationException(AppStrings.Format("DropTargetFolderMissingFormat", targetFolder));
        }
        return candidate;
    }

    public async Task<IReadOnlyList<TransferOutcome>> CopyBatchAsync(
        IReadOnlyList<string> sourcePaths,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        string? targetFolder = null)
    {
        Directory.CreateDirectory(_itemsRoot);
        string targetRoot = ResolveImportTarget(targetFolder);
        DropValidationResult validation = DropValidator.ValidateBatch(sourcePaths, targetRoot);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));

        var outcomes = new List<TransferOutcome>();
        foreach (string sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransferOutcome outcome = DropValidator.IsExecutable(sourcePath)
                ? CreateExecutableShortcut(sourcePath, targetRoot)
                : await CopyOneIntoAsync(sourcePath, progress, cancellationToken, targetRoot);
            outcomes.Add(outcome);
            if (outcome.Status is TransferStatus.Failed or TransferStatus.Cancelled) break;
        }
        return outcomes;
    }

    private async Task<TransferOutcome> CopyOneIntoAsync(
        string sourcePath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        string targetRoot)
    {
        string source = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar);
        bool isDirectory = Directory.Exists(source);
        string destination = GetUniquePath(Path.Combine(targetRoot, Path.GetFileName(source)), isDirectory);
        string staging = Path.Combine(_itemsRoot, $".glassfolder-staging-{Guid.NewGuid():N}");
        string itemName = Path.GetFileName(source);
        try
        {
            long totalBytes = isDirectory ? BuildManifest(source).TotalBytes : new FileInfo(source).Length;
            long copiedBytes = 0;
            Action<int> report = bytes =>
            {
                copiedBytes += bytes;
                progress?.Report(new TransferProgress(itemName, copiedBytes, totalBytes));
            };
            if (isDirectory)
            {
                await CopyDirectoryAsync(source, staging, report, cancellationToken);
                VerifyEquivalent(source, staging);
                Directory.Move(staging, destination);
            }
            else
            {
                await CopyFileAsync(source, staging, report, cancellationToken);
                if (new FileInfo(source).Length != new FileInfo(staging).Length) throw new IOException(AppStrings.Get("CopySizeMismatch"));
                File.Move(staging, destination);
            }
            return new(source, destination, TransferStatus.Copied, AppStrings.Get("Copied"));
        }
        catch (OperationCanceledException)
        {
            TryDelete(staging);
            return new(source, null, TransferStatus.Cancelled, AppStrings.Get("CopyCancelled"));
        }
        catch (Exception ex)
        {
            TryDelete(staging);
            AppLogger.Error($"复制导入失败：{source}", ex);
            return new(source, null, TransferStatus.Failed, ex.Message);
        }
    }

    internal string CreateUniqueFolder(string requestedName)
    {
        string name = ValidateNewFolderName(requestedName);
        Directory.CreateDirectory(_itemsRoot);
        string destination = GetUniquePath(Path.Combine(_itemsRoot, name), isDirectory: true);
        Directory.CreateDirectory(destination);
        return destination;
    }

    internal static string ValidateNewFolderName(string requestedName)
    {
        string name = requestedName.Trim();
        if (name.Length == 0) throw new InvalidOperationException(AppStrings.Get("FolderNameEmptyError"));
        if (name.Length > 120 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidOperationException(AppStrings.Get("FolderNameInvalidError"));
        string deviceName = name.Split('.')[0].ToUpperInvariant();
        if (deviceName is "CON" or "PRN" or "AUX" or "NUL" or
            "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or
            "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
            throw new InvalidOperationException(AppStrings.Get("FolderNameReservedError"));
        return name;
    }

    public IReadOnlyList<WidgetItem> ReadItems()
    {
        if (!Directory.Exists(_itemsRoot)) return [];
        var items = new List<WidgetItem>();
        foreach (string path in Directory.EnumerateFileSystemEntries(_itemsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            string relativeName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            if (relativeName.StartsWith(".glassfolder-staging-", StringComparison.OrdinalIgnoreCase) ||
                !DropValidator.TryGetKind(path, out WidgetItemKind kind))
            {
                continue;
            }
            items.Add(new WidgetItem(GetDisplayName(relativeName, kind), path, relativeName, kind));
        }
        return items;
    }

    public IReadOnlyList<string> ReadUnsupportedNames() => [];

    public async Task<TransferOutcome> ExportToDesktopAsync(
        string windowName,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_itemsRoot)) return new(_itemsRoot, null, TransferStatus.Moved, AppStrings.Get("StorageMissingDeleted"));
        if (!_exportEmptyDirectory && !Directory.EnumerateFileSystemEntries(_itemsRoot).Any())
        {
            Directory.Delete(_itemsRoot);
            DeleteEmptyParent();
            return new(_itemsRoot, null, TransferStatus.Moved, AppStrings.Get("EmptyDeleted"));
        }

        string desktop = AppPaths.DesktopRoot;
        if (string.IsNullOrWhiteSpace(desktop)) return new(_itemsRoot, null, TransferStatus.Failed, AppStrings.Get("DesktopUnavailable"));
        Directory.CreateDirectory(desktop);
        string destination = GetUniquePath(Path.Combine(desktop, AppStrings.Format("ExportFolderFormat", SanitizeName(windowName))), isDirectory: true);

        try
        {
            if (SameVolume(_itemsRoot, destination))
            {
                Directory.Move(_itemsRoot, destination);
                DeleteEmptyParent();
                return new(_itemsRoot, destination, TransferStatus.Moved, AppStrings.Get("ExportedDesktop"));
            }

            string staging = Path.Combine(desktop, $".glassfolder-staging-{Guid.NewGuid():N}");
            try
            {
                DirectoryManifest manifest = BuildManifest(_itemsRoot);
                long copied = 0;
                await CopyDirectoryAsync(_itemsRoot, staging, bytes =>
                {
                    copied += bytes;
                    progress?.Report(new TransferProgress(windowName, copied, manifest.TotalBytes));
                }, cancellationToken);
                VerifyEquivalent(_itemsRoot, staging);
                Directory.Move(staging, destination);
                try
                {
                    Directory.Delete(_itemsRoot, recursive: true);
                    DeleteEmptyParent();
                }
                catch
                {
                    TryDelete(destination);
                    throw;
                }
                return new(_itemsRoot, destination, TransferStatus.Moved, AppStrings.Get("ExportedDesktopCrossVolume"));
            }
            catch
            {
                TryDelete(staging);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            return new(_itemsRoot, null, TransferStatus.Cancelled, AppStrings.Get("ExportCancelled"));
        }
        catch (Exception ex)
        {
            AppLogger.Error($"导出收纳窗失败：{_itemsRoot}", ex);
            return new(_itemsRoot, null, TransferStatus.Failed, ex.Message);
        }
    }

    public async Task<TransferOutcome> MoveItemToDesktopAsync(
        string sourcePath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar);
        bool isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source))
            return new(source, null, TransferStatus.Failed, AppStrings.Get("ShellDragMissing"));
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            return new(source, null, TransferStatus.Failed, AppStrings.Get("DesktopUnavailable"));
        return await Task.Run(
            () => MoveItemToDirectoryAsync(source, desktop, progress, cancellationToken),
            cancellationToken);
    }

    internal static async Task<TransferOutcome> MoveItemToDirectoryAsync(
        string sourcePath,
        string destinationDirectory,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar);
        bool isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source))
            return new(source, null, TransferStatus.Failed, AppStrings.Get("ShellDragMissing"));
        string destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        string requestedDestination = Path.Combine(destinationRoot, Path.GetFileName(source));
        for (int attempt = 0; attempt < 128; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = GetUniquePath(requestedDestination, isDirectory);
            TransferOutcome outcome = await MovePathAsync(
                source,
                destination,
                progress,
                cancellationToken,
                rollbackDestinationOnSourceDeleteFailure: true);
            if (outcome.Status != TransferStatus.Failed ||
                (!File.Exists(destination) && !Directory.Exists(destination))) return outcome;
            AppLogger.Info($"目标名称被并发占用，重新编号：{destination}");
        }
        return new(source, null, TransferStatus.Failed, "目标目录冲突过于频繁，源项目已保留。");
    }

    private TransferOutcome CreateExecutableShortcut(string sourcePath, string targetRoot)
    {
        try
        {
            string source = Path.GetFullPath(sourcePath);
            string destination = GetUniquePath(Path.Combine(targetRoot, Path.GetFileNameWithoutExtension(source) + ".lnk"), isDirectory: false);
            Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException(AppStrings.Get("ShellUnavailable"));
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(destination);
            shortcut.TargetPath = source;
            shortcut.WorkingDirectory = Path.GetDirectoryName(source)!;
            shortcut.Save();
            return new(source, destination, TransferStatus.ShortcutCreated, AppStrings.Get("ShortcutCreated"));
        }
        catch (Exception ex)
        {
            AppLogger.Error($"创建程序快捷方式失败：{sourcePath}", ex);
            return new(sourcePath, null, TransferStatus.Failed, ex.Message);
        }
    }

    private async Task<TransferOutcome> MoveOneAsync(
        string sourcePath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        string targetRoot)
    {
        string source = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar);
        bool isDirectory = Directory.Exists(source);
        string destination = GetUniquePath(Path.Combine(targetRoot, Path.GetFileName(source)), isDirectory);
        return await MovePathAsync(source, destination, progress, cancellationToken, rollbackDestinationOnSourceDeleteFailure: false);
    }

    private async Task<TransferOutcome> CopyOneAsync(
        string sourcePath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar);
        bool isDirectory = Directory.Exists(source);
        string destination = GetUniquePath(Path.Combine(_itemsRoot, Path.GetFileName(source)), isDirectory);
        string staging = Path.Combine(_itemsRoot, $".glassfolder-staging-{Guid.NewGuid():N}");
        string itemName = Path.GetFileName(source);
        try
        {
            long totalBytes = isDirectory ? BuildManifest(source).TotalBytes : new FileInfo(source).Length;
            long copiedBytes = 0;
            Action<int> report = bytes =>
            {
                copiedBytes += bytes;
                progress?.Report(new TransferProgress(itemName, copiedBytes, totalBytes));
            };
            if (isDirectory)
            {
                await CopyDirectoryAsync(source, staging, report, cancellationToken);
                VerifyEquivalent(source, staging);
                Directory.Move(staging, destination);
            }
            else
            {
                await CopyFileAsync(source, staging, report, cancellationToken);
                if (new FileInfo(source).Length != new FileInfo(staging).Length) throw new IOException(AppStrings.Get("CopySizeMismatch"));
                File.Move(staging, destination);
            }
            return new(source, destination, TransferStatus.Copied, AppStrings.Get("Copied"));
        }
        catch (OperationCanceledException)
        {
            TryDelete(staging);
            return new(source, null, TransferStatus.Cancelled, AppStrings.Get("CopyCancelled"));
        }
        catch (Exception ex)
        {
            TryDelete(staging);
            AppLogger.Error($"复制导入失败：{source}", ex);
            return new(source, null, TransferStatus.Failed, ex.Message);
        }
    }

    private static async Task<TransferOutcome> MovePathAsync(
        string source,
        string destination,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        bool rollbackDestinationOnSourceDeleteFailure)
    {
        bool isDirectory = Directory.Exists(source);
        string itemName = Path.GetFileName(source);
        try
        {
            if (SameVolume(source, destination))
            {
                if (isDirectory) Directory.Move(source, destination);
                else File.Move(source, destination);
                progress?.Report(new TransferProgress(itemName, 1, 1));
                return new(source, destination, TransferStatus.Moved, AppStrings.Get("Moved"));
            }

            string destinationRoot = Path.GetDirectoryName(destination) ?? throw new IOException(AppStrings.Get("MoveToDesktopFailed"));
            Directory.CreateDirectory(destinationRoot);
            string staging = Path.Combine(destinationRoot, $".glassfolder-staging-{Guid.NewGuid():N}");
            try
            {
                long totalBytes = isDirectory ? BuildManifest(source).TotalBytes : new FileInfo(source).Length;
                long copiedBytes = 0;
                if (isDirectory)
                {
                    await CopyDirectoryAsync(source, staging, bytes =>
                    {
                        copiedBytes += bytes;
                        progress?.Report(new TransferProgress(itemName, copiedBytes, totalBytes));
                    }, cancellationToken);
                    VerifyEquivalent(source, staging);
                    Directory.Move(staging, destination);
                }
                else
                {
                    await CopyFileAsync(source, staging, bytes =>
                    {
                        copiedBytes += bytes;
                        progress?.Report(new TransferProgress(itemName, copiedBytes, totalBytes));
                    }, cancellationToken);
                    if (new FileInfo(source).Length != new FileInfo(staging).Length) throw new IOException(AppStrings.Get("CopySizeMismatch"));
                    File.Move(staging, destination);
                }
            }
            catch (OperationCanceledException)
            {
                TryDelete(staging);
                return new(source, null, TransferStatus.Cancelled, AppStrings.Get("MoveSourceRetained"));
            }
            catch
            {
                TryDelete(staging);
                throw;
            }

            try
            {
                if (isDirectory) Directory.Delete(source, recursive: true);
                else File.Delete(source);
                return new(source, destination, TransferStatus.Moved, AppStrings.Get("CrossVolumeMoved"));
            }
            catch (Exception deleteException)
            {
                AppLogger.Error($"目标副本完整，但无法删除源项目：{source}", deleteException);
                if (rollbackDestinationOnSourceDeleteFailure)
                {
                    TryDelete(destination);
                    return new(source, null, TransferStatus.Failed, deleteException.Message);
                }
                return new(source, destination, TransferStatus.CopiedSourceRetained, AppStrings.Get("DuplicateRetained"));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"移动失败：{source}", ex);
            return new(source, null, TransferStatus.Failed, ex.Message);
        }
    }

    internal static string GetUniquePath(string requestedPath) => GetUniquePath(requestedPath, isDirectory: false);

    internal static string GetUniquePath(string requestedPath, bool isDirectory)
    {
        if (!File.Exists(requestedPath) && !Directory.Exists(requestedPath)) return requestedPath;
        string directory = Path.GetDirectoryName(requestedPath)!;
        string extension = isDirectory ? string.Empty : Path.GetExtension(requestedPath);
        string name = isDirectory ? Path.GetFileName(requestedPath) : Path.GetFileNameWithoutExtension(requestedPath);
        for (int number = 2; ; number++)
        {
            string candidate = Path.Combine(directory, $"{name} {number}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static string GetDisplayName(string relativeName, WidgetItemKind kind)
    {
        if (kind is WidgetItemKind.Folder or WidgetItemKind.Shortcut or WidgetItemKind.InternetShortcut or WidgetItemKind.PortableNote)
            return Path.GetFileNameWithoutExtension(relativeName);
        return ExplorerShowsExtensions() ? relativeName : Path.GetFileNameWithoutExtension(relativeName);
    }

    private static bool ExplorerShowsExtensions()
    {
        try
        {
            object? hidden = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced")?.GetValue("HideFileExt");
            return hidden is not int value || value == 0;
        }
        catch
        {
            return true;
        }
    }

    private void DeleteEmptyParent()
    {
        string? parent = Path.GetDirectoryName(_itemsRoot);
        if (parent is not null && _ownedContainerPath is not null &&
            parent.Equals(_ownedContainerPath, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
        }
    }

    private static string SanitizeName(string name)
    {
        string safe = string.Concat((string.IsNullOrWhiteSpace(name) ? AppStrings.DefaultOrganizerName : name.Trim())
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return safe.Length <= 80 ? safe : safe[..80];
    }

    private static bool SameVolume(string first, string second) =>
        string.Equals(Path.GetPathRoot(first), Path.GetPathRoot(second), StringComparison.OrdinalIgnoreCase);

    private static async Task CopyDirectoryAsync(string source, string destination, Action<int> reportBytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException(AppStrings.Format("UnsupportedReparsePathFormat", directory));
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException(AppStrings.Format("UnsupportedReparsePathFormat", file));
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await CopyFileAsync(file, target, reportBytes, cancellationToken);
        }
    }

    private static async Task CopyFileAsync(string source, string destination, Action<int> reportBytes, CancellationToken cancellationToken)
    {
        const int BufferSize = 1024 * 1024;
        byte[] buffer = new byte[BufferSize];
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            reportBytes(read);
        }
        await output.FlushAsync(cancellationToken);
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }

    private static void VerifyEquivalent(string source, string destination)
    {
        DirectoryManifest sourceManifest = BuildManifest(source);
        DirectoryManifest destinationManifest = BuildManifest(destination);
        if (sourceManifest.TotalBytes != destinationManifest.TotalBytes ||
            !sourceManifest.Entries.SequenceEqual(destinationManifest.Entries, StringComparer.OrdinalIgnoreCase))
        {
            throw new IOException(AppStrings.Get("CopyVerificationFailed"));
        }
    }

    private static DirectoryManifest BuildManifest(string root)
    {
        var entries = new List<string>();
        long totalBytes = 0;
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException(AppStrings.Format("UnsupportedReparsePathFormat", directory));
            entries.Add($"D|{Path.GetRelativePath(root, directory)}");
        }
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException(AppStrings.Format("UnsupportedReparsePathFormat", file));
            long length = new FileInfo(file).Length;
            totalBytes += length;
            entries.Add($"F|{Path.GetRelativePath(root, file)}|{length}");
        }
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        return new(entries, totalBytes);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法清理临时路径：{path}", ex);
        }
    }

    private sealed record DirectoryManifest(IReadOnlyList<string> Entries, long TotalBytes);
}
