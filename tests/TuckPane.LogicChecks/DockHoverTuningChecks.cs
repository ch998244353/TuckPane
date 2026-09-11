using System.Numerics;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

// Only this conversation's contracts. No windows or input automation.
internal static class DockHoverTuningChecks
{
    internal static async Task RunAsync(string area)
    {
        switch (area)
        {
            case "stability": Stability(); break;
            case "padding": Padding(); break;
            case "settings": await SettingsAsync(); break;
            default: throw new ArgumentException($"Unknown Dock hover tuning group: {area}.");
        }
        Console.WriteLine($"PASS --dock-hover-tuning {area}: focused production logic only; no GUI or input automation.");
    }

    private static HoverLayoutItem[] Items(DockGeometry dock) => Enumerable.Range(0, dock.Count).Select(index =>
    {
        Vector2 center = dock.ItemCenter(index);
        float size = (float)dock.IconSize;
        return new HoverLayoutItem(new(center.X - size / 2, center.Y - size / 2, size, size), 1);
    }).ToArray();

    private static HoverLayoutPose[] Layout(DockGeometry dock, Vector2 pointer, float scale)
    {
        HoverLayoutItem[] items = Items(dock);
        var poses = new HoverLayoutPose[items.Length];
        bool horizontal = dock.Orientation == DockOrientation.Horizontal;
        DockHoverLayout.CalculateInto(items, pointer, (float)dock.Pitch, scale, horizontal, poses);
        HoverLayoutMath.ConstrainDockPoses(poses, new(0, 0, (float)dock.Width, (float)dock.Height),
            (float)dock.CornerRadius, horizontal);
        return poses;
    }

    private static void Stability()
    {
        foreach (DockOrientation orientation in new[] { DockOrientation.Horizontal, DockOrientation.Vertical })
        {
            DockGeometry dock = DockLayoutMath.Calculate(15, 64, orientation);
            bool horizontal = orientation == DockOrientation.Horizontal;
            Vector2 axis = horizontal ? Vector2.UnitX : Vector2.UnitY;
            float Along(Vector2 value) => horizontal ? value.X : value.Y;
            var left = new List<float>();
            var right = new List<float>();
            // Same 129 samples/four middle pitches that exposed eight legacy reversals.
            for (int step = 0; step <= 128; step++)
            {
                Vector2 pointer = dock.ItemCenter(5) + axis * (step * (float)dock.Pitch / 32);
                HoverLayoutPose[] poses = Layout(dock, pointer, 1.25f);
                left.Add(Along(poses[0].Translation));
                right.Add(Along(poses[^1].Translation));
            }
            double leftRange = left.Max() - left.Min(), rightRange = right.Max() - right.Min();
            Console.WriteLine($"{orientation} middle sweep: left range={leftRange:F6} DIP, right range={rightRange:F6} DIP.");
            Require(leftRange <= .001 && rightRange <= .001,
                "A moving steady field must not periodically translate either far end across middle cells.");

            var motion = new DockHoverMotion();
            motion.Retarget(dock.ItemCenter(0), 1.25f);
            for (int frame = 0; frame < 240 && motion.IsActive; frame++) motion.Step(1d / 60);
            Require(!motion.IsActive, "The initial wave must settle before the one-way sweep.");
            float previous = Along(motion.Position);
            // Include both ends and actual production animation state.
            for (int frame = 0; frame <= 240; frame++)
            {
                motion.Retarget(Vector2.Lerp(dock.ItemCenter(0), dock.ItemCenter(14), frame / 240f), 1.25f);
                motion.Step(1d / 60);
                float position = Along(motion.Position);
                Require(position + .001f >= previous, "A unidirectional target must not reverse the shared moving field.");
                previous = position;
                CheckContainedAndSeparated(dock, Layout(dock, motion.Position, motion.Scale));
            }
            motion.Retarget(null, 1.25f);
            for (int frame = 0; frame < 240 && motion.IsActive; frame++) motion.Step(1d / 60);
            Require(!motion.IsActive && motion.Scale == 1, "Leaving must settle to no magnification or displacement.");
            HoverLayoutPose[] resting = Layout(dock, motion.Position, motion.Scale);
            Require(resting.Select(pose => pose.Bounds).SequenceEqual(Items(dock).Select(item => item.Bounds)),
                "The settled layout must exactly restore baseline rectangles.");
        }
        // A single icon and minimum spacing exercise the tightest fixed boundaries.
        foreach (DockOrientation orientation in new[] { DockOrientation.Horizontal, DockOrientation.Vertical })
        foreach (int count in new[] { 1, 3 })
        {
            DockGeometry dock = DockLayoutMath.Calculate(count, 64, orientation, .5);
            for (int step = 0; step <= 32; step++)
                CheckContainedAndSeparated(dock, Layout(dock,
                    Vector2.Lerp(dock.ItemCenter(0), dock.ItemCenter(count - 1), step / 32f), 1.25f));
        }
    }

    private static void CheckContainedAndSeparated(DockGeometry dock, HoverLayoutPose[] poses)
    {
        bool horizontal = dock.Orientation == DockOrientation.Horizontal;
        for (int i = 0; i < poses.Length; i++)
        {
            HoverRect bounds = poses[i].Bounds;
            Require(bounds.X >= -.001 && bounds.Y >= -.001 && bounds.Right <= dock.Width + .001 &&
                    bounds.Bottom <= dock.Height + .001, "The magnified content must stay within the fixed Dock surface.");
            Require(dock.Contains(new(bounds.X, bounds.Y)) && dock.Contains(new(bounds.Right, bounds.Y)) &&
                    dock.Contains(new(bounds.X, bounds.Bottom)) && dock.Contains(new(bounds.Right, bounds.Bottom)),
                $"Magnified icon corners must stay within the rounded Dock: {dock.Orientation}, count={dock.Count}, item={i}, bounds={bounds}.");
            if (i > 0)
                Require((horizontal ? bounds.X - poses[i - 1].Bounds.Right : bounds.Y - poses[i - 1].Bounds.Bottom) >= -.001,
                    "Adjacent magnified icons must not overlap.");
        }
    }

    private static void Padding()
    {
        foreach (double size in new[] { 64d, 96d })
        {
            DockGeometry dock = DockLayoutMath.Calculate(3, size, DockOrientation.Horizontal);
            Near(dock.Height, size * 1.5, "The horizontal Dock height must stay unchanged");
            Near(dock.TopInset + dock.BottomInset, size / 2, "Total vertical whitespace must stay unchanged");
            Near(dock.TopInset / dock.BottomInset, 3d / 5, "Resting top/bottom whitespace must be 3:5");
            Vector2 center = dock.ItemCenter(1);
            Near(center.Y - size / 2, dock.TopInset, "The rendered center must share the new top inset");
            Near(dock.Height - center.Y - size / 2, dock.BottomInset, "The rendered center must share the new bottom inset");
            Require(dock.ContainsIcon(new(center.X, (float)dock.TopInset + .01f), 1, 1) &&
                    !dock.ContainsIcon(new(center.X, (float)dock.TopInset - .01f), 1, 1),
                "Baseline hit testing must follow the upward icon shift.");
            for (int i = 0; i < dock.Count; i++) CheckContainedAndSeparated(dock, Layout(dock, dock.ItemCenter(i), 1.25f));
        }
    }

    private static async Task SettingsAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-hover-tuning-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        try
        {
            string path = Path.Combine(root, "state.json");
            await File.WriteAllTextAsync(path, """{"SchemaVersion":16,"GlobalSettings":{"ThemeTransparency":0.47},"Organizers":[]}""");
            AppStateV2 state = await new StateStore(path).LoadAsync();
            Near(state.GlobalSettings.CompactHoverMagnificationScale, 1.25, "Missing compact scale must use the previous maximum");
            Near(state.GlobalSettings.DockHoverMagnificationScale, 1.25, "Missing Dock scale must use the previous maximum");
            Require(state.SchemaVersion == 16, "Adding optional settings must not change the schema");
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity })
                Near(GlobalSettings.NormalizeHoverMagnificationScale(invalid), 1.25, "Non-finite scales must restore the default");
            Near(GlobalSettings.NormalizeHoverMagnificationScale(.5), 1, "Low values must clamp");
            Near(GlobalSettings.NormalizeHoverMagnificationScale(2), 1.25, "High values must clamp");
            state.GlobalSettings.CompactHoverMagnificationScale = 1.07;
            state.GlobalSettings.DockHoverMagnificationScale = 1.19;
            state.GlobalSettings.DockHoverMagnificationEnabled = false;
            await new StateStore(path).SaveAsync(state);
            state = await new StateStore(path).LoadAsync();
            Near(state.GlobalSettings.CompactHoverMagnificationScale, 1.07, "Compact scale must survive independent persistence");
            Near(state.GlobalSettings.DockHoverMagnificationScale, 1.19, "Disabled Dock must retain its scale on reload");
            Near(state.GlobalSettings.ThemeTransparency, .47, "Saving scales must preserve existing settings");
            Require(!state.GlobalSettings.DockHoverMagnificationEnabled, "Saving a retained scale must not enable the switch");
            foreach (float scale in new[] { 1f, 1.07f, 1.25f })
            {
                var wave = new OrganizerHoverWave();
                Vector2 center = new(48, 82);
                wave.SetAvailability(enabled: true, suspended: false);
                wave.MovePointer(center);
                Near(wave.GetTargetScale(center, new(80), compactList: true, maximumScale: scale), scale,
                    "Compact production targets must use the selected maximum");
                DockGeometry dock = DockLayoutMath.Calculate(3, 64, DockOrientation.Horizontal);
                HoverLayoutPose[] poses = Layout(dock, dock.ItemCenter(1), scale);
                Near(poses[1].Scale, scale, "Dock production targets must use the selected maximum");
                if (scale == 1)
                    Require(poses.Select(pose => pose.Bounds).SequenceEqual(Items(dock).Select(item => item.Bounds)),
                        "100% must remove both magnification and displacement");
            }
            await SaveFailureAsync();
        }
        finally
        {
            await AppLogger.FlushAsync();
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SaveFailureAsync()
    {
        var sequence = new SliderSaveSequence();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        double live = 1.1, committed = 1.25;
        int attempts = 0, reports = 0, rollbacks = 0;
        Task Write(Action capture)
        {
            capture();
            return ++attempts == 1 ? pending.Task : Task.CompletedTask;
        }
        void Commit(double value) => committed = value;
        void Rollback() { live = committed; rollbacks++; }
        void Report(Exception _) => reports++;
        sequence.Changed();
        Task<bool> saving = sequence.SaveAsync(() => live, Write, Commit, Rollback, Report);
        live = 1.19;
        sequence.Changed();
        pending.SetException(new IOException("Controlled obsolete hover scale save"));
        Require(await saving.WaitAsync(TimeSpan.FromSeconds(5)) && attempts == 2 && reports == 0 && rollbacks == 0,
            "An obsolete failed write must preserve and save the latest slider input");
        Near(committed, 1.19, "Only the newly saved scale may become the rollback value");
        live = 1.05;
        sequence.Changed();
        bool success = await sequence.SaveAsync(() => live, capture =>
        {
            capture();
            return Task.FromException(new IOException("Controlled current hover scale save"));
        }, Commit, Rollback, Report);
        Require(!success && reports == 1 && rollbacks == 1, "A current failure must restore the committed scale and report once");
        Near(live, 1.19, "Rollback must restore the last successfully saved scale");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, string message) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= .0001,
            $"{message}: expected {expected}, got {actual}.");
}
