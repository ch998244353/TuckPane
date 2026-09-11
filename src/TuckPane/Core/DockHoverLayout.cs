using System.Numerics;

namespace TuckPane.Core;

// Sample one continuous field in baseline coordinates. Its integral saturates
// on either side, so distant items do not sway as the pointer crosses cells.
internal static class DockHoverLayout
{
    internal static void CalculateInto(IReadOnlyList<HoverLayoutItem> items, Vector2 pointer,
        float pitch, float maximumScale, bool horizontal, HoverLayoutPose[] poses)
    {
        double radius = Math.Max(1, pitch) * OrganizerHoverWave.Radius;
        double strength = float.IsFinite(maximumScale) ? Math.Clamp(maximumScale - 1, 0, .25f) : 0;
        for (int i = 0; i < items.Count; i++)
        {
            HoverRect bounds = items[i].Bounds;
            Vector2 center = bounds.Center;
            double distance = horizontal ? center.X - pointer.X : center.Y - pointer.Y;
            double cross = horizontal ? center.Y - pointer.Y : center.X - pointer.X;
            double amplitude = strength * Weight(cross, radius);
            float scale = (float)(1 + amplitude * Weight(distance, radius));
            float shift = (float)(amplitude * Integral(distance, radius));
            Vector2 translation = horizontal ? new(shift, 0) : new(0, shift);
            poses[i] = new(scale, translation,
                HoverLayoutMath.Transform(bounds, bounds, scale, translation, compactList: false));
        }
    }

    private static double Weight(double distance, double radius) => Math.Abs(distance) >= radius
        ? 0 : (1 + Math.Cos(Math.PI * distance / radius)) / 2;

    private static double Integral(double distance, double radius)
    {
        double bounded = Math.Clamp(distance, -radius, radius);
        return (bounded + radius / Math.PI * Math.Sin(Math.PI * bounded / radius)) / 2;
    }
}
