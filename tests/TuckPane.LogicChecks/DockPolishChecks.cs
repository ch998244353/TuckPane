using System.Numerics;
using TuckPane.Core;

internal static class DockPolishChecks
{
    internal static void Run(string area)
    {
        switch (area)
        {
            case "tooltip": Tooltip(); break;
            case "running": Running(); break;
            default: throw new ArgumentException($"Unknown --dock-polish area: {area}");
        }
        Console.WriteLine($"PASS --dock-polish {area}");
    }

    private static void Tooltip()
    {
        var work = new HoverRect(0, 0, 800, 600);
        var body = new Vector2(120, 28);
        foreach (float scale in new[] { 1f, 1.25f, 1.5f })
        {
            // Synthetic rendered bounds include scale and lateral/bounce displacement.
            var icon = new HoverRect(300 + 13 * scale, 210 - 17 * scale, 64 * scale, 64 * scale);
            var horizontal = DockVisualMath.PlaceTip(icon, body, work, horizontal: true);
            Require(horizontal.Side == DockTipSide.Top, "Horizontal tip prefers above the icon");
            Equal(horizontal.Bounds.X + horizontal.Bounds.Width / 2, icon.X + icon.Width / 2, "Horizontal body centers on the displayed icon");
            Equal(horizontal.Bounds.Bottom, icon.Y - 24, "Horizontal body stays 24 DIP above the displayed icon");
            Inside(horizontal.Bounds, work);

            var vertical = DockVisualMath.PlaceTip(icon, body, work, horizontal: false);
            Require(vertical.Side == DockTipSide.Right, "Vertical tip prefers the right side");
            Equal(vertical.Bounds.Y + vertical.Bounds.Height / 2, icon.Y + icon.Height / 2 - 12, "Vertical body shifts 12 DIP above the displayed icon center");
            Equal(vertical.Bounds.X, icon.Right + 12, "Vertical body keeps a 12 DIP side gap");
            Inside(vertical.Bounds, work);
        }
        var nearTop = DockVisualMath.PlaceTip(new HoverRect(300, 2, 64, 64), body, work, true);
        Require(nearTop.Side == DockTipSide.Bottom, "Horizontal tip flips when above is unavailable");
        Equal(nearTop.Bounds.Y, 78, "Flipped horizontal body stays 12 DIP below the icon");
        Inside(nearTop.Bounds, work);
        var nearRight = DockVisualMath.PlaceTip(new HoverRect(730, 200, 64, 64), body, work, false);
        Require(nearRight.Side == DockTipSide.Left, "Vertical tip flips when the right side is unavailable");
        Equal(nearRight.Bounds.Right, 718, "Flipped vertical body keeps its 12 DIP side gap");
        Inside(nearRight.Bounds, work);
        var shifted = DockVisualMath.PlaceTip(new HoverRect(2, 200, 32, 32), body, work, true);
        Inside(shifted.Bounds, work);
        Equal(shifted.Bounds.X, 8, "Body near the left edge keeps the work-area safety margin");
        var nearBottom = DockVisualMath.PlaceTip(new HoverRect(300, 580, 32, 32), body, work, false);
        Equal(nearBottom.Bounds.Bottom, 592, "Vertical body is clamped above the bottom safety margin");
        Inside(nearBottom.Bounds, work);
        var fullWidth = DockVisualMath.PlaceTip(new HoverRect(730, 200, 64, 64), new Vector2(784, 48), work, true);
        Equal(fullWidth.Bounds.X, 8, "A work-area-width name remains fully inside both horizontal margins");
        Inside(fullWidth.Bounds, work);
        var secondMonitor = new HoverRect(-1200, 100, 1200, 800);
        var offsetTip = DockVisualMath.PlaceTip(new HoverRect(-650, 400, 80, 80), body, secondMonitor, true);
        Inside(offsetTip.Bounds, secondMonitor);
        Equal(offsetTip.Bounds.X + offsetTip.Bounds.Width / 2, -610, "Nonzero monitor origin preserves the screen-space anchor");

        var desiredSize = new Vector2(120.21f, 28.41f);
        foreach (double scale in new[] { 1d, 1.25d, 1.5d })
        {
            var pixels = DockVisualMath.TipClientPixels(desiredSize, scale);
            CoversPixels(pixels.Width, desiredSize.X * scale, $"Tooltip width at {scale:P0} DPI");
            CoversPixels(pixels.Height, desiredSize.Y * scale, $"Tooltip height at {scale:P0} DPI");
        }
    }

    private static void Running()
    {
        foreach (string? arguments in new string?[] { null, "", "  " })
            Require(DockRunningState.CanMatchExecutableShortcut(arguments), "Plain application shortcuts allow executable matching");
        foreach (string arguments in new[] { @"""C:\Docs\Report.docx""", "https://example.com", "-applaunch 123", @"/e,""C:\Work\Project""" })
            Require(!DockRunningState.CanMatchExecutableShortcut(arguments), "Content-specific shortcut arguments must not match only the host process");

        var executable = new DockItemIdentity(DockIdentityKind.Executable, @"C:\Apps\Editor\Editor.exe");
        var normal = new DockOpenWindow(@"c:\apps\editor\EDITOR.EXE", null, Visible: true);
        // A minimized top-level window remains Visible in the collector's snapshot contract.
        var minimized = new DockOpenWindow(executable.Value, null, Visible: true);
        Require(DockRunningState.IsOpen(executable, [normal, minimized], []), "Two matching windows produce an open state");
        Require(DockRunningState.IsOpen(executable, [minimized], []), "Remaining minimized window keeps the dot on");
        Require(!DockRunningState.IsOpen(executable, [], []), "Closing the last window extinguishes the dot");
        Require(!DockRunningState.IsOpen(executable,
            [new(@"D:\Other\Editor.exe", null, true), normal with { Visible = false }, normal with { Cloaked = true }], []),
            "Same filename elsewhere, hidden and cloaked windows cannot light the dot");

        var app = new DockItemIdentity(DockIdentityKind.AppId, "Vendor.App_abc!Main");
        Require(DockRunningState.IsOpen(app, [new(null, "vendor.app_ABC!main", true)], []), "Confirmed AppId matches case-insensitively");
        Require(!DockRunningState.IsOpen(app, [new(null, "Vendor.Other_abc!Main", true)], []), "Another AppId cannot match");
        var folder = new DockItemIdentity(DockIdentityKind.Folder, @"C:\Work\Project\");
        Require(DockRunningState.IsOpen(folder, [], [@"c:\work\PROJECT"]), "Folder identity tolerates case and trailing separators");
        Require(!DockRunningState.IsOpen(folder, [], [@"D:\Work\Project"]), "Same folder name on another path cannot match");
        Require(!DockRunningState.IsOpen(new(DockIdentityKind.Unknown, executable.Value), [normal], []), "Unconfirmed identity never lights a dot");
        Require(!DockRunningState.IsOpen(new(DockIdentityKind.Executable, ""), [normal], []), "Failed identity resolution stays off");

        var version = new DockSnapshotVersion();
        long pending = version.Current;
        Require(version.Accepts(pending), "Current subscription accepts its snapshot");
        long updated = version.Invalidate();
        Require(!version.Accepts(pending) && version.Accepts(updated), "An item change rejects the pending old snapshot");
        version.Invalidate();
        Require(!version.Accepts(updated), "Another subscription change cannot revive a stale snapshot");
    }

    private static void Inside(HoverRect bounds, HoverRect work)
    {
        Require(bounds.X >= work.X + 8 - .001f && bounds.Y >= work.Y + 8 - .001f &&
            bounds.Right <= work.Right - 8 + .001f && bounds.Bottom <= work.Bottom - 8 + .001f,
            "The complete tooltip body stays inside the work area with an 8 DIP margin");
    }

    private static void CoversPixels(int actual, double desired, string message)
    {
        Require(actual >= desired && actual - desired < 1,
            $"{message}: desired {desired}, actual {actual}; allocation must round up by less than one pixel.");
    }

    private static void Equal(float actual, float expected, string message)
    {
        Require(float.IsFinite(actual) && Math.Abs(actual - expected) <= .001f,
            $"{message}: expected {expected}, actual {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
