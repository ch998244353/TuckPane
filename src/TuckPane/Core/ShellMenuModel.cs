namespace TuckPane.Core;

// Contains managed snapshots only. Native handles remain owned by the STA session.
internal sealed record ShellMenuEntry(uint Id, string Label, string? Verb, bool Enabled = true,
    bool Checked = false, bool Separator = false, bool Unsupported = false, long Submenu = 0);

internal static class ShellMenuModel
{
    internal static readonly string[] PrimaryVerbs = ["cut", "copy", "paste", "rename", "delete"];

    internal static string DisplayLabel(string label) => label.Split('\t')[0]
        .Replace("&&", "\0", StringComparison.Ordinal).Replace("&", "", StringComparison.Ordinal).Replace('\0', '&');

    internal static (IReadOnlyList<ShellMenuEntry> Primary, IReadOnlyList<ShellMenuEntry> Secondary) Partition(IReadOnlyList<ShellMenuEntry> entries)
    {
        var primary = new List<ShellMenuEntry>();
        foreach (string verb in PrimaryVerbs)
        {
            ShellMenuEntry? entry = entries.FirstOrDefault(e => !e.Unsupported && !e.Separator && e.Submenu == 0 &&
                string.Equals(e.Verb, verb, StringComparison.OrdinalIgnoreCase));
            if (entry is not null) primary.Add(entry);
        }
        var selected = primary.Select(e => e.Id).ToHashSet();
        var secondary = new List<ShellMenuEntry>();
        foreach (ShellMenuEntry entry in entries)
        {
            if (entry.Unsupported || (!entry.Separator && entry.Submenu == 0 && selected.Contains(entry.Id))) continue;
            if (entry.Separator && (secondary.Count == 0 || secondary[^1].Separator)) continue;
            secondary.Add(entry);
        }
        if (secondary.Count > 0 && secondary[^1].Separator) secondary.RemoveAt(secondary.Count - 1);
        return (primary, secondary);
    }
}
