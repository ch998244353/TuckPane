using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace TuckPane.Controls;

// The repeater owns recycling. Request visible rows plus the captured source,
// which must stay alive even after its original row leaves the viewport.
internal sealed class OrganizerRowLayout : VirtualizingLayout
{
    internal IReadOnlyList<int> Rows { get; private set; } = [];
    private int[] _starts = [0];
    private Size _cell;
    private double _gap;
    private int _columns = 1;
    private int _first, _end, _pinned = -1;
    internal Func<int>? DragSource { get; set; }

    internal void Configure(List<int> rows, int columns, Size cell, double gap)
    {
        if (Rows.SequenceEqual(rows) && _columns == columns && _cell.Equals(cell) && _gap == gap) return;
        Rows = rows;
        _columns = columns;
        _cell = cell;
        _gap = gap;
        _starts = new int[rows.Count + 1];
        for (int i = 0; i < rows.Count; i++) _starts[i + 1] = _starts[i] + rows[i];
        InvalidateMeasure();
    }

    private int Start(int row, int count) => Math.Min(count, row < _starts.Length
        ? _starts[row] : _starts[^1] + (row - Rows.Count) * _columns);

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        int count = context.ItemCount;
        int rows = Rows.Count + (Math.Max(0, count - _starts[^1]) + _columns - 1) / _columns;
        double pitch = Math.Max(1, _cell.Height + _gap);
        Rect rect = context.RealizationRect;
        int firstRow = (int)Math.Clamp(Math.Floor(rect.Y / pitch), 0, rows);
        int lastRow = (int)Math.Clamp(Math.Ceiling((rect.Y + rect.Height) / pitch), firstRow, rows);
        _first = Start(firstRow, count);
        _end = Start(lastRow, count);
        _pinned = DragSource?.Invoke() ?? -1;
        for (int i = _first; i < _end; i++) context.GetOrCreateElementAt(i).Measure(_cell);
        if (IsPinnedOutside(count)) context.GetOrCreateElementAt(_pinned).Measure(_cell);
        return new Size(_columns * _cell.Width + (_columns - 1) * _gap, Math.Max(0, rows * pitch - _gap));
    }

    private bool IsPinnedOutside(int count) =>
        _pinned >= 0 && _pinned < count && (_pinned < _first || _pinned >= _end);

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        for (int i = _first; i < Math.Min(_end, context.ItemCount); i++) ArrangeItem(context, i);
        if (IsPinnedOutside(context.ItemCount)) ArrangeItem(context, _pinned);
        return finalSize;
    }

    private void ArrangeItem(VirtualizingLayoutContext context, int index)
    {
        int row, col;
        if (index >= _starts[^1])
        {
            int extra = index - _starts[^1];
            row = Rows.Count + extra / _columns;
            col = extra % _columns;
        }
        else
        {
            int pos = Array.BinarySearch(_starts, index);
            row = pos >= 0 ? pos : ~pos - 1;
            col = index - _starts[row];
        }
        context.GetOrCreateElementAt(index).Arrange(new Rect(
            col * (_cell.Width + _gap), row * (_cell.Height + _gap), _cell.Width, _cell.Height));
    }
}
