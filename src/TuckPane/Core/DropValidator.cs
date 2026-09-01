using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane.Core;

public sealed record DropValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static DropValidationResult Success { get; } = new(true, []);
}

public static class DropValidator
{
    public static DropValidationResult ValidateBatch(IEnumerable<string> sourcePaths, string itemsRoot)
    {
        string normalizedRoot = Path.GetFullPath(itemsRoot).TrimEnd(Path.DirectorySeparatorChar);
        string[] paths = sourcePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var errors = new List<string>();

        if (paths.Length == 0)
        {
            errors.Add(AppStrings.Get("NoImportableItems"));
        }

        foreach (string source in paths)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(source);
            }
            catch
            {
                errors.Add(AppStrings.Format("InvalidPathFormat", source));
                continue;
            }

            bool isDirectory = Directory.Exists(fullPath);
            bool isFile = File.Exists(fullPath);
            if (!isDirectory && !isFile)
            {
                errors.Add(AppStrings.Format("MissingItemFormat", Path.GetFileName(fullPath)));
                continue;
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(fullPath);
            }
            catch (Exception ex)
            {
                errors.Add(AppStrings.Format("CannotReadFormat", Path.GetFileName(fullPath), ex.Message));
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                errors.Add(AppStrings.Format("UnsupportedReparseFormat", Path.GetFileName(fullPath)));
                continue;
            }

            if (isDirectory && !TryValidateTree(fullPath, out string? treeError))
            {
                errors.Add(treeError!);
                continue;
            }

            string trimmedSource = fullPath.TrimEnd(Path.DirectorySeparatorChar);
            if (trimmedSource.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                normalizedRoot.StartsWith(trimmedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                trimmedSource.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(AppStrings.Format("RecursiveImportFormat", Path.GetFileName(fullPath)));
                continue;
            }

        }

        return errors.Count == 0 ? DropValidationResult.Success : new(false, errors);
    }

    public static bool TryGetKind(string path, out WidgetItemKind kind)
    {
        if (Directory.Exists(path))
        {
            kind = WidgetItemKind.Folder;
            return true;
        }

        string extension = Path.GetExtension(path);
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            kind = WidgetItemKind.Shortcut;
            return true;
        }

        if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            kind = WidgetItemKind.InternetShortcut;
            return true;
        }

        if (extension.Equals(".tucknote", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            kind = WidgetItemKind.PortableNote;
            return true;
        }

        if (extension.Equals(".tucktodo", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            kind = WidgetItemKind.PortableTodo;
            return true;
        }

        if (File.Exists(path))
        {
            kind = WidgetItemKind.File;
            return true;
        }

        kind = default;
        return false;
    }

    public static bool IsExecutable(string path) => File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".com" or ".scr";

    private static bool TryValidateTree(string root, out string? error)
    {
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    error = AppStrings.Format("DirectoryReparseFormat", Path.GetFileName(entry));
                    return false;
                }
            }
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            error = AppStrings.Format("DirectoryReadErrorFormat", Path.GetFileName(root), ex.Message);
            return false;
        }
    }
}
