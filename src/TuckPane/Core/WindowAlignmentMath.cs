using TuckPane.Services;

namespace TuckPane.Core;

internal enum WindowAlignmentAnchor
{
    Start,
    End
}

internal enum WindowAlignmentTargetKind
{
    Screen,
    Organizer
}

internal readonly record struct WindowAlignmentTarget(Guid Id, NativeMethods.RECT Bounds);

internal readonly record struct WindowAlignmentKey(
    WindowAlignmentTargetKind TargetKind,
    Guid TargetId,
    WindowAlignmentAnchor MovingAnchor,
    WindowAlignmentAnchor TargetAnchor);

internal readonly record struct WindowAlignmentState(WindowAlignmentKey? X, WindowAlignmentKey? Y);

internal readonly record struct WindowAlignmentGuide(bool Vertical, int Coordinate, int Start, int End);

internal readonly record struct WindowAlignmentResult(
    NativeMethods.RECT Bounds,
    WindowAlignmentState State,
    WindowAlignmentGuide? XGuide,
    WindowAlignmentGuide? YGuide);

internal readonly record struct WindowAlignmentInsets(int Left, int Top, int Right, int Bottom)
{
    internal static WindowAlignmentInsets From(NativeMethods.RECT window, NativeMethods.RECT frame) => new(
        frame.Left - window.Left,
        frame.Top - window.Top,
        window.Right - frame.Right,
        window.Bottom - frame.Bottom);

    internal NativeMethods.RECT ToFrame(NativeMethods.RECT window) => new()
    {
        Left = window.Left + Left,
        Top = window.Top + Top,
        Right = window.Right - Right,
        Bottom = window.Bottom - Bottom
    };

    internal NativeMethods.RECT ToWindow(NativeMethods.RECT frame) => new()
    {
        Left = frame.Left - Left,
        Top = frame.Top - Top,
        Right = frame.Right + Right,
        Bottom = frame.Bottom + Bottom
    };
}

internal static class WindowAlignmentMath
{
    internal const double SnapDistanceDip = 12;
    internal const double ReleaseDistanceDip = 20;

    internal static int DipToPx(double dip, uint dpi) =>
        Math.Max(1, (int)Math.Round(dip * Math.Max(1, dpi) / 96d));

    internal static NativeMethods.RECT ClampFrame(
        NativeMethods.RECT window,
        NativeMethods.RECT work,
        WindowAlignmentInsets insets)
    {
        NativeMethods.RECT frame = insets.ToFrame(window);
        int width = Math.Min(Math.Max(1, frame.Width), work.Width);
        int height = Math.Min(Math.Max(1, frame.Height), work.Height);
        int left = Math.Clamp(frame.Left, work.Left, work.Right - width);
        int top = Math.Clamp(frame.Top, work.Top, work.Bottom - height);
        return insets.ToWindow(new NativeMethods.RECT
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        });
    }

    internal static WindowAlignmentResult Align(
        NativeMethods.RECT moving,
        NativeMethods.RECT work,
        IReadOnlyList<WindowAlignmentTarget> targets,
        int snapDistancePx,
        int releaseDistancePx,
        WindowAlignmentState previous)
    {
        int snapDistance = Math.Max(0, snapDistancePx);
        int releaseDistance = Math.Max(snapDistance, releaseDistancePx);

        Candidate? x = Select(BuildCandidates(moving, work, targets, vertical: true), previous.X, snapDistance, releaseDistance);
        NativeMethods.RECT aligned = Offset(moving, x?.Delta ?? 0, 0);
        Candidate? y = Select(BuildCandidates(aligned, work, targets, vertical: false), previous.Y, snapDistance, releaseDistance);
        aligned = Offset(aligned, 0, y?.Delta ?? 0);

        return new(
            aligned,
            new WindowAlignmentState(x?.Key, y?.Key),
            x is Candidate xCandidate ? CreateGuide(xCandidate, aligned, vertical: true) : null,
            y is Candidate yCandidate ? CreateGuide(yCandidate, aligned, vertical: false) : null);
    }

    private static List<Candidate> BuildCandidates(
        NativeMethods.RECT moving,
        NativeMethods.RECT work,
        IReadOnlyList<WindowAlignmentTarget> targets,
        bool vertical)
    {
        var candidates = new List<Candidate>(2 + targets.Count * 2);
        Add(candidates, moving, work, vertical, WindowAlignmentTargetKind.Screen, Guid.Empty,
            WindowAlignmentAnchor.Start, WindowAlignmentAnchor.Start);
        Add(candidates, moving, work, vertical, WindowAlignmentTargetKind.Screen, Guid.Empty,
            WindowAlignmentAnchor.End, WindowAlignmentAnchor.End);

        foreach (WindowAlignmentTarget target in targets)
        {
            Add(candidates, moving, target.Bounds, vertical, WindowAlignmentTargetKind.Organizer, target.Id,
                WindowAlignmentAnchor.Start, WindowAlignmentAnchor.Start);
            Add(candidates, moving, target.Bounds, vertical, WindowAlignmentTargetKind.Organizer, target.Id,
                WindowAlignmentAnchor.End, WindowAlignmentAnchor.End);
        }
        return candidates;
    }

    private static void Add(
        List<Candidate> candidates,
        NativeMethods.RECT moving,
        NativeMethods.RECT target,
        bool vertical,
        WindowAlignmentTargetKind targetKind,
        Guid targetId,
        WindowAlignmentAnchor movingAnchor,
        WindowAlignmentAnchor targetAnchor)
    {
        var key = new WindowAlignmentKey(targetKind, targetId, movingAnchor, targetAnchor);
        candidates.Add(new(
            key,
            Coordinate(target, vertical, targetAnchor) - Coordinate(moving, vertical, movingAnchor),
            target));
    }

    private static Candidate? Select(
        IReadOnlyList<Candidate> candidates,
        WindowAlignmentKey? previous,
        int snapDistance,
        int releaseDistance)
    {
        if (previous is WindowAlignmentKey locked)
        {
            foreach (Candidate candidate in candidates)
            {
                if (candidate.Key == locked && Math.Abs(candidate.Delta) <= releaseDistance) return candidate;
            }
        }

        return candidates
            .Where(candidate => Math.Abs(candidate.Delta) <= snapDistance)
            .OrderBy(candidate => Math.Abs(candidate.Delta))
            .ThenBy(candidate => candidate.Key.TargetKind)
            .ThenBy(candidate => candidate.Key.TargetId)
            .ThenBy(candidate => candidate.Key.MovingAnchor)
            .ThenBy(candidate => candidate.Key.TargetAnchor)
            .Cast<Candidate?>()
            .FirstOrDefault();
    }

    private static WindowAlignmentGuide CreateGuide(Candidate candidate, NativeMethods.RECT moving, bool vertical)
    {
        int coordinate = Coordinate(candidate.TargetBounds, vertical, candidate.Key.TargetAnchor);
        if (candidate.Key.TargetKind == WindowAlignmentTargetKind.Screen)
        {
            return vertical
                ? new(true, coordinate, moving.Top, moving.Bottom)
                : new(false, coordinate, moving.Left, moving.Right);
        }

        if (vertical)
        {
            return new(
                true,
                coordinate,
                Math.Min(moving.Top, candidate.TargetBounds.Top),
                Math.Max(moving.Bottom, candidate.TargetBounds.Bottom));
        }

        return new(
            false,
            coordinate,
            Math.Min(moving.Left, candidate.TargetBounds.Left),
            Math.Max(moving.Right, candidate.TargetBounds.Right));
    }

    private static int Coordinate(NativeMethods.RECT bounds, bool vertical, WindowAlignmentAnchor anchor)
    {
        int start = vertical ? bounds.Left : bounds.Top;
        int end = vertical ? bounds.Right : bounds.Bottom;
        return anchor switch
        {
            WindowAlignmentAnchor.Start => start,
            _ => end
        };
    }

    private static NativeMethods.RECT Offset(NativeMethods.RECT bounds, int x, int y) => new()
    {
        Left = bounds.Left + x,
        Top = bounds.Top + y,
        Right = bounds.Right + x,
        Bottom = bounds.Bottom + y
    };

    private readonly record struct Candidate(WindowAlignmentKey Key, int Delta, NativeMethods.RECT TargetBounds);
}
