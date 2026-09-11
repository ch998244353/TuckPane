using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using TuckPane.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private readonly SliderSaveSequence _hoverMagnificationSave = new();
    private DispatcherQueueTimer? _hoverMagnificationSaveTimer;
    private bool _loadingHoverMagnificationScales;
    private double _savedCompactHoverScale, _savedDockHoverScale;

    private void InitializeHoverMagnificationSliders()
    {
        _savedCompactHoverScale = _host.State.GlobalSettings.CompactHoverMagnificationScale;
        _savedDockHoverScale = _host.State.GlobalSettings.DockHoverMagnificationScale;
        _hoverMagnificationSaveTimer = DispatcherQueue.CreateTimer();
        _hoverMagnificationSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _hoverMagnificationSaveTimer.IsRepeating = false;
        _hoverMagnificationSaveTimer.Tick += async (_, _) => await SaveHoverMagnificationSlidersAsync();
        UpdateHoverMagnificationScaleControls();
    }

    private void UpdateHoverMagnificationScaleControls()
    {
        _loadingHoverMagnificationScales = true;
        try
        {
            GlobalSettings settings = _host.State.GlobalSettings;
            CompactHoverMagnificationSlider.SetModelValue(settings.CompactHoverMagnificationScale);
            DockHoverMagnificationSlider.SetModelValue(settings.DockHoverMagnificationScale);
            CompactHoverMagnificationSlider.IsEnabled = CompactHoverMagnificationToggle.IsEnabled
                ? settings.CompactHoverMagnificationEnabled : CompactHoverMagnificationToggle.IsOn;
            DockHoverMagnificationSlider.IsEnabled = DockHoverMagnificationToggle.IsEnabled
                ? settings.DockHoverMagnificationEnabled : DockHoverMagnificationToggle.IsOn;
            CompactHoverMagnificationValue.Text = $"{settings.CompactHoverMagnificationScale * 100:0}%";
            DockHoverMagnificationValue.Text = $"{settings.DockHoverMagnificationScale * 100:0}%";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CompactHoverMagnificationSlider,
                AppStrings.Get("CompactHoverMagnificationTitle") + " — " + AppStrings.Get("HoverMagnificationScaleLabel"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DockHoverMagnificationSlider,
                AppStrings.Get("DockHoverMagnificationTitle") + " — " + AppStrings.Get("HoverMagnificationScaleLabel"));
        }
        finally { _loadingHoverMagnificationScales = false; }
    }

    private void HoverMagnificationSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_componentReady || _loadingHoverMagnificationScales || _hoverMagnificationSaveTimer is null ||
            sender is not ConsoleSlider { IsEnabled: true } slider) return;
        GlobalSettings settings = _host.State.GlobalSettings;
        double value = GlobalSettings.NormalizeHoverMagnificationScale(slider.Value);
        bool dock = ReferenceEquals(slider, DockHoverMagnificationSlider);
        if (value == (dock ? settings.DockHoverMagnificationScale : settings.CompactHoverMagnificationScale)) return;
        _host.SetHoverMagnificationScales(dock ? settings.CompactHoverMagnificationScale : value,
            dock ? value : settings.DockHoverMagnificationScale);
        _hoverMagnificationSave.Changed();
        UpdateHoverMagnificationScaleControls();
        _hoverMagnificationSaveTimer.Stop();
        _hoverMagnificationSaveTimer.Start();
    }

    private Task<bool> SaveHoverMagnificationSlidersAsync() => _hoverMagnificationSave.SaveAsync(
        () => (_host.State.GlobalSettings.CompactHoverMagnificationScale, _host.State.GlobalSettings.DockHoverMagnificationScale),
        _host.SaveStateAsync,
        saved => (_savedCompactHoverScale, _savedDockHoverScale) = saved,
        () =>
        {
            _host.SetHoverMagnificationScales(_savedCompactHoverScale, _savedDockHoverScale);
            UpdateHoverMagnificationScaleControls();
        },
        ex => ShowSliderSaveError("SaveConfigurationError", ex));
}
