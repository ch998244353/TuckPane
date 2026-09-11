using System.Numerics;

namespace TuckPane.Core;

internal static class ItemMotionMath
{
    internal static Vector3 RedirectVelocity(Vector3 current, Vector3 target, Vector3 velocity, float maximumSpeed)
    {
        Vector3 remaining = target - current;
        if (remaining.LengthSquared() < .0001f) return Vector3.Zero;

        Vector3 direction = Vector3.Normalize(remaining);
        float forwardSpeed = Math.Clamp(Vector3.Dot(velocity, direction), 0, maximumSpeed);
        return direction * forwardSpeed;
    }

    internal static bool StepCriticalSpring(
        ref Vector3 value,
        ref Vector3 velocity,
        Vector3 target,
        double seconds,
        double responseSeconds = .14,
        float positionToleranceSquared = .0004f,
        float velocityToleranceSquared = .01f)
    {
        if (seconds <= 0) return IsSettled(value, velocity, target, positionToleranceSquared, velocityToleranceSquared);

        double omega = 2 * Math.PI / responseSeconds;
        double decay = Math.Exp(-omega * seconds);
        StepComponent(ref value.X, ref velocity.X, target.X, seconds, omega, decay);
        StepComponent(ref value.Y, ref velocity.Y, target.Y, seconds, omega, decay);
        StepComponent(ref value.Z, ref velocity.Z, target.Z, seconds, omega, decay);
        if (!IsSettled(value, velocity, target, positionToleranceSquared, velocityToleranceSquared)) return false;

        value = target;
        velocity = Vector3.Zero;
        return true;
    }

    private static void StepComponent(ref float value, ref float velocity, float target, double seconds, double omega, double decay)
    {
        double displacement = value - target;
        double coefficient = velocity + omega * displacement;
        value = (float)(target + (displacement + coefficient * seconds) * decay);
        velocity = (float)((velocity - omega * coefficient * seconds) * decay);
    }

    private static bool IsSettled(Vector3 value, Vector3 velocity, Vector3 target,
        float positionToleranceSquared, float velocityToleranceSquared) =>
        Vector3.DistanceSquared(value, target) < positionToleranceSquared &&
        velocity.LengthSquared() < velocityToleranceSquared;
}
