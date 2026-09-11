using TuckPane.Services;

namespace TuckPane.Core;

internal readonly record struct WindowAlignmentScaleResult(double Scale, WindowAlignmentResult Alignment);

internal static partial class WindowAlignmentMath
{
    // All rectangles here describe the visible panel in screen pixels, not the title band or HWND.
    internal static WindowAlignmentResult AlignResize(
        NativeMethods.RECT moving, NativeMethods.RECT work, IReadOnlyList<WindowAlignmentTarget> targets,
        CanvasResizeEdge edges, int minimumWidth, int minimumHeight,
        int snapDistancePx, int releaseDistancePx, WindowAlignmentState previous)
    {
        int snap = Math.Max(0, snapDistancePx), release = Math.Max(snap, releaseDistancePx);
        List<Candidate> Candidates(NativeMethods.RECT frame, bool vertical) =>
            BuildCandidates(frame, work, targets, vertical)
                .Where(candidate => IsDraggedEdge(edges, vertical, candidate.Key.MovingAnchor) &&
                    Fits(ResizeEdge(frame, candidate, vertical), work, minimumWidth, minimumHeight))
                .ToList();

        Candidate? x = Select(Candidates(moving, true), previous.X, snap, release);
        NativeMethods.RECT aligned = x is Candidate horizontal ? ResizeEdge(moving, horizontal, true) : moving;
        Candidate? y = Select(Candidates(aligned, false), previous.Y, snap, release);
        if (y is Candidate vertical) aligned = ResizeEdge(aligned, vertical, false);
        return ResizeResult(aligned, x, y);
    }

    internal static WindowAlignmentScaleResult AlignScale(
        int centerX, int centerY, double baseWidthPx, double baseHeightPx,
        double scale, double minimumScale, double maximumScale,
        NativeMethods.RECT work, IReadOnlyList<WindowAlignmentTarget> targets, CanvasResizeEdge edges,
        int snapDistancePx, int releaseDistancePx, WindowAlignmentState previous)
    {
        scale = Math.Clamp(scale, minimumScale, maximumScale);
        NativeMethods.RECT raw = CenteredFrame(centerX, centerY, baseWidthPx, baseHeightPx, scale);
        int snap = Math.Max(0, snapDistancePx), release = Math.Max(snap, releaseDistancePx);
        var options = new List<ScaleCandidate>();
        foreach (bool vertical in new[] { true, false })
        {
            WindowAlignmentKey? oldKey = vertical ? previous.X : previous.Y;
            foreach (Candidate candidate in BuildCandidates(raw, work, targets, vertical))
            {
                if (!IsDraggedEdge(edges, vertical, candidate.Key.MovingAnchor)) continue;
                bool locked = candidate.Key == oldKey;
                if (Math.Abs(candidate.Delta) > (locked ? release : snap)) continue;
                int coordinate = Coordinate(candidate.TargetBounds, vertical, candidate.Key.TargetAnchor);
                int center = vertical ? centerX : centerY;
                bool start = candidate.Key.MovingAnchor == WindowAlignmentAnchor.Start;
                double diameter = 2d * (start ? center - coordinate : coordinate - center);
                double basis = vertical ? baseWidthPx : baseHeightPx;
                // Both rounded odd/even sizes can put this edge on the same pixel. Also retain an
                // already matching raw scale instead of needlessly changing the opposite dimension.
                foreach (double proposed in new[] { scale, diameter / basis, (diameter + (start ? 1 : -1)) / basis })
                {
                    double value = Math.Clamp(proposed, minimumScale, maximumScale);
                    NativeMethods.RECT frame = CenteredFrame(centerX, centerY, baseWidthPx, baseHeightPx, value);
                    if (!Fits(frame, work, 1, 1) ||
                        Coordinate(frame, vertical, candidate.Key.MovingAnchor) != coordinate) continue;
                    options.Add(new(candidate, vertical, value, frame, locked));
                }
            }
        }

        ScaleCandidate? selected = options.OrderByDescending(option => option.Locked)
            .ThenBy(option => Math.Abs(option.Scale - scale))
            .ThenBy(option => option.Candidate.Key.TargetKind)
            .ThenBy(option => option.Candidate.Key.TargetId)
            .ThenBy(option => option.Candidate.Key.MovingAnchor)
            .ThenBy(option => option.Candidate.Key.TargetAnchor)
            .ThenBy(option => option.Vertical ? 0 : 1)
            .Cast<ScaleCandidate?>().FirstOrDefault();
        if (selected is not ScaleCandidate chosen) return new(scale, ResizeResult(raw, null, null));

        Candidate? Matching(bool vertical)
        {
            if (chosen.Vertical == vertical) return chosen.Candidate;
            return Select(options.Where(option => option.Vertical == vertical &&
                    Coordinate(chosen.Bounds, vertical, option.Candidate.Key.MovingAnchor) ==
                    Coordinate(option.Candidate.TargetBounds, vertical, option.Candidate.Key.TargetAnchor))
                .Select(option => option.Candidate).ToList(), vertical ? previous.X : previous.Y, snap, release);
        }
        return new(chosen.Scale, ResizeResult(chosen.Bounds, Matching(true), Matching(false)));
    }

    private static NativeMethods.RECT CenteredFrame(int x, int y, double width, double height, double scale)
    {
        var frame = OrganizerInteractionMath.CreateCenteredBounds(x, y, width * scale, height * scale);
        return new() { Left = frame.Left, Top = frame.Top, Right = frame.Left + frame.Width, Bottom = frame.Top + frame.Height };
    }

    private static bool IsDraggedEdge(CanvasResizeEdge edges, bool vertical, WindowAlignmentAnchor anchor) =>
        edges.HasFlag(vertical
            ? anchor == WindowAlignmentAnchor.Start ? CanvasResizeEdge.Left : CanvasResizeEdge.Right
            : anchor == WindowAlignmentAnchor.Start ? CanvasResizeEdge.Top : CanvasResizeEdge.Bottom);

    private static NativeMethods.RECT ResizeEdge(NativeMethods.RECT frame, Candidate candidate, bool vertical)
    {
        int coordinate = Coordinate(candidate.TargetBounds, vertical, candidate.Key.TargetAnchor);
        if (vertical)
        {
            if (candidate.Key.MovingAnchor == WindowAlignmentAnchor.Start) frame.Left = coordinate;
            else frame.Right = coordinate;
        }
        else if (candidate.Key.MovingAnchor == WindowAlignmentAnchor.Start) frame.Top = coordinate;
        else frame.Bottom = coordinate;
        return frame;
    }

    private static bool Fits(NativeMethods.RECT frame, NativeMethods.RECT work, int minimumWidth, int minimumHeight) =>
        frame.Width >= minimumWidth && frame.Height >= minimumHeight &&
        frame.Left >= work.Left && frame.Top >= work.Top && frame.Right <= work.Right && frame.Bottom <= work.Bottom;

    private static WindowAlignmentResult ResizeResult(NativeMethods.RECT frame, Candidate? x, Candidate? y) => new(
        frame, new(x?.Key, y?.Key),
        x is Candidate horizontal ? CreateGuide(horizontal, frame, true) : null,
        y is Candidate vertical ? CreateGuide(vertical, frame, false) : null);

    private readonly record struct ScaleCandidate(Candidate Candidate, bool Vertical, double Scale, NativeMethods.RECT Bounds, bool Locked);
}
