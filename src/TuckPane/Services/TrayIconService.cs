using TuckPane.Core;

namespace TuckPane.Services;

public enum TrayCommand
{
    OpenConsole = 1,
    ShowAll = 2,
    HideAll = 3,
    ToggleStartup = 4,
    CancelTransfer = 5,
    Exit = 6
}

public sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = NativeMethods.WM_APP + 73;
    private const uint IconId = 1;
    private static readonly UIntPtr SubclassId = new(0x47464F4CUL);
    private readonly IntPtr _window;
    private readonly Func<bool> _isStartupEnabled;
    private readonly Func<bool> _isTransferActive;
    private readonly Action<TrayCommand> _command;
    private readonly NativeMethods.SubclassProc _subclass;
    private readonly Func<string>? _lifecycleContext;
    private NativeMethods.NOTIFYICONDATA _data;
    private IntPtr _icon;

    public TrayIconService(IntPtr window, Func<bool> isStartupEnabled, Func<bool> isTransferActive, Action<TrayCommand> command,
        Func<string>? lifecycleContext = null)
    {
        _window = window;
        _isStartupEnabled = isStartupEnabled;
        _isTransferActive = isTransferActive;
        _command = command;
        _subclass = WindowProc;
        _lifecycleContext = lifecycleContext;

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TuckPane.ico");
        _icon = NativeMethods.LoadImage(IntPtr.Zero, iconPath, NativeMethods.IMAGE_ICON, 0, 0, NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
        _data = CreateData();
        bool subclassAdded = NativeMethods.SetWindowSubclass(_window, _subclass, SubclassId, IntPtr.Zero);
        bool iconAdded = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _data);
        _data.uTimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
        bool versionSet = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref _data);
        AppLogger.Lifecycle("tray-created", $"hwnd=0x{_window:X} subclass={subclassAdded} icon={iconAdded} version={versionSet}");
    }

    public void ApplyLanguage()
    {
        _data.szTip = AppStrings.Get("AppTitle");
        _data.uFlags = NativeMethods.NIF_TIP | NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON;
        _ = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref _data);
    }

    public void Dispose()
    {
        AppLogger.Lifecycle("tray-dispose", $"hwnd=0x{_window:X}");
        _ = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _data);
        _ = NativeMethods.RemoveWindowSubclass(_window, _subclass, SubclassId);
        if (_icon != IntPtr.Zero)
        {
            _ = NativeMethods.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, IntPtr referenceData)
    {
        string? lifecycle = LifecycleDiagnostics.DescribeWindowMessage(message, wParam.ToUInt64(), lParam.ToInt64());
        if (lifecycle is not null)
        {
            try
            {
                AppLogger.Lifecycle(lifecycle, $"source=host-message hwnd=0x{hWnd:X} message=0x{message:X} " +
                    $"wParam=0x{wParam.ToUInt64():X} lParam=0x{lParam.ToInt64():X} {_lifecycleContext?.Invoke()}");
            }
            catch { /* A diagnostic callback must never interrupt the native message chain. */ }
        }
        if (message == CallbackMessage)
        {
            int mouseMessage = unchecked((int)((long)lParam & 0xFFFF));
            if (mouseMessage is NativeMethods.WM_LBUTTONUP or (int)NativeMethods.WM_LBUTTONDBLCLK)
            {
                _command(TrayCommand.OpenConsole);
            }
            else if (mouseMessage is NativeMethods.WM_RBUTTONUP or (int)NativeMethods.WM_CONTEXTMENU)
            {
                ShowMenu();
            }
            return IntPtr.Zero;
        }
        return NativeMethods.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        try
        {
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)TrayCommand.OpenConsole, AppStrings.Get("TrayOpenConsole"));
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)TrayCommand.ShowAll, AppStrings.Get("TrayShowAll"));
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)TrayCommand.HideAll, AppStrings.Get("TrayHideAll"));
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            uint startupFlags = NativeMethods.MF_STRING | (_isStartupEnabled() ? NativeMethods.MF_CHECKED : 0);
            _ = NativeMethods.AppendMenu(menu, startupFlags, (UIntPtr)TrayCommand.ToggleStartup, AppStrings.Get("TrayStartup"));
            if (_isTransferActive())
            {
                _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)TrayCommand.CancelTransfer, AppStrings.Get("TrayCancelTransfer"));
            }
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)TrayCommand.Exit, AppStrings.Get("TrayExit"));

            _ = NativeMethods.GetCursorPos(out NativeMethods.POINT point);
            _ = NativeMethods.SetForegroundWindow(_window);
            uint selected = NativeMethods.TrackPopupMenu(menu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON, point.X, point.Y, 0, _window, IntPtr.Zero);
            if (selected != 0)
            {
                _command((TrayCommand)selected);
            }
        }
        finally
        {
            _ = NativeMethods.DestroyMenu(menu);
        }
    }

    private NativeMethods.NOTIFYICONDATA CreateData() => new()
    {
        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _window,
        uID = IconId,
        uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = AppStrings.Get("AppTitle"),
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };
}
