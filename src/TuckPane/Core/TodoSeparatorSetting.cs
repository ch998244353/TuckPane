using TuckPane.Models;

namespace TuckPane.Core;

internal sealed class TodoSeparatorSetting
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task SetAsync(GlobalSettings settings, bool enabled, Func<Task> persist, Action apply)
    {
        await _gate.WaitAsync();
        bool previous = settings.TodoShowSeparators;
        try
        {
            settings.TodoShowSeparators = enabled;
            apply();
            if (previous != enabled) await persist();
        }
        catch
        {
            settings.TodoShowSeparators = previous;
            apply();
            throw;
        }
        finally { _gate.Release(); }
    }
}
