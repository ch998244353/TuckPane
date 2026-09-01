namespace TuckPane.Models;

internal static class OrganizerLimits
{
    internal const int MaximumOrganizers = 50;
    internal const int MaximumStations = 4;
    internal const double MinimumCompactScale = 1.2;
    internal const double DefaultCompactScale = 1.56;
    internal const double MaximumCompactScale = 3;
    internal const double MaximumPositionedCompactScale = 1.8;
    internal const double CompactWindowWidthDip = 76;
    internal const double CompactWindowHeightDip = 68;
    internal const int MinimumGridDimension = 2;
    internal const int MinimumStationRows = 1;
    internal const int MinimumStationColumns = 1;
    internal const int MaximumLayoutDimension = 6;
    internal const int MaximumStationRows = 9;
    internal const int MaximumStationColumns = 9;

    internal static double CalculateCompactPreviewIconFraction(double itemScale)
    {
        double normalized = Math.Clamp(itemScale, .5, 1.65);
        return normalized <= 1
            ? .5 + (normalized - .5) * .4
            : .7 + (normalized - 1) / .65 * .25;
    }
}
