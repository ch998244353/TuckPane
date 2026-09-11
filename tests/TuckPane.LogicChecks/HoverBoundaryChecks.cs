using System.Numerics;
using System.Text.Json;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class HoverBoundaryChecks
{
    internal static async Task RunAsync(string area)
    {
        switch (area)
        {
            case "hover":
                ActivationAndClipping();
                AnchorRetargeting();
                break;
            case "dock":
                DockSpacingAndContainment();
                await DockPersistenceAsync();
                break;
            default:
                throw new ArgumentException($"Unknown hover-boundary group: {area}.");
        }
        Console.WriteLine($"PASS --hover-boundary-fixes {area}: focused production logic/state only; no GUI, input automation or presented-frame measurement.");
    }

    private static void ActivationAndClipping()
    {
        HoverRect viewport = new(0, 0, 200, 100), icon = new(8, 10, 32, 32), row = new(8, 10, 184, 32);
        HoverRect collapse = new(176, 0, 24, 24);
        Require(HoverLayoutMath.CanActivateCompact(row, viewport, collapse, icon.Center),
            "The baseline icon must activate compact hover.");
        foreach (Vector2 point in new[] { new Vector2(70, 26), new Vector2(160, 26) })
            Require(HoverLayoutMath.CanActivateCompact(row, viewport, collapse, point),
                "The filename and row whitespace must activate compact hover.");
        Require(!HoverLayoutMath.CanActivateCompact(new(176, 8, 24, 24), viewport, collapse, collapse.Center),
            "The collapse exclusion must win even when it overlaps an icon.");
        Require(!HoverLayoutMath.CanActivateCompact(new(8, -16, 32, 32), viewport, default, new(24, -1)),
            "An icon's clipped portion must not activate hover.");
        var wave = new OrganizerHoverWave();
        wave.SetAvailability(enabled: true, suspended: false);
        void Observe(Vector2 point)
        {
            if (HoverLayoutMath.CanActivateCompact(row, viewport, collapse, point)) wave.MovePointer(point);
            else wave.Leave();
        }
        var motion = new HoverWaveMotion();
        Observe(icon.Center);
        motion.Retarget(wave.GetTargetScale(icon.Center, new(32, 44), compactList: true));
        motion.Step(.1);
        float enlarged = motion.Scale;
        Require(wave.HasPointer && enlarged > 1, "An accepted icon point must produce real magnification.");
        Observe(new(70, 26));
        motion.Retarget(wave.GetTargetScale(icon.Center, new(32, 44), compactList: true));
        Require(wave.HasPointer && motion.Target > 1 && motion.Scale == enlarged,
            "Moving from the icon to its filename must retain the active wave and current animation pose.");

        foreach (var sample in new[] { (Content: new HoverRect(8, 0, 180, 36), Move: new Vector2(0, -8)),
                     (Content: new HoverRect(8, 64, 180, 36), Move: new Vector2(0, 8)) })
        {
            HoverRect visible = HoverLayoutMath.VisibleAfterTransform(sample.Content, 1.25f, sample.Move, true, viewport);
            Require(visible == visible.Intersect(viewport) && visible.Right == viewport.Right &&
                    (sample.Move.Y < 0 ? visible.Y == viewport.Y : visible.Bottom == viewport.Bottom),
                "The final enlarged row must be cropped at both the side and the relevant viewport edge.");
            HoverRect local = HoverLayoutMath.LocalClip(visible, sample.Content, 1.25f, sample.Move, true);
            HoverRect baseline = new(sample.Content.X + local.X, sample.Content.Y + local.Y, local.Width, local.Height);
            HoverRect roundTrip = HoverLayoutMath.Transform(baseline, sample.Content, 1.25f, sample.Move, true);
            Near(roundTrip.X, visible.X, "Local clipping must retain the final left edge");
            Near(roundTrip.Y, visible.Y, "Local clipping must retain the final top edge");
            Near(roundTrip.Width, visible.Width, "Local clipping must retain the final width");
            Near(roundTrip.Height, visible.Height, "Local clipping must retain the final height");
        }
        Require(HoverLayoutMath.VisibleAfterTransform(new(8, 150, 180, 36), 1.25f,
            Vector2.Zero, true, viewport).IsEmpty, "A row wholly outside the final viewport must be absent.");
    }

    private static void AnchorRetargeting()
    {
        var anchor = new HoverAnchorMotion();
        anchor.Retarget(new(20, 30), atRest: true);
        anchor.Retarget(new(180, 90));
        anchor.Step(1d / 60);
        Require(anchor.Position.X > 20 && anchor.Position.X < 180 && anchor.Velocity.X > 0,
            "An active anchor must move through intermediate positions.");
        Vector2 position = anchor.Position, velocity = anchor.Velocity;
        HoverLayoutItem[] rows = [new(new(8, 0, 180, 36), 1.15f), new(new(8, 44, 180, 36), 1.2f)];
        HoverLayoutPose[] before = HoverLayoutMath.Calculate(rows, position.Y, false, true);
        anchor.Retarget(new(20, 30));
        anchor.Retarget(new(120, 10));
        Require(anchor.Position == position && anchor.Velocity == velocity &&
                before.SequenceEqual(HoverLayoutMath.Calculate(rows, anchor.Position.Y, false, true)),
            "Leave/re-entry retargets must preserve anchor velocity and the still-enlarged rows' current pose.");
        anchor.Step(.0001);
        Require(Vector2.Distance(anchor.Position, position) < .5f,
            "The first short step after reversal must not teleport the row anchor.");
        for (int step = 0; step < 240 && anchor.IsActive; step++) anchor.Step(1d / 60);
        Require(!anchor.IsActive && anchor.Position == new Vector2(120, 10),
            "The reversed anchor must settle so rendering can stop.");
        anchor.Reset();
        anchor.Retarget(new(40, 70), atRest: true);
        Require(anchor.Position == new Vector2(40, 70) && anchor.Velocity == Vector2.Zero,
            "A fresh resting layout must not inherit the previous anchor or velocity.");
    }

    private static void DockSpacingAndContainment()
    {
        foreach (DockOrientation direction in new[] { DockOrientation.Horizontal, DockOrientation.Vertical })
        foreach (double factor in new[] { .5, .75, 2 })
        {
            bool horizontal = direction == DockOrientation.Horizontal;
            DockGeometry dock = DockLayoutMath.Calculate(5, 64, direction, factor);
            double pitch = 64 + 16 * factor / .75;
            Near(dock.Pitch, pitch, "Spacing must scale the default 16 DIP gap without resizing icons");
            Near(horizontal ? dock.Width : dock.Height, 64 + 4 * pitch + (horizontal ? 1 : 2) * (.5 * pitch + 8 + 2),
                "Dock length must include all gaps and enough fixed end padding for hover");
            Near(horizontal ? dock.Height : dock.Width, horizontal ? 96 : 80, "Spacing must retain orientation-specific Dock thickness");
            Near(dock.IconSize, 64, "Spacing must retain icon size");
            HoverRect panel = new(0, 0, (float)dock.Width, (float)dock.Height);
            foreach (float[] scales in new[] { new[] { 1.25f, 1f, 1.25f, 1f, 1.25f }, Enumerable.Repeat(1.25f, 5).ToArray() })
            foreach (float pointer in new[] { -1000f, 10000f })
            {
                HoverLayoutItem[] items = Enumerable.Range(0, 5).Select(i =>
                    new HoverLayoutItem(new(dock.ItemCenter(i).X - 32, dock.ItemCenter(i).Y - 32, 64, 64), scales[i])).ToArray();
                HoverLayoutPose[] poses = HoverLayoutMath.Calculate(items, pointer, horizontal, false, constrainDock: true);
                HoverLayoutMath.ConstrainDockPoses(poses, panel, (float)dock.CornerRadius, horizontal);
                Require(poses.All(p => new[] { new Vector2(p.Bounds.X, p.Bounds.Y),
                        new Vector2(p.Bounds.Right, p.Bounds.Y), new Vector2(p.Bounds.X, p.Bounds.Bottom),
                        new Vector2(p.Bounds.Right, p.Bounds.Bottom) }.All(corner =>
                        HoverLayoutMath.ContainsRounded(panel, (float)dock.CornerRadius, corner))),
                    "All four corners of residual peaks at either anchor extreme must stay inside the rounded Dock surface.");
                Require(poses.Sum(p => p.Scale - 1) <= .50001,
                    "Residual peaks must share the fixed Dock expansion budget.");
            }
            Vector2 center = new(-713.2f, 429.2f);
            var bounds = DockLayoutMath.CalculateBounds(dock, center, 1.25);
            Near((bounds.Left + bounds.Right) / 2d, center.X * 1.25, "Spacing must retain the saved horizontal centre", .501);
            Near((bounds.Top + bounds.Bottom) / 2d, center.Y * 1.25, "Spacing must retain the saved vertical centre", .501);
            foreach (int count in new[] { 0, 1 })
            {
                DockGeometry single = DockLayoutMath.Calculate(count, 64, direction, factor);
                Near(horizontal ? single.Width : single.Height, horizontal ? 88 : 112, "Zero/one-item Dock must halve only physical left/right padding");
            }
        }
        Near(DockLayoutMath.NormalizeSpacing(.1), .5, "Persisted spacing must respect the lower limit");
        Near(DockLayoutMath.NormalizeSpacing(3), 2, "Persisted spacing must respect the upper limit");
        Near(DockLayoutMath.NormalizeSpacing(double.NaN), .75, "Non-finite spacing must recover to the default");
    }

    private static async Task DockPersistenceAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-hover-boundary-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            var definition = new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Dock, DockIconSizeDip = 64 };
            var legacy = JsonSerializer.SerializeToNode(new AppStateV2 { Organizers = [definition] })!;
            legacy["Organizers"]![0]!.AsObject().Remove(nameof(OrganizerDefinition.DockSpacingFactor));
            string path = Path.Combine(root, "state.json");
            await File.WriteAllTextAsync(path, legacy.ToJsonString());
            var store = new StateStore(path);
            definition = (await store.LoadAsync()).Organizers.Single(item => item.Id == definition.Id);
            Near(definition.DockSpacingFactor, .75, "Existing saved Dock without a spacing field must retain the default gap");
            var changes = new DockSettingsChanges();
            Require(changes.Apply(definition, DockOrientation.Horizontal, 64, .5), "Changing only spacing must update the live definition");
            var oldSave = changes.Capture();
            changes.Apply(definition, DockOrientation.Horizontal, 64, 2);
            var latest = changes.Capture();
            Require(latest.Single().Spacing == 2 && changes.Rollback(oldSave).Count == 0 && definition.DockSpacingFactor == 2,
                "An old failed save must not overwrite the latest spacing edit");
            changes.Commit(latest);
            changes.Commit(oldSave);
            changes.Apply(definition, DockOrientation.Vertical, 80);
            Near(definition.DockSpacingFactor, 2, "The existing size/orientation overload must preserve spacing");
            var failed = changes.Capture();
            Require(changes.Rollback(failed).Count == 1 && definition.DockSpacingFactor == 2 &&
                    definition.DockIconSizeDip == 64 && definition.DockOrientation == DockOrientation.Horizontal,
                "A current save failure must restore the newest successfully saved Dock settings");
            await store.SaveAsync(new AppStateV2 { Organizers = [definition] });
            Near((await store.LoadAsync()).Organizers.Single(item => item.Id == definition.Id).DockSpacingFactor, 2,
                "The selected spacing must survive a real state-store save/load");
        }
        finally
        {
            await AppLogger.FlushAsync();
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            string fullRoot = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(fullRoot), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).StartsWith("TuckPane-hover-boundary-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the isolated hover-boundary test directory.");
            Directory.Delete(fullRoot, recursive: true);
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
