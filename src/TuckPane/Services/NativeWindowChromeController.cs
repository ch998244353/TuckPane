using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;

namespace TuckPane.Services;

internal sealed class NativeWindowChromeController : IDisposable
{
    private static readonly UIntPtr SubclassId = new(0x47464348);
    private readonly IntPtr _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly NativeMethods.SubclassProc _subclass;
    private int _visibleFrameThickness = 1;
    private bool _disposed;
    private bool _applyQueued;

    internal int ApplyCount { get; private set; }

    internal NativeWindowChromeController(IntPtr window, DispatcherQueue dispatcher)
    {
        _window = window;
        _dispatcher = dispatcher;
        _subclass = WindowProc;
        _ = NativeMethods.SetWindowSubclass(window, _subclass, SubclassId, IntPtr.Zero);
        Apply(refreshFrame: true);
    }

    internal void Apply(bool refreshFrame = false)
    {
        if (_disposed || _window == IntPtr.Zero || !NativeMethods.IsWindow(_window)) return;
        ApplyCount++;

        int noBorder = NativeMethods.DWMWA_COLOR_NONE;
        LogResult(NativeMethods.DwmSetWindowAttribute(
            _window,
            NativeMethods.DWMWA_BORDER_COLOR,
            ref noBorder,
            sizeof(int)), "DwmSetWindowAttribute(DWMWA_BORDER_COLOR)");

        int frameThickness = 1;
        int getFrame = NativeMethods.DwmGetWindowAttribute(
            _window,
            NativeMethods.DWMWA_VISIBLE_FRAME_BORDER_THICKNESS,
            out int visibleFrame,
            sizeof(int));
        LogResult(getFrame, "DwmGetWindowAttribute(DWMWA_VISIBLE_FRAME_BORDER_THICKNESS)");
        if (getFrame >= 0 && visibleFrame > 0) frameThickness = visibleFrame;
        _visibleFrameThickness = frameThickness;

        var margins = new NativeMethods.MARGINS { Top = frameThickness };
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
            parameters.ProposedClient.Top -= Math.Max(1, _visibleFrameThickness);
            Marshal.StructureToPtr(parameters, lParam, fDeleteOld: false);
            return result;
        }
        if (message is NativeMethods.WM_THEMECHANGED or
            NativeMethods.WM_DWMCOMPOSITIONCHANGED or
            NativeMethods.WM_SETTINGCHANGE)
        {
            QueueApply();
        }
        return result;
    }

    private void QueueApply()
    {
        if (_applyQueued || _disposed) return;
        _applyQueued = true;
        _ = _dispatcher.TryEnqueue(() =>
        {
            _applyQueued = false;
            Apply(refreshFrame: true);
        });
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
