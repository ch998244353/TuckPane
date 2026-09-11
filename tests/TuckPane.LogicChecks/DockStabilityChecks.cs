using System.Numerics;
using TuckPane.Core;

internal static class DockStabilityChecks
{
    internal static void Run(string area)
    {
        switch (area)
        {
            case "resize":
                Resize();
                break;
            case "hover":
                Hover();
                break;
            default:
                throw new ArgumentException($"Unknown Dock stability group: {area}.");
        }
        Console.WriteLine($"PASS --dock-stability {area}: production logic only; no WinUI frames, GUI or input automation.");
    }

    private static void Resize()
    {
        var commit = new DockLayoutCommit();
        HoverRect visible = new(0, 0, 32, 32);
        bool Ready(double expected, double host, double child) =>
            HoverLayoutMath.IsPresentationReady(expected, expected, host, host, child, child);

        long enlarged = commit.Request();
        Require(!commit.CanPresent(enlarged, Ready(128, 64, 64), visible),
            "A resize must not present the preceding layout's dimensions.");
        long shrunk = commit.Request();
        Require(!commit.Complete(enlarged) && commit.Pending,
            "A queued enlargement completion must not release the newer shrink layout.");
        Require(!commit.CanPresent(shrunk, Ready(32, 32, 32), visible),
            "Even matching dimensions must wait for the current layout commit.");
        Require(commit.Complete(shrunk), "The final shrink layout must complete.");
        Require(!commit.CanPresent(enlarged, Ready(32, 32, 32), visible),
            "Old-generation geometry must remain rejected after the latest layout completes.");
        Require(!commit.CanPresent(shrunk, Ready(32, 128, 128), visible) &&
                !commit.CanPresent(shrunk, Ready(32, 32, 128), visible),
            "A stale host or child must not replace the source after shrinking.");
        Require(!commit.CanPresent(shrunk, Ready(32, 32, 32), default),
            "A proxy with an empty visible clip must not replace the source.");
        Require(commit.CanPresent(shrunk, Ready(32, 32, 32), visible),
            "The final arranged shrink may present without another resize or pointer request.");

        long enlargedAgain = commit.Request();
        Require(!commit.CanPresent(shrunk, true, visible),
            "Starting a later resize must invalidate the previously presented layout.");
        Require(commit.Complete(enlargedAgain) &&
                !commit.CanPresent(enlargedAgain, Ready(96, 32, 32), visible) &&
                commit.CanPresent(enlargedAgain, Ready(96, 96, 96), visible),
            "The subsequent enlargement must reject stale small dimensions and accept its final layout.");
    }

    private static void Hover()
    {
        nint dock = 1, tooltip = 2, other = 3, awayFromPointer = 4;
        bool Over(nint[] windows, bool currentToolTipHit) => DockHoverOwnership.IsOverDock(
            windows[0], dock, currentToolTipHit,
            current =>
            {
                int index = Array.IndexOf(windows, current);
                return index >= 0 && index + 1 < windows.Length ? windows[index + 1] : 0;
            },
            window => window != awayFromPointer);

        var wave = new OrganizerHoverWave();
        Vector2 pointer = new(64, 64), pitch = new(80, 80);
        wave.SetAvailability(enabled: true, suspended: false);
        Require(wave.MovePointer(pointer), "The initial pointer must activate magnification.");
        // Repeated presence samples do not inject another pointer movement.
        for (int cycle = 0; cycle < 3; cycle++)
        {
            foreach (bool overDock in new[]
            {
                Over([dock], false),
                Over([tooltip, awayFromPointer, dock], true),
                Over([dock], false)
            })
                Require(wave.ObservePointerPresence(overDock, true) && wave.Pointer == pointer &&
                        wave.GetTargetScale(pointer, pitch, compactList: false) > 1,
                    "A stationary pointer must stay magnified as its Dock tooltip repeatedly appears and disappears.");
        }

        foreach (var exit in new[]
        {
            (Name: "external window above tooltip", OverDock: Over([other, tooltip, dock], false), Inside: true),
            (Name: "external window below tooltip", OverDock: Over([tooltip, other, dock], true), Inside: true),
            (Name: "unrecognized window above Dock", OverDock: Over([other, dock], false), Inside: true),
            (Name: "pointer outside activation region", OverDock: Over([dock], false), Inside: false)
        })
        {
            Require(wave.MovePointer(pointer), "Each exit scenario starts with a fresh real pointer state.");
            Require(!wave.ObservePointerPresence(exit.OverDock, exit.Inside) && !wave.HasPointer &&
                    wave.GetTargetScale(pointer, pitch, compactList: false) == 1,
                $"{exit.Name} must clear magnification.");
            Require(!wave.ObservePointerPresence(Over([dock], false), true),
                "A later presence poll must not restart magnification after a genuine exit.");
        }
        // Synthetic window handles exercise the production ownership policy;
        // native tooltip-instance identification and presented frames are not measured.
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
