using TuckPane.Core;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class AppHost
{
    private readonly OperationFeedbackQueue _operationFeedbackQueue = new();
    private bool _feedbackDisposed;

    public void LogStatus(string title, string message) => AppLogger.Info($"{title}: {message}");

    public void ReportOperationError(string title, string message, IntPtr owner = default, string? monitorDevice = null)
    {
        LogStatus(title, message);
        _ = ObserveOperationErrorAsync(title, message, owner, monitorDevice);
    }

    private async Task ObserveOperationErrorAsync(string title, string message, IntPtr owner, string? monitorDevice)
    {
        try
        {
            await ShowOperationErrorAsync(title, message, owner, monitorDevice);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法显示操作错误。", ex);
        }
    }

    private Task ShowOperationErrorAsync(string title, string message, IntPtr owner = default, string? monitorDevice = null)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await _operationFeedbackQueue.ShowAsync(async () =>
                {
                    if (_feedbackDisposed || _exitPreparation.IsActive) return;
                    IntPtr dialogOwner = owner != IntPtr.Zero && NativeMethods.IsWindow(owner)
                        ? owner : Console?.Hwnd ?? IntPtr.Zero;
                    DisplayInfo display = DisplayPlacementService.GetDisplay(monitorDevice);
                    await OwnedDialogWindow.ShowMessageAsync(dialogOwner, display, this,
                        title, message, AppStrings.Get("OrganizerCreationAcknowledge"));
                });
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }))
        {
            completion.TrySetException(new InvalidOperationException("操作错误无法加入 UI 调度队列。"));
        }
        return completion.Task;
    }
}
