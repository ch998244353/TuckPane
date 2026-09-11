using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private ThemeTarget _manageCategory = ThemeTarget.Organizer;
    private OrganizerPlacementMode ManagedPlacementMode => _editing?.PlacementMode switch
    {
        OrganizerPlacementMode.Station => OrganizerPlacementMode.Station,
        OrganizerPlacementMode.Dock => OrganizerPlacementMode.Dock,
        _ => ReferenceEquals(ManagePlacementModeCombo.SelectedItem, ManagePositionedModeItem)
            ? OrganizerPlacementMode.Positioned : OrganizerPlacementMode.Floating
    };

    private void SelectManagePlacementMode(OrganizerPlacementMode mode) =>
        ManagePlacementModeCombo.SelectedItem = mode switch
        {
            OrganizerPlacementMode.Floating => ManageFloatingModeItem,
            OrganizerPlacementMode.Positioned => ManagePositionedModeItem,
            _ => null
        };

    internal void SetDockSize(Guid id, double size)
    {
        if (_host.State.Organizers.FirstOrDefault(item => item.Id == id) is
            { PlacementMode: OrganizerPlacementMode.Dock } dock)
            ChangeDockSettings(dock, dock.DockOrientation, size, dock.DockSpacingFactor);
    }

    private void ChangeDockSettings(OrganizerDefinition dock, DockOrientation direction, double size, double spacingFactor)
    {
        if (!_host.DockSettings.Apply(dock, direction, size, spacingFactor)) return;
        _host.Windows.FirstOrDefault(window => window.OrganizerId == dock.Id)?.ApplyDefinition(OrganizerVisualChange.Layout);
        RefreshDockSettings(dock);
        _manageChangeVersion++;
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    internal void RefreshDockSettings(OrganizerDefinition dock)
    {
        if (_selectedId != dock.Id || _editing is null) return;
        bool loading = _loadingEditor;
        _loadingEditor = true;
        try
        {
            _editing.DockIconSizeDip = dock.DockIconSizeDip;
            _editing.DockOrientation = dock.DockOrientation;
            _editing.DockSpacingFactor = dock.DockSpacingFactor;
            ManageDockIconSizeSlider.Value = dock.DockIconSizeDip;
            ManageDockSpacingSlider.Value = dock.DockSpacingFactor;
            ManageDockOrientationCombo.SelectedIndex = (int)dock.DockOrientation;
            ManageDockIconSizeValue.Text = $"{dock.DockIconSizeDip:0} DIP";
            ManageDockSpacingValue.Text = $"{dock.DockSpacingFactor:0.00}";
        }
        finally { _loadingEditor = loading; }
    }

    private void SelectManageCategory(Guid? organizerId)
    {
        if (_host.State.Organizers.FirstOrDefault(item => item.Id == organizerId) is { } organizer)
            _manageCategory = OrganizerKinds.ThemeFor(organizer.PlacementMode);
        RootNavigation.SelectedItem = _manageCategory switch
        {
            ThemeTarget.Station => StationNavItem,
            ThemeTarget.Dock => DockNavItem,
            _ => ManageNavItem
        };
        UpdateManageCategoryHeader();
    }

    private void UpdateManageCategoryHeader()
    {
        ManagePageTitle.Text = AppStrings.Get(_manageCategory switch
        {
            ThemeTarget.Station => "NavStations",
            ThemeTarget.Dock => "NavDocks",
            _ => "NavOrganizers"
        });
    }

    private bool UpdateDockAddControls()
    {
        bool dock = AddedPlacementMode == OrganizerPlacementMode.Dock;
        AddDockSettingsCard.Visibility = dock ? Visibility.Visible : Visibility.Collapsed;
        AddExpandedContentModeCard.Visibility = dock ? Visibility.Collapsed : Visibility.Visible;
        if (!_addNameWasEdited && !_loadingCreation)
        {
            _loadingDefaultName = true;
            AddNameBox.Text = dock ? AppStrings.Get("NavDocks") : _defaultAddName;
            _loadingDefaultName = false;
        }
        if (!dock) return false;
        AddNameCard.Visibility = Visibility.Visible;
        foreach (FrameworkElement control in new FrameworkElement[] { AddExpansionModeCard, AddDisplayCard,
            AddDockEdgeCard, AddCompactScaleCard, AddCanvasScaleCard, AddRowsCard, AddColumnsCard })
            control.Visibility = Visibility.Collapsed;
        AddExpandedContentModeCombo.SelectedIndex = 0;
        AddExpansionModeCombo.SelectedIndex = 0;
        AddDockIconSizeValue.Text = $"{DockLayoutMath.NormalizeIconSize(AddDockIconSizeSlider.Value):0} DIP";
        AddDockSpacingValue.Text = $"{DockLayoutMath.NormalizeSpacing(AddDockSpacingSlider.Value):0.00}";
        CreateOrganizerButton.IsEnabled = true;
        CreateLimitText.Visibility = Visibility.Collapsed;
        return true;
    }

    private bool UpdateDockManageControls()
    {
        bool dock = _editing?.PlacementMode == OrganizerPlacementMode.Dock;
        bool regular = _editing is { } current && OrganizerKinds.IsRegular(current.PlacementMode);
        ManageIdentityGroup.Visibility = _editing?.PlacementMode == OrganizerPlacementMode.Station
            ? Visibility.Collapsed : Visibility.Visible;
        ManageDockSettingsCard.Visibility = dock ? Visibility.Visible : Visibility.Collapsed;
        ManagePlacementModeCard.Visibility = regular ? Visibility.Visible : Visibility.Collapsed;
        ManageExpandedContentModeCard.Visibility = regular ? Visibility.Visible : Visibility.Collapsed;
        ManagePlacementModeCombo.IsEnabled = regular;
        if (!dock) return false;
        ManageNameCard.Visibility = Visibility.Visible;
        foreach (FrameworkElement control in new FrameworkElement[] { ManageExpansionModeCard, ManageDisplayCard,
            ManageDockEdgeCard, ManageCompactScaleCard, ManageCanvasScaleCard, ManageRowsCard, ManageColumnsCard })
            control.Visibility = Visibility.Collapsed;
        ManageDockIconSizeValue.Text = $"{DockLayoutMath.NormalizeIconSize(ManageDockIconSizeSlider.Value):0} DIP";
        ManageDockSpacingValue.Text = $"{DockLayoutMath.NormalizeSpacing(ManageDockSpacingSlider.Value):0.00}";
        return true;
    }
}
