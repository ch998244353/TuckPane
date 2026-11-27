namespace TuckPane.Core;

internal static class TodoRowMetrics
{
    // Row padding is 8 + 4; the checkbox target is at least 20 with a 1 DIP top margin.
    internal static double SingleLineHeight(double fontSize, double textLineHeight, bool showSeparators) =>
        Math.Max(textLineHeight, Math.Max(20, fontSize * .8) + 1) + 12 + (showSeparators ? 1 : 0);
}
