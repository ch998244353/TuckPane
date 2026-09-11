namespace TuckPane.Core;

internal static class WidgetDragActivation
{
    internal static bool ShouldStart(double deltaXpx, double deltaYpx, double displayScale)
    {
        double threshold = ItemReorderSession.ActivationThresholdDip * Math.Max(1, displayScale);
        return deltaXpx * deltaXpx + deltaYpx * deltaYpx >= threshold * threshold;
    }
}
