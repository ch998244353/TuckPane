using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using Windows.Foundation;
using WinRT.Interop;

namespace TuckPane.Services;

// One stable, explicitly owned HWND. It never becomes a tooltip-service target
// and cannot take activation away from the item which is being hovered.
internal sealed class DockTipWindow : Window, IDisposable
{
    private readonly Grid _root = new() { RequestedTheme = ElementTheme.Light, UseLayoutRounding = true };
    private readonly Border _body = new()
    {
        Padding = new Thickness(10, 6, 10, 6),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private readonly SystemBackdropElement _surface = new()
    {
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBlock _text = new()
    {
        FontSize = 12, Foreground = new SolidColorBrush(Colors.Black),
        TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap,
        IsTextSelectionEnabled = false
    };
    private readonly ThemeBackdrop _backdrop = new(surfaceName: "dock-tip", useRoundedMask: true);
    private readonly Windows.UI.ViewManagement.UISettings _settings = new();
    private readonly NativeWindowChromeController _chrome;
    private XamlRoot? _xamlRoot;
    private string? _measuredText;
    private double _measuredWidth, _measuredScale, _measuredTextScale;
    private (int Width, int Height) _clientSize;
    private Presentation? _request;
    private bool _presenting, _disposed;
    private readonly record struct Presentation(string Text, HoverRect Icon, NativeMethods.RECT Work, bool Horizontal, double Scale);
    internal IntPtr Hwnd { get; }
    internal bool IsOpen { get; private set; }

    internal DockTipWindow(IntPtr owner)
    {
        _root.Children.Add(_surface);
        _body.Child = _text;
        _root.Children.Add(_body);
        _root.Loaded += Root_Loaded;
        _surface.SizeChanged += Surface_SizeChanged;
        Content = _root;
        Hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        }
        NativeMethods.SetWindowLongPtr(Hwnd, NativeMethods.GWLP_HWNDPARENT, owner);
        long windowStyle = NativeMethods.GetWindowLongPtr(Hwnd, NativeMethods.GWL_STYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(Hwnd, NativeMethods.GWL_STYLE, new((windowStyle | NativeMethods.WS_POPUP) &
            ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU |
              NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX)));
        long style = NativeMethods.GetWindowLongPtr(Hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(Hwnd, NativeMethods.GWL_EXSTYLE,
            new((style | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE |
                NativeMethods.WS_EX_TRANSPARENT) & ~NativeMethods.WS_EX_APPWINDOW));
        SystemBackdrop = new TransparentWindowBackdrop();
        _backdrop.Attach(_surface);
        _chrome = new(Hwnd, _root.DispatcherQueue, extendClientFrame: false, applyBorderLast: true);
        _settings.AdvancedEffectsEnabledChanged += EffectsChanged;
        _settings.TextScaleFactorChanged += TextScaleChanged;
        ApplyMaterial();
    }

    private void EffectsChanged(Windows.UI.ViewManagement.UISettings sender, object args) =>
        _root.DispatcherQueue.TryEnqueue(() => { if (!_disposed) ApplyMaterial(); });

    private void ApplyMaterial() => _backdrop.SetTheme(new ThemeValues(0xFFFFFFFF, .78, .65),
        _settings.AdvancedEffectsEnabled);

    private void TextScaleChanged(Windows.UI.ViewManagement.UISettings sender, object args) => QueueMeasureRefresh();

    private void QueueMeasureRefresh() => _root.DispatcherQueue.TryEnqueue(() =>
    {
        if (_disposed) return;
        _measuredText = null;
        PresentRequest();
    });

    private void Root_Loaded(object sender, RoutedEventArgs args)
    {
        if (_xamlRoot is not null) _xamlRoot.Changed -= XamlRoot_Changed;
        _xamlRoot = _root.XamlRoot;
        if (_xamlRoot is not null) _xamlRoot.Changed += XamlRoot_Changed;
        // Run after the current resize/presentation has completed; Loaded and DPI
        // notifications can arrive synchronously inside those native calls.
        QueueMeasureRefresh();
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale != _measuredScale) QueueMeasureRefresh();
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs args) =>
        _backdrop.SetRoundedGeometry(RoundedSurfaceGeometry.Create(args.NewSize.Width, args.NewSize.Height,
            8, _root.XamlRoot?.RasterizationScale ?? _measuredScale));

    internal void Present(string text, HoverRect iconScreenPixels, NativeMethods.RECT work, bool horizontal, double scale)
    {
        if (_disposed) return;
        _request = new(text, iconScreenPixels, work, horizontal, scale);
        PresentRequest();
    }

    private void PresentRequest()
    {
        if (_disposed || _presenting || _request is not { } request) return;
        _presenting = true;
        try
        {
            // Move a hidden/reused tip to its destination monitor before resolving its DPI.
            if (!IsOpen)
                NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, (int)request.Icon.X, (int)request.Icon.Y, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                    NativeMethods.SWP_NOOWNERZORDER);
            double scale = Math.Max(.5, _root.XamlRoot?.RasterizationScale ?? request.Scale);
            float s = (float)scale;
            var area = new HoverRect(request.Work.Left / s, request.Work.Top / s,
                request.Work.Width / s, request.Work.Height / s);
            // Floor the cap first, so rounding the measured client size up cannot cross the work area.
            double maxBodyWidth = Math.Floor(DockVisualMath.TipWorkArea(area).Width * scale) / scale;
            double textScale = _settings.TextScaleFactor;
            if (_measuredText != request.Text || _measuredWidth != maxBodyWidth ||
                _measuredScale != scale || _measuredTextScale != textScale)
            {
                _text.Text = request.Text;
                _text.TextWrapping = TextWrapping.NoWrap;
                _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double maxTextWidth = Math.Max(1, maxBodyWidth - 20);
                if (_text.DesiredSize.Width > maxTextWidth)
                {
                    _text.TextWrapping = TextWrapping.Wrap;
                    _text.Measure(new Size(maxTextWidth, double.PositiveInfinity));
                }
                _clientSize = DockVisualMath.TipClientPixels(new Vector2(
                    (float)Math.Min(maxBodyWidth, _text.DesiredSize.Width + 20),
                    (float)(_text.DesiredSize.Height + 12)), scale);
                _body.Width = _surface.Width = _clientSize.Width / scale;
                _body.Height = _surface.Height = _clientSize.Height / scale;
                _backdrop.SetRoundedGeometry(RoundedSurfaceGeometry.Create(_body.Width, _body.Height, 8, scale));
                _measuredText = request.Text;
                _measuredWidth = maxBodyWidth;
                _measuredScale = scale;
                _measuredTextScale = textScale;
            }
            if (AppWindow.ClientSize.Width != _clientSize.Width || AppWindow.ClientSize.Height != _clientSize.Height)
                AppWindow.ResizeClient(new(_clientSize.Width, _clientSize.Height));

            var icon = new HoverRect(request.Icon.X / s, request.Icon.Y / s, request.Icon.Width / s, request.Icon.Height / s);
            var placement = DockVisualMath.PlaceTip(icon, new((float)_body.Width, (float)_body.Height), area, request.Horizontal);
            // Placement describes the client body's origin. SetWindowPos describes the outer HWND.
            var origin = new NativeMethods.POINT();
            if (!NativeMethods.GetWindowRect(Hwnd, out var window) || !NativeMethods.ClientToScreen(Hwnd, ref origin)) return;
            NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOP,
                (int)Math.Round(placement.Bounds.X * scale) - (origin.X - window.Left),
                (int)Math.Round(placement.Bounds.Y * scale) - (origin.Y - window.Top), 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW);
            IsOpen = true;
        }
        finally { _presenting = false; }
    }

    internal void HideTip()
    {
        _request = null;
        if (!IsOpen || _disposed) return;
        AppWindow.Hide();
        IsOpen = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        HideTip();
        _disposed = true;
        _root.Loaded -= Root_Loaded;
        _surface.SizeChanged -= Surface_SizeChanged;
        if (_xamlRoot is not null) _xamlRoot.Changed -= XamlRoot_Changed;
        _settings.AdvancedEffectsEnabledChanged -= EffectsChanged;
        _settings.TextScaleFactorChanged -= TextScaleChanged;
        _backdrop.DetachAndClose();
        _chrome.Dispose();
        Close();
    }
}
