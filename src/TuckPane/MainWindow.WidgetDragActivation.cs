using TuckPane.Core;
using TuckPane.Services;
namespace TuckPane;

public sealed partial class MainWindow
{
    private bool _widgetMousePress;
    private void TryStartMouseWidgetDrag()
    {
        if (!_widgetMousePress || !_pressActive || _widgetDragging ||
            !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)) return;
        if (WidgetDragActivation.ShouldStart(cursor.X - _pressCursorPx.X, cursor.Y - _pressCursorPx.Y,
                NativeMethods.GetDpiForWindow(_hwnd) / 96d)) TryStartWidgetDrag();
    }
}
