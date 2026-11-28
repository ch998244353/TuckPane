namespace TuckPane.Models;

public enum GlassMaterialKind { Classic, Frosted, Mist, Pearl }
public enum GlassLightDirection { TopLeft, TopRight, BottomLeft, BottomRight }

// These parameters describe the finish above the existing glass. Applying a
// preset must never replace the base colour, opacity or backdrop blur.
public sealed record GlassMaterialSettings
{
    public GlassMaterialKind Kind { get; init; }
    public uint ColorAArgb { get; init; } = 0xFF79D7FF;
    public uint ColorBArgb { get; init; } = 0xFFC4A5FF;
    public double ColorIntensity { get; init; }
    public double Reflection { get; init; } = 1;
    public double Grain { get; init; }
    public GlassLightDirection LightDirection { get; init; }

    internal static GlassMaterialSettings Classic { get; } = new();
    private static readonly GlassMaterialSettings Frosted = new()
        { Kind = GlassMaterialKind.Frosted, Reflection = .45, Grain = .70 };
    private static readonly GlassMaterialSettings Mist = new()
        { Kind = GlassMaterialKind.Mist, ColorIntensity = .70, Reflection = .55, Grain = .20 };
    private static readonly GlassMaterialSettings Pearl = new()
        { Kind = GlassMaterialKind.Pearl, ColorIntensity = .80, Reflection = .80, Grain = .06 };

    internal static GlassMaterialSettings ForPreset(GlassMaterialKind kind) => kind switch
    {
        GlassMaterialKind.Frosted => Frosted,
        GlassMaterialKind.Mist => Mist,
        GlassMaterialKind.Pearl => Pearl,
        _ => Classic
    };

    internal GlassMaterialSettings Normalize()
    {
        if (!Enum.IsDefined(Kind)) return Classic;
        GlassMaterialSettings preset = ForPreset(Kind);
        return this with
        {
            ColorAArgb = ColorAArgb | 0xFF000000,
            ColorBArgb = ColorBArgb | 0xFF000000,
            ColorIntensity = Limit(ColorIntensity, preset.ColorIntensity),
            Reflection = Limit(Reflection, preset.Reflection),
            Grain = Limit(Grain, preset.Grain),
            LightDirection = Enum.IsDefined(LightDirection) ? LightDirection : preset.LightDirection
        };
    }

    private static double Limit(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;
}
