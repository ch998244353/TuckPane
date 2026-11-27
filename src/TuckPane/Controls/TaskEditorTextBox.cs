using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace TuckPane.Controls;

// The native text view is created inside ContentElement when the collapsed
// editor gets its template/focus. Bind that host as well as the outer TextBox
// so its native input view cannot inherit a different default font.
public sealed class TaskEditorTextBox : TextBox
{
    private Control? _typographyHost;
    private ContentPresenter? _typographyPresenter;
    private int _typographyRequest;
    private bool _synchronizingTypography;

    public TaskEditorTextBox()
    {
        DefaultStyleKey = typeof(TextBox);
        GettingFocus += (_, _) => SynchronizeTemplateTypography();
        GotFocus += (_, _) =>
        {
            SynchronizeTemplateTypography();
            QueueTypographySynchronization();
        };
        Loaded += (_, _) =>
        {
            SynchronizeTemplateTypography();
            QueueTypographySynchronization();
        };
        Unloaded += (_, _) => ++_typographyRequest;
        DataContextChanged += (_, _) =>
        {
            SynchronizeTemplateTypography();
            QueueTypographySynchronization();
        };
        foreach (DependencyProperty property in new[]
        {
            FontSizeProperty, FontFamilyProperty, FontWeightProperty, FontStyleProperty,
            FontStretchProperty, CharacterSpacingProperty, IsTextScaleFactorEnabledProperty
        })
            RegisterPropertyChangedCallback(property, (_, _) => SynchronizeTemplateTypography());
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _typographyHost = null;
        _typographyPresenter = null;
        SynchronizeTemplateTypography();
        QueueTypographySynchronization();
    }

    internal void SynchronizeTemplateTypography()
    {
        if (_synchronizingTypography || GetTemplateChild("ContentElement") is not Control content) return;
        _synchronizingTypography = true;
        try
        {
            if (!ReferenceEquals(_typographyHost, content))
            {
                Bind(content, FontSizeProperty, nameof(FontSize));
                Bind(content, FontFamilyProperty, nameof(FontFamily));
                Bind(content, FontWeightProperty, nameof(FontWeight));
                Bind(content, FontStyleProperty, nameof(FontStyle));
                Bind(content, FontStretchProperty, nameof(FontStretch));
                Bind(content, CharacterSpacingProperty, nameof(CharacterSpacing));
                Bind(content, IsTextScaleFactorEnabledProperty, nameof(IsTextScaleFactorEnabled));
                _typographyHost = content;
            }
            // The ScrollViewer creates its presenter later than the TextBox template.
            // Its font defaults must not become the native text view's inherited font.
            content.ApplyTemplate();
            ContentPresenter? presenter = FindContentPresenter(content);
            if (presenter is not null && !ReferenceEquals(_typographyPresenter, presenter))
            {
                Bind(presenter, ContentPresenter.FontSizeProperty, nameof(FontSize));
                Bind(presenter, ContentPresenter.FontFamilyProperty, nameof(FontFamily));
                Bind(presenter, ContentPresenter.FontWeightProperty, nameof(FontWeight));
                Bind(presenter, ContentPresenter.FontStyleProperty, nameof(FontStyle));
                Bind(presenter, ContentPresenter.FontStretchProperty, nameof(FontStretch));
                Bind(presenter, ContentPresenter.CharacterSpacingProperty, nameof(CharacterSpacing));
                Bind(presenter, ContentPresenter.IsTextScaleFactorEnabledProperty, nameof(IsTextScaleFactorEnabled));
                _typographyPresenter = presenter;
            }
        }
        finally { _synchronizingTypography = false; }
    }

    private void QueueTypographySynchronization()
    {
        int request = ++_typographyRequest;
        object context = DataContext;
        object tag = Tag;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (request != _typographyRequest || !IsLoaded || Visibility != Visibility.Visible ||
                !ReferenceEquals(DataContext, context) || !ReferenceEquals(Tag, tag)) return;
            SynchronizeTemplateTypography();
        });
    }

    private static ContentPresenter? FindContentPresenter(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollContentPresenter presenter) return presenter;
            if (FindContentPresenter(child) is { } found) return found;
        }
        return null;
    }

    private void Bind(FrameworkElement content, DependencyProperty property, string name) =>
        content.SetBinding(property, new Binding
        {
            Source = this,
            Path = new PropertyPath(name),
            Mode = BindingMode.OneWay
        });
}
