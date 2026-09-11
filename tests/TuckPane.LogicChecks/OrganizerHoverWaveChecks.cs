using System.Numerics;
using TuckPane.Core;

internal static class OrganizerHoverWaveChecks
{
    internal static void Run()
    {
        DistanceAndCellCrossing();
        RetargetAndConvergence();
        ScrollAndAvailability();
        ResetAndReuse();
        Console.WriteLine("PASS --organizer-hover-wave: 4 pure-logic groups covering distance continuity, motion retargeting, scroll/suspension and reuse/idle. No GUI, input automation or visual smoothness measurement.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(float actual, float expected, string message, float tolerance = .0001f) =>
        Require(float.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{message}: expected {expected}, got {actual}.");

    private static OrganizerHoverWave EnabledWave()
    {
        var wave = new OrganizerHoverWave();
        wave.SetAvailability(enabled: true, suspended: false);
        return wave;
    }

    private static void DistanceAndCellCrossing()
    {
        var wave = EnabledWave();
        var center = new Vector2(100, 100);
        var pitch = new Vector2(80, 120);
        float At(Vector2 pointer, Vector2 item, bool list = false)
        {
            Require(wave.MovePointer(pointer), "An available wave must accept viewport coordinates.");
            float value = wave.GetTargetScale(item, pitch, list);
            Require(float.IsFinite(value) && value >= 1 && value <= 1.25f,
                "Distance sampling must stay finite and within the agreed hover range.");
            return value;
        }

        float peak = At(center, center);
        float horizontal = At(center + new Vector2(64, 0), center);
        float vertical = At(center + new Vector2(0, 96), center);
        float diagonal = At(center + new Vector2(64, 96), center);
        Require(peak > horizontal && horizontal > diagonal && diagonal > 1,
            "Grid influence must decrease with two-dimensional distance, including diagonals.");
        Near(horizontal, vertical, "Rectangular cells must normalize horizontal and vertical pitch equally");
        Near(At(center + pitch * 3, center), 1, "Items outside the influence must stay at their base scale");

        var adjacent = center + new Vector2(pitch.X, 0);
        var midpoint = (center + adjacent) / 2;
        float leftBefore = At(midpoint - new Vector2(.01f, 0), center);
        float rightBefore = At(midpoint - new Vector2(.01f, 0), adjacent);
        float leftAfter = At(midpoint + new Vector2(.01f, 0), center);
        float rightAfter = At(midpoint + new Vector2(.01f, 0), adjacent);
        Require(leftBefore > rightBefore && rightAfter > leftAfter,
            "The wave peak must transfer to the neighboring cell when crossing their boundary.");
        Near(leftBefore, leftAfter, "Crossing a grid boundary must not jump the previous item's scale");
        Near(rightBefore, rightAfter, "Crossing a grid boundary must not jump the next item's scale");

        float listBefore = At(center + new Vector2(-300, 59.99f), center, list: true);
        float listAfter = At(center + new Vector2(300, 60.01f), center, list: true);
        // Activation is separately gated by the baseline icon rectangle. Once active,
        // the compact wave still propagates vertically across complete rows.
        Near(listBefore, listAfter, "An already activated compact wave must propagate continuously along its rows");
        Require(listBefore > At(center + new Vector2(0, 120), center, list: true),
            "List influence must fall with vertical row distance.");
    }

    private static void RetargetAndConvergence()
    {
        var motion = new HoverWaveMotion();
        motion.Retarget(1.25f);
        motion.Step(1d / 60);
        Require(motion.Scale > 1 && motion.Scale < 1.25f && motion.Velocity > 0,
            "Entering hover must create intermediate motion instead of assigning the final scale.");
        float scale = motion.Scale;
        float velocity = motion.Velocity;
        motion.Retarget(1);
        Near(motion.Scale, scale, "Reversing a moving wave must retain its current scale", 0);
        Near(motion.Velocity, velocity, "Reversing a moving wave must retain its current velocity", 0);
        motion.Step(.0001);
        Require(Math.Abs(motion.Scale - scale) < .001f,
            "The first short step after reversal must continue from the current state.");
        Settle(motion, 1);

        motion.Retarget(.97f);
        Settle(motion, .97f);
        float pressed = motion.Scale;
        motion.Retarget(1);
        motion.Step(.001);
        Require(motion.Scale > pressed && motion.Scale < 1,
            "Releasing a pressed icon must interpolate from the pressed scale without clamping instantly to 1.");
        motion.Retarget(1.25f);
        Settle(motion, 1.25f);

        var sixtyHz = new HoverWaveMotion();
        var oneTwentyHz = new HoverWaveMotion();
        sixtyHz.Retarget(1.25f);
        oneTwentyHz.Retarget(1.25f);
        for (int index = 0; index < 6; index++) sixtyHz.Step(1d / 60);
        for (int index = 0; index < 12; index++) oneTwentyHz.Step(1d / 120);
        Near(sixtyHz.Scale, oneTwentyHz.Scale,
            "Equivalent elapsed time at 60/120 Hz must produce the same in-flight scale");
    }

    private static void Settle(HoverWaveMotion motion, float target)
    {
        bool settled = false;
        for (int step = 0; step < 180 && !settled; step++)
        {
            settled = motion.Step(1d / 120);
            Require(float.IsFinite(motion.Scale) && float.IsFinite(motion.Velocity) &&
                    motion.Scale >= .97f && motion.Scale <= 1.25f,
                "Motion must stay finite and within press/hover bounds while settling.");
        }
        Require(settled && !motion.IsActive, "Settled motion must let the rendering loop become idle.");
        Near(motion.Scale, target, "Motion must converge to the current target");
    }

    private static void ScrollAndAvailability()
    {
        var wave = EnabledWave();
        var center = new Vector2(120, 100);
        var pitch = new Vector2(80, 120);
        void Neutral(string reason)
        {
            Require(!wave.HasPointer, reason + ": stale pointer remains.");
            Near(wave.GetTargetScale(center, pitch, compactList: false), 1, reason);
        }
        void MoveAgain()
        {
            Require(wave.MovePointer(center) && wave.GetTargetScale(center, pitch, compactList: false) > 1,
                "A new pointer move after resuming must restore hover influence.");
        }

        MoveAgain();
        wave.SetScrolling(true);
        Require(!wave.MovePointer(center), "Pointer moves during scrolling must not re-arm hover.");
        wave.SetScrolling(false);
        Neutral("Ending a scroll must wait for a new pointer move");
        MoveAgain();

        foreach (bool suspend in new[] { true, false })
        {
            wave.SetAvailability(enabled: suspend, suspended: suspend);
            Require(!wave.MovePointer(center), "Suspended/disabled hover must reject pointer updates.");
            Neutral("Suspending or disabling must clear the pointer");
            wave.SetAvailability(enabled: true, suspended: false);
            Neutral("Re-enabling must not restore the old pointer");
            MoveAgain();
        }
    }

    private static void ResetAndReuse()
    {
        var wave = EnabledWave();
        var motion = new HoverWaveMotion();
        wave.MovePointer(new Vector2(100, 100));
        motion.Retarget(1.25f);
        motion.Step(1d / 60);
        Require(motion.IsActive && motion.Velocity > 0, "The reuse case must begin with an in-flight animation.");
        wave.Leave();
        motion.Reset();
        Require(!wave.HasPointer && !motion.IsActive && motion.Velocity == 0 && motion.Step(.1),
            "Leaving/recycling must discard pointer, velocity and pending animation work.");
        Near(motion.Scale, 1, "Recycled content must restore its base scale");
        Near(motion.Target, 1, "Recycled content must discard its previous target");

        var newCenter = new Vector2(900, 700);
        wave.MovePointer(newCenter);
        Near(wave.GetTargetScale(new Vector2(100, 100), new Vector2(80, 120), compactList: false), 1,
            "Reused input state must no longer magnify the previous item's location");
        motion.Retarget(wave.GetTargetScale(newCenter, new Vector2(80, 120), compactList: false));
        motion.Step(1d / 120);
        Require(motion.IsActive && motion.Scale > 1 && motion.Scale < motion.Target,
            "Reused content must start fresh continuous motion at the new location.");
    }
}
