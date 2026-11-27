using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class UnifiedExpandedSizeChecks
{
    private const OrganizerExpandedContentMode Icon = OrganizerExpandedContentMode.Icon;
    private const OrganizerExpandedContentMode Compact = OrganizerExpandedContentMode.CompactList;

    internal static async Task RunAsync()
    {
        CheckScope();
        CheckGeometryAndLimits();
        CheckConversionAndReapplication();
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-unified-expanded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CheckPersistenceAsync(root);
            await CheckSaveOwnershipAsync(root);
        }
        finally
        {
            string fullRoot = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(fullRoot) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) &&
                Path.GetFileName(fullRoot).StartsWith("TuckPane-unified-expanded-", StringComparison.Ordinal),
                "Refusing cleanup outside the isolated fixture directory.");
            Directory.Delete(fullRoot, recursive: true);
        }
        Console.WriteLine("PASS --unified-expanded-size: 5 focused groups (mode/scope, DIP geometry/limits, conversion/reapplication, disabled/legacy/persistence, failed/stale saves). Production geometry and StateStore only; no windows or input automation. Native settings and resize wiring remain manual acceptance.");
    }

    private static void CheckScope()
    {
        foreach (var mode in new[] { Icon, Compact })
        {
            var settings = new GlobalSettings();
            var otherMode = mode == Icon ? Compact : Icon;
            var floating = Item(mode);
            var positioned = Item(mode, OrganizerPlacementMode.Positioned);
            var station = Item(mode, OrganizerPlacementMode.Station);
            var dock = Item(mode, OrganizerPlacementMode.Dock);
            var contained = Item(mode);
            contained.ContainerOrganizerId = station.Id;
            var other = Item(otherMode);
            var items = new[] { floating, positioned, contained, station, dock, other };
            var excludedBefore = items.Skip(3).Select(UnifiedExpandedSnapshot.Geometry.Capture).ToArray();
            var otherSettings = UnifiedExpandedSize.Get(settings, otherMode);
            UnifiedExpandedSize.Set(settings, mode, new(true, 320, 410, 140, 1.1));
            var applied = UnifiedExpandedSize.Apply(settings, mode, items.Select(Target));
            Require(applied.Changed.ToHashSet().SetEquals(new[] { floating.Id, positioned.Id, contained.Id }),
                "Floating, positioned and contained regular organizers must participate; Station, Dock and other mode must not.");
            Require(items.Skip(3).Select(UnifiedExpandedSnapshot.Geometry.Capture).SequenceEqual(excludedBefore) &&
                UnifiedExpandedSize.Get(settings, otherMode) == otherSettings,
                "Applying one mode changed an excluded organizer or the other mode's independent settings.");
            Require(UnifiedExpandedSize.Participates(contained, mode) && !UnifiedExpandedSize.Participates(station, mode) &&
                !UnifiedExpandedSize.Participates(dock, mode) && !UnifiedExpandedSize.Participates(other, mode),
                "Participation must depend on regular placement and current content mode, not containment.");
        }
    }

    private static void CheckGeometryAndLimits()
    {
        var settings = new GlobalSettings();
        var wide = Item(Icon, rows: 2, columns: 4);
        var tall = Item(Icon, rows: 6, columns: 2);
        UnifiedExpandedSize.Set(settings, Icon, new(true, 280, 360, 128, 1));
        UnifiedExpandedSize.Apply(settings, Icon, new[] { Target(wide), Target(tall) });
        Near(wide.ManualCanvasBaseWidthDip!.Value, 606, "2x4 panel width in DIP");
        Near(wide.ManualCanvasBaseHeightDip!.Value, 337.5, "2x4 panel height in DIP");
        Near(tall.ManualCanvasBaseWidthDip!.Value, 325, "6x2 panel width in DIP");
        Near(tall.ManualCanvasBaseHeightDip!.Value, 899.5, "6x2 panel height in DIP");
        RequireSquareCell(wide, 128);
        RequireSquareCell(tall, 128);
        Near(UnifiedExpandedSize.CellFromPanel(wide.Layout, 909, 506.25, 1.5), 192,
            "150% physical geometry must preserve the 128-DIP cell");

        var excluded = Item(Compact);
        var station = Item(Icon, OrganizerPlacementMode.Station);
        UnifiedExpandedSize.Set(settings, Icon, new(true, 280, 360, 200, 1.65));
        var limited = UnifiedExpandedSize.Apply(settings, Icon,
            [new(wide, 606, 337.5), new(tall, 269, 731.5), new(excluded, 1, 1), new(station, 1, 1)]);
        Near(limited.Values.Cell, 100, "The tightest participating layout must constrain the common cell");
        Near(limited.Limits.Cell, 100, "Reported common cell limit");
        Require(limited.Limited && limited.Values.Content < 1.65 &&
            DisplayPlacementService.CalculateRequiredCellDip(limited.Values.Content) <= 100.0001,
            "Content must be capped to fit the constrained cell.");
        RequireSquareCell(wide, 100);
        RequireSquareCell(tall, 100);

        var compactA = Item(Compact);
        var compactB = Item(Compact);
        UnifiedExpandedSize.Set(settings, Compact, new(true, 500, 500, 128, 1.65));
        var compact = UnifiedExpandedSize.Apply(settings, Compact,
            [new(compactA, 310, 105), new(compactB, 250, 200), new(wide, 1, 1)]);
        Near(compact.Values.Width, 250, "Compact common width");
        Near(compact.Values.Height, 105, "Compact common height");
        Near(compact.Values.Content, 36.5 / 36, "One compact row must fit below the vertical insets");
        Require(compact.Limited && compactA.CompactListCanvasWidthDip == compactB.CompactListCanvasWidthDip &&
            compactA.CompactListItemScale == compactB.CompactListItemScale,
            "Compact participants must receive the same constrained panel and content size.");
    }

    private static void CheckConversionAndReapplication()
    {
        var settings = new GlobalSettings();
        var source = Item(Icon, rows: 2, columns: 4);
        source.ManualCanvasBaseWidthDip = 606;
        source.ManualCanvasBaseHeightDip = 337.5;
        source.CanvasScale = source.IconContentScale = 1.2;
        source.ItemScale = .75;
        var converted = UnifiedExpandedSize.FromOrganizer(settings, source);
        Near(converted.Cell, 153.6, "Organizer conversion must include canvas and inset scaling");
        Near(converted.Content, .9, "Organizer conversion must include icon content scaling");
        UnifiedExpandedSize.Set(settings, Icon, converted with { Enabled = true });
        var newcomer = Item(Icon, rows: 6, columns: 2);
        var switched = Item(Compact);
        var targets = new[] { source, newcomer, switched };
        UnifiedExpandedSize.Apply(settings, Icon, targets.Select(Target));
        RequireSquareCell(newcomer, 153.6);
        switched.ExpandedContentMode = Icon;
        newcomer.Layout.Rows = 4;
        newcomer.Layout.Columns = 3;
        var changed = UnifiedExpandedSize.Apply(settings, Icon, targets.Select(Target));
        Require(changed.Changed.ToHashSet().SetEquals(new[] { newcomer.Id, switched.Id }),
            "The same Apply entry must adopt mode switches and row/column edits without changing settled peers.");
        RequireSquareCell(newcomer, 153.6);
        RequireSquareCell(switched, 153.6);
        Require(UnifiedExpandedSize.Apply(settings, Icon, targets.Select(Target)).Changed.Length == 0,
            "Repeated application must be idempotent.");

        var compact = Item(Compact);
        compact.CompactListCanvasWidthDip = 315;
        compact.CompactListCanvasHeightDip = 425;
        compact.CompactListContentScale = 1.2;
        compact.CompactListItemScale = .75;
        var compactValue = UnifiedExpandedSize.FromOrganizer(settings, compact);
        Near(compactValue.Width, 315, "Compact conversion width");
        Near(compactValue.Height, 425, "Compact conversion height");
        Near(compactValue.Content, .9, "Compact conversion content scale");
    }

    private static async Task CheckPersistenceAsync(string root)
    {
        string path = Path.Combine(root, "state.json");
        await File.WriteAllTextAsync(path, """{"SchemaVersion":16,"GlobalSettings":{},"Organizers":[]}""");
        var state = await new StateStore(path).LoadAsync();
        Require(UnifiedExpandedSize.Get(state.GlobalSettings, Icon) == new UnifiedExpandedValues(false, 280, 360, 128, 1) &&
            UnifiedExpandedSize.Get(state.GlobalSettings, Compact) == new UnifiedExpandedValues(false, 280, 360, 128, 1),
            "Old JSON without the seven fields must leave both modes disabled with stable defaults.");
        var item = Item(Icon);
        state.Organizers.Add(item);
        UnifiedExpandedSize.Set(state.GlobalSettings, Icon, new(true, 280, 360, 145, 1.15));
        UnifiedExpandedSize.Set(state.GlobalSettings, Compact, new(true, 315, 425, 128, .9));
        UnifiedExpandedSize.Apply(state.GlobalSettings, Icon, [Target(item)]);
        var retained = UnifiedExpandedSnapshot.Geometry.Capture(item);
        var enabled = UnifiedExpandedSize.Get(state.GlobalSettings, Icon);
        UnifiedExpandedSize.Set(state.GlobalSettings, Icon, enabled with { Enabled = false });
        var disabled = UnifiedExpandedSize.Apply(state.GlobalSettings, Icon, [new(item, 200, 200)]);
        Require(disabled.Changed.Length == 0 && UnifiedExpandedSnapshot.Geometry.Capture(item) == retained &&
            UnifiedExpandedSize.Get(state.GlobalSettings, Icon) == (enabled with { Enabled = false }),
            "Disabling must retain the last parameters and organizer geometry, even under smaller limits.");
        await new StateStore(path).SaveAsync(state);
        var loaded = await new StateStore(path).LoadAsync();
        Require(UnifiedExpandedSize.Get(loaded.GlobalSettings, Icon) == (enabled with { Enabled = false }) &&
            UnifiedExpandedSize.Get(loaded.GlobalSettings, Compact) == UnifiedExpandedSize.Get(state.GlobalSettings, Compact) &&
            UnifiedExpandedSnapshot.Geometry.Capture(loaded.Organizers.Single()) == retained,
            "StateStore reload must retain independent mode values and last applied geometry while disabled.");
        UnifiedExpandedSize.Set(loaded.GlobalSettings, Icon, enabled);
        var next = Item(Icon);
        loaded.Organizers.Add(next);
        UnifiedExpandedSize.Apply(loaded.GlobalSettings, Icon, loaded.Organizers.Select(Target));
        RequireSquareCell(next, 145);
        Near(next.ItemScale, 1.15, "Re-enabling must reuse the persisted content size");
    }

    private static async Task CheckSaveOwnershipAsync(string root)
    {
        var state = new AppStateV2 { Organizers = [Item(Icon)] };
        var item = state.Organizers.Single();
        var original = UnifiedExpandedSnapshot.Geometry.Capture(item);
        var originalSettings = UnifiedExpandedSize.Get(state.GlobalSettings, Icon);
        var changes = new UnifiedExpandedChanges();
        changes.Begin(state, Icon);
        item.Layout.Rows = 2;
        item.Layout.Columns = 4;
        UnifiedExpandedSize.Set(state.GlobalSettings, Icon, new(true, 280, 360, 150, 1.1));
        UnifiedExpandedSize.Apply(state.GlobalSettings, Icon, [Target(item)]);
        item.ExpandedContentMode = Compact;
        item.Layout.Mode = OrganizerLayoutMode.Row;
        item.Name = "unrelated edit survives";
        item.CompactListCanvasWidthDip = 333;
        var newcomer = Item(Compact);
        state.Organizers.Add(newcomer);
        string blocker = Path.Combine(root, "blocked-parent");
        await File.WriteAllTextAsync(blocker, "file prevents directory creation");
        UnifiedExpandedSave? failed = null;
        bool rejected = false;
        try { await new StateStore(Path.Combine(blocker, "state.json")).SaveAsync(state, () => failed = changes.Capture(state)); }
        catch (IOException) { rejected = true; }
        Require(rejected && failed is not null && changes.Rollback(state, failed),
            "A real StateStore save failure must restore the captured feature baseline.");
        Require(UnifiedExpandedSize.Get(state.GlobalSettings, Icon) == originalSettings &&
            item.ManualCanvasBaseWidthDip == original.ManualWidth && item.ManualCanvasBaseHeightDip == original.ManualHeight &&
            item.ItemScale == original.Item && item.Name == "unrelated edit survives" &&
            item.CompactListCanvasWidthDip == 333 && state.Organizers.Contains(newcomer),
            "Rollback must restore only the edited mode, preserving unrelated edits and new membership.");
        Require(item.ExpandedContentMode == original.Mode && item.Name == "unrelated edit survives",
            "A failed save must restore the previous content mode without reverting an unrelated name edit.");
        Require(item.Layout.Rows == original.Rows && item.Layout.Columns == original.Columns && item.Layout.Mode == original.LayoutMode,
            "A failed save must restore the previous row count, column count and layout mode.");

        changes.Begin(state, Icon);
        UnifiedExpandedSize.Set(state.GlobalSettings, Icon, new(true, 280, 360, 140, 1));
        UnifiedExpandedSize.Apply(state.GlobalSettings, Icon, [Target(item)]);
        var older = changes.Capture(state)!;
        changes.Begin(state, Icon);
        UnifiedExpandedSize.Set(state.GlobalSettings, Icon, new(true, 280, 360, 160, 1));
        UnifiedExpandedSize.Apply(state.GlobalSettings, Icon, [Target(item)]);
        var newer = changes.Capture(state)!;
        Require(!changes.Rollback(state, older), "An obsolete failed save must not roll back a newer edit.");
        changes.Commit(older);
        Near(UnifiedExpandedSize.Get(state.GlobalSettings, Icon).Cell, 160, "An older commit must leave the newer edit visible");
        Require(changes.Capture(state) is not null && changes.Rollback(state, newer),
            "A newer failed save must still roll back after an earlier save commits.");
        Near(UnifiedExpandedSize.Get(state.GlobalSettings, Icon).Cell, 140, "Rollback must use the latest successfully saved baseline");
        RequireSquareCell(item, 140);

        changes.Begin(state, Icon);
        UnifiedExpandedSize.Set(state.GlobalSettings, Icon, new(true, 280, 360, 170, 1));
        UnifiedExpandedSize.Apply(state.GlobalSettings, Icon, [Target(item)]);
        var latest = changes.Capture(state)!;
        changes.Commit(latest);
        changes.Commit(older);
        Require(changes.Capture(state) is null && !changes.Rollback(state, older) && !changes.Rollback(state, latest),
            "Obsolete or already committed callbacks must not resurrect pending edits or undo a successful save.");
        RequireSquareCell(item, 170);
        await CheckSaveGateAsync(root);
    }

    private static async Task CheckSaveGateAsync(string root)
    {
        var timeout = TimeSpan.FromSeconds(5);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interactionEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new AppStateV2();
        string path = Path.Combine(root, "queued-state.json");
        var store = new StateStore(path);
        UnifiedExpandedSize.Set(state.GlobalSettings, Icon, new(true, 280, 360, 140, 1));
        double firstCell = 0, secondCell = 0;
        Task first = store.SaveAsync(state,
            () => firstCell = UnifiedExpandedSize.Get(state.GlobalSettings, Icon).Cell,
            async () =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(timeout);
            });
        Task? second = null;
        try
        {
            await firstEntered.Task.WaitAsync(timeout);
            second = store.SaveAsync(state,
                () => secondCell = UnifiedExpandedSize.Get(state.GlobalSettings, Icon).Cell,
                async () =>
                {
                    secondEntered.TrySetResult();
                    await interactionEnded.Task.WaitAsync(timeout);
                    UnifiedExpandedSize.Set(state.GlobalSettings, Icon, new(true, 280, 360, 190, 1));
                });
            Require(!secondEntered.Task.IsCompleted && !second.IsCompleted && secondCell == 0,
                "A queued save must acquire the save gate before running readiness or capturing a snapshot.");
            UnifiedExpandedSize.Set(state.GlobalSettings, Icon, new(true, 280, 360, 180, 1));
            releaseFirst.TrySetResult();
            await secondEntered.Task.WaitAsync(timeout);
            Near(firstCell, 180, "The first snapshot must follow its readiness wait");
            Require(secondCell == 0 && !second.IsCompleted,
                "Acquiring the gate must not capture a snapshot before the active interaction finishes.");
            interactionEnded.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(timeout);
            Near(secondCell, 190, "The queued snapshot must capture the final value after interaction readiness");
            var loaded = await store.LoadAsync().WaitAsync(timeout);
            Near(UnifiedExpandedSize.Get(loaded.GlobalSettings, Icon).Cell, 190,
                "Serialization must persist the same final state observed by snapshotCaptured");
            string lastGood = await File.ReadAllTextAsync(path);
            UnifiedExpandedSize.Set(state.GlobalSettings, Icon, new(true, 280, 360, 200, 1));
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool blocked = false;
                try { await store.SaveAsync(state).WaitAsync(timeout); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { blocked = true; }
                Require(blocked && !File.Exists(path + ".tmp") && await File.ReadAllTextAsync(path) == lastGood,
                    "A failed replacement must keep the live file and clean up its temporary snapshot.");
            }
            await store.SaveAsync(state).WaitAsync(timeout);
            Require(await File.ReadAllTextAsync(path + ".bak") == lastGood,
                "The next successful save must back up the previous persisted state.");
            Near(UnifiedExpandedSize.Get((await store.LoadAsync()).GlobalSettings, Icon).Cell, 200,
                "A failed write must release the gate for a later save.");
        }
        finally
        {
            releaseFirst.TrySetResult();
            interactionEnded.TrySetResult();
            await (second is null ? first : Task.WhenAll(first, second)).WaitAsync(timeout);
        }
    }

    private static OrganizerDefinition Item(OrganizerExpandedContentMode mode,
        OrganizerPlacementMode placement = OrganizerPlacementMode.Floating, int rows = 3, int columns = 3) => new()
    {
        ExpandedContentMode = mode,
        PlacementMode = placement,
        Layout = new OrganizerLayout { Rows = rows, Columns = columns }
    };

    private static UnifiedExpandedTarget Target(OrganizerDefinition item) => new(item, 4000, 4000);

    private static void RequireSquareCell(OrganizerDefinition item, double expected)
    {
        var cells = DisplayPlacementService.CalculateItemCellSizeDip(
            item.ManualCanvasBaseWidthDip!.Value - 2 * DisplayPlacementService.ExpandedSideInsetDip,
            item.ManualCanvasBaseHeightDip!.Value - DisplayPlacementService.ExpandedTopInsetDip - DisplayPlacementService.ExpandedBottomInsetDip,
            item.Layout);
        Near(cells.Width, expected, "Rendered cell width");
        Near(cells.Height, expected, "Rendered cell height");
        Near(item.CanvasScale, 1, "Unified canvas scale normalization");
        Near(item.IconContentScale, 1, "Unified icon content scale normalization");
    }

    private static void Near(double actual, double expected, string message) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) < .0001, $"{message}: expected {expected}, got {actual}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
