namespace TuckPane.Core;

/// <summary>Serializes operation error dialogs within one host instance.</summary>
internal sealed class OperationFeedbackQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task ShowAsync(Func<Task> show)
    {
        await _gate.WaitAsync();
        try
        {
            await show();
        }
        finally
        {
            _gate.Release();
        }
    }
}
