namespace TuckPane.Services;

using System.Text.Json;
using System.Text.Json.Serialization;
using TuckPane.Core;
using TuckPane.Models;

public sealed class NoteStore
{
    internal const int MaximumHtmlLength = 64 * 1024 * 1024;
    internal const long MaximumPortableFileLength = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions PortableJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root;

    public NoteStore(string? root = null) => _root = Path.GetFullPath(root ?? AppPaths.NotesRoot);

    public async Task<NoteDocument> LoadAsync(Guid noteId)
    {
        string path = GetPath(noteId);
        Exception? loadError = null;
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                await using FileStream stream = File.OpenRead(candidate);
                NoteDocument document = await JsonSerializer.DeserializeAsync<NoteDocument>(stream, JsonOptions) ?? new NoteDocument();
                document.Html ??= string.Empty;
                if (document.Html.Length > MaximumHtmlLength)
                    throw new InvalidDataException("The note document exceeds the supported size.");
                document.Version = 1;
                return document;
            }
            catch (Exception ex)
            {
                loadError = ex;
                AppLogger.Error($"无法读取便签文件：{candidate}", ex);
            }
        }
        if (loadError is not null)
            throw new InvalidDataException("The note document and its backup could not be read.", loadError);
        return new NoteDocument();
    }

    public async Task SaveAsync(Guid noteId, NoteDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Html ??= string.Empty;
        if (document.Html.Length > MaximumHtmlLength)
            throw new InvalidDataException("The note document exceeds the supported size.");
        document.Version = 1;
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_root);
            string path = GetPath(noteId);
            string temporary = path + ".tmp";
            await using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            }))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions);
                await stream.FlushAsync();
            }
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CopyAsync(Guid sourceId, Guid destinationId) =>
        await SaveAsync(destinationId, await LoadAsync(sourceId));

    public async Task DeleteAsync(Guid noteId)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (string suffix in new[] { string.Empty, ".bak", ".tmp" })
            {
                string path = GetPath(noteId) + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> ExistsAsync(Guid noteId) => Task.FromResult(File.Exists(GetPath(noteId)));

    internal static string ValidatePortableDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException(AppStrings.Get("StorageAbsoluteRequired"));
        if (directory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException(AppStrings.Get("PortableNoteLocalFolderRequired"));
        string fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException(AppStrings.Get("StorageFolderMissing"));
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        DriveType driveType = new DriveInfo(root).DriveType;
        if (driveType is not DriveType.Fixed and not DriveType.Removable)
            throw new InvalidOperationException(AppStrings.Get("PortableNoteLocalFolderRequired"));
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    internal async Task<string> CreatePortableStagingAsync(string noteName, PortableNoteDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidatePortableDocument(document);
        string directory = Path.Combine(AppPaths.NoteStagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, CreatePortableFileName(noteName));
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await WritePortableTemporaryAsync(temporary, document);
            File.Move(temporary, path);
            return path;
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            throw;
        }
    }

    internal async Task<string> CreatePortableAsync(string directory, string noteName, PortableNoteDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidatePortableDocument(document);
        string fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory)) throw new DirectoryNotFoundException(fullDirectory);
        string requestedPath = Path.Combine(fullDirectory, CreatePortableFileName(noteName));
        await _gate.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < 128; attempt++)
            {
                string path = StorageService.GetUniquePath(requestedPath);
                string temporary = Path.Combine(fullDirectory, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    await WritePortableTemporaryAsync(temporary, document);
                    File.Move(temporary, path);
                    return path;
                }
                catch (IOException) when (File.Exists(path) || Directory.Exists(path))
                {
                    AppLogger.Info($"便携便签名称被并发占用，重新编号：{path}");
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            throw new IOException("目标目录冲突过于频繁，未创建便签。");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<string> CreateTodoAsync(string directory, string todoName, PortableTodoDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateTodoDocument(document);
        string fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory)) throw new DirectoryNotFoundException(fullDirectory);
        string requestedPath = Path.Combine(fullDirectory, CreateTodoFileName(todoName));
        await _gate.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < 128; attempt++)
            {
                string path = StorageService.GetUniquePath(requestedPath);
                string temporary = Path.Combine(fullDirectory, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    await WritePortableTemporaryAsync(temporary, document);
                    File.Move(temporary, path);
                    return path;
                }
                catch (IOException) when (File.Exists(path) || Directory.Exists(path))
                {
                    AppLogger.Info($"便携待办名称被并发占用，重新编号：{path}");
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            throw new IOException("目标目录冲突过于频繁，未创建待办。");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<PortableNoteDocument> LoadPortableAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(fullPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        if (stream.Length > MaximumPortableFileLength)
            throw new InvalidDataException("The portable note exceeds 64 MiB.");
        try
        {
            PortableNoteDocument document = await JsonSerializer.DeserializeAsync<PortableNoteDocument>(stream, PortableJsonOptions)
                ?? throw new InvalidDataException("The portable note is empty.");
            ValidatePortableDocument(document);
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The portable note is not valid TuckPane.Note JSON v1.", ex);
        }
    }

    internal async Task SavePortableAsync(string path, PortableNoteDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidatePortableDocument(document);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The portable note has no parent directory.");
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The portable note was moved or deleted.", fullPath);
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        await _gate.WaitAsync();
        try
        {
            await WritePortableTemporaryAsync(temporary, document);
            File.Replace(temporary, fullPath, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    internal async Task<PortableTodoDocument> LoadTodoAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(fullPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        if (stream.Length > MaximumPortableFileLength)
            throw new InvalidDataException("The portable todo exceeds 64 MiB.");
        try
        {
            PortableTodoDocument document = await JsonSerializer.DeserializeAsync<PortableTodoDocument>(stream, PortableJsonOptions)
                ?? throw new InvalidDataException("The portable todo is empty.");
            ValidateTodoDocument(document);
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The portable todo is not valid TuckPane.Todo JSON v1.", ex);
        }
    }

    internal async Task SaveTodoAsync(string path, PortableTodoDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateTodoDocument(document);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The portable todo has no parent directory.");
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The portable todo was moved or deleted.", fullPath);
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        await _gate.WaitAsync();
        try
        {
            await WritePortableTemporaryAsync(temporary, document);
            File.Replace(temporary, fullPath, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    internal async Task<IReadOnlyList<string>> ApplyThemeToTopLevelPortableFilesAsync(
        string directory,
        NoteTheme theme,
        IReadOnlySet<string>? excludedPaths = null)
    {
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) return [];
        string[] paths;
        try { paths = Directory.GetFiles(root, "*.tucknote", SearchOption.TopDirectoryOnly); }
        catch (Exception ex)
        {
            AppLogger.Error($"无法枚举便签主题同步目录：{root}", ex);
            return [root];
        }

        var failed = new List<string>();
        foreach (string path in paths)
        {
            string fullPath = Path.GetFullPath(path);
            if (excludedPaths?.Contains(fullPath) == true) continue;
            try
            {
                PortableNoteDocument portable = await LoadPortableAsync(fullPath);
                portable.Theme = theme;
                await SavePortableAsync(fullPath, portable);
            }
            catch (Exception ex)
            {
                failed.Add(fullPath);
                AppLogger.Error($"无法同步便签文件主题：{fullPath}", ex);
            }
        }
        return failed;
    }

    internal async Task<IReadOnlyList<string>> ApplyThemeToTopLevelTodoFilesAsync(
        string directory,
        NoteTheme theme,
        IReadOnlySet<string>? excludedPaths = null)
    {
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) return [];
        string[] paths;
        try { paths = Directory.GetFiles(root, "*.tucktodo", SearchOption.TopDirectoryOnly); }
        catch (Exception ex)
        {
            AppLogger.Error($"无法枚举待办主题同步目录：{root}", ex);
            return [root];
        }

        var failed = new List<string>();
        foreach (string path in paths)
        {
            string fullPath = Path.GetFullPath(path);
            if (excludedPaths?.Contains(fullPath) == true) continue;
            try
            {
                PortableTodoDocument portable = await LoadTodoAsync(fullPath);
                portable.Theme = theme;
                await SaveTodoAsync(fullPath, portable);
            }
            catch (Exception ex)
            {
                failed.Add(fullPath);
                AppLogger.Error($"无法同步待办文件主题：{fullPath}", ex);
            }
        }
        return failed;
    }

    internal static string CreatePortableFileName(string? noteName) =>
        CreatePortableFileName(noteName, "便签", ".tucknote");

    internal static string CreateTodoFileName(string? todoName) =>
        CreatePortableFileName(todoName, "待办", ".tucktodo");

    private static string CreatePortableFileName(string? name, string fallback, string extension)
    {
        string safe = string.Concat((string.IsNullOrWhiteSpace(name) ? fallback : name.Trim())
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character))
            .TrimEnd(' ', '.');
        if (safe.Length == 0) safe = fallback;
        if (safe.Length > 120)
        {
            int length = 120;
            if (char.IsHighSurrogate(safe[length - 1]) && char.IsLowSurrogate(safe[length])) length--;
            safe = safe[..length].TrimEnd(' ', '.');
        }
        string deviceName = safe.Split('.', 2)[0];
        if (IsReservedDeviceName(deviceName)) safe = '_' + safe;
        return safe + extension;
    }

    private static async Task WritePortableTemporaryAsync<TDocument>(string temporary, TDocument document)
    {
        await using var stream = new FileStream(temporary, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        });
        await JsonSerializer.SerializeAsync(stream, document, PortableJsonOptions);
        await stream.FlushAsync();
        if (stream.Length > MaximumPortableFileLength)
            throw new InvalidDataException("The portable note exceeds 64 MiB.");
    }

    private static void ValidatePortableDocument(PortableNoteDocument document)
    {
        if (!string.Equals(document.Format, "TuckPane.Note", StringComparison.Ordinal))
            throw new InvalidDataException("Unknown portable note format.");
        if (document.Version != 1) throw new InvalidDataException("Unsupported portable note version.");
        if (!Enum.IsDefined(document.Theme)) throw new InvalidDataException("Unknown portable note theme.");
        if (!double.IsFinite(document.FontSize) ||
            document.FontSize < OrganizerNoteRules.MinimumFontSize ||
            document.FontSize > OrganizerNoteRules.MaximumFontSize)
            throw new InvalidDataException("The portable note font size is invalid.");
        if (document.Html is null || document.Html.Length > MaximumHtmlLength)
            throw new InvalidDataException("The portable note HTML is invalid or too large.");
        if (document.Placement is not { } placement) return;
        if (placement.MonitorDevice is null ||
            !double.IsFinite(placement.XDip) || !double.IsFinite(placement.YDip) ||
            !double.IsFinite(placement.WidthDip) || !double.IsFinite(placement.HeightDip) ||
            placement.WidthDip is < 280 or > 1600 || placement.HeightDip is < 220 or > 1200)
            throw new InvalidDataException("The portable note placement is invalid.");
    }

    private static void ValidateTodoDocument(PortableTodoDocument document)
    {
        if (!string.Equals(document.Format, "TuckPane.Todo", StringComparison.Ordinal))
            throw new InvalidDataException("Unknown portable todo format.");
        if (document.Version != 1) throw new InvalidDataException("Unsupported portable todo version.");
        if (!Enum.IsDefined(document.Theme)) throw new InvalidDataException("Unknown portable todo theme.");
        if (!double.IsFinite(document.FontSize) ||
            document.FontSize < OrganizerNoteRules.MinimumFontSize ||
            document.FontSize > OrganizerNoteRules.MaximumFontSize)
            throw new InvalidDataException("The portable todo font size is invalid.");
        if (document.Tasks is null) throw new InvalidDataException("The portable todo task list is missing.");
        var ids = new HashSet<Guid>();
        foreach (PortableTodoTask task in document.Tasks)
        {
            if (task is null || task.Id == Guid.Empty || !ids.Add(task.Id))
                throw new InvalidDataException("The portable todo contains an invalid or duplicate task ID.");
            string normalized = TodoRules.NormalizeText(task.Text);
            if (task.Text is null || normalized.Length == 0 || normalized != task.Text)
                throw new InvalidDataException("The portable todo contains invalid task text.");
            if (task.Done != task.CompletedAtUtc.HasValue ||
                task.CompletedAtUtc is DateTimeOffset completed && completed.Offset != TimeSpan.Zero)
                throw new InvalidDataException("The portable todo contains an invalid completion timestamp.");
        }
        if (document.Placement is not { } placement) return;
        if (placement.MonitorDevice is null ||
            !double.IsFinite(placement.XDip) || !double.IsFinite(placement.YDip) ||
            !double.IsFinite(placement.WidthDip) || !double.IsFinite(placement.HeightDip) ||
            placement.WidthDip is < 280 or > 1600 || placement.HeightDip is < 340 or > 1200)
            throw new InvalidDataException("The portable todo placement is invalid.");
    }

    internal static bool IsReservedDeviceName(string name)
    {
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Length == 4 && name[3] is >= '1' and <= '9' &&
            (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private string GetPath(Guid noteId) => Path.Combine(_root, $"{noteId:N}.json");
}
