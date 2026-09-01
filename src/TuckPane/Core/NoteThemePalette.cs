namespace TuckPane.Core;

using Microsoft.UI;
using TuckPane.Models;
using Windows.UI;

internal sealed record NoteThemeColors(
    NoteTheme Theme,
    string NameKey,
    string Surface,
    string Editor,
    string Accent,
    string Border,
    string Text,
    string Muted)
{
    internal IReadOnlyDictionary<string, string> Css => new Dictionary<string, string>
    {
        ["surface"] = Surface,
        ["editor"] = Editor,
        ["accent"] = Accent,
        ["border"] = Border,
        ["text"] = Text,
        ["muted"] = Muted
    };

    internal Color SurfaceColor => Parse(Surface);
    internal Color EditorColor => Parse(Editor);
    internal Color AccentColor => Parse(Accent);
    internal Color BorderColor => Parse(Border);
    internal Color TextColor => Parse(Text);
    internal Color MutedColor => Parse(Muted);

    private static Color Parse(string value) => ColorHelper.FromArgb(
        255,
        Convert.ToByte(value.Substring(1, 2), 16),
        Convert.ToByte(value.Substring(3, 2), 16),
        Convert.ToByte(value.Substring(5, 2), 16));
}

internal static class NoteThemePalette
{
    internal static IReadOnlyList<NoteThemeColors> All { get; } =
    [
        new(NoteTheme.RainBlue, "NoteThemeRainBlue", "#40505B", "#4B5D68", "#A8C2CF", "#A1B2BB", "#F3F6F7", "#D0D9DD"),
        new(NoteTheme.Graphite, "NoteThemeGraphite", "#35383B", "#42464A", "#C9AE7B", "#8D9296", "#F4F1EB", "#C2BDB4"),
        new(NoteTheme.SunYellow, "NoteThemeSunYellow", "#D2C79C", "#E9DFB7", "#765A22", "#897A4A", "#3A3222", "#6C6248"),
        new(NoteTheme.InkBlack, "NoteThemeInkBlack", "#161A1D", "#23282C", "#9EABB3", "#68747C", "#F2F4F5", "#ADB5BA"),
        new(NoteTheme.TransparentGlass, "NoteThemeTransparentGlass", "#3B4954", "#566A77", "#B3CAD5", "#B1C3CB", "#F7FAFB", "#E1E9ED"),
        new(NoteTheme.CloudPaper, "NoteThemeCloudPaper", "#D7DEE1", "#E8EDEF", "#587889", "#788A93", "#29353B", "#5A6970"),
        new(NoteTheme.WheatPaper, "NoteThemeWheatPaper", "#D5C5B1", "#E9DDCB", "#85664E", "#90775E", "#332A22", "#6A5B4F")
    ];

    internal static NoteThemeColors Get(NoteTheme theme) =>
        All.FirstOrDefault(colors => colors.Theme == theme) ?? All[0];
}
