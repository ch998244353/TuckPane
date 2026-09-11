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
        double scale = ContentScale;
        var cell = DisplayPlacementService.CalculateItemCellSizeDip(width / scale, height / scale, _definition.Layout);
        return (cell.Width * scale, cell.Height * scale);
    }
}
