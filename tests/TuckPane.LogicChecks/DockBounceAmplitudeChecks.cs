using TuckPane.Core;

internal static class DockBounceAmplitudeChecks
{
    internal static void Run()
    {
        var dock = new ItemBounceMotion();
        var grid = new ItemBounceMotion();
        dock.Restart(64, isDock: true);
        grid.Restart(64);
        float envelopePeak = 64 * .18f * dock.AmplitudeMultiplier;
        foreach (var sample in new[] {
                     (Seconds: .12, GridPeak: -11.52f, Ratio: 1f),
                     (Seconds: .22, GridPeak: -6.4f, Ratio: .10f / .18f),
                     (Seconds: .18, GridPeak: -3.2f, Ratio: .05f / .18f) })
        {
            dock.Step(sample.Seconds);
            grid.Step(sample.Seconds);
            Equal(grid.Offset, sample.GridPeak, "Ordinary icon peak stays unchanged");
            foreach (float scale in new[] { 1f, 1.25f, 1.75f })
            {
                // Rendered, scaled bounds before bounce; the result is a local offset.
                var icon = new HoverRect(120, 112, 64 * scale, 64 * scale);
                float travel = icon.Y - 100 + icon.Height / 3;
                float offset = DockVisualMath.BounceOffset(dock.Offset, envelopePeak, icon, 100, 0, scale);
                Equal(offset * scale, -travel * sample.Ratio, "Horizontal peaks preserve the three envelope ratios");
                if (sample.Ratio == 1)
                    Equal(100 - (icon.Y + offset * scale), icon.Height / 3, "First peak exposes one third of displayed icon height");
                float limited = DockVisualMath.BounceOffset(dock.Offset, envelopePeak, icon, 100, 105, scale);
                Equal(limited * scale, -7 * sample.Ratio, "Screen top scales the complete envelope");
            }
        }
        var surface = new HoverRect(0, 0, 320, 96);
        var tightIcon = new HoverRect(120, 8, 80, 80);
        Equal(ItemBounceMotion.FitOffsetToBounds(-envelopePeak, 64, 1.25f,
            tightIcon, surface, surface, 12, dock.AmplitudeMultiplier), -6.4f, "Vertical Dock keeps its top boundary");
        var roomySurface = new HoverRect(0, 0, 320, 220);
        Equal(ItemBounceMotion.FitOffsetToBounds(-11.52f, 64, 1,
            new HoverRect(120, 60, 64, 64), roomySurface, roomySurface, 12), -11.52f, "Ordinary icon amplitude remains unchanged");
        Console.WriteLine("PASS --dock-bounce-amplitude: horizontal one-third peak, scaled decay and screen limit; vertical/non-Dock controls.");
    }

    private static void Equal(float actual, float expected, string message)
    {
        if (!float.IsFinite(actual) || Math.Abs(actual - expected) > .001f)
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}.");
    }
}
