using System.Runtime.InteropServices;
using System.Diagnostics;

namespace TuckPane.Services;

internal enum DesktopIconPlacementStatus
{
    Positioned,
    AutoArrangeEnabled,
    Failed
}

internal readonly record struct DesktopIconPlacementResult(
    DesktopIconPlacementStatus Status,
    NativeMethods.POINT? Position,
    string? Warning);

internal sealed class DesktopIconPlacementService
{
    private const int DesktopCsidl = 0;
    private const int ShellWindowClassDesktop = 8;
    private const int FindWindowNeedDispatch = 1;
    private const int Success = 0;
    private const int False = 1;
    private const uint SVSI_POSITIONITEM = 0x00000080;
    private const uint SVSI_NOSTATECHANGE = 0x80000000;

    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserInterface = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid ShellFolderInterface = new("000214E6-0000-0000-C000-000000000046");

    internal async Task<DesktopIconPlacementResult> PositionAsync(
        string finalDesktopPath,
        NativeMethods.POINT dropScreenPoint,
        CancellationToken cancellationToken = default)
    {
        string finalPath = Path.GetFullPath(finalDesktopPath);
        TimeSpan timeout = TimeSpan.FromSeconds(1);
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        DesktopIconPlacementResult last = new(DesktopIconPlacementStatus.Failed, null, "桌面图标视图尚未载入新项目。");
        while (System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = TryPosition(finalPath, dropScreenPoint);
            if (last.Status != DesktopIconPlacementStatus.Failed) return last;
            await Task.Delay(50, cancellationToken);
        }
        return last with { Warning = $"桌面图标定位在 1 秒内未完成：{last.Warning}" };
    }

    internal async Task<DesktopIconPlacementResult> MoveAwayIfOverlappedAsync(
        string finalDesktopPath,
        IReadOnlyList<NativeMethods.RECT> organizerBounds,
        CancellationToken cancellationToken = default)
    {
        string finalPath = Path.GetFullPath(finalDesktopPath);
        if (organizerBounds.Count == 0) return new(DesktopIconPlacementStatus.Positioned, null, null);
        TimeSpan timeout = TimeSpan.FromSeconds(1);
        long startedAt = Stopwatch.GetTimestamp();
        DesktopIconPlacementResult last = new(DesktopIconPlacementStatus.Failed, null, "桌面图标视图尚未载入新项目。");
        while (Stopwatch.GetElapsedTime(startedAt) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = TryMoveAwayIfOverlapped(finalPath, organizerBounds);
            if (last.Status != DesktopIconPlacementStatus.Failed) return last;
            await Task.Delay(50, cancellationToken);
        }
        return last with { Warning = $"桌面图标定位在 1 秒内未完成：{last.Warning}" };
    }

    private static DesktopIconPlacementResult TryPosition(string finalPath, NativeMethods.POINT dropScreenPoint)
    {
        object? shellWindowsObject = null;
        object? desktopDispatch = null;
        object? browserObject = null;
        IShellView? shellView = null;
        object? folderObject = null;
        IntPtr childPidl = IntPtr.Zero;
        try
        {
            Type shellWindowsType = Type.GetTypeFromCLSID(
                new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"),
                throwOnError: true)!;
            shellWindowsObject = Activator.CreateInstance(shellWindowsType)
                ?? throw new InvalidOperationException("无法创建桌面 ShellWindows 服务。");
            var shellWindows = (IShellWindows)shellWindowsObject;
            object location = DesktopCsidl;
            object root = new();
            desktopDispatch = shellWindows.FindWindowSW(
                ref location,
                ref root,
                ShellWindowClassDesktop,
                out _,
                FindWindowNeedDispatch);
            if (desktopDispatch is not IServiceProvider serviceProvider)
                throw new InvalidCastException("桌面窗口未提供 IServiceProvider。");

            Guid service = TopLevelBrowserService;
            Guid browserIid = ShellBrowserInterface;
            int hresult = serviceProvider.QueryService(ref service, ref browserIid, out IntPtr browserPointer);
            Marshal.ThrowExceptionForHR(hresult);
            try
            {
                browserObject = Marshal.GetObjectForIUnknown(browserPointer);
            }
            finally
            {
                _ = Marshal.Release(browserPointer);
            }

            var browser = (IShellBrowser)browserObject;
            hresult = browser.QueryActiveShellView(out shellView);
            Marshal.ThrowExceptionForHR(hresult);
            hresult = shellView.GetWindow(out IntPtr activeViewWindow);
            Marshal.ThrowExceptionForHR(hresult);
            IntPtr expectedView = DesktopLayerService.FindDesktopIconView();
            if (expectedView == IntPtr.Zero || activeViewWindow != expectedView)
                throw new InvalidOperationException("活动 Shell 视图不是当前桌面图标视图。");

            var folderView = (IFolderView)shellView;
            var spacing = new NativeMethods.POINT();
            hresult = folderView.GetSpacing(ref spacing);
            Marshal.ThrowExceptionForHR(hresult);
            if (spacing.X <= 0 || spacing.Y <= 0)
                throw new InvalidOperationException("桌面图标网格间距无效。");

            Guid folderIid = ShellFolderInterface;
            hresult = folderView.GetFolder(ref folderIid, out IntPtr folderPointer);
            Marshal.ThrowExceptionForHR(hresult);
            try
            {
                folderObject = Marshal.GetObjectForIUnknown(folderPointer);
            }
            finally
            {
                _ = Marshal.Release(folderPointer);
            }
            var shellFolder = (IShellFolder)folderObject;
            hresult = shellFolder.ParseDisplayName(
                IntPtr.Zero,
                IntPtr.Zero,
                Path.GetFileName(finalPath),
                IntPtr.Zero,
                out childPidl,
                IntPtr.Zero);
            Marshal.ThrowExceptionForHR(hresult);

            hresult = folderView.GetItemPosition(childPidl, out _);
            if (hresult != Success)
                return new(DesktopIconPlacementStatus.Failed, null, "Explorer 桌面视图尚未载入新项目。");

            int autoArrange = folderView.GetAutoArrange();
            if (autoArrange == Success)
                return new(DesktopIconPlacementStatus.AutoArrangeEnabled, null, null);
            if (autoArrange != False) Marshal.ThrowExceptionForHR(autoArrange);

            IntPtr listView = NativeMethods.FindWindowEx(activeViewWindow, IntPtr.Zero, "SysListView32", "FolderView");
            if (listView == IntPtr.Zero)
                throw new InvalidOperationException("无法读取桌面图标视图范围。");

            NativeMethods.POINT dropClientPoint = dropScreenPoint;
            if (!NativeMethods.ScreenToClient(listView, ref dropClientPoint))
                throw new InvalidOperationException("无法把桌面落点转换到图标视图客户区。");
            IntPtr monitor = NativeMethods.MonitorFromPoint(dropScreenPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                szDevice = string.Empty
            };
            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
                throw new InvalidOperationException("无法读取落点显示器工作区。");
            var workTopLeft = new NativeMethods.POINT { X = monitorInfo.rcWork.Left, Y = monitorInfo.rcWork.Top };
            var workBottomRight = new NativeMethods.POINT { X = monitorInfo.rcWork.Right, Y = monitorInfo.rcWork.Bottom };
            if (!NativeMethods.ScreenToClient(listView, ref workTopLeft) ||
                !NativeMethods.ScreenToClient(listView, ref workBottomRight))
                throw new InvalidOperationException("无法转换显示器工作区坐标。");
            var monitorClientBounds = new NativeMethods.RECT
            {
                Left = Math.Min(workTopLeft.X, workBottomRight.X),
                Top = Math.Min(workTopLeft.Y, workBottomRight.Y),
                Right = Math.Max(workTopLeft.X, workBottomRight.X),
                Bottom = Math.Max(workTopLeft.Y, workBottomRight.Y)
            };
            NativeMethods.POINT position = DesktopIconPlacementMath.SnapToGrid(dropClientPoint, monitorClientBounds, spacing);
            hresult = folderView.SelectAndPositionItems(
                1,
                [childPidl],
                [position],
                SVSI_POSITIONITEM | SVSI_NOSTATECHANGE);
            Marshal.ThrowExceptionForHR(hresult);
            hresult = folderView.GetItemPosition(childPidl, out NativeMethods.POINT actualPosition);
            if (hresult != Success)
                return new(DesktopIconPlacementStatus.Failed, null, "桌面图标写入后无法读回位置。");
            int toleranceX = Math.Max(12, spacing.X / 2);
            int toleranceY = Math.Max(12, spacing.Y / 2);
            bool onTargetMonitor = actualPosition.X >= monitorClientBounds.Left &&
                actualPosition.Y >= monitorClientBounds.Top &&
                actualPosition.X + spacing.X <= monitorClientBounds.Right &&
                actualPosition.Y + spacing.Y <= monitorClientBounds.Bottom;
            if (!onTargetMonitor || Math.Abs(actualPosition.X - position.X) > toleranceX ||
                Math.Abs(actualPosition.Y - position.Y) > toleranceY)
            {
                return new(DesktopIconPlacementStatus.Failed, actualPosition, "桌面图标读回位置与目标网格不一致。");
            }
            return new(DesktopIconPlacementStatus.Positioned, actualPosition, null);
        }
        catch (Exception ex)
        {
            return new(DesktopIconPlacementStatus.Failed, null, ex.Message);
        }
        finally
        {
            if (childPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(childPidl);
            ReleaseComObject(folderObject);
            ReleaseComObject(shellView);
            ReleaseComObject(browserObject);
            ReleaseComObject(desktopDispatch);
            ReleaseComObject(shellWindowsObject);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.ReleaseComObject(value);
    }

    private static DesktopIconPlacementResult TryMoveAwayIfOverlapped(
        string finalPath,
        IReadOnlyList<NativeMethods.RECT> organizerBounds)
    {
        object? shellWindowsObject = null;
        object? desktopDispatch = null;
        object? browserObject = null;
        object? folderObject = null;
        IShellView? shellView = null;
        IntPtr childPidl = IntPtr.Zero;
        List<IntPtr> readPidls = [];
        try
        {
            Type shellWindowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!;
            shellWindowsObject = Activator.CreateInstance(shellWindowsType)
                ?? throw new InvalidOperationException("无法创建桌面 ShellWindows 服务。");
            var shellWindows = (IShellWindows)shellWindowsObject;
            object location = 0;
            object root = new();
            desktopDispatch = shellWindows.FindWindowSW(ref location, ref root, ShellWindowClassDesktop, out _, FindWindowNeedDispatch);
            if (desktopDispatch is not IServiceProvider serviceProvider)
                throw new InvalidCastException("桌面窗口未提供 IServiceProvider。");

            Guid service = TopLevelBrowserService;
            Guid browserIid = ShellBrowserInterface;
            int hresult = serviceProvider.QueryService(ref service, ref browserIid, out IntPtr browserPointer);
            Marshal.ThrowExceptionForHR(hresult);
            try
            {
                browserObject = Marshal.GetObjectForIUnknown(browserPointer);
            }
            finally
            {
                _ = Marshal.Release(browserPointer);
            }

            var browser = (IShellBrowser)browserObject;
            hresult = browser.QueryActiveShellView(out shellView);
            Marshal.ThrowExceptionForHR(hresult);
            hresult = shellView.GetWindow(out IntPtr activeViewWindow);
            Marshal.ThrowExceptionForHR(hresult);
            IntPtr expectedView = DesktopLayerService.FindDesktopIconView();
            if (expectedView == IntPtr.Zero || activeViewWindow != expectedView)
                throw new InvalidOperationException("活动 Shell 视图不是当前桌面图标视图。");

            var folderView = (IFolderView)shellView;
            var spacing = new NativeMethods.POINT();
            hresult = folderView.GetSpacing(ref spacing);
            Marshal.ThrowExceptionForHR(hresult);
            if (spacing.X <= 0 || spacing.Y <= 0) throw new InvalidOperationException("桌面图标网格间距无效。");

            Guid folderIid = ShellFolderInterface;
            hresult = folderView.GetFolder(ref folderIid, out IntPtr folderPointer);
            Marshal.ThrowExceptionForHR(hresult);
            try
            {
                folderObject = Marshal.GetObjectForIUnknown(folderPointer);
            }
            finally
            {
                _ = Marshal.Release(folderPointer);
            }
            var shellFolder = (IShellFolder)folderObject;
            hresult = shellFolder.ParseDisplayName(
                IntPtr.Zero,
                IntPtr.Zero,
                Path.GetFileName(finalPath),
                IntPtr.Zero,
                out childPidl,
                IntPtr.Zero);
            Marshal.ThrowExceptionForHR(hresult);

            hresult = folderView.GetItemPosition(childPidl, out _);
            if (hresult != Success)
                return new(DesktopIconPlacementStatus.Failed, null, "Explorer 桌面视图尚未载入新项目。");

            IntPtr listView = NativeMethods.FindWindowEx(activeViewWindow, IntPtr.Zero, "SysListView32", "FolderView");
            if (listView == IntPtr.Zero) throw new InvalidOperationException("无法读取桌面图标视图范围。");

            // 新文件图标在桌面网格中的实际格子（列表视图客户区的左上角，与 GetItemPosition 一致）。
            var sourceCell = new NativeMethods.POINT();
            hresult = folderView.GetItemPosition(childPidl, out sourceCell);
            if (hresult != Success) return new(DesktopIconPlacementStatus.Failed, null, "无法读取新项目位置。");

            // 用图标格子中心（屏幕坐标）判断是否落在某个折叠态收纳盒内。
            var sourceCenter = new NativeMethods.POINT { X = sourceCell.X + spacing.X / 2, Y = sourceCell.Y + spacing.Y / 2 };
            var sourceCenterScreen = sourceCenter;
            if (!NativeMethods.ClientToScreen(listView, ref sourceCenterScreen))
                throw new InvalidOperationException("无法把项目位置转换到屏幕坐标。");
            bool overlapped = organizerBounds.Any(bounds =>
                sourceCenterScreen.X >= bounds.Left && sourceCenterScreen.X < bounds.Right &&
                sourceCenterScreen.Y >= bounds.Top && sourceCenterScreen.Y < bounds.Bottom);
            if (!overlapped) return new(DesktopIconPlacementStatus.Positioned, sourceCenterScreen, null);

            NativeMethods.RECT monitorClientBounds = ReadMonitorClientBounds(listView, sourceCenterScreen);
            IReadOnlyList<NativeMethods.RECT> organizerClientRects = ToClientRects(listView, organizerBounds);

            // 收集桌面图标占用的格子（客户区左上角），仅用于避让，绝不移动它们。
            var occupiedCells = new HashSet<(int X, int Y)>();
            if (folderView.ItemCount(0, out int count) == Success && count is > 0 and <= 10000)
            {
                for (int index = 0; index < count; index++)
                {
                    if (folderView.Item(index, out IntPtr itemPidl) < 0) continue;
                    readPidls.Add(itemPidl);
                    var itemCell = new NativeMethods.POINT();
                    if (folderView.GetItemPosition(itemPidl, out itemCell) < 0) continue;
                    occupiedCells.Add((itemCell.X, itemCell.Y));
                }
            }

            if (!TryFindNearestFreeCell(
                    sourceCell,
                    spacing,
                    organizerClientRects,
                    occupiedCells,
                    monitorClientBounds,
                    out NativeMethods.POINT targetCell))
                return new(DesktopIconPlacementStatus.Positioned, sourceCenterScreen, null);

            hresult = folderView.SelectAndPositionItems(1, [childPidl], [targetCell], SVSI_POSITIONITEM | SVSI_NOSTATECHANGE);
            Marshal.ThrowExceptionForHR(hresult);
            var actual = new NativeMethods.POINT();
            if (folderView.GetItemPosition(childPidl, out actual) != Success)
                return new(DesktopIconPlacementStatus.Failed, null, "挪动后无法读回位置。");
            int toleranceX = Math.Max(8, spacing.X / 4);
            int toleranceY = Math.Max(8, spacing.Y / 4);
            if (Math.Abs(actual.X - targetCell.X) > toleranceX || Math.Abs(actual.Y - targetCell.Y) > toleranceY)
                return new(DesktopIconPlacementStatus.Failed, actual, "桌面图标读回位置与目标网格不一致。");
            var actualCenter = new NativeMethods.POINT { X = actual.X + spacing.X / 2, Y = actual.Y + spacing.Y / 2 };
            var actualCenterScreen = actualCenter;
            NativeMethods.ClientToScreen(listView, ref actualCenterScreen);
            return new(DesktopIconPlacementStatus.Positioned, actualCenterScreen, null);
        }
        catch (Exception ex)
        {
            return new(DesktopIconPlacementStatus.Failed, null, ex.Message);
        }
        finally
        {
            if (childPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(childPidl);
            foreach (IntPtr pidl in readPidls) Marshal.FreeCoTaskMem(pidl);
            ReleaseComObject(folderObject);
            ReleaseComObject(shellView);
            ReleaseComObject(browserObject);
            ReleaseComObject(desktopDispatch);
            ReleaseComObject(shellWindowsObject);
        }
    }

    private static NativeMethods.RECT ReadMonitorClientBounds(IntPtr listView, NativeMethods.POINT screenPoint)
    {
        IntPtr monitor = NativeMethods.MonitorFromPoint(screenPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
            szDevice = string.Empty
        };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
            throw new InvalidOperationException("无法读取落点显示器工作区。");
        var topLeft = new NativeMethods.POINT { X = monitorInfo.rcWork.Left, Y = monitorInfo.rcWork.Top };
        var bottomRight = new NativeMethods.POINT { X = monitorInfo.rcWork.Right, Y = monitorInfo.rcWork.Bottom };
        if (!NativeMethods.ScreenToClient(listView, ref topLeft) || !NativeMethods.ScreenToClient(listView, ref bottomRight))
            throw new InvalidOperationException("无法转换显示器工作区坐标。");
        return new NativeMethods.RECT
        {
            Left = Math.Min(topLeft.X, bottomRight.X),
            Top = Math.Min(topLeft.Y, bottomRight.Y),
            Right = Math.Max(topLeft.X, bottomRight.X),
            Bottom = Math.Max(topLeft.Y, bottomRight.Y)
        };
    }

    private static IReadOnlyList<NativeMethods.RECT> ToClientRects(IntPtr listView, IReadOnlyList<NativeMethods.RECT> screenRects)
    {
        var result = new List<NativeMethods.RECT>(screenRects.Count);
        foreach (var rect in screenRects)
        {
            var topLeft = new NativeMethods.POINT { X = rect.Left, Y = rect.Top };
            var bottomRight = new NativeMethods.POINT { X = rect.Right, Y = rect.Bottom };
            if (!NativeMethods.ScreenToClient(listView, ref topLeft) || !NativeMethods.ScreenToClient(listView, ref bottomRight))
                continue;
            result.Add(new NativeMethods.RECT
            {
                Left = Math.Min(topLeft.X, bottomRight.X),
                Top = Math.Min(topLeft.Y, bottomRight.Y),
                Right = Math.Max(topLeft.X, bottomRight.X),
                Bottom = Math.Max(topLeft.Y, bottomRight.Y)
            });
        }
        return result;
    }

    private static bool TryFindNearestFreeCell(
        NativeMethods.POINT sourceCell,
        NativeMethods.POINT spacing,
        IReadOnlyList<NativeMethods.RECT> organizerClientRects,
        HashSet<(int X, int Y)> occupiedCells,
        NativeMethods.RECT monitorClientBounds,
        out NativeMethods.POINT targetCell)
    {
        targetCell = default;
        int width = monitorClientBounds.Right - monitorClientBounds.Left;
        int height = monitorClientBounds.Bottom - monitorClientBounds.Top;
        int maxColumns = Math.Max(1, width / spacing.X) + 2;
        int maxRows = Math.Max(1, height / spacing.Y) + 2;
        int maxRadius = Math.Max(maxColumns, maxRows);

        for (int ring = 1; ring <= maxRadius; ring++)
        {
            var candidates = new List<NativeMethods.POINT>();
            for (int dx = -ring; dx <= ring; dx++)
            {
                for (int dy = -ring; dy <= ring; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring) continue;
                    candidates.Add(new NativeMethods.POINT
                    {
                        X = sourceCell.X + dx * spacing.X,
                        Y = sourceCell.Y + dy * spacing.Y
                    });
                }
            }
            candidates.Sort((a, b) =>
            {
                long da = (long)(a.X - sourceCell.X) * (a.X - sourceCell.X) + (long)(a.Y - sourceCell.Y) * (a.Y - sourceCell.Y);
                long db = (long)(b.X - sourceCell.X) * (b.X - sourceCell.X) + (long)(b.Y - sourceCell.Y) * (b.Y - sourceCell.Y);
                return da.CompareTo(db);
            });

            foreach (var candidate in candidates)
            {
                bool inside = candidate.X >= monitorClientBounds.Left &&
                    candidate.Y >= monitorClientBounds.Top &&
                    candidate.X + spacing.X <= monitorClientBounds.Right &&
                    candidate.Y + spacing.Y <= monitorClientBounds.Bottom;
                if (!inside) continue;
                if (occupiedCells.Contains((candidate.X, candidate.Y))) continue;
                if (organizerClientRects.Any(rect => CellIntersectsRect(candidate, spacing, rect))) continue;
                targetCell = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool CellIntersectsRect(NativeMethods.POINT cellTopLeft, NativeMethods.POINT spacing, NativeMethods.RECT other)
    {
        int left = cellTopLeft.X;
        int top = cellTopLeft.Y;
        int right = left + spacing.X;
        int bottom = top + spacing.Y;
        return left < other.Right && right > other.Left && top < other.Bottom && bottom > other.Top;
    }

    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    internal interface IShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(
            [In, Out, MarshalAs(UnmanagedType.Struct)] ref object location,
            [In, Out, MarshalAs(UnmanagedType.Struct)] ref object root,
            int windowClass,
            out int window,
            int options);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid interfaceId, out IntPtr result);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
        [PreserveSig] int InsertMenusSB(IntPtr menu, IntPtr widths);
        [PreserveSig] int SetMenuSB(IntPtr menu, IntPtr oleMenu, IntPtr activeObject);
        [PreserveSig] int RemoveMenusSB(IntPtr menu);
        [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr message, ushort id);
        [PreserveSig] int BrowseObject(IntPtr itemIdList, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr window);
        [PreserveSig] int SendControlMsg(uint id, uint message, UIntPtr wParam, IntPtr lParam, out IntPtr result);
        [PreserveSig] int QueryActiveShellView(out IShellView shellView);
    }

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr window);
    }

    [ComImport]
    [Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint viewMode);
        [PreserveSig] int SetCurrentViewMode(uint viewMode);
        [PreserveSig] int GetFolder(ref Guid interfaceId, out IntPtr folder);
        [PreserveSig] int Item(int index, out IntPtr itemIdList);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, ref Guid interfaceId, out IntPtr items);
        [PreserveSig] int GetSelectionMarkedItem(out int index);
        [PreserveSig] int GetFocusedItem(out int index);
        [PreserveSig] int GetItemPosition(IntPtr itemIdList, out NativeMethods.POINT point);
        [PreserveSig] int GetSpacing(ref NativeMethods.POINT point);
        [PreserveSig] int GetDefaultSpacing(out NativeMethods.POINT point);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int index, uint flags);
        [PreserveSig] int SelectAndPositionItems(
            uint count,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] itemIdLists,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] NativeMethods.POINT[] points,
            uint flags);
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(
            IntPtr owner,
            IntPtr bindContext,
            [MarshalAs(UnmanagedType.LPWStr)] string displayName,
            IntPtr charactersEaten,
            out IntPtr itemIdList,
            IntPtr attributes);
    }
}
