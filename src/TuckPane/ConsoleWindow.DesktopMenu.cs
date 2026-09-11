using Microsoft.UI.Xaml;
using TuckPane.Services;
namespace TuckPane;
public sealed partial class ConsoleWindow
{
    private bool _loadingDesktopMenu;
    private void UpdateDesktopMenuControls(string? error = null)
    {
        if (!_componentReady) return;
        _loadingDesktopMenu = true;
        try
        {
            FolderContextMenuState state = _host.DesktopContextMenu.ReadState();
            DesktopMenuToggle.IsOn = state.Status == FolderContextMenuStatus.Enabled;
            DesktopMenuRepairButton.Visibility = error is not null || state.Status is FolderContextMenuStatus.Broken or FolderContextMenuStatus.OtherCopy
                ? Visibility.Visible : Visibility.Collapsed;
            DesktopMenuStatusText.Text = error ?? (state.Status switch
            {
                FolderContextMenuStatus.Broken => AppStrings.Get("DesktopMenuBroken"),
                FolderContextMenuStatus.OtherCopy => AppStrings.Get("DesktopMenuOtherCopy"),
                _ => string.Empty
            });
        }
        catch (Exception ex)
        {
            DesktopMenuToggle.IsOn = false;
            DesktopMenuStatusText.Text = AppStrings.Get("DesktopMenuErrorTitle") + " " + ex.Message;
            DesktopMenuRepairButton.Visibility = Visibility.Visible;
        }
        finally
        {
            DesktopMenuStatusText.Visibility = string.IsNullOrEmpty(DesktopMenuStatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
            _loadingDesktopMenu = false;
        }
    }

    private void DesktopMenuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _loadingDesktopMenu) return;
        SetDesktopMenuEnabled(DesktopMenuToggle.IsOn);
    }

    private void DesktopMenuRepairButton_Click(object sender, RoutedEventArgs e) => SetDesktopMenuEnabled(true);

    private void SetDesktopMenuEnabled(bool enabled)
    {
        string? error = null;
        try
        {
            if (enabled) _host.DesktopContextMenu.Enable(AppStrings.Get("DesktopMenuCommand"));
            else _host.DesktopContextMenu.Disable();
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新文件夹右键菜单。", ex);
            error = AppStrings.Get("DesktopMenuErrorTitle") + " " + ex.Message;
            ShowError(AppStrings.Get("DesktopMenuErrorTitle"), ex.Message);
        }
        UpdateDesktopMenuControls(error);
    }

}
