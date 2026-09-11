using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private bool _exportingDiagnostics;

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_exportingDiagnostics || _closingPermanently || _appWindow is null) return;
        _exportingDiagnostics = true;
        ExportDiagnosticsButton.IsEnabled = false;
        try
        {
            var picker = new FileSavePicker(_appWindow.Id)
            {
                SuggestedFileName = $"TuckPane-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}",
                DefaultFileExtension = ".zip"
            };
            picker.FileTypeChoices.Add("ZIP", new List<string> { ".zip" });
            PickFileResult? selected = await picker.PickSaveFileAsync();
            if (selected is null || _closingPermanently) return;
            using var operation = AppLogger.Begin(DiagnosticArea.Export);
            try { await DiagnosticsExporter.ExportAsync(selected.Path); }
            catch (Exception ex) { operation.Fail(ex); throw; }
            operation.Complete();
            if (!_closingPermanently)
            {
                ShowError(AppStrings.Get("DiagnosticsTitle"), AppStrings.Get("DiagnosticsExported"));
                ConsoleInfoBar.Severity = InfoBarSeverity.Success;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Error("Diagnostics export failed", ex);
            if (!_closingPermanently) ShowError(AppStrings.Get("DiagnosticsTitle"), AppStrings.Get("DiagnosticsExportFailed"));
        }
        finally
        {
            _exportingDiagnostics = false;
            if (!_closingPermanently) ExportDiagnosticsButton.IsEnabled = true;
        }
    }
}
