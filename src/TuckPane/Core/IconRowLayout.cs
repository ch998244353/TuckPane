namespace TuckPane.Core;

internal readonly record struct IconRowTarget(int Row, int Column, bool NewRow = false);
internal readonly record struct IconRowEdit(int Index, List<int> Rows);

internal static class IconRowLayout
{
    // Row lengths describe consecutive slices of ItemOrder. Overflow always moves forward.
    internal static List<int> Normalize(IReadOnlyList<int>? counts, int columns, int itemCount)
    {
        columns = Math.Max(columns, 1);
        int remaining = Math.Max(itemCount, 0);
        var rows = new List<int>();
        long carry = 0;
        if (counts is not null)
        {
            foreach (int count in counts)
            {
                if (remaining == 0) break;
                if (count <= 0) continue;
                carry += count;
                int used = (int)Math.Min(carry, Math.Min(columns, remaining));
                rows.Add(used);
                remaining -= used;
                carry -= used;
            }
        }
        // Newly discovered items append to the final row before creating more rows.
        if (rows.Count != 0 && remaining > 0)
        {
            int used = Math.Min(remaining, columns - rows[^1]);
            rows[^1] += used;
            remaining -= used;
        }
        while (remaining > 0)
        {
            int used = Math.Min(columns, remaining);
            rows.Add(used);
            remaining -= used;
        }
        return rows;
    }

    internal static (int Row, int Column) Locate(IReadOnlyList<int> rows, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        for (int row = 0; row < rows.Count; row++)
        {
            if (index < rows[row]) return (row, index);
            index -= rows[row];
        }
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    // A narrow band centred on the gutter also works at zero row spacing.
    internal static IconRowTarget HitTest(IReadOnlyList<int> rows, double x, double y,
        double cellWidth, double cellHeight, double gap)
    {
        cellWidth = Math.Max(1, cellWidth);
        cellHeight = Math.Max(1, cellHeight);
        gap = Math.Max(0, gap);
        double pitch = cellHeight + gap;
        double band = Math.Min(5, cellHeight * .15);
        y = Math.Max(y, 0);
        int row = (int)Math.Min(Math.Floor(y / pitch), rows.Count);
        if (row >= rows.Count) return new(rows.Count, 0, true);
        double offset = y - row * pitch;
        if (row > 0 && offset < band) return new(row, 0, true);
        if (offset >= cellHeight - band) return new(row + 1, 0, true);
        double boundary = Math.Floor((x + cellWidth * .5 + gap) / (cellWidth + gap));
        return new(row, (int)Math.Clamp(boundary, 0, rows[row]));
    }

    internal static IconRowEdit Move(IReadOnlyList<int> rows, int columns, int sourceIndex, IconRowTarget target)
    {
        var (sourceRow, sourceColumn) = Locate(rows, sourceIndex);
        var updated = rows.ToList();
        updated[sourceRow]--;
        if (!target.NewRow && target.Row == sourceRow)
        {
            int column = target.Column > sourceColumn ? target.Column - 1 : target.Column;
            target = target with { Column = column };
        }
        else if (updated[sourceRow] == 0)
        {
            updated.RemoveAt(sourceRow);
            if (target.Row > sourceRow) target = target with { Row = target.Row - 1 };
        }
        return Insert(updated, columns, target);
    }

    internal static IconRowEdit Insert(IReadOnlyList<int> rows, int columns, IconRowTarget target, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        columns = Math.Max(columns, 1);
        var updated = rows.ToList();
        int row = Math.Clamp(target.Row, 0, updated.Count);
        if (target.NewRow || row == updated.Count) updated.Insert(row, 0);
        int index = Math.Clamp(target.Column, 0, updated[row]);
        for (int i = 0; i < row; i++) index += updated[i];
        int surplus = count;
        for (int i = row; surplus > 0; i++)
        {
            if (i == updated.Count) updated.Add(0);
            int added = Math.Min(columns - updated[i], surplus);
            updated[i] += added;
            surplus -= added;
        }
        return new(index, updated);
    }

    internal static List<int> Remove(IReadOnlyList<int> rows, int index)
    {
        var (row, _) = Locate(rows, index);
        var updated = rows.ToList();
        if (--updated[row] == 0) updated.RemoveAt(row);
        return updated;
    }
}
