using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.CompilerServices;
using TuckPane.Core;
using TuckPane.Models;
using Microsoft.UI.Xaml;
using Windows.Graphics.Effects;
using WinRT;
using WinUIEx;
using WinUIEx.Messaging;
using Wuc = Windows.UI.Composition;

namespace TuckPane.Services;

/// <summary>
/// Owns the complete background pipeline for a window (or a local
/// SystemBackdropElement): fixed background balancing, a single colour
/// opacity contribution, and final blur. Window/target ownership stays local.
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
    private readonly ThemeTarget _themeTarget;
    private readonly string _surfaceName;
    private readonly bool _useRoundedMask;
    private RoundedSurfaceGeometry _roundedGeometry;
    private RoundedBackdropMask? _roundedMask;
    private Wuc.CompositionBrush? _maskedSourceBrush;
    private string? _lastDiagnostic;
    private bool _hasTheme;
    private Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop? _target;
    private Wuc.Compositor? _compositor;
    private IntPtr _hwnd;
    private Wuc.CompositionBrush? _currentBrush;
    private Wuc.CompositionBackdropBrush? _backdropBrush;
    private WindowMessageMonitor? _messageMonitor;
    private bool _hostBackdropCapabilityAvailable;
    private IntPtr _registeredHostBackdropHwnd;
    private SystemBackdropElement? _surfaceHost;
    private DispatcherQueue? _dispatcher;
    private BackdropTargetLifetime<ICompositionSupportsSystemBackdrop>? _targets;
    private int _ownerThread;
    private bool _detached;

    internal bool IsAvailable { get; private set; } = true;

    internal ThemeBackdrop(ThemeTarget themeTarget = ThemeTarget.Organizer, string surfaceName = "local", bool useRoundedMask = false)
    {
        _themeTarget = themeTarget;
        _surfaceName = surfaceName;
        _useRoundedMask = useRoundedMask;
    }

    internal void SetRoundedGeometry(RoundedSurfaceGeometry geometry)
    {
        _roundedGeometry = geometry;
        try
        {
            _roundedMask?.Update(geometry, _surfaceHost?.XamlRoot?.RasterizationScale ?? 1);
        }
        catch (Exception ex)
        {
            // Keep the material usable if the mask's device is lost during
            // a size/DPI update. The SDK's original rounded clip is the fallback.
            AppLogger.Error("收起背景圆角遮罩更新失败，恢复原生圆角裁剪。", ex);
            try
            {
                if (_maskedSourceBrush is not null && _target is not null)
                {
                    _target.SystemBackdrop = _maskedSourceBrush;
                    Wuc.CompositionBrush? wrapper = _currentBrush;
                    _currentBrush = _maskedSourceBrush;
                    _maskedSourceBrush = null;
                    ReleaseMaskResource(wrapper);
                }
                // If replacing the target failed, it still owns the wrapper;
                // keep its resources alive for the normal disconnect cleanup.
                if (_maskedSourceBrush is null)
                {
                    RoundedBackdropMask? failedMask = _roundedMask;
                    _roundedMask = null;
                    ReleaseMaskResource(failedMask);
                }
            }
            catch (Exception recoveryError)
            {
                AppLogger.Error("收起背景圆角遮罩降级时目标不可用，保留资源等待断开清理。", recoveryError);
            }
            finally
            {
                try
                {
                    if (_surfaceHost is not null) _surfaceHost.CornerRadius = new CornerRadius(geometry.Radius);
                }
                catch (Exception clipError) { AppLogger.Error("恢复原生圆角裁剪失败。", clipError); }
            }
            return;
        }
        if (_surfaceHost is not null)
            _surfaceHost.CornerRadius = new CornerRadius(_maskedSourceBrush is null ? geometry.Radius : 0);
    }

    private static void ReleaseMaskResource(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) { AppLogger.Error("释放圆角遮罩资源失败。", ex); }
    }

    // This explicit attachment restricts IClosable.Close to our local backdrop
    // links. Window-level backdrop targets are owned by WinUI and never enter here.
    internal void Attach(SystemBackdropElement surfaceHost)
    {
        if (_surfaceHost is not null || _detached)
            throw new InvalidOperationException("A ThemeBackdrop belongs to one local surface.");
        _surfaceHost = surfaceHost;
        _dispatcher = surfaceHost.DispatcherQueue;
        _ownerThread = Environment.CurrentManagedThreadId;
        _targets = new(() => _dispatcher.HasThreadAccess, QueueTargetCleanup, CloseTarget);
        _dispatcher.ShutdownStarting += Dispatcher_ShutdownStarting;
        surfaceHost.SystemBackdrop = this;
    }

    internal void DetachAndClose()
    {
        if (_detached || _surfaceHost is null) return;
        if (_dispatcher?.HasThreadAccess != true)
            throw new InvalidOperationException("Backdrop detachment requires the owning UI thread.");
        // Returning from this setter means SystemBackdropElement has finished
        // unlinking the target; draining is now safe even during app shutdown.
        if (ReferenceEquals(_surfaceHost.SystemBackdrop, this)) _surfaceHost.SystemBackdrop = null;
        _targets!.DrainDisconnected();
        if (_targets.RetainedCount != 0)
            throw new InvalidOperationException("The local backdrop still has connected targets.");
        _dispatcher.ShutdownStarting -= Dispatcher_ShutdownStarting;
        _surfaceHost = null;
        _detached = true;
    }

    private void Dispatcher_ShutdownStarting(DispatcherQueue sender, DispatcherQueueShutdownStartingEventArgs args)
    {
        try { DetachAndClose(); }
        catch (Exception ex) { LogLifetimeError("dispatcher-shutdown", null, ex); }
    }

    private bool QueueTargetCleanup(Action cleanup) => _dispatcher!.TryEnqueue(() =>
    {
        try { cleanup(); }
        catch (Exception ex)
        {
            LogLifetimeError("deferred-close", null, ex);
            // One queued retry, then an explicit drain at permanent close.
            // A failed Close never silently drops the last protected reference.
            if (!_dispatcher.TryEnqueue(() =>
            {
                try { _targets!.DrainDisconnected(); }
                catch (Exception retryError) { LogLifetimeError("close-retry", null, retryError); }
            })) LogLifetimeError("close-retry-not-queued", null, ex);
        }
    });

    private void CloseTarget(ICompositionSupportsSystemBackdrop target)
    {
        try
        {
            // The runtime class is not projected by the stable SDK. As<T>
            // queries its public IClosable interface (projected as IDisposable);
            // it does not dispose C#/WinRT's internal IObjectReference.
            target.As<IDisposable>().Dispose();
            AppLogger.Performance($"backdrop-close target={RuntimeHelpers.GetHashCode(target):X} thread={_ownerThread}");
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80000013))
        {
            // WinUI already closed this disconnected target.
        }
        catch (Exception ex)
        {
            LogLifetimeError("target-close", target, ex);
            throw;
        }
    }

    private void LogLifetimeError(string operation, ICompositionSupportsSystemBackdrop? target, Exception ex) =>
        AppLogger.Error($"Backdrop lifetime {operation}: target={(target is null ? "pending" : RuntimeHelpers.GetHashCode(target).ToString("X"))}, " +
            $"ownerThread={_ownerThread}, currentThread={Environment.CurrentManagedThreadId}, retained={_targets?.RetainedCount ?? 0}", ex);

    internal void SetTheme(ThemeValues theme, bool useEffects)
    {
        ThemeCompositionPlan next = ThemePalette.BuildCompositionPlan(theme, useEffects);
        if (_hasTheme && _theme == theme && _plan == next) return;
        _hasTheme = true;
        _theme = theme;
        _plan = next;
        if (_target is not null && _compositor is not null)
        {
            UpdateHostBackdropCapability();
            ReplaceConnectedBrush();
        }
    }

    protected override Wuc.CompositionBrush CreateBrush(Wuc.Compositor compositor)
    {
        _compositor = compositor;
        Wuc.CompositionBrush brush = ApplyRoundedMask(compositor, BuildBrush(compositor));
        _currentBrush = brush;
        return brush;
    }

    protected override void OnTargetConnected(
        Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop connectedTarget,
        Microsoft.UI.Xaml.XamlRoot xamlRoot)
    {
        if (_targets is null || _detached)
            throw new InvalidOperationException("Attach the backdrop to its local surface before connecting.");
        if (_targets.IsConnected(connectedTarget)) return;
        _targets.Connect(connectedTarget);
        _target = connectedTarget;
        try
        {
            IntPtr hwnd = (IntPtr)xamlRoot.ContentIslandEnvironment.AppWindowId.Value;
            _hwnd = hwnd;
            if (hwnd != IntPtr.Zero)
            {
                UpdateHostBackdropCapability();
                _messageMonitor?.Dispose();
                _messageMonitor = new WindowMessageMonitor(hwnd);
                _messageMonitor.WindowMessageReceived += MessageMonitor_WindowMessageReceived;
            }
            base.OnTargetConnected(connectedTarget, xamlRoot);
        }
        catch (Exception ex)
        {
            // Let the framework finish registering the connection so it will
            // disconnect it later, even if creating the material failed.
            LogLifetimeError("connect-material", connectedTarget, ex);
        }
    }

    protected override void OnTargetDisconnected(
        Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        if (_targets?.IsConnected(disconnectedTarget) != true) return;
        bool current = ReferenceEquals(_target, disconnectedTarget);
        Wuc.CompositionBrush? brush = current ? _currentBrush : null;
        bool brushReleasedByBase = false;
        try
        {
            bool installed = ReferenceEquals(disconnectedTarget.SystemBackdrop, brush);
            base.OnTargetDisconnected(disconnectedTarget);
            brushReleasedByBase = installed;
        }
        catch (Exception ex) { LogLifetimeError("disconnect-material", disconnectedTarget, ex); }
        finally
        {
            try
            {
                if (current)
                {
                    _target = null;
                    _compositor = null;
                    _currentBrush = null;
                    WindowMessageMonitor? monitor = _messageMonitor;
                    _messageMonitor = null;
                    Wuc.CompositionBackdropBrush? backdrop = _backdropBrush;
                    _backdropBrush = null;
                    Wuc.CompositionBrush? maskedSource = _maskedSourceBrush;
                    _maskedSourceBrush = null;
                    RoundedBackdropMask? roundedMask = _roundedMask;
                    _roundedMask = null;
                    if (monitor is not null)
                    {
                        monitor.WindowMessageReceived -= MessageMonitor_WindowMessageReceived;
                        ReleaseResource(monitor.Dispose);
                    }
                    ReleaseResource(DisableHostBackdropCapability);
                    _hwnd = IntPtr.Zero;
                    // Also covers a brush created before a failed attachment.
                    if (!brushReleasedByBase && brush is not null) ReleaseResource(brush.Dispose);
                    if (maskedSource is not null) ReleaseResource(maskedSource.Dispose);
                    if (roundedMask is not null) ReleaseResource(roundedMask.Dispose);
                    if (backdrop is not null) ReleaseResource(backdrop.Dispose);
                }
            }
            catch (Exception ex) { LogLifetimeError("disconnect-resources", disconnectedTarget, ex); }
            finally
            {
                try { _targets!.Disconnect(disconnectedTarget); }
                catch (Exception ex) { LogLifetimeError("queue-close", disconnectedTarget, ex); }
            }
        }

        void ReleaseResource(Action release)
        {
            try { release(); }
            catch (Exception ex) { LogLifetimeError("disconnect-resource", disconnectedTarget, ex); }
        }
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
            if (NativeMethods.IsWindow(registeredHwnd))
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
        Wuc.CompositionBrush? previousSource = _maskedSourceBrush;
        Wuc.CompositionBrush next = ApplyRoundedMask(_compositor, BuildBrush(_compositor));
        try
        {
            _target.SystemBackdrop = next;
        }
        catch
        {
            next.Dispose();
            _maskedSourceBrush?.Dispose();
            _backdropBrush?.Dispose();
            _maskedSourceBrush = previousSource;
            _backdropBrush = previousBackdrop;
            if (_useRoundedMask && _surfaceHost is not null)
                _surfaceHost.CornerRadius = new CornerRadius(previousSource is null ? _roundedGeometry.Radius : 0);
            throw;
        }
        _currentBrush = next;

        if (previous is not null && !ReferenceEquals(previous, next))
            previous.Dispose();
        previousSource?.Dispose();
        if (previousBackdrop is not null && !ReferenceEquals(previousBackdrop, _backdropBrush))
            previousBackdrop.Dispose();
        if (_maskedSourceBrush is null && _roundedMask is not null)
        {
            RoundedBackdropMask? unusedMask = _roundedMask;
            _roundedMask = null;
            ReleaseMaskResource(unusedMask);
        }
    }

    private Wuc.CompositionBrush ApplyRoundedMask(Wuc.Compositor compositor, Wuc.CompositionBrush source)
    {
        bool maskWasInUse = _maskedSourceBrush is not null;
        _maskedSourceBrush = null;
        if (!_useRoundedMask) return source;
        Wuc.CompositionMaskBrush? brush = null;
        try
        {
            _roundedMask ??= new RoundedBackdropMask(compositor);
            _roundedMask.Update(_roundedGeometry, _surfaceHost?.XamlRoot?.RasterizationScale ?? 1);
            brush = _roundedMask.Wrap(source);
            // The brush now owns the sole backdrop contour. Keep SDK target
            // ownership and rectangular bounds, without clipping its alpha twice.
            if (_surfaceHost is not null) _surfaceHost.CornerRadius = new CornerRadius(0);
            _maskedSourceBrush = source;
            return brush;
        }
        catch (Exception ex)
        {
            brush?.Dispose();
            if (!maskWasInUse)
            {
                RoundedBackdropMask? failedMask = _roundedMask;
                _roundedMask = null;
                ReleaseMaskResource(failedMask);
            }
            if (_surfaceHost is not null) _surfaceHost.CornerRadius = new CornerRadius(_roundedGeometry.Radius);
            AppLogger.Error("收起背景圆角遮罩不可用，保留原生圆角裁剪。", ex);
            return source;
        }
    }

    private Wuc.CompositionBrush BuildBrush(Wuc.Compositor compositor)
    {
        _backdropBrush = null;
        Wuc.CompositionBackdropBrush? backdrop = null;
        Wuc.CompositionEffectBrush? effectBrush = null;
        ThemeBackdropBranch branch = ThemeEffectGraph.ResolveBranch(_plan, _hostBackdropCapabilityAvailable);
        try
        {
            if (branch != ThemeBackdropBranch.Glass)
            {
                DisableHostBackdropCapability();
                return BuildColorBrush(compositor, branch);
            }

            backdrop = compositor.CreateHostBackdropBrush();
            IGraphicsEffect effect = ThemeEffectGraph.Create(
                _plan, new Wuc.CompositionEffectSourceParameter("Backdrop"));
            effectBrush = CreateEffectBrush(compositor, effect, backdrop);
            _backdropBrush = backdrop;
            IsAvailable = true;
            LogBranch(ThemeBackdropBranch.Glass);
            return effectBrush;
        }
        catch (Exception ex)
        {
            effectBrush?.Dispose();
            backdrop?.Dispose();
            _backdropBrush = null;
            AppLogger.Error("HostBackdrop 玻璃效果不可用，已切换为遵守不透明度的主题色。", ex);
            DisableHostBackdropCapability();
            return BuildColorBrush(compositor, ThemeBackdropBranch.Fallback);
        }
    }

    private Wuc.CompositionBrush BuildColorBrush(Wuc.Compositor compositor, ThemeBackdropBranch branch)
    {
        IsAvailable = branch != ThemeBackdropBranch.Fallback;
        Wuc.CompositionBrush brush = compositor.CreateColorBrush(
            ThemePalette.WithOpacity(_plan.TintColor, _plan.TintOpacity));
        LogBranch(branch);
        return brush;
    }

    private void LogBranch(ThemeBackdropBranch branch)
    {
        // This identifies the selected target and actual path without logging
        // pointer events, file names or every duplicate ThemeChanged broadcast.
        string diagnostic = FormattableString.Invariant($"theme-backdrop target={_themeTarget} surface={_surfaceName} hwnd=0x{_hwnd.ToInt64():X} color=#{_theme.ColorArgb:X8} opacity={_plan.TintOpacity:0.####} blurStrength={GlobalSettings.NormalizeThemeBlurStrength(_theme.BlurStrength):0.##} sigma={(branch == ThemeBackdropBranch.Glass ? _plan.BlurAmount : 0):0.##} solid={_theme.SolidColorMode} effects={_plan.UseEffects} branch={branch}");
        if (diagnostic == _lastDiagnostic) return;
        _lastDiagnostic = diagnostic;
        AppLogger.Info(diagnostic);
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
