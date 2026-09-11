using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics.Effects;

namespace TuckPane.Services;

internal enum ThemeBackdropBranch
{
    Opaque,
    Transparent,
    ClearTint,
    Glass,
    Fallback
}

/// <summary>
/// Constructs the actual production effects without a window, compositor or
/// device. This is also the seam for checking source order and tint weight.
/// </summary>
internal static class ThemeEffectGraph
{
    internal static ThemeBackdropBranch ResolveBranch(ThemeCompositionPlan plan, bool hostBackdropAvailable)
    {
        if (plan.TintOpacity >= 1) return ThemeBackdropBranch.Opaque;
        if (plan.RequiresHostBackdrop)
            return hostBackdropAvailable ? ThemeBackdropBranch.Glass : ThemeBackdropBranch.Fallback;
        return plan.TintOpacity <= 0 ? ThemeBackdropBranch.Transparent : ThemeBackdropBranch.ClearTint;
    }

    internal static IGraphicsEffect Create(ThemeCompositionPlan plan, IGraphicsEffectSource backdrop)
    {
        ArgumentNullException.ThrowIfNull(backdrop);
        if (!plan.RequiresHostBackdrop)
            throw new InvalidOperationException("A colour-only theme must bypass the sampled effect graph.");

        var balancedBackdrop = new CompositeEffect { Name = "BalancedBackdrop", Mode = CanvasComposite.SourceOver };
        balancedBackdrop.Sources.Add(backdrop);
        balancedBackdrop.Sources.Add(new OpacityEffect
        {
            Name = "NeutralOpacity",
            Opacity = plan.LuminosityOpacity,
            Source = new ColorSourceEffect { Name = "NeutralColor", Color = plan.LuminosityColor }
        });

        // SourceOver: input 0 is the background, input 1 is the foreground.
        // Keep float opacity on the colour branch, avoiding byte-alpha rounding
        // and, crucially, any second attenuation of the composited output.
        var tintedBackdrop = new CompositeEffect { Name = "TintComposite", Mode = CanvasComposite.SourceOver };
        tintedBackdrop.Sources.Add(balancedBackdrop);
        tintedBackdrop.Sources.Add(new OpacityEffect
        {
            Name = "TintOpacity",
            Opacity = plan.TintOpacity,
            Source = new ColorSourceEffect { Name = "TintColor", Color = plan.TintColor }
        });

        return new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = plan.BlurAmount,
            BorderMode = EffectBorderMode.Hard,
            Optimization = EffectOptimization.Balanced,
            Source = tintedBackdrop
        };
    }
}
