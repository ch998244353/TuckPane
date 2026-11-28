using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TuckPane.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.UI.ViewManagement;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private (GlassMaterialKind Kind, ToggleButton Button, GlassMaterialPreview Preview)[] _materialCards = [];
    private bool _materialPageVisible;
    private OptionalEventSubscription? _materialEffectsSubscription;

    private void InitializeMaterialControls()
    {
        _materialCards =
        [
            (GlassMaterialKind.Classic, ClassicMaterialButton, ClassicMaterialPreview),
            (GlassMaterialKind.Frosted, FrostedMaterialButton, FrostedMaterialPreview),
            (GlassMaterialKind.Mist, MistMaterialButton, MistMaterialPreview),
            (GlassMaterialKind.Pearl, PearlMaterialButton, PearlMaterialPreview)
        ];
        _materialEffectsSubscription = OptionalEventSubscription.TryCreate(
            () => _uiSettings.AdvancedEffectsEnabledChanged += MaterialEffectsChanged,
            () => _uiSettings.AdvancedEffectsEnabledChanged -= MaterialEffectsChanged,
            ex => AppLogger.Error("主题预览的系统效果通知不可用，设置窗口激活时将重新检查。", ex));
    }

    private void MaterialEffectsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closingPermanently && _materialPageVisible) UpdateThemeControls();
        });

    private void UpdateMaterialVisibility(bool pageVisible)
    {
        if (_materialPageVisible == pageVisible) return;
        _materialPageVisible = pageVisible;
        if (pageVisible) UpdateThemeControls();
        else ReleaseMaterialPreviews();
    }

    private void ReleaseMaterialPreviews()
    {
        foreach (var card in _materialCards) card.Preview.Suspend();
    }

    private void MaterialPreset_Click(object sender, RoutedEventArgs args)
    {
        if (!_componentReady || _loadingTheme || sender is not ToggleButton { Tag: string tag } button ||
            !Enum.TryParse(tag, out GlassMaterialKind kind)) return;
        GlassMaterialSettings current = _host.State.GlobalSettings.GetTheme(_themeTarget).Material;
        if (current.Kind == kind)
            button.IsChecked = true;
        else
            ApplyThemeChange(material: GlassMaterialSettings.ForPreset(kind));
    }

    private void MaterialSlider_ValueChanged(object sender, object args)
    {
        if (!_componentReady || _loadingTheme) return;
        GlassMaterialSettings current = _host.State.GlobalSettings.GetTheme(_themeTarget).Material;
        if (ReferenceEquals(sender, ThemeColorIntensitySlider))
            ApplyThemeChange(material: current with { ColorIntensity = ThemeColorIntensitySlider.Value });
        else if (ReferenceEquals(sender, ThemeReflectionSlider))
            ApplyThemeChange(material: current with { Reflection = ThemeReflectionSlider.Value });
        else if (ReferenceEquals(sender, ThemeGrainSlider))
            ApplyThemeChange(material: current with { Grain = ThemeGrainSlider.Value });
    }

    private void MaterialColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_componentReady || _loadingTheme) return;
        GlassMaterialSettings current = _host.State.GlobalSettings.GetTheme(_themeTarget).Material;
        uint color = ToArgb(args.NewColor);
        ApplyThemeChange(material: sender == ThemeLightColorAPicker
            ? current with { ColorAArgb = color }
            : current with { ColorBArgb = color });
    }

    private void MaterialLightDirection_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_componentReady || _loadingTheme || ThemeLightDirectionCombo.SelectedIndex < 0) return;
        GlassMaterialSettings current = _host.State.GlobalSettings.GetTheme(_themeTarget).Material;
        ApplyThemeChange(material: current with { LightDirection = (GlassLightDirection)ThemeLightDirectionCombo.SelectedIndex });
    }

    private void LocalizeMaterialControls()
    {
        ThemeMaterialExpander.Header = AppStrings.Get("GlassFineTune");
        ThemeLightDirectionCombo.Header = AppStrings.Get("GlassLightDirection");
        foreach (var card in _materialCards)
            AutomationProperties.SetName(card.Button, AppStrings.Get("Glass" + card.Kind));
        AutomationProperties.SetName(ThemeLightColorAPicker, AppStrings.Get("GlassLightA"));
        AutomationProperties.SetName(ThemeLightColorBPicker, AppStrings.Get("GlassLightB"));
        AutomationProperties.SetName(ThemeColorIntensitySlider, AppStrings.Get("GlassColorIntensity"));
        AutomationProperties.SetName(ThemeReflectionSlider, AppStrings.Get("GlassReflection"));
        AutomationProperties.SetName(ThemeGrainSlider, AppStrings.Get("GlassGrain"));
        AutomationProperties.SetName(ThemeLightDirectionCombo, AppStrings.Get("GlassLightDirection"));
    }

    private void UpdateMaterialControls(ThemeValues theme)
    {
        bool glass = !theme.FullyTransparent && !theme.SolidColorMode;
        ThemeMaterialCardGroup.Visibility = ThemeMaterialAdjustmentGroup.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        GlassMaterialSettings material = theme.Material;
        bool colorLights = glass && material.Kind is (GlassMaterialKind.Mist or GlassMaterialKind.Pearl);
        ThemeLightColorsGroup.Visibility = ThemeColorIntensityRow.Visibility = colorLights ? Visibility.Visible : Visibility.Collapsed;
        if (!_materialPageVisible || !glass)
        {
            ReleaseMaterialPreviews();
            return;
        }

        if (colorLights)
        {
            UpdateLightColor(ThemeLightColorAButton, ThemeLightColorAPicker, ThemeLightColorAChip, material.ColorAArgb, "GlassLightA");
            UpdateLightColor(ThemeLightColorBButton, ThemeLightColorBPicker, ThemeLightColorBChip, material.ColorBArgb, "GlassLightB");
        }
        SetMaterialAmount(ThemeColorIntensitySlider, ThemeColorIntensityValue, material.ColorIntensity);
        SetMaterialAmount(ThemeReflectionSlider, ThemeReflectionValue, material.Reflection);
        SetMaterialAmount(ThemeGrainSlider, ThemeGrainValue, material.Grain);
        ThemeLightDirectionCombo.SelectedIndex = (int)material.LightDirection;

        bool effects = !_settingsHighContrast && _uiSettings.AdvancedEffectsEnabled;
        string sampleName = AppStrings.Get("GlassSampleName");
        foreach (var card in _materialCards)
        {
            bool selected = card.Kind == material.Kind;
            card.Button.IsChecked = selected;
            ThemeValues sample = theme with { Material = selected ? material : GlassMaterialSettings.ForPreset(card.Kind) };
            card.Preview.Update(sample, effects, _host.State.GlobalSettings.EdgeGlowEnabled, _settingsHighContrast, sampleName);
        }
    }

    private static void UpdateLightColor(Button button, ColorPicker picker, Border chip, uint argb, string label)
    {
        var color = FromArgb(argb);
        if (picker.Color != color) picker.Color = color;
        if (chip.Background is not SolidColorBrush brush || brush.Color != color)
            chip.Background = new SolidColorBrush(color);
        AutomationProperties.SetName(button, $"{AppStrings.Get(label)} #{argb & 0xffffff:X6}");
    }

    private static void SetMaterialAmount(ConsoleSlider slider, TextBlock label, double value)
    {
        slider.SetModelValue(value);
        label.Text = $"{Math.Round(value * 100):0}%";
    }
}
