using TuckPane.Core;
using TuckPane.Models;

namespace TuckPane;

public sealed partial class AppHost
{
    private readonly SemaphoreSlim _organizerPreferenceGate = new(1, 1);

    internal async Task SetHideCollapseIndicatorAsync(bool hidden)
    {
        await _organizerPreferenceGate.WaitAsync();
        try
        {
            bool previous = State.GlobalSettings.HideCollapseIndicator;
            void Apply(bool value)
            {
                State.GlobalSettings.HideCollapseIndicator = value;
                foreach (MainWindow window in _windows.Values) window.RefreshCollapseIndicator();
            }
            Apply(hidden);
            try { await SaveStateAsync(); }
            catch { Apply(previous); throw; }
        }
        finally { _organizerPreferenceGate.Release(); }
    }

    internal async Task SetOrganizerMenuVisibilityAsync(string key, bool enabled)
    {
        if (!OrganizerMenuVisibility.Actions.Contains(key)) throw new ArgumentOutOfRangeException(nameof(key));
        await _organizerPreferenceGate.WaitAsync();
        try
        {
            GlobalSettings settings = State.GlobalSettings;
            bool previous = OrganizerMenuVisibility.IsEnabled(settings, key);
            settings.OrganizerMenuVisibility[key] = enabled;
            RefreshOrganizerMenus();
            try { await SaveStateAsync(); }
            catch
            {
                settings.OrganizerMenuVisibility[key] = previous;
                RefreshOrganizerMenus();
                throw;
            }
        }
        finally { _organizerPreferenceGate.Release(); }
    }

    private void RefreshOrganizerMenus()
    {
        foreach (MainWindow window in _windows.Values) window.RefreshOrganizerMenuVisibility();
    }

    internal void SetHoverMagnificationScales(double compactScale, double dockScale)
    {
        State.GlobalSettings.CompactHoverMagnificationScale = GlobalSettings.NormalizeHoverMagnificationScale(compactScale);
        State.GlobalSettings.DockHoverMagnificationScale = GlobalSettings.NormalizeHoverMagnificationScale(dockScale);
        foreach (MainWindow window in _windows.Values) window.RefreshHoverPreference();
    }

    internal async Task SetHoverMagnificationAsync(bool dock, bool enabled)
    {
        await _organizerPreferenceGate.WaitAsync();
        try
        {
            GlobalSettings settings = State.GlobalSettings;
            bool previous = dock ? settings.DockHoverMagnificationEnabled : settings.CompactHoverMagnificationEnabled;
            void Apply(bool value)
            {
                if (dock) settings.DockHoverMagnificationEnabled = value;
                else settings.CompactHoverMagnificationEnabled = value;
                foreach (MainWindow window in _windows.Values) window.RefreshHoverPreference();
            }
            Apply(enabled);
            try { await SaveStateAsync(); }
            catch { Apply(previous); throw; }
        }
        finally { _organizerPreferenceGate.Release(); }
    }
}
