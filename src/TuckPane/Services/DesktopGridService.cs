using System.Runtime.InteropServices;
using System.Diagnostics;
using TuckPane.Models;

namespace TuckPane.Services;

internal sealed record DesktopGridSnapshot(
    DisplayInfo Display,
    int CellWidthPx,
    int CellHeightPx,
    IReadOnlyList<NativeMethods.POINT> DesktopIconCenters,
    bool ExplorerPositionsAvailable);

internal sealed record DesktopGridPlacement(
    NativeMethods.RECT Bounds,
    double CompactScale,
    bool ExplorerPositionsAvailable);

internal sealed class DesktopGridService
{
    private readonly record struct GridSlot(int Column, int Row, NativeMethods.POINT Center, NativeMethods.RECT Cell, NativeMethods.RECT Bounds, double CompactScale);
    private readonly Dictionary<string, (long Timestamp, DesktopGridSnapshot Snapshot)> _snapshotCache = new(StringComparer.OrdinalIgnoreCase);

    internal DesktopGridSnapshot ReadSnapshot(DisplayInfo display)
    {
        if (_snapshotCache.TryGetValue(display.Device, out var cached) &&
            Stopwatch.GetElapsedTime(cached.Timestamp).TotalMilliseconds < 750 &&
            cached.Snapshot.Display.Work.Left == display.Work.Left &&
            cached.Snapshot.Display.Work.Top == display.Work.Top &&
            cached.Snapshot.Display.Work.Right == display.Work.Right &&
            cached.Snapshot.Display.Work.Bottom == display.Work.Bottom &&
            Math.Abs(cached.Snapshot.Display.Scale - display.Scale) < .001)
        {
            return cached.Snapshot;
        }

        uint dpi = (uint)Math.Max(96, Math.Round(display.Scale * 96));
        NativeMethods.ICONMETRICS metrics = new()
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.ICONMETRICS>(),
            Font = new NativeMethods.LOGFONT { FaceName = string.Empty }
        };
        bool readMetrics = NativeMethods.SystemParametersInfoForDpi(
            NativeMethods.SPI_GETICONMETRICS,
            metrics.Size,
            ref metrics,
            0,
            dpi);
        int cellWidth = readMetrics ? metrics.HorizontalSpacing : NativeMethods.GetSystemMetrics(NativeMethods.SM_CXICONSPACING);
        int cellHeight = readMetrics ? metrics.VerticalSpacing : NativeMethods.GetSystemMetrics(NativeMethods.SM_CYICONSPACING);
        IntPtr listView = FindDesktopListView();
        if (TryReadActiveGridSpacing(listView, out int activeCellWidth, out int activeCellHeight))
        {
            cellWidth = activeCellWidth;
            cellHeight = activeCellHeight;
        }
        cellWidth = Math.Max(48, cellWidth);
        cellHeight = Math.Max(48, cellHeight);

        bool positionsAvailable = TryReadDesktopIconCenters(
            listView,
            cellWidth,
            cellHeight,
            display.Scale,
            out IReadOnlyList<NativeMethods.POINT> centers);
        var snapshot = new DesktopGridSnapshot(
            display,
            cellWidth,
            cellHeight,
            centers.Where(point => Contains(display.Monitor, point)).ToArray(),
            positionsAvailable);
        _snapshotCache[display.Device] = (Stopwatch.GetTimestamp(), snapshot);
        return snapshot;
    }

    internal DesktopGridPlacement? FindFirstAvailable(
        DisplayInfo display,
        IReadOnlyList<NativeMethods.RECT> occupiedOrganizerBounds,
        double compactScale)
    {
        DesktopGridSnapshot snapshot = ReadSnapshot(display);
        return Find(snapshot, occupiedOrganizerBounds, desiredCenter: null, compactScale);
    }

    internal DesktopGridPlacement? FindNearestAvailable(
        DisplayInfo display,
        NativeMethods.POINT desiredCenter,
        IReadOnlyList<NativeMethods.RECT> occupiedOrganizerBounds,
        double compactScale)
    {
        DesktopGridSnapshot snapshot = ReadSnapshot(display);
        return Find(snapshot, occupiedOrganizerBounds, desiredCenter, compactScale);
    }

    internal static double CalculatePositionedCompactScale(DesktopGridSnapshot snapshot)
    {
        const double nameAndGapDip = 23;
        double widthScale = (snapshot.CellWidthPx - 8d * snapshot.Display.Scale) /
            (39d * snapshot.Display.Scale);
        double heightScale = (snapshot.CellHeightPx - nameAndGapDip * snapshot.Display.Scale) /
            (39d * snapshot.Display.Scale);
        return Math.Clamp(
            Math.Min(widthScale, heightScale),
            .25,
            OrganizerLimits.MaximumPositionedCompactScale);
    }

    internal static (int Width, int Height, int TileSize) CalculatePositionedWindowSize(
        DesktopGridSnapshot snapshot,
        double compactScale)
    {
        int tileSize = Math.Max(1, (int)Math.Round(39 * compactScale * snapshot.Display.Scale));
        int sidePadding = Math.Max(0, (int)Math.Round(8 * snapshot.Display.Scale));
        int nameAndGap = Math.Max(1, (int)Math.Round(23 * snapshot.Display.Scale));
        return (
            Math.Min(snapshot.CellWidthPx, tileSize + sidePadding),
            Math.Min(snapshot.CellHeightPx, tileSize + nameAndGap),
            tileSize);
    }

    internal static DesktopGridPlacement? Find(
        DesktopGridSnapshot snapshot,
        IReadOnlyList<NativeMethods.RECT> occupiedOrganizerBounds,
        NativeMethods.POINT? desiredCenter,
        double compactScale)
    {
        double requestedScale = Math.Clamp(
            compactScale,
            OrganizerLimits.MinimumCompactScale,
            OrganizerLimits.MaximumPositionedCompactScale);
        double scale = Math.Min(requestedScale, CalculatePositionedCompactScale(snapshot));
        (int width, int height, int tileSize) = CalculatePositionedWindowSize(snapshot, scale);
        List<GridSlot> slots = CreateSlots(snapshot, width, height, tileSize, scale, desiredCenter);

        IEnumerable<GridSlot> available = slots.Where(slot =>
            !snapshot.DesktopIconCenters.Any(center => IsSameCell(snapshot, slot.Center, center)) &&
            !occupiedOrganizerBounds.Any(bounds => OccupiesSlot(snapshot, slot, bounds, tileSize)));
        if (desiredCenter is NativeMethods.POINT center)
        {
            available = available
                .OrderBy(slot => DistanceSquared(slot.Center, center))
                .ThenBy(slot => slot.Cell.Top)
                .ThenBy(slot => slot.Cell.Left);
        }

        GridSlot? selected = available.Cast<GridSlot?>().FirstOrDefault();
        return selected is GridSlot slot
            ? new DesktopGridPlacement(slot.Bounds, slot.CompactScale, snapshot.ExplorerPositionsAvailable)
            : null;
    }

    internal static bool IsAligned(
        DesktopGridSnapshot snapshot,
        NativeMethods.RECT bounds,
        double compactScale,
        int tolerancePx = 1)
    {
        double scale = Math.Min(compactScale, CalculatePositionedCompactScale(snapshot));
        (int width, int height, int tileSize) = CalculatePositionedWindowSize(snapshot, scale);
        return CreateSlots(snapshot, width, height, tileSize, scale, desiredCenter: null).Any(slot =>
            Math.Abs(slot.Bounds.Left - bounds.Left) <= tolerancePx &&
            Math.Abs(slot.Bounds.Top - bounds.Top) <= tolerancePx &&
            Math.Abs(slot.Bounds.Width - bounds.Width) <= tolerancePx &&
            Math.Abs(slot.Bounds.Height - bounds.Height) <= tolerancePx);
    }

    private static List<GridSlot> CreateSlots(
        DesktopGridSnapshot snapshot,
        int width,
        int height,
        int tileSize,
        double compactScale,
        NativeMethods.POINT? desiredCenter)
    {
        NativeMethods.RECT work = snapshot.Display.Work;
        NativeMethods.POINT lattice = desiredCenter is NativeMethods.POINT desired
            ? snapshot.DesktopIconCenters.OrderBy(center => DistanceSquared(center, desired)).FirstOrDefault()
            : snapshot.DesktopIconCenters.OrderBy(center => center.Y).ThenBy(center => center.X).FirstOrDefault();
        if (snapshot.DesktopIconCenters.Count == 0)
        {
            lattice = new NativeMethods.POINT
            {
                X = work.Left + width / 2,
                Y = work.Top + tileSize / 2
            };
        }
        while (lattice.X - snapshot.CellWidthPx >= work.Left) lattice.X -= snapshot.CellWidthPx;
        while (lattice.X < work.Left) lattice.X += snapshot.CellWidthPx;
        while (lattice.Y - snapshot.CellHeightPx >= work.Top) lattice.Y -= snapshot.CellHeightPx;
        while (lattice.Y < work.Top) lattice.Y += snapshot.CellHeightPx;

        // 第一排的窗口不能伸出屏幕，也不能下探挡第二排：把第一排 tile 缩小，
        // 使窗口顶贴住工作区、底正好相切于第二排窗口顶。
        int sidePadding = Math.Max(0, (int)Math.Round(8 * snapshot.Display.Scale));
        int nameAndGap = Math.Max(1, (int)Math.Round(23 * snapshot.Display.Scale));
        int minimumTile = Math.Max(1, (int)Math.Round(39 * .25 * snapshot.Display.Scale));
        int topRowTileBudget = (lattice.Y - work.Top) + snapshot.CellHeightPx - tileSize / 2 - nameAndGap;
        int topRowTileSize = Math.Clamp(topRowTileBudget, minimumTile, tileSize);
        double topRowScale = topRowTileSize / (39d * Math.Max(1, snapshot.Display.Scale));

        var result = new List<GridSlot>();
        int column = 0;
        for (int centerX = lattice.X; centerX < work.Right; centerX += snapshot.CellWidthPx, column++)
        {
            int row = 0;
            for (int centerY = lattice.Y; centerY < work.Bottom; centerY += snapshot.CellHeightPx, row++)
            {
                bool topRow = row == 0 && topRowTileSize >= minimumTile;
                int rowTileSize = topRow ? topRowTileSize : tileSize;
                int rowWidth = topRow
                    ? Math.Min(snapshot.CellWidthPx, topRowTileSize + sidePadding)
                    : width;
                int rowHeight = topRow
                    ? Math.Min(snapshot.CellHeightPx, topRowTileSize + nameAndGap)
                    : height;
                int left = centerX - rowWidth / 2;
                int top = topRow
                    ? Math.Max(centerY - rowTileSize / 2, work.Top)
                    : centerY - rowTileSize / 2;
                var bounds = new NativeMethods.RECT
                {
                    Left = left,
                    Top = top,
                    Right = left + rowWidth,
                    Bottom = top + rowHeight
                };
                // 顶/左越界时保留该格（窗口可延伸出工作区，与图标单元格一致），
                // 底/右越界则整格跳过。
                if (bounds.Right > work.Right || bounds.Bottom > work.Bottom) continue;
                var cell = new NativeMethods.RECT
                {
                    Left = centerX - snapshot.CellWidthPx / 2,
                    Top = centerY - snapshot.CellHeightPx / 2,
                    Right = centerX - snapshot.CellWidthPx / 2 + snapshot.CellWidthPx,
                    Bottom = centerY - snapshot.CellHeightPx / 2 + snapshot.CellHeightPx
                };
                result.Add(new GridSlot(column, row, new NativeMethods.POINT { X = centerX, Y = centerY }, cell, bounds, topRow ? topRowScale : compactScale));
            }
        }
        return result;
    }

    private static bool IsSameCell(DesktopGridSnapshot snapshot, NativeMethods.POINT first, NativeMethods.POINT second) =>
        Math.Abs(first.X - second.X) < snapshot.CellWidthPx / 2d &&
        Math.Abs(first.Y - second.Y) < snapshot.CellHeightPx / 2d;

    private static bool OccupiesSlot(
        DesktopGridSnapshot snapshot,
        GridSlot candidate,
        NativeMethods.RECT occupiedBounds,
        int tileSize)
    {
        var occupiedVisibleCenter = new NativeMethods.POINT
        {
            X = occupiedBounds.Left + occupiedBounds.Width / 2,
            Y = occupiedBounds.Top + Math.Min(tileSize, occupiedBounds.Height) / 2
        };
        return IsSameCell(snapshot, candidate.Center, occupiedVisibleCenter) || Intersects(candidate.Bounds, occupiedBounds);
    }

    private static long DistanceSquared(NativeMethods.POINT first, NativeMethods.POINT second)
    {
        long x = first.X - second.X;
        long y = first.Y - second.Y;
        return x * x + y * y;
    }

    internal static bool TryDecodeItemSpacing(UIntPtr packedSpacing, out int width, out int height)
    {
        ulong packed = packedSpacing.ToUInt64();
        width = (int)(packed & 0xFFFF);
        height = (int)((packed >> 16) & 0xFFFF);
        return width is >= 48 and <= 512 && height is >= 48 and <= 512;
    }

    private static IntPtr FindDesktopListView()
    {
        IntPtr desktopView = DesktopLayerService.FindDesktopIconView();
        return desktopView == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.FindWindowEx(desktopView, IntPtr.Zero, "SysListView32", "FolderView");
    }

    private static bool TryReadActiveGridSpacing(IntPtr listView, out int width, out int height)
    {
        width = 0;
        height = 0;
        return listView != IntPtr.Zero &&
            NativeMethods.SendMessageTimeout(
                listView,
                NativeMethods.LVM_GETITEMSPACING,
                UIntPtr.Zero,
                IntPtr.Zero,
                0,
                250,
                out UIntPtr packedSpacing) != IntPtr.Zero &&
            TryDecodeItemSpacing(packedSpacing, out width, out height);
    }

    private static bool TryReadDesktopIconCenters(
        IntPtr listView,
        int cellWidth,
        int cellHeight,
        double displayScale,
        out IReadOnlyList<NativeMethods.POINT> centers)
    {
        centers = [];
        if (listView == IntPtr.Zero) return false;

        if (NativeMethods.SendMessageTimeout(listView, NativeMethods.LVM_GETITEMCOUNT, UIntPtr.Zero, IntPtr.Zero, 0, 250, out UIntPtr countResult) == IntPtr.Zero)
        {
            return false;
        }
        int count = unchecked((int)countResult.ToUInt64());
        if (count < 0 || count > 10000) return false;
        _ = NativeMethods.GetWindowThreadProcessId(listView, out uint processId);
        if (processId == 0) return false;

        IntPtr process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION |
            NativeMethods.PROCESS_VM_OPERATION |
            NativeMethods.PROCESS_VM_READ |
            NativeMethods.PROCESS_VM_WRITE,
            false,
            processId);
        if (process == IntPtr.Zero) return false;

        IntPtr remoteBuffer = IntPtr.Zero;
        try
        {
            UIntPtr pointSize = (UIntPtr)Marshal.SizeOf<NativeMethods.POINT>();
            UIntPtr rectSize = (UIntPtr)Marshal.SizeOf<NativeMethods.RECT>();
            remoteBuffer = NativeMethods.VirtualAllocEx(
                process,
                IntPtr.Zero,
                rectSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);
            if (remoteBuffer == IntPtr.Zero) return false;

            var clientOrigin = new NativeMethods.POINT();
            if (!NativeMethods.ClientToScreen(listView, ref clientOrigin)) return false;
            var result = new List<NativeMethods.POINT>(count);
            for (int index = 0; index < count; index++)
            {
                var iconRectRequest = new NativeMethods.RECT { Left = NativeMethods.LVIR_ICON };
                bool wroteRequest = NativeMethods.WriteProcessMemory(
                    process,
                    remoteBuffer,
                    ref iconRectRequest,
                    rectSize,
                    out UIntPtr bytesWritten) && bytesWritten.ToUInt64() >= rectSize.ToUInt64();
                IntPtr sent = NativeMethods.SendMessageTimeout(
                    listView,
                    NativeMethods.LVM_GETITEMRECT,
                    (UIntPtr)(uint)index,
                    remoteBuffer,
                    0,
                    250,
                    out UIntPtr success);
                if (wroteRequest && sent != IntPtr.Zero && success != UIntPtr.Zero &&
                    NativeMethods.ReadProcessMemory(process, remoteBuffer, out NativeMethods.RECT iconRect, rectSize, out UIntPtr rectBytesRead) &&
                    rectBytesRead.ToUInt64() >= rectSize.ToUInt64() && iconRect.Width > 0 && iconRect.Height > 0)
                {
                    result.Add(new NativeMethods.POINT
                    {
                        X = clientOrigin.X + iconRect.Left + iconRect.Width / 2,
                        Y = clientOrigin.Y + iconRect.Top + iconRect.Height / 2
                    });
                    continue;
                }

                sent = NativeMethods.SendMessageTimeout(
                    listView,
                    NativeMethods.LVM_GETITEMPOSITION,
                    (UIntPtr)(uint)index,
                    remoteBuffer,
                    0,
                    250,
                    out success);
                if (sent == IntPtr.Zero || success == UIntPtr.Zero) return false;
                if (!NativeMethods.ReadProcessMemory(process, remoteBuffer, out NativeMethods.POINT point, pointSize, out UIntPtr pointBytesRead) ||
                    pointBytesRead.ToUInt64() < pointSize.ToUInt64()) return false;
                result.Add(new NativeMethods.POINT
                {
                    X = clientOrigin.X + point.X + cellWidth / 2,
                    Y = clientOrigin.Y + point.Y + Math.Min(cellHeight / 2, (int)Math.Round(24 * displayScale))
                });
            }
            centers = result;
            return true;
        }
        finally
        {
            if (remoteBuffer != IntPtr.Zero) _ = NativeMethods.VirtualFreeEx(process, remoteBuffer, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            _ = NativeMethods.CloseHandle(process);
        }
    }

    private static bool Contains(NativeMethods.RECT rect, NativeMethods.POINT point) =>
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

    private static bool Intersects(NativeMethods.RECT first, NativeMethods.RECT second) =>
        first.Left < second.Right && first.Right > second.Left && first.Top < second.Bottom && first.Bottom > second.Top;
}
