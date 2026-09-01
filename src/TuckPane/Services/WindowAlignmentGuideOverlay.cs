using System.Runtime.InteropServices;
using TuckPane.Core;
using Windows.UI.ViewManagement;

namespace TuckPane.Services;

internal sealed class WindowAlignmentGuideOverlay : IDisposable
{
    internal const string ClassName = "TuckPane.WindowAlignmentGuide";
    internal const string XWindowName = "TuckPane.AlignmentGuide.X";
    internal const string YWindowName = "TuckPane.AlignmentGuide.Y";

    private static readonly object RegistrationGate = new();
    private static readonly NativeMethods.WindowProc WindowProc = GuideWindowProc;
    private static bool _registered;

    private IntPtr _xWindow;
    private IntPtr _yWindow;

    internal WindowAlignmentGuideOverlay(IntPtr owner)
    {
        EnsureRegistered();
        _xWindow = Create(owner, XWindowName);
        _yWindow = Create(owner, YWindowName);
    }

    internal void Show(WindowAlignmentGuide? xGuide, WindowAlignmentGuide? yGuide, uint dpi)
    {
        int thickness = WindowAlignmentMath.DipToPx(1, dpi);
        ShowLine(_xWindow, xGuide, thickness);
        ShowLine(_yWindow, yGuide, thickness);
    }

    internal void Hide()
    {
        HideLine(_xWindow);
        HideLine(_yWindow);
    }

    public void Dispose()
    {
        if (_xWindow != IntPtr.Zero) _ = NativeMethods.DestroyWindow(_xWindow);
        if (_yWindow != IntPtr.Zero) _ = NativeMethods.DestroyWindow(_yWindow);
        _xWindow = IntPtr.Zero;
        _yWindow = IntPtr.Zero;
    }

    private static void EnsureRegistered()
    {
        lock (RegistrationGate)
        {
            if (_registered) return;
            Windows.UI.Color accent = new UISettings().GetColorValue(UIColorType.Accent);
            uint color = accent.R | ((uint)accent.G << 8) | ((uint)accent.B << 16);
            var windowClass = new NativeMethods.WNDCLASSEX
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                WindowProc = Marshal.GetFunctionPointerForDelegate(WindowProc),
                Instance = NativeMethods.GetModuleHandle(null),
                Background = NativeMethods.CreateSolidBrush(color),
                ClassName = ClassName
            };
            _registered = NativeMethods.RegisterClassEx(ref windowClass) != 0;
            if (!_registered) AppLogger.Error($"无法注册窗口对齐指示线，Win32={Marshal.GetLastWin32Error()}。");
        }
    }

    private static IntPtr Create(IntPtr owner, string name)
    {
        if (!_registered) return IntPtr.Zero;
        IntPtr window = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
            ClassName,
            name,
            NativeMethods.WS_POPUP,
            0,
            0,
            1,
            1,
            owner,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);
        if (window != IntPtr.Zero) _ = NativeMethods.SetLayeredWindowAttributes(window, 0, 224, NativeMethods.LWA_ALPHA);
        return window;
    }

    private static void ShowLine(IntPtr window, WindowAlignmentGuide? guide, int thickness)
    {
        if (window == IntPtr.Zero || guide is not WindowAlignmentGuide line || line.End <= line.Start)
        {
            HideLine(window);
            return;
        }

        int left = line.Vertical ? line.Coordinate - thickness / 2 : line.Start;
        int top = line.Vertical ? line.Start : line.Coordinate - thickness / 2;
        int width = line.Vertical ? thickness : line.End - line.Start;
        int height = line.Vertical ? line.End - line.Start : thickness;
        _ = NativeMethods.SetWindowPos(
            window,
            NativeMethods.HWND_TOP,
            left,
            top,
            Math.Max(1, width),
            Math.Max(1, height),
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW);
    }

    private static void HideLine(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        _ = NativeMethods.SetWindowPos(
            window,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_HIDEWINDOW);
    }

    private static IntPtr GuideWindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam) =>
        message == NativeMethods.WM_NCHITTEST
            ? new IntPtr(NativeMethods.HTTRANSPARENT)
            : NativeMethods.DefWindowProc(window, message, wParam, lParam);
}
