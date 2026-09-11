using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class OrganizerAlignmentMenuNameChecks
{
    internal static async Task RunAsync(string? area)
    {
        if (area is not null and not "alignment" and not "name")
            throw new ArgumentException($"Unknown organizer alignment/menu/name check: {area}.");
        if (area is null or "alignment")
        {
            CheckAlignment();
            Console.WriteLine("PASS --organizer-alignment-menu-name alignment: opposite edges, fixed-edge/corner resize, centered scale/conflicts, hysteresis, constraints and 144 DPI. No UI or input automation.");
        }
        if (area is null or "name")
        {
            await CheckNameAsync();
            Console.WriteLine("PASS --organizer-alignment-menu-name name: missing-field compatibility, per-organizer save/reload, show again, copy and failed-save rollback. Menu/visual acceptance is static/manual.");
        }
    }

    private static void CheckAlignment()
    {
        NativeMethods.RECT work = Rect(0, 0, 2000, 1500);
        Guid peerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        WindowAlignmentTarget[] peers = [new(peerId, Rect(600, 400, 780, 560))];
        (CanvasResizeEdge Edge, NativeMethods.RECT Moving, int Coordinate)[] cases =
        [
            (CanvasResizeEdge.Left, Rect(787, 850, 907, 930), 780),
            (CanvasResizeEdge.Right, Rect(473, 850, 593, 930), 600),
            (CanvasResizeEdge.Top, Rect(250, 567, 370, 647), 560),
            (CanvasResizeEdge.Bottom, Rect(250, 313, 370, 393), 400)
        ];
        foreach (var (edge, moving, coordinate) in cases)
        {
            WindowAlignmentResult moved = WindowAlignmentMath.Align(moving, work, peers, 12, 20, default);
            Require(EdgeCoordinate(moved.Bounds, edge) == coordinate &&
                moved.Bounds.Width == moving.Width && moved.Bounds.Height == moving.Height,
                $"Moving {edge} must snap to the opposite peer edge without resizing.");
            WindowAlignmentResult resized = WindowAlignmentMath.AlignResize(
                moving, work, peers, edge, 60, 60, 12, 20, default);
            Require(EdgeCoordinate(resized.Bounds, edge) == coordinate &&
                (edge == CanvasResizeEdge.Left || resized.Bounds.Left == moving.Left) &&
                (edge == CanvasResizeEdge.Right || resized.Bounds.Right == moving.Right) &&
                (edge == CanvasResizeEdge.Top || resized.Bounds.Top == moving.Top) &&
                (edge == CanvasResizeEdge.Bottom || resized.Bounds.Bottom == moving.Bottom),
                $"Resizing {edge} must snap only the dragged edge and keep all other edges fixed.");
        }

        WindowAlignmentResult corner = WindowAlignmentMath.AlignResize(
            Rect(473, 313, 593, 393), work, peers,
            CanvasResizeEdge.Right | CanvasResizeEdge.Bottom, 60, 60, 12, 20, default);
        Require(Same(corner.Bounds, Rect(473, 313, 600, 400)) && corner.XGuide is not null && corner.YGuide is not null,
            "A fixed-edge corner must independently snap both dragged axes and preserve the opposite corner.");
        NativeMethods.RECT constrained = Rect(540, 850, 607, 930);
        WindowAlignmentResult rejected = WindowAlignmentMath.AlignResize(
            constrained, work, peers, CanvasResizeEdge.Right, 64, 60, 12, 20, default);
        Require(Same(rejected.Bounds, constrained) && rejected.State.X is null && rejected.XGuide is null,
            "A candidate below minimum width must be rejected without leaving a false guide or lock.");

        WindowAlignmentResult locked = WindowAlignmentMath.Align(cases[0].Moving, work, peers, 12, 20, default);
        WindowAlignmentResult retained = WindowAlignmentMath.Align(Rect(793, 850, 913, 930), work, peers, 12, 20, locked.State);
        WindowAlignmentResult released = WindowAlignmentMath.Align(Rect(801, 850, 921, 930), work, peers, 12, 20, retained.State);
        Require(retained.Bounds.Left == 780 && retained.State.X == locked.State.X &&
            released.Bounds.Left == 801 && released.State.X is null && released.XGuide is null,
            "An opposite-edge lock must retain between trigger/release distances, then release cleanly.");
        WindowAlignmentResult dpi144 = WindowAlignmentMath.Align(
            Rect(797, 850, 917, 930), work, peers,
            WindowAlignmentMath.DipToPx(WindowAlignmentMath.SnapDistanceDip, 144),
            WindowAlignmentMath.DipToPx(WindowAlignmentMath.ReleaseDistanceDip, 144), default);
        Require(dpi144.Bounds.Left == 780, "A 17 px gap must snap at 144 DPI using the existing DIP threshold.");

        // The inputs are visible panel bounds: the fixed title band stays in the UI adapter.
        (CanvasResizeEdge Edge, WindowAlignmentTarget Target, int Coordinate)[] scaleCases =
        [
            (CanvasResizeEdge.Right, new(peerId, Rect(1107, 500, 1287, 650)), 1107),
            (CanvasResizeEdge.Bottom, new(peerId, Rect(1300, 861, 1450, 1000)), 861)
        ];
        foreach (var (edge, target, coordinate) in scaleCases)
        {
            WindowAlignmentScaleResult scaled = WindowAlignmentMath.AlignScale(
                1000, 800, 201, 103, 1, .5, 2, work, [target], edge, 12, 20, default);
            Require(EdgeCoordinate(scaled.Alignment.Bounds, edge) == coordinate,
                $"Centered {edge} resize must reach the opposite target edge.");
            CheckCentered(scaled, 1000, 800, 201, 103);
        }

        WindowAlignmentTarget[] conflicting =
        [
            new(peerId, Rect(1107, 500, 1287, 650)),
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"), Rect(1300, 856, 1450, 1000))
        ];
        WindowAlignmentScaleResult conflict = WindowAlignmentMath.AlignScale(
            1000, 800, 200, 100, 1, .5, 2, work, conflicting,
            CanvasResizeEdge.Right | CanvasResizeEdge.Bottom, 12, 20, default);
        // At integer center 1000, both widths 213 and 214 reach right edge 1107;
        // width 213 is the smaller legal scale correction and preserves the rounding model.
        Require(Math.Abs(conflict.Scale - 1.065) < .00001 && conflict.Alignment.Bounds.Right == 1107 &&
            conflict.Alignment.XGuide is not null && conflict.Alignment.YGuide is null && conflict.Alignment.State.Y is null,
            "Conflicting corner targets must choose the smallest scale correction and omit the unaligned axis guide.");
        CheckCentered(conflict, 1000, 800, 200, 100);

        WindowAlignmentScaleResult scaleLimit = WindowAlignmentMath.AlignScale(
            1000, 800, 200, 100, 1, .5, 1.05, work, [conflicting[0]],
            CanvasResizeEdge.Right, 12, 20, default);
        Require(Math.Abs(scaleLimit.Scale - 1) < .00001 && scaleLimit.Alignment.State.X is null &&
            scaleLimit.Alignment.XGuide is null, "A target above maximum scale must not be clamped into a false snap.");
        CheckCentered(scaleLimit, 1000, 800, 200, 100);
    }

    private static void CheckCentered(WindowAlignmentScaleResult result, int centerX, int centerY, double baseWidth, double baseHeight)
    {
        NativeMethods.RECT bounds = result.Alignment.Bounds;
        Require(bounds.Left + bounds.Width / 2 == centerX && bounds.Top + bounds.Height / 2 == centerY &&
            bounds.Width == (int)Math.Round(baseWidth * result.Scale) &&
            bounds.Height == (int)Math.Round(baseHeight * result.Scale),
            "Scaled panel must keep the original integer center and derive both dimensions from one scale, including odd sizes.");
    }

    private static async Task CheckNameAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-alignment-menu-name-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            string legacyPath = Path.Combine(root, "legacy.json");
            await File.WriteAllTextAsync(legacyPath,
                $$"""{"SchemaVersion":{{new AppStateV2().SchemaVersion}},"Organizers":[{"Name":"legacy"}]}""");
            Require(!(await new StateStore(legacyPath).LoadAsync()).Organizers.Single().HideName,
                "Existing data without HideName must continue showing its name.");
            var hidden = new OrganizerDefinition { Name = "hidden" };
            var visible = new OrganizerDefinition { Name = "visible" };
            var state = new AppStateV2 { Organizers = [hidden, visible] };
            var store = new StateStore(Path.Combine(root, "state.json"));
            await OrganizerNameVisibility.SetAsync(hidden, true, () => store.SaveAsync(state));
            AppStateV2 loaded = await store.LoadAsync();
            OrganizerDefinition loadedHidden = loaded.Organizers.Single(item => item.Id == hidden.Id);
            Require(loadedHidden.HideName && loadedHidden.Name == "hidden" &&
                !loaded.Organizers.Single(item => item.Id == visible.Id).HideName,
                "Name hiding must persist only for the selected organizer without replacing its name.");
            Require(OrganizerInteractionMath.CopySettings(loadedHidden, "copy").HideName,
                "A copied organizer must inherit the name visibility setting.");
            await OrganizerNameVisibility.SetAsync(loadedHidden, false, () => store.SaveAsync(loaded));
            Require(!(await store.LoadAsync()).Organizers.Single(item => item.Id == hidden.Id).HideName,
                "Showing a hidden name again must persist through reload.");

            foreach (bool before in new[] { false, true })
            {
                var organizer = new OrganizerDefinition { HideName = before };
                int saves = 0;
                try
                {
                    await OrganizerNameVisibility.SetAsync(organizer, !before, () =>
                    {
                        saves++;
                        Require(organizer.HideName == !before, "Save must observe the requested visibility.");
                        return Task.FromException(new IOException("Expected isolated save failure."));
                    });
                    throw new InvalidOperationException("Name visibility save failure was not propagated.");
                }
                catch (IOException)
                {
                    Require(saves == 1 && organizer.HideName == before,
                        "A failed visibility save must restore the previous value and avoid duplicate writes.");
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            string fullRoot = Path.GetFullPath(root);
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(fullRoot), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).StartsWith("TuckPane-alignment-menu-name-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the isolated test directory.");
            Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static int EdgeCoordinate(NativeMethods.RECT bounds, CanvasResizeEdge edge) => edge switch
    {
        CanvasResizeEdge.Left => bounds.Left,
        CanvasResizeEdge.Right => bounds.Right,
        CanvasResizeEdge.Top => bounds.Top,
        CanvasResizeEdge.Bottom => bounds.Bottom,
        _ => throw new ArgumentException("Expected one edge.", nameof(edge))
    };

    private static bool Same(NativeMethods.RECT left, NativeMethods.RECT right) =>
        left.Left == right.Left && left.Top == right.Top && left.Right == right.Right && left.Bottom == right.Bottom;

    private static NativeMethods.RECT Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
