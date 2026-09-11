using System.Numerics;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class SettingsDockAdjustmentChecks
{
    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-settings-dock-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        try
        {
            CheckMenus();
            await CheckSavedHoverSettingsAsync(root);
            await CheckSliderSaveOrderingAsync();
            CheckVerticalGeometry();
            Console.WriteLine("PASS --settings-dock-adjustments: 3 focused groups (menu filtering; isolated state/hover and slider save ordering; vertical Dock geometry at 125% DPI). No GUI or input automation; slider feel, visual reset and tooltip placement require manual acceptance.");
        }
        finally
        {
            await AppLogger.FlushAsync();
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckMenus()
    {
        var settings = new GlobalSettings();
        Require(OrganizerMenuVisibility.VisibleActions(settings, OrganizerPlacementMode.Floating)
                .SequenceEqual(OrganizerMenuVisibility.Actions),
            "Missing menu choices must retain every eligible operation in its existing order.");
        foreach (string key in new[] { "ContextRename", "ContextDuplicate", "ContextSwitchPlacement",
                     "ContextSwitchContent", "ContextSwitchExpansion", "ContextHideName" })
        {
            Require(!OrganizerMenuVisibility.VisibleActions(settings, OrganizerPlacementMode.Station).Contains(key),
                $"Station must retain its original restriction for {key}.");
            Require(OrganizerMenuVisibility.VisibleActions(settings, OrganizerPlacementMode.Dock).Contains(key)
                    == (key is "ContextRename" or "ContextDuplicate"),
                $"Dock must retain its original restriction for {key}.");
        }

        settings.OrganizerMenuVisibility = OrganizerMenuVisibility.Actions.ToDictionary(key => key, _ => false);
        foreach (OrganizerPlacementMode mode in Enum.GetValues<OrganizerPlacementMode>())
        {
            string[] empty = OrganizerMenuVisibility.VisibleActions(settings, mode);
            Require(empty.Length == 0 && !OrganizerMenuVisibility.ShowSeparator(empty),
                "Disabling every choice must leave no menu content or separator in any mode.");
        }
        settings.OrganizerMenuVisibility["ContextAddItem"] = true;
        string[] firstGroup = OrganizerMenuVisibility.VisibleActions(settings, OrganizerPlacementMode.Floating);
        Require(firstGroup.SequenceEqual(new[] { "ContextAddItem" }) && !OrganizerMenuVisibility.ShowSeparator(firstGroup),
            "A lone first-group action must not leave a trailing separator.");
        settings.OrganizerMenuVisibility["ContextManage"] = true;
        string[] mixed = OrganizerMenuVisibility.VisibleActions(settings, OrganizerPlacementMode.Floating);
        Require(mixed.SequenceEqual(new[] { "ContextAddItem", "ContextManage" }) && OrganizerMenuVisibility.ShowSeparator(mixed),
            "Independently enabled actions in both groups must retain their separator and order.");
        settings.OrganizerMenuVisibility["ContextAddItem"] = false;
        string[] secondGroup = OrganizerMenuVisibility.VisibleActions(settings, OrganizerPlacementMode.Floating);
        Require(secondGroup.SequenceEqual(new[] { "ContextManage" }) && !OrganizerMenuVisibility.ShowSeparator(secondGroup),
            "A lone second-group action must not leave a leading separator.");
    }

    private static async Task CheckSavedHoverSettingsAsync(string root)
    {
        string path = Path.Combine(root, "state.json");
        await File.WriteAllTextAsync(path,
            """{"SchemaVersion":16,"GlobalSettings":{"ThemeTransparency":0.47},"Organizers":[]}""");
        AppStateV2 state = await new StateStore(path).LoadAsync();
        Require(state.SchemaVersion == 16 && state.GlobalSettings.CompactHoverMagnificationEnabled &&
                state.GlobalSettings.DockHoverMagnificationEnabled &&
                OrganizerMenuVisibility.Actions.All(key => OrganizerMenuVisibility.IsEnabled(state.GlobalSettings, key)) &&
                Math.Abs(state.GlobalSettings.ThemeTransparency - .47) < .001,
            "Existing Schema 16 without new fields must retain personal settings and default the new choices on.");

        foreach (bool compactEnabled in new[] { false, true })
        {
            state.GlobalSettings.CompactHoverMagnificationEnabled = compactEnabled;
            state.GlobalSettings.DockHoverMagnificationEnabled = !compactEnabled;
            state.GlobalSettings.OrganizerMenuVisibility["ContextManage"] = false;
            await new StateStore(path).SaveAsync(state);
            state = await new StateStore(path).LoadAsync();
            GlobalSettings settings = state.GlobalSettings;
            Require(settings.CompactHoverMagnificationEnabled == compactEnabled &&
                    settings.DockHoverMagnificationEnabled == !compactEnabled &&
                    !OrganizerMenuVisibility.IsEnabled(settings, "ContextManage") &&
                    OrganizerMenuVisibility.IsEnabled(settings, "ContextAddItem") && state.SchemaVersion == 16,
                "Independent choices and a sparse menu dictionary must survive a real save/reload without migration.");

            foreach (OrganizerPlacementMode mode in new[] { OrganizerPlacementMode.Floating, OrganizerPlacementMode.Dock })
            {
                bool expected = mode == OrganizerPlacementMode.Dock ? !compactEnabled : compactEnabled;
                bool enabled = OrganizerHoverWave.IsEnabledFor(mode, compactList: true, iconEnabled: false,
                    settings.CompactHoverMagnificationEnabled, settings.DockHoverMagnificationEnabled);
                var wave = new OrganizerHoverWave();
                var center = new Vector2(48, 82);
                var pitch = new Vector2(80, 80);
                wave.SetAvailability(enabled, suspended: false);
                Require(wave.MovePointer(center) == expected &&
                        (wave.GetTargetScale(center, pitch, compactList: true) > 1) == expected,
                    "Each saved switch must independently determine the actual wave target, even when Dock uses compact content.");
                wave.SetAvailability(enabled: false, suspended: false);
                Require(!wave.HasPointer && wave.GetTargetScale(center, pitch, compactList: true) == 1,
                    "Disabling an active wave must discard its pointer and restore a neutral target immediately.");
                wave.SetAvailability(enabled: true, suspended: true);
                Require(!wave.MovePointer(center) && wave.GetTargetScale(center, pitch, compactList: true) == 1,
                    "The new switches must not bypass existing animation suspension.");
            }
            Require(!OrganizerHoverWave.IsEnabledFor(OrganizerPlacementMode.Floating,
                    compactList: false, iconEnabled: false, settings.CompactHoverMagnificationEnabled,
                    settings.DockHoverMagnificationEnabled),
                "The new switches must not enable ordinary icon-mode animation.");
        }
    }

    private static async Task CheckSliderSaveOrderingAsync()
    {
        var sequence = new SliderSaveSequence();
        int live = 20, committed = 10, rollbacks = 0, reports = 0;
        void Rollback() { live = committed; rollbacks++; }
        void Report(Exception _) => reports++;
        void Commit(int value) => committed = value;

        // Hold each production save await explicitly: no timers, disk races or input simulation.
        foreach (bool oldSaveFails in new[] { true, false })
        {
            var oldWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var newWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var newWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int attempts = 0;
            Task Save(Action snapshotCaptured)
            {
                snapshotCaptured();
                if (++attempts == 1) return oldWrite.Task;
                newWriteStarted.SetResult();
                return newWrite.Task;
            }
            int previousCommitted = committed;
            int firstValue = oldSaveFails ? 20 : 50;
            live = firstValue;
            sequence.Changed();
            Task<bool> saving = sequence.SaveAsync(() => live, Save, Commit, Rollback, Report);
            live = firstValue + 10;
            sequence.Changed();
            if (oldSaveFails) oldWrite.SetException(new IOException("Controlled stale write failure"));
            else oldWrite.SetResult();
            await newWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(attempts == 2 && rollbacks == 0 && reports == 0 && live == firstValue + 10 &&
                    committed == (oldSaveFails ? previousCommitted : firstValue),
                "An old save must not roll back newer input or commit the newer value before its own write completes.");
            newWrite.SetResult();
            Require(await saving && committed == firstValue + 10,
                "A change made during the old save must receive its own write and commit without another debounce.");
        }

        live = 70;
        sequence.Changed();
        bool success = await sequence.SaveAsync(() => live,
            snapshotCaptured =>
            {
                snapshotCaptured();
                return Task.FromException(new IOException("Controlled current write failure"));
            }, Commit, Rollback, Report);
        Require(!success && live == 60 && committed == 60 && rollbacks == 1 && reports == 1,
            "Only a current failed save may restore the latest committed value and report its failure once.");

        // A different settings group can hold StateStore's gate before this save captures JSON.
        var gatedSequence = new SliderSaveSequence();
        var storeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int gateLive = 20, gateCommitted = 10, gateAttempts = 0, gateRollbacks = 0, gateReports = 0;
        async Task GatedSave(Action snapshotCaptured)
        {
            gateAttempts++;
            await storeGate.Task;
            snapshotCaptured();
        }
        void GateCommit(int value) => gateCommitted = value;
        void GateRollback() { gateLive = gateCommitted; gateRollbacks++; }
        void GateReport(Exception _) => gateReports++;
        gatedSequence.Changed();
        Task<bool> waiting = gatedSequence.SaveAsync(() => gateLive, GatedSave, GateCommit, GateRollback, GateReport);
        gateLive = 30;
        gatedSequence.Changed();
        storeGate.SetResult();
        Require(await waiting && gateCommitted == 30 && gateAttempts == 1 && gateRollbacks == 0 && gateReports == 0,
            "A save waiting for StateStore must capture the latest value and revision at the actual write, without writing it twice.");
        gateLive = 40;
        gatedSequence.Changed();
        bool gateSuccess = await gatedSequence.SaveAsync(() => gateLive,
            snapshotCaptured =>
            {
                snapshotCaptured();
                return Task.FromException(new IOException("Controlled post-gate write failure"));
            }, GateCommit, GateRollback, GateReport);
        Require(!gateSuccess && gateLive == 30 && gateCommitted == 30 && gateRollbacks == 1 && gateReports == 1,
            "A later failed edit must restore the value actually written after gate admission, not the pre-gate snapshot.");
    }

    private static void CheckVerticalGeometry()
    {
        DockGeometry geometry = DockLayoutMath.Calculate(3, 64, DockOrientation.Vertical);
        Require(geometry.Width == 96 && geometry.CrossInset == 16,
            "A vertical Dock with 64 DIP icons must provide 16 DIP of room on each side.");
        for (int index = 0; index < geometry.Count; index++)
        {
            Vector2 center = geometry.ItemCenter(index);
            Require(center.X == geometry.Width / 2 &&
                    center.X - geometry.IconSize / 2 == geometry.HorizontalInset,
                "Every icon must remain centered within the widened surface.");
            Require(geometry.Contains(new Vector2(88, center.Y)) &&
                    !geometry.Contains(new Vector2(97, center.Y)) &&
                    geometry.ContainsIcon(center, index, 1) &&
                    !geometry.ContainsIcon(new Vector2(88, center.Y), index, 1),
                "New right-side padding must accept surface input without enlarging the baseline icon hit area.");
        }
        Vector2 windowCenter = new(-713.2f, 429.2f);
        NativeMethods.RECT bounds = DockLayoutMath.CalculateBounds(geometry, windowCenter, 1.25);
        Require(bounds.Width == 120 &&
                Math.Abs((bounds.Left + bounds.Right) / 2d - windowCenter.X * 1.25) <= .5 &&
                Math.Abs((bounds.Top + bounds.Bottom) / 2d - windowCenter.Y * 1.25) <= .5,
            "At 125% DPI the widened native bounds must stay centered within pixel rounding.");
        DockGeometry horizontal = DockLayoutMath.Calculate(3, 64, DockOrientation.Horizontal);
        Require(horizontal.Width == 274 && horizontal.Height == 96 && horizontal.Pitch == geometry.Pitch,
            "Vertical widening must preserve horizontal geometry and item spacing.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
