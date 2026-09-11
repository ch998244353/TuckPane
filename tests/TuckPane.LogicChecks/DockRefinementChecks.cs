using System.Numerics;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class DockRefinementChecks
{
    internal static async Task RunAsync(string root)
    {
        CheckGeometry();
        CheckWheel();
        CheckIconBounds();
        await CheckSizeChangesAsync(root);
    }

    private static void CheckGeometry()
    {
        foreach (DockOrientation orientation in new[] { DockOrientation.Horizontal, DockOrientation.Vertical })
        {
            bool horizontal = orientation == DockOrientation.Horizontal;
            DockGeometry geometry = DockLayoutMath.Calculate(3, 64, orientation);
            Near(geometry.Width, horizontal ? 274 : 96, "Dock width with orientation-specific left/right padding");
            Near(geometry.Height, horizontal ? 96 : 324, "Dock height with fixed hover-safe ends");
            Require(geometry.EndInset == (horizontal ? 25 : 50) && geometry.CrossInset == 16 && geometry.CornerRadius == 18 &&
                    geometry.HorizontalInset == (horizontal ? 25 : 16) &&
                    geometry.VerticalInset == (horizontal ? 16 : 50),
                "Rendering and hit testing must share the enlarged corner radius and orientation-aware insets.");
            Require(geometry.ItemCenter(0) == (horizontal ? new Vector2(57, 48) : new Vector2(48, 82)),
                "The first Dock item must respect orientation-specific left/right padding.");
            Require(geometry.Contains(new Vector2(4, 20)) && !geometry.Contains(Vector2.Zero),
                "Rounded corners must accept (4, 20) while excluding the outer corner.");

            DockGeometry scaled = DockLayoutMath.Calculate(3, 128, orientation);
            Near(scaled.Width, geometry.Width * 2 - (horizontal ? 2 : 0), "Scaling must keep the halved physical end guards fixed");
            Near(scaled.Height, geometry.Height * 2 - (horizontal ? 0 : 4), "Scaling must keep the two 2 DIP end guards fixed");
            Require(scaled.ItemCenter(0) == geometry.ItemCenter(0) * 2 - (horizontal ? new Vector2(1, 0) : new Vector2(0, 2)) &&
                    scaled.Pitch == geometry.Pitch * 2,
                "Scaling must preserve item/gap proportions while retaining the fixed end guard.");
            double endGuard = horizontal ? 1 : 2;
            Require(scaled.EndInset - endGuard == (geometry.EndInset - endGuard) * 2 && scaled.CrossInset == geometry.CrossInset * 2 &&
                    scaled.CornerRadius == geometry.CornerRadius * 2 && scaled.Contains(new Vector2(8, 40)),
                "Scaling must keep corner hit testing and all insets proportional to the whole Dock.");
            Vector2 center = new(-713.2f, 429.2f);
            NativeMethods.RECT before = DockLayoutMath.CalculateBounds(geometry, center, 1.25);
            NativeMethods.RECT after = DockLayoutMath.CalculateBounds(scaled, center, 1.25);
            Require(Math.Abs((before.Left + before.Right) / 2d - (after.Left + after.Right) / 2d) <= .5 &&
                    Math.Abs((before.Top + before.Bottom) / 2d - (after.Top + after.Bottom) / 2d) <= .5,
                "Scaling must preserve the Dock centre within pixel rounding at 125% DPI.");

            DockGeometry empty = DockLayoutMath.Calculate(0, 64, orientation);
            DockGeometry one = DockLayoutMath.Calculate(1, 64, orientation);
            Require(empty.Count == 0 && empty.Width == one.Width && empty.Height == one.Height &&
                    empty.Contains(empty.ItemCenter(0)),
                "An empty Dock must retain one interactive placeholder slot.");
        }
    }

    private static void CheckWheel()
    {
        int remainder = 0;
        double size = DockLayoutMath.ApplyWheelDelta(64, 60, true, false, ref remainder);
        Require(size == 64 && remainder == 60, "A partial wheel tick must accumulate without resizing.");
        size = DockLayoutMath.ApplyWheelDelta(size, 180, true, false, ref remainder);
        Require(size == 72 && remainder == 0, "Accumulated positive ticks must enlarge by 4 DIP per tick.");
        size = DockLayoutMath.ApplyWheelDelta(size, -120, true, false, ref remainder);
        Require(size == 68 && remainder == 0, "A negative wheel tick must shrink by 4 DIP.");
        Require(DockLayoutMath.ApplyWheelDelta(128, 240, true, false, ref remainder) == 128 &&
                DockLayoutMath.ApplyWheelDelta(32, -240, true, false, ref remainder) == 32,
            "Wheel sizing must stop at 32 and 128 DIP.");
        remainder = 60;
        Require(DockLayoutMath.ApplyWheelDelta(size, 120, false, false, ref remainder) == size && remainder == 0,
            "Ordinary scrolling must leave the size alone and clear a previous partial Ctrl tick.");
        remainder = 60;
        Require(DockLayoutMath.ApplyWheelDelta(size, 120, true, true, ref remainder) == size && remainder == 0,
            "Busy interactions must ignore sizing and clear partial ticks before the next interaction.");
    }

    private static void CheckIconBounds()
    {
        IconCacheService.IconSnapshot widePadding = RectangleIcon(24, 8, 10, 8, 4);
        IconCacheService.IconSnapshot narrowPadding = RectangleIcon(16, 2, 5, 12, 6);
        foreach (IconCacheService.IconSnapshot source in new[] { widePadding, narrowPadding })
        {
            byte[] original = (byte[])source.Pixels.Clone();
            IconCacheService.IconSnapshot result = IconCacheService.NormalizeDockIcon(source);
            Require(IconCacheService.TryGetVisiblePixelBounds(result, out var visible) &&
                    visible.Left == 0 && visible.Top == 0 &&
                    visible.Right == result.Width && visible.Bottom == result.Height,
                "Dock Shell icons with different transparent padding must fill their normalized image bounds.");
            Near((double)result.Width / result.Height, 2, "Normalizing must preserve the visible icon aspect ratio");
            double fit = 64d / Math.Max(result.Width, result.Height);
            Near(Math.Max(visible.Width, visible.Height) * fit, 64,
                "Both normalized icons must display the same visible longest edge in a 64 DIP slot");
            Require(source.Pixels.SequenceEqual(original), "Dock normalization must not mutate shared source pixels.");
        }

        var transparent = new IconCacheService.IconSnapshot(new byte[8 * 8 * 4], 8, 8);
        IconCacheService.IconSnapshot fallback = IconCacheService.NormalizeDockIcon(transparent);
        Require(fallback.Width == transparent.Width && fallback.Height == transparent.Height &&
                fallback.Pixels.SequenceEqual(transparent.Pixels),
            "A fully transparent icon must retain a valid unchanged fallback.");
    }

    private static IconCacheService.IconSnapshot RectangleIcon(int size, int left, int top, int width, int height)
    {
        byte[] pixels = new byte[size * size * 4];
        pixels[3] = 15;
        for (int y = top; y < top + height; y++)
        for (int x = left; x < left + width; x++)
        {
            int offset = (y * size + x) * 4;
            pixels[offset] = 40;
            pixels[offset + 1] = 120;
            pixels[offset + 2] = 200;
            pixels[offset + 3] = 255;
        }
        return new(pixels, size, size);
    }

    private static async Task CheckSizeChangesAsync(string root)
    {
        var dock = new OrganizerDefinition
        {
            PlacementMode = OrganizerPlacementMode.Dock,
            DockIconSizeDip = 64,
            DockOrientation = DockOrientation.Horizontal
        };
        var changes = new DockSettingsChanges();
        Require(changes.Apply(dock, DockOrientation.Horizontal, 68), "Wheel sizing must update the live Dock.");
        var wheelSave = changes.Capture();
        Require(changes.Apply(dock, DockOrientation.Vertical, 80), "Slider sizing must share the live Dock state.");
        Require(changes.Rollback(wheelSave).Count == 0 && dock.DockIconSizeDip == 80 &&
                dock.DockOrientation == DockOrientation.Vertical,
            "A failed old wheel save must not overwrite a newer slider edit.");
        var sliderSave = changes.Capture();
        changes.Commit(sliderSave);
        changes.Commit(wheelSave);
        changes.Apply(dock, DockOrientation.Horizontal, 84);
        var failedSave = changes.Capture();
        Require(changes.Rollback(failedSave).Count == 1 && dock.DockIconSizeDip == 80 &&
                dock.DockOrientation == DockOrientation.Vertical,
            "A current failed save must restore the newest successful settings, ignoring an older commit.");

        var store = new StateStore(Path.Combine(root, "dock-refinements.json"));
        await store.SaveAsync(new AppStateV2 { Organizers = [dock] });
        OrganizerDefinition restored = (await store.LoadAsync()).Organizers.Single(item => item.Id == dock.Id);
        Require(restored.DockIconSizeDip == 80 && restored.DockOrientation == DockOrientation.Vertical,
            "The shared sizing state must round-trip through the existing persisted Dock fields.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, string message) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) < .001,
            $"{message}: expected {expected}, got {actual}.");
}
