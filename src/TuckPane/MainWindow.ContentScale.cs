using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class MainWindow
{
    private double _contentDisplayFit = 1;
    private double ContentScale => !OrganizerKinds.IsRegular(_definition.PlacementMode) ? 1 :
        OrganizerContentScale.Normalize(IsCompactList ? _definition.CompactListContentScale : _definition.IconContentScale) * _contentDisplayFit;

    private (double Width, double Height) GetScaledGridCellSize(double width, double height)
    {
        if (_canvasResize is null && _host.UsesUnifiedExpanded(_definition))
        {
            double unifiedCell = _host.State.GlobalSettings.UniformIconExpandedCellDip;
            return (unifiedCell, unifiedCell);
        }
        return DisplayPlacementService.CalculateScaledItemCellSizeDip(width, height, _definition.Layout, ContentScale);
    }
}
