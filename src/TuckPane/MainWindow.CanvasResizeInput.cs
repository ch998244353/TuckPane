using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using TuckPane.Core;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class MainWindow
{
    private CanvasResizeFrame? _canvasResizeAppliedFrame;

    private bool TryGetCanvasResizeFrame(out CanvasResizeFrame frame, out NativeMethods.POINT clientOrigin)
    {
        frame = default;
        clientOrigin = default;
        if (!WindowRoot.IsLoaded || !TryGetElementScreenBounds(ExpandedPanel, out var panel) ||
            !NativeMethods.GetClientRect(_hwnd, out var client) ||
            !NativeMethods.ClientToScreen(_hwnd, ref clientOrigin)) return false;
        int left = Math.Max(0, panel.Left - clientOrigin.X);
        int top = Math.Max(0, panel.Top - clientOrigin.Y);
        int right = Math.Min(client.Width, panel.Right - clientOrigin.X);
        int bottom = Math.Min(client.Height, panel.Bottom - clientOrigin.Y);
        frame = new(left, top, right - left, bottom - top, WindowRoot.XamlRoot.RasterizationScale);
        return frame.IsValid;
    }

    private void RefreshCanvasResizeLayout()
    {
        if (_closing || !_expanded || _animating || IsDock ||
            _definition.PlacementMode == Models.OrganizerPlacementMode.Station) return;
        if (!TryGetCanvasResizeFrame(out var frame, out _) || _canvasResizeAppliedFrame != frame)
            UpdateCanvasResizeEdgeWindows(show: true);
    }

    private bool TryHandleCanvasResizePress(PointerRoutedEventArgs args)
    {
        if (args.Handled || args.Pointer.PointerDeviceType != PointerDeviceType.Mouse ||
            !args.GetCurrentPoint(WindowRoot).Properties.IsLeftButtonPressed) return false;
        if (_canvasResize is null && !TryBeginCanvasResize()) return false;
        args.Handled = true;
        return true;
    }
}
