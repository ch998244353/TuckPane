using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class MainWindow
{
    private void CommitCompactCanvasResize(CanvasResizeSession session, NativeMethods.POINT cursor)
    {
        int title = GetExpandedTitleBandPx(session.DisplayScale);
        var insets = new WindowAlignmentInsets(0, title, 0, 0);
        NativeMethods.RECT start = insets.ToFrame(session.StartBounds);
        NativeMethods.RECT work = DisplayPlacementService.GetExpandedWorkArea(session.Display);
        work.Top += title; // Keep the title on screen while anchoring the content's opposite corner.
        int minimumWidth = (int)Math.Ceiling(OrganizerLimits.MinimumCompactListCanvasWidthDip * session.DisplayScale);
        int minimumHeight = (int)Math.Ceiling(OrganizerLimits.MinimumCompactListCanvasHeightDip * session.DisplayScale);
        CompactResizeResult resized = CompactResizeMath.Resize(start, work, session.Edge,
            cursor.X - session.StartCursor.X, cursor.Y - session.StartCursor.Y,
            minimumWidth, minimumHeight, session.StartContentScale, _definition.CompactListItemScale);
        NativeMethods.RECT panel = resized.Bounds;
        double factor = resized.Scale;
        WindowAlignmentResult? alignment = null;
        if (session.AlignmentInsets is not null)
        {
            uint dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(_hwnd));
            int snap = WindowAlignmentMath.DipToPx(WindowAlignmentMath.SnapDistanceDip, dpi);
            int release = WindowAlignmentMath.DipToPx(WindowAlignmentMath.ReleaseDistanceDip, dpi);
            var targets = _host.GetWindowAlignmentTargets(this, session.Display);
            if (CompactResizeMath.IsCorner(session.Edge))
            {
                var aligned = WindowAlignmentMath.AlignCompactCorner(start, resized, work, targets,
                    session.Edge, snap, release, _windowAlignmentState);
                alignment = aligned.Alignment;
                factor = aligned.Scale;
            }
            else alignment = WindowAlignmentMath.AlignResize(panel, work, targets, session.Edge,
                Math.Min(minimumWidth, panel.Width), Math.Min(minimumHeight, panel.Height), snap, release, _windowAlignmentState);
            panel = alignment.Value.Bounds;
        }
        NativeMethods.RECT bounds = insets.ToWindow(panel);
        if (RectsEqual(bounds, _dragCurrentBounds)) return;
        double previousFit = _contentDisplayFit;
        double previousScale = _definition.CompactListContentScale;
        _contentDisplayFit = 1;
        _definition.CompactListContentScale = session.StartContentScale * factor;
        ApplyExpandedContentInset();
        if (!ApplyCanvasResizeBounds(bounds, alignment))
        {
            _contentDisplayFit = previousFit;
            _definition.CompactListContentScale = previousScale;
            ApplyExpandedContentInset();
            return;
        }
        _definition.CompactListCanvasWidthDip = panel.Width / session.DisplayScale;
        _definition.CompactListCanvasHeightDip = panel.Height / session.DisplayScale;
        ConfigureItemsLayout();
        UpdateCanvasResizeEdgeWindows(show: true);
    }
}
