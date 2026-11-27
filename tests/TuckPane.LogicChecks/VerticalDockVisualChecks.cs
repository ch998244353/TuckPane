using System.Numerics;
using TuckPane.Core;
using TuckPane.Models;

internal static class VerticalDockVisualChecks
{
    internal static void Run()
    {
        CheckGeometry();
        CheckRunningDot();
        CheckHoverBounds();
        Console.WriteLine("PASS --vertical-dock-visuals: 3:2 padding at 32/64/128 DIP; running-dot placement after resize or reorientation; three-item hover boundaries. No GUI or input automation.");
    }

    internal static void CheckGeometry()
    {
        foreach (double size in new[] { 32d, 64d, 128d })
        {
            DockGeometry dock = DockLayoutMath.Calculate(3, size, DockOrientation.Vertical);
            Near(dock.Width, size * 1.5, "Dock width");
            Near(dock.LeftInset + dock.RightInset, size / 2, "Total padding");
            Near(dock.LeftInset / dock.RightInset, 1.5, "Left/right ratio");
            for (int i = 0; i < dock.Count; i++)
            {
                Vector2 center = dock.ItemCenter(i);
                Near(center.X - size / 2, dock.LeftInset, "Icon left edge");
                Near(dock.Width - center.X - size / 2, dock.RightInset, "Icon right edge");
                Require(dock.ContainsIcon(new((float)dock.LeftInset + .01f, center.Y), i, 1) &&
                    !dock.ContainsIcon(new((float)dock.LeftInset - .01f, center.Y), i, 1),
                    "Baseline hit testing must follow the shifted icon.");
                if (i > 0) Near(center.Y - dock.ItemCenter(i - 1).Y, dock.Pitch, "Vertical pitch");
            }
        }
    }

    private static void CheckRunningDot()
    {
        foreach (double size in new[] { 32d, 128d })
        {
            // The same running item can remain open across a resize and rotation.
            DockGeometry horizontal = DockLayoutMath.Calculate(3, size, DockOrientation.Horizontal);
            Vector2 horizontalIcon = horizontal.ItemCenter(1);
            Vector2 horizontalDot = DockVisualMath.RunningDotCenter(horizontal, (float)horizontal.Height, horizontalIcon);
            Near(horizontalDot.X, horizontalIcon.X, "Horizontal dot follows the arranged icon");
            Near(horizontalDot.Y, horizontal.Height - Math.Max(5, horizontal.BottomInset / 2), "Horizontal dot stays in the bottom gutter");

            DockGeometry vertical = DockLayoutMath.Calculate(3, size, DockOrientation.Vertical);
            foreach (float shift in new[] { 0f, -13f, 21f })
            {
                Vector2 verticalIcon = vertical.ItemCenter(1) + new Vector2(0, shift);
                Vector2 verticalDot = DockVisualMath.RunningDotCenter(vertical, (float)vertical.Height, verticalIcon);
                Near(verticalDot.X, vertical.LeftInset / 2, "Vertical dot stays in the left gutter");
                Near(verticalDot.Y, verticalIcon.Y, "Vertical dot follows the arranged icon and layout displacement");
            }
        }
    }

    private static void CheckHoverBounds()
    {
        DockGeometry dock = DockLayoutMath.Calculate(3, 64, DockOrientation.Vertical);
        var surface = new HoverRect(0, 0, (float)dock.Width, (float)dock.Height);
        HoverLayoutItem[] items = Enumerable.Range(0, dock.Count).Select(i =>
        {
            Vector2 center = dock.ItemCenter(i);
            return new HoverLayoutItem(new(center.X - 32, center.Y - 32, 64, 64), 1);
        }).ToArray();
        var poses = new HoverLayoutPose[items.Length];
        for (int hovered = 0; hovered < items.Length; hovered++)
        {
            DockHoverLayout.CalculateInto(items, dock.ItemCenter(hovered), (float)dock.Pitch, 1.25f, false, poses);
            HoverLayoutMath.ConstrainDockPoses(poses, surface, (float)dock.CornerRadius, false);
            for (int i = 0; i < poses.Length; i++)
            {
                HoverRect bounds = poses[i].Bounds;
                Require(dock.Contains(new(bounds.X, bounds.Y)) && dock.Contains(new(bounds.Right, bounds.Y)) &&
                    dock.Contains(new(bounds.X, bounds.Bottom)) && dock.Contains(new(bounds.Right, bounds.Bottom)),
                    $"Hovered icon {i} must stay within the rounded Dock.");
                Vector2 dot = DockVisualMath.RunningDotCenter(dock, (float)dock.Height,
                    items[i].Bounds.Center + poses[i].Translation);
                Near(dot.X, dock.LeftInset / 2, "Hover keeps the dot column fixed");
                Near(dot.Y, bounds.Center.Y, "Dot follows the displayed icon center");
                Require(dock.Contains(dot - new Vector2(2)) && dock.Contains(dot + new Vector2(2)),
                    "The 4 DIP running dot must stay within the Dock.");
            }
        }
    }

    private static void Near(double actual, double expected, string message) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= .0001,
            $"{message}: expected {expected}, got {actual}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
