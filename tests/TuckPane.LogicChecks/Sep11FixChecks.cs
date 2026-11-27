using System.Xml.Linq;
using TuckPane.Core;
using TuckPane.Services;

internal static class Sep11FixChecks
{
    internal static void Run(string area)
    {
        if (area is not ("resize" or "menu" or "title" or "all"))
            throw new ArgumentException($"Unknown Sep 11 check: {area}.");
        if (area is "resize" or "all") CheckResize();
        if (area is "menu" or "all") CheckMenu();
        if (area is "title" or "all") CheckTitle();
        Console.WriteLine($"PASS --sep11-fixes {area}: targeted production logic / local XAML only. No UI, input automation, real registry or clipboard access.");
    }

    private static void CheckResize()
    {
        (double X, double Y, CanvasResizeEdge Expected)[] cases =
        [
            (5.999, 100, CanvasResizeEdge.Left),
            (6, 100, CanvasResizeEdge.None),
            (20, 100, CanvasResizeEdge.None),
            (3, 20, CanvasResizeEdge.Left | CanvasResizeEdge.Top),
            (7, 20, CanvasResizeEdge.None),
            (3, 280, CanvasResizeEdge.Left | CanvasResizeEdge.Bottom),
            (7, 280, CanvasResizeEdge.None),
            (200, 0, CanvasResizeEdge.Top),
            (200, 294, CanvasResizeEdge.Bottom),
            (397, 20, CanvasResizeEdge.Right | CanvasResizeEdge.Top),
            (397, 280, CanvasResizeEdge.Right | CanvasResizeEdge.Bottom),
            (28, 3, CanvasResizeEdge.Left | CanvasResizeEdge.Top),
            (29, 3, CanvasResizeEdge.Top),
            (200, -1, CanvasResizeEdge.None),
            (200, 300, CanvasResizeEdge.None),
            (400, 100, CanvasResizeEdge.None)
        ];
        foreach (bool compactList in new[] { false, true })
        foreach (var (x, y, expected) in cases)
            Require(CanvasResizeHitZones.HitTest(x, y, 400, 300, compactList) == expected,
                $"Mode compact={compactList}: hit at ({x}, {y}) must be {expected}.");
        foreach (double scale in new[] { 1, 1.25, 1.5 })
        {
            var rectangles = CanvasResizeHitZones.EdgeRectangles(600, 450, scale, compactList: true);
            int band = (int)Math.Ceiling(6 * scale);
            Require(rectangles.Length == 4 && rectangles[2] == (0, band, band, 450 - 2 * band) &&
                rectangles[0] == (0, 0, 600, band) &&
                rectangles[1] == (0, 450 - band, 600, band) &&
                rectangles[3] == (600 - band, band, band, 450 - 2 * band),
                $"Native interceptors must use the same 6 DIP band at scale {scale}.");
            Require(rectangles.SequenceEqual(CanvasResizeHitZones.EdgeRectangles(600, 450, scale, compactList: false)),
                "Both content modes must have identical native hot zones.");

            // An offset client frame stands in for the arranged panel below the title.
            var frame = new CanvasResizeFrame(11, (int)Math.Round(56 * scale), 600, 450, scale);
            var native = CanvasResizeHitZones.EdgeRectangles(frame);
            Require(CanvasResizeHitZones.HitTest(frame, frame.X + 300, frame.Y - 1) == CanvasResizeEdge.None &&
                CanvasResizeHitZones.HitTest(frame, frame.X + 300, frame.Y) == CanvasResizeEdge.Top &&
                CanvasResizeHitZones.HitTest(frame, frame.X + 300, frame.Y + 449) == CanvasResizeEdge.Bottom,
                "The visible panel top/bottom must resize, regardless of title offset or covering items.");
            int corner = (int)Math.Floor(28 * scale);
            int[] xs = [-1, 0, band - 1, band, corner, corner + 1, 300, 600 - band - 1, 600 - band, 599, 600];
            int[] ys = [-1, 0, band - 1, band, 225, 450 - band - 1, 450 - band, 449, 450];
            foreach (int x in xs)
            foreach (int y in ys)
            {
                int cx = frame.X + x, cy = frame.Y + y;
                bool intercepted = native.Any(rect => cx >= rect.X && cx < rect.X + rect.Width &&
                    cy >= rect.Y && cy < rect.Y + rect.Height);
                Require(intercepted == (CanvasResizeHitZones.HitTest(frame, cx, cy) != CanvasResizeEdge.None),
                    $"Native/logical coverage mismatch at DPI {scale}, client ({cx},{cy}).");
            }
        }
        Require(CanvasResizeHitZones.HitTest(default, 0, 0) == CanvasResizeEdge.None &&
            CanvasResizeHitZones.EdgeRectangles(default).Length == 0, "Unavailable layout must have no input region.");

        // This is a source integration guard, not a claim of real Windows input validation.
        string main = File.ReadAllText("src/TuckPane/MainWindow.xaml.cs");
        string hover = File.ReadAllText("src/TuckPane/MainWindow.HoverPresentation.cs");
        string input = File.ReadAllText("src/TuckPane/MainWindow.CanvasResizeInput.cs");
        static string Body(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException($"Missing input entry: {signature}");
            int end = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
            return source[start..(end < 0 ? source.Length : end)];
        }
        string itemPress = Body(main, "private void BeginItemPointerPress(");
        string proxyPress = Body(hover, "private void ForwardHoverPress(");
        Require(itemPress.IndexOf("TryHandleCanvasResizePress", StringComparison.Ordinal) is >= 0 and var itemResize &&
            itemResize < itemPress.IndexOf("BeginCompactClick", StringComparison.Ordinal) &&
            proxyPress.IndexOf("TryHandleCanvasResizePress", StringComparison.Ordinal) is >= 0 and var proxyResize &&
            proxyResize < proxyPress.IndexOf("HoverItemAtViewportPoint", StringComparison.Ordinal) &&
            Body(main, "private void ExpandedView_PointerPressed(").Contains("TryHandleCanvasResizePress(e)"),
            "Resize must precede normal item clicks and hover proxy hit/press forwarding.");
        string hit = Body(main, "private CanvasResizeEdge GetCanvasResizeEdge(NativeMethods.POINT cursor)");
        Require(!hit.Contains("HoverItemAtViewportPoint") && hit.Contains("TryGetCanvasResizeFrame") &&
            hit.Contains("CanvasResizeHitZones.HitTest(frame") &&
            Body(main, "private void UpdateCanvasResizeEdgeWindows(").Contains("CanvasResizeHitZones.EdgeRectangles(frame)") &&
            input.Contains("TryGetElementScreenBounds(ExpandedPanel") && input.Contains("ClientToScreen"),
            "Items must not veto edge hits; native and logical routes must share the actual panel frame.");
        string begin = Body(main, "private bool TryBeginCanvasResize(");
        Require(begin.Contains("_shellDropFinalizing") && begin.Contains("_itemReorderProbeRunning") &&
            input.Contains("args.Handled = true"), "Priority must preserve busy guards and consume a resize press.");
    }

    private static void CheckMenu()
    {
        const string executable = @"C:\Apps\TuckPane\TuckPane.exe";
        const string foreign = @"D:\Portable\TuckPane.exe";
        const string label = "新建空白收纳窗";
        string verb = FolderContextMenuService.DesktopVerbKey;
        string preference = FolderContextMenuService.DesktopPreferenceKey;
        var store = new MemoryStore();
        int notifications = 0;
        var service = new FolderContextMenuService(store, executable, _ => true, () => notifications++, desktop: true);
        store.Write(preference, new Dictionary<string, object> { ["Enabled"] = 1, ["OwnerExecutable"] = executable });
        service.InitializeDesktop(label, installedCopy: true);
        Require(service.ReadState().Status == FolderContextMenuStatus.Enabled &&
            (string)store.Read(verb + @"\command")![""] == $"\"{executable}\" --create-organizer" &&
            (string)store.Read(verb)![""] == label && notifications == 1,
            "Startup must recreate the entirely missing owned desktop verb and notify Shell.");
        Require(!store.Read(verb)!.ContainsKey("MultiSelectModel"),
            "Desktop background verbs must not declare a file-selection model because Shell can suppress the zero-selection command.");

        store.Write(verb, new Dictionary<string, object>
        {
            [""] = label,
            ["Icon"] = $"\"{executable}\",0",
            ["MultiSelectModel"] = "Single",
            ["OwnerExecutable"] = executable
        });
        notifications = 0;
        service.InitializeDesktop(label, installedCopy: true);
        Require(!store.Read(verb)!.ContainsKey("MultiSelectModel") && notifications == 1,
            "Startup must repair an owned legacy desktop verb by removing its file-selection model and notifying Shell.");
        string installer = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "installer", "TuckPane.iss"));
        Require(installer.Contains("RegDeleteValue(HKCU, DesktopMenuKey, 'MultiSelectModel');", StringComparison.Ordinal),
            "An owned upgrade install must remove the legacy desktop file-selection model before the user launches the app.");

        store.Delete(verb);
        store.Write(preference, new Dictionary<string, object> { ["Enabled"] = 0, ["OwnerExecutable"] = executable });
        notifications = 0;
        service.InitializeDesktop(label, installedCopy: true);
        Require(store.Read(verb) is null && (int)store.Read(preference)!["Enabled"] == 0 && notifications == 0,
            "Startup must preserve an explicit disabled preference.");

        store.Write(preference, new Dictionary<string, object> { ["Enabled"] = 1, ["OwnerExecutable"] = foreign });
        service.InitializeDesktop(label, installedCopy: true);
        Require(store.Read(verb) is null && (string)store.Read(preference)!["OwnerExecutable"] == foreign && notifications == 0,
            "Startup must not repair another copy's missing menu.");

        new FolderContextMenuService(store, foreign, _ => true, () => { }, desktop: true).Enable("Foreign label");
        // Stale preference for this copy cannot override another copy's surviving command/ownership.
        store.Write(preference, new Dictionary<string, object> { ["Enabled"] = 1, ["OwnerExecutable"] = executable });
        service.InitializeDesktop(label, installedCopy: true);
        Require((string)store.Read(verb + @"\command")![""] == $"\"{foreign}\" --create-organizer" &&
            (string)store.Read(verb)![""] == "Foreign label" && notifications == 0,
            "Startup must not steal a surviving foreign desktop registration.");
    }

    private static void CheckTitle()
    {
        var document = XDocument.Load(Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement editor = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "ExpandedNameEditor");
        XElement title = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "ExpandedNameText");
        foreach (string attribute in new[] { "Margin", "HorizontalAlignment", "VerticalAlignment", "TextAlignment", "FontFamily", "FontWeight" })
            Require((string?)editor.Attribute(attribute) == (string?)title.Attribute(attribute),
                $"Title editor and display must share {attribute}.");
        Require((string?)editor.Attribute("Background") == "Transparent" &&
            (string?)editor.Attribute("BorderThickness") == "0" &&
            (string?)editor.Attribute("Padding") == "0" &&
            (string?)editor.Attribute("TextAlignment") == "Center",
            "Title editing must be transparent, borderless and centered without padding.");
        XElement template = editor.Descendants().Single(e => e.Name.LocalName == "ControlTemplate");
        XElement content = template.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "ContentElement");
        Require(template.Descendants().All(e => e.Name.LocalName == "ScrollViewer") &&
            content.Name.LocalName == "ScrollViewer" &&
            (string?)content.Attribute("Background") == "Transparent" &&
            (string?)content.Attribute("BorderThickness") == "0" &&
            (string?)content.Attribute("Padding") == "{TemplateBinding Padding}" &&
            (string?)content.Attribute("HorizontalAlignment") == "Stretch" &&
            (string?)content.Attribute("VerticalAlignment") == "Center" &&
            (string?)content.Attribute("HorizontalScrollBarVisibility") == "Hidden" &&
            (string?)content.Attribute("HorizontalScrollMode") == "Enabled",
            "The local editor template must contain only the scrolling text host, with no background state, underline or delete-button slot.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class MemoryStore : IFolderContextMenuStore
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, object>> _values = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, object>? Read(string key) => _values.GetValueOrDefault(key);
        public void Write(string key, IReadOnlyDictionary<string, object> values) =>
            _values[key] = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase);
        public void Delete(string key)
        {
            foreach (string candidate in _values.Keys.Where(candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToArray())
                _values.Remove(candidate);
        }
    }
}
