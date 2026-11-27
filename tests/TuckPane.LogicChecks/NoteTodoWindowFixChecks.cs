using System.Text.RegularExpressions;
using System.Xml.Linq;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class NoteTodoWindowFixChecks
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static string Source(string file) => Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", file);

    internal static async Task RunAsync()
    {
        CheckMinimizeWiring();
        Console.WriteLine("PASS note-todo-window-fixes: both minimize buttons, order, handlers and accessible labels (static).");
        await CheckPlacementAsync();
        Console.WriteLine("PASS note-todo-window-fixes: minimized/invalid placement preserves latest tasks; restored geometry and todo defaults persist; store remains strict.");
        CheckRowHeight();
        Console.WriteLine("PASS note-todo-window-fixes: shared row/composer height at small/default/large fonts and separator states. No GUI; visual acceptance pending.");
    }

    private static void CheckMinimizeWiring()
    {
        foreach (string window in new[] { "NoteWindow", "TodoWindow" })
        {
            XDocument xaml = XDocument.Load(Source(window + ".xaml"));
            XElement minimize = Named(xaml, "MinimizeButton"), close = Named(xaml, "CloseButton");
            Require(minimize.Parent == close.Parent && minimize.ElementsAfterSelf().FirstOrDefault() == close &&
                (string?)minimize.Attribute("Click") == "MinimizeButton_Click",
                window + ": minimize must precede close and have its click handler.");
            string code = File.ReadAllText(Source(window + ".xaml.cs"));
            Require(code.Contains("AutomationProperties.SetName(MinimizeButton, AppStrings.Get(\"WindowMinimize\"))") &&
                code.Contains("ToolTipService.SetToolTip(MinimizeButton, AppStrings.Get(\"WindowMinimize\"))") &&
                Regex.IsMatch(code, @"void MinimizeButton_Click\([^)]*\)\s*\{[^}]*\.Minimize\(", RegexOptions.Singleline),
                window + ": minimize needs localized name, tooltip and presenter invocation.");
        }
    }

    private static async Task CheckPlacementAsync()
    {
        string code = File.ReadAllText(Source("TodoWindow.xaml.cs"));
        string changed = code[code.IndexOf("private void AppWindow_Changed", StringComparison.Ordinal)..];
        int guard = changed.IndexOf("State: OverlappedPresenterState.Restored", StringComparison.Ordinal);
        Require(guard >= 0 && guard < changed.IndexOf("if (args.DidSizeChange)", StringComparison.Ordinal) &&
            code.Contains("_document.Placement = _placementRecovery.CaptureForSave(_document.Placement);"),
            "The non-restored guard must precede geometry correction and the save boundary must recover placement.");
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-window-fixes-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            var store = new NoteStore(Path.Combine(root, "legacy"));
            var original = new PortableNotePlacement { MonitorDevice = "test-display", XDip = -20, YDip = 60,
                WidthDip = 442.4, HeightDip = 480.8 };
            var recovery = new TodoPlacementRecovery(original);
            var document = new PortableTodoDocument { Placement = original, Tasks = [new() { Text = "initial" }] };
            string path = await store.CreateTodoAsync(root, "placement", document);
            Require(recovery.CaptureWindowChange(new() { WidthDip = 600, HeightDip = 500 }, isRestored: false) is null,
                "A minimized event replaced the valid snapshot even though its rectangle looked legal.");
            foreach (PortableNotePlacement invalid in new PortableNotePlacement[]
            {
                new() { HeightDip = 0 }, new() { HeightDip = 339.9 }, new() { HeightDip = 1200.1 },
                new() { WidthDip = 279.9, HeightDip = 480 }, new() { WidthDip = 1600.1, HeightDip = 480 },
                new() { XDip = double.NaN, HeightDip = 480 }
            })
            {
                Require(recovery.CaptureWindowChange(invalid, isRestored: true) is null, "Invalid event replaced valid geometry.");
                document.Placement = recovery.CaptureForSave(invalid);
                document.Tasks[0].Text = "latest task " + invalid.HeightDip;
                await store.SaveTodoAsync(path, document);
                PortableTodoDocument saved = await store.LoadTodoAsync(path);
                Require(saved.Tasks[0].Text == document.Tasks[0].Text && Same(saved.Placement, original),
                    "Latest task or the retained valid placement was lost.");
            }
            var moved = new PortableNotePlacement { MonitorDevice = "second-display", XDip = 80, YDip = 90,
                WidthDip = 280, HeightDip = 340 };
            document.Placement = recovery.CaptureWindowChange(moved, isRestored: true);
            Require(Same(document.Placement, moved), "A restored legal move/resize was rejected.");
            moved.HeightDip = 0; // The caller must not be able to damage the retained snapshot.
            document.Placement = recovery.CaptureForSave(null);
            document.Tasks[0].Text = "restored latest task";
            await store.SaveTodoAsync(path, document);
            PortableTodoDocument restored = await store.LoadTodoAsync(path);
            Require(restored.Placement is { WidthDip: 280, HeightDip: 340, XDip: 80, YDip: 90 } &&
                restored.Tasks[0].Text == "restored latest task", "Restored geometry/content did not persist.");

            foreach (PortableNotePlacement? initial in new PortableNotePlacement?[] { null, new() })
            {
                document.Placement = new TodoPlacementRecovery(initial).CaptureForSave(initial);
                await store.SaveTodoAsync(path, document);
                Require((await store.LoadTodoAsync(path)).Placement is { WidthDip: 360, HeightDip: 480 },
                    "Missing/invalid initial placement must use todo defaults, not note defaults.");
            }
            document.Placement = new() { HeightDip = 339 };
            bool rejected = false;
            try { await store.SaveTodoAsync(path, document); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Store validation was weakened to hide invalid placement.");
            string invalidJson = (await File.ReadAllTextAsync(path)).Replace("\"heightDip\":480", "\"heightDip\":339", StringComparison.Ordinal);
            Require(invalidJson.Contains("\"heightDip\":339"), "Invalid reader fixture did not alter placement.");
            await File.WriteAllTextAsync(path, invalidJson);
            rejected = false;
            try { await store.LoadTodoAsync(path); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Store accepted invalid on-disk geometry.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            string fullRoot = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(fullRoot) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) &&
                Path.GetFileName(fullRoot).StartsWith("TuckPane-window-fixes-", StringComparison.Ordinal), "Unsafe fixture cleanup path.");
            Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static void CheckRowHeight()
    {
        foreach (bool separators in new[] { false, true })
        {
            double previous = 0;
            foreach (double font in new[] { 8d, 14d, 48d })
            {
                double lineHeight = font * 1.4;
                double height = TodoRowMetrics.SingleLineHeight(font, lineHeight, separators);
                double contentHeight = height - 12 - (separators ? 1 : 0);
                Require(contentHeight >= lineHeight && contentHeight >= Math.Max(20, font * .8) + 1 && height >= previous,
                    "Shared height clipped text/checkbox or shrank as font grew.");
                Require(font != 8 || height < 40, "Small text is still pinned to the old 40 DIP minimum.");
                Require(TodoRowMetrics.SingleLineHeight(font, lineHeight * 2, separators) >= lineHeight * 2 + 12,
                    "System-scaled text cannot fit.");
                previous = height;
            }
        }
        XDocument xaml = XDocument.Load(Source("TodoWindow.xaml"));
        Require((string?)Named(xaml, "NewTaskEntry").Attribute("MinHeight") != "40" &&
            xaml.Descendants(Presentation + "Border").Any(e => (string?)e.Attribute("MinHeight") == "{Binding SingleLineHeight}") &&
            xaml.Descendants(Presentation + "Style").Where(e => (string?)e.Attribute("TargetType") == "ListViewItem")
                .SelectMany(e => e.Elements(Presentation + "Setter"))
                .Any(e => (string?)e.Attribute("Property") == "MinHeight" && (string?)e.Attribute("Value") == "0"),
            "Rows/composer retain inconsistent fixed minimum heights.");
        string code = File.ReadAllText(Source("TodoWindow.xaml.cs"));
        Require(Regex.IsMatch(code, @"double (\w+) = TodoRowMetrics\.SingleLineHeight\([\s\S]*?NewTaskEntry\.Height = \1;[\s\S]*?row\.SingleLineHeight = \1;"),
            "Composer and task rows must receive the same measured single-line height.");
    }

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);
    private static bool Same(PortableNotePlacement? a, PortableNotePlacement b) => a is not null &&
        a.MonitorDevice == b.MonitorDevice && a.XDip == b.XDip && a.YDip == b.YDip && a.WidthDip == b.WidthDip && a.HeightDip == b.HeightDip;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
