using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class MainWindow : IExpansionModeView
{
    private bool _changingExpansionMode;
    private bool _pendingExpansionModeApply;
    private long _transitionRequestVersion;
    private long _modeVisibilityVersion;
    private int _modeInteractionOperations;
    private readonly ExpansionHoverGuard _modeHoverGuard = new();
    internal bool IsPermanentlyExpanded => OrganizerExpansion.IsPermanent(_definition);
    private Task ExpandAsyncForMode() => ApplyExpansionModeAsync();

    private bool ExpansionModeInteractionBusy => _pressActive || _widgetDragging || _nativeMouseCapture ||
        _canvasResize is not null || _widgetGesture.IsCompleting || _resizeGesture.IsCompleting ||
        _shellDragActive || _shellDropFinalizing || _shellPromotionPending || _itemReorderSession is not null ||
        _modeInteractionOperations > 0 || _dragHasLocalFile;

    void IExpansionModeView.Begin(OrganizerExpansionMode mode)
    {
        _changingExpansionMode = true;
        ++_transitionRequestVersion;
        _transitionCancellation?.Cancel();
        _outsideClickHook?.Stop();
        _externalHoverTimer.Stop();
        _hoverExpandScrollToEnd = false;
        _ordinaryOutsideSince = 0;
        if (mode == OrganizerExpansionMode.Collapsible) _modeHoverGuard.SuppressUntilExit();
        UpdateExpansionModeControls();
        RefreshPerformanceSettings();
    }

    async Task IExpansionModeView.WaitUntilReadyAsync(CancellationToken token)
    {
        await InitializeAsync().WaitAsync(token);
        // This short wait exists only while a requested mode is pending. It observes
        // gesture ownership, including asynchronous save/drop cleanup, not mouse input.
        while (ExpansionModeInteractionBusy) await Task.Delay(25, token);
        token.ThrowIfCancellationRequested();
        if (_closing) throw new OperationCanceledException(token);
        if (!OrganizerExpansion.CanEnable(_definition))
            throw new InvalidOperationException(AppStrings.Get("ExpansionModeUnavailable"));
        if (_expanded && !_animating && _transitionProgress >= 1 &&
            TryGetLogicalWindowRect(out NativeMethods.RECT bounds))
            _definition.ExpandedPosition = DisplayPlacementService.Capture(bounds, _hwnd);
        ClearWindowAlignment();
    }

    async Task IExpansionModeView.ApplyAsync(OrganizerExpansionMode mode, CancellationToken token)
    {
        while (ExpansionModeInteractionBusy) await Task.Delay(25, token);
        token.ThrowIfCancellationRequested();
        if (!OrganizerExpansion.CanEnable(_definition))
            throw new InvalidOperationException(AppStrings.Get("ExpansionModeUnavailable"));
        UpdateExpansionModeControls();
        if (!_runtimeVisible)
        {
            _pendingExpansionModeApply = true;
            return;
        }
        _pendingExpansionModeApply = false;
        long visibilityVersion = _modeVisibilityVersion;
        if (mode == OrganizerExpansionMode.AlwaysExpanded)
        {
            _host.NotifyPermanentExpanded(this);
            await ExpandTransitionAsync(forMode: true, modeCancellation: token);
        }
        else await CollapseTransitionAsync(forMode: true, modeCancellation: token);
        token.ThrowIfCancellationRequested();
        if (!_runtimeVisible || visibilityVersion != _modeVisibilityVersion)
        {
            _pendingExpansionModeApply = true;
            return;
        }
        if (!_closing && (_animating || _expanded != (mode == OrganizerExpansionMode.AlwaysExpanded) ||
            _transitionProgress != (mode == OrganizerExpansionMode.AlwaysExpanded ? 1 : 0)))
            throw new InvalidOperationException("The organizer did not settle in the requested expansion mode.");
        if (_closing) return;
        // A hidden window can already have the requested endpoint. The transition's
        // idempotent fast path must still restore native visibility when shown again.
        NativeMethods.RECT visibleBounds = _compactBounds;
        if (_expanded && !TryGetLogicalWindowRect(out visibleBounds))
            visibleBounds = CalculateExpandedBounds(_compactBounds);
        ApplyBounds(visibleBounds, show: true, preserveZOrder: true);
        _desktopLayer?.SetExpanded(_expanded, showWindow: false);
        UpdateCanvasResizeEdgeWindows(show: _expanded);
        if (_expanded) _canvasResizeInputTimer.Start();
        else
        {
            _canvasResizeInputTimer.Stop();
            _host.NotifyCollapsed(this);
        }
    }

    void IExpansionModeView.Restore(OrganizerExpansionMode mode)
    {
        if (_closing) return;
        ++_transitionRequestVersion;
        _transitionCancellation?.Cancel();
        _animating = false;
        _transitionVelocity = 0;
        if (!_runtimeVisible || IsContained || _hwnd == IntPtr.Zero)
        {
            _pendingExpansionModeApply = true;
            return;
        }
        _expanded = mode == OrganizerExpansionMode.AlwaysExpanded && OrganizerExpansion.CanEnable(_definition);
        _transitionProgress = _expanded ? 1 : 0;
        NativeMethods.RECT bounds = _collapseTransitionGeometry?.ExpandedBounds ?? CalculateExpandedBounds(_compactBounds);
        _collapseTransitionGeometry = null;
        CompactView.Visibility = _expanded ? Visibility.Collapsed : Visibility.Visible;
        CompactView.Translation = Vector3.Zero;
        CompactView.Opacity = 1;
        ExpandedView.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandedView.Opacity = _expanded ? 1 : 0;
        GetExpandedCompositionVisual().Scale = Vector3.One;
        ApplyBounds(_expanded ? bounds : _compactBounds, show: true, preserveZOrder: true);
        _desktopLayer?.SetExpanded(_expanded, showWindow: false);
        UpdateCanvasResizeEdgeWindows(show: _expanded);
        if (_expanded)
        {
            _host.NotifyPermanentExpanded(this);
            _canvasResizeInputTimer.Start();
        }
        else
        {
            _canvasResizeInputTimer.Stop();
            _host.NotifyCollapsed(this);
        }
    }

    void IExpansionModeView.End()
    {
        _changingExpansionMode = false;
        if (_closing) return;
        UpdateExpansionModeControls();
        ApplyOutsideClickSetting();
        RefreshPerformanceSettings();
    }

    internal async Task ApplyExpansionModeAsync()
    {
        do
        {
            _ = await _host.SetOrganizerExpansionModeAsync(OrganizerId, _host.GetRequestedExpansionMode(OrganizerId));
        }
        while (!_closing && _runtimeVisible && _pendingExpansionModeApply);
    }

    private bool IsCurrentTransitionRequest(long version, CancellationToken modeToken) =>
        !_closing && !modeToken.IsCancellationRequested && version == _transitionRequestVersion &&
        (_definition.PlacementMode == OrganizerPlacementMode.Station ? _stationVisible : _runtimeVisible || IsContained);

    private IDisposable HoldExpansionModeInteraction()
    {
        _modeInteractionOperations++;
        return new ExpansionModeInteractionHold(this);
    }

    private sealed class ExpansionModeInteractionHold(MainWindow window) : IDisposable
    {
        public void Dispose() => window._modeInteractionOperations--;
    }

    private void UpdateExpansionModeControls()
    {
        bool station = !OrganizerKinds.IsRegular(_definition.PlacementMode);
        bool permanent = _host.GetRequestedExpansionMode(OrganizerId) == OrganizerExpansionMode.AlwaysExpanded;
        CollapseButton.Visibility = station || permanent ? Visibility.Collapsed : Visibility.Visible;
        OrganizerExpansionMode target = permanent ? OrganizerExpansionMode.Collapsible : OrganizerExpansionMode.AlwaysExpanded;
        foreach (MenuFlyoutItem item in new[] { CompactToggleExpansionModeMenuItem, ExpandedToggleExpansionModeMenuItem })
        {
            item.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
            item.IsEnabled = OrganizerExpansion.CanEnable(_definition);
            item.Tag = target;
            item.Text = AppStrings.Get(permanent ? "ContextSwitchToCollapsible" : "ContextSwitchToAlwaysExpanded");
            item.FontFamily = new FontFamily(AppStrings.FontFamily);
            item.CharacterSpacing = AppStrings.CharacterSpacing;
        }
        RefreshOrganizerMenuVisibility();
    }

    private async void ToggleExpansionModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: OrganizerExpansionMode target }) return;
        await RunSafelyAsync(async () =>
        {
            string? error = await _host.SetOrganizerExpansionModeAsync(OrganizerId, target);
            if (error is not null) ShowMessage(error, InfoBarSeverity.Warning);
        }, "切换展开模式失败");
    }
}
