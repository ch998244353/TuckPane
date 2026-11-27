using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.System;

namespace TuckPane;

public sealed partial class MainWindow
{
    private sealed class FileRenameEdit(Border host, WidgetItem item, TextBox editor, TextBlock label, StackPanel content)
    {
        internal readonly Border Host = host;
        internal readonly WidgetItem Item = item;
        internal readonly TextBox Editor = editor;
        internal readonly TextBlock Label = label;
        internal readonly StackPanel Content = content;
        internal readonly Visibility LabelVisibility = label.Visibility;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Saving;
    }

    private FileRenameEdit? _fileRenameEdit;

    // The caller retains its menu operation/expansion gate until editing finishes.
    private async Task BeginFileRenameAsync(FrameworkElement host, WidgetItem item)
    {
        if (_closing || _fileRenameEdit is not null || item.Kind is WidgetItemKind.Note or WidgetItemKind.Organizer) return;
        if (host is not Border border || FindItemPart<TextBox>(border, "ItemNameEditor") is not { } editor ||
            FindItemPart<TextBlock>(border, "ItemNameText") is not { } label ||
            FindItemPart<StackPanel>(border, "ItemContent") is not { } content)
            throw new InvalidOperationException("无法定位文件名编辑控件。");

        CancelItemReorder();
        _compactClicks.Reset();
        _catalogRefreshCancellation?.Cancel();
        bool portable = item.Kind is WidgetItemKind.PortableNote or WidgetItemKind.PortableTodo;
        var edit = new FileRenameEdit(border, item, editor, label, content);
        _fileRenameEdit = edit;
        Activated += FileRenameWindow_Activated;
        ResetHoverWave(); // Reveal the real control beneath compact/dock presentation proxies.
        _overlayOpenCount++;
        _externalHoverTimer.Stop();
        _ordinaryOutsideSince = 0;
        _desktopLayer?.SetInputActivation(true);
        try
        {
            editor.Text = FileRenameRules.InitialName(item.FullPath, portable);
            editor.FontSize = label.FontSize;
            editor.Width = IsCompactList ? Math.Max(24, label.Width) : Math.Max(72, border.ActualWidth - 10);
            editor.HorizontalAlignment = label.HorizontalAlignment;
            editor.TextAlignment = label.TextAlignment;
            content.IsHitTestVisible = true;
            label.Visibility = Visibility.Collapsed;
            editor.Visibility = Visibility.Visible;
            editor.Focus(FocusState.Programmatic);
            editor.Select(0, FileRenameRules.SelectionLength(editor.Text, item.Kind == WidgetItemKind.Folder, portable));
            await edit.Completion.Task;
        }
        finally
        {
            EndFileRename(edit);
        }
    }

    private async void ItemNameEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Escape)) return;
        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            if (_fileRenameEdit is { Saving: false } edit) EndFileRename(edit);
        }
        else await CommitFileRenameAsync();
    }

    private async void ItemNameEditor_LostFocus(object sender, RoutedEventArgs e) => await CommitFileRenameAsync();

    private async void FileRenameWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) await CommitFileRenameAsync();
    }

    private bool CommitFileRenameOnOutsidePress(PointerRoutedEventArgs args)
    {
        if (_fileRenameEdit is not { } edit) return false;
        for (DependencyObject? element = args.OriginalSource as DependencyObject; element is not null;
             element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, edit.Editor)) return false;
        args.Handled = true;
        _ = CommitFileRenameAsync();
        return true;
    }

    private async Task CommitFileRenameAsync()
    {
        if (_fileRenameEdit is not { Saving: false } edit || _closing) return;
        edit.Saving = true; // Enter and LostFocus share one commit.
        edit.Editor.IsReadOnly = true;
        try
        {
            await RenameFileItemAsync(edit.Item, edit.Editor.Text);
            EndFileRename(edit);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法重命名文件项目：{edit.Item.FullPath}", ex);
            if (!_closing && ReferenceEquals(_fileRenameEdit, edit))
            {
                ShowMessage(AppStrings.Format("RenameItemErrorFormat", edit.Item.Name, ex.Message), InfoBarSeverity.Error);
                edit.Editor.Focus(FocusState.Programmatic);
            }
        }
        finally
        {
            edit.Saving = false;
            edit.Editor.IsReadOnly = false;
            if (!ReferenceEquals(_fileRenameEdit, edit)) edit.Completion.TrySetResult();
        }
    }

    private void CancelFileRenameForHost(Border host)
    {
        if (_fileRenameEdit is { } edit && ReferenceEquals(edit.Host, host)) EndFileRename(edit);
    }

    private void CancelMissingFileRename()
    {
        if (_fileRenameEdit is { Saving: false } edit &&
            !File.Exists(edit.Item.FullPath) && !Directory.Exists(edit.Item.FullPath)) EndFileRename(edit);
    }

    private void CancelFileRename()
    {
        if (_fileRenameEdit is { } edit) EndFileRename(edit);
    }

    private void EndFileRename(FileRenameEdit edit)
    {
        if (!ReferenceEquals(_fileRenameEdit, edit)) return;
        _fileRenameEdit = null; // Hiding the editor can synchronously raise LostFocus.
        Activated -= FileRenameWindow_Activated;
        edit.Editor.Visibility = Visibility.Collapsed;
        edit.Content.IsHitTestVisible = false;
        edit.Label.Visibility = edit.LabelVisibility;
        _overlayOpenCount = Math.Max(0, _overlayOpenCount - 1);
        _desktopLayer?.SetInputActivation(false);
        ResetStationPointerDelay();
        // Recycling/closing may hide the editor while a shell rename is still returning.
        if (!edit.Saving) edit.Completion.TrySetResult();
        if (!_closing) _ = StartCatalogRefreshIfReady();
    }
}
