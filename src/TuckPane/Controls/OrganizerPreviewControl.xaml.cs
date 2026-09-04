namespace TuckPane.Controls;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

public sealed partial class OrganizerPreviewControl : UserControl
{
    public OrganizerPreviewControl()
    {
        InitializeComponent();
        Images = [Preview0, Preview1, Preview2, Preview3];
    }

    internal IReadOnlyList<Image> Images { get; }
    internal FontIcon EmptyStateIcon => EmptyIcon;
    internal object? RequestToken { get; set; }
    internal bool IsCompactVisual { get; private set; }

    internal void SetCompactVisual(bool compact)
    {
        IsCompactVisual = compact;
        ShadowBorder.Margin = compact ? new Thickness(.5, 1, 0, 0) : new Thickness(1, 2, 0, 0);
        ShadowBorder.CornerRadius = new CornerRadius(compact ? 3 : 10);
        SurfaceBorder.Margin = compact ? new Thickness(0, 0, .5, 1) : new Thickness(0, 0, 1, 2);
        SurfaceBorder.Padding = new Thickness(compact ? 1 : 4);
        SurfaceBorder.CornerRadius = new CornerRadius(compact ? 3 : 10);
        foreach (Image image in Images) image.Margin = new Thickness(compact ? .5 : 2);
        EmptyIcon.FontSize = compact ? 7 : 20;
    }
}
