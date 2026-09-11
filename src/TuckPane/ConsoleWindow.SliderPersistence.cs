using TuckPane.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private readonly SliderSaveSequence _uniformSliderSave = new();
    private readonly SliderSaveSequence _nameSliderSave = new();
    private readonly SliderSaveSequence _delaySliderSave = new();
    private readonly SliderSaveSequence _themeSliderSave = new();

    private void InitializeSliderCompletion()
    {
        // Named controls include those in pages/expanders not materialized yet.
        foreach (ConsoleSlider slider in new[] { UniformFloatingCompactScaleSlider, UniformPositionedCompactScaleSlider,
            CompactHoverMagnificationSlider, DockHoverMagnificationSlider,
            CompactNameScaleSlider, ExpandedNameScaleSlider, HoverExpandDelaySlider, PointerLeaveCollapseDelaySlider,
            StationPointerLeaveCollapseDelaySlider, StationActivationDistanceSlider, StationHoverExpandDelaySlider,
            ThemeTransparencySlider, ThemeBlurStrengthSlider, ManageRowsSlider, ManageColumnsSlider,
            ManageCompactScaleSlider, ManageCanvasScaleSlider, ManageItemScaleSlider,
            ManageDockIconSizeSlider, ManageDockSpacingSlider, AddRowsSlider, AddColumnsSlider,
            AddCompactScaleSlider, AddCanvasScaleSlider, AddItemScaleSlider, AddDockIconSizeSlider, AddDockSpacingSlider })
            slider.AdjustmentCompleted += Slider_AdjustmentCompleted;
    }

    private async void Slider_AdjustmentCompleted(object? sender, EventArgs args)
    {
        if (!_componentReady || _closingPermanently) return;
        await FlushPendingSliderSavesAsync();
        await FlushPendingManageChangesAsync(commitName: false);
        UpdateUniformCompactScaleControls();
        UpdateNameScaleControls();
        UpdateHoverDelayControls();
        UpdateThemeControls();
        UpdateAddControls();
        UpdateManageControls();
        UpdateHoverMagnificationScaleControls();
    }

    internal async Task<bool> FlushPendingSliderSavesAsync()
    {
        _uniformCompactScaleSaveTimer.Stop();
        _nameScaleSaveTimer.Stop();
        _hoverDelaySaveTimer.Stop();
        _themeSaveTimer.Stop();
        _hoverMagnificationSaveTimer?.Stop();
        if (_uniformCompactScaleApplyScheduled) ApplyPendingUniformCompactScaleChanges(null, EventArgs.Empty);
        bool saved = await SaveHoverMagnificationSlidersAsync();
        saved = await SaveUniformSlidersAsync() && saved;
        saved = await SaveNameSlidersAsync() && saved;
        saved = await SaveDelaySlidersAsync() && saved;
        return await SaveThemeAsync() && saved;
    }

    private Task<bool> SaveUniformSlidersAsync() => _uniformSliderSave.SaveAsync(
        () =>
        {
            if (_uniformCompactScaleApplyScheduled) ApplyPendingUniformCompactScaleChanges(null, EventArgs.Empty);
            return (_host.State.GlobalSettings.UniformFloatingCompactScale,
            _host.State.GlobalSettings.UniformPositionedCompactScale,
            _host.CaptureUniformCompactScaleSnapshots(OrganizerPlacementMode.Floating),
            _host.CaptureUniformCompactScaleSnapshots(OrganizerPlacementMode.Positioned), _uniformSliderSave.Revision);
        },
        _host.SaveStateAsync,
        saved =>
        {
            _savedUniformFloatingCompactScale = saved.Item1;
            _savedUniformPositionedCompactScale = saved.Item2;
            // Keep the committed geometry only while a newer edit is already pending.
            // A later independent gesture must capture fresh window state.
            bool newerEdit = _uniformSliderSave.Revision != saved.Item5;
            _savedUniformFloatingSnapshots = newerEdit ? saved.Item3 : null;
            _savedUniformPositionedSnapshots = newerEdit ? saved.Item4 : null;
        },
        () => { RestoreSavedUniformCompactScales(); UpdateUniformCompactScaleControls(); },
        ex => ShowSliderSaveError("UniformCompactScaleErrorTitle", ex));

    private Task<bool> SaveNameSlidersAsync() => _nameSliderSave.SaveAsync(
        () => (_host.State.GlobalSettings.UniformFloatingCompactNameScale, _host.State.GlobalSettings.ExpandedNameScale),
        _host.SaveStateAsync,
        saved => { _savedCompactNameScale = saved.Item1; _savedExpandedNameScale = saved.Item2; },
        () => { RestoreSavedNameScales(); UpdateNameScaleControls(); },
        ex => ShowSliderSaveError("UniformCompactNameScaleErrorTitle", ex));

    private Task<bool> SaveDelaySlidersAsync() => _delaySliderSave.SaveAsync(
        () => (_host.State.GlobalSettings.HoverExpandDelayMs, _host.State.GlobalSettings.PointerLeaveCollapseDelayMs,
            _host.State.GlobalSettings.StationPointerLeaveCollapseDelayMs, _host.State.GlobalSettings.StationActivationDistanceDip,
            _host.State.GlobalSettings.StationHoverExpandDelayMs),
        _host.SaveStateAsync,
        saved =>
        {
            (_savedHoverExpandDelayMs, _savedPointerLeaveCollapseDelayMs, _savedStationPointerLeaveCollapseDelayMs,
                _savedStationActivationDistanceDip, _savedStationHoverExpandDelayMs) = saved;
        },
        () =>
        {
            _host.SetHoverDelays(_savedHoverExpandDelayMs, _savedPointerLeaveCollapseDelayMs, _savedStationPointerLeaveCollapseDelayMs);
            _host.SetStationActivation(_savedStationActivationDistanceDip, _savedStationHoverExpandDelayMs);
            UpdateHoverDelayControls();
        },
        ex => ShowSliderSaveError("HoverDelayErrorTitle", ex));

    private Task<bool> SaveThemeSlidersAsync() => _themeSliderSave.SaveAsync(
        () => (_host.State.GlobalSettings.GetTheme(ThemeTarget.Organizer),
            _host.State.GlobalSettings.GetTheme(ThemeTarget.Station), _host.State.GlobalSettings.GetTheme(ThemeTarget.Dock)),
        _host.SaveStateAsync,
        saved => (_savedOrganizerTheme, _savedStationTheme, _savedDockTheme) = saved,
        () =>
        {
            foreach (var (target, previous) in new[] { (ThemeTarget.Organizer, _savedOrganizerTheme), (ThemeTarget.Station, _savedStationTheme), (ThemeTarget.Dock, _savedDockTheme) })
                _host.UpdateGlobalTheme(target, previous.ColorArgb, previous.Transparency, previous.BlurStrength,
                    previous.SolidColorMode, previous.SolidOpacity, previous.FullyTransparent);
        },
        ex => ShowSliderSaveError("ThemeSaveErrorTitle", ex));

    private void ShowSliderSaveError(string key, Exception ex)
    {
        AppLogger.Error("无法保存滑条设置。", ex);
        ShowError(AppStrings.Get(key), ex.Message);
    }
}
