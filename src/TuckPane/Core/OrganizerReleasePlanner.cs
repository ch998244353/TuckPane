namespace TuckPane.Core;

using TuckPane.Services;

internal readonly record struct OrganizerReleaseItem(Guid OrganizerId, int Width, int Height);

internal static class OrganizerReleasePlanner
{
    internal const double GapDip = 12;
    internal const int MaximumColumns = 3;

    internal static IReadOnlyDictionary<Guid, NativeMethods.RECT> PlanFloating(
        NativeMethods.RECT parentBounds,
        NativeMethods.RECT workArea,
        double displayScale,
        IReadOnlyList<OrganizerReleaseItem> items)
    {
        if (items.Count == 0) return new Dictionary<Guid, NativeMethods.RECT>();

        int gap = Math.Max(1, (int)Math.Round(GapDip * Math.Max(1, displayScale)));
        int columns = Math.Min(MaximumColumns, items.Count);
        int rows = (items.Count + columns - 1) / columns;
        int cellWidth = Math.Max(1, items.Max(item => item.Width));
        int cellHeight = Math.Max(1, items.Max(item => item.Height));
        int blockWidth = columns * cellWidth + (columns - 1) * gap;
        int blockHeight = rows * cellHeight + (rows - 1) * gap;
        int desiredLeft = parentBounds.Left + (parentBounds.Width - blockWidth) / 2;
        int desiredTop = parentBounds.Bottom + gap;
        if (desiredTop + blockHeight > workArea.Bottom)
            desiredTop = parentBounds.Top - gap - blockHeight;
        int left = ClampBlockOrigin(desiredLeft, blockWidth, workArea.Left, workArea.Right);
        int top = ClampBlockOrigin(desiredTop, blockHeight, workArea.Top, workArea.Bottom);

        var result = new Dictionary<Guid, NativeMethods.RECT>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            OrganizerReleaseItem item = items[index];
            int column = index % columns;
            int row = index / columns;
            int itemLeft = left + column * (cellWidth + gap) + (cellWidth - item.Width) / 2;
            int itemTop = top + row * (cellHeight + gap) + (cellHeight - item.Height) / 2;
            result[item.OrganizerId] = new NativeMethods.RECT
            {
                Left = itemLeft,
                Top = itemTop,
                Right = itemLeft + item.Width,
                Bottom = itemTop + item.Height
            };
        }
        return result;
    }

    private static int ClampBlockOrigin(int desired, int size, int minimum, int maximum)
    {
        if (size >= maximum - minimum) return minimum;
        return Math.Clamp(desired, minimum, maximum - size);
    }
}
