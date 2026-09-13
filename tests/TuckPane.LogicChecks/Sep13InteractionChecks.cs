using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Xml.Linq;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class Sep13InteractionChecks
{
    internal static async Task RunAsync(string area)
    {
        switch (area)
        {
            case "resize": CheckResize(); break;
            case "menu": CheckMenu(); break;
            case "shortcut": await OnStaAsync(CheckShortcut); break;
            default: throw new ArgumentException($"Unknown September 13 interaction scope: {area}.");
        }
        Console.WriteLine($"PASS September 13 {area}: focused non-GUI checks; no input automation or clipboard writes.");
    }

    private static void CheckResize()
    {
        var start = Rect(200, 200, 600, 400);
        var work = Rect(0, 0, 1000, 800);
        var sides = new[]
        {
            (CanvasResizeEdge.Left, -100d, 90d, Rect(100, 200, 600, 400)),
            (CanvasResizeEdge.Right, 100d, 90d, Rect(200, 200, 700, 400)),
            (CanvasResizeEdge.Top, 90d, -50d, Rect(200, 150, 600, 400)),
            (CanvasResizeEdge.Bottom, 90d, 50d, Rect(200, 200, 600, 450))
        };
        foreach (var (edge, dx, dy, expected) in sides)
        {
            var result = Resize(start, work, edge, dx, dy);
            Require(Same(result.Bounds, expected) && result.Scale == 1,
                $"{edge}: dragging one edge must ignore cross-axis movement and preserve content scale and opposite edge.");
            var minimum = Resize(start, work, edge, -dx * 100, -dy * 100);
            bool horizontal = edge is CanvasResizeEdge.Left or CanvasResizeEdge.Right;
            Require(horizontal ? minimum.Bounds.Width == 200 && minimum.Bounds.Height == 200
                : minimum.Bounds.Width == 400 && minimum.Bounds.Height == 100,
                $"{edge}: minimum must restrict only the dragged axis.");
            Anchored(start, minimum.Bounds, edge);
            var maximum = Resize(start, work, edge, dx * 100, dy * 100);
            Require(Inside(maximum.Bounds, work) && (edge switch
            {
                CanvasResizeEdge.Left => maximum.Bounds.Left == work.Left,
                CanvasResizeEdge.Right => maximum.Bounds.Right == work.Right,
                CanvasResizeEdge.Top => maximum.Bounds.Top == work.Top,
                _ => maximum.Bounds.Bottom == work.Bottom
            }), $"{edge}: the moving edge must stop at the work area.");
            Anchored(start, maximum.Bounds, edge);
        }

        foreach (var edge in new[] { CanvasResizeEdge.Left | CanvasResizeEdge.Top,
            CanvasResizeEdge.Left | CanvasResizeEdge.Bottom, CanvasResizeEdge.Right | CanvasResizeEdge.Top,
            CanvasResizeEdge.Right | CanvasResizeEdge.Bottom })
        {
            double vx = edge.HasFlag(CanvasResizeEdge.Left) ? -400 : 400;
            double vy = edge.HasFlag(CanvasResizeEdge.Top) ? -200 : 200;
            foreach (double factor in new[] { .5, 1.25, 10d })
            {
                var result = Resize(start, work, edge, vx * (factor - 1), vy * (factor - 1));
                Anchored(start, result.Bounds, edge);
                Require(Inside(result.Bounds, work) && result.Bounds.Width >= 200 && result.Bounds.Height >= 100 &&
                    Math.Abs(result.Bounds.Width - 2 * result.Bounds.Height) <= 1,
                    $"{edge}: corner scaling must preserve aspect, minimum and work-area limits.");
                if (factor <= 1.25) Require(Math.Abs(result.Scale - factor) < .000001,
                    $"{edge}: an unrestricted diagonal must retain its requested content factor.");
            }

            var raw = Resize(start, work, edge, vx * .245, vy * .245);
            int coordinate = edge.HasFlag(CanvasResizeEdge.Left) ? 100 : 700;
            var target = new WindowAlignmentTarget(Guid.NewGuid(), Rect(coordinate, 50, coordinate + 70, 70));
            var snapped = WindowAlignmentMath.AlignCompactCorner(start, raw, work, [target], edge, 12, 20, default);
            Anchored(start, snapped.Alignment.Bounds, edge);
            Require(Math.Abs(snapped.Scale - 1.25) < .000001 && snapped.Alignment.XGuide is not null &&
                snapped.Alignment.Bounds.Width == 500 && snapped.Alignment.Bounds.Height == 250,
                $"{edge}: snapping the moving edge must scale both axes without translating the fixed corner.");
        }

        // A legacy panel already smaller than today's minimum can retain its initial size.
        var small = Rect(200, 200, 350, 280);
        Require(Same(Resize(small, work, CanvasResizeEdge.Left, 100, 0).Bounds, small),
            "Resizing a legacy small panel must not jump to a larger minimum.");
    }

    private static void CheckMenu()
    {
        foreach (OrganizerPlacementMode mode in Enum.GetValues<OrganizerPlacementMode>())
        {
            var settings = new GlobalSettings();
            Require(OrganizerMenuVisibility.VisibleActions(settings, mode).Contains("ContextAddItem"),
                $"{mode}: Add item must be visible by default.");
            settings.OrganizerMenuVisibility["ContextAddItem"] = false;
            Require(!OrganizerMenuVisibility.VisibleActions(settings, mode).Contains("ContextAddItem"),
                $"{mode}: explicit hiding must override the default.");
        }
        XDocument xaml = XDocument.Parse(Source("src/TuckPane/MainWindow.xaml"));
        XNamespace name = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string viewName in new[] { "CompactView", "ExpandedView" })
        {
            XElement view = xaml.Descendants().Single(element => (string?)element.Attribute(name + "Name") == viewName);
            Require((string?)view.Attribute("ContextRequested") == "OrganizerWindow_ContextRequested",
                $"{viewName}: empty area/title must route the window context request.");
            XElement add = view.Descendants().Single(element => element.Name.LocalName == "MenuFlyoutSubItem" &&
                (string?)element.Attribute("Tag") == "ContextAddItem");
            Require(add.Elements().Count() == 2 && add.Elements().Any(element =>
                (string?)element.Attribute("Tag") == "ContextAddFile" && (string?)element.Attribute("Click") == "AddFileMenuItem_Click") &&
                add.Elements().Any(element => (string?)element.Attribute("Tag") == "ContextAddFolder" &&
                    (string?)element.Attribute("Click") == "AddFolderMenuItem_Click"),
                $"{viewName}: Add item must contain exactly the file and folder actions.");
        }
        string menu = Source("src/TuckPane/MainWindow.OrganizerMenu.cs");
        Require(menu.Contains("menu.ShouldConstrainToRootBounds = false") &&
            menu.Contains("Item_ContextRequested(host, e)") && menu.Contains("finally") &&
            menu.Contains("_overlayOpenCount = Math.Max(0, _overlayOpenCount - 1)") &&
            menu.Contains("_addingItem = false"),
            "Window menus must allow side-station popups, preserve Dock item routing and release picker protection.");
    }

    private static void CheckShortcut()
    {
        string temp = Path.GetFullPath(Path.GetTempPath());
        string root = Path.GetFullPath(Path.Combine(temp, "TuckPane-sep13-" + Guid.NewGuid().ToString("N")));
        try
        {
            string source = Path.Combine(root, "notes.v2");
            Directory.CreateDirectory(source);
            string sentinel = Path.Combine(source, "keep.txt");
            File.WriteAllText(sentinel, "source stays in place");
            var storage = new StorageService(Path.Combine(root, "items"));
            Require(storage.AddItemShortcut(source + Path.DirectorySeparatorChar).Status == TransferStatus.ShortcutCreated,
                "A real directory with a trailing separator must create a shortcut.");
            string first = Path.Combine(storage.ItemsRoot, "notes.v2.lnk");
            Require(File.Exists(first) && SamePath(ReadTarget(first), source),
                "Folder shortcut must retain the dotted name and point to the original directory.");
            byte[] originalLink = File.ReadAllBytes(first);
            Require(storage.AddItemShortcut(source).Status == TransferStatus.ShortcutCreated,
                "Adding the same folder again must choose another name.");
            string second = Path.Combine(storage.ItemsRoot, "notes.v2 2.lnk");
            Require(File.Exists(second) && SamePath(ReadTarget(second), source) &&
                File.ReadAllBytes(first).SequenceEqual(originalLink) && File.ReadAllText(sentinel) == "source stays in place",
                "Name avoidance must preserve the first shortcut and the untouched source directory.");
            string[] before = Directory.GetFileSystemEntries(storage.ItemsRoot).Order().ToArray();
            Require(storage.AddItemShortcut(Path.Combine(root, "missing")).Status == TransferStatus.Failed &&
                Directory.GetFileSystemEntries(storage.ItemsRoot).Order().SequenceEqual(before),
                "A missing source must fail without publishing a link or leaving staging files.");
            string absent = Path.Combine(root, "absent-items");
            Require(new StorageService(absent, createIfMissing: false).AddItemShortcut(source).Status == TransferStatus.Failed &&
                !Directory.Exists(absent) && !Directory.EnumerateFiles(root, ".glassfolder-staging-*", SearchOption.AllDirectories).Any(),
                "A missing destination must fail without recreating it or leaving staging files.");
        }
        finally
        {
            Require(root.StartsWith(Path.TrimEndingDirectorySeparator(temp) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("TuckPane-sep13-", StringComparison.Ordinal),
                "Temporary cleanup must stay within this check's unique directory.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string ReadTarget(string shortcut)
    {
        object instance = new NativeMethods.ShellLink();
        try
        {
            ((IPersistFile)instance).Load(shortcut, 0);
            var target = new StringBuilder(32768);
            Marshal.ThrowExceptionForHR(((NativeMethods.IShellLinkW)instance).GetPath(target, target.Capacity, IntPtr.Zero, 0));
            return target.ToString();
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    private static Task OnStaAsync(Action check)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { check(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "TuckPane shortcut check STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static CompactResizeResult Resize(NativeMethods.RECT start, NativeMethods.RECT work,
        CanvasResizeEdge edge, double dx, double dy) => CompactResizeMath.Resize(start, work, edge, dx, dy, 200, 100, 1, 1);
    private static NativeMethods.RECT Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };
    private static bool Same(NativeMethods.RECT a, NativeMethods.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    private static bool Inside(NativeMethods.RECT bounds, NativeMethods.RECT work) =>
        bounds.Left >= work.Left && bounds.Top >= work.Top && bounds.Right <= work.Right && bounds.Bottom <= work.Bottom;
    private static bool SamePath(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static void Anchored(NativeMethods.RECT start, NativeMethods.RECT resized, CanvasResizeEdge edge)
    {
        Require(edge.HasFlag(CanvasResizeEdge.Left) ? resized.Right == start.Right : resized.Left == start.Left,
            $"{edge}: opposite horizontal anchor moved.");
        Require(edge.HasFlag(CanvasResizeEdge.Top) ? resized.Bottom == start.Bottom : resized.Top == start.Top,
            $"{edge}: opposite vertical anchor moved.");
    }
    private static string Source(string relative)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException($"Cannot locate source for wiring check: {relative}");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
