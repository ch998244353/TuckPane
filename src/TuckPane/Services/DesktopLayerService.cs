namespace TuckPane.Services;

public sealed class DesktopLayerService : IDisposable
{
    private const uint SpawnWorkerMessage = 0x052C;
    private readonly IntPtr _window;
    private readonly IntPtr _expandedOwner;
    private readonly NativeMethods.SubclassProc _activationGuard;
    private static readonly UIntPtr ActivationSubclassId = new(0x47464C59UL);
    private IntPtr _desktopIconView;
    private bool _allowActivation;
    private bool _expanded;
    private bool _stayTopmost;

    public DesktopLayerService(IntPtr window, IntPtr expandedOwner)
    {
        _window = window;
        _expandedOwner = expandedOwner;
        _activationGuard = ActivationGuard;
        ApplyToolWindowStyle();
        _ = NativeMethods.SetWindowSubclass(_window, _activationGuard, ActivationSubclassId, IntPtr.Zero);
        if (NativeMethods.SupportsWindows11DwmAttributes)
        {
            int corner = NativeMethods.DWMWCP_DONOTROUND;
            _ = NativeMethods.DwmSetWindowAttribute(window, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            int border = NativeMethods.DWMWA_COLOR_NONE;
            _ = NativeMethods.DwmSetWindowAttribute(window, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        Reattach();
    }

    public void Reattach()
    {
        if (_expanded)
        {
            if (NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT) != _expandedOwner)
            {
                _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, _expandedOwner);
            }

            _ = NativeMethods.SetWindowPos(
                _window,
                _stayTopmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_TOP,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOOWNERZORDER);
            return;
        }

        if (_desktopIconView == IntPtr.Zero || !NativeMethods.IsWindow(_desktopIconView))
        {
            _desktopIconView = FindDesktopIconView();
        }

        bool ownerChanged = _desktopIconView != IntPtr.Zero &&
            NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT) != _desktopIconView;
        if (ownerChanged)
        {
            _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, _desktopIconView);
        }

        uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOOWNERZORDER;
        if (!ownerChanged) flags |= NativeMethods.SWP_NOZORDER;
        _ = NativeMethods.SetWindowPos(
            _window,
            NativeMethods.HWND_BOTTOM,
            0,
            0,
            0,
            0,
            flags);
    }

    public void SetExpanded(bool expanded, bool stayTopmost = false)
    {
        if (!expanded && _stayTopmost)
        {
            _ = NativeMethods.SetWindowPos(
                _window,
                NativeMethods.HWND_NOTOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER);
        }

        _expanded = expanded;
        _stayTopmost = expanded && stayTopmost;
        Reattach();
        if (expanded && !stayTopmost) RaiseAboveNormalWindows();
    }

    public void BringAboveDesktopPeers()
    {
        Reattach();
        _ = NativeMethods.SetWindowPos(
            _window,
            NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    public void SetTransientOwner(IntPtr owner)
    {
        if (owner == IntPtr.Zero || !NativeMethods.IsWindow(owner)) return;
        if (NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT) != owner)
            _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, owner);
        _ = NativeMethods.SetWindowPos(
            _window,
            NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOOWNERZORDER);
    }

    public void SetInputActivation(bool enabled)
    {
        _allowActivation = enabled;
        if (enabled && _stayTopmost)
        {
            _ = NativeMethods.SetWindowPos(
                _window,
                NativeMethods.HWND_NOTOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER);
        }
        long style = NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWL_EXSTYLE).ToInt64();
        style = enabled ? style & ~NativeMethods.WS_EX_NOACTIVATE : style | NativeMethods.WS_EX_NOACTIVATE;
        _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
        _ = NativeMethods.SetWindowPos(
            _window,
            NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOOWNERZORDER);

        if (enabled)
        {
            _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, _expandedOwner);
            _ = NativeMethods.SetWindowPos(
                _window,
                NativeMethods.HWND_TOP,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW |
                NativeMethods.SWP_NOOWNERZORDER);
            _ = NativeMethods.SetForegroundWindow(_window);
        }
        else
        {
            Reattach();
        }
    }

    public void Dispose()
    {
        if (NativeMethods.IsWindow(_window))
        {
            _ = NativeMethods.RemoveWindowSubclass(_window, _activationGuard, ActivationSubclassId);
            _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, _expandedOwner);
        }
    }

    private void ApplyToolWindowStyle()
    {
        long windowStyle = NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWL_STYLE).ToInt64();
        windowStyle &= ~(
            NativeMethods.WS_CAPTION |
            NativeMethods.WS_THICKFRAME |
            NativeMethods.WS_SYSMENU |
            NativeMethods.WS_MINIMIZEBOX |
            NativeMethods.WS_MAXIMIZEBOX);
        windowStyle |= NativeMethods.WS_POPUP;
        _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWL_STYLE, new IntPtr(windowStyle));

        long style = NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        style &= ~NativeMethods.WS_EX_APPWINDOW;
        _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
        _ = NativeMethods.SetWindowPos(
            _window,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    private void RaiseAboveNormalWindows()
    {
        // HWND_TOP cannot reliably cross the foreground boundary for a no-activate window.
        const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW;
        _ = NativeMethods.SetWindowPos(_window, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
        _ = NativeMethods.SetWindowPos(_window, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
    }

    private IntPtr ActivationGuard(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, IntPtr referenceData)
    {
        if (message == NativeMethods.WM_MOUSEACTIVATE && !_allowActivation)
        {
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        if (message == NativeMethods.WM_GETMINMAXINFO)
        {
            NativeMethods.MINMAXINFO info = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
            info.MinTrackSize = new NativeMethods.POINT { X = 1, Y = 1 };
            System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
            return IntPtr.Zero;
        }
        return NativeMethods.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    internal static IntPtr FindDesktopIconView()
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (!IsDesktopHostWindow(window)) return true;
            IntPtr child = NativeMethods.FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child == IntPtr.Zero)
            {
                return true;
            }
            found = child;
            return false;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero)
        {
            return found;
        }

        IntPtr progman = NativeMethods.FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            _ = NativeMethods.SendMessageTimeout(progman, SpawnWorkerMessage, UIntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
            found = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        }
        return found;
    }

    internal static bool IsDesktopHostClassName(string? className) =>
        className is "Progman" or "WorkerW";

    private static bool IsDesktopHostWindow(IntPtr window)
    {
        var className = new System.Text.StringBuilder(32);
        return NativeMethods.GetClassName(window, className, className.Capacity) > 0 &&
            IsDesktopHostClassName(className.ToString());
    }
}
