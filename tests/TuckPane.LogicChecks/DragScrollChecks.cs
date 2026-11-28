using TuckPane.Core;

internal static class DragScrollChecks
{
    internal static void Run()
    {
        foreach (bool list in new[] { false, true })
        {
            OrganizerWheelAction Route(bool ctrl, bool shell) =>
                OrganizerInteractionMath.ResolveWheelAction(true, false, false, true, shell, ctrl, list, true);
            if (Route(false, false) != OrganizerWheelAction.ScrollReorder ||
                Route(true, false) != OrganizerWheelAction.Ignore || Route(false, true) != OrganizerWheelAction.Ignore)
                throw new InvalidOperationException("Internal drag wheel routing failed.");
        }

        double top = OrganizerScrollMath.DragEdgeDelta(4, 80, .016);
        double bottom = OrganizerScrollMath.DragEdgeDelta(76, 80, .016);
        if (top >= 0 || bottom != -top || OrganizerScrollMath.DragEdgeDelta(40, 80, .016) != 0)
            throw new InvalidOperationException("Drag edges overlap or scroll in the wrong direction.");

        var state = new SmoothScrollState(24, 24, 0, 0);
        state = OrganizerScrollMath.ConsumeWheelDelta(state, -60, 32, 60);
        state = OrganizerScrollMath.ConsumeWheelDelta(state, -60, 32, 60);
        if (state.TargetOffset != 56 || OrganizerScrollMath.QueueTarget(state, bottom, 60).TargetOffset != 60 ||
            OrganizerScrollMath.QueueTarget(SmoothScrollState.Empty, top, 60).TargetOffset != 0)
            throw new InvalidOperationException("Wheel and edge scrolling failed to share a bounded target.");
        Console.WriteLine("PASS --drag-scroll");
    }
}
