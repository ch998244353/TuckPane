using System.Numerics;
using System.Text.Json;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class DockModeChecks
{
    internal static async Task RunAsync(string area)
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-dock-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            switch (area)
            {
                case "refinements":
                    await DockRefinementChecks.RunAsync(root);
                    Console.WriteLine("PASS --dock-mode refinements: focused Dock geometry, icon and sizing regressions only; no windows or input automation.");
                    break;
                case "state":
                    await CheckStateAsync(root);
                    Console.WriteLine("PASS --dock-mode state: schema-15 preservation and dock round-trip, independent capacity, runtime/restored containment. Isolated state files only; no windows or input automation.");
                    break;
                case "layout":
                    CheckLayout();
                    Console.WriteLine("PASS --dock-mode layout: horizontal/vertical growth about a fixed centre at 125% DPI, empty placeholder geometry, unclamped long content and shared body/icon hit geometry. Pure calculations; rendered clipping and desktop pass-through remain manual acceptance.");
                    break;
                case "hover":
                    CheckHover();
                    Console.WriteLine("PASS --dock-mode hover: Dock default enablement with both orientations, suspension/disable and fresh-pointer recovery using production policy, geometry and wave helpers. No existing motion suite, GUI or input automation.");
                    break;
                case "theme":
                    await CheckThemeAsync(root);
                    Console.WriteLine("PASS --dock-mode theme: migration to four independent targets, isolated persistence, transparent background/edge policy and parameter recovery. No compositor or desktop rendering.");
                    break;
                case "commands":
                    CheckCommands();
                    Console.WriteLine("PASS --dock-mode commands: category conversion boundary and theme-target routing. Production policy helpers only; menu presentation and file-picker/drag interaction remain manual acceptance.");
                    break;
                default:
                    throw new ArgumentException($"Unknown Dock check: {area}.");
            }
        }
        finally
        {
            await AppLogger.FlushAsync();
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            string fullRoot = Path.GetFullPath(root);
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(fullRoot), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).StartsWith("TuckPane-dock-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the isolated Dock test directory.");
            Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static async Task CheckStateAsync(string root)
    {
        var parent = new OrganizerDefinition
        {
            Name = "原收纳窗", PlacementMode = OrganizerPlacementMode.Floating,
            StorageAbsolutePath = Path.Combine(root, "existing-folder"), StorageOwnedByApp = false,
            Position = Position(311.5, 229.25)
        };
        var child = new OrganizerDefinition
        {
            Name = "原定位入口", PlacementMode = OrganizerPlacementMode.Positioned,
            StorageAbsolutePath = Path.Combine(root, "existing-child"), StorageOwnedByApp = false,
            ContainerOrganizerId = parent.Id, Position = Position(719, 419)
        };
        var station = new OrganizerDefinition
        {
            Name = "原中转站", PlacementMode = OrganizerPlacementMode.Station,
            StorageAbsolutePath = Path.Combine(root, "existing-station"), StorageOwnedByApp = false,
            Position = Position(1470, 430)
        };
        parent.ItemOrder = ["existing.txt", OrganizerContainment.ItemKey(child.Id), "last.lnk"];
        OrganizerDefinition[] originals = [parent, child, station];
        string expected = LegacySnapshot(originals);
        string path = Path.Combine(root, "state.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = 15,
            GlobalSettings = new
            {
                UseUniformFloatingCompactScale = true, UniformFloatingCompactScale = 1.72,
                UseUniformPositionedCompactScale = false, UniformPositionedCompactScale = 1.36
            },
            Organizers = originals.Select(item => new
            {
                item.Id, item.Name, item.PlacementMode, item.Position,
                item.StorageAbsolutePath, item.StorageOwnedByApp, item.ItemOrder,
                ContainerStationId = item.ContainerOrganizerId
            })
        }));
        var store = new StateStore(path);
        AppStateV2 migrated = await store.LoadAsync();
        Require(migrated.SchemaVersion == 16 && LegacySnapshot(migrated.Organizers) == expected,
            "The Dock migration changed existing types, folder bindings, item order, positions or containment.");
        Require(migrated.GlobalSettings.UseUniformFloatingCompactScale &&
                migrated.GlobalSettings.UniformFloatingCompactScale == 1.72 &&
                !migrated.GlobalSettings.UseUniformPositionedCompactScale &&
                migrated.GlobalSettings.UniformPositionedCompactScale == 1.36,
            "Moving the size controls changed their persisted user values.");

        var dock = new OrganizerDefinition
        {
            Name = "纵向 Dock", PlacementMode = OrganizerPlacementMode.Dock,
            DockOrientation = DockOrientation.Vertical, DockIconSizeDip = 84,
            DockCenter = Position(1027.5, 612.25),
            StorageAbsolutePath = Path.Combine(root, "dock-folder"), StorageOwnedByApp = false,
            ItemOrder = ["one.txt", "two.lnk"]
        };
        migrated.Organizers.Add(dock);
        string expectedCenter = JsonSerializer.Serialize(dock.DockCenter);
        await store.SaveAsync(migrated);
        AppStateV2 reloaded = await store.LoadAsync();
        OrganizerDefinition saved = reloaded.Organizers.Single(item => item.Id == dock.Id);
        Require(LegacySnapshot(reloaded.Organizers.Where(item => item.Id != dock.Id)) == expected &&
                saved.PlacementMode == OrganizerPlacementMode.Dock &&
                saved.DockOrientation == DockOrientation.Vertical && saved.DockIconSizeDip == 84 &&
                JsonSerializer.Serialize(saved.DockCenter) == expectedCenter &&
                saved.StorageAbsolutePath == dock.StorageAbsolutePath && !saved.StorageOwnedByApp &&
                saved.ItemOrder.SequenceEqual(["one.txt", "two.lnk"]),
            "Saving and reloading a Dock changed its independent geometry/storage settings or existing organizers.");

        CheckCapacity();
        CheckContainment();
    }

    private static void CheckCapacity()
    {
        var state = new AppStateV2
        {
            Organizers = Enumerable.Range(0, 24)
                .Select(index => new OrganizerDefinition { Name = $"Dock {index}", PlacementMode = OrganizerPlacementMode.Dock })
                .ToList()
        };
        state.Organizers.Add(new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Floating });
        Require(OrganizerKinds.CanCreate(OrganizerPlacementMode.Floating, state.Organizers),
            "Docks consumed the regular organizer creation allowance.");
        state.Organizers.AddRange(Enumerable.Range(1, 14)
            .Select(_ => new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Positioned }));
        int originalCount = state.Organizers.Count;
        StateStore.Normalize(state);
        Require(state.Organizers.Count == originalCount &&
                OrganizerKinds.CanCreate(OrganizerPlacementMode.Floating, state.Organizers) &&
                OrganizerKinds.CanCreate(OrganizerPlacementMode.Dock, state.Organizers),
            "Normalization or creation limits mixed Dock capacity with the regular organizer allowance.");
    }

    private static void CheckContainment()
    {
        var dock = new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Dock };
        var child = new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Floating };
        var other = new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Floating };
        List<OrganizerDefinition> organizers = [dock, child, other];
        Require(OrganizerContainment.TryMove(organizers, child.Id, dock.Id, 0).Succeeded &&
                child.ContainerOrganizerId == dock.Id &&
                dock.ItemOrder.SequenceEqual([OrganizerContainment.ItemKey(child.Id)]),
            "A Dock did not accept an eligible ordinary organizer entry through the shared containment service.");
        string beforeRejectedMove = JsonSerializer.Serialize(organizers);
        Require(!OrganizerContainment.TryMove(organizers, dock.Id, other.Id, 0).Succeeded &&
                JsonSerializer.Serialize(organizers) == beforeRejectedMove,
            "A Dock was accepted as a contained source or a rejected move mutated ordering/ownership.");

        dock.ContainerOrganizerId = other.Id;
        other.ItemOrder.Add(OrganizerContainment.ItemKey(dock.Id));
        StateStore.Normalize(new AppStateV2 { Organizers = organizers });
        Require(dock.ContainerOrganizerId is null &&
                !other.ItemOrder.Contains(OrganizerContainment.ItemKey(dock.Id)) &&
                child.ContainerOrganizerId == dock.Id,
            "Restoring persisted containment accepted a Dock source or detached its valid ordinary child.");
    }

    private static void CheckLayout()
    {
        DockGeometry horizontal = DockLayoutMath.Calculate(4, 64, DockOrientation.Horizontal);
        DockGeometry vertical = DockLayoutMath.Calculate(4, 64, DockOrientation.Vertical);
        Require(horizontal.Width == vertical.Height && horizontal.Height == vertical.Width,
            "Changing Dock orientation changed its item sizes or length instead of exchanging axes.");
        foreach (DockGeometry geometry in new[] { horizontal, vertical })
        {
            bool row = geometry.Orientation == DockOrientation.Horizontal;
            DockGeometry longer = DockLayoutMath.Calculate(5, geometry.IconSize, geometry.Orientation);
            Near(row ? longer.Width - geometry.Width : longer.Height - geometry.Height,
                geometry.Pitch, "Adding one item must add one pitch along the Dock axis");
            Near(row ? longer.Height : longer.Width, row ? geometry.Height : geometry.Width,
                "Adding one item must not introduce a new row or column");
            Vector2 first = geometry.ItemCenter(0);
            Vector2 last = geometry.ItemCenter(geometry.Count - 1);
            Vector2 axisStep = row ? new Vector2(1, 0) : new Vector2(0, 1);
            Require(geometry.InsertionIndex(first - axisStep) == 0 && geometry.InsertionIndex(first + axisStep) == 1,
                "Dropping across an icon centre must switch from inserting before to inserting after it.");
            Near(row ? first.Y : first.X, row ? last.Y : last.X, "All Dock items must share one cross-axis centre");
            Near((first.X + last.X) / 2, geometry.Width / 2, "Item distribution must centre horizontally");
            Near((first.Y + last.Y) / 2, geometry.Height / 2, "Item distribution must centre vertically");
            Require(geometry.Contains((first + geometry.ItemCenter(1)) / 2) &&
                    !geometry.Contains(Vector2.Zero),
                "The transparent content gap must be interactive while the outside rounded corner remains excluded.");
            float half = (float)(geometry.IconSize * OrganizerHoverWave.MaximumScale / 2 - .01);
            Vector2 raisedCorner = first - new Vector2(half, half);
            Require(geometry.ContainsIcon(raisedCorner, 0, OrganizerHoverWave.MaximumScale) &&
                    !geometry.ContainsIcon(raisedCorner, 0, 1),
                "A magnified first icon's visible corner was lost outside the base icon bounds.");
            Require(geometry.InsertionIndex(first - new Vector2((float)geometry.Pitch)) == 0 &&
                    geometry.InsertionIndex(last + new Vector2((float)geometry.Pitch)) == geometry.Count,
                "Dock insertion did not accept both end positions along the chosen axis.");
            var center = new Vector2(-713.2f, 429.2f);
            const double dpiScale = 1.25;
            NativeMethods.RECT before = DockLayoutMath.CalculateBounds(geometry, center, dpiScale);
            NativeMethods.RECT after = DockLayoutMath.CalculateBounds(longer, center, dpiScale);
            Require(Math.Abs((before.Left + before.Right) / 2d - center.X * dpiScale) <= .501 &&
                    Math.Abs((before.Top + before.Bottom) / 2d - center.Y * dpiScale) <= .501 &&
                    before.Left < 0 && after.Left < 0,
                "Fractional-DPI placement lost its saved centre or clamped a negative display coordinate.");
            Near(before.Left + before.Right, after.Left + after.Right,
                "Adding an item must preserve the horizontal pixel centre");
            Near(before.Top + before.Bottom, after.Top + after.Bottom,
                "Adding an item must preserve the vertical pixel centre");
            Near(row ? before.Left - after.Left : before.Top - after.Top, geometry.Pitch * dpiScale / 2,
                "Adding an item must grow equally toward both ends at 125% DPI");
        }
        DockGeometry empty = DockLayoutMath.Calculate(0, 64, DockOrientation.Horizontal);
        DockGeometry one = DockLayoutMath.Calculate(1, 64, DockOrientation.Horizontal);
        Require(empty.Count == 0 && empty.Width == one.Width && empty.Height == one.Height &&
                empty.Contains(empty.ItemCenter(0)) && empty.InsertionIndex(empty.ItemCenter(0)) == 0,
            "The empty Dock must retain a usable temporary slot without adding a real item or order index.");
        DockGeometry many = DockLayoutMath.Calculate(80, 64, DockOrientation.Horizontal);
        Require(many.Width > 1920 && many.Height == horizontal.Height && many.IconSize == horizontal.IconSize &&
                many.ItemCenter(79).X > 1920,
            "Long Dock content was clamped, wrapped or shrunk to a screen-sized viewport.");
        NativeMethods.RECT overflow = DockLayoutMath.CalculateBounds(many, new Vector2(960, 540), 1);
        Require(overflow.Left < 0 && overflow.Right > 1920 && overflow.Width == many.Width,
            "Positioning a long Dock must retain both off-screen ends without changing content size.");
    }

    private static void CheckHover()
    {
        bool enabled = OrganizerHoverWave.IsEnabledFor(OrganizerPlacementMode.Dock, compactList: false, iconEnabled: false);
        var wave = new OrganizerHoverWave();
        wave.SetAvailability(enabled, suspended: false);
        foreach (DockOrientation orientation in new[] { DockOrientation.Horizontal, DockOrientation.Vertical })
        {
            DockGeometry geometry = DockLayoutMath.Calculate(3, 64, orientation);
            Vector2 first = geometry.ItemCenter(0);
            var pitch = new Vector2((float)geometry.Pitch);
            Require(wave.MovePointer(first) &&
                    wave.GetTargetScale(first, pitch, compactList: false) >
                    wave.GetTargetScale(geometry.ItemCenter(1), pitch, compactList: false) &&
                    wave.GetTargetScale(geometry.ItemCenter(1), pitch, compactList: false) > 1,
                "A default Dock did not enable distance-based magnification along its chosen axis.");
            wave.SetAvailability(enabled, suspended: true);
            Require(!wave.HasPointer && !wave.MovePointer(first) &&
                    wave.GetTargetScale(first, pitch, compactList: false) == 1,
                "Suspending a Dock interaction left magnification or accepted fresh pointer input.");
            wave.SetAvailability(enabled, suspended: false);
            Require(!wave.HasPointer && wave.MovePointer(first),
                "Resuming a Dock must accept a fresh pointer without reviving stale hover state.");
        }
        wave.SetAvailability(enabled: false, suspended: false);
        Require(!wave.HasPointer && !wave.MovePointer(Vector2.Zero),
            "An outer animation/performance disable must still override the Dock's default hover policy.");
    }

    private static async Task CheckThemeAsync(string root)
    {
        ThemeValues organizer = new(0xFF243648, .42, 1.6, true, .67);
        ThemeValues settingsTheme = new(0xFF786858, .83, .8, false, .54);
        string path = Path.Combine(root, "themes.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = 15,
            GlobalSettings = new
            {
                ThemeColorArgb = organizer.ColorArgb, ThemeTransparency = organizer.Transparency,
                ThemeBlurStrength = organizer.BlurStrength, SolidColorMode = organizer.SolidColorMode,
                SolidThemeOpacity = organizer.SolidOpacity,
                SettingsThemeColorArgb = settingsTheme.ColorArgb, SettingsThemeTransparency = settingsTheme.Transparency,
                SettingsThemeBlurStrength = settingsTheme.BlurStrength, SettingsSolidColorMode = settingsTheme.SolidColorMode,
                SettingsSolidThemeOpacity = settingsTheme.SolidOpacity
            },
            Organizers = Array.Empty<object>()
        }));
        var store = new StateStore(path);
        AppStateV2 state = await store.LoadAsync();
        GlobalSettings settings = state.GlobalSettings;
        Require(settings.GetTheme(ThemeTarget.Organizer) == organizer &&
                settings.GetTheme(ThemeTarget.Settings) == settingsTheme &&
                settings.GetTheme(ThemeTarget.Station) == organizer &&
                settings.GetTheme(ThemeTarget.Dock) == organizer,
            "Migration must copy every old organizer theme parameter into the two new targets without altering either old target.");

        ThemeValues station = new(0xFF876543, .31, .4, false, .28);
        ThemeValues dock = new(0xFF345678, .57, 1.2, true, .73);
        settings.SetTheme(ThemeTarget.Station, station);
        settings.SetTheme(ThemeTarget.Dock, dock);
        ThemeValues transparent = GlobalSettings.ResolveThemeUpdate(dock,
            dock.ColorArgb, dock.Transparency, dock.BlurStrength, dock.SolidColorMode, fullyTransparent: true);
        settings.SetTheme(ThemeTarget.Dock, transparent);
        await store.SaveAsync(state);
        settings = (await store.LoadAsync()).GlobalSettings;
        Require(settings.GetTheme(ThemeTarget.Organizer) == organizer &&
                settings.GetTheme(ThemeTarget.Settings) == settingsTheme &&
                settings.GetTheme(ThemeTarget.Station) == station &&
                settings.GetTheme(ThemeTarget.Dock) == transparent &&
                (transparent with { FullyTransparent = false }) == dock,
            "Editing and reloading Dock transparency changed another target or discarded the Dock's glass/solid parameters.");
        ThemeValues restored = GlobalSettings.ResolveThemeUpdate(settings.GetTheme(ThemeTarget.Dock),
            dock.ColorArgb, dock.Transparency, dock.BlurStrength, dock.SolidColorMode, fullyTransparent: false);
        Require(restored == dock, "Leaving fully transparent mode did not recover the prior Dock theme parameters.");
        foreach (bool effects in new[] { true, false })
        foreach (bool solid in new[] { true, false })
        {
            ThemeValues active = transparent with { SolidColorMode = solid };
            ThemeCompositionPlan plan = ThemePalette.BuildCompositionPlan(active, effects);
            Require(plan.SurfaceOpacity == 0 && plan.TintOpacity == 0 && plan.BlurAmount == 0 &&
                    plan.LuminosityOpacity == 0 && plan.HighlightOpacity == 0 &&
                    !plan.RequiresHostBackdrop && !plan.UsesGaussianBlur &&
                    ThemeEffectGraph.ResolveBranch(plan, hostBackdropAvailable: true) == ThemeBackdropBranch.Transparent &&
                    ThemePalette.EdgeOpacity(active) == 0,
                "Fully transparent Dock retained a painted surface, blur, luminosity, highlight or edge in a material/effects branch.");
        }
        Require(ThemePalette.BuildCompositionPlan(restored, useEffects: true).SurfaceOpacity > 0 &&
                ThemePalette.EdgeOpacity(restored) > 0,
            "Returning to the prior Dock theme did not restore its background and edge policy.");
    }

    private static void CheckCommands()
    {
        OrganizerPlacementMode[] modes =
            [OrganizerPlacementMode.Floating, OrganizerPlacementMode.Positioned, OrganizerPlacementMode.Station, OrganizerPlacementMode.Dock];
        (OrganizerPlacementMode Source, OrganizerPlacementMode Target)[] allowed =
        [
            (OrganizerPlacementMode.Floating, OrganizerPlacementMode.Floating),
            (OrganizerPlacementMode.Floating, OrganizerPlacementMode.Positioned),
            (OrganizerPlacementMode.Positioned, OrganizerPlacementMode.Floating),
            (OrganizerPlacementMode.Positioned, OrganizerPlacementMode.Positioned),
            (OrganizerPlacementMode.Station, OrganizerPlacementMode.Station),
            (OrganizerPlacementMode.Dock, OrganizerPlacementMode.Dock)
        ];
        foreach (OrganizerPlacementMode source in modes)
        foreach (OrganizerPlacementMode target in modes)
        {
            Require(OrganizerKinds.CanChange(source, target) == allowed.Contains((source, target)),
                $"The category boundary allowed a forbidden conversion or blocked a regular placement switch: {source} -> {target}.");
        }
        Require(OrganizerKinds.ThemeFor(OrganizerPlacementMode.Floating) == ThemeTarget.Organizer &&
                OrganizerKinds.ThemeFor(OrganizerPlacementMode.Positioned) == ThemeTarget.Organizer &&
                OrganizerKinds.ThemeFor(OrganizerPlacementMode.Station) == ThemeTarget.Station &&
                OrganizerKinds.ThemeFor(OrganizerPlacementMode.Dock) == ThemeTarget.Dock,
            "The split management categories do not resolve to their corresponding independent themes.");
    }

    private static WidgetPosition Position(double x, double y) => new()
    {
        MonitorDevice = "test-display", XDip = x, YDip = y,
        SavedWorkAreaWidthDip = 1536, SavedWorkAreaHeightDip = 864
    };

    private static string LegacySnapshot(IEnumerable<OrganizerDefinition> organizers) =>
        JsonSerializer.Serialize(organizers.Select(item => new
        {
            item.Id, item.Name, item.PlacementMode, item.Position,
            item.StorageAbsolutePath, item.StorageOwnedByApp, item.ItemOrder, item.ContainerOrganizerId
        }));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, string message) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) < .001,
            $"{message}: expected {expected}, got {actual}.");
}
