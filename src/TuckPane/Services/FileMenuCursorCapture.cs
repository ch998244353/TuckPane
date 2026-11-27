using System.ComponentModel;
using System.Runtime.InteropServices;
using TuckPane.Core;
using Windows.Foundation;
using static TuckPane.Services.ShellMenuNative;

namespace TuckPane.Services;

internal static class FileMenuCursorCapture
{
    // Only called at the event boundary, before Shell initialization or document saves.
    internal static FileMenuPopup Capture(Point position, double fallbackScale)
    {
        IntPtr monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT
        {
            X = checked((int)position.X), Y = checked((int)position.Y)
        }, 2); // MONITOR_DEFAULTTONEAREST
        var info = new NativeMethods.MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(), szDevice = string.Empty
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) throw new Win32Exception();
        double scale = NativeMethods.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0
            ? dpi / 96d : fallbackScale;
        uint metricsDpi = checked((uint)Math.Round(scale * 96));
        Rect cursorBounds = ReadCursorBounds(position) ?? FileMenuPosition.FallbackCursorBounds(position,
            GetSystemMetricsForDpi(13, metricsDpi), GetSystemMetricsForDpi(14, metricsDpi)); // SM_CX/Y CURSOR
        return FileMenuPosition.Pointer(position, cursorBounds,
            new Rect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Width, info.rcWork.Height), scale);
    }

    private static Rect? ReadCursorBounds(Point position)
    {
        var cursor = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || cursor.Cursor == IntPtr.Zero) return null;
        IconInfo icon = default;
        try
        {
            if (!GetIconInfo(cursor.Cursor, out icon)) return null;
            IntPtr bitmap = icon.Color != IntPtr.Zero ? icon.Color : icon.Mask;
            if (bitmap == IntPtr.Zero || NativeMethods.GetObject(bitmap, Marshal.SizeOf<NativeMethods.BITMAP>(), out var data) == 0)
                return null;
            // Bitmap pixels are physical. The event point, not a later cursor position, owns the anchor.
            return FileMenuPosition.CursorBounds(position, data.Width, data.Height,
                icon.HotspotX, icon.HotspotY, icon.Color == IntPtr.Zero);
        }
        finally
        {
            if (icon.Mask != IntPtr.Zero) NativeMethods.DeleteObject(icon.Mask);
            if (icon.Color != IntPtr.Zero) NativeMethods.DeleteObject(icon.Color);
            // cursor.Cursor is borrowed from Windows; never destroy it.
        }
    }
}
