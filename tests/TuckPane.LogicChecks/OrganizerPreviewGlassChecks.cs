using Microsoft.Graphics.Canvas.Effects;
using TuckPane.Services;
using Color = Windows.UI.Color;

internal static class OrganizerPreviewGlassChecks
{
    internal static void Run()
    {
        // Inspect production parameters and effect objects only: no window,
        // compositor, desktop capture, rendering or input is created.
        CheckFixedMaterialAndGraph();
        CheckTransparentFallback();
        CheckSubtleEdge();
        Console.WriteLine("PASS: organizer preview glass (3 focused groups; desktop visuals not tested)");
    }

    private static void CheckFixedMaterialAndGraph()
    {
        Near(OrganizerPreviewMaterial.Theme.Transparency, 0, "预览背景颜色不透明度");
        Near(OrganizerPreviewMaterial.Theme.BlurStrength, .05, "预览模糊强度");
        ThemeCompositionPlan plan = OrganizerPreviewMaterial.BuildPlan(useEffects: true);
        Require(plan.RequiresHostBackdrop && plan.UsesGaussianBlur && plan.UseEffects &&
                ThemeEffectGraph.ResolveBranch(plan, hostBackdropAvailable: true) == ThemeBackdropBranch.Glass,
            "预览没有选择 HostBackdrop 玻璃分支。");
        Near(plan.TintOpacity, 0, "预览染色权重");
        Near(plan.LuminosityOpacity, 0, "预览明暗均衡权重");
        Near(plan.HighlightOpacity, 0, "预览内部高光权重");
        Near(plan.SurfaceOpacity, 1, "模糊结果输出权重");
        Near(plan.BlurAmount, 2, "5% 模糊参数");
        using var backdrop = new ColorSourceEffect { Color = Color.FromArgb(255, 16, 32, 48) };
        using var blur = ThemeEffectGraph.Create(plan, backdrop) as GaussianBlurEffect
            ?? throw new InvalidOperationException("预览效果图最终节点必须为 GaussianBlurEffect。");
        Require(ReferenceEquals(blur.Source, backdrop),
            "预览模糊必须直接采样原背景，不能插入染色或中灰覆盖节点。");
        Near(blur.BlurAmount, 2, "实际效果图模糊参数");
    }

    private static void CheckTransparentFallback()
    {
        ThemeCompositionPlan disabled = OrganizerPreviewMaterial.BuildPlan(useEffects: false);
        Require(!disabled.UseEffects && !disabled.RequiresHostBackdrop && !disabled.UsesGaussianBlur &&
                ThemeEffectGraph.ResolveBranch(disabled, hostBackdropAvailable: true) == ThemeBackdropBranch.Transparent,
            "关闭系统特效时预览必须使用透明分支。");
        Near(disabled.BlurAmount, 0, "关闭特效的模糊参数");
        Require(ThemePalette.WithOpacity(disabled.TintColor, disabled.TintOpacity).A == 0,
            "关闭系统特效后出现不透明底色。");

        ThemeCompositionPlan enabled = OrganizerPreviewMaterial.BuildPlan(useEffects: true);
        Require(ThemeEffectGraph.ResolveBranch(enabled, hostBackdropAvailable: false) == ThemeBackdropBranch.Fallback &&
                ThemePalette.WithOpacity(enabled.TintColor, enabled.TintOpacity).A == 0,
            "HostBackdrop 不可用时的颜色画刷必须完全透明。");
    }

    private static void CheckSubtleEdge()
    {
        Near(OrganizerPreviewMaterial.EdgeThickness, 1, "预览细边缘宽度");
        var stops = OrganizerPreviewMaterial.EdgeStops;
        Require(stops.Count >= 2 && stops.All(stop =>
                double.IsFinite(stop.Offset) && stop.Offset >= 0 && stop.Offset <= 1 && stop.Color.A <= 56),
            "边缘渐变位置无效或不透明度超过微弱弧光范围。");
        Require(stops.Any(stop => stop.Color is { A: > 0, R: 255, G: 255, B: 255 }) &&
                stops.Any(stop => stop.Color is { A: > 0 and <= 26, R: 0, G: 0, B: 0 }),
            "边缘必须同时保留微弱白色弧光和暗色轮廓。");
    }

    private static void Near(double actual, double expected, string message) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) < .0001,
            $"{message}：实际 {actual:0.####}，期望 {expected:0.####}。");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
