using TuckPane.Core;
using TuckPane.Models;
using System.Text.Json;

internal static class IconRowLayoutChecks
{
    internal static void Run()
    {
        Check(IconRowLayout.Normalize(null, 3, 5), [3, 2]);
        Check(IconRowLayout.Normalize([2, 0, 0, 1], 4, 4), [2, 2]);
        Check(IconRowLayout.Normalize([3, 3, 1], 2, 7), [2, 2, 2, 1]);
        Check(IconRowLayout.Normalize([1, 3, 1], 6, 5), [1, 3, 1]);

        int[] rows = [2, 1, 3];
        if (IconRowLayout.Locate(rows, 3) != (2, 0) ||
            IconRowLayout.HitTest(rows, 200, 65, 40, 50, 6) != new IconRowTarget(1, 1) ||
            IconRowLayout.HitTest(rows, 60, 52, 40, 50, 6) != new IconRowTarget(1, 0, true) ||
            IconRowLayout.HitTest(rows, 0, 168, 40, 50, 6) != new IconRowTarget(3, 0, true))
            throw new Exception("Incorrect cell or row boundary.");

        Edit(IconRowLayout.Move(rows, 3, 0, new(0, 2)), 1, [2, 1, 3]);
        Edit(IconRowLayout.Move(rows, 3, 2, new(2, 0)), 2, [2, 3, 1]);
        Edit(IconRowLayout.Move(rows, 3, 0, new(2, 0, true)), 2, [1, 1, 1, 3]);
        Edit(IconRowLayout.Move(rows, 3, 2, new(1, 0)), 2, rows);
        Edit(IconRowLayout.Insert([3, 3, 1], 3, new(0, 1), 3), 1, [3, 3, 3, 1]);
        Check(IconRowLayout.Remove(rows, 2), [2, 3]);
        var organizer = new OrganizerDefinition { IconRowCounts = [1, 2, 1], ItemOrder = ["A", "B", "C", "D"] };
        Check(OrganizerIconRows.DisplayRows(organizer, true, 3, 4), [3, 1]);
        Check(OrganizerIconRows.DisplayRows(organizer, false, 3, 4), [1, 2, 1]);

        var settings = JsonSerializer.Deserialize<GlobalSettings>("{}")!;
        if (settings.AutoFillIconRows) throw new Exception("Missing auto-fill setting must default to off.");
        settings.AutoFillIconRows = true;
        if (JsonSerializer.Deserialize<GlobalSettings>(JsonSerializer.Serialize(settings))?.AutoFillIconRows != true)
            throw new Exception("Auto-fill setting did not survive a save and reload.");
        Console.WriteLine("PASS icon rows");
    }

    private static void Edit(IconRowEdit edit, int index, int[] rows)
    {
        if (edit.Index != index) throw new Exception($"Expected insertion at {index}, got {edit.Index}");
        Check(edit.Rows, rows);
    }

    private static void Check(List<int> rows, int[] expected)
    {
        if (!rows.SequenceEqual(expected))
            throw new Exception($"Expected [{string.Join(',', expected)}], got [{string.Join(',', rows)}]");
    }
}
