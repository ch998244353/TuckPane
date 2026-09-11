using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;

namespace TuckPane.Controls;

public sealed class ConsoleSlider : Slider
{
    private Thumb? _thumb;
    private SolidColorBrush _normalBrush = new(Windows.UI.Color.FromArgb(255, 250, 249, 246));
    private SolidColorBrush _borderBrush = new(Windows.UI.Color.FromArgb(255, 184, 183, 179));

    private bool _adjusting;
    public event EventHandler? AdjustmentCompleted;

    public ConsoleSlider()
    {
        IsEnabledChanged += (_, _) => UpdateThumbOpacity();
        AddHandler(PointerPressedEvent, new PointerEventHandler(Slider_PointerPressed), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(Slider_PointerEnded), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(Slider_PointerEnded), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Slider_PointerEnded), true);
        LostFocus += (_, _) => CompleteAdjustment();
        Unloaded += (_, _) => CompleteAdjustment();
    }

    public void SetModelValue(double value)
    {
        if (!_adjusting && Math.Abs(Value - value) > .000001) Value = value;
    }

    private void Slider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) _adjusting = true;
    }

    private void Slider_PointerEnded(object sender, PointerRoutedEventArgs e) => CompleteAdjustment();

    private void CompleteAdjustment()
    {
        if (!_adjusting) return;
        _adjusting = false;
        UpdateThumbOpacity();
        AdjustmentCompleted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnApplyTemplate()
    {
        if (_thumb is not null)
        {
            _thumb.PointerPressed -= Thumb_PointerPressed;
            _thumb.PointerReleased -= Thumb_PointerReleased;
        }
        base.OnApplyTemplate();
        _thumb = GetTemplateChild("HorizontalThumb") as Thumb;
        if (_thumb is null) return;
        _thumb.Width = 8;
        _thumb.Height = 22;
        _thumb.Template = (ControlTemplate)Application.Current.Resources["ConsoleSliderThumbTemplate"];
        _thumb.Background = _normalBrush;
        _thumb.BorderBrush = _borderBrush;
        _thumb.BorderThickness = new Thickness(1);
        _thumb.PointerPressed += Thumb_PointerPressed;
        _thumb.PointerReleased += Thumb_PointerReleased;
        UpdateThumbOpacity();
    }

    private void Thumb_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_thumb is not null) _thumb.Opacity = .86;
    }

    private void Thumb_PointerReleased(object sender, PointerRoutedEventArgs e) => UpdateThumbOpacity();

    public void SetThumbPalette(Windows.UI.Color color, Windows.UI.Color borderColor)
    {
        _normalBrush = new SolidColorBrush(color);
        _borderBrush = new SolidColorBrush(borderColor);
        if (_thumb is null) return;
        _thumb.Background = _normalBrush;
        _thumb.BorderBrush = _borderBrush;
        _thumb.BorderThickness = new Thickness(1);
    }

    private void UpdateThumbOpacity()
    {
        if (_thumb is not null) _thumb.Opacity = IsEnabled ? 1 : .45;
    }
}
