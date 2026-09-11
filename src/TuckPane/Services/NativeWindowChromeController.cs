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
    private readonly bool _applyBorderLast;
    private int _visibleFrameThickness = 1;
    private bool _disposed;
    private bool _applyQueued;
    private bool _refreshFramePending;

    internal NativeWindowChromeController(
        IntPtr window,
        DispatcherQueue dispatcher,
        bool extendClientFrame = true,
        bool applyBorderLast = false)
    {
        _window = window;
        _dispatcher = dispatcher;
        _extendClientFrame = extendClientFrame;
        _applyBorderLast = applyBorderLast;
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
            if (!_applyBorderLast) SuppressBorder();

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
        // Console's title-bar and frame refresh may restore the system accent
        // border. Its final native write must suppress that border again.
        if (_applyBorderLast && NativeMethods.SupportsWindows11DwmAttributes) SuppressBorder();
    }

    private void SuppressBorder()
    {
        int noBorder = NativeMethods.DWMWA_COLOR_NONE;
        LogResult(NativeMethods.DwmSetWindowAttribute(
            _window, NativeMethods.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int)),
            "DwmSetWindowAttribute(DWMWA_BORDER_COLOR)");
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
            QueueApply(message != NativeMethods.WM_NCACTIVATE);
        }
        return result;
    }

    internal void QueueApply(bool refreshFrame = false)
    {
        if (_disposed) return;
        _refreshFramePending |= refreshFrame;
        if (_applyQueued) return;
        _applyQueued = true;
        if (!_dispatcher.TryEnqueue(() =>
        {
            bool refresh = _refreshFramePending;
            _refreshFramePending = false;
            // Ignore reentrant activation-only refreshes; retain a real theme/frame change.
            try { Apply(refresh); }
            finally
            {
                _applyQueued = false;
                if (_refreshFramePending) QueueApply(refreshFrame: true);
            }
        })) _applyQueued = false;
    }

    private static void LogResult(int hresult, string operation)
    {
        if (hresult >= 0)
        {
            AppLogger.Performance($"{operation} 完成，HRESULT=0x{hresult:X8}。");
        }
        else
        {
            AppLogger.Error($"{operation} 失败，HRESULT=0x{hresult:X8}。");
        }
    }
}
