using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class Sep09Checks
{
    internal static async Task RunAsync(string area)
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-sep09-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            switch (area)
            {
                case "grid":
                    CheckGrid();
                    Console.WriteLine("PASS --sep09-fixes grid: 3 geometry cases including item 10, rectangular layout and fractional 125% DPI; margin wiring is static only. No UI or input automation.");
                    break;
                case "shortcut":
                    await RunStaAsync(() => CheckShortcut(root));
                    Console.WriteLine("PASS --sep09-fixes shortcut: native link target, source preservation, numbered conflicts, exact existing-link bytes/metadata and write-failure cleanup in isolated directories. No picker or application launch.");
                    break;
                case "lifecycle":
                    CheckLifecycle(root);
                    Console.WriteLine("PASS --sep09-fixes lifecycle: pure suspend/resume/session message classification and immediately readable isolated lifecycle log without Flush. No real power, session or exit action.");
                    break;
                default:
                    throw new ArgumentException($"Unknown Sep 09 check: {area}.");
            }
        }
        finally
        {
            // Failed transfers log asynchronously: drain before deleting this run's tree.
            await AppLogger.FlushAsync();
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            string fullRoot = Path.GetFullPath(root);
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(fullRoot), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).StartsWith("TuckPane-sep09-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the isolated test directory.");
            Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static void CheckLifecycle(string root)
    {
        Require(LifecycleDiagnostics.DescribeWindowMessage(0x0218, 4, 0) == "power-suspend" &&
                LifecycleDiagnostics.DescribeWindowMessage(0x0218, 18, 0) == "power-resume-automatic" &&
                LifecycleDiagnostics.DescribeWindowMessage(0x0218, 7, 0) == "power-resume-suspend",
            "The three ordinary suspend/resume messages must remain distinguishable in diagnostics.");
        Require(LifecycleDiagnostics.DescribeWindowMessage(0x0016, 1, unchecked((int)0x80000000)) ==
                    "session-end confirmed=True flags=0x80000000" &&
                LifecycleDiagnostics.DescribeWindowMessage(0x0016, 0, 0) ==
                    "session-end confirmed=False flags=0x00000000",
            "Diagnostics must distinguish confirmed logoff from a cancelled session ending.");
        Require(LifecycleDiagnostics.DescribeWindowMessage(0x0200, 0, 0) is null &&
                LifecycleDiagnostics.DescribeWindowMessage(0x0218, 10, 0) is null,
            "Unrelated pointer/power-status messages must not generate lifecycle records.");

        string isolatedPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Require(Path.GetFullPath(AppPaths.LogPath).StartsWith(isolatedPrefix, StringComparison.OrdinalIgnoreCase),
            "Lifecycle logging must remain inside this invocation's isolated test root.");
        string marker = Guid.NewGuid().ToString("N");
        AppLogger.Lifecycle("diagnostic-check", $"marker={marker}");
        string[] immediateLines = File.ReadAllLines(AppPaths.LogPath); // Deliberately before FlushAsync.
        Require(immediateLines.Count(line => line.Contains(
                    $"[LIFECYCLE] pid={Environment.ProcessId} event=diagnostic-check marker={marker}",
                    StringComparison.Ordinal)) == 1,
            "The lifecycle record must be completely readable when Lifecycle returns, without queue draining.");
    }

    private static void CheckGrid()
    {
        (int Rows, int Columns, double WidthPx, double HeightPx, double Scale)[] cases =
        [
            (3, 3, 360, 300, 1),
            (2, 4, 417, 229, 1.25),
            (6, 2, 241, 597, 1.25)
        ];
        foreach (var sample in cases)
        {
            double width = sample.WidthPx / sample.Scale;
            double height = sample.HeightPx / sample.Scale;
            var layout = new OrganizerLayout { Rows = sample.Rows, Columns = sample.Columns };
            (double cellWidth, double cellHeight) = DisplayPlacementService.CalculateItemCellSizeDip(width, height, layout);
            double gap = DisplayPlacementService.ItemGapDip;
            Require(sample.Columns * cellWidth + (sample.Columns - 1) * gap <= width + 1e-6 &&
                    sample.Rows * cellHeight + (sample.Rows - 1) * gap <= height + 1e-6,
                $"The configured {sample.Rows}x{sample.Columns} cells do not fit completely inside the content viewport.");
            int nextItemIndex = sample.Rows * sample.Columns;
            double nextRowTop = nextItemIndex / sample.Columns * (cellHeight + gap);
            Require(nextRowTop >= height - 1e-6,
                $"Item {nextItemIndex + 1} enters the first {sample.Rows} rows' viewport.");
        }

        // This checks adapter wiring, not rendered WinUI clipping or input behavior.
        string source = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", "MainWindow.xaml.cs"));
        int start = source.IndexOf("private void ApplyExpandedContentInset()", StringComparison.Ordinal);
        Require(start >= 0, "The expanded inset adapter was not found.");
        int end = source.IndexOf("private Size GetItemsViewportSize()", start, StringComparison.Ordinal);
        Require(end > start, "The viewport adapter was not found after the inset adapter.");
        string adapter = source[start..end];
        Require(adapter.Contains("ItemsScrollView.Margin = IsCompactList ? new Thickness(0) : inset;", StringComparison.Ordinal) &&
                adapter.Contains("ItemsRepeater.Margin = IsCompactList ? inset : new Thickness(0);", StringComparison.Ordinal),
            "Static wiring: icon insets must be outside the scroller while compact-list insets stay on the repeater.");
    }

    private static void CheckShortcut(string root)
    {
        string input = Directory.CreateDirectory(Path.Combine(root, "输入 文件")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "收纳窗")).FullName;
        var storage = new StorageService(destination, createIfMissing: false);
        string source = Path.Combine(input, "资料 文本.txt");
        byte[] sourceBytes = Encoding.UTF8.GetBytes("Source stays in place. 原文件保持不变。\n");
        File.WriteAllBytes(source, sourceBytes);

        TransferOutcome first = storage.AddFileShortcut(source);
        string firstPath = CreatedPath(first);
        byte[] firstBytes = File.ReadAllBytes(firstPath);
        LinkDetails firstLink = ReadLink(firstPath);
        Require(SamePath(firstLink.Target, source) && SamePath(firstLink.WorkingDirectory, input) &&
                File.ReadAllBytes(source).SequenceEqual(sourceBytes),
            "The new native shortcut must target the original file and preserve the source bytes.");

        string secondPath = CreatedPath(storage.AddFileShortcut(source));
        Require(!SamePath(firstPath, secondPath) && File.ReadAllBytes(firstPath).SequenceEqual(firstBytes) &&
                SamePath(ReadLink(secondPath).Target, source),
            "A repeated name must produce a different shortcut without replacing the existing one.");

        string icon = Path.Combine(input, "专用图标.ico");
        File.Copy(Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", "Assets", "TuckPane.ico"), icon);
        string existingLink = Path.Combine(input, "已有快捷方式.lnk");
        const string arguments = "--message \"保留 spaces\" --flag";
        CreateLink(existingLink, source, input, arguments, icon, 0);
        byte[] existingBytes = File.ReadAllBytes(existingLink);
        string copiedPath = CreatedPath(storage.AddFileShortcut(existingLink));
        LinkDetails copied = ReadLink(copiedPath);
        Require(File.ReadAllBytes(copiedPath).SequenceEqual(existingBytes) &&
                File.ReadAllBytes(existingLink).SequenceEqual(existingBytes) &&
                copied.Arguments == arguments && SamePath(copied.Icon, icon) && copied.IconIndex == 0,
            "An existing .lnk must be copied byte-for-byte, preserving arguments and icon metadata.");

        string[] beforeFailure = Directory.GetFiles(destination).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        using (var lockedSource = new FileStream(existingLink, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Require(storage.AddFileShortcut(existingLink).Status == TransferStatus.Failed,
                "An exclusively locked .lnk must fail during the staging copy.");
        }
        Require(Directory.GetFiles(destination).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(beforeFailure) &&
                File.ReadAllBytes(existingLink).SequenceEqual(existingBytes),
            "A failed staging copy must leave no temporary/destination file and preserve the source.");
        Require(storage.AddFileShortcut(Path.Combine(input, "missing.txt")).Status == TransferStatus.Failed,
            "A missing source must report failure.");
        string missingDirectory = Path.Combine(root, "未创建的目录");
        Require(new StorageService(missingDirectory, createIfMissing: false).AddFileShortcut(source).Status == TransferStatus.Failed &&
                !Directory.Exists(missingDirectory),
            "Adding a shortcut must not recreate a missing bound directory.");
    }

    private static string CreatedPath(TransferOutcome outcome)
    {
        Require(outcome.Status == TransferStatus.ShortcutCreated && outcome.DestinationPath is not null &&
                File.Exists(outcome.DestinationPath), $"Shortcut creation failed: {outcome}");
        return outcome.DestinationPath!;
    }

    private sealed record LinkDetails(string Target, string WorkingDirectory, string Arguments, string Icon, int IconIndex);

    private static LinkDetails ReadLink(string path)
    {
        object instance = new NativeMethods.ShellLink();
        try
        {
            ((IPersistFile)instance).Load(path, 0);
            var link = (NativeMethods.IShellLinkW)instance;
            var target = new StringBuilder(32768);
            var directory = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var icon = new StringBuilder(32768);
            Marshal.ThrowExceptionForHR(link.GetPath(target, target.Capacity, IntPtr.Zero, 0));
            Marshal.ThrowExceptionForHR(link.GetWorkingDirectory(directory, directory.Capacity));
            Marshal.ThrowExceptionForHR(link.GetArguments(arguments, arguments.Capacity));
            Marshal.ThrowExceptionForHR(link.GetIconLocation(icon, icon.Capacity, out int index));
            return new(target.ToString(), directory.ToString(), arguments.ToString(), icon.ToString(), index);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    private static void CreateLink(string path, string target, string directory, string arguments, string icon, int index)
    {
        object instance = new NativeMethods.ShellLink();
        try
        {
            var link = (NativeMethods.IShellLinkW)instance;
            Marshal.ThrowExceptionForHR(link.SetPath(target));
            Marshal.ThrowExceptionForHR(link.SetWorkingDirectory(directory));
            Marshal.ThrowExceptionForHR(link.SetArguments(arguments));
            Marshal.ThrowExceptionForHR(link.SetIconLocation(icon, index));
            ((IPersistFile)instance).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    private static Task RunStaAsync(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception ex) { completed.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
