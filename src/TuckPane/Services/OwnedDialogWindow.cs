using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Models;
using Windows.Graphics;
using WinUIEx;
using WinRT.Interop;

namespace TuckPane.Services;

internal sealed class OwnedDialogWindow : Window
{
    private readonly IntPtr _owner;
    private readonly AppHost _host;
    private readonly Grid _root;
    private readonly ThemeSurface _surface;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly Button _primaryButton;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<bool>? _tryAccept;
    private bool _accepted;
    private bool _ownerDisabled;

    private OwnedDialogWindow(
        IntPtr owner,
        DisplayInfo display,
        AppHost host,
        string title,
        FrameworkElement body,
        string primaryText,
        string cancelText)
    {
        _owner = owner;
        _host = host;
        Title = title;

        _root = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent)
        };
        _root.KeyDown += Root_KeyDown;

        var surfaceHost = new Grid { IsHitTestVisible = false };
        var content = new Grid
        {
            Padding = new Thickness(24, 20, 24, 20),
            RowSpacing = 18
        };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Children.Add(surfaceHost);
        _root.Children.Add(content);

        Grid.SetRow(body, 0);
        content.Children.Add(body);

        _primaryButton = new Button
        {
            Content = primaryText,
            MinWidth = 92,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _primaryButton.Click += (_, _) => Accept();
        var cancelButton = new Button { Content = cancelText, MinWidth = 92 };
        cancelButton.Click += (_, _) => Close();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(_primaryButton);
        actions.Children.Add(cancelButton);
        Grid.SetRow(actions, 1);
        content.Children.Add(actions);
        Content = _root;
        SystemBackdrop = new TransparentTintBackdrop(Colors.Transparent);
        _surface = new ThemeSurface(surfaceHost);

        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.IsShownInSwitchers = false;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, true);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        NativeMethods.RECT bounds = DisplayPlacementService.CalculateCenteredDialogBounds(display);
        appWindow.MoveAndResize(new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
        if (owner != IntPtr.Zero)
        {
            _ = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_HWNDPARENT, owner);
            long extendedStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            extendedStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            extendedStyle &= ~NativeMethods.WS_EX_APPWINDOW;
            _ = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(extendedStyle));
        }

        Closed += OwnedDialogWindow_Closed;
        _host.ThemeChanged += Host_ThemeChanged;
        ApplyTheme();
    }

    internal static Task<bool> ShowTextInputAsync(
        IntPtr owner,
        DisplayInfo display,
        AppHost host,
        string title,
        string defaultText,
        string primaryText,
        string cancelText,
        Func<string, string?> validateAndAccept,
        int maxLength = 120,
        string? placeholderText = null)
    {
        var input = new TextBox
        {
            Text = defaultText,
            MaxLength = maxLength,
            PlaceholderText = placeholderText
        };
        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.IndianRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var body = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(input);
        body.Children.Add(error);

        var window = new OwnedDialogWindow(owner, display, host, title, body, primaryText, cancelText);
        window._tryAccept = () =>
        {
            string? validationError = validateAndAccept(input.Text);
            if (validationError is null) return true;
            error.Text = validationError;
            error.Visibility = Visibility.Visible;
            input.SelectAll();
            _ = input.Focus(FocusState.Programmatic);
            return false;
        };
        window._root.Loaded += (_, _) =>
        {
            input.SelectAll();
            _ = input.Focus(FocusState.Programmatic);
        };
        return window.ShowAsync();
    }

    internal static Task<bool> ShowConfirmationAsync(
        IntPtr owner,
        DisplayInfo display,
        AppHost host,
        string title,
        string message,
        string primaryText,
        string cancelText)
    {
        var body = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var window = new OwnedDialogWindow(owner, display, host, title, body, primaryText, cancelText)
        {
            _tryAccept = static () => true
        };
        window._root.Loaded += (_, _) => _ = window._primaryButton.Focus(FocusState.Programmatic);
        return window.ShowAsync();
    }

    private Task<bool> ShowAsync()
    {
        try
        {
            if (_owner != IntPtr.Zero)
            {
                _ownerDisabled = NativeMethods.IsWindowEnabled(_owner);
                if (_ownerDisabled) _ = NativeMethods.EnableWindow(_owner, false);
            }
            Activate();
            return _completion.Task;
        }
        catch
        {
            RestoreOwner();
            throw;
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            Accept();
        }
    }

    private void Accept()
    {
        if (_tryAccept is null || !_tryAccept()) return;
        _accepted = true;
        Close();
    }

    private void OwnedDialogWindow_Closed(object sender, WindowEventArgs args)
    {
        _host.ThemeChanged -= Host_ThemeChanged;
        _surface.Dispose();
        RestoreOwner();
        _completion.TrySetResult(_accepted);
    }

    private void ApplyTheme()
    {
        ThemeValues theme = _host.State.GlobalSettings.GetTheme(ThemeTarget.Organizer);
        _root.RequestedTheme = ThemePalette.IsDark(theme) ? ElementTheme.Dark : ElementTheme.Light;
        _surface.SetTheme(theme, _uiSettings.AdvancedEffectsEnabled);
    }

    private void Host_ThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void RestoreOwner()
    {
        if (!_ownerDisabled) return;
        _ownerDisabled = false;
        _ = NativeMethods.EnableWindow(_owner, true);
    }
}
