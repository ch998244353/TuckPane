using Microsoft.UI.Xaml.Controls;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private long _manageExpansionVersion;

    internal void RefreshExpansionModeSelection(Guid id)
    {
        if (_selectedId != id) return;
        bool loading = _loadingEditor;
        _loadingEditor = true;
        try
        {
            ManageExpansionModeCombo.SelectedIndex = (int)_host.GetRequestedExpansionMode(id);
            UpdateManageControls();
        }
        finally { _loadingEditor = loading; }
    }

    private async void ManageExpansionMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEditor || _adjustingManageControls ||
            _selectedId is not Guid id || ManageExpansionModeCombo.SelectedIndex < 0) return;
        var mode = (OrganizerExpansionMode)ManageExpansionModeCombo.SelectedIndex;
        long version = ++_manageExpansionVersion;
        try
        {
            // Register before flushing: a later menu choice must supersede this selection
            // even when the editor's previous save has not finished yet.
            string? error = await _host.SetOrganizerExpansionModeAsync(id, mode,
                token => FlushPendingManageChangesAsync().WaitAsync(token));
            if (error is not null && _selectedId == id && version == _manageExpansionVersion)
                ShowError(AppStrings.Get("OrganizerModeErrorTitle"), error);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法切换收纳窗展开模式。", ex);
            if (_selectedId == id && version == _manageExpansionVersion)
                ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
        }
        finally
        {
            if (version == _manageExpansionVersion) RefreshExpansionModeSelection(id);
        }
    }
}
