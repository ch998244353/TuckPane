using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TuckPane.Services;

// Register each local override once. Later themes update the same brush, so already
// instantiated templates keep their reference and no resource is removed mid-update.
internal sealed class WindowThemeBrushes
{
    private readonly Dictionary<(ResourceDictionary Resources, string Key), SolidColorBrush> _owned = [];

    internal void Set(ResourceDictionary resources, string key, Color color)
    {
        try
        {
            if (_owned.TryGetValue((resources, key), out SolidColorBrush? brush))
            {
                brush.Color = color;
                return;
            }
            brush = new SolidColorBrush(color);
            resources[key] = brush;
            _owned.Add((resources, key), brush);
        }
        catch (Exception ex)
        {
            // Keep stage/key in the local error; do not disguise resource failures as IO.
            throw new InvalidOperationException($"Theme resource update failed: {key} (0x{ex.HResult:X8}).", ex);
        }
    }
}
