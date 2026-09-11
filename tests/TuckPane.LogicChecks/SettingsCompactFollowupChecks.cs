using System.Xml.Linq;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

// Only this conversation's five changes. Never creates windows or injects input.
internal static class SettingsCompactFollowupChecks
{
    internal static async Task RunAsync(string area)
    {
        if (area is not ("all" or "menus" or "colors" or "collapse" or "rename" or "inset"))
            throw new ArgumentException($"Unknown settings/compact followup area: {area}.");
        if (area is "all" or "menus") CheckMenus();
        if (area is "all" or "colors") await CheckColorsAsync();
        if (area is "all" or "collapse") await CheckCollapseAsync();
        if (area is "all" or "rename") CheckRename();
        if (area is "all" or "inset") SevenFeatureChecks.CheckInset();
        Console.WriteLine($"PASS --settings-compact-followup {area}: focused production logic, isolated state and source/XAML checks. No GUI or input automation; actual multi-monitor placement, invisible-button clicks and visual spacing require user acceptance.");
    }

    private static void CheckMenus()
    {
        var document = XDocument.Parse(Source("ConsoleWindow.xaml"));
        XElement page = Named(document, "MenuPage");
        XElement options = Named(document, "OrganizerMenuOptions");
        Require(Named(document, "DisplayNavItem").ElementsAfterSelf().First() == Named(document, "MenuNavItem"),
            "The menu navigation item must immediately follow Display.");
        foreach (string name in new[] { "FolderMenuToggle", "DesktopMenuToggle" })
            Require(Named(document, name).Ancestors().Contains(Named(document, "SystemPage")),
                "Windows shell menu switches must stay on System.");
        string preferences = Source("ConsoleWindow.OrganizerPreferences.cs");
        Require(preferences.Contains("OrganizerMenuOptions.Children.Add(toggle)") &&
            preferences.Contains("toggle.Toggled += OrganizerMenuToggle_Toggled") && preferences.Contains("_host.SetOrganizerMenuVisibilityAsync(key, toggle.IsOn)"),
            "The moved menu controls must retain the existing event and persistence chain.");
        Require((string?)Named(document, "MenuNavItem").Attribute("Tag") == "menus" &&
            options.Ancestors().Contains(page) &&
            !options.Ancestors().Any(e => e.Name.LocalName == "Expander") &&
            !options.Ancestors().Contains(Named(document, "DisplayPage")),
            "Menu choices must be directly visible in their own menus page, outside Display and an Expander.");
        string source = Source("ConsoleWindow.xaml.cs");
        Require(source.Contains("\"menus\" => MenuPage") && source.Contains("MenuPage,", StringComparison.Ordinal),
            "Navigation must select and participate in hiding/showing the new menu page.");
    }

    internal static async Task CheckColorsAsync()
    {
        Require(GlobalSettings.DefaultOrganizerTextColor == OrganizerTextColor.White,
            "New settings must default to white text.");
        foreach (int value in new[] { 0, 1, 2, 99, -1 })
        {
            Require(GlobalSettings.NormalizeOrganizerTextColor((OrganizerTextColor)value) == OrganizerTextColor.White,
                "Legacy and invalid color choices must normalize to white.");
            foreach (uint background in new[] { 0xFFF4F5F7u, 0xFF202124u })
            {
                var color = ThemePalette.ResolveOrganizerTextColor((OrganizerTextColor)value, new ThemeValues(background, .35));
                Require(color.A == 255 && color.R == 255 && color.G == 255 && color.B == 255,
                    "All legacy modes and themes must render opaque pure white organizer text.");
            }
        }
        await WithStateRootAsync(async path =>
        {
            foreach (string settings in new[] { "{}", "{\"OrganizerTextColor\":0}", "{\"OrganizerTextColor\":1}",
                "{\"OrganizerTextColor\":2}", "{\"OrganizerTextColor\":99}" })
            {
                await File.WriteAllTextAsync(path, "{\"SchemaVersion\":16,\"GlobalSettings\":" + settings + ",\"Organizers\":[]}");
                Require((await new StateStore(path).LoadAsync()).GlobalSettings.OrganizerTextColor == OrganizerTextColor.White,
                    "Existing state with absent, Black, Auto or invalid text color must load as white.");
            }
        });
        Require(!Source("ConsoleWindow.xaml").Contains("OrganizerTextColorCombo") &&
            !Source("ConsoleWindow.xaml.cs").Contains("OrganizerTextColorCombo"),
            "The removed color selector must leave no UI or event-handler wiring.");
    }

    private static async Task CheckCollapseAsync()
    {
        Require(!new GlobalSettings().HideCollapseIndicator, "Collapse appearance must remain visible by default.");
        await WithStateRootAsync(async path =>
        {
            await File.WriteAllTextAsync(path, "{\"SchemaVersion\":16,\"GlobalSettings\":{},\"Organizers\":[]}");
            var store = new StateStore(path);
            AppStateV2 state = await store.LoadAsync();
            Require(!state.GlobalSettings.HideCollapseIndicator, "Existing state must default the new hide switch off.");
            foreach (bool hidden in new[] { true, false })
            {
                state.GlobalSettings.HideCollapseIndicator = hidden;
                await store.SaveAsync(state);
                state = await store.LoadAsync();
                Require(state.GlobalSettings.HideCollapseIndicator == hidden, "Both hide choices must survive save/reload.");
            }
        });
        var settings = XDocument.Parse(Source("ConsoleWindow.xaml"));
        Require(Named(settings, "HideCollapseIndicatorToggle").Ancestors().Contains(Named(settings, "DisplayPage")),
            "The global collapse appearance switch must be on Display.");
        var main = XDocument.Parse(Source("MainWindow.xaml"));
        XElement button = Named(main, "CollapseButton");
        Require((string?)button.Attribute("Click") == "CollapseButton_Click" &&
            (string?)button.Attribute("Background") == "Transparent" &&
            (string?)button.Attribute("Width") == "28" && (string?)button.Attribute("Height") == "28" &&
            Named(main, "CollapseButtonSurface").Ancestors().Contains(button),
            "Hiding the surface must preserve the original transparent 28 DIP button and its click handler.");
        string source = Source("MainWindow.xaml.cs");
        Require(System.Text.RegularExpressions.Regex.IsMatch(source,
            @"CollapseButtonSurface\.Opacity\s*=\s*[^;]*HideCollapseIndicator[^;]*\?\s*0\s*:\s*1\s*;"),
            "The setting must hide the button surface using opacity while retaining the hit target.");
    }

    private static void CheckRename()
    {
        var main = new DisplayInfo("saved-primary", Rect(0, 0, 1920, 1080), Rect(0, 0, 1920, 1040), 1);
        var current = new DisplayInfo("current-negative", Rect(-2560, -200, 0, 1240), Rect(-2560, -200, 0, 1200), 1.5);
        var dock = Rect(-2400, 220, -2280, 640);
        int boundsCalls = 0, savedCalls = 0;
        DisplayInfo ForBounds(NativeMethods.RECT bounds)
        {
            boundsCalls++;
            Require(bounds.Left == dock.Left && bounds.Top == dock.Top && bounds.Right == dock.Right && bounds.Bottom == dock.Bottom,
                "The current logical Dock rectangle must reach monitor selection unchanged.");
            return current;
        }
        DisplayInfo Saved(string? device)
        {
            savedCalls++;
            Require(device == main.Device, "Only the fallback may use the saved monitor device.");
            return main;
        }
        Require(ReferenceEquals(RenameDialogPlacement.Resolve(dock, main.Device, ForBounds, Saved), current) &&
            boundsCalls == 1 && savedCalls == 0, "Current bounds must override a stale saved screen without consulting it.");
        Require(ReferenceEquals(RenameDialogPlacement.Resolve(null, main.Device, ForBounds, Saved), main) &&
            boundsCalls == 1 && savedCalls == 1, "Unavailable current bounds must use the saved-device fallback.");
        Require(ReferenceEquals(RenameDialogPlacement.Resolve(null, "disconnected-screen", ForBounds, device =>
            { Require(device == "disconnected-screen", "The missing-device fallback must receive the saved device."); return main; }), main),
            "When the saved screen is disconnected, the display service's available-screen fallback must be respected.");
        var dialog = DisplayPlacementService.CalculateCenteredDialogBounds(current);
        Require(Math.Abs(dialog.Left + dialog.Width / 2d - (current.Work.Left + current.Work.Width / 2d)) <= .5 &&
            Math.Abs(dialog.Top + dialog.Height / 2d - (current.Work.Top + current.Work.Height / 2d)) <= .5 &&
            dialog.Width == 660 && dialog.Height == 420,
            "The selected negative-coordinate screen must center the dialog in its work area at its own DPI.");
        string source = Source("MainWindow.xaml.cs");
        foreach (string method in new[] { "ShowRenameFileDialogAsync", "ShowRenameNoteDialogAsync", "ShowRenameDialogAsync" })
        {
            int start = source.IndexOf("private async Task " + method + "(", StringComparison.Ordinal);
            Require(start >= 0, $"The rename adapter {method} must exist.");
            int dialogCall = source.IndexOf("OwnedDialogWindow.ShowTextInputAsync", start, StringComparison.Ordinal);
            Require(dialogCall > start && source[start..dialogCall].Contains("GetRenameDialogDisplay()"),
                $"{method} must resolve the current display before creating its rename dialog.");
        }
        Require(source.Contains("RenameDialogPlacement.Resolve(") && source.Contains("TryGetLogicalWindowRect") && source.Contains("DockCenter"),
            "Production rename placement must connect logical bounds and the Dock-center fallback to the tested resolver.");
    }

    private static NativeMethods.RECT Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };
    private static XElement Named(XDocument document, string name) => document.Descendants()
        .Single(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == name);
    private static string Source(string relative) => File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", relative));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task WithStateRootAsync(Func<string, Task> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-settings-compact-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        try { await check(Path.Combine(root, "state.json")); }
        finally
        {
            await AppLogger.FlushAsync();
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previous);
            Directory.Delete(root, recursive: true);
        }
    }
}
