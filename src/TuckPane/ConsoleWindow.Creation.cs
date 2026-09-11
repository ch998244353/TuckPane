using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private readonly SettingsCreationSession _creationSession = new();
    private bool _creating;
    private bool _loadingCreation;
    private ThemeTarget _creationCategory = ThemeTarget.Organizer;
    private long _settingsViewVersion;
    private NavigationViewItemBase? _activeNavigationItem;
    private bool _restoringNavigation;

    private OrganizerPlacementMode AddedPlacementMode => _creationCategory switch
    {
        ThemeTarget.Station => OrganizerPlacementMode.Station,
        ThemeTarget.Dock => OrganizerPlacementMode.Dock,
        _ => AddPlacementModeCombo.SelectedIndex == 1
            ? OrganizerPlacementMode.Positioned : OrganizerPlacementMode.Floating
    };

    private SettingsCreationDraft NewCreationDraft(ThemeTarget category) => new(new OrganizerDefinition
    {
        Name = category == ThemeTarget.Organizer ? AppStrings.DefaultOrganizerName :
            AppStrings.Get(category == ThemeTarget.Station ? "StationDefaultName" : "NavDocks"),
        PlacementMode = category == ThemeTarget.Station ? OrganizerPlacementMode.Station :
            category == ThemeTarget.Dock ? OrganizerPlacementMode.Dock : OrganizerPlacementMode.Floating,
        Position = new WidgetPosition { MonitorDevice = GetPrimaryDisplay().Device }
    }, null);

    private void CaptureCreationDraft()
    {
        if (_creating && !_loadingCreation)
            _creationSession.Save(_creationCategory, ReadCreationDraft());
    }

    private void LeaveCreationEditor()
    {
        _creating = false;
        AddEditor.Visibility = Visibility.Collapsed;
    }

    private async void BeginCreationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_creationSession.SubmittingCategory == _manageCategory) return;
        long version = ++_settingsViewVersion;
        ThemeTarget category = _manageCategory;
        if (!await FlushPendingManageChangesAsync() || version != _settingsViewVersion) return;
        CaptureCreationDraft();
        _creationCategory = category;
        _creating = true;
        _selectedId = null;
        _editing = null;
        _suppressSelection = true;
        ManageList.SelectedItem = null;
        _suppressSelection = false;
        UpdateManageListItemSurfaces();
        LoadCreationDraft(_creationSession.GetOrCreate(category, () => NewCreationDraft(category)));
        ManageDetailCard.Visibility = Visibility.Collapsed;
        AddEditor.Visibility = Visibility.Visible;
    }

    private void LoadCreationDraft(SettingsCreationDraft draft)
    {
        _loadingCreation = true;
        _loadingDefaultName = true;
        try
        {
            OrganizerDefinition value = draft.Definition;
            _addOrganizerId = value.Id;
            _addStoragePath = draft.StoragePath;
            _addNameWasEdited = draft.NameWasEdited;
            AddNameBox.Text = value.Name;
            AddPlacementModeCombo.SelectedIndex = value.PlacementMode == OrganizerPlacementMode.Positioned ? 1 : 0;
            AddExpansionModeCombo.SelectedIndex = (int)value.ExpansionMode;
            AddExpandedContentModeCombo.SelectedIndex = (int)value.ExpandedContentMode;
            AddDockEdgeCombo.SelectedIndex = (int)value.DockEdge;
            SelectDisplay(AddDisplayCombo, value.Position?.MonitorDevice);
            ConfigureGridSliders(AddRowsSlider, AddColumnsSlider, _creationCategory == ThemeTarget.Station);
            AddRowsSlider.SetModelValue(value.Layout.Rows);
            AddColumnsSlider.SetModelValue(value.Layout.Columns);
            AddCompactScaleSlider.Maximum = value.PlacementMode == OrganizerPlacementMode.Positioned
                ? OrganizerLimits.MaximumPositionedCompactScale : OrganizerLimits.MaximumCompactScale;
            AddCompactScaleSlider.SetModelValue(value.CompactScale);
            AddCanvasScaleSlider.Minimum = .1;
            AddItemScaleSlider.Maximum = Math.Max(1.65, value.ItemScale);
            AddCanvasScaleSlider.SetModelValue(value.CanvasScale);
            AddItemScaleSlider.SetModelValue(value.ItemScale);
            AddDockOrientationCombo.SelectedIndex = (int)value.DockOrientation;
            AddDockIconSizeSlider.SetModelValue(value.DockIconSizeDip);
            AddDockSpacingSlider.SetModelValue(value.DockSpacingFactor);
        }
        finally { _loadingCreation = false; _loadingDefaultName = false; }
        UpdateAddStoragePath();
        UpdateAddControls();
    }

    private void UpdateCreationAvailability()
    {
        CreationScrollViewer.IsEnabled = !_creationSession.IsSubmitting;
        ManageAddButton.IsEnabled = _creationSession.SubmittingCategory != _manageCategory;
        if (_creationSession.IsSubmitting) CreateOrganizerButton.IsEnabled = false;
    }

    private async void CreateOrganizerButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_creating || _creationSession.IsSubmitting) return;
        UpdateAddControls();
        if (!CreateOrganizerButton.IsEnabled) return;
        CaptureCreationDraft();
        ThemeTarget category = _creationCategory;
        long view = _settingsViewVersion;
        int generation = _creationSession.Generation;
        try
        {
            Task<SettingsCreationResult?> pending = _creationSession.SubmitAsync(category, _host.CreateOrganizerAsync);
            UpdateCreationAvailability();
            SettingsCreationResult? result = await pending;
            if (result is { SameSession: true } && view == _settingsViewVersion && _creating)
            {
                LeaveCreationEditor();
                PopulateManageList(result.Created.Id);
            }
            else if (result is { SameSession: true })
            {
                // Reflect the new object without replacing another editor or stealing navigation.
                PopulateManageList(_selectedId, preserveEditor: true);
            }
        }
        catch (Exception ex)
        {
            if (generation == _creationSession.Generation && view == _settingsViewVersion)
                ShowError(AppStrings.Get("CreateErrorTitle"), ex.Message);
            else AppLogger.Error("Unable to create organizer from settings draft.", ex);
        }
        finally { UpdateAddControls(); }
    }
}
