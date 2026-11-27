using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class MainWindow
{
    private bool _unifiedWheelEdit;
    private TaskCompletionSource? _unifiedResizeCompletion;
    internal bool IsResizingExpanded => _canvasResize is not null;
    internal Task WaitForUnifiedResizeAsync() => _unifiedResizeCompletion?.Task ?? Task.CompletedTask;

    private void CompleteUnifiedResize()
    {
        if (_unifiedResizeCompletion is null) return;
        try { _host.CommitUnifiedExpandedEdit(_definition); }
        finally
        {
            _unifiedResizeCompletion.TrySetResult();
            _unifiedResizeCompletion = null;
        }
    }

    internal DisplayInfo UnifiedExpandedDisplay => _expanded && TryGetLogicalWindowRect(out var bounds)
        ? DisplayPlacementService.ForBounds(bounds)
        : ShouldRememberExpandedPosition() && _definition.ExpandedPosition is { } saved
            ? DisplayPlacementService.GetDisplay(saved.MonitorDevice)
            : DisplayPlacementService.ForBounds(_compactBounds);

    internal void RefreshUnifiedExpandedSize()
    {
        if (_closing || _canvasResize is not null) return;
        _contentDisplayFit = 1;
        ApplyDefinition(OrganizerVisualChange.CanvasScale | OrganizerVisualChange.ItemScale | OrganizerVisualChange.CompactListItemScale);
    }

    private void BeginUnifiedWheelEdit()
    {
        if (_unifiedWheelEdit || !_host.UsesUnifiedExpanded(_definition)) return;
        _host.BeginUnifiedExpandedEdit(_definition);
        _unifiedWheelEdit = true;
    }

    internal void CommitUnifiedWheelEdit()
    {
        if (!_unifiedWheelEdit) return;
        _unifiedWheelEdit = false;
        _host.CommitUnifiedExpandedEdit(_definition);
    }
}
