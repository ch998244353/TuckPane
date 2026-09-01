using TuckPane.Models;
using TuckPane.Core;

namespace TuckPane.Services;

internal sealed record DisplayInfo(string Device, NativeMethods.RECT Monitor, NativeMethods.RECT Work, double Scale);

internal static class DisplayPlacementService
{
    internal const double ExpandedSideInsetDip = 28;
    internal const double StationSideInsetDip = 12;
    internal const double StationTopInsetDip = 12;
    internal const double StationBottomInsetDip = 12;
    internal const double ExpandedTopInsetDip = 40.5;
    internal const double ExpandedBottomInsetDip = 28;
    internal const double ExpandedTitleBandDip = 56;
    internal const double ItemGapDip = 12;
    internal const double MaximumItemScale = 1.65;
    private const double IconCellFraction = .68;
    private const double StationIconCellFraction = .82;
    private const double NameCellFraction = .15;
    private const double PreviousIconCellFraction = .62;

    internal static double ResolveExpandedSideInset(
        OrganizerPlacementMode placementMode,
        OrganizerExpandedContentMode contentMode) =>
        placementMode == OrganizerPlacementMode.Station ||
        contentMode == OrganizerExpandedContentMode.CompactList
            ? StationSideInsetDip
            : ExpandedSideInsetDip;

    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var displays = new List<DisplayInfo>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref NativeMethods.RECT monitorRect, IntPtr data) =>
        {
            NativeMethods.MONITORINFOEX info = CreateMonitorInfo();
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                double scale = 1;
                if (NativeMethods.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                {
                    scale = dpiX / 96d;
                }
                displays.Add(new(info.szDevice, info.rcMonitor, info.rcWork, scale));
            }
            return true;
        }, IntPtr.Zero);
        return displays;
    }

    internal static DisplayInfo GetDisplay(string? device = null)
    {
        IReadOnlyList<DisplayInfo> displays = GetDisplays();
        return displays.FirstOrDefault(display => string.Equals(display.Device, device, StringComparison.OrdinalIgnoreCase))
            ?? displays.FirstOrDefault(display => display.Monitor.Left == 0 && display.Monitor.Top == 0)
            ?? displays.First();
    }

    public static NativeMethods.RECT Restore(
        WidgetPosition? saved,
        int widthPx,
        int heightPx,
        WindowAlignmentInsets? alignmentInsets = null)
    {
        IReadOnlyList<DisplayInfo> displays = GetDisplays();
        DisplayInfo display = displays.FirstOrDefault(d => string.Equals(d.Device, saved?.MonitorDevice, StringComparison.OrdinalIgnoreCase))
            ?? displays.FirstOrDefault(d => d.Monitor.Left == 0 && d.Monitor.Top == 0)
            ?? displays.First();

        return RestoreToDisplay(saved, display, widthPx, heightPx, alignmentInsets);
    }

    internal static NativeMethods.RECT RestoreToDisplay(
        WidgetPosition? saved,
        DisplayInfo display,
        int widthPx,
        int heightPx,
        WindowAlignmentInsets? alignmentInsets = null)
    {
        int x = saved is null || string.IsNullOrWhiteSpace(saved.MonitorDevice)
            ? display.Work.Left + (display.Work.Width - widthPx) / 2
            : display.Work.Left + (int)Math.Round(saved.XDip * display.Scale);
        int y = saved is null || string.IsNullOrWhiteSpace(saved.MonitorDevice)
            ? display.Work.Top + (int)Math.Round(96 * display.Scale)
            : display.Work.Top + (int)Math.Round(saved.YDip * display.Scale);
        var bounds = new NativeMethods.RECT { Left = x, Top = y, Right = x + widthPx, Bottom = y + heightPx };
        return alignmentInsets is WindowAlignmentInsets insets
            ? WindowAlignmentMath.ClampFrame(bounds, display.Work, insets)
            : Clamp(bounds, display.Work);
    }

    public static NativeMethods.RECT Clamp(NativeMethods.RECT bounds, NativeMethods.RECT work)
    {
        int width = Math.Min(bounds.Width, work.Width);
        int height = Math.Min(bounds.Height, work.Height);
        int x = Math.Clamp(bounds.Left, work.Left, work.Right - width);
        int y = Math.Clamp(bounds.Top, work.Top, work.Bottom - height);
        return new NativeMethods.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
    }

    internal static NativeMethods.RECT CalculateCenteredDialogBounds(
        DisplayInfo display,
        double widthDip = 440,
        double heightDip = 280,
        double marginDip = 24)
    {
        double scale = Math.Max(1, display.Scale);
        int margin = Math.Max(0, (int)Math.Round(marginDip * scale));
        int maximumWidth = Math.Max(1, display.Work.Width - margin * 2);
        int maximumHeight = Math.Max(1, display.Work.Height - margin * 2);
        int width = Math.Min(maximumWidth, Math.Max(1, (int)Math.Round(widthDip * scale)));
        int height = Math.Min(maximumHeight, Math.Max(1, (int)Math.Round(heightDip * scale)));
        int left = display.Work.Left + (display.Work.Width - width) / 2;
        int top = display.Work.Top + (display.Work.Height - height) / 2;
        return new NativeMethods.RECT
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        };
    }

    internal static NativeMethods.RECT CalculateDraggedBounds(
        NativeMethods.RECT pressAnchor,
        NativeMethods.POINT pressCursor,
        NativeMethods.POINT currentCursor,
        NativeMethods.RECT work)
    {
        int x = pressAnchor.Left + currentCursor.X - pressCursor.X;
        int y = pressAnchor.Top + currentCursor.Y - pressCursor.Y;
        return Clamp(new NativeMethods.RECT
        {
            Left = x,
            Top = y,
            Right = x + pressAnchor.Width,
            Bottom = y + pressAnchor.Height
        }, work);
    }

    internal static double CalculateGridCellExtent(double availableExtent, int count, double gap)
    {
        int safeCount = Math.Max(1, count);
        double exactExtent = (availableExtent - gap * (safeCount - 1)) / safeCount;
        return Math.Max(1, exactExtent - .5);
    }

    internal static (double Width, double Height) CalculateItemCellSizeDip(
        double availableWidth,
        double availableHeight,
        OrganizerLayout layout)
    {
        double width = CalculateGridCellExtent(availableWidth, layout.Columns, ItemGapDip);
        double height = CalculateGridCellExtent(availableHeight, layout.Rows, ItemGapDip);
        return (width, height);
    }

    internal static double CalculateMaximumItemScale(DisplayInfo display, OrganizerLayout layout, double canvasScale)
    {
        double cellDip = CalculateCanvasCell(display, layout, canvasScale) / display.Scale;
        return CalculateMaximumItemScaleForCell(cellDip);
    }

    internal static double CalculateMaximumStationItemScale(DisplayInfo display, OrganizerLayout layout)
    {
        int columns = Math.Clamp(layout.Columns, 1, OrganizerLimits.MaximumStationColumns);
        int rows = Math.Clamp(layout.Rows, 1, OrganizerLimits.MaximumStationRows);
        double workWidthDip = display.Work.Width / display.Scale;
        double workHeightDip = display.Work.Height / display.Scale;
        double availableCellWidth = (workWidthDip - StationSideInsetDip * 2 - ItemGapDip * (columns - 1)) / columns;
        double availableCellHeight = (workHeightDip - StationTopInsetDip - StationBottomInsetDip - ItemGapDip * (rows - 1)) / rows;
        if (availableCellWidth <= 1 || availableCellHeight <= 1) return .5;
        double low = .5;
        double high = MaximumItemScale;
        for (int iteration = 0; iteration < 24; iteration++)
        {
            double candidate = (low + high) / 2;
            (double width, double height) = CalculateRequiredStationCellSizeDip(candidate);
            if (width <= availableCellWidth && height <= availableCellHeight) low = candidate;
            else high = candidate;
        }
        return Math.Clamp(low, .5, MaximumItemScale);
    }

    internal static double CalculateMaximumItemScaleForExpandedSize(
        OrganizerLayout layout,
        double widthDip,
        double heightDip,
        double sideInsetDip = ExpandedSideInsetDip)
    {
        int columns = Math.Clamp(layout.Columns, 1, OrganizerLimits.MaximumStationColumns);
        int rows = Math.Clamp(layout.Rows, 1, OrganizerLimits.MaximumStationRows);
        double availableWidth = Math.Max(1, widthDip - sideInsetDip * 2);
        double availableHeight = Math.Max(1, heightDip - ExpandedTopInsetDip - ExpandedBottomInsetDip);
        double cellDip = Math.Min(
            CalculateGridCellExtent(availableWidth, columns, ItemGapDip),
            CalculateGridCellExtent(availableHeight, rows, ItemGapDip));
        return CalculateMaximumItemScaleForCell(cellDip);
    }

    internal static (double WidthDip, double HeightDip) CalculateMinimumExpandedSizeDip(
        OrganizerLayout layout,
        double itemScale,
        double sideInsetDip = ExpandedSideInsetDip)
    {
        int columns = Math.Clamp(layout.Columns, 1, OrganizerLimits.MaximumStationColumns);
        int rows = Math.Clamp(layout.Rows, 1, OrganizerLimits.MaximumStationRows);
        double cell = CalculateRequiredCellDip(itemScale);
        return (
            cell * columns + ItemGapDip * (columns - 1) + sideInsetDip * 2,
            cell * rows + ItemGapDip * (rows - 1) + ExpandedTopInsetDip + ExpandedBottomInsetDip);
    }

    private static double CalculateMaximumItemScaleForCell(double cellDip)
    {
        if (CalculateRequiredCellDip(.5) >= cellDip) return .5;
        double low = .5;
        double high = MaximumItemScale;
        for (int iteration = 0; iteration < 24; iteration++)
        {
            double candidate = (low + high) / 2;
            if (CalculateRequiredCellDip(candidate) <= cellDip) low = candidate;
            else high = candidate;
        }
        return Math.Clamp(low, .5, MaximumItemScale);
    }

    internal static double CalculateMinimumCanvasScale(
        DisplayInfo display,
        OrganizerLayout layout,
        double sideInsetDip = ExpandedSideInsetDip)
    {
        double baseCell = CalculateBaseCell(display);
        double scale = display.Scale;
        if (Math.Abs(sideInsetDip - StationSideInsetDip) < .001)
            return Math.Clamp(CalculateRequiredCellDip(.5) * scale / baseCell, .1, 1.2);

        int columns = Math.Clamp(layout.Columns, 1, OrganizerLimits.MaximumStationColumns);
        int rows = Math.Clamp(layout.Rows, 1, OrganizerLimits.MaximumStationRows);
        double gap = ItemGapDip * scale;
        double horizontalChrome = sideInsetDip * 2 * scale + gap * (columns - 1);
        double verticalChrome = (ExpandedTopInsetDip + ExpandedBottomInsetDip) * scale + gap * (rows - 1);
        double previousRequiredCellDip = Math.Max(88, Math.Max(72 / PreviousIconCellFraction, 13 / NameCellFraction));
        double legacyCell = Math.Min(
            CalculateMaximumCell(display, layout, sideInsetDip),
            Math.Max(baseCell * .4, previousRequiredCellDip * scale));
        double legacyLongest = Math.Max(
            legacyCell * columns + horizontalChrome,
            legacyCell * rows + verticalChrome);
        double targetLongest = legacyLongest * 2d / 3d;
        double targetCell = Math.Min(
            (targetLongest - horizontalChrome) / columns,
            (targetLongest - verticalChrome) / rows);
        targetCell = Math.Max(CalculateRequiredCellDip(.5) * scale, targetCell);
        return Math.Clamp(targetCell / baseCell, .1, 1.2);
    }

    internal static double CalculateRequiredCellDip(double itemScale)
    {
        double normalized = Math.Clamp(itemScale, .5, MaximumItemScale);
        double fontSize = Math.Max(8, 13 * normalized);
        double verticalContent = 72 * normalized + fontSize * 1.25 + Math.Max(2, 6 * normalized) + Math.Max(4, 10 * normalized);
        return Math.Max(
            Math.Max(72 * normalized / IconCellFraction, fontSize / NameCellFraction),
            verticalContent);
    }

    internal static (double Width, double Height) CalculateRequiredStationCellSizeDip(double itemScale)
    {
        double normalized = Math.Clamp(itemScale, .5, MaximumItemScale);
        double fontSize = Math.Max(8, 13 * normalized);
        double iconSize = 72 * normalized;
        double padding = Math.Max(4, 10 * normalized);
        return (
            Math.Max(iconSize / StationIconCellFraction, fontSize / NameCellFraction),
            iconSize + fontSize * 1.25 + Math.Max(2, 6 * normalized) + padding);
    }

    internal static NativeMethods.RECT CalculateExpandedBounds(NativeMethods.RECT compact, DisplayInfo display)
        => CalculateExpandedBounds(compact, display, new OrganizerLayout(), canvasScale: 1);

    internal static NativeMethods.RECT CalculateExpandedBounds(
        NativeMethods.RECT compact,
        DisplayInfo display,
        OrganizerLayout layout,
        double canvasScale,
        double? manualCanvasBaseWidthDip = null,
        double? manualCanvasBaseHeightDip = null,
        double sideInsetDip = ExpandedSideInsetDip)
    {
        NativeMethods.RECT insetWork = GetExpandedWorkArea(display);
        int titleHeightPx = Math.Max(0, (int)Math.Round(ExpandedTitleBandDip * display.Scale));
        int availablePanelHeight = Math.Max(1, insetWork.Height - titleHeightPx);
        int columns = Math.Clamp(layout.Columns, 1, OrganizerLimits.MaximumStationColumns);
        int rows = Math.Clamp(layout.Rows, 1, OrganizerLimits.MaximumStationRows);
        double width;
        double height;
        if (manualCanvasBaseWidthDip is double baseWidth && manualCanvasBaseHeightDip is double baseHeight &&
            double.IsFinite(baseWidth) && double.IsFinite(baseHeight) && baseWidth > 0 && baseHeight > 0)
        {
            width = baseWidth * Math.Clamp(canvasScale, .1, 1.2) * display.Scale;
            height = baseHeight * Math.Clamp(canvasScale, .1, 1.2) * display.Scale;
            double fit = Math.Min(1, Math.Min(insetWork.Width / width, availablePanelHeight / height));
            width *= fit;
            height *= fit;
        }
        else
        {
            double cell = CalculateCanvasCell(display, layout, canvasScale, sideInsetDip);
            double gap = ItemGapDip * display.Scale;
            width = cell * columns + gap * (columns - 1) + sideInsetDip * 2 * display.Scale;
            height = cell * rows + gap * (rows - 1) + (ExpandedTopInsetDip + ExpandedBottomInsetDip) * display.Scale;
        }
        int widthPx = Math.Max(1, (int)Math.Round(width));
        int heightPx = Math.Min(availablePanelHeight, Math.Max(1, (int)Math.Round(height)));
        int centerX = compact.Left + compact.Width / 2;
        int centerY = compact.Top + (int)Math.Round(19.5 * display.Scale);
        var desired = new NativeMethods.RECT
        {
            Left = centerX - widthPx / 2,
            Top = centerY - heightPx / 2 - titleHeightPx,
            Right = centerX - widthPx / 2 + widthPx,
            Bottom = centerY - heightPx / 2 + heightPx
        };
        return Clamp(desired, insetWork);
    }

    internal static NativeMethods.RECT GetExpandedWorkArea(DisplayInfo display)
    {
        int margin = (int)Math.Round(24 * display.Scale);
        return new NativeMethods.RECT
        {
            Left = display.Work.Left + margin,
            Top = display.Work.Top + margin,
            Right = display.Work.Right - margin,
            Bottom = display.Work.Bottom - margin
        };
    }

    internal static NativeMethods.RECT CalculateStationAnchor(
        DisplayInfo display,
        OrganizerDockEdge edge,
        WidgetPosition? saved = null)
    {
        double xRatio = saved is null || saved.SavedWorkAreaWidthDip <= 0
            ? .5
            : saved.XDip / saved.SavedWorkAreaWidthDip;
        double yRatio = saved is null || saved.SavedWorkAreaHeightDip <= 0
            ? .5
            : saved.YDip / saved.SavedWorkAreaHeightDip;
        int centerX = Math.Clamp(
            display.Work.Left + (int)Math.Round(Math.Clamp(xRatio, 0, 1) * display.Work.Width),
            display.Work.Left,
            display.Work.Right - 1);
        int centerY = Math.Clamp(
            display.Work.Top + (int)Math.Round(Math.Clamp(yRatio, 0, 1) * display.Work.Height),
            display.Work.Top,
            display.Work.Bottom - 1);
        return edge switch
        {
            OrganizerDockEdge.Left => new() { Left = display.Work.Left, Top = centerY, Right = display.Work.Left + 1, Bottom = centerY + 1 },
            OrganizerDockEdge.Top => new() { Left = centerX, Top = display.Work.Top, Right = centerX + 1, Bottom = display.Work.Top + 1 },
            OrganizerDockEdge.Right => new() { Left = display.Work.Right - 1, Top = centerY, Right = display.Work.Right, Bottom = centerY + 1 },
            _ => new() { Left = centerX, Top = display.Work.Bottom - 1, Right = centerX + 1, Bottom = display.Work.Bottom }
        };
    }

    internal static NativeMethods.RECT CalculateStationBounds(
        DisplayInfo display,
        OrganizerDockEdge edge,
        OrganizerLayout layout,
        double canvasScale,
        double itemScale,
        WidgetPosition? position = null,
        double? manualCanvasBaseWidthDip = null,
        double? manualCanvasBaseHeightDip = null)
    {
        _ = canvasScale;
        _ = manualCanvasBaseWidthDip;
        _ = manualCanvasBaseHeightDip;
        int columns = Math.Clamp(layout.Columns, 1, OrganizerLimits.MaximumStationColumns);
        int rows = Math.Clamp(layout.Rows, 1, OrganizerLimits.MaximumStationRows);
        double effectiveItemScale = Math.Min(
            Math.Clamp(itemScale, .5, MaximumItemScale),
            CalculateMaximumStationItemScale(display, layout));
        (double cellWidthDip, double cellHeightDip) = CalculateRequiredStationCellSizeDip(effectiveItemScale);
        int width = Math.Min(display.Work.Width, Math.Max(1, (int)Math.Round((
            cellWidthDip * columns + ItemGapDip * (columns - 1) + StationSideInsetDip * 2) * display.Scale)));
        int height = Math.Min(display.Work.Height, Math.Max(1, (int)Math.Round((
            cellHeightDip * rows + ItemGapDip * (rows - 1) + StationTopInsetDip + StationBottomInsetDip) * display.Scale)));
        NativeMethods.RECT anchor = CalculateStationAnchor(display, edge, position);
        int left = anchor.Left - width / 2;
        int top = anchor.Top - height / 2;
        switch (edge)
        {
            case OrganizerDockEdge.Left: left = display.Work.Left; break;
            case OrganizerDockEdge.Top: top = display.Work.Top; break;
            case OrganizerDockEdge.Right: left = display.Work.Right - width; break;
            case OrganizerDockEdge.Bottom: top = display.Work.Bottom - height; break;
        }
        return Clamp(new NativeMethods.RECT
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        }, display.Work);
    }

    internal static bool IsStationHotZone(
        NativeMethods.POINT point,
        DisplayInfo display,
        OrganizerDockEdge edge,
        int distanceDip)
    {
        NativeMethods.RECT bounds = display.Monitor;
        if (point.X < bounds.Left || point.X >= bounds.Right ||
            point.Y < bounds.Top || point.Y >= bounds.Bottom)
        {
            return false;
        }
        int thickness = Math.Max(1, (int)Math.Round(distanceDip * Math.Max(1, display.Scale)));
        return edge switch
        {
            OrganizerDockEdge.Left => point.X < bounds.Left + thickness,
            OrganizerDockEdge.Top => point.Y < bounds.Top + thickness,
            OrganizerDockEdge.Right => point.X >= bounds.Right - thickness,
            _ => point.Y >= bounds.Bottom - thickness
        };
    }

    internal static bool IsStationExpandedSafeRegion(
        NativeMethods.POINT point,
        DisplayInfo display,
        OrganizerDockEdge edge,
        NativeMethods.RECT expandedBounds,
        int activationDistanceDip)
    {
        if (Contains(expandedBounds, point) ||
            IsStationHotZone(point, display, edge, activationDistanceDip)) return true;
        NativeMethods.RECT monitor = display.Monitor;
        return edge switch
        {
            OrganizerDockEdge.Left => point.X >= monitor.Left && point.X < expandedBounds.Left &&
                point.Y >= expandedBounds.Top && point.Y < expandedBounds.Bottom,
            OrganizerDockEdge.Top => point.Y >= monitor.Top && point.Y < expandedBounds.Top &&
                point.X >= expandedBounds.Left && point.X < expandedBounds.Right,
            OrganizerDockEdge.Right => point.X >= expandedBounds.Right && point.X < monitor.Right &&
                point.Y >= expandedBounds.Top && point.Y < expandedBounds.Bottom,
            _ => point.Y >= expandedBounds.Bottom && point.Y < monitor.Bottom &&
                point.X >= expandedBounds.Left && point.X < expandedBounds.Right
        };
    }

    private static bool Contains(NativeMethods.RECT bounds, NativeMethods.POINT point) =>
        point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;

    internal static NativeMethods.RECT CalculateStationDraggedBounds(
        NativeMethods.RECT pressBounds,
        NativeMethods.POINT pressCursor,
        NativeMethods.POINT currentCursor,
        DisplayInfo display,
        OrganizerDockEdge edge)
    {
        int width = Math.Min(pressBounds.Width, display.Work.Width);
        int height = Math.Min(pressBounds.Height, display.Work.Height);
        int left = pressBounds.Left;
        int top = pressBounds.Top;
        if (edge is OrganizerDockEdge.Left or OrganizerDockEdge.Right)
        {
            top = Math.Clamp(pressBounds.Top + currentCursor.Y - pressCursor.Y, display.Work.Top, display.Work.Bottom - height);
            left = edge == OrganizerDockEdge.Left ? display.Work.Left : display.Work.Right - width;
        }
        else
        {
            left = Math.Clamp(pressBounds.Left + currentCursor.X - pressCursor.X, display.Work.Left, display.Work.Right - width);
            top = edge == OrganizerDockEdge.Top ? display.Work.Top : display.Work.Bottom - height;
        }
        return new NativeMethods.RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    internal static WidgetPosition CaptureStationPosition(
        DisplayInfo display,
        OrganizerDockEdge edge,
        NativeMethods.RECT stationBounds)
    {
        int centerX = stationBounds.Left + stationBounds.Width / 2;
        int centerY = stationBounds.Top + stationBounds.Height / 2;
        NativeMethods.RECT anchor = edge switch
        {
            OrganizerDockEdge.Left => new() { Left = display.Work.Left, Top = centerY, Right = display.Work.Left + 1, Bottom = centerY + 1 },
            OrganizerDockEdge.Top => new() { Left = centerX, Top = display.Work.Top, Right = centerX + 1, Bottom = display.Work.Top + 1 },
            OrganizerDockEdge.Right => new() { Left = display.Work.Right - 1, Top = centerY, Right = display.Work.Right, Bottom = centerY + 1 },
            _ => new() { Left = centerX, Top = display.Work.Bottom - 1, Right = centerX + 1, Bottom = display.Work.Bottom }
        };
        return new WidgetPosition
        {
            MonitorDevice = display.Device,
            XDip = (anchor.Left - display.Work.Left) / display.Scale,
            YDip = (anchor.Top - display.Work.Top) / display.Scale,
            SavedWorkAreaWidthDip = display.Work.Width / display.Scale,
            SavedWorkAreaHeightDip = display.Work.Height / display.Scale
        };
    }

    private static double CalculateBaseCell(DisplayInfo display)
    {
        int margin = (int)Math.Round(24 * display.Scale);
        int legacyWidth = Math.Min((int)Math.Round(display.Work.Width * .70), display.Work.Width - margin * 2);
        return legacyWidth / 6d;
    }

    private static double CalculateCanvasCell(
        DisplayInfo display,
        OrganizerLayout layout,
        double canvasScale,
        double sideInsetDip = ExpandedSideInsetDip)
    {
        double minimumScale = CalculateMinimumCanvasScale(display, layout, sideInsetDip);
        double desiredCell = CalculateBaseCell(display) * Math.Clamp(canvasScale, minimumScale, 1.2);
        return Math.Min(CalculateMaximumCell(display, layout, sideInsetDip), desiredCell);
    }

    private static double CalculateMaximumCell(
        DisplayInfo display,
        OrganizerLayout layout,
        double sideInsetDip = ExpandedSideInsetDip)
    {
        int margin = (int)Math.Round(24 * display.Scale);
        int columns = Math.Clamp(layout.Columns, 1, OrganizerLimits.MaximumLayoutDimension);
        int rows = Math.Clamp(layout.Rows, 1, OrganizerLimits.MaximumStationRows);
        double gap = ItemGapDip * display.Scale;
        double availableWidth = display.Work.Width - margin * 2 - sideInsetDip * 2 * display.Scale - gap * (columns - 1);
        double availableHeight = display.Work.Height - margin * 2 -
            (ExpandedTitleBandDip + ExpandedTopInsetDip + ExpandedBottomInsetDip) * display.Scale - gap * (rows - 1);
        return Math.Max(1, Math.Min(availableWidth / columns, availableHeight / rows));
    }

    public static NativeMethods.RECT FindAvailableOnPrimary(IReadOnlyList<NativeMethods.RECT> occupied, int widthPx, int heightPx)
    {
        return FindAvailable(GetDisplay(), occupied, widthPx, heightPx);
    }

    internal static NativeMethods.RECT FindAvailable(
        DisplayInfo display,
        IReadOnlyList<NativeMethods.RECT> occupied,
        int widthPx,
        int heightPx)
    {
        int gap = (int)Math.Round(16 * display.Scale);
        for (int y = display.Work.Top + gap; y + heightPx <= display.Work.Bottom; y += heightPx + gap)
        {
            for (int x = display.Work.Left + gap; x + widthPx <= display.Work.Right; x += widthPx + gap)
            {
                var candidate = new NativeMethods.RECT { Left = x, Top = y, Right = x + widthPx, Bottom = y + heightPx };
                if (!occupied.Any(bounds => Intersects(candidate, bounds))) return candidate;
            }
        }
        return RestoreToDisplay(null, display, widthPx, heightPx);
    }

    public static WidgetPosition Capture(NativeMethods.RECT bounds, IntPtr window = default)
    {
        var point = new NativeMethods.POINT { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 };
        IntPtr monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
        NativeMethods.MONITORINFOEX info = CreateMonitorInfo();
        NativeMethods.GetMonitorInfo(monitor, ref info);
        double scale = 1;
        if (NativeMethods.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96d;
        }
        if (window != IntPtr.Zero)
        {
            scale = Math.Max(1, NativeMethods.GetDpiForWindow(window) / 96d);
        }

        return new WidgetPosition
        {
            MonitorDevice = info.szDevice,
            XDip = (bounds.Left - info.rcWork.Left) / scale,
            YDip = (bounds.Top - info.rcWork.Top) / scale,
            SavedWorkAreaWidthDip = info.rcWork.Width / scale,
            SavedWorkAreaHeightDip = info.rcWork.Height / scale
        };
    }

    public static DisplayInfo ForBounds(NativeMethods.RECT bounds)
    {
        var point = new NativeMethods.POINT { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 };
        IntPtr monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
        NativeMethods.MONITORINFOEX info = CreateMonitorInfo();
        NativeMethods.GetMonitorInfo(monitor, ref info);
        double scale = 1;
        if (NativeMethods.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96d;
        }
        return new(info.szDevice, info.rcMonitor, info.rcWork, scale);
    }

    private static NativeMethods.MONITORINFOEX CreateMonitorInfo() => new()
    {
        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
        szDevice = string.Empty
    };

    private static bool Intersects(NativeMethods.RECT first, NativeMethods.RECT second) =>
        first.Left < second.Right && first.Right > second.Left && first.Top < second.Bottom && first.Bottom > second.Top;
}
