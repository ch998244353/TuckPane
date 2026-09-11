using Microsoft.UI.Xaml;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private void InitializeUpdates()
    {
        _host.Updates.Changed += UpdatesChanged;
        RefreshUpdates();
    }

    private void UpdatesChanged()
    {
        if (!_closingPermanently) DispatcherQueue.TryEnqueue(() => { if (!_closingPermanently) RefreshUpdates(); });
    }

    private void RefreshUpdates()
    {
        AppUpdateService update = _host.Updates;
        UpdateNavItem.Content = AppStrings.Get("NavUpdate");
        UpdateVersionText.Text = AppStrings.Format("UpdateCurrentVersion", update.CurrentVersion,
            AppStrings.Get(update.Installed ? "UpdateInstalledEdition" : "UpdatePortableEdition"));
        UpdateStatusText.Text = AppStrings.Get(update.StatusKey) + (update.Available is { } release ? "  " + release.Version : "");
        UpdateLastCheckText.Text = AppStrings.Format("UpdateLastCheck", update.LastCheck is { } last ? AppStrings.FormatDate(last) : AppStrings.Get("UpdateNeverChecked"));
        UpdateBadge.Visibility = update.Available is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateCheckButton.IsEnabled = !update.Busy;
        UpdateInstallButton.IsEnabled = !update.Busy && update.Available is not null;
        UpdateCancelButton.Visibility = update.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Visibility = update.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = update.Progress;
        UpdateErrorText.Text = update.Error ?? "";
        UpdateErrorText.Visibility = string.IsNullOrEmpty(update.Error) ? Visibility.Collapsed : Visibility.Visible;
        UpdateNotesText.Text = update.Available?.Notes ?? AppStrings.Get("UpdateNoNotes");
        UpdateRecoveryText.Text = string.Join(Environment.NewLine, new[] { update.PreviousResult,
            string.IsNullOrEmpty(update.BackupPath) ? null : AppStrings.Format("UpdateBackupLocation", update.BackupPath) }.Where(s => !string.IsNullOrEmpty(s)));
    }

    private async void UpdateCheck_Click(object sender, RoutedEventArgs e) => await _host.Updates.CheckAsync(true);
    private async void UpdateInstall_Click(object sender, RoutedEventArgs e) =>
        await _host.Updates.DownloadAndInstallAsync(_host.ExitForUpdateAsync, () =>
            _host.State.Organizers.Select(AppPaths.ResolveStoragePath).Concat(new[] { AppPaths.UserRoot, AppPaths.LocalRoot }).ToArray());
    private void UpdateCancel_Click(object sender, RoutedEventArgs e) => _host.Updates.CancelDownload();
}
