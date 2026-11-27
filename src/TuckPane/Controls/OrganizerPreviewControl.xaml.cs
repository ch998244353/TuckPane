namespace TuckPane.Controls;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Services;
using Windows.Foundation;
using Windows.UI.ViewManagement;

public sealed partial class OrganizerPreviewControl : UserControl
{
    private readonly UISettings _uiSettings = new();
    private ThemeBackdrop? _backdrop;
    private XamlRoot? _subscribedRoot;
    private bool _materialConnected;
    private OrganizerPreviewEdgeState _edgeState;

    public OrganizerPreviewControl()
    {
        InitializeComponent();
        Images = [Preview0, Preview1, Preview2, Preview3];
        var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        foreach (var (offset, color) in OrganizerPreviewMaterial.EdgeStops)
            edge.GradientStops.Add(new GradientStop { Offset = offset, Color = color });
        PreviewEdge.BorderBrush = edge;
        PreviewEdge.BorderThickness = new Thickness(OrganizerPreviewMaterial.EdgeThickness);
        Loaded += Preview_Loaded;
        Unloaded += Preview_Unloaded;
        PreviewBackdropHost.SizeChanged += (_, _) => UpdateBackdropGeometry();
    }

    internal IReadOnlyList<Image> Images { get; }
    internal FontIcon EmptyStateIcon => EmptyIcon;
    internal object? RequestToken { get; set; }
    internal bool IsCompactVisual { get; private set; }

    internal void SetEdgeGlow(bool enabled, bool isDock, bool isIconPreview)
    {
        _edgeState = _edgeState with { Enabled = enabled, IsDock = isDock, IsIconPreview = isIconPreview };
        ApplyEdgeGlow();
    }

    private void ApplyEdgeGlow() =>
        PreviewReflectionLayer.Visibility = _edgeState.IsVisible ? Visibility.Visible : Visibility.Collapsed;

    internal void SetCompactVisual(bool compact)
    {
        IsCompactVisual = compact;
        _edgeState = _edgeState with { IsCompact = compact };
        ApplyEdgeGlow();
        SurfaceBorder.Margin = compact ? new Thickness(0, 0, .5, 1) : new Thickness(0, 0, 1, 2);
        SurfaceBorder.Padding = new Thickness(compact ? 1 : 4);
        SurfaceBorder.CornerRadius = new CornerRadius(compact ? 3 : 10);
        PreviewBackdropHost.Margin = PreviewEdge.Margin = SurfaceBorder.Margin;
        PreviewEdge.CornerRadius = SurfaceBorder.CornerRadius;
        UpdateBackdropGeometry();
        foreach (Image image in Images) image.Margin = new Thickness(compact ? .5 : 2);
        EmptyIcon.FontSize = compact ? 7 : 20;
    }

    private void Preview_Loaded(object sender, RoutedEventArgs e)
    {
        if (_materialConnected || !TryReleaseBackdrop()) return;
        _backdrop = new ThemeBackdrop(surfaceName: "organizer-preview", useRoundedMask: true,
            material: ThemeBackdropMaterial.OrganizerPreview);
        // Configure before connecting, so the first frame never uses a theme default.
        ApplyMaterial();
        UpdateBackdropGeometry();
        _backdrop.Attach(PreviewBackdropHost);
        _materialConnected = true;
        _subscribedRoot = XamlRoot;
        if (_subscribedRoot is not null) _subscribedRoot.Changed += Root_Changed;
        _uiSettings.AdvancedEffectsEnabledChanged += Effects_Changed;
    }

    private void Preview_Unloaded(object sender, RoutedEventArgs e)
    {
        _materialConnected = false;
        _uiSettings.AdvancedEffectsEnabledChanged -= Effects_Changed;
        if (_subscribedRoot is not null) _subscribedRoot.Changed -= Root_Changed;
        _subscribedRoot = null;
        ThemeBackdrop? pending = _backdrop;
        if (!TryReleaseBackdrop())
            DispatcherQueue.TryEnqueue(() =>
            {
                // Never let old queued cleanup release a newly loaded instance.
                if (!IsLoaded && ReferenceEquals(_backdrop, pending)) TryReleaseBackdrop();
            });
    }

    private bool TryReleaseBackdrop()
    {
        try
        {
            _backdrop?.DetachAndClose();
            // Detachment is terminal. Recycled controls create a fresh instance.
            _backdrop = null;
            return true;
        }
        catch (Exception ex)
        {
            // Retain the owner for a bounded retry or the next load/shutdown.
            AppLogger.Error("释放收纳窗预览背景失败。", ex);
            return false;
        }
    }

    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateBackdropGeometry();

    private void Effects_Changed(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded && _backdrop is not null) ApplyMaterial();
        });

    private void ApplyMaterial() =>
        _backdrop?.SetTheme(OrganizerPreviewMaterial.Theme, _uiSettings.AdvancedEffectsEnabled);

    private void UpdateBackdropGeometry() =>
        _backdrop?.SetRoundedGeometry(RoundedSurfaceGeometry.Create(
            PreviewBackdropHost.ActualWidth, PreviewBackdropHost.ActualHeight,
            IsCompactVisual ? 3 : 10, XamlRoot?.RasterizationScale ?? 1));
}
