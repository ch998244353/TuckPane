using System.Numerics;
using TuckPane.Models;

namespace TuckPane.Core;

// All Dock icons use this one moving field and one strength envelope. There
// are no independent item springs whose residual waves can move the whole row.
internal sealed class DockHoverMotion
{
    private readonly HoverAnchorMotion _anchor = new();
    private readonly HoverWaveMotion _strength = new();

    internal Vector2 Position => _anchor.Position;
    internal float Scale => _strength.Scale;
    internal bool IsActive => _anchor.IsActive || _strength.IsActive;

    internal void Retarget(Vector2? pointer, float maximumScale)
    {
        if (pointer is Vector2 position && float.IsFinite(position.X) && float.IsFinite(position.Y))
        {
            _anchor.Retarget(position, atRest: Scale == 1 && !_strength.IsActive);
            _strength.Retarget((float)GlobalSettings.NormalizeHoverMagnificationScale(maximumScale));
        }
        else _strength.Retarget(1);
    }

    internal bool Step(double seconds)
    {
        bool anchorSettled = _anchor.Step(seconds);
        bool strengthSettled = _strength.Step(seconds);
        return anchorSettled && strengthSettled;
    }

    internal void Reset()
    {
        _anchor.Reset();
        _strength.Reset();
    }
}
