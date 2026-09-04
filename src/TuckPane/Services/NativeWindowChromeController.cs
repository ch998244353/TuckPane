using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;

namespace TuckPane.Services;

internal sealed class NativeWindowChromeController : IDisposable
{
    private static readonly UIntPtr SubclassId = new(0x47464348);
    private readonly IntPtr _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly NativeMethods.SubclassProc _subclass;
    private readonly bool _extendClientFrame;
    private int _visibleFrameThickness = 1;
    private bool _disposed;

    internal NativeWindowChromeController(
        IntPtr window,
        DispatcherQueue dispatcher,
        bool extendClientFrame = true)
    {
        _window = window;
        _dispatcher = dispatcher;
        _extendClientFrame = extendClientFrame;
        _subclass = WindowProc;
        _ = NativeMethods.SetWindowSubclass(window, _subclass, SubclassId, IntPtr.Zero);
        Apply(refreshFrame: true);
    }

    internal void Apply(bool refreshFrame = false)
    {
        if (_disposed || _window == IntPtr.Zero || !NativeMethods.IsWindow(_window)) return;

        int frameThickness = 1;
        if (NativeMethods.SupportsWindows11DwmAttributes)
        {
            int noBorder = NativeMethods.DWMWA_COLOR_NONE;
            LogResult(NativeMethods.DwmSetWindowAttribute(
                _window,
                NativeMethods.DWMWA_BORDER_COLOR,
                ref noBorder,
                sizeof(int)), "DwmSetWindowAttribute(DWMWA_BORDER_COLOR)");

            int getFrame = NativeMethods.DwmGetWindowAttribute(
                _window,
                NativeMethods.DWMWA_VISIBLE_FRAME_BORDER_THICKNESS,
                out int visibleFrame,
                sizeof(int));
            LogResult(getFrame, "DwmGetWindowAttribute(DWMWA_VISIBLE_FRAME_BORDER_THICKNESS)");
            if (getFrame >= 0 && visibleFrame > 0) frameThickness = visibleFrame;
        }
        // A zero-margin call explicitly clears any previous sheet-of-glass
        // extension.  The theme pipeline owns desktop sampling and blur; this
        // controller must never request a full-client DWM glass surface.
        _visibleFrameThickness = _extendClientFrame ? frameThickness : 0;
        NativeMethods.MARGINS margins = _extendClientFrame
            ? new NativeMethods.MARGINS { Top = frameThickness }
            : new NativeMethods.MARGINS();
        LogResult(NativeMethods.DwmExtendFrameIntoClientArea(_window, ref margins), "DwmExtendFrameIntoClientArea");

        if (refreshFrame)
        {
            _ = NativeMethods.SetWindowPos(
                _window,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_FRAMECHANGED);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = NativeMethods.RemoveWindowSubclass(_window, _subclass, SubclassId);
    }

    private IntPtr WindowProc(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        IntPtr referenceData)
    {
        if (message == NativeMethods.WM_SYSCOMMAND &&
            (wParam.ToUInt64() & 0xFFF0UL) == NativeMethods.SC_MAXIMIZE)
        {
            return IntPtr.Zero;
        }
        IntPtr result = NativeMethods.DefSubclassProc(window, message, wParam, lParam);
        if (message == NativeMethods.WM_NCCALCSIZE && wParam != UIntPtr.Zero && lParam != IntPtr.Zero)
        {
            var parameters = Marshal.PtrToStructure<NativeMethods.NCCALCSIZE_PARAMS>(lParam);
            if (_visibleFrameThickness > 0)
                parameters.ProposedClient.Top -= _visibleFrameThickness;
            Marshal.StructureToPtr(parameters, lParam, fDeleteOld: false);
            return result;
        }
        if (message is NativeMethods.WM_NCACTIVATE or
            NativeMethods.WM_THEMECHANGED or
            NativeMethods.WM_DWMCOMPOSITIONCHANGED or
            NativeMethods.WM_SETTINGCHANGE)
        {
            bool refreshFrame = message != NativeMethods.WM_NCACTIVATE;
            _ = _dispatcher.TryEnqueue(() => Apply(refreshFrame));
        }
        return result;
    }

    private static void LogResult(int hresult, string operation)
    {
        if (hresult >= 0)
        {
            AppLogger.Info($"{operation} 完成，HRESULT=0x{hresult:X8}。");
        }
        else
        {
            AppLogger.Error($"{operation} 失败，HRESULT=0x{hresult:X8}。");
        }
    }
}
