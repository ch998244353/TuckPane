namespace TuckPane.Core;

using TuckPane.Models;
using Windows.ApplicationModel.DataTransfer;

[Flags]
internal enum CanvasResizeEdge
{
    None = 0,
    Left = 1 << 0,
    Top = 1 << 1,
    Right = 1 << 2,
    Bottom = 1 << 3
}

internal enum OrganizerWheelAction
{
    Ignore,
    ScrollCompactList,
    ScrollGrid,
    ScaleCompactList,
    ScaleGrid
}

internal static class OrganizerInteractionMath
{
    internal const double WheelScaleStep = .05;

    internal static bool ShouldShowItemFallback(
        bool hasImageSource,
        bool iconLoadPending,
        bool organizerPreviewVisible) =>
        !hasImageSource && !iconLoadPending && !organizerPreviewVisible;

    internal static bool ShouldStartHoverExpand(
        bool enabled,
        bool station,
        bool expanded,
        bool animating,
        bool interactionActive) =>
        enabled && !station && !expanded && !animating && !interactionActive;

    internal static bool ShouldExpandForOrganizerDragHover(
        bool dragActive,
        bool sourceIsTarget,
        OrganizerPlacementMode targetMode,
        bool targetContained,
        bool targetExpanded,
        bool targetAnimating) =>
        dragActive && !sourceIsTarget &&
        targetMode is OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned &&
        !targetContained && !targetExpanded && !targetAnimating;

    internal static bool ShouldPollPointer(
        OrganizerPlacementMode placementMode,
        bool visible,
        bool contained,
        bool expanded,
        bool expandOnHover,
        bool collapseOnPointerLeave) =>
        visible &&
        (placementMode == OrganizerPlacementMode.Station ||
         !contained && expandOnHover ||
         expanded && collapseOnPointerLeave);

    internal static bool ShouldUseWindowAlignment(
        bool enabled,
        bool draggingExpanded,
        OrganizerPlacementMode placementMode,
        bool overOrganizerDropTarget) =>
        enabled &&
        !draggingExpanded &&
        placementMode == OrganizerPlacementMode.Floating &&
        !overOrganizerDropTarget;

    internal static bool ShouldRememberExpandedPosition(
        bool enabled,
        OrganizerPlacementMode placementMode) =>
        enabled && placementMode != OrganizerPlacementMode.Station;

    internal static DataPackageOperation SelectDropOperation(DataPackageOperation allowed) =>
        allowed.HasFlag(DataPackageOperation.Move) ? DataPackageOperation.Move :
        allowed.HasFlag(DataPackageOperation.Copy) ? DataPackageOperation.Copy :
        DataPackageOperation.None;

    internal static DataPackageOperation ExternalDragAllowedOperations(WidgetItemKind kind) =>
        kind == WidgetItemKind.Note
            ? DataPackageOperation.Copy | DataPackageOperation.Move
            : DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link;

    internal static DataPackageOperation ExternalDragRequestedOperation(WidgetItemKind kind) =>
        kind == WidgetItemKind.Note
            ? DataPackageOperation.Move
            : ExternalDragAllowedOperations(kind);

    internal static bool ExternalDragMovedSource(
        DataPackageOperation operation,
        bool internalDropAccepted,
        bool sourceItemExists) =>
        !internalDropAccepted &&
        (operation.HasFlag(DataPackageOperation.Move) ||
         operation == DataPackageOperation.None && !sourceItemExists);

    internal static string CreateCopyName(string sourceName, IEnumerable<string> existingNames, string suffix)
    {
        string stem = sourceName + suffix;
        var names = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(stem)) return stem;
        for (int number = 2; ; number++)
        {
            string candidate = $"{stem} ({number})";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    internal static OrganizerDefinition CopySettings(OrganizerDefinition source, string name) => new()
    {
        Name = name,
        PlacementMode = source.PlacementMode,
        DockEdge = source.DockEdge,
        Layout = new OrganizerLayout
        {
            Mode = source.Layout.Mode,
            Rows = source.Layout.Rows,
            Columns = source.Layout.Columns
        },
        CompactScale = source.CompactScale,
        CanvasScale = source.CanvasScale,
        ItemScale = source.ItemScale,
        NameScale = source.NameScale,
        CompactListItemScale = source.CompactListItemScale,
        ExpandedContentMode = source.ExpandedContentMode,
        CompactListCanvasWidthDip = source.CompactListCanvasWidthDip,
        CompactListCanvasHeightDip = source.CompactListCanvasHeightDip,
        ManualCanvasBaseWidthDip = source.ManualCanvasBaseWidthDip,
        ManualCanvasBaseHeightDip = source.ManualCanvasBaseHeightDip
    };

    internal static bool TryToggleExpandedContentMode(OrganizerDefinition organizer)
    {
        if (organizer.PlacementMode == OrganizerPlacementMode.Station) return false;
        organizer.ExpandedContentMode = organizer.ExpandedContentMode == OrganizerExpandedContentMode.Icon
            ? OrganizerExpandedContentMode.CompactList
            : OrganizerExpandedContentMode.Icon;
        return true;
    }

    internal static (int Left, int Top, int Width, int Height) ResizeFixedEdges(
        CanvasResizeEdge edge,
        int left,
        int top,
        int width,
        int height,
        int deltaX,
        int deltaY,
        int minimumWidth,
        int minimumHeight,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom)
    {
        int right = left + width;
        int bottom = top + height;
        if (edge.HasFlag(CanvasResizeEdge.Left)) left = Math.Clamp(left + deltaX, workLeft, right - minimumWidth);
        if (edge.HasFlag(CanvasResizeEdge.Right)) right = Math.Clamp(right + deltaX, left + minimumWidth, workRight);
        if (edge.HasFlag(CanvasResizeEdge.Top)) top = Math.Clamp(top + deltaY, workTop, bottom - minimumHeight);
        if (edge.HasFlag(CanvasResizeEdge.Bottom)) bottom = Math.Clamp(bottom + deltaY, top + minimumHeight, workBottom);
        return (left, top, right - left, bottom - top);
    }

    internal static double CalculateResizeFactor(
        CanvasResizeEdge edge,
        double deltaX,
        double deltaY,
        double startWidth,
        double startHeight)
    {
        double vectorX = edge.HasFlag(CanvasResizeEdge.Left) ? -startWidth / 2 :
            edge.HasFlag(CanvasResizeEdge.Right) ? startWidth / 2 : 0;
        double vectorY = edge.HasFlag(CanvasResizeEdge.Top) ? -startHeight / 2 :
            edge.HasFlag(CanvasResizeEdge.Bottom) ? startHeight / 2 : 0;
        double lengthSquared = vectorX * vectorX + vectorY * vectorY;
        if (lengthSquared <= 0) return 1;
        return Math.Max(0, 1 + (deltaX * vectorX + deltaY * vectorY) / lengthSquared);
    }

    internal static double ApplyWheelSteps(double current, int steps, double minimum, double maximum)
    {
        if (minimum > maximum) minimum = maximum;
        double target = Math.Round((current + steps * WheelScaleStep) * 100, MidpointRounding.AwayFromZero) / 100;
        return Math.Clamp(target, minimum, maximum);
    }

    internal static bool CanChangePlacementMode(OrganizerPlacementMode current, OrganizerPlacementMode next) =>
        (current == OrganizerPlacementMode.Station) == (next == OrganizerPlacementMode.Station);

    internal static bool ShouldApplyCtrlWheelScale(
        bool expanded,
        bool animating,
        bool resizing,
        bool reordering,
        bool shellDragging,
        bool controlPressed) =>
        expanded && !animating && !resizing && !reordering && !shellDragging && controlPressed;

    internal static OrganizerWheelAction ResolveWheelAction(
        bool expanded,
        bool animating,
        bool resizing,
        bool reordering,
        bool shellDragging,
        bool controlPressed,
        bool compactList,
        bool pointerInsideList)
    {
        if (!expanded || animating || resizing || reordering || shellDragging || !pointerInsideList)
            return OrganizerWheelAction.Ignore;
        if (controlPressed)
            return compactList ? OrganizerWheelAction.ScaleCompactList : OrganizerWheelAction.ScaleGrid;
        return compactList ? OrganizerWheelAction.ScrollCompactList : OrganizerWheelAction.ScrollGrid;
    }

    internal static (double ScrollDeltaDip, int Remainder) ConsumeCompactListWheelDelta(
        int remainder,
        int delta,
        double rowHeightDip)
    {
        int total = remainder + delta;
        int steps = total / 120;
        return (-steps * Math.Max(0, rowHeightDip), total % 120);
    }

    internal static (int Left, int Top, int Width, int Height) CreateCenteredBounds(
        int centerX,
        int centerY,
        double width,
        double height)
    {
        int roundedWidth = Math.Max(1, (int)Math.Round(width));
        int roundedHeight = Math.Max(1, (int)Math.Round(height));
        return (centerX - roundedWidth / 2, centerY - roundedHeight / 2, roundedWidth, roundedHeight);
    }
}
