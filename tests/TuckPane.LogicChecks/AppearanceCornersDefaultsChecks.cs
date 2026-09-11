using System.Text.Json;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class AppearanceCornersDefaultsChecks
{
    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-appearance-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            await CheckStateAsync(root);
            CheckGeometry();
            Console.WriteLine("PASS --appearance-corners-defaults: new-user snapshot, existing/backup preservation, round-trip, damaged-state fallback; 6 DPI/compact-size combinations, 1 settings-window rectangle and 3 distinct stroke configurations. No UI automation; visual acceptance remains manual.");
        }
        finally
        {
            // Corrupt-state recovery logs through AppLogger; drain its isolated writes first.
            await AppLogger.FlushAsync();
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckStateAsync(string root)
    {
        var expected = new GlobalSettings
        {
            ThemeColorArgb = 0xFFBFD7EA, ThemeTransparency = .15, ThemeBlurStrength = 2,
            SolidColorMode = false, SolidThemeOpacity = .72,
            SettingsThemeColorArgb = 0xFFF5F6F8, SettingsThemeTransparency = .93,
            SettingsThemeBlurStrength = 1.9, SettingsSolidColorMode = false, SettingsSolidThemeOpacity = 1,
            EdgeGlowEnabled = true, OrganizerTextColor = OrganizerTextColor.White,
            NoteTheme = NoteTheme.SunYellow, Language = AppLanguage.ChineseSimplified,
            PerformanceProfile = PerformanceProfile.HighPerformance, NoteAlwaysOnTop = true,
            ExclusiveExpansion = false, CollapseOnOutsideClick = false, ExpandOnHover = false,
            CollapseOnPointerLeave = true, WindowAlignmentEnabled = true, RememberExpandedOrganizerPosition = true,
            UseUniformFloatingCompactScale = true, UniformFloatingCompactScale = 1.97,
            UseUniformPositionedCompactScale = true, UniformPositionedCompactScale = 1.55,
            UseUniformFloatingCompactNameScale = false, UniformFloatingCompactNameScale = .8,
            UseUniformPositionedCompactNameScale = false, UniformPositionedCompactNameScale = 1,
            ExpandedNameScale = .6, HoverExpandDelayMs = 350, PointerLeaveCollapseDelayMs = 550,
            StationPointerLeaveCollapseDelayMs = 650, StationActivationDistanceDip = 4,
            StationHoverExpandDelayMs = 120, StartWithWindows = false, DefaultStorageDirectory = null
        };
        string path = Path.Combine(root, "state.json");
        var store = new StateStore(path);
        AppStateV2 fresh = await store.LoadAsync();
        Same(expected, fresh.GlobalSettings, "Fresh installation must use the complete approved snapshot.");
        Require(fresh.SchemaVersion == 15 && fresh.ConsolePlacement is null && fresh.Organizers.Count == 0,
            "Fresh state must not copy personal windows, placement or files, or change the schema.");
        string freshSnapshot = JsonSerializer.Serialize(fresh);
        await store.SaveAsync(fresh);
        SameSnapshot(freshSnapshot, await new StateStore(path).LoadAsync(), "Fresh defaults must survive saving and reloading.");

        // Distinct current-schema values expose accidental application of the fresh-user preset.
        var existing = new AppStateV2
        {
            GlobalSettings = new GlobalSettings
            {
                ThemeColorArgb = 0xFF123456, ThemeTransparency = .47, ThemeBlurStrength = .8,
                SettingsThemeColorArgb = 0xFF654321, SettingsThemeTransparency = .61,
                SettingsThemeBlurStrength = .6, SolidColorMode = true, SolidThemeOpacity = .44,
                SettingsSolidColorMode = true, SettingsSolidThemeOpacity = .57,
                Language = AppLanguage.English, StartWithWindows = true,
                DefaultStorageDirectory = Path.Combine(root, "personal-storage")
            }
        };
        // SaveAsync normalizes its input in place; freeze expectations before saving.
        string existingSnapshot = JsonSerializer.Serialize(existing);
        await store.SaveAsync(existing); // The backup contains the different fresh-user state.
        SameSnapshot(existingSnapshot, await store.LoadAsync(), "Valid primary state must win over its backup and remain unchanged.");

        string backupPath = Path.Combine(root, "backup-only.json");
        await File.WriteAllTextAsync(backupPath + ".bak", existingSnapshot);
        var backupStore = new StateStore(backupPath);
        SameSnapshot(existingSnapshot, await backupStore.LoadAsync(), "An existing backup alone is not a new installation.");
        await File.WriteAllTextAsync(backupPath, "{damaged");
        AppStateV2 recovered = await backupStore.LoadAsync();
        SameSnapshot(existingSnapshot, recovered, "Valid backup must recover a damaged primary without applying fresh defaults.");
        await backupStore.SaveAsync(recovered);
        SameSnapshot(existingSnapshot, await backupStore.LoadAsync(), "Recovered settings must survive saving and reloading.");

        string damagedPath = Path.Combine(root, "damaged-only.json");
        await File.WriteAllTextAsync(damagedPath + ".bak", "{damaged");
        Same(StateStore.Normalize(new AppStateV2()), await new StateStore(damagedPath).LoadAsync(),
            "An unreadable existing backup must retain legacy fallback rather than apply the new-user preset.");
    }

    private static void CheckGeometry()
    {
        foreach (double dpi in new[] { 1d, 1.25, 1.5 })
        foreach (double scale in new[] { 1.97, 1.55 })
        {
            CheckSurfaceGeometry(39 * scale, 39 * scale, 8 * scale, dpi);
        }

        CheckSurfaceGeometry(960, 680, 18, 1.25);
    }

    private static void CheckSurfaceGeometry(double width, double height, double radius, double dpi)
    {
        RoundedSurfaceGeometry surface = RoundedSurfaceGeometry.Create(width, height, radius, dpi);
        Near(surface.Width, width, "Do not round the arranged background width a second time.");
        Near(surface.Height, height, "Content and background must retain the same arranged height.");
        Require(surface.Radius > 0 && surface.Radius <= Math.Min(surface.Width, surface.Height) / 2,
            "Corner radius must stay legal at every tested window size.");
        Near(surface.Radius * dpi, Math.Round(surface.Radius * dpi), "Align the shared radius once at the current DPI.");
        foreach ((float thickness, float edgeInset) in new[] { (1.25f, 0f), (.75f, 1.5f), (2.75f, 0f) })
        {
            RoundedStrokeGeometry stroke = surface.InsetStroke(thickness, edgeInset);
            Near(stroke.Offset - thickness / 2, edgeInset, "Stroke must not protrude past the outer left/top edge.");
            Near(stroke.Offset + stroke.Width + thickness / 2, surface.Width - edgeInset,
                "Right edge must match the shared contour without asymmetric rounding.");
            Near(stroke.Offset + stroke.Height + thickness / 2, surface.Height - edgeInset,
                "Bottom edge must match the shared contour without asymmetric rounding.");
            Near(stroke.Offset + stroke.Radius, surface.Radius, "Top/left corner centre must stay concentric.");
            Near(stroke.Offset + stroke.Width - stroke.Radius, surface.Width - surface.Radius,
                "Right corner centre must stay concentric.");
            Near(stroke.Offset + stroke.Height - stroke.Radius, surface.Height - surface.Radius,
                "Bottom corner centre must stay concentric.");
            Near(stroke.Radius + thickness / 2, surface.Radius - edgeInset,
                "The rounded stroke exterior must follow the same outer arc, including the 2.75 DIP glow.");
        }
    }

    private static void Same<T>(T expected, T actual, string message) =>
        SameSnapshot(JsonSerializer.Serialize(expected), actual, message);

    private static void SameSnapshot<T>(string expectedJson, T actual, string message) =>
        Require(expectedJson == JsonSerializer.Serialize(actual), message);

    private static void Near(double actual, double expected, string message) =>
        Require(Math.Abs(actual - expected) < .0001, $"{message} Expected {expected}, got {actual}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
