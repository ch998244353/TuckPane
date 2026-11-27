using TuckPane.Core;
using Windows.Foundation;
using Size = Windows.Foundation.Size;

internal static class FileMenuPlacementChecks
{
    internal static void Run()
    {
        Rect work = new(0, 0, 1000, 800);
        Size menu = new(240, 320);
        var cases = new (string Name, Rect Item, Rect Work, Size Menu, double Scale,
            Rect Expected, bool Left, bool Up, double MaximumHeight)[]
        {
            ("right below", new(100, 100, 100, 80), work, menu, 1, new(206, 186, 240, 320), false, false, 614),
            ("right edge", new(900, 50, 100, 80), work, menu, 1, new(654, 136, 240, 320), true, false, 664),
            ("left edge", new(0, 100, 100, 80), work, menu, 1, new(106, 186, 240, 320), false, false, 614),
            ("bottom edge", new(100, 700, 100, 80), work, menu, 1, new(206, 374, 240, 320), false, true, 694),
            ("tall below", new(100, 100, 100, 80), work, new(240, 1000), 1, new(206, 186, 240, 614), false, false, 614),
            ("tall above", new(100, 600, 100, 80), work, new(240, 1000), 1, new(206, 0, 240, 594), false, true, 594),
            ("equal space prefers below", new(100, 370, 100, 60), work, new(240, 1000), 1, new(206, 436, 240, 364), false, false, 364),
            ("negative monitor", new(-200, 0, 100, 80), new(-1920, -200, 1920, 1080), menu, 1, new(-446, 86, 240, 320), true, false, 794),
            ("125 percent DPI", new(125, 125, 125, 100), new(0, 0, 1250, 1000), new(300, 400), 1.25, new(257.5, 232.5, 300, 400), false, false, 767.5),
            ("150 percent DPI", new(150, 150, 150, 120), new(0, 0, 1500, 1200), new(360, 480), 1.5, new(309, 279, 360, 480), false, false, 921),
            ("content zoom already in bounds", new(100, 100, 150, 120), work, menu, 1, new(256, 226, 240, 320), false, false, 574),
            ("wide horizontal clamp", new(400, 100, 100, 80), work, new(800, 320), 1, new(0, 186, 800, 320), true, false, 614),
            ("wider than work area", new(400, 100, 100, 80), work, new(1200, 320), 1, new(0, 186, 1000, 320), true, false, 614)
        };

        foreach (var c in cases)
        {
            var result = FileMenuPlacement.Calculate(c.Item, c.Work, c.Menu, c.Scale);
            Equal(result.Bounds, c.Expected, c.Name);
            Require(result.OpensLeft == c.Left && result.OpensUp == c.Up, c.Name + ": wrong direction");
            Near(result.MaximumHeight, c.MaximumHeight, c.Name + ": available height");
            Rect b = result.Bounds;
            Require(b.Left >= c.Work.Left && b.Top >= c.Work.Top && b.Right <= c.Work.Right && b.Bottom <= c.Work.Bottom,
                c.Name + ": menu exceeds work area");
            Require(b.Right <= c.Item.Left || b.Left >= c.Item.Right || b.Bottom <= c.Item.Top || b.Top >= c.Item.Bottom,
                c.Name + ": menu overlaps item");
            double gap = 6 * c.Scale;
            Equal(result.Exclusion, new(c.Item.X - gap, c.Item.Y - gap, c.Item.Width + 2 * gap, c.Item.Height + 2 * gap),
                c.Name + ": exclusion");
            Require(c.Up ? b.Bottom <= c.Item.Top - gap : b.Top >= c.Item.Bottom + gap, c.Name + ": vertical gap");
        }

        Equal(FileMenuPlacement.ToLocal(new(-450, 150, 300, 240), new(-600, 0), 1.5),
            new(100, 100, 200, 160), "screen to root DIP");
        Throws<InvalidOperationException>(() => FileMenuPlacement.Calculate(work, work, menu, 1), "no vertical space");
        Rect item = new(100, 100, 100, 80);
        foreach (double scale in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
            Throws<ArgumentOutOfRangeException>(() => FileMenuPlacement.Calculate(item, work, menu, scale), "invalid scale");
        Throws<ArgumentOutOfRangeException>(() => FileMenuPlacement.Calculate(new(0, 0, 0, 80), work, menu, 1), "empty item");
        Throws<ArgumentOutOfRangeException>(() => FileMenuPlacement.Calculate(item, new(0, 0, 0, 800), menu, 1), "empty work area");
        Throws<ArgumentOutOfRangeException>(() => FileMenuPlacement.Calculate(item, work, new(240, 0), 1), "empty menu");
        Throws<ArgumentOutOfRangeException>(() => FileMenuPlacement.Calculate(item, work, new(double.NaN, 320), 1), "nonfinite menu");
        Console.WriteLine($"PASS: file menu placement ({cases.Length} geometry cases, DIP conversion and invalid inputs; no UI or Shell execution)");
    }

    private static void Equal(Rect actual, Rect expected, string name)
    {
        Near(actual.X, expected.X, name + ": X");
        Near(actual.Y, expected.Y, name + ": Y");
        Near(actual.Width, expected.Width, name + ": width");
        Near(actual.Height, expected.Height, name + ": height");
    }

    private static void Near(double actual, double expected, string name) =>
        Require(Math.Abs(actual - expected) < 0.0001, $"{name}: expected {expected}, got {actual}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(name + ": expected " + typeof(T).Name);
    }
}
