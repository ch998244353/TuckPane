using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using TuckPane.Models;
using Windows.Graphics.Effects;
using WinUIEx;
using WinUIEx.Messaging;
using Wuc = Windows.UI.Composition;

namespace TuckPane.Services;

/// <summary>
/// Owns the complete background pipeline for a window (or a local
/// SystemBackdropElement): desktop sampling, independent transparency/tint,
/// the fixed Glass optical treatment, and the optional final blur.
/// </summary>
internal sealed class ThemeBackdrop : CompositionBrushBackdrop
{
    // A window can host several local SystemBackdropElement instances (the
    // organizer window has compact and expanded surfaces. DWM's
    // DWMWA_USE_HOSTBACKDROPBRUSH flag is per HWND, so each ThemeBackdrop must
    // participate in a shared reference count instead of enabling/disabling
    // the flag as if it owned the whole window.
    private static readonly object HostBackdropGate = new();
    private static readonly Dictionary<IntPtr, int> HostBackdropRequestCounts = [];

    private ThemeValues _theme;
    private ThemeCompositionPlan _plan;
    private Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop? _target;
    private Wuc.Compositor? _compositor;
    private IntPtr _hwnd;
    private Wuc.CompositionBrush? _currentBrush;
    private Wuc.CompositionBackdropBrush? _backdropBrush;
    private WindowMessageMonitor? _messageMonitor;
    private bool _hostBackdropCapabilityAvailable;
    private IntPtr _registeredHostBackdropHwnd;

    internal bool IsAvailable { get; private set; } = true;

    internal void SetTheme(ThemeValues theme, bool useEffects)
    {
        _theme = theme;
        _plan = ThemePalette.BuildCompositionPlan(theme, useEffects);
        if (_target is not null && _compositor is not null)
        {
            UpdateHostBackdropCapability();
            ReplaceConnectedBrush();
        }
    }

    protected override Wuc.CompositionBrush CreateBrush(Wuc.Compositor compositor)
    {
        _compositor = compositor;
        Wuc.CompositionBrush brush = BuildBrush(compositor);
        _currentBrush = brush;
        return brush;
    }

    protected override void OnTargetConnected(
        Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop connectedTarget,
        Microsoft.UI.Xaml.XamlRoot xamlRoot)
    {
        _target = connectedTarget;
        IntPtr hwnd = (IntPtr)xamlRoot.ContentIslandEnvironment.AppWindowId.Value;
        _hwnd = hwnd;
        if (hwnd != IntPtr.Zero)
        {
            // Opt into HostBackdrop only for the one real glass case:
            // intermediate opacity with a non-zero blur strength.
            UpdateHostBackdropCapability();
            _messageMonitor?.Dispose();
            _messageMonitor = new WindowMessageMonitor(hwnd);
            _messageMonitor.WindowMessageReceived += MessageMonitor_WindowMessageReceived;
        }
        base.OnTargetConnected(connectedTarget, xamlRoot);
    }

    protected override void OnTargetDisconnected(
        Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        if (_messageMonitor is not null)
        {
            _messageMonitor.WindowMessageReceived -= MessageMonitor_WindowMessageReceived;
            _messageMonitor.Dispose();
            _messageMonitor = null;
        }
        // CompositionBrushBackdrop disposes the brush currently installed on
        // the target. Release source and texture objects afterwards.
        base.OnTargetDisconnected(disconnectedTarget);
        DisableHostBackdropCapability();
        _currentBrush = null;
        _target = null;
        _compositor = null;
        _hwnd = IntPtr.Zero;
        _backdropBrush?.Dispose();
        _backdropBrush = null;
    }

    private void MessageMonitor_WindowMessageReceived(
        object? sender,
        WindowMessageEventArgs e)
    {
        if (e.Message.MessageId != NativeMethods.WM_DWMCOMPOSITIONCHANGED ||
            _target is null ||
            _compositor is null)
            return;

        // Re-apply the local HostBackdrop capability after a DWM restart,
        // then rebuild the graph so availability changes cannot leave an old
        // brush (or stale blur topology) mounted on the target.
        _hwnd = e.Message.Hwnd;
        UpdateHostBackdropCapability();
        ReplaceConnectedBrush();
    }

    private void UpdateHostBackdropCapability()
    {
        bool shouldEnable = _plan.RequiresHostBackdrop;
        if (_hwnd == IntPtr.Zero)
        {
            ReleaseHostBackdropCapability();
            _hostBackdropCapabilityAvailable = !shouldEnable;
            return;
        }

        if (!shouldEnable)
        {
            ReleaseHostBackdropCapability();
            _hostBackdropCapabilityAvailable = true;
            return;
        }

        // For an enabled graph, a failed opt-in means the color fallback is
        // the only safe result.  The shared registry leaves other local
        // backdrops on the same HWND untouched.
        bool applied = RequestHostBackdropCapability();
        _hostBackdropCapabilityAvailable = applied;
        if (!applied)
        {
            AppLogger.Error("HostBackdrop 属性启用失败，将使用遵守透明度的主题色回退。", null);
        }
    }

    private void DisableHostBackdropCapability()
    {
        ReleaseHostBackdropCapability();
        _hostBackdropCapabilityAvailable = false;
    }

    private bool RequestHostBackdropCapability()
    {
        lock (HostBackdropGate)
        {
            if (_registeredHostBackdropHwnd != IntPtr.Zero &&
                _registeredHostBackdropHwnd != _hwnd)
            {
                ReleaseHostBackdropCapabilityLocked();
            }

            bool applied = NativeMethods.SetHostBackdropBrushEnabled(_hwnd, enabled: true);
            if (!applied)
            {
                ReleaseHostBackdropCapabilityLocked();
                return false;
            }

            if (_registeredHostBackdropHwnd == IntPtr.Zero)
            {
                HostBackdropRequestCounts.TryGetValue(_hwnd, out int count);
                HostBackdropRequestCounts[_hwnd] = count + 1;
                _registeredHostBackdropHwnd = _hwnd;
            }
            return true;
        }
    }

    private void ReleaseHostBackdropCapability()
    {
        lock (HostBackdropGate)
        {
            ReleaseHostBackdropCapabilityLocked();
        }
    }

    private void ReleaseHostBackdropCapabilityLocked()
    {
        IntPtr registeredHwnd = _registeredHostBackdropHwnd;
        if (registeredHwnd == IntPtr.Zero) return;

        _registeredHostBackdropHwnd = IntPtr.Zero;
        if (!HostBackdropRequestCounts.TryGetValue(registeredHwnd, out int count) || count <= 1)
        {
            HostBackdropRequestCounts.Remove(registeredHwnd);
            _ = NativeMethods.SetHostBackdropBrushEnabled(registeredHwnd, enabled: false);
            return;
        }

        HostBackdropRequestCounts[registeredHwnd] = count - 1;
    }

    private void ReplaceConnectedBrush()
    {
        if (_target is null || _compositor is null) return;

        Wuc.CompositionBrush? previous = _currentBrush;
        Wuc.CompositionBackdropBrush? previousBackdrop = _backdropBrush;
        Wuc.CompositionBrush next = BuildBrush(_compositor);
        _target.SystemBackdrop = next;
        _currentBrush = next;

        if (previous is not null && !ReferenceEquals(previous, next))
            previous.Dispose();
        if (previousBackdrop is not null && !ReferenceEquals(previousBackdrop, _backdropBrush))
            previousBackdrop.Dispose();
    }

    private Wuc.CompositionBrush BuildBrush(Wuc.Compositor compositor)
    {
        _backdropBrush = null;
        Wuc.CompositionBackdropBrush? backdrop = null;
        Wuc.CompositionEffectBrush? effectBrush = null;
        try
        {
            // Both endpoints bypass the desktop graph. This also prevents an
            // invisible HostBackdrop request from colouring the transparent
            // window shell outside the rounded local surface.
            if (_plan.SurfaceOpacity <= .0001f)
            {
                DisableHostBackdropCapability();
                return BuildTransparentBrush(compositor);
            }

            if (_plan.SurfaceOpacity >= .9999f)
            {
                DisableHostBackdropCapability();
                return BuildTintOnlyBrush(compositor);
            }

            // Blur 0%, disabled advanced effects, and all other non-glass
            // cases are a clear theme-colour brush whose alpha is exactly the
            // selected background opacity.
            if (!_plan.RequiresHostBackdrop)
            {
                DisableHostBackdropCapability();
                return BuildColorFallbackBrush(compositor);
            }

            // DWM opt-in is verified before constructing the source. This
            // avoids empty/black desktop branches when the attribute call is
            // unsupported or failed.
            if (!_hostBackdropCapabilityAvailable)
            {
                DisableHostBackdropCapability();
                return BuildColorFallbackBrush(compositor);
            }

            backdrop = compositor.CreateHostBackdropBrush();

            // Keep the desktop branch independent from the tint branch. This
            // prevents GaussianBlur from ever blurring the selected colour.
            IGraphicsEffectSource desktopSource =
                new Wuc.CompositionEffectSourceParameter("Backdrop");
            if (Math.Abs(_plan.Saturation - 1) > .0001f)
            {
                desktopSource = new SaturationEffect
                {
                    Name = "MaterialSaturation",
                    Saturation = _plan.Saturation,
                    Source = desktopSource
                };
            }

            if (_plan.LuminosityOpacity > .0001f)
            {
                var luminosityComposite = new CompositeEffect
                {
                    Name = "MaterialLuminosity",
                    Mode = CanvasComposite.SourceOver
                };
                luminosityComposite.Sources.Add(desktopSource);
                luminosityComposite.Sources.Add(new OpacityEffect
                {
                    Name = "LuminosityOpacity",
                    Opacity = _plan.LuminosityOpacity,
                    Source = new ColorSourceEffect
                    {
                        Name = "LuminosityColor",
                        Color = _plan.LuminosityColor
                    }
                });
                desktopSource = luminosityComposite;
            }

            // Blur is optional and applies only to the desktop contribution.
            if (_plan.UsesGaussianBlur)
            {
                desktopSource = new GaussianBlurEffect
                {
                    Name = "Blur",
                    BlurAmount = _plan.BlurAmount,
                    BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Balanced,
                    Source = desktopSource
                };
            }

            // First mix the blurred desktop towards the selected theme colour
            // by o, then apply o to the entire local surface. Endpoint paths
            // above avoid constructing this graph when o is 0 or 1.
            IGraphicsEffectSource mixedSurface = new CrossFadeEffect
            {
                Name = "TransparencyComposite",
                Source1 = desktopSource,
                Source2 = new ColorSourceEffect
                {
                    Name = "TintColor",
                    Color = ThemePalette.TintColor(_theme)
                },
                CrossFade = _plan.TintOpacity
            };
            IGraphicsEffectSource output = new OpacityEffect
            {
                Name = "SurfaceOpacity",
                Opacity = _plan.SurfaceOpacity,
                Source = mixedSurface
            };

            if (output is not IGraphicsEffect finalEffect)
                throw new InvalidOperationException("主题效果图根节点不是可创建的图形效果。");
            effectBrush = CreateEffectBrush(compositor, finalEffect, backdrop);
            _backdropBrush = backdrop;
            IsAvailable = true;
            AppLogger.Info(
                $"HostBackdrop 已连接，BlurAmount={_plan.BlurAmount:0.##}，UsesGaussianBlur={_plan.UsesGaussianBlur}。");
            return effectBrush;
        }
        catch (Exception ex)
        {
            effectBrush?.Dispose();
            backdrop?.Dispose();
            _backdropBrush = null;
            IsAvailable = false;
            AppLogger.Error("HostBackdrop 玻璃效果不可用，已切换为遵守透明度的主题色。", ex);
            DisableHostBackdropCapability();
            return BuildColorFallbackBrush(compositor, logFallback: false);
        }
    }

    private Wuc.CompositionBrush BuildColorFallbackBrush(
        Wuc.Compositor compositor,
        bool logFallback = true)
    {
        IsAvailable = false;
        if (logFallback)
            AppLogger.Info("高级主题效果不可用，使用遵守透明度比例的主题色背景。");
        return compositor.CreateColorBrush(
            ThemePalette.WithOpacity(
                ThemePalette.TintColor(_theme),
                _plan.TintOpacity));
    }

    private Wuc.CompositionBrush BuildTransparentBrush(Wuc.Compositor compositor)
    {
        IsAvailable = true;
        AppLogger.Info("背景不透明度为 0%，使用完全透明画刷并旁路 HostBackdrop。");
        return compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private Wuc.CompositionBrush BuildTintOnlyBrush(Wuc.Compositor compositor)
    {
        IsAvailable = true;
        AppLogger.Info("背景不透明度为 100%，使用纯主题色背景并旁路 HostBackdrop。");
        return compositor.CreateColorBrush(_plan.TintColor);
    }

    private static Wuc.CompositionEffectBrush CreateEffectBrush(
        Wuc.Compositor compositor,
        IGraphicsEffect effect,
        Wuc.CompositionBackdropBrush backdrop)
    {
        using Wuc.CompositionEffectFactory factory = compositor.CreateEffectFactory(effect);
        Wuc.CompositionEffectBrush brush = factory.CreateBrush();
        try
        {
            brush.SetSourceParameter("Backdrop", backdrop);
            return brush;
        }
        catch
        {
            brush.Dispose();
            throw;
        }
    }

}
