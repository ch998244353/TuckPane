using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using TuckPane.Models;
using TuckPane.Core;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class MainWindow
{
    internal void RefreshOrganizerMenuVisibility()
    {
        var settings = _host.State.GlobalSettings;
        string[] visible = OrganizerMenuVisibility.VisibleActions(settings, _definition.PlacementMode);
        foreach (MenuFlyout menu in new[] { CompactTileContextMenu, ExpandedViewContextMenu })
        {
            // Side stations can be narrower than the menu; let the popup use the screen.
            menu.ShouldConstrainToRootBounds = false;
            foreach (MenuFlyoutItemBase item in menu.Items)
                item.Visibility = item is MenuFlyoutSeparator
                    ? OrganizerMenuVisibility.ShowSeparator(visible) ? Visibility.Visible : Visibility.Collapsed
                    : visible.Contains(item.Name.EndsWith("ToggleExpansionModeMenuItem", StringComparison.Ordinal)
                        ? "ContextSwitchExpansion" : item.Tag as string ?? "") ? Visibility.Visible : Visibility.Collapsed;
            if (visible.Length == 0) menu.Hide();
        }
        CompactView.ContextFlyout = visible.Length == 0 ? null : CompactTileContextMenu;
        ExpandedView.ContextFlyout = visible.Length == 0 ? null : ExpandedViewContextMenu;
    }

    private bool _addingItem;

    private void OrganizerWindow_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || _closing || _animating || _organizerTitleEdit.IsEditing) return;
        // Preserve item menus when the magnified icon extends beyond its original host.
        if (IsDock && e.TryGetPosition(ExpandedView, out Point dockPoint) && DockItemAtPoint(dockPoint) is { } host)
        {
            Item_ContextRequested(host, e);
            return;
        }
        RefreshOrganizerMenuVisibility();
        MenuFlyout menu = _expanded ? ExpandedViewContextMenu : CompactTileContextMenu;
        if (!menu.Items.Any(item => item.Visibility == Visibility.Visible)) return;
        FrameworkElement target = _expanded ? ExpandedView : CompactView;
        e.Handled = true;
        if (e.TryGetPosition(target, out Point point)) menu.ShowAt(target, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(target);
    }

    // The empty Dock button shares the same two choices as the window menu.
    private void AddItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_addingItem || _closing || sender is not FrameworkElement target) return;
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false,
            MenuFlyoutPresenterStyle = (Style)WindowRoot.Resources["OrganizerMenuPresenterStyle"] };
        var file = new MenuFlyoutItem { Text = AppStrings.Get("ContextAddFile"), Tag = "ContextAddFile" };
        var folder = new MenuFlyoutItem { Text = AppStrings.Get("ContextAddFolder"), Tag = "ContextAddFolder" };
        file.Click += AddFileMenuItem_Click;
        folder.Click += AddFolderMenuItem_Click;
        menu.Items.Add(file);
        menu.Items.Add(folder);
        LocalizeContextMenu(menu);
        menu.Opening += ContextMenu_Opening;
        menu.Opened += ContextMenu_Opened;
        menu.Closed += ContextMenu_Closed;
        menu.ShowAt(target);
    }

    private async void AddFileMenuItem_Click(object sender, RoutedEventArgs e) => await AddSelectedItemAsync(folder: false);
    private async void AddFolderMenuItem_Click(object sender, RoutedEventArgs e) => await AddSelectedItemAsync(folder: true);

    private async Task AddSelectedItemAsync(bool folder)
    {
        if (_addingItem || _closing) return;
        _addingItem = true;
        _overlayOpenCount++;
        try
        {
            // Give the flyout a chance to close; a late Closed event also respects
            // _addingItem while the Shell dialog runs its nested modal loop.
            await Task.Yield();
            if (_closing) return;
            _desktopLayer?.SetInputActivation(true);
            ResetStationPointerDelay();
            string? source = folder ? FileSelectionService.PickSingleFolder(_hwnd) : FileSelectionService.PickSingleFile(_hwnd);
            if (_closing || string.IsNullOrWhiteSpace(source)) return;
            TransferOutcome outcome = _storage.AddItemShortcut(source);
            if (outcome.Status != TransferStatus.ShortcutCreated)
            {
                ShowMessage(outcome.Message, InfoBarSeverity.Error);
                return;
            }
            StartWatcher();
            await RefreshCatalogAsync(notifyUnsupported: false);
            ShowMessage(AppStrings.Get("ShortcutCreated"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLogger.Error("添加项目失败。", ex);
            if (!_closing) ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (!_closing) _desktopLayer?.SetInputActivation(_contextMenuActivated);
            _overlayOpenCount = Math.Max(0, _overlayOpenCount - 1);
            _addingItem = false;
            ResetStationPointerDelay();
        }
    }

    private void UpdatePlacementModeMenuItems()
    {
        bool station = !OrganizerKinds.IsRegular(_definition.PlacementMode);
        string key = _definition.PlacementMode == OrganizerPlacementMode.Floating
            ? "ContextSwitchToPositioned" : "ContextSwitchToFloating";
        foreach (MenuFlyoutItem item in new[] { CompactToggleModeMenuItem, ExpandedToggleModeMenuItem })
        {
            item.Text = AppStrings.Get(key);
            item.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
            item.FontFamily = new FontFamily(AppStrings.FontFamily);
            item.CharacterSpacing = AppStrings.CharacterSpacing;
        }
        UpdateHideNameMenuItems();
    }

    private void UpdateHideNameMenuItems()
    {
        foreach (ToggleMenuFlyoutItem item in new[] { CompactHideNameMenuItem, ExpandedHideNameMenuItem })
        {
            item.Text = AppStrings.Get("ContextHideName");
            item.IsChecked = _definition.HideName;
            item.Visibility = !OrganizerKinds.IsRegular(_definition.PlacementMode) ? Visibility.Collapsed : Visibility.Visible;
            item.FontFamily = new FontFamily(AppStrings.FontFamily);
            item.CharacterSpacing = AppStrings.CharacterSpacing;
        }
        RefreshOrganizerMenuVisibility();
    }

    private void ApplyOrganizerNameVisibility()
    {
        CompactNameText.Visibility = _definition.HideName ? Visibility.Collapsed : Visibility.Visible;
        ExpandedNameText.Visibility = _organizerTitleEdit.IsEditing ? Visibility.Collapsed : _definition.HideName || !OrganizerKinds.IsRegular(_definition.PlacementMode)
            ? Visibility.Collapsed : Visibility.Visible;
        // Row heights and bounds are deliberately unchanged when names are hidden.
        ToolTipService.SetToolTip(CompactTile, _storage.Exists
            ? _definition.HideName ? null : _definition.Name
            : AppStrings.Get("MissingStorage"));
        UpdateHideNameMenuItems();
    }

    private async void HideNameMenuItem_Click(object sender, RoutedEventArgs e) =>
        await RunSafelyAsync(() => _host.SetOrganizerNameVisibilityAsync(OrganizerId,
            ((ToggleMenuFlyoutItem)sender).IsChecked), "更新收纳窗名称显示失败");
}
