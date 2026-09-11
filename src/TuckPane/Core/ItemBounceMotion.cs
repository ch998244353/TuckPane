namespace TuckPane.Core;

// A finite launch feedback envelope, independent of hover scale and layout.
internal sealed class ItemBounceMotion
{
    private double _elapsed;
    private float _size, _start;
    internal float Offset { get; private set; }
    internal bool IsActive { get; private set; }
    internal float AmplitudeMultiplier { get; private set; } = 1;

    internal void Restart(float iconSize, bool isDock = false)
    {
        _start = Offset;
        AmplitudeMultiplier = isDock ? 2 : 1;
        _size = iconSize * AmplitudeMultiplier;
        _elapsed = 0;
        IsActive = true;
    }

    internal bool Step(double seconds)
    {
        if (!IsActive) return true;
        _elapsed += Math.Max(0, seconds);
        if (_elapsed >= .6 - 1e-9)
        {
            Reset();
            return true;
        }
        double start = _elapsed < .24 ? 0 : _elapsed < .44 ? .24 : .44;
        double duration = start == 0 ? .24 : start == .24 ? .20 : .16;
        float height = _size * (start == 0 ? .18f : start == .24 ? .10f : .05f);
        double phase = (_elapsed - start) / duration;
        float from = start == 0 ? _start : 0;
        // Each half uses a cosine ease: no jump at restart, apex or landing.
        if (phase < .5)
            Offset = from + (-height - from) * (float)((1 - Math.Cos(phase * 2 * Math.PI)) / 2);
        else
            Offset = -height * (float)((1 + Math.Cos((phase - .5) * 2 * Math.PI)) / 2);
        return false;
    }

    internal void Reset()
    {
        _elapsed = 0;
        Offset = 0;
        IsActive = false;
    }

    internal static float FitOffsetToBounds(float offset, float iconSize, float scale,
        HoverRect icon, HoverRect viewport, HoverRect surface, float radius, float amplitudeMultiplier = 1)
    {
        radius = Math.Clamp(radius, 0, Math.Min(surface.Width, surface.Height) / 2);
        float cornerDistance = Math.Clamp(Math.Max(surface.X + radius - icon.X,
            icon.Right - (surface.Right - radius)), 0, radius);
        float inset = radius - MathF.Sqrt(Math.Max(0, radius * radius - cornerDistance * cornerDistance));
        float top = Math.Max(viewport.Y, surface.Y + inset);
        float headroom = Math.Max(0, icon.Y - top);
        float fullPeak = iconSize * .18f * amplitudeMultiplier * scale;
        // Scale the entire envelope, rather than flattening just the first peak.
        return fullPeak > 0 ? offset * Math.Min(1, headroom / fullPeak) : 0;
    }
}
