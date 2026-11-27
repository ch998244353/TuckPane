using System.Xml.Linq;
using TuckPane;
using TuckPane.Models;
using TuckPane.Services;

internal static class Oct03FixChecks
{
    internal static async Task RunAsync(string? area)
    {
        if (area is not (null or "todo" or "menu" or "corners")) throw new ArgumentException("Expected --oct03-fixes [todo|menu|corners].");
        if (area is null or "todo") CheckTodo();
        if (area is null or "menu") { await CheckMenuLifecycle(); CheckMenuRouting(); }
        if (area is null or "corners") CheckCorners();
        Console.WriteLine("PASS oct03-fixes: requested checks complete. No GUI/mouse tests; visual and live Shell acceptance pending.");
    }

    private static void CheckTodo()
    {
        foreach (double size in new[] { 14d, 24d })
        {
            var row = new TodoWindow.TodoRow(new PortableTodoTask { Text = "字号" }, size);
            var observed = new List<double>();
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(row.FontSize)) observed.Add(row.FontSize); };
            row.BeginEdit();
            Require(row.IsEditing && row.FontSize == size, "First edit loses current size.");
            row.FontSize = size + 2;
            row.CommitEdit("更新"); row.BeginEdit();
            Require(row.FontSize == size + 2 && observed.SequenceEqual(new[] { size, size + 2, size + 2 }), "Font notifications lose changed or reopened size.");
        }
        XDocument xaml = XDocument.Load(SourcePath("TodoWindow.xaml"));
        XElement text = xaml.Descendants().Single(e => (string?)e.Attribute("DoubleTapped") == "TaskText_DoubleTapped");
        XElement edit = xaml.Descendants().Single(e => (string?)e.Attribute("KeyDown") == "TaskEditor_KeyDown");
        Require(edit.Name.LocalName == "TaskEditorTextBox" && text.Parent == edit.Parent, "Editor does not use synchronized template host.");
        foreach (string property in new[] { "FontSize", "FontFamily", "FontWeight", "FontStretch", "CharacterSpacing", "IsTextScaleFactorEnabled" })
            Require(text.Attribute(property) is { } declared && declared.Value == (string?)edit.Attribute(property), $"Unequal typography: {property}.");
        Require((string?)edit.Attribute("FontSize") == "{Binding FontSize}" && (string?)edit.Attribute("IsTextScaleFactorEnabled") == "True", "Font source or system scaling differs.");
        Require((string?)edit.Parent?.Attribute("Loaded") == "TaskRow_Loaded" && (string?)edit.Parent?.Attribute("DataContextChanged") == "TaskRow_DataContextChanged", "Recycled row lacks synchronization hooks.");
        string control = Read("Controls/TaskEditorTextBox.cs");
        Require(control.Contains("GettingFocus +=") && control.Contains("override void OnApplyTemplate()") && control.Contains("Source = this") && control.Contains("BindingMode.OneWay"), "Template/focus typography binding hooks missing.");
        foreach (string property in new[] { "FontSize", "FontFamily", "FontWeight", "FontStyle", "FontStretch", "CharacterSpacing", "IsTextScaleFactorEnabled" })
            Require(control.Contains($"Bind(content, {property}Property, nameof({property}))"), $"Native content lacks {property} binding.");
        string begin = Method(Read("TodoWindow.xaml.cs"), "private void BeginTaskEdit", "private void TaskRow_Loaded");
        int template = begin.IndexOf("editor.ApplyTemplate()", StringComparison.Ordinal), focus = begin.IndexOf("editor.Focus(", StringComparison.Ordinal);
        Require(template >= 0 && focus > template && begin.Contains("taskEditor.SynchronizeTemplateTypography()") && begin.Contains("editor.SelectAll()"), "Template synchronization must precede focus and selection.");
        Console.WriteLine("PASS oct03-fixes todo: 14/24 row notifications plus template/focus/recycling typography contracts (static; visual pending).");
    }

    private static async Task CheckMenuLifecycle()
    {
        await Scenario("save-failed", "enter,prepare,leave");
        await Scenario("cancel", "enter,prepare,create,show,dispose,leave");
        await Scenario("handled", "enter,prepare,create,show,handle,dispose,refresh,leave");
        await Scenario("unknown", "enter,prepare,create,show,handle,invoke,dispose,refresh,leave");
        foreach (string stage in new[] { "enter", "prepare", "create", "show", "handle", "invoke", "refresh" })
        {
            string expected = stage switch
            {
                "enter" => "enter,leave",
                "prepare" => "enter,prepare,leave",
                "create" => "enter,prepare,create,leave",
                "show" => "enter,prepare,create,show,dispose,leave",
                "handle" => "enter,prepare,create,show,handle,dispose,leave",
                "invoke" => "enter,prepare,create,show,handle,invoke,dispose,leave",
                _ => "enter,prepare,create,show,handle,invoke,dispose,refresh,leave"
            };
            await Scenario("throw-" + stage, expected);
        }
        Console.WriteLine("PASS oct03-fixes menu: save failure, cancellation, handled/Shell commands and seven exception exits; ordered disposal and protection release.");
    }

    private static async Task Scenario(string name, string expected)
    {
        var trace = new List<string>();
        void Step(string value) { trace.Add(value); if (name == "throw-" + value) throw new TestFailure(); }
        var session = new FakeSession(Step, name == "cancel");
        bool failed = false;
        try
        {
            await FileContextMenuOperation.RunAsync(() => Step("enter"),
                () => { Step("prepare"); return Task.FromResult(name != "save-failed"); },
                () => { Step("create"); return session; },
                verb => { Step("handle"); Require(verb == "custom", "Canonical verb not forwarded."); return Task.FromResult(name == "handled"); },
                () => { Step("refresh"); return Task.CompletedTask; }, () => Step("leave"));
        }
        catch (TestFailure) { failed = true; }
        Require(failed == name.StartsWith("throw-", StringComparison.Ordinal) && string.Join(',', trace) == expected, $"{name}: {string.Join(',', trace)}; expected {expected}.");
    }

    private static void CheckMenuRouting()
    {
        foreach (WidgetItemKind kind in Enum.GetValues<WidgetItemKind>())
            Require(ShellContextMenuService.UsesShellMenu(kind) == (kind is not (WidgetItemKind.Note or WidgetItemKind.Organizer)), $"Incorrect route for {kind}.");
        Require(!ShellContextMenuService.UsesShellMenu((WidgetItemKind)999), "Undefined item kind routed to Shell.");
        string main = Read("MainWindow.xaml.cs");
        string route = Method(main, "private void Item_ContextRequested", "private bool TryGetElementScreenBounds");
        string menu = Read("MainWindow.FileContextMenu.cs");
        Require(route.Contains("ShellContextMenuService.UsesShellMenu") && route.Contains("ShowFileContextMenuAsync(host, item, e)") && menu.Contains("ShellContextMenuService.Create") && menu.Contains("FileContextMenuOperation.RunAsync") && menu.Contains("TryGetPosition(WindowRoot") && menu.Contains("ClientToScreen") && menu.Contains("RasterizationScale") && menu.Contains("bounds.Bottom") && menu.Contains("FlushPortableWindowAsync"), "Shared file routing lacks Shell, mouse/keyboard position, or save preparation.");
        foreach (string file in new[] { "MainWindow.Dock.cs", "MainWindow.HoverPresentation.cs", "MainWindow.OrganizerMenu.cs" })
            Require(Read(file).Contains("Item_ContextRequested(host, e)"), $"{file} bypasses shared file route.");
        Require(menu.Contains("ContextMenu_Opening(null, EventArgs.Empty)") && menu.Contains("_shellContextMenuOpen = false") && menu.Contains("RestoreContextMenuHost"), "Host activation or collapse protection release missing.");
        foreach (string verb in new[] { "rename", "copy", "cut", "delete" })
            Require(menu.Contains($"case \"{verb}\":") && menu.Contains("default: return false;"), $"Missing application command: {verb}.");
        Console.WriteLine("PASS oct03-fixes menu: file/special-kind routes and organizer/Dock/station/hover entry contracts (static; live Shell pending).");
    }

    private static void CheckCorners()
    {
        string console = Read("ConsoleWindow.xaml.cs"), chrome = Read("Services/NativeWindowChromeController.cs");
        string geometry = Method(console, "private void UpdateConsoleSurfaceGeometry", "private void ApplyNativeWindowChrome");
        Require(console.Contains("preferSystemCorners: true") && chrome.Contains("bool preferSystemCorners = false") && chrome.Contains("DWMWA_WINDOW_CORNER_PREFERENCE") && chrome.Contains("DWMWCP_ROUND"), "Console lacks opt-in native round corners.");
        Require(geometry.Contains("new CornerRadius(0)") && geometry.Contains("SetCornerRadius(0)") && geometry.Contains(".Clip = null") && !geometry.Contains("CreateRectangleClip"), "Console retains extra transparent rounded clipping.");
        Require(console.Contains("ConsoleXamlRoot_Changed") && console.Contains("UpdateConsoleSurfaceGeometry()"), "DPI geometry update missing.");
        string theme = Method(console, "public void ApplyTheme()", "public void RefreshAll");
        Require(theme.Contains("GetSurfaceBrush(\"ConsolePageSurfaceBrush\").Color") && theme.Contains("ConsoleRoot.Background = new SolidColorBrush(background)") && theme.Contains("SolidOpacity: 1") && theme.Contains("_settingsEdgeSurface.SetEnabled(false)"), "Opaque client fill or disabled edge overlay missing.");
        Require(geometry.Contains("_chrome?.QueueApply()"), "DPI updates fail to reapply native corner policy.");
        Console.WriteLine("PASS oct03-fixes corners: native radius, full client fill, no extra clip, disabled edge and DPI update contracts (static; visual pending).");
    }

    private sealed class FakeSession(Action<string> step, bool cancel) : IFileContextMenuSession
    {
        public FileMenuSelection? Show() { step("show"); return cancel ? null : new(7, "custom"); }
        public void Invoke(FileMenuSelection selection) { Require(selection == new FileMenuSelection(7, "custom"), "Shell command selection changed."); step("invoke"); }
        public void Dispose() => step("dispose");
    }
    private sealed class TestFailure : Exception { }
    private static string SourcePath(string file) => Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", file);
    private static string Read(string file) => File.ReadAllText(SourcePath(file));
    private static string Method(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal), last = source.IndexOf(end, first < 0 ? 0 : first, StringComparison.Ordinal);
        Require(first >= 0 && last > first, $"Missing source contract boundaries: {start} / {end}.");
        return source[first..last];
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
