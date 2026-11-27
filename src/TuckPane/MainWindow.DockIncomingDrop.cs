using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Numerics;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private DockIncomingGap? _dockIncomingGap;
    private object? _dockIncomingOwner;
    private readonly object _dockFileDragOwner = new();
    private bool _dockIncomingRendering;
    private long _dockIncomingLastFrame;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dockIncomingGuard;
    private bool _dockIncomingMouseButtonSeen;
    private int DockDisplayCount => _items.Count + (_dockIncomingGap is null ? 0 : 1);
    private bool IsReturningContainedDockDrag => IsDock && _itemReorderSession is { NativeDragStarted: true } &&
        _items.Any(item => item.RelativeName == _draggedRelativeName && item.Kind == WidgetItemKind.Organizer);

    private void UpdateDockFileGap(DragEventArgs e)
    {
        if (!IsDock || _draggedRelativeName is not null || !_dragHasLocalFile) return;
        if (_dockIncomingGap?.Frozen == true)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }
        if (OrganizerInteractionMath.SelectDropOperation(e.AllowedOperations) ==
            Windows.ApplicationModel.DataTransfer.DataPackageOperation.None) return;
        UpdateIncomingDockGap(e.GetPosition(ExpandedView), _dockFileDragOwner);
    }

    internal void UpdateIncomingDockGap(NativeMethods.POINT screenPoint, object owner)
    {
        if (!IsDock || !NativeMethods.ScreenToClient(_hwnd, ref screenPoint)) return;
        double scale = Math.Max(1, WindowRoot.XamlRoot?.RasterizationScale ?? 1);
        if (ReferenceEquals(owner, this) && IsReturningContainedDockDrag && _itemReorderSession is { } session)
        {
            Point contentOrigin = ItemsRepeater.TransformToVisual(WindowRoot).TransformPoint(new Point());
            session.MarkInternalPreview();
            session.UpdateTarget(new Point(screenPoint.X / scale - contentOrigin.X, screenPoint.Y / scale - contentOrigin.Y),
                _nativeDragCellWidth, _nativeDragCellHeight, GetItemLayoutGapDip(), GetItemLayoutColumnCount(), _items.Count);
            _ = StartGapTransitionIfReady(session, Stopwatch.GetTimestamp());
            StartNativeItemMotionRendering();
            return;
        }
        Point origin = ExpandedView.TransformToVisual(WindowRoot).TransformPoint(new Point());
        UpdateIncomingDockGap(new Point(screenPoint.X / scale - origin.X, screenPoint.Y / scale - origin.Y), owner);
    }

    private void UpdateIncomingDockGap(Point point, object owner)
    {
        if (!IsDock || _closing || _itemReorderSession is not null || _dockIncomingGap?.Frozen == true) return;
        var local = new Vector2((float)point.X, (float)point.Y);
        if (_dockIncomingGap is null)
        {
            ResetHoverWave();
            _dockIncomingGap = new DockIncomingGap(_dockGeometry.InsertionIndex(local));
            _dockIncomingOwner = owner;
            if (ReferenceEquals(owner, _dockFileDragOwner)) StartDockIncomingGuard();
            ApplyDockLayout();
            ApplyAllProvisionalItemVisuals(animate: UseCustomAnimations);
        }
        else if (ReferenceEquals(_dockIncomingOwner, owner) && _dockIncomingGap.Update(local, _dockGeometry, _items.Count))
            ApplyAllProvisionalItemVisuals(animate: UseCustomAnimations);
    }

    private DockGapAnchor? FreezeDockFileGap(DragEventArgs e, out DockIncomingGap? token)
    {
        token = null;
        if (!IsDock || _dockIncomingGap?.Frozen == true) return null;
        UpdateDockFileGap(e);
        token = _dockIncomingGap;
        return token?.Freeze(_items.Select(item => item.RelativeName).ToArray());
    }

    internal void ClearIncomingDockGap(object? owner = null, bool refresh = true)
    {
        if ((owner is null || ReferenceEquals(owner, this)) && IsReturningContainedDockDrag &&
            _itemReorderSession is { } session)
        {
            session.LeaveInternalPreview();
            ResetGapTransitionState();
            ApplyAllProvisionalItemVisuals(animate: false);
        }
        if (_dockIncomingGap is null || owner is not null &&
            !ReferenceEquals(owner, _dockIncomingOwner) && !ReferenceEquals(owner, _dockIncomingGap)) return;
        if (ReferenceEquals(_dockIncomingOwner, _dockFileDragOwner)) _dragHasLocalFile = false;
        _dockIncomingGuard?.Stop();
        _dockIncomingGap = null;
        _dockIncomingOwner = null;
        StopDockIncomingRendering();
        foreach (Border host in _itemMotionStates.Keys.ToArray()) ResetItemMotion(host);
        if (!_closing)
        {
            ApplyDockLayout();
            InvalidateHoverGeometry();
            if (refresh) _ = StartCatalogRefreshIfReady();
        }
    }

    private void LeaveDockFileGap(DragEventArgs e)
    {
        // Routed child DragLeave events also reach WindowRoot; only clear when
        // the pointer has really left the receiving surface.
        if (_dockIncomingGap?.Frozen != false) return;
        Point point = e.GetPosition(ExpandedView);
        if (DockFileDragEnded() || !_dockGeometry.Contains(new Vector2((float)point.X, (float)point.Y)))
            ClearIncomingDockGap(_dockFileDragOwner);
    }

    private bool DockFileDragEnded() => (NativeMethods.GetAsyncKeyState(0x1B) & 0x8000) != 0 ||
        _dockIncomingMouseButtonSeen && (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) == 0;

    private void StartDockIncomingGuard()
    {
        _dockIncomingMouseButtonSeen = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        if (_dockIncomingGuard is null)
        {
            _dockIncomingGuard = DispatcherQueue.CreateTimer();
            _dockIncomingGuard.Interval = TimeSpan.FromMilliseconds(60);
            _dockIncomingGuard.Tick += (_, _) =>
            {
                if (_dockIncomingGap?.Frozen != false) return;
                _dockIncomingMouseButtonSeen |= (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
                if (DockFileDragEnded()) ClearIncomingDockGap(_dockFileDragOwner);
            };
        }
        _dockIncomingGuard.Start();
    }

    private bool ApplyIncomingDockVisual(Border host, int index, bool animate)
    {
        if (_dockIncomingGap is not { } gap) return false;
        float distance = index >= gap.Index ? (float)_dockGeometry.Pitch : 0;
        ItemMotionState motion = GetItemMotion(host);
        motion.TranslationTarget = _definition.DockOrientation == DockOrientation.Horizontal
            ? new Vector3(distance, 0, 0) : new Vector3(0, distance, 0);
        host.Scale = motion.Scale = motion.ScaleTarget = Vector3.One;
        if (!animate || !UseCustomAnimations)
        {
            host.Translation = motion.Translation = motion.TranslationTarget;
            motion.TranslationVelocity = Vector3.Zero;
        }
        else if (!_dockIncomingRendering)
        {
            _dockIncomingLastFrame = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += DockIncomingRendering;
            _dockIncomingRendering = true;
        }
        return true;
    }

    private void DockIncomingRendering(object? sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = Math.Clamp(Stopwatch.GetElapsedTime(_dockIncomingLastFrame, now).TotalSeconds, 0, .05);
        _dockIncomingLastFrame = now;
        bool settled = true;
        foreach ((Border host, ItemMotionState motion) in _itemMotionStates)
        {
            settled &= ItemMotionMath.StepCriticalSpring(ref motion.Translation, ref motion.TranslationVelocity,
                motion.TranslationTarget, seconds);
            host.Translation = motion.Translation;
        }
        if (settled || _dockIncomingGap is null || _closing) StopDockIncomingRendering();
    }

    private void StopDockIncomingRendering()
    {
        if (_dockIncomingRendering) CompositionTarget.Rendering -= DockIncomingRendering;
        _dockIncomingRendering = false;
    }

    private async Task CommitDockIncomingFilesAsync(DockGapAnchor anchor, IReadOnlyList<TransferOutcome> outcomes)
    {
        List<string> previous = _definition.ItemOrder.ToList();
        _definition.ItemOrder = anchor.Insert(previous, DockGapAnchor.SuccessfulNames(outcomes));
        try { await _host.SaveStateAsync(); }
        catch
        {
            _definition.ItemOrder = previous;
            throw;
        }
    }
}
