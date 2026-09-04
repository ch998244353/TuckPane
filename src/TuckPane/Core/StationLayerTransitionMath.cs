namespace TuckPane.Core;

/// <summary>
/// Pure command contract for moving a Station between the Explorer desktop
/// owner and its expanded topmost layer. The actual HWND calls remain in
/// DesktopLayerService; this seam keeps the safety ordering deterministic.
/// </summary>
internal enum StationLayerTransitionStep
{
    DetachDesktopOwner,
    SetTopmostNoActivate,
    MoveAndShow,
    Hide,
    ClearTopmost,
    AttachDesktopOwner
}

internal static class StationLayerTransitionMath
{
    internal static IReadOnlyList<StationLayerTransitionStep> ExpandPlan { get; } =
    [
        StationLayerTransitionStep.DetachDesktopOwner,
        StationLayerTransitionStep.SetTopmostNoActivate,
        StationLayerTransitionStep.MoveAndShow
    ];

    internal static IReadOnlyList<StationLayerTransitionStep> CollapsePlan { get; } =
    [
        StationLayerTransitionStep.Hide,
        StationLayerTransitionStep.ClearTopmost,
        StationLayerTransitionStep.AttachDesktopOwner
    ];
}
