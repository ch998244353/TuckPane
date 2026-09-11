using TuckPane.Core;

internal static class ClickBounceCompactHoverChecks
{
    internal static void Run()
    {
        var failures = new List<string>();

        void Require(bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        void OffsetIs(ItemBounceMotion motion, float expected, string message)
        {
            Require(Math.Abs(motion.Offset - expected) < 0.001f,
                $"{message}: expected {expected}, actual {motion.Offset}");
        }

        var bounce = new ItemBounceMotion();
        OffsetIs(bounce, 0, "A new item starts at rest");
        Require(!bounce.IsActive, "A new item is inactive");
        bounce.Restart(100);
        Require(bounce.IsActive, "Opening an item starts its bounce");
        OffsetIs(bounce, 0, "The first opening starts at the resting position");

        Require(!bounce.Step(0.12), "The first peak has not settled");
        OffsetIs(bounce, -18, "The first peak at 120 ms moves upward by 18 pixels");
        bounce.Step(0.12);
        OffsetIs(bounce, 0, "The first jump returns to rest at 240 ms");
        Require(!bounce.Step(0.10), "The second peak has not settled");
        OffsetIs(bounce, -10, "The second peak at 340 ms is smaller");
        bounce.Step(0.10);
        OffsetIs(bounce, 0, "The second jump returns to rest at 440 ms");
        Require(!bounce.Step(0.08), "The third peak has not settled");
        OffsetIs(bounce, -5, "The third peak at 520 ms is smaller again");
        Require(bounce.Step(0.08), "The motion settles at 600 ms");
        OffsetIs(bounce, 0, "The completed motion returns to rest");
        Require(!bounce.IsActive, "The completed motion becomes inactive");

        bounce.Restart(100);
        bounce.Step(0.12);
        float beforeRestart = bounce.Offset;
        bounce.Restart(100);
        OffsetIs(bounce, beforeRestart, "Reopening preserves the current displacement");
        Require(!bounce.Step(0.52) && bounce.IsActive,
            "Reopening restarts the duration instead of finishing the old motion");
        Require(bounce.Step(0.08), "The restarted motion settles after a fresh 600 ms");
        OffsetIs(bounce, 0, "The restarted motion returns to rest");

        bounce.Restart(100);
        bounce.Step(0.12);
        bounce.Reset();
        OffsetIs(bounce, 0, "Cancellation immediately clears the displacement");
        Require(!bounce.IsActive, "Cancellation stops the motion");
        Require(bounce.Step(0.20), "A cancelled motion remains settled on later frames");
        OffsetIs(bounce, 0, "Later frames do not revive a cancelled motion");

        var other = new ItemBounceMotion();
        bounce.Restart(100);
        other.Restart(200);
        bounce.Step(0.12);
        other.Step(0.34);
        OffsetIs(bounce, -18, "Advancing another item does not change the first item");
        OffsetIs(other, -20, "Another item uses its own size and elapsed time");
        bounce.Reset();
        Require(other.IsActive, "Cancelling one item leaves another item active");
        OffsetIs(other, -20, "Cancelling one item preserves another item's displacement");

        var dockSurface = new HoverRect(0, 0, 320, 96);
        var dockIcon = new HoverRect(120, 8, 80, 80);
        float first = ItemBounceMotion.FitOffsetToBounds(-11.52f, 64, 1.25f,
            dockIcon, dockSurface, dockSurface, 12);
        float second = ItemBounceMotion.FitOffsetToBounds(-6.4f, 64, 1.25f,
            dockIcon, dockSurface, dockSurface, 12);
        float third = ItemBounceMotion.FitOffsetToBounds(-3.2f, 64, 1.25f,
            dockIcon, dockSurface, dockSurface, 12);
        Require(Math.Abs(first - -6.4f) < 0.001f && Math.Abs(first * 1.25f - -8) < 0.001f,
            "A magnified 64 DIP Dock icon uses only its 8 DIP of visible headroom");
        Require(Math.Abs(second / first - 0.5555556f) < 0.001f &&
            Math.Abs(third / first - 0.2777778f) < 0.001f,
            "Limited headroom scales all three jumps together and preserves their height ratios");

        var roomySurface = new HoverRect(0, 0, 320, 160);
        float unrestricted = ItemBounceMotion.FitOffsetToBounds(-11.52f, 64, 1.25f,
            new HoverRect(120, 40, 80, 80), roomySurface, roomySurface, 12);
        Require(Math.Abs(unrestricted - -11.52f) < 0.001f,
            "Sufficient headroom preserves the requested bounce displacement");

        var roundedSurface = new HoverRect(0, 0, 100, 96);
        var cornerViewport = new HoverRect(0, 2, 100, 94);
        var cornerIcon = new HoverRect(10, 8, 80, 80);
        float cornerOffset = ItemBounceMotion.FitOffsetToBounds(-11.52f, 64, 1.25f,
            cornerIcon, cornerViewport, roundedSurface, 25);
        var leftCorner = new System.Numerics.Vector2(cornerIcon.X, cornerIcon.Y + cornerOffset * 1.25f);
        var rightCorner = new System.Numerics.Vector2(cornerIcon.Right, leftCorner.Y);
        Require(cornerOffset < 0 &&
            HoverLayoutMath.ContainsRounded(roundedSurface, 25, leftCorner) &&
            HoverLayoutMath.ContainsRounded(roundedSurface, 25, rightCorner) &&
            cornerViewport.Contains(leftCorner) && cornerViewport.Contains(rightCorner),
            "A bounce near both rounded corners still moves upward while keeping both top corners inside the surface and viewport");

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }

        Console.WriteLine("PASS --click-bounce-compact-hover: three jumps, settling, continuous restart, cancellation, independent items, edge fitting.");
    }
}
