using System.Numerics;
using TuckPane.Models;

namespace TuckPane.Core;

// Pointer coordinates and item centres are in the unscaled scroll viewport.
internal sealed class OrganizerHoverWave
{
    internal static bool IsEnabledFor(OrganizerPlacementMode mode, bool compactList, bool iconEnabled,
        bool compactEnabled = true, bool dockEnabled = true) =>
        mode == OrganizerPlacementMode.Dock ? dockEnabled : compactList ? compactEnabled : iconEnabled;
    internal const float MaximumScale = 1.25f;
    internal const double Radius = 1.75;
    private Vector2? _pointer;
    private bool _enabled;
    private bool _suspended;
    private bool _scrolling;

    internal bool HasPointer => _pointer.HasValue;
    internal Vector2? Pointer => _pointer;

    // Native presence checks may clear a stale pointer, but never rearm one after a menu/leave.
    internal bool ObservePointerPresence(bool overOwner, bool insideRegion)
    {
        if (!overOwner || !insideRegion) Leave();
        return HasPointer;
    }

    internal void SetAvailability(bool enabled, bool suspended)
    {
        _enabled = enabled;
        _suspended = suspended;
        if (!enabled || suspended) Leave();
    }

    internal void SetScrolling(bool scrolling, bool preservePointer = false)
    {
        _scrolling = scrolling && !preservePointer;
        if (_scrolling) Leave();
        // Compact lists keep viewport coordinates while rows scroll beneath them.
        // Other modes still require fresh input after scrolling.
    }

    internal bool MovePointer(Vector2 pointer)
    {
        if (!_enabled || _suspended || _scrolling ||
            !float.IsFinite(pointer.X) || !float.IsFinite(pointer.Y)) return false;
        _pointer = pointer;
        return true;
    }

    internal void Leave() => _pointer = null;

    internal float GetTargetScale(Vector2 center, Vector2 pitch, bool compactList,
        double maximumScale = MaximumScale)
    {
        if (_pointer is not Vector2 pointer || !float.IsFinite(center.X) || !float.IsFinite(center.Y) ||
            !float.IsFinite(pitch.X) || !float.IsFinite(pitch.Y) || pitch.X <= 0 || pitch.Y <= 0) return 1;
        double x = compactList ? 0 : (pointer.X - center.X) / pitch.X;
        double y = (pointer.Y - center.Y) / pitch.Y;
        double distance = Math.Sqrt(x * x + y * y);
        if (distance >= Radius) return 1;
        double weight = (1 + Math.Cos(Math.PI * distance / Radius)) / 2;
        return (float)(1 + (GlobalSettings.NormalizeHoverMagnificationScale(maximumScale) - 1) * weight);
    }
}

internal sealed class HoverWaveMotion
{
    private Vector3 _scale = Vector3.One;
    private Vector3 _velocity;
    private Vector3 _target = Vector3.One;

    internal float Scale => _scale.X;
    internal float Velocity => _velocity.X;
    internal float Target => _target.X;
    internal bool IsActive => _scale != _target || _velocity != Vector3.Zero;

    internal void Retarget(float target)
    {
        // The lower bound also accommodates the existing icon press feedback.
        float bounded = float.IsFinite(target) ? Math.Clamp(target, .97f, OrganizerHoverWave.MaximumScale) : 1;
        _target = new Vector3(bounded, bounded, 1);
    }

    internal bool Step(double seconds)
    {
        if (!IsActive) return true;
        if (!double.IsFinite(seconds) || seconds <= 0) return false;
        float minimum = _target.X < 1 || _scale.X < 1 ? .97f : 1;
        bool settled = ItemMotionMath.StepCriticalSpring(ref _scale, ref _velocity, _target, seconds,
            positionToleranceSquared: .000001f, velocityToleranceSquared: .0001f);
        float bounded = Math.Clamp(_scale.X, minimum, OrganizerHoverWave.MaximumScale);
        if (bounded != _scale.X)
        {
            _scale = new Vector3(bounded, bounded, 1);
            if ((bounded <= minimum && _velocity.X < 0) ||
                (bounded >= OrganizerHoverWave.MaximumScale && _velocity.X > 0)) _velocity = Vector3.Zero;
        }
        return settled || !IsActive;
    }

    internal void Reset()
    {
        _scale = _target = Vector3.One;
        _velocity = Vector3.Zero;
    }
}

// The layout anchor has its own continuous state. Retargeting must not instantly
// translate a row whose scales are still returning from the previous pointer.
internal sealed class HoverAnchorMotion
{
    private Vector3 _position;
    private Vector3 _velocity;
    private Vector3 _target;
    private bool _initialized;

    internal Vector2 Position => new(_position.X, _position.Y);
    internal Vector2 Velocity => new(_velocity.X, _velocity.Y);
    internal bool IsActive => _position != _target || _velocity != Vector3.Zero;

    internal void Retarget(Vector2 position, bool atRest = false)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) return;
        _target = new(position, 0);
        if (!_initialized || atRest)
        {
            _position = _target;
            _velocity = Vector3.Zero;
            _initialized = true;
        }
    }

    internal bool Step(double seconds) => !_initialized || !IsActive ||
        double.IsFinite(seconds) && seconds > 0 &&
        ItemMotionMath.StepCriticalSpring(ref _position, ref _velocity, _target, seconds);

    internal void Reset()
    {
        _initialized = false;
        _position = _target = _velocity = Vector3.Zero;
    }
}
