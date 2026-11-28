using System.Text.Json;
using TuckPane.Models;
using TuckPane.Services;

internal static class GlassMaterialChecks
{
    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"TuckPane-glass-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            await CheckUserStateAsync(Path.Combine(root, "state.json"));
            CheckOptics();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("PASS: classic compatibility, independent window materials, mode transitions, persistence and optical controls.");
    }

    private static async Task CheckUserStateAsync(string path)
    {
        const uint silver = 0xFFE2E5E9;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = 16,
            GlobalSettings = new
            {
                ThemeColorArgb = silver, ThemeTransparency = .18, ThemeBlurStrength = .30,
                StationThemeColorArgb = silver, StationThemeTransparency = .35, StationThemeBlurStrength = 1.45,
                DockThemeColorArgb = silver, DockThemeTransparency = .51, DockThemeBlurStrength = 2.0
            }
        }));
        var store = new StateStore(path);
        var state = await store.LoadAsync();
        var settings = state.GlobalSettings;
        (ThemeTarget Target, double Opacity, double Blur, GlassMaterialKind Kind)[] targets =
        [
            (ThemeTarget.Organizer, .18, .30, GlassMaterialKind.Mist),
            (ThemeTarget.Station, .35, 1.45, GlassMaterialKind.Frosted),
            (ThemeTarget.Dock, .51, 2.0, GlassMaterialKind.Pearl)
        ];
        foreach (var (target, opacity, blur, kind) in targets)
        {
            var initial = settings.GetTheme(target);
            Require(initial.Material == GlassMaterialSettings.Classic && initial.ColorArgb == silver &&
                initial.Transparency == opacity && initial.BlurStrength == blur, $"Legacy data changed for {target}.");
            var recipe = GlassMaterialSettings.ForPreset(kind) with { ColorAArgb = 0xFF348591, LightDirection = GlassLightDirection.BottomLeft };
            var chosen = GlobalSettings.ResolveThemeUpdate(initial, silver, opacity, blur, false, material: recipe);
            var solid = GlobalSettings.ResolveThemeUpdate(chosen, silver, opacity, blur, true, solidOpacity: .41);
            var transparent = GlobalSettings.ResolveThemeUpdate(solid, silver, opacity, blur, false,
                fullyTransparent: target == ThemeTarget.Dock);
            var back = GlobalSettings.ResolveThemeUpdate(transparent, silver, opacity, blur, false, fullyTransparent: false);
            Require(back == (initial with { Material = recipe, SolidOpacity = .41 }), $"Mode switch lost settings for {target}.");
            settings.SetTheme(target, back);
        }
        await store.SaveAsync(state);
        var saved = (await store.LoadAsync()).GlobalSettings;
        foreach (var (target, _, _, kind) in targets)
            Require(saved.GetTheme(target) == settings.GetTheme(target) && saved.GetTheme(target).Material.Kind == kind,
                $"Material round trip failed for {target}.");
        var invalid = new GlassMaterialSettings { Kind = GlassMaterialKind.Mist, ColorIntensity = double.NaN, Grain = 3, Reflection = -1,
            ColorAArgb = 0x00010203, LightDirection = (GlassLightDirection)101 }.Normalize();
        Require(invalid.ColorIntensity == GlassMaterialSettings.ForPreset(GlassMaterialKind.Mist).ColorIntensity &&
            invalid.Grain == 1 && invalid.Reflection == 0 && invalid.ColorAArgb == 0xFF010203 &&
            invalid.LightDirection == GlassLightDirection.TopLeft, "Invalid saved fields were not normalized.");
    }

    private static void CheckOptics()
    {
        var theme = new ThemeValues(0xFFE2E5E9, .18, .3);
        var classic = GlassMaterialOptics.Resolve(theme, true);
        Require(classic.Reflection == ThemePalette.HighlightOpacity(theme) && classic.GrainOpacity == 0 &&
            classic.ColorOpacity == 0, "Classic finish must preserve the original highlight.");
        foreach (GlassMaterialKind kind in Enum.GetValues<GlassMaterialKind>())
        {
            var selected = theme with { Material = GlassMaterialSettings.ForPreset(kind) };
            Require(ThemePalette.BuildCompositionPlan(selected, true) == ThemePalette.BuildCompositionPlan(theme, true),
                "A finish changed the existing backdrop.");
            Require(!GlassMaterialOptics.Resolve(selected, false).ShowFinish &&
                !GlassMaterialOptics.Resolve(selected with { SolidColorMode = true }, true).ShowFinish &&
                !GlassMaterialOptics.Resolve(selected with { FullyTransparent = true }, true).ShowFinish,
                "Glass decoration leaked into a reduced or non-glass mode.");
        }
        var mist = theme with { Material = GlassMaterialSettings.ForPreset(GlassMaterialKind.Mist) };
        var finish = GlassMaterialOptics.Resolve(mist, true);
        Require(finish.ColorOpacity > 0 && finish.Reflection > 0 && finish.GrainOpacity > 0, "Mist preset missing a layer.");
        Require(GlassMaterialOptics.Resolve(mist with { Material = mist.Material with { Grain = 0 } }, true) ==
                (finish with { GrainOpacity = 0 }) &&
                GlassMaterialOptics.Resolve(mist with { Material = mist.Material with { Reflection = 0 } }, true) ==
                (finish with { Reflection = 0 }) &&
                GlassMaterialOptics.Resolve(mist with { Material = mist.Material with { ColorIntensity = 0 } }, true) ==
                (finish with { ColorOpacity = 0 }), "An optical control also changed another layer.");
        Require(Enum.GetValues<GlassLightDirection>().Select(GlassMaterialOptics.LightOrigin).Distinct().Count() == 4,
            "Light directions must select distinct corners.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
