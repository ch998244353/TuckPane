using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private static readonly IReadOnlyDictionary<string, uint> NativeControlColors = new Dictionary<string, uint>
    {
        ["ToggleSwitchStrokeOn"] = 0xFF007AFF,
        ["ToggleButtonBorderBrushChecked"] = 0xFF007AFF,
        ["ToggleSwitchFillOff"] = 0xFFD1D1D6,
        ["ToggleSwitchStrokeOff"] = 0xFFC7C7CC,
        ["ToggleSwitchKnobFillOff"] = 0xFFFFFFFF,
        ["ToggleSwitchStrokeOnPointerOver"] = 0xFF006EE6,
        ["ToggleButtonBorderBrushCheckedPointerOver"] = 0xFF006EE6,
        ["ToggleSwitchFillOffPointerOver"] = 0xFFD1D1D6,
        ["ToggleSwitchStrokeOffPointerOver"] = 0xFFC7C7CC,
        ["ToggleSwitchKnobFillOffPointerOver"] = 0xFFFFFFFF,
        ["ToggleSwitchStrokeOnPressed"] = 0xFF0062CC,
        ["ToggleButtonBorderBrushCheckedPressed"] = 0xFF0062CC,
        ["ToggleSwitchFillOffPressed"] = 0xFFD1D1D6,
        ["ToggleSwitchStrokeOffPressed"] = 0xFFC7C7CC,
        ["ToggleSwitchKnobFillOffPressed"] = 0xFFFFFFFF,
        ["ToggleSwitchStrokeOnDisabled"] = 0xFFD1D1D6,
        ["ToggleButtonBorderBrushCheckedDisabled"] = 0xFFD1D1D6,
        ["ToggleSwitchFillOffDisabled"] = 0xFFE5E5EA,
        ["ToggleSwitchStrokeOffDisabled"] = 0xFFD1D1D6,
        ["ToggleSwitchKnobFillOffDisabled"] = 0xFFF5F5F7,
        ["ButtonBackground"] = 0xFFF5F5F7,
        ["ButtonForeground"] = 0xFF1D1D1F,
        ["ButtonBorderBrush"] = 0xFFD1D1D6,
        ["ButtonBackgroundPointerOver"] = 0xFFEBEBEF,
        ["ButtonForegroundPointerOver"] = 0xFF1D1D1F,
        ["ButtonBorderBrushPointerOver"] = 0xFFD1D1D6,
        ["ButtonBackgroundPressed"] = 0xFFE2E2E7,
        ["ButtonForegroundPressed"] = 0xFF1D1D1F,
        ["ButtonBorderBrushPressed"] = 0xFFD1D1D6,
        ["ButtonBackgroundDisabled"] = 0xFFF5F5F7,
        ["ButtonForegroundDisabled"] = 0xFF8E8E93,
        ["ButtonBorderBrushDisabled"] = 0xFFE5E5EA,
        ["ComboBoxBackground"] = 0xFFF5F5F7,
        ["ComboBoxForeground"] = 0xFF1D1D1F,
        ["ComboBoxBorderBrush"] = 0xFFD1D1D6,
        ["ComboBoxBackgroundPointerOver"] = 0xFFEBEBEF,
        ["ComboBoxForegroundPointerOver"] = 0xFF1D1D1F,
        ["ComboBoxBorderBrushPointerOver"] = 0xFFD1D1D6,
        ["ComboBoxBackgroundPressed"] = 0xFFE2E2E7,
        ["ComboBoxForegroundPressed"] = 0xFF1D1D1F,
        ["ComboBoxBorderBrushPressed"] = 0xFFD1D1D6,
        ["ComboBoxBackgroundDisabled"] = 0xFFF5F5F7,
        ["ComboBoxForegroundDisabled"] = 0xFF8E8E93,
        ["ComboBoxBorderBrushDisabled"] = 0xFFE5E5EA,
        ["TextControlBackground"] = 0xFFF5F5F7,
        ["TextControlForeground"] = 0xFF1D1D1F,
        ["TextControlBorderBrush"] = 0xFFD1D1D6,
        ["TextControlBackgroundPointerOver"] = 0xFFEBEBEF,
        ["TextControlForegroundPointerOver"] = 0xFF1D1D1F,
        ["TextControlBorderBrushPointerOver"] = 0xFFD1D1D6,
        ["TextControlBackgroundPressed"] = 0xFFE2E2E7,
        ["TextControlForegroundPressed"] = 0xFF1D1D1F,
        ["TextControlBorderBrushPressed"] = 0xFFD1D1D6,
        ["TextControlBackgroundDisabled"] = 0xFFF5F5F7,
        ["TextControlForegroundDisabled"] = 0xFF8E8E93,
        ["TextControlBorderBrushDisabled"] = 0xFFE5E5EA,
        ["ToggleButtonBackground"] = 0xFFF5F5F7,
        ["ToggleButtonForeground"] = 0xFF1D1D1F,
        ["ToggleButtonBorderBrush"] = 0xFFD1D1D6,
        ["ToggleButtonBackgroundPointerOver"] = 0xFFEBEBEF,
        ["ToggleButtonForegroundPointerOver"] = 0xFF1D1D1F,
        ["ToggleButtonBorderBrushPointerOver"] = 0xFFD1D1D6,
        ["ToggleButtonBackgroundPressed"] = 0xFFE2E2E7,
        ["ToggleButtonForegroundPressed"] = 0xFF1D1D1F,
        ["ToggleButtonBorderBrushPressed"] = 0xFFD1D1D6,
        ["ToggleButtonBackgroundDisabled"] = 0xFFF5F5F7,
        ["ToggleButtonForegroundDisabled"] = 0xFF8E8E93,
        ["ToggleButtonBorderBrushDisabled"] = 0xFFE5E5EA,
        ["TextControlBorderBrushFocused"] = 0xFF007AFF,
        ["TextControlBackgroundFocused"] = 0xFFFFFFFF,
        ["TextControlForegroundFocused"] = 0xFF1D1D1F,
        ["ComboBoxDropDownBackground"] = 0xFFFFFFFF,
        ["ComboBoxDropDownBorderBrush"] = 0xFFE5E5EA,
        ["SystemControlHighlightAccentBrush"] = 0xFF007AFF,
        ["SystemControlHighlightListAccentLowBrush"] = 0xFFEAF3FF,
        ["ToggleSwitchFillOn"] = 0xFF007AFF,
        ["ToggleSwitchFillOnPointerOver"] = 0xFF006EE6,
        ["ToggleSwitchFillOnPressed"] = 0xFF0062CC,
        ["ToggleSwitchFillOnDisabled"] = 0xFFD1D1D6,
        ["ToggleButtonBackgroundChecked"] = 0xFF007AFF,
        ["ToggleButtonBackgroundCheckedPointerOver"] = 0xFF006EE6,
        ["ToggleButtonBackgroundCheckedPressed"] = 0xFF0062CC,
        ["ToggleButtonBackgroundCheckedDisabled"] = 0xFFD1D1D6,
        ["ToggleSwitchKnobFillOn"] = 0xFFFFFFFF,
        ["ToggleSwitchKnobFillOnPointerOver"] = 0xFFFFFFFF,
        ["ToggleSwitchKnobFillOnPressed"] = 0xFFFFFFFF,
        ["ToggleSwitchKnobFillOnDisabled"] = 0xFFFFFFFF,
        ["ToggleButtonForegroundChecked"] = 0xFFFFFFFF,
        ["ToggleButtonForegroundCheckedPointerOver"] = 0xFFFFFFFF,
        ["ToggleButtonForegroundCheckedPressed"] = 0xFFFFFFFF,
        ["ToggleButtonForegroundCheckedDisabled"] = 0xFFFFFFFF,
        ["NavigationViewItemBackgroundSelected"] = 0xFFEAF3FF,
        ["NavigationViewItemBackgroundSelectedPointerOver"] = 0xFFDEECFF,
        ["NavigationViewItemBackgroundSelectedPressed"] = 0xFFD1E4FF,
        ["NavigationViewItemBackgroundSelectedDisabled"] = 0xFFF5F5F7,
        ["NavigationViewItemForegroundSelected"] = 0xFF1D1D1F,
        ["NavigationViewItemForegroundSelectedPointerOver"] = 0xFF1D1D1F,
        ["NavigationViewItemForegroundSelectedPressed"] = 0xFF1D1D1F,
        ["NavigationViewItemForegroundSelectedDisabled"] = 0xFF1D1D1F,
        ["NavigationViewSelectionIndicatorForeground"] = 0xFF007AFF,
    };

    private void SettingsHighContrastChanged(AccessibilitySettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closingPermanently) return;
            ApplyTheme();
            UpdateManageListItemSurfaces();
        });

    private void RefreshSettingsAccessibility()
    {
        if (_settingsHighContrast == _accessibilitySettings.HighContrast) return;
        ApplyTheme();
        UpdateManageListItemSurfaces();
    }

    private void ApplyNativeControlPalette(bool highContrast)
    {
        foreach (var (key, argb) in NativeControlColors)
        {
            bool selected = key.Contains("Checked") || key.Contains("FillOn") || key.Contains("StrokeOn") || key.Contains("Selected") || key.Contains("Accent") || key.Contains("Indicator");
            bool text = key.Contains("Foreground") || key.Contains("Knob");
            UIElementType systemColor = key.Contains("Disabled")
                ? (text || key.Contains("Border") || key.Contains("Stroke") ? UIElementType.GrayText : UIElementType.ButtonFace)
                : text ? (selected ? UIElementType.HighlightText : UIElementType.ButtonText)
                : selected || (key.Contains("Border") && key.Contains("Focused")) ? UIElementType.Highlight
                : (key.Contains("Border") || key.Contains("Stroke")) ? UIElementType.WindowText : UIElementType.ButtonFace;
            SetSurfaceBrush(key, highContrast ? _uiSettings.UIElementColor(systemColor) : FromArgb(argb));
        }
    }

    private Border CreateMenuSettingRow(ToggleSwitch toggle, string label)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label, FontSize = 14, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetSurfaceBrush("ConsolePrimaryTextBrush")
        });
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return new Border { Style = (Style)ConsoleRoot.Resources["Row"], Child = grid };
    }

    private readonly Dictionary<Border, long> _settingRowVisibilityTokens = new();

    private void SettingsGroup_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border { Child: StackPanel panel }) return;
        foreach (Border row in panel.Children.OfType<Border>())
        {
            if (_settingRowVisibilityTokens.ContainsKey(row)) continue;
            _settingRowVisibilityTokens[row] = row.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, (_, _) => UpdateSettingGroupSeparators(panel));
        }
        UpdateSettingGroupSeparators(panel);
    }

    private static void UpdateSettingGroupSeparators(StackPanel panel)
    {
        Border[] visibleRows = panel.Children.OfType<Border>()
            .Where(row => row.Visibility == Visibility.Visible).ToArray();
        for (int index = 0; index < visibleRows.Length; index++)
            visibleRows[index].BorderThickness = index == visibleRows.Length - 1
                ? new Thickness(0) : new Thickness(0, 0, 0, 1);
    }
}
