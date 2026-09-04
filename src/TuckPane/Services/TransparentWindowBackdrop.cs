using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using WinUIEx;
using WinUIEx.Messaging;
using Wuc = Windows.UI.Composition;

namespace TuckPane.Services;

/// <summary>
/// Makes a top-level WinUI window transparent. DWM blur-behind is enabled with
/// an empty region only to activate per-pixel alpha composition; on Windows 8
/// and later it does not add visual blur. ThemeBackdrop remains the sole owner
/// of the visible Gaussian blur applied to local surfaces.
/// </summary>
internal sealed class TransparentWindowBackdrop : CompositionBrushBackdrop
{
    private WindowMessageMonitor? _messageMonitor;

    protected override Wuc.CompositionBrush CreateBrush(Wuc.Compositor compositor) =>
        compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget,
        XamlRoot xamlRoot)
    {
        // ICompositionSupportsSystemBackdrop is not guaranteed to be a
        // Window (SystemBackdropElement is a common target), so obtain the
        // HWND from the XamlRoot's AppWindowId just like WinUIEx does.
        IntPtr hwnd = (IntPtr)xamlRoot.ContentIslandEnvironment.AppWindowId.Value;
        Configure(hwnd);
        if (hwnd != IntPtr.Zero)
        {
            _messageMonitor?.Dispose();
            _messageMonitor = new WindowMessageMonitor(hwnd);
            _messageMonitor.WindowMessageReceived += MessageMonitor_WindowMessageReceived;
        }
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _ = ClearBackground(hwnd);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        if (_messageMonitor is not null)
        {
            _messageMonitor.WindowMessageReceived -= MessageMonitor_WindowMessageReceived;
            _messageMonitor.Dispose();
            _messageMonitor = null;
        }
        base.OnTargetDisconnected(disconnectedTarget);
    }

    private void MessageMonitor_WindowMessageReceived(
        object? sender,
        WindowMessageEventArgs e)
    {
        if (e.Message.MessageId == WM_ERASEBKGND)
        {
            // Clearing to GDI black initializes the client pixels for DWM's
            // alpha-aware composition. The acquired HDC and brush are scoped
            // to this operation so reconnects cannot leak native resources.
            if (ClearBackground(e.Message.Hwnd))
            {
                e.Result = 1;
                e.Handled = true;
            }
            return;
        }

        if (e.Message.MessageId != NativeMethods.WM_DWMCOMPOSITIONCHANGED) return;

        // DWM requires the alpha-composition state to be reapplied whenever
        // composition is toggled. Leave the message unhandled so every other
        // HWND subclass can continue its own frame and border refresh.
        Configure(e.Message.Hwnd);
    }

    private static void Configure(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // Zero margins clear any previous full-client frame extension. The
        // empty blur region below enables alpha without supplying visual blur.
        var margins = new DwmMargins();
        int frameResult = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        LogResult(frameResult, "DwmExtendFrameIntoClientArea(ZeroMargins)");

        IntPtr emptyRegion = CreateRectRgn(-2, -2, -1, -1);
        if (emptyRegion == IntPtr.Zero)
        {
            AppLogger.Error(
                $"CreateRectRgn 失败，Win32Error={Marshal.GetLastWin32Error()}。",
                null);
            return;
        }

        try
        {
            var blur = new DwmBlurBehind
            {
                Flags = DwmBlurBehindEnable | DwmBlurBehindBlurRegion,
                Enable = true,
                Region = emptyRegion,
                TransitionOnMaximized = false
            };
            int blurResult = DwmEnableBlurBehindWindow(hwnd, ref blur);
            LogResult(blurResult, "DwmEnableBlurBehindWindow(AlphaComposition)");
        }
        finally
        {
            _ = DeleteObject(emptyRegion);
        }
    }

    private static bool ClearBackground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        IntPtr hdc = GetDC(hwnd);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            if (!GetClientRect(hwnd, out Rect rect)) return false;

            IntPtr brush = CreateSolidBrush(0);
            if (brush == IntPtr.Zero) return false;
            try
            {
                return FillRect(hdc, ref rect, brush) != 0;
            }
            finally
            {
                _ = DeleteObject(brush);
            }
        }
        finally
        {
            _ = ReleaseDC(hwnd, hdc);
        }
    }

    private static void LogResult(int hresult, string operation)
    {
        if (hresult >= 0)
            AppLogger.Info($"{operation} 完成，HRESULT=0x{hresult:X8}。");
        else
            AppLogger.Error($"{operation} 失败，HRESULT=0x{hresult:X8}。");
    }

    private const uint DwmBlurBehindEnable = 0x00000001;
    private const uint DwmBlurBehindBlurRegion = 0x00000002;
    private const uint WM_ERASEBKGND = 0x0014;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Enable;
        public IntPtr Region;
        [MarshalAs(UnmanagedType.Bool)] public bool TransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmMargins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blurBehind);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref DwmMargins margins);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);
}
