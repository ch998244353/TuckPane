using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TuckPane.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private bool _loadingUnifiedExpanded;
    private bool _unifiedExpandedDirty;
    private bool _handlingUnifiedManageInput;
    private DispatcherQueueTimer? _unifiedExpandedSaveTimer;
    private Task<bool>? _unifiedExpandedSaveTask;

    private void InitializeUnifiedExpandedControls()
    {
        _unifiedExpandedSaveTimer = DispatcherQueue.CreateTimer();
        _unifiedExpandedSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _unifiedExpandedSaveTimer.IsRepeating = false;
        _unifiedExpandedSaveTimer.Tick += async (_, _) => await FlushUnifiedExpandedAsync();
        foreach (var slider in new[] { UnifiedCompactWidthSlider, UnifiedCompactHeightSlider, UnifiedCompactContentSlider,
            UnifiedIconCellSlider, UnifiedIconContentSlider })
            slider.AdjustmentCompleted += async (_, _) => await FlushUnifiedExpandedAsync();
        _host.UnifiedExpandedChanged += Host_UnifiedExpandedChanged;
        Closed += (_, _) =>
        {
            _host.UnifiedExpandedChanged -= Host_UnifiedExpandedChanged;
            _unifiedExpandedSaveTimer.Stop();
        };
    }

    private void Host_UnifiedExpandedChanged(object? sender, EventArgs args)
    {
        UpdateUnifiedExpandedControls();
        if (_editing is null || _loadingEditor || _adjustingManageControls) return;
        var source = _host.State.Organizers.FirstOrDefault(o => o.Id == _editing.Id);
        if (source is null) return;
        UnifiedExpandedSnapshot.Geometry.Capture(source).Restore(_editing);
        bool loading = _loadingEditor;
        _loadingEditor = true;
        try { UpdateManageControls(); }
        finally { _loadingEditor = loading; }
    }

    private void UpdateUnifiedExpandedControls()
    {
        if (!_componentReady) return;
        _loadingUnifiedExpanded = true;
        try
        {
            var compact = UnifiedExpandedSize.Get(_host.State.GlobalSettings, OrganizerExpandedContentMode.CompactList);
            var icon = UnifiedExpandedSize.Get(_host.State.GlobalSettings, OrganizerExpandedContentMode.Icon);
            var compactResult = _host.GetUnifiedExpandedResult(OrganizerExpandedContentMode.CompactList);
            var iconResult = _host.GetUnifiedExpandedResult(OrganizerExpandedContentMode.Icon);
            UnifiedCompactToggle.IsOn = compact.Enabled;
            UnifiedIconToggle.IsOn = icon.Enabled;
            SetUnifiedSlider(UnifiedCompactWidthSlider, compact.Width, 180, compactResult.Limits.Width, compact.Enabled);
            SetUnifiedSlider(UnifiedCompactHeightSlider, compact.Height, 105, compactResult.Limits.Height, compact.Enabled);
            SetUnifiedSlider(UnifiedCompactContentSlider, compact.Content, .5, compactResult.Limits.Content, compact.Enabled);
            SetUnifiedSlider(UnifiedIconCellSlider, icon.Cell, DisplayPlacementService.CalculateRequiredCellDip(.5), iconResult.Limits.Cell, icon.Enabled);
            SetUnifiedSlider(UnifiedIconContentSlider, icon.Content, .5, iconResult.Limits.Content, icon.Enabled);
            UnifiedCompactWidthValue.Text = AppStrings.Format("UniformExpandedDipValue", compact.Width);
            UnifiedCompactHeightValue.Text = AppStrings.Format("UniformExpandedDipValue", compact.Height);
            UnifiedIconCellValue.Text = AppStrings.Format("UniformExpandedDipValue", icon.Cell);
            SetPercent(UnifiedCompactContentValue, compact.Content);
            SetPercent(UnifiedIconContentValue, icon.Content);
            UnifiedExpandedLimitHint.Visibility = compactResult.Limited || iconResult.Limited ? Visibility.Visible : Visibility.Collapsed;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UnifiedCompactToggle, AppStrings.Get("UniformCompactExpandedTitle"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UnifiedIconToggle, AppStrings.Get("UniformIconExpandedTitle"));
            foreach (var (slider, key) in new[] { (UnifiedCompactWidthSlider, "UniformExpandedWidth"),
                (UnifiedCompactHeightSlider, "UniformExpandedHeight"), (UnifiedIconCellSlider, "UniformExpandedCell"),
                (UnifiedCompactContentSlider, "UniformExpandedContent"), (UnifiedIconContentSlider, "UniformExpandedContent") })
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, AppStrings.Get(key));
        }
        finally { _loadingUnifiedExpanded = false; }
    }

    private static void SetUnifiedSlider(ConsoleSlider slider, double value, double minimum, double maximum, bool enabled, bool syncValue = true)
    {
        // Keep the saved value visible when the feature is disabled; constrain it on enable.
        slider.Minimum = Math.Min(minimum, maximum);
        slider.Maximum = Math.Max(slider.Minimum, enabled ? maximum : Math.Max(value, maximum));
        if (syncValue) slider.SetModelValue(value);
        slider.IsEnabled = enabled;
    }

    private async void UnifiedExpandedToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_componentReady || _loadingUnifiedExpanded || sender is not ToggleSwitch toggle) return;
        var mode = ReferenceEquals(toggle, UnifiedCompactToggle) ? OrganizerExpandedContentMode.CompactList : OrganizerExpandedContentMode.Icon;
        _host.ChangeUnifiedExpanded(mode, UnifiedExpandedSize.Get(_host.State.GlobalSettings, mode) with { Enabled = toggle.IsOn });
        _unifiedExpandedDirty = true;
        await FlushUnifiedExpandedAsync();
    }

    private void UnifiedExpandedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_componentReady || _loadingUnifiedExpanded || sender is not ConsoleSlider slider || !slider.IsEnabled) return;
        var mode = slider.Tag as string == "Compact" ? OrganizerExpandedContentMode.CompactList : OrganizerExpandedContentMode.Icon;
        var value = UnifiedExpandedSize.Get(_host.State.GlobalSettings, mode);
        if (ReferenceEquals(slider, UnifiedCompactWidthSlider)) value = value with { Width = args.NewValue };
        else if (ReferenceEquals(slider, UnifiedCompactHeightSlider)) value = value with { Height = args.NewValue };
        else if (ReferenceEquals(slider, UnifiedIconCellSlider)) value = value with { Cell = args.NewValue };
        else value = value with { Content = args.NewValue };
        _host.ChangeUnifiedExpanded(mode, value);
        _unifiedExpandedDirty = true;
        _unifiedExpandedSaveTimer?.Stop();
        _unifiedExpandedSaveTimer?.Start();
    }

    private async Task<bool> FlushUnifiedExpandedAsync()
    {
        _unifiedExpandedSaveTimer?.Stop();
        while (true)
        {
            if (_unifiedExpandedSaveTask is { } pending)
            {
                bool saved = await pending;
                if (ReferenceEquals(_unifiedExpandedSaveTask, pending)) _unifiedExpandedSaveTask = null;
                if (!saved && !_unifiedExpandedDirty) return false;
                continue;
            }
            if (!_unifiedExpandedDirty) return true;
            _unifiedExpandedDirty = false;
            _unifiedExpandedSaveTask = SaveUnifiedExpandedAsync();
        }
    }

    private async Task<bool> SaveUnifiedExpandedAsync()
    {
        try { await _host.SaveStateAsync(); return true; }
        catch (Exception ex) { ShowUnifiedExpandedError(ex); return false; }
        finally { UpdateUnifiedExpandedControls(); }
    }

    internal void ShowUnifiedExpandedError(Exception ex) => ShowError(AppStrings.Get("UniformExpandedSaveError"), ex.Message);

    private bool UpdateUnifiedManageControls()
    {
        OrganizerExpandedContentMode mode = (OrganizerExpandedContentMode)Math.Clamp(ManageExpandedContentModeCombo.SelectedIndex, 0, 1);
        if (_editing is null || !OrganizerKinds.IsRegular(ManagedPlacementMode) ||
            !UnifiedExpandedSize.Get(_host.State.GlobalSettings, mode).Enabled)
        {
            ToolTipService.SetToolTip(ManageCanvasScaleSlider, null);
            ToolTipService.SetToolTip(ManageItemScaleSlider, null);
            ManageCanvasScaleSlider.Maximum = 1.2;
            return false;
        }
        var value = UnifiedExpandedSize.Get(_host.State.GlobalSettings, mode);
        var limits = _host.GetUnifiedExpandedResult(mode).Limits;
        bool compact = mode == OrganizerExpandedContentMode.CompactList;
        double size = compact ? value.Width / 280 : value.Cell / 128;
        double minimum = compact ? Math.Max(180 / 280d, 105 / value.Height * size) : DisplayPlacementService.CalculateRequiredCellDip(.5) / 128;
        double maximum = compact ? Math.Min(limits.Width / 280, limits.Height / value.Height * size) : limits.Cell / 128;
        SetUnifiedSlider(ManageCanvasScaleSlider, size, minimum, maximum, true, !_handlingUnifiedManageInput);
        SetUnifiedSlider(ManageItemScaleSlider, value.Content, .5, limits.Content, true, !_handlingUnifiedManageInput);
        SetPercent(ManageCanvasPercent, size);
        SetPercent(ManageItemPercent, value.Content);
        SetPercent(ManageCompactPercent, ManageCompactScaleSlider.Value);
        ToolTipService.SetToolTip(ManageCanvasScaleSlider, AppStrings.Get("UniformExpandedLinkedHint"));
        ToolTipService.SetToolTip(ManageItemScaleSlider, AppStrings.Get("UniformExpandedLinkedHint"));
        return true;
    }
}
