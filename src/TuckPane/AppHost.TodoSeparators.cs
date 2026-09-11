using TuckPane.Core;

namespace TuckPane;

public sealed partial class AppHost
{
    private readonly TodoSeparatorSetting _todoSeparators = new();

    internal Task SetTodoShowSeparatorsAsync(bool enabled) =>
        _todoSeparators.SetAsync(State.GlobalSettings, enabled, SaveStateAsync, () =>
        {
            foreach (TodoWindow window in _externalTodoWindows.Values.ToArray())
                window.ApplySeparatorSetting();
        });
}
