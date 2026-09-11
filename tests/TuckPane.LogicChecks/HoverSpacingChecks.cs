using System.Numerics;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

// This selector exercises only the production geometry/state for this change.
// It never creates a window, injects input or touches the user's stored data.
internal static class HoverSpacingChecks
{
    internal static void Run(string area)
    {
        switch (area)
        {
            case "spacing":
                Spacing();
                break;
            case "exit":
                Exit();
                break;
            case "corners":
                Corners();
                break;
            default:
                throw new ArgumentException($"Unknown hover-spacing group: {area}.");
        }
        Console.WriteLine($"PASS --hover-spacing-fixes {area}: production pure-logic checks; no GUI or input automation.");
    }

    private static void Spacing()
    {
        foreach (var mode in new[] { (Name: "horizontal Dock", Horizontal: true, Compact: false),
                     (Name: "vertical Dock", Horizontal: false, Compact: false),
                     (Name: "compact rows", Horizontal: false, Compact: true) })
        {
            float length = mode.Compact ? 36 : 64;
            float gap = mode.Compact ? 8 : 16;
            HoverRect Bounds(int i) => mode.Horizontal ? new(i * (length + gap), 10, 64, 64) :
                new(10, i * (length + gap), mode.Compact ? 180 : 64, length);
            float Axis(Vector2 point) => mode.Horizontal ? point.X : point.Y;
            float Start(HoverRect rect) => mode.Horizontal ? rect.X : rect.Y;
            float End(HoverRect rect) => mode.Horizontal ? rect.Right : rect.Bottom;
            float anchor = Axis(Bounds(1).Center) + 4;

            foreach (float progress in new[] { 0f, .5f, 1f })
            {
                float[] scales = [1 + .1f * progress, 1 + .25f * progress, 1 + .05f * progress];
                HoverLayoutItem[] items = Enumerable.Range(0, 3).Select(i => new HoverLayoutItem(Bounds(i), scales[i])).ToArray();
                HoverLayoutPose[] poses = HoverLayoutMath.Calculate(items, anchor, mode.Horizontal, mode.Compact);
                for (int i = 1; i < poses.Length; i++)
                    Near(Start(poses[i].Bounds) - End(poses[i - 1].Bounds), gap * (scales[i - 1] + scales[i]) / 2,
                        $"{mode.Name}: the visible gap must grow with its two neighbours at every animation step");
                Near(Axis(poses[1].Bounds.Center) + (anchor - Axis(Bounds(1).Center)) * scales[1], anchor,
                    $"{mode.Name}: magnification must leave the pointer's baseline content point fixed");
                Near(poses[1].Bounds.Width, Bounds(1).Width * scales[1],
                    $"{mode.Name}: the whole content width must scale, including a compact row's filename");
                if (mode.Compact) Near(poses[1].Bounds.X, Bounds(1).X, "Compact row scaling must retain its left edge");
                if (progress == 0)
                    Require(poses.Select(pose => pose.Bounds).SequenceEqual(items.Select(item => item.Bounds)),
                        $"{mode.Name}: resting geometry must be the original layout");
                else
                {
                    Require(Start(poses[0].Bounds) < Start(Bounds(0)) && End(poses[^1].Bounds) > End(Bounds(2)),
                        $"{mode.Name}: magnification must push both ends outward");
                    Vector2 overflowPoint = mode.Horizontal ? new(poses[0].Bounds.X + .01f, poses[0].Bounds.Center.Y) :
                        new(poses[0].Bounds.Center.X, poses[0].Bounds.Y + .01f);
                    Require(!Bounds(0).Contains(overflowPoint) && poses[0].Bounds.Contains(overflowPoint),
                        $"{mode.Name}: expanded layout geometry must include growth before the final surface clip");
                }

                HoverRect viewport = mode.Horizontal ? new(5, 0, End(Bounds(2)) - 12, 300) :
                    new(0, 5, 300, End(Bounds(2)) - 12);
                foreach (int index in new[] { 0, 2 })
                {
                    HoverRect clipped = HoverLayoutMath.VisibleAfterTransform(Bounds(index), poses[index].Scale,
                        poses[index].Translation, mode.Compact, viewport);
                    Require(clipped == clipped.Intersect(viewport),
                        $"{mode.Name}: crop the final transformed content to the fixed viewport");
                    Require((mode.Horizontal ? clipped.Width : clipped.Height) < length * scales[index],
                        "An enlarged edge item must remain cropped at the final viewport boundary");
                }
                Require(HoverLayoutMath.VisibleAfterTransform(Bounds(3), 1.25f,
                    Vector2.Zero, mode.Compact, viewport).IsEmpty, "A fully offscreen final item must remain absent");

                float boundary = End(Bounds(0));
                HoverLayoutPose[] before = HoverLayoutMath.Calculate(items, boundary - .0001f, mode.Horizontal, mode.Compact);
                HoverLayoutPose[] after = HoverLayoutMath.Calculate(items, boundary + .0001f, mode.Horizontal, mode.Compact);
                Near(Axis(before[1].Translation), Axis(after[1].Translation),
                    $"{mode.Name}: moving from content into its gap must not jump the anchor", .001);
            }
        }

        NativeMethods.RECT panel = new() { Left = -811, Top = 243, Right = -539, Bottom = 339 };
        NativeMethods.RECT host = HoverLayoutMath.Inflate(panel, 41, 27);
        NativeMethods.RECT restored = HoverLayoutMath.Deflate(host, 41, 27);
        Require(restored.Left == panel.Left && restored.Top == panel.Top && restored.Right == panel.Right &&
                restored.Bottom == panel.Bottom && (host.Left + host.Right) == (panel.Left + panel.Right),
            "Transparent window margins must round-trip panel position and preserve its centre on a negative-coordinate monitor");
        NativeMethods.RECT pixels = HoverLayoutMath.ToPixels(new(-.25f, .5f, 64, 32), 1.25, new(30, 45), fringe: 1);
        Require(pixels.Left == 36 && pixels.Top == 55 && pixels.Right == 119 && pixels.Bottom == 98,
            "The 125% DPI native region must include the transparent offset, round outward and retain one antialias pixel");
        // OS suggestion includes the 125% padding rescaled to 150%: 41 -> 49 px, 27 -> 32 px.
        NativeMethods.RECT suggested = new() { Left = -753, Top = 296, Right = -247, Bottom = 504 };
        NativeMethods.RECT dpiPanel = HoverLayoutMath.AdjustDpiSuggestedBounds(suggested, 41, 27, 1.25, 1.5);
        Near((dpiPanel.Right - dpiPanel.Left) / 1.5, 272, "A DPI transition must exclude scaled host padding from the logical panel width");
        Near((dpiPanel.Bottom - dpiPanel.Top) / 1.5, 96, "A DPI transition must exclude scaled host padding from the logical panel height");
        Require(dpiPanel.Left + dpiPanel.Right == suggested.Left + suggested.Right &&
                dpiPanel.Top + dpiPanel.Bottom == suggested.Top + suggested.Bottom,
            "Removing temporary padding at the new DPI must preserve the OS-suggested panel centre");
    }

    private static void Exit()
    {
        DockGeometry dock = DockLayoutMath.Calculate(3, 64, DockOrientation.Horizontal);
        HoverRect surface = new(0, 0, (float)dock.Width, (float)dock.Height);
        HoverRect[] overflow = [new(-12, 20, 80, 80)];
        Vector2 center = dock.ItemCenter(1);
        Vector2 pitch = new((float)dock.Pitch, (float)dock.Pitch);
        foreach (var scenario in new[] { (Name: "outside background", Point: new Vector2(400, 120), OverOwner: true),
                     (Name: "transparent rounded corner", Point: new Vector2(1, 1), OverOwner: true),
                     (Name: "owner obscured with no exit event", Point: center, OverOwner: false) })
        {
            var wave = new OrganizerHoverWave();
            wave.SetAvailability(enabled: true, suspended: false);
            Require(wave.MovePointer(center), "A fresh pointer must enable the wave");
            var motion = new HoverWaveMotion();
            motion.Retarget(wave.GetTargetScale(center, pitch, compactList: false));
            motion.Step(1d / 60);
            Require(motion.Scale > 1, "Exercise exit while magnification is actually visible");

            Require(wave.ObservePointerPresence(true, HoverLayoutMath.ContainsPointer(surface,
                    (float)dock.CornerRadius, new(-5, 40), overflow)),
                "Staying on an overflow icon must keep the wave active outside the background");
            bool inside = HoverLayoutMath.ContainsPointer(surface, (float)dock.CornerRadius, scenario.Point, overflow);
            Require(!wave.ObservePointerPresence(scenario.OverOwner, inside) && !wave.HasPointer,
                $"{scenario.Name}: production presence observation must clear the stale pointer without an exit event");
            float before = motion.Scale;
            motion.Retarget(wave.GetTargetScale(center, pitch, compactList: false));
            Near(motion.Target, 1, "Leaving must target the original scale");
            Near(motion.Scale, before, "Changing the target must preserve the current animation state");
            motion.Step(1d / 60);
            Require(motion.Scale > 1 && motion.IsActive, "Leaving must animate back instead of resetting instantly");
            for (int frame = 0; frame < 240 && motion.IsActive; frame++) motion.Step(1d / 60);
            Require(!motion.IsActive && motion.Scale == 1 && motion.Velocity == 0,
                $"{scenario.Name}: the return animation must converge so frame/pointer checks can stop");
            Require(!wave.ObservePointerPresence(true, true), "A later presence poll must not restore the stale pointer");
            Vector2 next = dock.ItemCenter(2);
            Require(wave.MovePointer(next) && wave.Pointer == next &&
                    wave.GetTargetScale(next, pitch, false) > wave.GetTargetScale(center, pitch, false),
                "Re-entry must magnify the new pointer location rather than the old target");
        }
    }

    private static void Corners()
    {
        foreach (DockOrientation orientation in new[] { DockOrientation.Horizontal, DockOrientation.Vertical })
        {
            DockGeometry dock = DockLayoutMath.Calculate(3, 64, orientation);
            Near(dock.CornerRadius, 18, "Default Dock corners must grow from 12 to 18 DIP");
            Near(DockLayoutMath.Calculate(3, 128, orientation).CornerRadius, 36,
                "The larger corner must retain icon-size proportionality");
            Require(!dock.Contains(Vector2.Zero) && !dock.Contains(new Vector2(4, 4)) &&
                    dock.Contains(new Vector2(6, 6)) && dock.Contains(new Vector2(18, 0)),
                "The enlarged circle must reject the transparent outer corner and accept points inside its arc.");

            foreach (double dpi in new[] { 1d, 1.25d })
            {
                RoundedSurfaceGeometry surface = RoundedSurfaceGeometry.Create(dock.Width, dock.Height,
                    dock.CornerRadius, dpi);
                double expectedRadius = Math.Round(18 * dpi) / dpi;
                Near(surface.Radius, expectedRadius, "All layers must use the same pixel-aligned radius");
                Near(surface.Width, dock.Width, "DPI alignment must leave the arranged panel width fixed");
                Near(surface.Height, dock.Height, "DPI alignment must leave the arranged panel height fixed");
                HoverRect panel = new(0, 0, surface.Width, surface.Height);
                Require(!HoverLayoutMath.ContainsRounded(panel, surface.Radius, new(4, 4)) &&
                        HoverLayoutMath.ContainsRounded(panel, surface.Radius, new(6, 6)) &&
                        !HoverLayoutMath.ContainsRounded(panel, surface.Radius, new(surface.Width - 4, surface.Height - 4)) &&
                        HoverLayoutMath.ContainsRounded(panel, surface.Radius, new(surface.Width - 6, surface.Height - 6)),
                    "DPI-aligned input geometry must reject outer corner pixels and include the rounded interior");
                RoundedStrokeGeometry stroke = surface.InsetStroke((float)(1 / dpi));
                Near(stroke.Offset + stroke.Width / 2, surface.Width / 2,
                    "The stroke and surface must share their horizontal centre");
                Near(stroke.Offset + stroke.Height / 2, surface.Height / 2,
                    "The stroke and surface must share their vertical centre");
                Near(stroke.Radius + stroke.Offset, surface.Radius,
                    "The stroke's outside edge must meet the same rounded outline");
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, string message, double tolerance = .0001) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{message}: expected {expected}, got {actual}.");
}
