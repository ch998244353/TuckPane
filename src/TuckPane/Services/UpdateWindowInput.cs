using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace TuckPane.Services;

// HWND disabling also suspends keyboard input inside WebView2 while documents are flushed.
internal sealed class UpdateWindowInput : IDisposable
{
    private readonly List<(IntPtr Handle, bool Enabled)> _windows = [];
    public UpdateWindowInput(IEnumerable<Window> windows)
    {
        foreach (Window window in windows)
        {
            IntPtr handle = WindowNative.GetWindowHandle(window);
            _windows.Add((handle, IsWindowEnabled(handle)));
            EnableWindow(handle, false);
        }
    }
    public void Dispose()
    {
        foreach (var window in _windows) EnableWindow(window.Handle, window.Enabled);
    }
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enabled);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
}
