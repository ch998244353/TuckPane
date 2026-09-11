namespace TuckPane.Models;

// Product preset captured on 2026-09-08. Keep it separate from the model's
// legacy defaults, which are also used when reading and migrating old JSON.
internal static class NewUserDefaults
{
    internal static AppStateV2 CreateState() => new()
    {
        GlobalSettings = new GlobalSettings
        {
            ThemeColorArgb = 0xFFBFD7EA,
            ThemeTransparency = .15,
            SolidThemeOpacity = .72,
            ThemeBlurStrength = 2,
            SolidColorMode = false,
            SettingsThemeColorArgb = 0xFFF5F6F8,
            SettingsThemeTransparency = .93,
            SettingsSolidThemeOpacity = 1,
            SettingsThemeBlurStrength = 1.9,
            SettingsSolidColorMode = false,
            EdgeGlowEnabled = true,
            OrganizerTextColor = OrganizerTextColor.White,
            NoteTheme = NoteTheme.SunYellow,
            Language = AppLanguage.ChineseSimplified,
            PerformanceProfile = PerformanceProfile.HighPerformance,
            ExclusiveExpansion = false,
            CollapseOnOutsideClick = false,
            NoteAlwaysOnTop = true,
            ExpandOnHover = false,
            CollapseOnPointerLeave = true,
            WindowAlignmentEnabled = true,
            RememberExpandedOrganizerPosition = true,
            UseUniformFloatingCompactScale = true,
            UniformFloatingCompactScale = 1.97,
            UseUniformPositionedCompactScale = true,
            UniformPositionedCompactScale = 1.55,
            UseUniformFloatingCompactNameScale = false,
            UniformFloatingCompactNameScale = .8,
            UseUniformPositionedCompactNameScale = false,
            UniformPositionedCompactNameScale = 1,
            ExpandedNameScale = .6,
            HoverExpandDelayMs = 350,
            PointerLeaveCollapseDelayMs = 550,
            StationPointerLeaveCollapseDelayMs = 650,
            StationActivationDistanceDip = 4,
            StationHoverExpandDelayMs = 120,
            StartWithWindows = false,
            DefaultStorageDirectory = null
        }
    };
}
