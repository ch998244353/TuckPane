using TuckPane.Services;

namespace TuckPane.Core;

internal static partial class WindowAlignmentMath
{
    internal static WindowAlignmentScaleResult AlignCompactCorner(NativeMethods.RECT start,
        CompactResizeResult raw, NativeMethods.RECT work, IReadOnlyList<WindowAlignmentTarget> targets,
        CanvasResizeEdge edges, int snapDistancePx, int releaseDistancePx, WindowAlignmentState previous)
    {
        var options = new List<ScaleCandidate>();
        foreach (bool vertical in new[] { true, false })
        {
            foreach (Candidate candidate in BuildCandidates(raw.Bounds, work, targets, vertical))
            {
                if (!IsDraggedEdge(edges, vertical, candidate.Key.MovingAnchor)) continue;
                bool locked = candidate.Key == (vertical ? previous.X : previous.Y);
                if (Math.Abs(candidate.Delta) > (locked ? releaseDistancePx : snapDistancePx)) continue;
                int coordinate = Coordinate(candidate.TargetBounds, vertical, candidate.Key.TargetAnchor);
                bool draggingStart = candidate.Key.MovingAnchor == WindowAlignmentAnchor.Start;
                double size = draggingStart
                    ? Coordinate(start, vertical, WindowAlignmentAnchor.End) - coordinate
                    : coordinate - Coordinate(start, vertical, WindowAlignmentAnchor.Start);
                double scale = size / (vertical ? start.Width : start.Height);
                if (scale < raw.MinimumScale || scale > raw.MaximumScale) continue;
                NativeMethods.RECT bounds = CompactResizeMath.FromScale(start, edges, scale);
                if (!Fits(bounds, work, 1, 1) || Coordinate(bounds, vertical, candidate.Key.MovingAnchor) != coordinate) continue;
                options.Add(new(candidate, vertical, scale, bounds, locked));
            }
        }
        ScaleCandidate? selected = options.OrderByDescending(option => option.Locked)
            .ThenBy(option => Math.Abs(option.Scale - raw.Scale))
            .ThenBy(option => option.Candidate.Key.TargetKind).ThenBy(option => option.Candidate.Key.TargetId)
            .ThenBy(option => option.Vertical ? 0 : 1).Cast<ScaleCandidate?>().FirstOrDefault();
        return selected is ScaleCandidate chosen
            ? new(chosen.Scale, ResizeResult(chosen.Bounds, chosen.Vertical ? chosen.Candidate : null,
                chosen.Vertical ? null : chosen.Candidate))
            : new(raw.Scale, ResizeResult(raw.Bounds, null, null));
    }
}
