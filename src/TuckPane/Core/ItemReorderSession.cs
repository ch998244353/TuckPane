using System.Numerics;
using Windows.Foundation;

namespace TuckPane.Core;

internal enum ItemDragState
{
    Pressed,
    NativeDragging,
    InternalPreview,
    Committing,
    DesktopRequested,
    ExternalMoved,
    Committed,
    Cancelled
}

internal sealed class ItemReorderSession
{
    internal const double ActivationThresholdDip = 8;
    internal const double SlotHysteresisDip = 6;

    private IReadOnlyList<int>? _originalRows;
    private IconRowTarget? _rowTarget;
    private IconRowEdit? _rowEdit;
    private int _rowRevision;
    internal List<int>? PreviewRows => _rowEdit?.Rows;

    internal void UpdateRowTarget(Point pointer, IReadOnlyList<int> rows, int columns, IconRowTarget target)
    {
        Track(pointer);
        if (!IsActive || IsSealed || _rowTarget == target) return;
        _originalRows = rows;
        _rowTarget = target;
        _rowEdit = IconRowLayout.Move(rows, columns, SourceIndex, target);
        _rowRevision++;
    }

    private GridGapPath? _gapPath;
    private GridGapPrimaryAxis _primaryAxis = GridGapPrimaryAxis.Horizontal;
    private GridGapPrimaryAxis _pendingPrimaryAxis = GridGapPrimaryAxis.Horizontal;
    private int? _pendingTarget;
    private Point _directionAnchor;
    private int[]? _sealedVisualIndices;

    internal ItemReorderSession(
        string relativeName,
        int sourceIndex,
        Point pressPointerContent,
        Point grabOffset)
    {
        RelativeName = relativeName;
        SourceIndex = sourceIndex;
        PressPointerContent = pressPointerContent;
        LatestPointerContent = pressPointerContent;
        _directionAnchor = pressPointerContent;
        GrabOffset = grabOffset;
        State = ItemDragState.Pressed;
    }

    internal string RelativeName { get; }
    internal int SourceIndex { get; }
    internal int TargetIndex => _rowEdit?.Index ?? _pendingTarget ?? _gapPath?.GapIndex ?? SourceIndex;
    internal int PreviewRevision => _rowEdit.HasValue ? _rowRevision : _gapPath?.Revision ?? 0;
    internal Point PressPointerContent { get; }
    internal Point LatestPointerContent { get; private set; }
    internal Point GrabOffset { get; }
    internal ItemDragState State { get; private set; }
    internal bool IsActive => State is ItemDragState.NativeDragging or ItemDragState.InternalPreview;
    internal bool NativeDragStarted { get; private set; }
    internal bool IsNativeDragging => NativeDragStarted && IsActive;

    internal bool TryActivate(Point pointerContent)
    {
        Track(pointerContent);
        double x = pointerContent.X - PressPointerContent.X;
        double y = pointerContent.Y - PressPointerContent.Y;
        if (State == ItemDragState.Pressed && x * x + y * y >= ActivationThresholdDip * ActivationThresholdDip)
            State = ItemDragState.InternalPreview;
        return IsActive;
    }

    internal void Activate(Point pointerContent)
    {
        Track(pointerContent);
        State = ItemDragState.InternalPreview;
    }

    internal void StartNativeDrag()
    {
        ResetPreview();
        NativeDragStarted = true;
        State = ItemDragState.NativeDragging;
    }

    internal void MarkInternalPreview() => State = ItemDragState.InternalPreview;

    internal void BeginCommit()
    {
        if (!IsActive || _sealedVisualIndices is null)
            throw new InvalidOperationException("A reorder must be active and sealed before it can commit.");
        State = ItemDragState.Committing;
    }

    internal void LeaveInternalPreview()
    {
        if (_sealedVisualIndices is null) ResetPreview();
        if (IsNativeDragging) State = ItemDragState.NativeDragging;
    }

    internal void MarkOutcome(ItemDragState state)
    {
        if (state is not (ItemDragState.DesktopRequested or ItemDragState.ExternalMoved or ItemDragState.Committed or ItemDragState.Cancelled))
            throw new ArgumentOutOfRangeException(nameof(state));
        State = state;
    }

    internal void Track(Point pointerContent) => LatestPointerContent = pointerContent;

    internal int UpdateTarget(
        Point pointerContent,
        double cellWidth,
        double cellHeight,
        double gap,
        int columns,
        int itemCount)
    {
        Track(pointerContent);
        if (!IsActive || itemCount <= 1 || columns <= 0) return TargetIndex;

        _gapPath ??= new GridGapPath(itemCount, columns, SourceIndex);
        if (_gapPath.Count != itemCount || _gapPath.Columns != columns) return TargetIndex;

        Point draggedCenter = new(
            pointerContent.X - GrabOffset.X + cellWidth / 2,
            pointerContent.Y - GrabOffset.Y + cellHeight / 2);
        double stepX = Math.Max(1, cellWidth + gap);
        double stepY = Math.Max(1, cellHeight + gap);
        int candidate = FindNearestSlot(draggedCenter, columns, itemCount, stepX, stepY, cellWidth, cellHeight);
        if (candidate == TargetIndex || _sealedVisualIndices is not null) return TargetIndex;

        Point currentCenter = SlotCenter(TargetIndex, columns, stepX, stepY, cellWidth, cellHeight);
        Point candidateCenter = SlotCenter(candidate, columns, stepX, stepY, cellWidth, cellHeight);
        double currentDistance = Distance(draggedCenter, currentCenter);
        double candidateDistance = Distance(draggedCenter, candidateCenter);
        if (candidateDistance + SlotHysteresisDip <= currentDistance)
        {
            double deltaX = pointerContent.X - _directionAnchor.X;
            double deltaY = pointerContent.Y - _directionAnchor.Y;
            if (Math.Abs(deltaX) > Math.Abs(deltaY) + SlotHysteresisDip) _primaryAxis = GridGapPrimaryAxis.Horizontal;
            else if (Math.Abs(deltaY) > Math.Abs(deltaX) + SlotHysteresisDip) _primaryAxis = GridGapPrimaryAxis.Vertical;
            _pendingTarget = candidate;
            _pendingPrimaryAxis = _primaryAxis;
            _directionAnchor = pointerContent;
        }
        return TargetIndex;
    }

    internal bool TryBeginPreviewTransition(int itemCount, out int[] fromVisualIndices, out int[] toVisualIndices)
    {
        if (_rowEdit.HasValue) { fromVisualIndices = toVisualIndices = []; return false; }
        fromVisualIndices = GetVisualIndices(itemCount);
        toVisualIndices = fromVisualIndices;
        if (_sealedVisualIndices is not null || _pendingTarget is not int target || _gapPath is null) return false;

        _pendingTarget = null;
        if (!_gapPath.MoveGapTo(target, _pendingPrimaryAxis)) return false;
        toVisualIndices = _gapPath.CreateVisualIndices();
        return true;
    }

    internal int GetVisualIndex(int originalIndex)
        => _sealedVisualIndices is { } sealedMapping
            ? sealedMapping[originalIndex]
            : _rowEdit is { } edit ? MovedIndex(originalIndex, edit.Index)
            : _gapPath?.GetVisualSlot(originalIndex) ?? originalIndex;

    private int MovedIndex(int index, int destination)
    {
        if (index == SourceIndex) return destination;
        if (SourceIndex < index && index <= destination) return index - 1;
        if (destination <= index && index < SourceIndex) return index + 1;
        return index;
    }

    internal int[] GetVisualIndices(int itemCount)
    {
        if (_sealedVisualIndices is { } sealedMapping) return (int[])sealedMapping.Clone();
        if (_rowEdit.HasValue) return Enumerable.Range(0, itemCount).Select(GetVisualIndex).ToArray();
        if (_gapPath is null) return Enumerable.Range(0, itemCount).ToArray();
        if (_gapPath.Count != itemCount) throw new ArgumentOutOfRangeException(nameof(itemCount));
        return _gapPath.CreateVisualIndices();
    }

    internal int[] SealVisualIndices(int itemCount)
    {
        _sealedVisualIndices ??= GetVisualIndices(itemCount);
        return (int[])_sealedVisualIndices.Clone();
    }

    internal bool IsSealed => _sealedVisualIndices is not null;

    internal Vector2 GetSlotTranslation(int originalIndex, int columns, double cellWidth, double cellHeight, double gap)
    {
        int visualIndex = GetVisualIndex(originalIndex);
        double stepX = cellWidth + gap;
        double stepY = cellHeight + gap;
        if (_originalRows is not null && PreviewRows is { } preview)
        {
            var from = IconRowLayout.Locate(_originalRows, originalIndex);
            var to = IconRowLayout.Locate(preview, visualIndex);
            return new Vector2((float)((to.Column - from.Column) * stepX), (float)((to.Row - from.Row) * stepY));
        }
        return new Vector2(
            (float)((visualIndex % columns - originalIndex % columns) * stepX),
            (float)((visualIndex / columns - originalIndex / columns) * stepY));
    }

    private static Point SlotCenter(int index, int columns, double stepX, double stepY, double width, double height) =>
        new(index % columns * stepX + width / 2, index / columns * stepY + height / 2);

    private int FindNearestSlot(
        Point draggedCenter,
        int columns,
        int itemCount,
        double stepX,
        double stepY,
        double width,
        double height)
    {
        int nearest = TargetIndex;
        double nearestDistance = Distance(draggedCenter, SlotCenter(nearest, columns, stepX, stepY, width, height));
        for (int index = 0; index < itemCount; index++)
        {
            if (index == TargetIndex) continue;
            double distance = Distance(draggedCenter, SlotCenter(index, columns, stepX, stepY, width, height));
            if (distance + .001 < nearestDistance)
            {
                nearest = index;
                nearestDistance = distance;
            }
        }
        return nearest;
    }

    private static double Distance(Point left, Point right)
    {
        double x = left.X - right.X;
        double y = left.Y - right.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private void ResetPreview()
    {
        if (_sealedVisualIndices is not null) return;
        _pendingTarget = null;
        _gapPath?.Reset();
        _rowEdit = null;
        _rowTarget = null;
        _originalRows = null;
        _directionAnchor = LatestPointerContent;
    }
}
