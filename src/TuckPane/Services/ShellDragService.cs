using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using TuckPane.Models;

namespace TuckPane.Services;

[ComVisible(true)]
[Guid("00000121-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropSource
{
    [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, uint keyState);
    [PreserveSig] int GiveFeedback(uint effect);
}

[StructLayout(LayoutKind.Sequential)]
public struct ShellDragSize { public int Width; public int Height; }

[StructLayout(LayoutKind.Sequential)]
public struct ShellDragPoint { public int X; public int Y; }

[StructLayout(LayoutKind.Sequential)]
public struct ShellDragImage
{
    public ShellDragSize Size;
    public ShellDragPoint Offset;
    public IntPtr Bitmap;
    public uint ColorKey;
}

[ComImport]
[Guid("DE5BF786-477A-11D2-839D-00C04FD918D0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellDragSourceHelper
{
    [PreserveSig] int InitializeFromBitmap(ref ShellDragImage dragImage, IntPtr dataObject);
    [PreserveSig] int InitializeFromWindow(IntPtr window, ref ShellDragPoint point, IntPtr dataObject);
}

internal enum ShellDragOutcome
{
    ExternalCopied,
    ExternalMoved,
    ExternalLinked,
    DesktopRequested,
    Cancelled,
}

internal readonly record struct ShellDragResult(
    ShellDragOutcome Outcome,
    NativeMethods.POINT? DesktopDropPoint,
    TimeSpan PreparationDuration,
    TimeSpan? FirstFeedbackDelay,
    int CallbackCount,
    TimeSpan MaximumCallbackInterval,
    TimeSpan DragDuration);

internal static class ShellDragService
{
    private const int DragImageSize = 64;
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;
    private const uint DropEffectLink = 4;
    private const short CfHDrop = 15;
    private const uint GlobalMoveable = 0x0002;
    private const uint GlobalZeroInit = 0x0040;
    private const uint MouseKeyLeft = 0x0001;
    private const int Success = 0;
    private const int DragDropDrop = 0x00040100;
    private const int DragDropCancel = 0x00040101;
    private const int DragDropUseDefaultCursors = 0x00040102;
    private static readonly Guid ShellItemInterface = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid DataObjectHandler = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    private static readonly Guid DataObjectInterface = new("0000010E-0000-0000-C000-000000000046");
    private static readonly Guid DragDropHelperClass = new("4657278A-411B-11D2-839A-00C04FD918D0");
    private const string PreferredDropEffectFormat = "Preferred DropEffect";

    internal static bool RequiresNativeDrag(WidgetItemKind kind) => Enum.IsDefined(kind);

    internal static void SetCutClipboard(string path)
    {
        int oleResult = OleInitialize(IntPtr.Zero);
        if (oleResult < 0) Marshal.ThrowExceptionForHR(oleResult);
        try
        {
            using ShellDataObject data = CreateDataObject(path);
            object dataObject = Marshal.GetObjectForIUnknown(data.Pointer);
            try
            {
                SetPreferredDropEffect((IDataObject)dataObject, DropEffectMove);
                Marshal.ThrowExceptionForHR(OleSetClipboard(data.Pointer));
                Marshal.ThrowExceptionForHR(OleFlushClipboard());
            }
            finally
            {
                if (Marshal.IsComObject(dataObject)) _ = Marshal.ReleaseComObject(dataObject);
            }
        }
        finally
        {
            OleUninitialize();
        }
    }

    internal static bool TryGetClipboardPaths(out string[] paths, out bool move)
    {
        paths = [];
        move = false;
        int oleResult = OleInitialize(IntPtr.Zero);
        if (oleResult < 0) return false;
        IntPtr pointer = IntPtr.Zero;
        object? dataObject = null;
        try
        {
            if (OleGetClipboard(out pointer) < 0 || pointer == IntPtr.Zero) return false;
            dataObject = Marshal.GetObjectForIUnknown(pointer);
            var clipboard = (IDataObject)dataObject;
            paths = ReadFileDropPaths(clipboard);
            move = (ReadPreferredDropEffect(clipboard) & DropEffectMove) != 0;
            return paths.Length > 0;
        }
        catch (COMException)
        {
            paths = [];
            move = false;
            return false;
        }
        finally
        {
            if (dataObject is not null && Marshal.IsComObject(dataObject)) _ = Marshal.ReleaseComObject(dataObject);
            if (pointer != IntPtr.Zero) _ = Marshal.Release(pointer);
            OleUninitialize();
        }
    }

    private static void SetPreferredDropEffect(IDataObject dataObject, uint effect)
    {
        FORMATETC format = CreateFormat(unchecked((short)RegisterClipboardFormat(PreferredDropEffectFormat)));
        IntPtr memory = GlobalAlloc(GlobalMoveable | GlobalZeroInit, (UIntPtr)sizeof(uint));
        if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        try
        {
            IntPtr value = GlobalLock(memory);
            if (value == IntPtr.Zero) throw new OutOfMemoryException();
            try { Marshal.WriteInt32(value, unchecked((int)effect)); }
            finally { _ = GlobalUnlock(memory); }
            var medium = new STGMEDIUM
            {
                tymed = TYMED.TYMED_HGLOBAL,
                unionmember = memory,
                pUnkForRelease = null
            };
            dataObject.SetData(ref format, ref medium, release: true);
            memory = IntPtr.Zero;
        }
        finally
        {
            if (memory != IntPtr.Zero) _ = GlobalFree(memory);
        }
    }

    private static string[] ReadFileDropPaths(IDataObject dataObject)
    {
        FORMATETC format = CreateFormat(CfHDrop);
        if (dataObject.QueryGetData(ref format) != 0) return [];
        dataObject.GetData(ref format, out STGMEDIUM medium);
        try
        {
            uint count = DragQueryFile(medium.unionmember, uint.MaxValue, null, 0);
            var result = new List<string>(checked((int)count));
            for (uint index = 0; index < count; index++)
            {
                uint length = DragQueryFile(medium.unionmember, index, null, 0);
                var buffer = new StringBuilder(checked((int)length + 1));
                if (DragQueryFile(medium.unionmember, index, buffer, (uint)buffer.Capacity) > 0)
                    result.Add(buffer.ToString());
            }
            return result.ToArray();
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static uint ReadPreferredDropEffect(IDataObject dataObject)
    {
        FORMATETC format = CreateFormat(unchecked((short)RegisterClipboardFormat(PreferredDropEffectFormat)));
        if (dataObject.QueryGetData(ref format) != 0) return 0;
        dataObject.GetData(ref format, out STGMEDIUM medium);
        try
        {
            IntPtr value = GlobalLock(medium.unionmember);
            if (value == IntPtr.Zero) return 0;
            try { return unchecked((uint)Marshal.ReadInt32(value)); }
            finally { _ = GlobalUnlock(medium.unionmember); }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static FORMATETC CreateFormat(short format) => new()
    {
        cfFormat = format,
        ptd = IntPtr.Zero,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = TYMED.TYMED_HGLOBAL
    };

    internal static ShellDragResult Move(IntPtr owner, string path, Func<bool>? cancellationRequested = null)
    {
        using ShellDragSession session = Prepare(path);
        return Move(owner, session, default, cancellationRequested);
    }

    internal static ShellDragSession Prepare(string path, double grabRatioX = .5, double grabRatioY = .5)
    {
        long started = Stopwatch.GetTimestamp();
        ShellDataObject data = CreateDataObject(path);
        IntPtr bitmap = IntPtr.Zero;
        bool dragImageInitialized = false;
        try
        {
            try
            {
                bitmap = IconCacheService.CreateDragBitmap(path, DragImageSize);
                var grabOffset = new ShellDragPoint
                {
                    X = Math.Clamp((int)Math.Round(grabRatioX * DragImageSize), 0, DragImageSize - 1),
                    Y = Math.Clamp((int)Math.Round(grabRatioY * DragImageSize), 0, DragImageSize - 1)
                };
                dragImageInitialized = TryInitializeDragImage(data.Pointer, bitmap, grabOffset);
            }
            catch (Exception ex)
            {
                AppLogger.Error("无法准备自定义Shell拖动图像，将继续使用系统默认反馈。", ex);
            }
            return new ShellDragSession(path, data, bitmap, dragImageInitialized, Stopwatch.GetElapsedTime(started));
        }
        catch
        {
            if (bitmap != IntPtr.Zero) _ = NativeMethods.DeleteObject(bitmap);
            data.Dispose();
            throw;
        }
    }

    internal static ShellDragResult Move(
        IntPtr owner,
        ShellDragSession session,
        NativeMethods.RECT desktopExclusionBounds,
        Func<bool>? cancellationRequested = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ThrowIfDisposed();
        long dragStarted = Stopwatch.GetTimestamp();
        var dropSource = new DesktopAwareDropSource(owner, desktopExclusionBounds, dragStarted, cancellationRequested);
        int oleResult = OleInitialize(IntPtr.Zero);
        if (oleResult < 0) Marshal.ThrowExceptionForHR(oleResult);
        IntPtr dropSourcePointer = IntPtr.Zero;
        try
        {
            dropSourcePointer = AcquireDropSourcePointer(dropSource);
            int result = DoDragDrop(
                session.Data.Pointer,
                dropSourcePointer,
                DropEffectCopy | DropEffectMove | DropEffectLink,
                out uint performed);
            session.DragDuration = Stopwatch.GetElapsedTime(dragStarted);
            session.FirstFeedbackDelay = dropSource.FirstFeedbackDelay;
            session.CallbackCount = dropSource.CallbackCount;
            session.MaximumCallbackInterval = dropSource.MaximumCallbackInterval;
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            ShellDragOutcome outcome = ClassifyOutcome(dropSource.DesktopRequested, performed);
            return new ShellDragResult(
                outcome,
                dropSource.DesktopDropPoint,
                session.PreparationDuration,
                session.FirstFeedbackDelay,
                session.CallbackCount,
                session.MaximumCallbackInterval,
                session.DragDuration);
        }
        finally
        {
            if (dropSourcePointer != IntPtr.Zero) _ = Marshal.Release(dropSourcePointer);
            OleUninitialize();
            GC.KeepAlive(dropSource);
        }
    }

    internal static ShellDragOutcome ClassifyOutcome(bool desktopRequested, uint performed) =>
        desktopRequested ? ShellDragOutcome.DesktopRequested :
        (performed & DropEffectMove) != 0 ? ShellDragOutcome.ExternalMoved :
        (performed & DropEffectCopy) != 0 ? ShellDragOutcome.ExternalCopied :
        (performed & DropEffectLink) != 0 ? ShellDragOutcome.ExternalLinked :
        ShellDragOutcome.Cancelled;

    private static bool TryInitializeDragImage(IntPtr dataObject, IntPtr bitmap, ShellDragPoint grabOffset)
    {
        object? helper = null;
        try
        {
            Type type = Type.GetTypeFromCLSID(DragDropHelperClass, throwOnError: true)!;
            helper = Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法创建Shell拖动图像管理器。");
            var source = (IShellDragSourceHelper)helper;
            var image = new ShellDragImage
            {
                Size = new ShellDragSize { Width = DragImageSize, Height = DragImageSize },
                Offset = grabOffset,
                Bitmap = bitmap,
                ColorKey = uint.MaxValue
            };
            int result = source.InitializeFromBitmap(ref image, dataObject);
            Marshal.ThrowExceptionForHR(result);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Shell拖动图像初始化失败，将继续使用系统默认反馈。", ex);
            return false;
        }
        finally
        {
            if (helper is not null && Marshal.IsComObject(helper)) _ = Marshal.FinalReleaseComObject(helper);
        }
    }

    internal static IntPtr AcquireDropSourcePointer(IDropSource source) =>
        Marshal.GetComInterfaceForObject<object, IDropSource>(source);

    internal static ShellDataObject CreateDataObject(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException(AppStrings.Get("ShellDragMissing"), path);
        int result = SHCreateItemFromParsingName(path, IntPtr.Zero, ShellItemInterface, out IShellItem item);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            Guid handler = DataObjectHandler;
            Guid iid = DataObjectInterface;
            result = item.BindToHandler(IntPtr.Zero, ref handler, ref iid, out IntPtr pointer);
            Marshal.ThrowExceptionForHR(result);
            return new ShellDataObject(item, pointer);
        }
        catch
        {
            _ = Marshal.FinalReleaseComObject(item);
            throw;
        }
    }

    private sealed class DesktopAwareDropSource(
        IntPtr owner,
        NativeMethods.RECT desktopExclusionBounds,
        long dragStarted,
        Func<bool>? cancellationRequested) : IDropSource
    {
        private long _lastCallbackAt;

        public bool DesktopRequested { get; private set; }
        public NativeMethods.POINT? DesktopDropPoint { get; private set; }
        public TimeSpan? FirstFeedbackDelay { get; private set; }
        public int CallbackCount { get; private set; }
        public TimeSpan MaximumCallbackInterval { get; private set; }

        public int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, uint keyState)
        {
            NotifyShellLoopActive();
            if (escapePressed || cancellationRequested?.Invoke() == true) return DragDropCancel;
            if ((keyState & MouseKeyLeft) != 0) return Success;
            if (IsDesktopTarget(owner, out NativeMethods.POINT dropPoint))
            {
                if (DragBoundaryMath.Contains(desktopExclusionBounds, dropPoint)) return DragDropCancel;
                DesktopRequested = true;
                DesktopDropPoint = dropPoint;
                return DragDropCancel;
            }
            return DragDropDrop;
        }

        public int GiveFeedback(uint effect)
        {
            NotifyShellLoopActive();
            return DragDropUseDefaultCursors;
        }

        private void NotifyShellLoopActive()
        {
            long now = Stopwatch.GetTimestamp();
            if (CallbackCount == 0)
            {
                FirstFeedbackDelay = Stopwatch.GetElapsedTime(dragStarted, now);
            }
            else
            {
                TimeSpan interval = Stopwatch.GetElapsedTime(_lastCallbackAt, now);
                if (interval > MaximumCallbackInterval) MaximumCallbackInterval = interval;
            }
            _lastCallbackAt = now;
            CallbackCount++;
        }
    }

    private static bool IsDesktopTarget(IntPtr owner, out NativeMethods.POINT point)
    {
        point = default;
        if (!NativeMethods.GetCursorPos(out point)) return false;
        IntPtr hit = NativeMethods.WindowFromPoint(point);
        if (hit == IntPtr.Zero || hit == owner || NativeMethods.IsChild(owner, hit)) return false;
        for (IntPtr current = hit; current != IntPtr.Zero; current = NativeMethods.GetParent(current))
        {
            _ = NativeMethods.GetWindowThreadProcessId(current, out uint processId);
            if (processId == Environment.ProcessId || IsTuckPaneProcess(processId)) return false;
        }
        IntPtr desktopView = DesktopLayerService.FindDesktopIconView();
        if (desktopView == IntPtr.Zero) return false;
        IntPtr listView = NativeMethods.FindWindowEx(desktopView, IntPtr.Zero, "SysListView32", "FolderView");
        IntPtr desktopHost = NativeMethods.GetParent(desktopView);
        for (IntPtr current = hit; current != IntPtr.Zero; current = NativeMethods.GetParent(current))
        {
            if (current == listView || current == desktopView || current == desktopHost) return true;
        }
        return false;
    }

    private static bool IsTuckPaneProcess(uint processId)
    {
        if (processId == 0) return false;
        try
        {
            using Process process = Process.GetProcessById(checked((int)processId));
            return string.Equals(process.ProcessName, "TuckPane", StringComparison.OrdinalIgnoreCase)
                || string.Equals(process.ProcessName, "GlassFolder", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal sealed class ShellDataObject : IDisposable
    {
        private IShellItem? _item;

        internal ShellDataObject(IShellItem item, IntPtr pointer)
        {
            _item = item;
            Pointer = pointer;
        }

        internal IntPtr Pointer { get; private set; }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                _ = Marshal.Release(Pointer);
                Pointer = IntPtr.Zero;
            }
            if (_item is not null)
            {
                _ = Marshal.FinalReleaseComObject(_item);
                _item = null;
            }
        }
    }

    internal sealed class ShellDragSession : IDisposable
    {
        private IntPtr _bitmap;

        internal ShellDragSession(string path, ShellDataObject data, IntPtr bitmap, bool dragImageInitialized, TimeSpan preparationDuration)
        {
            Path = path;
            Data = data;
            _bitmap = bitmap;
            DragImageInitialized = dragImageInitialized;
            PreparationDuration = preparationDuration;
        }

        internal string Path { get; }
        internal ShellDataObject Data { get; }
        internal TimeSpan PreparationDuration { get; }
        internal bool HasCustomDragImage => _bitmap != IntPtr.Zero && DragImageInitialized;
        internal bool DragImageInitialized { get; }
        internal TimeSpan? FirstFeedbackDelay { get; set; }
        internal int CallbackCount { get; set; }
        internal TimeSpan MaximumCallbackInterval { get; set; }
        internal TimeSpan DragDuration { get; set; }
        internal bool IsDisposed { get; private set; }

        internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            Data.Dispose();
            if (_bitmap != IntPtr.Zero)
            {
                _ = NativeMethods.DeleteObject(_bitmap);
                _bitmap = IntPtr.Zero;
            }
        }
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint displayNameType, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        in Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int DoDragDrop(
        IntPtr dataObject,
        IntPtr dropSource,
        uint allowedEffects,
        out uint performedEffect);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int OleSetClipboard(IntPtr dataObject);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int OleGetClipboard(out IntPtr dataObject);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int OleFlushClipboard();

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint fileIndex, StringBuilder? file, uint characterCount);

    internal static void DeleteToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION)
        };
        int result = SHFileOperationW(ref op);
        if (result != 0 || op.fAnyOperationsAborted)
        {
            throw new IOException(Marshal.GetExceptionForHR(result << 16)?.Message ?? $"0x{result:X8}");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);
}
