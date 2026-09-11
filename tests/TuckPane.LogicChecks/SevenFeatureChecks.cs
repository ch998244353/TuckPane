using TuckPane.Core;
using TuckPane.Services;
using TuckPane.Models;
using System.Text.Json;

internal static class SevenFeatureChecks
{
    internal static async Task RunAsync(string area)
    {
        if (area == "all")
        {
            foreach (string scope in new[] { "menu", "rename", "resize", "drop", "drag", "inset", "limits" }) await RunAsync(scope);
            return;
        }
        switch (area)
        {
            case "menu": CheckMenu(); break;
            case "rename": CheckRename(); break;
            case "resize": await CheckResizeAsync(); break;
            case "drop": CheckDrop(); break;
            case "drag": CheckDrag(); break;
            case "inset": CheckInset(); break;
            case "limits": await CheckLimitsAsync(); break;
            default: throw new ArgumentException($"Unknown seven-feature scope: {area}.");
        }
        Console.WriteLine($"PASS --seven-features {area}: focused logic and source wiring; no UI/input automation or real registry.");
    }

    private static void CheckMenu()
    {
        string executable = Path.GetFullPath(@"C:\Seven Checks\TuckPane.exe");
        string other = Path.GetFullPath(@"C:\Other Copy\TuckPane.exe");
        string verb = FolderContextMenuService.DesktopVerbKey;
        string command = verb + @"\command";
        string preference = FolderContextMenuService.DesktopPreferenceKey;
        var store = new MemoryMenuStore();
        int notifications = 0;
        FolderContextMenuService Service(string path, bool desktop = true) =>
            new(store, path, _ => true, () => notifications++, desktop);
        var desktop = Service(executable);
        var folder = Service(executable, desktop: false);

        Require(FolderContextMenuService.BuildCommand(executable, desktop: true) ==
            $"\"{executable}\" --create-organizer", "Desktop command must quote the executable without a folder argument.");
        desktop.InitializeDesktop("New organizer", installedCopy: false);
        Require(desktop.ReadState().Status == FolderContextMenuStatus.Disabled && notifications == 0,
            "A portable copy must not automatically register a menu.");
        folder.Disable();
        desktop.InitializeDesktop("New organizer", installedCopy: true);
        Require(desktop.ReadState().Status == FolderContextMenuStatus.Enabled &&
            folder.ReadState().Status == FolderContextMenuStatus.Disabled,
            "First installation/upgrade must enable desktop independently of a disabled folder menu.");
        desktop.Disable();
        desktop.InitializeDesktop("New organizer", installedCopy: true);
        Require(desktop.ReadState().Status == FolderContextMenuStatus.Disabled &&
            (int)store.Read(preference)!["Enabled"] == 0, "A saved disabled preference must survive startup/upgrade.");

        desktop.Enable("New organizer");
        store.Delete(command);
        Require(desktop.ReadState().Status == FolderContextMenuStatus.Broken && desktop.RepairIfOwned("Repaired") &&
            desktop.ReadState().Status == FolderContextMenuStatus.Enabled, "An owned incomplete registration must be repairable.");
        Service(other).Enable("Other copy");
        var foreignSnapshot = store.Snapshot();
        desktop.InitializeDesktop("Do not replace", installedCopy: true);
        Require(!desktop.RepairIfOwned("Do not replace"), "Startup repair must not take over another copy.");
        Throws<InvalidOperationException>(desktop.Disable);
        Require(store.Snapshot() == foreignSnapshot && desktop.ReadState().Status == FolderContextMenuStatus.OtherCopy,
            "Startup and disable must preserve foreign registration and preference.");

        // Fail halfway through explicit takeover and check all three keys, including the foreign preference.
        int beforeFailure = notifications;
        store.FailNextWrite = command;
        Throws<IOException>(() => desktop.Enable("Failed takeover"));
        Require(store.Snapshot() == foreignSnapshot && notifications == beforeFailure,
            "A partial enable failure must restore the complete previous registration without notifying success.");

        string installer = Source("installer/TuckPane.iss");
        string registrySection = installer.Split("[Registry]")[1].Split("[Code]")[0];
        Require(!registrySection.Contains("Subkey: \"Software\\Classes\\DesktopBackground\\Shell\\TuckPane.CreateOrganizer\""),
            "Installer registry entries must not unconditionally delete the current desktop verb.");
        Require(installer.Contains("RegisterDesktopMenu;") && installer.Contains("UnregisterOwnedDesktopMenu;") &&
            installer.Contains("if Enabled = 0 then Exit;") && installer.Contains("if not DesktopMenuOwnedByThisInstall then Exit;") &&
            installer.Contains("if DesktopMenuOwnedByThisInstall then"),
            "Installer and uninstaller must retain disabled preferences and guard registration ownership.");
        Require(Source("src/TuckPane/AppHost.cs").Contains("DesktopContextMenu.InitializeDesktop(") &&
            Source("src/TuckPane/ConsoleWindow.DesktopMenu.cs").Contains("_host.DesktopContextMenu.Disable()"),
            "Startup and the independent settings toggle must use the desktop service.");
    }

    private static void CheckRename()
    {
        var edit = new OrganizerTitleEdit();
        Require(!edit.IsBusy && edit.Begin() && edit.IsEditing && !edit.Begin(), "Only one title editor can begin.");
        Require(edit.Finish(commit: true) && edit.IsSaving && edit.IsBusy && !edit.IsEditing &&
            !edit.Finish(commit: true) && !edit.Begin(), "Enter plus LostFocus must submit once and block reentry during save.");
        edit.Saved();
        Require(!edit.IsBusy && edit.Begin() && !edit.Finish(commit: false) && !edit.IsBusy &&
            !edit.Finish(commit: true), "Escape followed by LostFocus must cancel without saving.");

        string title = Source("src/TuckPane/MainWindow.OrganizerTitle.cs");
        Require(title.Contains("_definition.HideName") && title.Contains("ExpandedNameEditor.SelectAll()") &&
            title.Contains("WindowActivationState.Deactivated") && title.Contains("CommitTitleOnOutsidePress") &&
            title.IndexOf("_organizerTitleEdit.Finish(commit)", StringComparison.Ordinal) <
                title.IndexOf("ExpandedNameEditor.Visibility = Visibility.Collapsed", StringComparison.Ordinal) &&
            title.Contains("e.Handled = true;") && title.Contains("_host.RenameOrganizerAsync(OrganizerId, draft)"),
            "Editor must respect hidden names, select text, deduplicate before LostFocus, consume Escape, and use the existing rename service.");
        string window = Source("src/TuckPane/MainWindow.xaml.cs");
        Require(window.Contains("canOpen && _pressedOrganizerTitle && !wasDragging") &&
            window.Contains("Activated += OrganizerTitleWindow_Activated") && window.Contains("if (CommitTitleOnOutsidePress(e)) return;") &&
            window.Contains("_organizerTitleEdit.IsBusy || _closing") &&
            window.Contains("private Task CollapseAsync() => _organizerTitleEdit.IsBusy") && window.Contains("Task.CompletedTask : CollapseTransitionAsync()"),
            "A title drag must not rename, and title editing/saving must block drag and collapse.");
        string host = Source("src/TuckPane/AppHost.OrganizerRename.cs");
        Require(host.Contains("OrganizerNameChange.RenameAsync(definition, name, SaveStateAsync)") &&
            host.Contains("parent.RefreshContainedOrganizerName") && host.Contains("Console.RefreshOrganizerName"),
            "Inline rename must retain existing save/rollback and parent/management name synchronization.");
    }

    private static async Task CheckResizeAsync()
    {
        var range = OrganizerContentScale.FactorRange(400, 200, 200, 100, 600, 500, 1, 1);
        Require(range.Min == .5 && range.Max == 1.5, "Both axes must share the tighter size limit.");
        foreach (double requested in new[] { .25, .75, 1.25, 2d })
        {
            double factor = Math.Clamp(requested, range.Min, range.Max);
            var bounds = OrganizerInteractionMath.CreateCenteredBounds(500, 400, 400 * factor, 200 * factor);
            Require(bounds.Width / (double)bounds.Height == 2 && bounds.Left + bounds.Width / 2 == 500 &&
                bounds.Top + bounds.Height / 2 == 400 && 40 * factor / bounds.Width == .1,
                "Growing, shrinking and clamping must preserve panel center, aspect and content proportion.");
        }
        Require(OrganizerContentScale.DisplayFit(400, 200, 300, 100) == .5 &&
            OrganizerContentScale.Normalize(double.NaN) == 1 && OrganizerContentScale.Normalize(10) == 4,
            "Work-area fitting and invalid persisted scales must have one finite factor.");
        OrganizerDefinition legacy = JsonSerializer.Deserialize<OrganizerDefinition>("{}")!;
        Require(legacy.IconContentScale == 1 && legacy.CompactListContentScale == 1, "Missing scale fields must preserve old appearance.");
        foreach (OrganizerExpandedContentMode mode in Enum.GetValues<OrganizerExpandedContentMode>())
        {
            var definition = new OrganizerDefinition { ExpandedContentMode = mode, IconContentScale = 1.25,
                CompactListContentScale = .75, CompactListCanvasWidthDip = 450, CompactListCanvasHeightDip = 300,
                ManualCanvasBaseWidthDip = 400, ManualCanvasBaseHeightDip = 200 };
            AppStateV2 loaded = await RoundTripAsync(new() { Organizers = [definition] });
            OrganizerDefinition copy = OrganizerInteractionMath.CopySettings(loaded.Organizers.Single(), "Copy");
            Require(copy.IconContentScale == 1.25 && copy.CompactListContentScale == .75 &&
                copy.ManualCanvasBaseWidthDip == 400 && copy.ManualCanvasBaseHeightDip == 200 &&
                copy.CompactListCanvasWidthDip == 450 && copy.CompactListCanvasHeightDip == 300,
                "Both modes must retain their independent content and panel sizes across save/load/copy.");
        }
        string window = Source("src/TuckPane/MainWindow.xaml.cs");
        Require(window.Contains("session.StartContentScale * canvasScale") && window.Contains("WindowAlignmentMath.AlignScale(centerX, centerY"),
            "Live resize and alignment must use the same snapshot factor.");
    }

    private static void CheckDrop()
    {
        var activity = new IncomingDropActivity();
        Require(!activity.ProtectsExpansion(false) && activity.ProtectsExpansion(true), "File hover alone must protect expansion.");
        IDisposable hover = activity.Hold();
        using (activity.Hold()) Require(activity.ProtectsExpansion(false), "Organizer hover and in-flight receive must protect expansion.");
        Require(activity.IsActive, "Completing a receive must not release another active hover.");
        hover.Dispose(); hover.Dispose();
        Require(!activity.IsActive, "Leave/cancel release must be idempotent.");
        Throws<IOException>(() => { using var failedReceive = activity.Hold(); throw new IOException("Failed drop"); });
        Require(!activity.IsActive, "A failed receive must release its hold.");
        string host = Source("src/TuckPane/AppHost.cs"), window = Source("src/TuckPane/MainWindow.xaml.cs");
        Require(host.Contains("target.HoldIncomingDrop()") && window.Contains("using var receivingDrop = HoldIncomingDrop()") &&
            window.Contains("_host.EndOrganizerDragHover(this);") && window.Contains("ReceivingDrop || !_host.State.GlobalSettings.CollapseOnPointerLeave"),
            "Organizer and file adapters must share receive protection and existing pointer-leave policy.");
        Require(OrganizerExpansion.IsPermanent(new() { ExpansionMode = OrganizerExpansionMode.AlwaysExpanded }),
            "Permanent mode remains permanent after transient receive protection is released.");
    }

    private static void CheckDrag()
    {
        foreach (double dpiScale in new[] { 1d, 1.5, 2 })
            Require(!WidgetDragActivation.ShouldStart(7.9 * dpiScale, 0, dpiScale) &&
                WidgetDragActivation.ShouldStart(8 * dpiScale, 0, dpiScale) &&
                WidgetDragActivation.ShouldStart(-6 * dpiScale, -6 * dpiScale, dpiScale),
                "Mouse activation must retain the 8 DIP radial threshold at each DPI without a time requirement.");
        string window = Source("src/TuckPane/MainWindow.xaml.cs");
        Require(window.Contains("if (!_widgetMousePress) _longPressTimer.Start();") &&
            window.Contains("TryStartMouseWidgetDrag();") && window.Contains("canOpen && _pressedOrganizerTitle && !wasDragging"),
            "Mouse movement must trigger directly, preserve touch long press, and suppress post-drag title clicks.");
    }

    internal static void CheckInset()
    {
        foreach (var (itemScale, contentScale) in new[] { (1d, 1d), (.5, .75), (1.65, 1.25) })
            Require(Math.Abs((OrganizerContentScale.CompactLeftInset(12, itemScale) + 4 * itemScale) * contentScale -
                2 * (12 + 4 * itemScale) * contentScale * (2d / 3)) < .00001,
                "The full left-to-icon gap, including row padding, must be two thirds of the previous doubled gap.");
        Require(Source("src/TuckPane/MainWindow.xaml.cs").Contains("OrganizerContentScale.CompactLeftInset(side, _definition.CompactListItemScale)"),
            "The live content inset must use the reduced total-gap calculation.");
    }

    private static async Task CheckLimitsAsync()
    {
        var parent = new OrganizerDefinition { Name = "Container" };
        List<OrganizerDefinition> definitions = [parent, .. Enumerable.Range(1, 15).Select(index =>
            new OrganizerDefinition { Name = $"Organizer {index}", ContainerOrganizerId = parent.Id })];
        parent.ItemOrder = definitions.Skip(1).Select(item => OrganizerContainment.ItemKey(item.Id)).ToList();
        await new FolderOrganizerCreation().CreateAsync(null, new GlobalSettings(), definitions, (draft, path) =>
        {
            Require(path is null && draft.Layout.Rows == 3 && draft.Layout.Columns == 3, "Shell creation must retain default 3x3 behavior above twelve.");
            definitions.Add(draft);
            return Task.CompletedTask;
        });
        AppStateV2 loaded = await RoundTripAsync(new() { Organizers = definitions });
        Require(loaded.Organizers.Count == 17 && definitions.Select(item => item.Id).ToHashSet().SetEquals(loaded.Organizers.Select(item => item.Id)) &&
            OrganizerContainment.GetDirectChildren(loaded.Organizers, parent.Id).Select(item => item.Id).SequenceEqual(definitions.Skip(1).Take(15).Select(item => item.Id)),
            "More than twelve windows must survive creation/save/load with every ID and child order preserved.");
        Require(!Source("src/TuckPane/Models/OrganizerLimits.cs").Contains("MaximumOrganizers") &&
            !Source("src/TuckPane/AppHost.cs").Contains("MaximumOrganizers"), "Ordinary creation and duplication must not retain a hidden count gate.");
    }

    private static async Task<AppStateV2> RoundTripAsync(AppStateV2 state)
    {
        string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TuckPane-seven-" + Guid.NewGuid().ToString("N")));
        try
        {
            var store = new StateStore(Path.Combine(directory, "state.json"));
            await store.SaveAsync(state);
            return await store.LoadAsync();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static string Source(string relative)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException($"Cannot locate source for static wiring check: {relative}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class MemoryMenuStore : IFolderContextMenuStore
    {
        private readonly Dictionary<string, Dictionary<string, object>> _keys = new(StringComparer.OrdinalIgnoreCase);
        internal string? FailNextWrite { get; set; }
        public IReadOnlyDictionary<string, object>? Read(string key) =>
            _keys.TryGetValue(key, out var values) ? new Dictionary<string, object>(values) : null;
        public void Write(string key, IReadOnlyDictionary<string, object> values)
        {
            if (key == FailNextWrite) { FailNextWrite = null; throw new IOException("Injected registration failure."); }
            _keys[key] = new Dictionary<string, object>(values);
        }
        public void Delete(string key)
        {
            foreach (string match in _keys.Keys.Where(item => item.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                item.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToArray()) _keys.Remove(match);
        }
        internal string Snapshot() => System.Text.Json.JsonSerializer.Serialize(_keys.OrderBy(pair => pair.Key)
            .Select(pair => new { pair.Key, Values = pair.Value.OrderBy(value => value.Key).ToArray() }));
    }
}
