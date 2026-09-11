using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private readonly CompactClickSequence _compactClicks = new();

    private CompactClickTarget? CompactTargetAt(Point point, out Border? host)
    {
        host = HoverItemAtViewportPoint(point);
        if (host?.Tag is not string name) return null;
        WidgetItem? item = _items.FirstOrDefault(item =>
            StringComparer.OrdinalIgnoreCase.Equals(item.RelativeName, name));
        return item is null ? null : CompactClickTarget.From(item);
    }

    private Vector2 PointerScreenPixels(PointerRoutedEventArgs args)
    {
        Point point = args.GetCurrentPoint(WindowRoot).Position;
        double scale = WindowRoot.XamlRoot?.RasterizationScale ?? 1;
        var origin = new NativeMethods.POINT();
        if (!NativeMethods.ClientToScreen(_hwnd, ref origin)) return new(float.NaN, float.NaN);
        return new(origin.X + (float)(point.X * scale), origin.Y + (float)(point.Y * scale));
    }

    private void BeginCompactClick(Border host, PointerRoutedEventArgs args)
    {
        CompactClickTarget? target = CompactTargetAt(args.GetCurrentPoint(ItemsScrollView).Position, out Border? hit);
        Vector2 pixels = PointerScreenPixels(args);
        if (target is null || !ReferenceEquals(hit, host) || !float.IsFinite(pixels.X) || !float.IsFinite(pixels.Y))
        {
            _compactClicks.Reset();
            return;
        }
        _compactClicks.Begin(target.Value, pixels, Environment.TickCount64, NativeMethods.GetDoubleClickTime(),
            new(NativeMethods.GetSystemMetrics(36), NativeMethods.GetSystemMetrics(37)),
            (float)(ItemReorderSession.ActivationThresholdDip * (WindowRoot.XamlRoot?.RasterizationScale ?? 1)));
    }

    private async Task CompleteCompactClickAsync(PointerRoutedEventArgs args)
    {
        // Resolve before cleanup can refresh/recycle hosts, while press geometry is frozen.
        CompactClickTarget? released = CompactTargetAt(args.GetCurrentPoint(ItemsScrollView).Position, out Border? hit);
        CompactClickTarget? open = _compactClicks.Complete(released, PointerScreenPixels(args));
        NativeMethods.RECT? anchor = open?.Kind == WidgetItemKind.Organizer && hit is not null &&
            TryGetElementScreenBounds(hit, out NativeMethods.RECT bounds) ? bounds : null;
        args.Handled = true;
        CancelItemReorder(preserveHover: _hoverWave.HasPointer, preserveClickSequence: true);
        if (open is not { } identity || !IsCompactList || !HoverViewportVisible) return;
        WidgetItem? item = _items.FirstOrDefault(candidate => identity.SameAs(CompactClickTarget.From(candidate)));
        if (item is not null)
            await OpenItemAsync(item, doubleTap: identity.Kind != WidgetItemKind.Organizer, organizerAnchor: anchor);
    }
}
