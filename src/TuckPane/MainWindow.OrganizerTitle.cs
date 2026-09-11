using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Services;
using Windows.System;

namespace TuckPane;

public sealed partial class MainWindow
{
    private readonly OrganizerTitleEdit _organizerTitleEdit = new();
    private bool _pressedOrganizerTitle;

    private async void OrganizerTitleWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && _organizerTitleEdit.IsEditing)
            await FinishOrganizerTitleEditAsync(commit: true);
    }

    private bool CommitTitleOnOutsidePress(PointerRoutedEventArgs args)
    {
        if (!_organizerTitleEdit.IsEditing || !args.GetCurrentPoint(ExpandedView).Properties.IsLeftButtonPressed) return false;
        for (DependencyObject? element = args.OriginalSource as DependencyObject; element is not null;
             element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, ExpandedNameEditor)) return false;
        args.Handled = true;
        _ = FinishOrganizerTitleEditAsync(commit: true);
        return true;
    }

    private void BeginOrganizerTitleEdit()
    {
        if (_closing || !_expanded || _definition.HideName || !_organizerTitleEdit.Begin()) return;
        _externalHoverTimer.Stop();
        _ordinaryOutsideSince = 0;
        _desktopLayer?.SetInputActivation(true);
        ExpandedNameEditor.Text = _definition.Name;
        ExpandedNameEditor.FontSize = ExpandedNameText.FontSize;
        ExpandedNameEditor.Foreground = ExpandedNameText.Foreground;
        ExpandedNameText.Visibility = Visibility.Collapsed;
        ExpandedNameEditor.Visibility = Visibility.Visible;
        ExpandedNameEditor.Focus(FocusState.Programmatic);
        ExpandedNameEditor.SelectAll();
    }

    private async void ExpandedNameEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Escape)) return;
        e.Handled = true;
        await FinishOrganizerTitleEditAsync(e.Key == VirtualKey.Enter);
    }

    private async void ExpandedNameEditor_LostFocus(object sender, RoutedEventArgs e) =>
        await FinishOrganizerTitleEditAsync(commit: true);

    private async Task FinishOrganizerTitleEditAsync(bool commit)
    {
        if (!_organizerTitleEdit.IsEditing) return;
        string draft = ExpandedNameEditor.Text;
        bool save = _organizerTitleEdit.Finish(commit);
        ExpandedNameEditor.Visibility = Visibility.Collapsed;
        ApplyOrganizerNameVisibility();
        try
        {
            if (save) await _host.RenameOrganizerAsync(OrganizerId, draft);
        }
        catch (Exception ex)
        {
            AppLogger.Error("收纳窗标题改名失败。", ex);
            ShowMessage(ex.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            _organizerTitleEdit.Saved();
            if (!_closing)
            {
                _desktopLayer?.SetInputActivation(false);
                UpdateOrganizerName();
                _ordinaryOutsideSince = 0;
            }
        }
    }
}
