using System.Numerics;
using TuckPane.Core;
using TuckPane.Models;

// Only this change's production logic and necessary WinUI source wiring.
// No window creation, input injection, stored-data changes or real file opening.
internal static class CompactDockFixChecks
{
    internal static void Run(string area)
    {
        switch (area)
        {
            case "compact": CompactActivation(); CompactHitTargets(); break;
            case "dock": DockGeometry(); break;
            default: throw new ArgumentException($"Unknown compact/dock group: {area}.");
        }
        Console.WriteLine($"PASS --compact-dock-fixes {area}: focused production logic/source checks; UI experience awaits user verification.");
    }

    private static void CompactActivation()
    {
        HoverRect viewport = new(0, 0, 200, 100), activation = new(8, 8, 184, 80);
        HoverRect firstRow = new(8, 8, 184, 36), secondRow = new(8, 52, 184, 36);
        HoverRect collapse = new(176, 0, 24, 24);
        foreach (Vector2 point in new[] { new Vector2(24, 26), new Vector2(70, 26),
                     new Vector2(160, 26), new Vector2(70, 48), new Vector2(70, 70) })
            Require(HoverLayoutMath.CanActivateCompact(activation, viewport, collapse, point),
                "The icon, name, row whitespace, gap and next row must form a continuous activation region.");
        foreach (Vector2 point in new[] { collapse.Center, new Vector2(7, 26), new Vector2(70, 7),
                     new Vector2(70, 89), new Vector2(70, -1) })
            Require(!HoverLayoutMath.CanActivateCompact(activation, viewport, collapse, point),
                "The collapse button, outside row margins and clipped region must remain excluded.");
        Require(!HoverLayoutMath.CanActivateCompact(new(8, -16, 184, 36), viewport, default, new(70, -1)),
            "A partially clipped row must not activate outside the viewport.");
        Vector2 gap = new(70, 48);
        Require(!firstRow.Contains(gap) && !secondRow.Contains(gap),
            "The continuous activation gap must not become a content click target.");

        string source = ReadSource("MainWindow.HoverPresentation.cs") + ReadSource("MainWindow.HoverWave.cs");
        Require(System.Text.RegularExpressions.Regex.IsMatch(source, @"CanActivateCompact\(\s*_compactHoverBounds") &&
                source.Contains("RowBounds", StringComparison.Ordinal),
            "Compact production activation must use aggregated stable row bounds instead of only icon bounds.");
    }

    private static void CompactHitTargets()
    {
        HoverRect viewport = new(0, 0, 200, 110), excluded = new(176, 0, 24, 24);
        HoverRect first = new(8, 8, 184, 36), second = new(8, 60, 184, 36);
        HoverHitRegion[] items =
        [
            new(first, new(8, 8, 180, 36), default, Vector2.Zero, 1.25f, true),
            new(second, new(8, 60, 180, 36), default, Vector2.Zero, 1, false)
        ];
        int Hit(Vector2 point) => HoverLayoutMath.HitTestItems(items, true, viewport, excluded, point);
        Require(Hit(new(24, 26)) == 0 && Hit(new(70, 26)) == 0 && Hit(new(198, 26)) == 0,
            "The presented icon, filename and magnified row must resolve the same opening target.");
        Require(Hit(new(70, 78)) == 1,
            "A still-visible source row must stay clickable while a different row has a ready proxy.");
        Require(Hit(new(70, 55)) == -1 && Hit(excluded.Center) == -1 && Hit(new(205, 26)) == -1,
            "Row gaps, the collapse button and clipped portions must not resolve opening targets.");
        items[0] = items[0] with { Presented = false };
        Require(Hit(new(70, 26)) == 0 && Hit(new(198, 26)) == -1,
            "A not-yet-ready proxy must use the original row's real bounds, without stale enlarged hit areas.");
        // This verifies the shared production target resolver only. WinUI gesture
        // capture, cancellation and exactly-once DoubleTapped routing need source
        // review and user hand testing; this harness never synthesizes gestures.
    }

    private static void DockGeometry()
    {
        foreach (DockOrientation orientation in new[] { DockOrientation.Horizontal, DockOrientation.Vertical })
        {
            bool horizontal = orientation == DockOrientation.Horizontal;
            DockGeometry baseline = DockLayoutMath.Calculate(3, 64, orientation);
            Near(baseline.Width, horizontal ? 274 : 80, "Default physical left/right padding must be halved");
            Near(baseline.Height, horizontal ? 96 : 324, "Physical top/bottom padding must stay unchanged");
            Near(baseline.Pitch, 80, "Inter-item spacing must stay unchanged");

            foreach (var setting in new[] { (Size: 32d, Spacing: .5), (Size: 128d, Spacing: 2d), (Size: 64d, Spacing: .75) })
            foreach (int count in new[] { 1, 3 })
            {
                DockGeometry dock = DockLayoutMath.Calculate(count, setting.Size, orientation, setting.Spacing);
                double pitch = setting.Size + setting.Size / 3 * setting.Spacing;
                double oldEnd = count > 1 ? .5 * pitch + setting.Size * .125 + 2 : setting.Size * .375;
                Near(dock.EndInset, oldEnd * (horizontal ? .5 : 1), "Only horizontal end padding is halved");
                Near(dock.CrossInset, setting.Size / 4 * (horizontal ? 1 : .5), "Only vertical side padding is halved");
                Near(dock.Pitch, pitch, "Resizing and spacing changes must retain the intended pitch");
                HoverLayoutItem[] items = Enumerable.Range(0, count).Select(i =>
                    new HoverLayoutItem(new(dock.ItemCenter(i).X - (float)setting.Size / 2,
                        dock.ItemCenter(i).Y - (float)setting.Size / 2, (float)setting.Size, (float)setting.Size), 1.25f)).ToArray();
                HoverRect surface = new(0, 0, (float)dock.Width, (float)dock.Height);
                foreach (float anchor in new[] { -1000f, 10000f })
                {
                    HoverLayoutPose[] poses = HoverLayoutMath.Calculate(items, anchor, horizontal, false, constrainDock: true);
                    HoverLayoutPose[] before = (HoverLayoutPose[])poses.Clone();
                    HoverLayoutMath.ConstrainDockPoses(poses, surface, (float)dock.CornerRadius, horizontal);
                    Vector2 commonShift = poses[0].Bounds.Center - before[0].Bounds.Center;
                    for (int i = 0; i < count; i++)
                        Require(poses[i].Scale == before[i].Scale &&
                                Vector2.Distance(poses[i].Bounds.Center - before[i].Bounds.Center, commonShift) < .001,
                            "Boundary correction must preserve scales and spacing by translating every item equally.");
                    Require(poses.Length == count && poses.All(p => !p.Bounds.IsEmpty && p.Scale > 1),
                        "Every first/middle/last item must retain visible magnified geometry after setting changes.");
                    string[] outside = poses.SelectMany((pose, index) => Corners(pose.Bounds)
                        .Where(corner => !HoverLayoutMath.ContainsRounded(surface, (float)dock.CornerRadius, corner))
                        .Select(corner => $"item={index}, corner=({corner.X:R},{corner.Y:R}), bounds={pose.Bounds}, scale={pose.Scale:R}, translation={pose.Translation}"))
                        .ToArray();
                    Require(outside.Length == 0,
                        $"Magnified icons must fit inside the reduced rounded surface: orientation={orientation}, count={count}, size={setting.Size}, spacing={setting.Spacing}, anchor={anchor}, surface={surface}, radius={dock.CornerRadius}; outside: {string.Join("; ", outside)}");
                }
            }
        }
        PresentationReadiness();
    }

    private static void PresentationReadiness()
    {
        Require(HoverLayoutMath.IsPresentationReady(64, 64, 64, 64, 64, 64),
            "A fully arranged proxy and child may replace the source.");
        foreach (double previousSize in new[] { 32d, 128d })
        {
            Require(!HoverLayoutMath.IsPresentationReady(64, 64, previousSize, previousSize, previousSize, previousSize),
                "A stale nonzero proxy must not hide the source after either enlargement or shrinkage.");
            Require(!HoverLayoutMath.IsPresentationReady(64, 64, 64, 64, previousSize, previousSize),
                "An arranged host with a stale child must keep the source visible.");
        }
        Require(!HoverLayoutMath.IsPresentationReady(64, 64, 64, 64, 0, 0) &&
                !HoverLayoutMath.IsPresentationReady(double.NaN, 64, 64, 64, 64, 64) &&
                HoverLayoutMath.IsPresentationReady(64, 64, 64.4, 63.6, 64.4, 63.6) &&
                !HoverLayoutMath.IsPresentationReady(64, 64, 64.6, 64, 64, 64),
            "Presentation readiness must reject unarranged/invalid dimensions and allow only the rounding tolerance.");
    }

    private static Vector2[] Corners(HoverRect rect) =>
        [new(rect.X, rect.Y), new(rect.Right, rect.Y), new(rect.X, rect.Bottom), new(rect.Right, rect.Bottom)];

    private static string ReadSource(string relative)
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory != null)
        {
            string path = Path.Combine(directory.FullName, "src", "TuckPane", relative);
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Cannot locate production source {relative}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, string message) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) < .001,
            $"{message}: expected {expected}, got {actual}.");
}
