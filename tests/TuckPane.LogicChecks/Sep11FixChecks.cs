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
            (6, 100, CanvasResizeEdge.Left),
            (6.001, 100, CanvasResizeEdge.None),
            (20, 100, CanvasResizeEdge.None),
            (6, 20, CanvasResizeEdge.Left | CanvasResizeEdge.Top),
            (7, 20, CanvasResizeEdge.Top),
            (6, 280, CanvasResizeEdge.Left | CanvasResizeEdge.Bottom),
            (7, 280, CanvasResizeEdge.Bottom)
        ];
        foreach (var (x, y, expected) in cases)
            Require(CanvasResizeHitZones.HitTest(x, y, 400, 300, compactList: true) == expected,
                $"Compact-list hit at ({x}, {y}) must be {expected}.");
        Require(CanvasResizeHitZones.HitTest(28, 100, 400, 300, compactList: false) == CanvasResizeEdge.Left,
            "Icon mode must retain its 28 DIP left zone.");
        foreach (double scale in new[] { 1, 1.25, 1.5 })
        {
            var rectangles = CanvasResizeHitZones.EdgeRectangles(600, 450, scale, compactList: true);
            int left = (int)Math.Ceiling(6 * scale), other = (int)Math.Ceiling(28 * scale);
            Require(rectangles.Length == 4 && rectangles[2] == (0, other, left, 450 - 2 * other),
                $"The native left interceptor must release the gutter at scale {scale}.");
            Require(rectangles[0] == (0, 0, 600, other) &&
                rectangles[1] == (0, 450 - other, 600, other) &&
                rectangles[3] == (600 - other, other, other, 450 - 2 * other),
                $"The other native edges must retain 28 DIP at scale {scale}.");
        }
        Require(CanvasResizeHitZones.EdgeRectangles(600, 450, 1, compactList: false)[2].Width == 28,
            "Icon mode's native left interceptor must retain 28 DIP.");
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
