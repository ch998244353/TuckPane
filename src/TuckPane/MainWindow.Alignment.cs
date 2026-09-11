using System.Runtime.InteropServices;
using TuckPane.Core;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class MainWindow
{
    private void StopCanvasResizeForHide()
    {
        ClearWindowAlignment();
        if (_canvasResize is null) return;
        StopCanvasResizeRendering();
        _canvasResize = null;
        _resizeGesture.Invalidate();
        if (NativeMethods.GetCapture() == _hwnd) _ = NativeMethods.ReleaseCapture();
        ConfigureItemsLayout();
        UpdateCompactPreviewItemScale();
        UpdateSurfaceClips();
        _interactionSaveTimer.Stop();
        CaptureExpandedPosition();
        _ = SaveStateAsync();
    }

    private bool ApplyCanvasResizeBounds(NativeMethods.RECT bounds, WindowAlignmentResult? alignment)
    {
        if (!RectsEqual(bounds, _dragCurrentBounds) && !NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW))
        {
            AppLogger.Error($"拉伸收纳窗失败，Win32={Marshal.GetLastWin32Error()}。");
            ClearWindowAlignment();
            return false;
        }
        _dragCurrentBounds = bounds;
        if (alignment is WindowAlignmentResult result)
        {
            _windowAlignmentState = result.State;
            uint dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(_hwnd));
            _windowAlignmentGuide ??= new WindowAlignmentGuideOverlay(_hwnd);
            _windowAlignmentGuide.Show(result.XGuide, result.YGuide, dpi);
        }
        else ClearWindowAlignment();
        return true;
    }
}
