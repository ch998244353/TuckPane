using System.Runtime.InteropServices;
using System.Text;
using TuckPane.Core;

namespace TuckPane.Services;

// External discovery runs on one background STA per completed sample. No UI calls,
// application launches, title guesses or PID lifetime assumptions are made here.
internal sealed class DockRunningService
{
    private readonly Dictionary<string, (long Stamp, long Length, DockItemIdentity Identity)> _identities = new(StringComparer.OrdinalIgnoreCase);

    private long _requestedEpoch, _cacheEpoch;
    internal void Invalidate() => Interlocked.Increment(ref _requestedEpoch);

    internal Task<Dictionary<string, bool>> SampleAsync(string[] paths)
    {
        var completion = new TaskCompletionSource<Dictionary<string, bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Sample(paths)); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "TuckPane Dock open-state" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private Dictionary<string, bool> Sample(string[] paths)
    {
        long epoch = Interlocked.Read(ref _requestedEpoch);
        if (_cacheEpoch != epoch) { _identities.Clear(); _cacheEpoch = epoch; }
        var windows = CaptureWindows();
        object? shell = null;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Type? type = Type.GetTypeFromProgID("Shell.Application");
            if (type is not null) shell = Activator.CreateInstance(type);
            if (shell is not null) CaptureFolders(shell, folders);
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                DockItemIdentity identity = ResolveCached(path, shell);
                result[path] = DockRunningState.IsOpen(identity, windows, folders);
            }
            foreach (string removed in _identities.Keys.Except(paths, StringComparer.OrdinalIgnoreCase).ToArray())
                _identities.Remove(removed);
            return result;
        }
        finally { Release(shell); }
    }

    private DockItemIdentity ResolveCached(string path, object? shell)
    {
        try
        {
            if (Directory.Exists(path)) return new(DockIdentityKind.Folder, Path.GetFullPath(path));
            var file = new FileInfo(path);
            if (!file.Exists) return default;
            long stamp = file.LastWriteTimeUtc.Ticks, length = file.Length;
            if (_identities.TryGetValue(path, out var old) && old.Stamp == stamp && old.Length == length) return old.Identity;
            var identity = Resolve(path, shell, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            // Retry unresolved shell identities on later samples (a COM failure is not permanent).
            if (identity.Kind != DockIdentityKind.Unknown) _identities[path] = (stamp, length, identity);
            else _identities.Remove(path);
            return identity;
        }
        catch { return default; }
    }

    private static DockItemIdentity Resolve(string path, object? shell, HashSet<string> visited)
    {
        if (visited.Count >= 8 || !visited.Add(path)) return default;
        if (Directory.Exists(path)) return new(DockIdentityKind.Folder, Path.GetFullPath(path));
        string extension = Path.GetExtension(path);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new(DockIdentityKind.Executable, Path.GetFullPath(path));
        if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return default;
        string? appId = ReadShortcutAppId(path, shell);
        if (ShellLinkTarget.TryRead(path, out string target, out string arguments))
        {
            target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
            if (!Path.IsPathRooted(target)) target = Path.GetFullPath(target, Path.GetDirectoryName(path)!);
            if (string.Equals(Path.GetFileName(target), "explorer.exe", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(appId) && string.Equals(arguments.Trim().Trim('"'),
                    @"shell:AppsFolder\" + appId, StringComparison.OrdinalIgnoreCase))
                return new(DockIdentityKind.AppId, appId);
            if (!DockRunningState.CanMatchExecutableShortcut(arguments)) return default;
            DockItemIdentity resolved = Resolve(target, shell, visited);
            if (resolved.Kind != DockIdentityKind.Unknown) return resolved;
        }
        return !string.IsNullOrWhiteSpace(appId) ? new(DockIdentityKind.AppId, appId) : default;
    }

    private static string? ReadShortcutAppId(string path, object? shell)
    {
        if (shell is null) return null;
        object? folder = null, item = null;
        try
        {
            folder = ((dynamic)shell).NameSpace(Path.GetDirectoryName(path));
            if (folder is null) return null;
            item = ((dynamic)folder).ParseName(Path.GetFileName(path));
            return item is null ? null : ((dynamic)item).ExtendedProperty("System.AppUserModel.ID") as string;
        }
        catch { return null; }
        finally { Release(item); Release(folder); }
    }

    private static List<DockOpenWindow> CaptureWindows()
    {
        var result = new List<DockOpenWindow>();
        var processes = new Dictionary<uint, (string? Path, string? Id)>();
        (string? Path, string? Id) Identity(IntPtr hwnd)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (processes.TryGetValue(pid, out var cached)) return cached;
            IntPtr process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return default;
            try
            {
                var path = new StringBuilder(32768); int length = path.Capacity;
                string? executable = QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
                uint idLength = 0;
                string? id = null;
                if (GetApplicationUserModelId(process, ref idLength, null) == 122 && idLength is > 1 and < 32768)
                {
                    var value = new StringBuilder((int)idLength);
                    if (GetApplicationUserModelId(process, ref idLength, value) == 0) id = value.ToString();
                }
                return processes[pid] = (executable, id);
            }
            finally { CloseHandle(process); }
        }
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsApplicationWindow(hwnd)) return true;
                var identity = Identity(hwnd);
                result.Add(new(identity.Path, identity.Id, true));
                // Classic UWP frames belong to ApplicationFrameHost; the child
                // owns the actual app identity. Do not descend arbitrary apps.
                if (string.Equals(Path.GetFileName(identity.Path), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                    EnumChildWindows(hwnd, (child, _) =>
                    {
                        var app = Identity(child);
                        if (!string.IsNullOrEmpty(app.Id)) result.Add(new(app.Path, app.Id, true));
                        return true;
                    }, IntPtr.Zero);
            }
            catch { /* A window may disappear between any two Win32 reads. */ }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsApplicationWindow(IntPtr hwnd) => IsWindowVisible(hwnd) &&
        (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() & NativeMethods.WS_EX_TOOLWINDOW) == 0 &&
        (NativeMethods.DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) < 0 || cloaked == 0);

    private static void CaptureFolders(object shell, HashSet<string> paths)
    {
        object? windows = null;
        try
        {
            windows = ((dynamic)shell).Windows();
            int count = ((dynamic)windows).Count;
            for (int i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    window = ((dynamic)windows).Item(i);
                    if (window is null || !IsApplicationWindow(new IntPtr((long)((dynamic)window).HWND))) continue;
                    string url = ((dynamic)window).LocationURL;
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
                        paths.Add(Path.GetFullPath(uri.LocalPath));
                }
                catch { /* Closed/inaccessible Explorer tabs remain unconfirmed. */ }
                finally { Release(window); }
            }
        }
        catch { /* Shell unavailable: no folder may be confirmed. */ }
        finally { Release(windows); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, NativeMethods.EnumWindowsProc callback, IntPtr param);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder? id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

internal static class ShellLinkTarget
{
    internal static bool TryRead(string shortcutPath, out string targetPath) =>
        TryRead(shortcutPath, out targetPath, out _) || !string.IsNullOrEmpty(targetPath);

    internal static bool TryRead(string shortcutPath, out string targetPath, out string arguments)
    {
        targetPath = string.Empty;
        arguments = string.Empty;
        NativeMethods.IShellLinkW? link = null;
        try
        {
            link = (NativeMethods.IShellLinkW)new NativeMethods.ShellLink();
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(shortcutPath, 0);
            var value = new StringBuilder(32768);
            if (link.GetPath(value, value.Capacity, IntPtr.Zero, 0) < 0 || value.Length == 0) return false;
            targetPath = value.ToString();
            value.Clear();
            if (link.GetArguments(value, value.Capacity) < 0) return false;
            arguments = value.ToString();
            return true;
        }
        catch { return false; }
        finally { if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link); }
    }
}
