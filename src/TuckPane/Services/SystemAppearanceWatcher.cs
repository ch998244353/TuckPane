using Microsoft.UI.Dispatching;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TuckPane.Services;

// Native broadcasts are delivered even when WinRT event registration is
// unavailable on an unpackaged process or an isolated desktop.
internal sealed class SystemAppearanceWatcher(
    DispatcherQueue dispatcher, AccessibilitySettings accessibility, UISettings ui, Action changed)
{
    private Appearance _last = Read(accessibility, ui);
    private bool _queued;

    internal static bool IsAppearanceMessage(uint message) =>
        message is NativeMethods.WM_SETTINGCHANGE or NativeMethods.WM_THEMECHANGED or NativeMethods.WM_SYSCOLORCHANGE;

    internal void QueueCheck()
    {
        if (_queued) return;
        _queued = true;
        if (!dispatcher.TryEnqueue(() =>
        {
            _queued = false;
            Appearance next = Read(accessibility, ui);
            if (next == _last) return;
            _last = next;
            changed();
        })) _queued = false;
    }

    // Include the actual contrast palette. Moving between two high-contrast
    // themes changes colours without toggling the HighContrast flag.
    private readonly record struct Appearance(bool HighContrast, bool Effects, Color Window,
        Color WindowText, Color ButtonFace, Color ButtonText, Color Highlight, Color HighlightText, Color GrayText);

    private static Appearance Read(AccessibilitySettings access, UISettings settings)
    {
        bool contrast = access.HighContrast;
        Color ColorFor(UIElementType type) => contrast ? settings.UIElementColor(type) : default;
        return new(contrast, settings.AdvancedEffectsEnabled, ColorFor(UIElementType.Window),
            ColorFor(UIElementType.WindowText), ColorFor(UIElementType.ButtonFace), ColorFor(UIElementType.ButtonText),
            ColorFor(UIElementType.Highlight), ColorFor(UIElementType.HighlightText), ColorFor(UIElementType.GrayText));
    }
}
