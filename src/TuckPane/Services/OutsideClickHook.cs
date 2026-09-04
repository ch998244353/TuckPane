using Microsoft.UI.Dispatching;

namespace TuckPane.Services;

internal sealed class OutsideClickHook : IDisposable
{
    private readonly IntPtr _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<NativeMethods.POINT> _outsideClick;
    private readonly NativeMethods.HookProc _callback;
    private IntPtr _hook;

    internal OutsideClickHook(IntPtr window, DispatcherQueue dispatcher, Action<NativeMethods.POINT> outsideClick)
    {
        _window = window;
        _dispatcher = dispatcher;
        _outsideClick = outsideClick;
        _callback = OnMouse;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            AppLogger.Error("无法安装外部点击捕获钩子。");
        }
    }

    public void Stop() => StopNow();

    public void Dispose() => StopNow();

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || _hook == IntPtr.Zero)
        {
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        }

        int message = wParam.ToInt32();
        bool isDown = message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN;
        if (isDown && NativeMethods.GetWindowRect(_window, out NativeMethods.RECT windowRect))
        {
            NativeMethods.MSLLHOOKSTRUCT data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            bool outside = data.Point.X < windowRect.Left || data.Point.X >= windowRect.Right || data.Point.Y < windowRect.Top || data.Point.Y >= windowRect.Bottom;
            if (outside)
            {
                NativeMethods.POINT clickPoint = data.Point;
                _ = _dispatcher.TryEnqueue(() => _outsideClick(clickPoint));
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void StopNow()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }
        if (_hook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
