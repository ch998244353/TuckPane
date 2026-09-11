using System.Numerics;
using TuckPane.Models;

namespace TuckPane.Core;

// Immutable catalog identity; visual hosts may be replaced between either click.
internal readonly record struct CompactClickTarget(
    string RelativeName, string FullPath, WidgetItemKind Kind, Guid? NoteId, Guid? OrganizerId)
{
    internal static CompactClickTarget From(WidgetItem item) =>
        new(item.RelativeName, item.FullPath, item.Kind, item.NoteId, item.OrganizerId);

    internal bool SameAs(CompactClickTarget other) =>
        StringComparer.OrdinalIgnoreCase.Equals(RelativeName, other.RelativeName) &&
        StringComparer.OrdinalIgnoreCase.Equals(FullPath, other.FullPath) &&
        Kind == other.Kind && NoteId == other.NoteId && OrganizerId == other.OrganizerId;
}

// Per-window mouse sequence. Time is monotonic milliseconds; all distances are physical pixels.
internal sealed class CompactClickSequence
{
    private sealed record Press(CompactClickTarget Target, Vector2 Position, long Time, float DragDistance);
    private Press? _press, _previous;
    private bool _secondClick;

    internal void Begin(CompactClickTarget target, Vector2 position, long time,
        uint doubleClickMilliseconds, Vector2 doubleClickSize, float dragDistance)
    {
        _secondClick = _previous is { } previous && previous.Target.SameAs(target) &&
            time >= previous.Time && time - previous.Time <= doubleClickMilliseconds &&
            Math.Abs(position.X - previous.Position.X) <= doubleClickSize.X / 2 &&
            Math.Abs(position.Y - previous.Position.Y) <= doubleClickSize.Y / 2;
        _previous = null;
        _press = new(target, position, time, dragDistance);
    }

    internal CompactClickTarget? Complete(CompactClickTarget? releasedTarget, Vector2 position)
    {
        Press? press = _press;
        bool secondClick = _secondClick;
        _press = null;
        _secondClick = false;
        if (press is null || releasedTarget is not { } target || !press.Target.SameAs(target) ||
            !float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            Vector2.DistanceSquared(press.Position, position) >= press.DragDistance * press.DragDistance)
        {
            Reset();
            return null;
        }
        if (secondClick || target.Kind == WidgetItemKind.Organizer)
        {
            _previous = null;
            return press.Target;
        }
        _previous = press;
        return null;
    }

    internal void Reset()
    {
        _press = _previous = null;
        _secondClick = false;
    }
}
