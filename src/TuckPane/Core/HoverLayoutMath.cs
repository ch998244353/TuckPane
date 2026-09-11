using System.Numerics;
using TuckPane.Services;

namespace TuckPane.Core;

internal readonly record struct HoverRect(float X, float Y, float Width, float Height)
{
    internal float Right => X + Width;
    internal float Bottom => Y + Height;
    internal Vector2 Center => new(X + Width / 2, Y + Height / 2);
    internal bool IsEmpty => Width <= 0 || Height <= 0;
    internal bool Contains(Vector2 point) => !IsEmpty && float.IsFinite(point.X) && float.IsFinite(point.Y) &&
        point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;

    internal HoverRect Intersect(HoverRect other)
    {
        float x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        return new(x, y, Math.Max(0, Math.Min(Right, other.Right) - x),
            Math.Max(0, Math.Min(Bottom, other.Bottom) - y));
    }
}

internal readonly record struct HoverLayoutItem(HoverRect Bounds, float Scale);
internal readonly record struct HoverLayoutPose(float Scale, Vector2 Translation, HoverRect Bounds);
internal readonly record struct HoverHitRegion(HoverRect Row, HoverRect Content, HoverRect Visible,
    Vector2 Translation, float Scale, bool Presented);

internal static class HoverLayoutMath
{
    internal const float DockExpansionBudget = .5f;

    internal static bool CanActivateCompact(HoverRect rows, HoverRect viewport, HoverRect excluded, Vector2 point) =>
        rows.Intersect(viewport).Contains(point) && !excluded.Contains(point);

    internal static int HitTestItems(ReadOnlySpan<HoverHitRegion> items, bool compactList,
        HoverRect viewport, HoverRect excluded, Vector2 point)
    {
        if (!viewport.Contains(point) || excluded.Contains(point)) return -1;
        int best = -1;
        float highest = 0;
        for (int i = 0; i < items.Length; i++)
        {
            HoverHitRegion item = items[i];
            HoverRect bounds = item.Presented
                ? compactList ? Transform(item.Row, item.Content, item.Scale, item.Translation, true) : item.Visible
                : compactList ? item.Row : item.Content;
            float scale = item.Presented ? item.Scale : 1;
            if (bounds.Contains(point) && scale >= highest) { best = i; highest = scale; }
        }
        return best;
    }

    internal static bool IsPresentationReady(double width, double height, double actualWidth, double actualHeight,
        double childWidth, double childHeight) => width > 0 && height > 0 &&
        actualWidth > 0 && actualHeight > 0 && childWidth > 0 && childHeight > 0 &&
        Math.Abs(width - actualWidth) <= .5 && Math.Abs(height - actualHeight) <= .5 &&
        Math.Abs(width - childWidth) <= .5 && Math.Abs(height - childHeight) <= .5;

    // Preserve scales and pairwise spacing: only the wave's common translation
    // changes near the ends. Account for the actual rounded surface, not its box.
    internal static void ConstrainDockPoses(HoverLayoutPose[] poses, HoverRect surface, float radius, bool horizontal)
    {
        if (poses.Length == 0 || surface.IsEmpty) return;
        float minimum = float.NegativeInfinity, maximum = float.PositiveInfinity;
        float start = horizontal ? surface.X : surface.Y;
        float end = horizontal ? surface.Right : surface.Bottom;
        float crossStart = horizontal ? surface.Y : surface.X;
        float crossEnd = horizontal ? surface.Bottom : surface.Right;
        radius = Math.Clamp(radius, 0, Math.Min(surface.Width, surface.Height) / 2);
        foreach (HoverLayoutPose pose in poses)
        {
            HoverRect bounds = pose.Bounds;
            float near = horizontal ? bounds.Y : bounds.X;
            float far = horizontal ? bounds.Bottom : bounds.Right;
            float distance = Math.Clamp(Math.Max(crossStart + radius - near, far - crossEnd + radius), 0, radius);
            float inset = radius - MathF.Sqrt(Math.Max(0, radius * radius - distance * distance));
            minimum = Math.Max(minimum, start + inset - (horizontal ? bounds.X : bounds.Y));
            maximum = Math.Min(maximum, end - inset - (horizontal ? bounds.Right : bounds.Bottom));
        }
        // Keep a hundredth of a DIP inside curved boundaries when space permits:
        // float translation/rectangle addition can otherwise round back outside.
        float shift = maximum - minimum >= .02f
            ? Math.Clamp(0, minimum + .01f, maximum - .01f) : (minimum + maximum) / 2;
        Vector2 delta = horizontal ? new(shift, 0) : new(0, shift);
        for (int i = 0; i < poses.Length; i++)
        {
            HoverRect bounds = poses[i].Bounds;
            poses[i] = poses[i] with
            {
                Translation = poses[i].Translation + delta,
                Bounds = new(bounds.X + delta.X, bounds.Y + delta.Y, bounds.Width, bounds.Height)
            };
        }
    }

    internal static HoverRect VisibleAfterTransform(HoverRect content, float scale, Vector2 translation,
        bool compactList, HoverRect viewport) => Transform(content, content, scale, translation, compactList).Intersect(viewport);

    internal static HoverRect LocalClip(HoverRect visible, HoverRect content, float scale,
        Vector2 translation, bool compactList)
    {
        if (visible.IsEmpty) return default;
        Vector2 pivot = compactList ? new(0, content.Height / 2) : new(content.Width / 2, content.Height / 2);
        return new(pivot.X + (visible.X - content.X - pivot.X - translation.X) / scale,
            pivot.Y + (visible.Y - content.Y - pivot.Y - translation.Y) / scale,
            visible.Width / scale, visible.Height / scale);
    }

    // Input is in baseline layout order. The pointer is never measured against a transformed item.
    internal static HoverLayoutPose[] Calculate(IReadOnlyList<HoverLayoutItem> items, float anchor,
        bool horizontal, bool compactList, bool constrainDock = false)
    {
        if (items.Count == 0) return [];
        var poses = new HoverLayoutPose[items.Count];
        var centers = new float[items.Count];
        var scales = new float[items.Count];
        CalculateInto(items, anchor, horizontal, compactList, poses, centers, scales, constrainDock);
        return poses;
    }

    // Buffers belong to the window's realized layout, not to an individual animation frame.
    internal static void CalculateInto(IReadOnlyList<HoverLayoutItem> items, float anchor,
        bool horizontal, bool compactList, HoverLayoutPose[] poses, float[] centers, float[] scales,
        bool constrainDock = false)
    {
        if (items.Count == 0) return;
        float Center(int i) => horizontal ? items[i].Bounds.Center.X : items[i].Bounds.Center.Y;
        float Length(int i) => horizontal ? items[i].Bounds.Width : items[i].Bounds.Height;
        float expansion = 0;
        for (int i = 0; i < items.Count; i++)
        {
            scales[i] = float.IsFinite(items[i].Scale) ? Math.Clamp(items[i].Scale, 1, OrganizerHoverWave.MaximumScale) : 1;
            expansion += scales[i] - 1;
        }
        // A static cosine wave is below this budget. Bound overlapping residual waves too,
        // so fixed Dock end padding protects every rendered frame regardless of item count.
        float budget = constrainDock && expansion > DockExpansionBudget ? DockExpansionBudget / expansion : 1;
        if (constrainDock && float.IsFinite(anchor))
            anchor = Math.Clamp(anchor, Center(0) - Length(0) / 2,
                Center(items.Count - 1) + Length(items.Count - 1) / 2);
        for (int i = 0; i < items.Count; i++)
        {
            scales[i] = 1 + (scales[i] - 1) * budget;
            if (i == 0) centers[i] = Center(i);
            else
            {
                float gap = Math.Max(0, Center(i) - Center(i - 1) - (Length(i) + Length(i - 1)) / 2);
                centers[i] = centers[i - 1] + Length(i - 1) * scales[i - 1] / 2 +
                    gap * (scales[i - 1] + scales[i]) / 2 + Length(i) * scales[i] / 2;
            }
        }

        // Map the anchor through the same piecewise magnification as content and gaps,
        // then subtract that displacement from every item. Crossing a cell never changes anchors.
        float mapped = centers[^1] + (anchor - Center(items.Count - 1)) * scales[^1];
        for (int i = 0; i < items.Count; i++)
        {
            float left = Center(i) - Length(i) / 2;
            float right = Center(i) + Length(i) / 2;
            if (anchor <= right)
            {
                if (i > 0 && anchor < left)
                {
                    float previousRight = Center(i - 1) + Length(i - 1) / 2;
                    mapped = centers[i - 1] + Length(i - 1) * scales[i - 1] / 2 +
                        (anchor - previousRight) * (scales[i - 1] + scales[i]) / 2;
                }
                else mapped = centers[i] + (anchor - Center(i)) * scales[i];
                break;
            }
        }
        float shift = float.IsFinite(anchor) ? mapped - anchor : 0;
        for (int i = 0; i < items.Count; i++)
        {
            float delta = centers[i] - Center(i) - shift;
            Vector2 translation = horizontal ? new(delta, 0) : new(0, delta);
            poses[i] = new(scales[i], translation, Transform(items[i].Bounds, items[i].Bounds,
                scales[i], translation, compactList));
        }
    }

    // Clip in baseline coordinates first, then transform the visible intersection.
    internal static HoverRect Transform(HoverRect visible, HoverRect content, float scale,
        Vector2 translation, bool compactList)
    {
        if (visible.IsEmpty) return default;
        Vector2 pivot = compactList ? new(content.X, content.Center.Y) : content.Center;
        return new(pivot.X + (visible.X - pivot.X) * scale + translation.X,
            pivot.Y + (visible.Y - pivot.Y) * scale + translation.Y,
            visible.Width * scale, visible.Height * scale);
    }

    internal static bool ContainsRounded(HoverRect surface, float radius, Vector2 point)
    {
        if (!surface.Contains(point)) return false;
        radius = Math.Clamp(radius, 0, Math.Min(surface.Width, surface.Height) / 2);
        float x = Math.Clamp(point.X, surface.X + radius, surface.Right - radius);
        float y = Math.Clamp(point.Y, surface.Y + radius, surface.Bottom - radius);
        return Vector2.DistanceSquared(point, new(x, y)) <= radius * radius;
    }

    internal static bool ContainsPointer(HoverRect surface, float radius, Vector2 point,
        IEnumerable<HoverRect> overflow) => ContainsRounded(surface, radius, point) || overflow.Any(rect => rect.Contains(point));

    internal static Vector2 OverflowPadding(Vector2 contentSize, Vector2 pitch, bool horizontal)
    {
        float growth = OrganizerHoverWave.MaximumScale - 1;
        // At most six neighbouring pitches can contribute to either side of the 1.75-pitch wave.
        return contentSize * growth + (horizontal ? new Vector2(6 * pitch.X * growth, 0) :
            new Vector2(0, 6 * pitch.Y * growth)) + new Vector2(2);
    }

    internal static NativeMethods.RECT Inflate(NativeMethods.RECT bounds, int x, int y) => new()
    {
        Left = bounds.Left - x, Top = bounds.Top - y, Right = bounds.Right + x, Bottom = bounds.Bottom + y
    };

    internal static NativeMethods.RECT Deflate(NativeMethods.RECT bounds, int x, int y) => Inflate(bounds, -x, -y);

    internal static NativeMethods.RECT AdjustDpiSuggestedBounds(NativeMethods.RECT suggested,
        int paddingX, int paddingY, double previousScale, double nextScale) => Deflate(suggested,
            (int)Math.Round(paddingX * nextScale / previousScale),
            (int)Math.Round(paddingY * nextScale / previousScale));

    internal static NativeMethods.RECT ToPixels(HoverRect bounds, double scale, Vector2 offset, int fringe = 0) => new()
    {
        Left = (int)Math.Floor((bounds.X + offset.X) * scale) - fringe,
        Top = (int)Math.Floor((bounds.Y + offset.Y) * scale) - fringe,
        Right = (int)Math.Ceiling((bounds.Right + offset.X) * scale) + fringe,
        Bottom = (int)Math.Ceiling((bounds.Bottom + offset.Y) * scale) + fringe
    };
}
