using System.Runtime.InteropServices;
using TuckPane.Core;
using TuckPane.Services;
using Windows.Foundation;
using Point = Windows.Foundation.Point;

internal static class FileMenuCursorPlacementChecks
{
    internal static void Run()
    {
        CheckOrdinary();
        CheckEdges();
        CheckPhysicalCursorAndDpi();
        CheckCursorBitmaps();
        CheckFallback();
        CheckKeyboardAndCapture();
        Console.WriteLine("PASS file menu cursor placement: 6 pure-function groups; anchor/exclusion/native parameters only, no GUI, mouse access or native Shell acceptance.");
    }

    private static void CheckOrdinary()
    {
        var popup = FileMenuPosition.Pointer(new(100, 100), new(100, 100, 24, 32), new(0, 0, 1000, 800), 1);
        Equal(popup.Position, new(128, 136), "ordinary lower-right anchor");
        Rect exclusion = Exclusion(popup);
        Equal(exclusion, new(96, 96, 32, 40), "complete cursor plus four-pixel gap");
        Flags(popup);
        var parameters = ShellMenuNative.CreatePopupParameters(exclusion);
        Require(parameters.Size == (uint)Marshal.SizeOf<ShellMenuNative.PopupParameters>() &&
            parameters.Exclude.Left == 96 && parameters.Exclude.Top == 96 &&
            parameters.Exclude.Right == 128 && parameters.Exclude.Bottom == 136,
            "TPMPARAMS must contain its native size and the complete exclusion rectangle.");
        var fractional = FileMenuPosition.Pointer(new(100, 100), new(100, 100, 24, 32), new(0, 0, 1000, 800), 1.1);
        Equal(Exclusion(fractional), new(95, 95, 34, 42), "fractional DPI gap rounds outward");
        Equal(fractional.Position, new(129, 137), "fractional DPI lower-right anchor");
    }

    private static void CheckEdges()
    {
        Rect work = new(0, 0, 1000, 800);
        var cases = new (Point Pointer, Point Anchor)[]
        {
            (new(985, 100), new(999, 134)),
            (new(100, 789), new(127, 799)),
            (new(985, 789), new(999, 799))
        };
        foreach (var c in cases)
        {
            Rect cursor = new(c.Pointer.X - 1, c.Pointer.Y - 2, 24, 32);
            var popup = FileMenuPosition.Pointer(c.Pointer, cursor, work, 1);
            Equal(popup.Position, c.Anchor, "right/bottom/corner anchor remains on its monitor");
            Equal(Exclusion(popup), new(cursor.X - 4, cursor.Y - 4, 32, 40), "edge exclusion must not be clipped");
            Flags(popup);
        }
    }

    private static void CheckPhysicalCursorAndDpi()
    {
        Point position = new(-1800, -100);
        Rect cursor = FileMenuPosition.CursorBounds(position, 48, 60, 6, 9, false)
            ?? throw new InvalidOperationException("A valid physical cursor was rejected.");
        Equal(cursor, new(-1806, -109, 48, 60), "physical bitmap and hotspot");
        var popup = FileMenuPosition.Pointer(position, cursor, new(-1920, -200, 1920, 1080), 1.5);
        Equal(Exclusion(popup), new(-1812, -115, 60, 72), "150% scales the gap only");
        Equal(popup.Position, new(-1752, -43), "negative-origin lower-right anchor");
        Flags(popup);
    }

    private static void CheckCursorBitmaps()
    {
        Rect monochrome = FileMenuPosition.CursorBounds(new(100, 200), 32, 64, 3, 5, true)
            ?? throw new InvalidOperationException("A valid monochrome cursor was rejected.");
        Equal(monochrome, new(97, 195, 32, 32), "monochrome stacked masks use half height");
        var invalid = new (int Width, int Height, uint HotX, uint HotY, bool Mono)[]
        {
            (0, 32, 0, 0, false), (-1, 32, 0, 0, false), (32, 0, 0, 0, false),
            (32, -1, 0, 0, false), (32, 65, 0, 0, true), (32, 1, 0, 0, true),
            (32, 32, 32, 0, false), (32, 64, 0, 32, true), (32, 32, uint.MaxValue, 0, false)
        };
        foreach (var c in invalid)
            Require(FileMenuPosition.CursorBounds(new(100, 200), c.Width, c.Height, c.HotX, c.HotY, c.Mono) is null,
                "Invalid bitmap dimensions or out-of-bounds hotspots must select fallback.");
    }

    private static void CheckFallback()
    {
        Point position = new(100, 200);
        Equal(FileMenuPosition.FallbackCursorBounds(position, 32, 24), new(68, 176, 64, 48),
            "unknown hotspot must allow the full cursor dimension on every side");
        Equal(FileMenuPosition.FallbackCursorBounds(position, 0, -1), new(99, 199, 2, 2),
            "invalid system dimensions retain a nonempty conservative exclusion");
    }

    private static void CheckKeyboardAndCapture()
    {
        Rect item = new(20, 30, 80, 40);
        Point captured = FileMenuPosition.Capture(null, item, new(100, 200), 1.5);
        Equal(captured, new(130, 305), "keyboard retains item left/bottom position");
        foreach (uint alignment in new uint[] { 0, 0x8 })
        {
            var popup = FileMenuPosition.Keyboard(captured, alignment);
            Equal(popup.Position, captured, "keyboard position remains unchanged");
            Require(popup.Exclusion is null && popup.PositionFlags == alignment,
                "Keyboard invocation retains system alignment and has no pointer exclusion.");
        }
        Equal(FileMenuPosition.Capture(new(42, 65), item, new(100, 200), 1), new(142, 265), "event coordinate capture");
        Equal(FileMenuPosition.Capture(new(11, 13), item, new(-1920, -200), 1.5), new(-1904, -180), "physical coordinate rounding");
    }

    private static Rect Exclusion(FileMenuPopup popup) => popup.Exclusion
        ?? throw new InvalidOperationException("Pointer invocation must provide an exclusion rectangle.");

    private static void Flags(FileMenuPopup popup) => Require(popup.PositionFlags == (0x40u | 0x10000u),
        "Pointer placement must request vertical exclusion/work-area handling with left/top alignment, never center/right/bottom alignment.");

    private static void Equal(Point actual, Point expected, string name) =>
        Require(actual == expected, $"{name}: expected {expected}, got {actual}");

    private static void Equal(Rect actual, Rect expected, string name) =>
        Require(actual == expected, $"{name}: expected {expected}, got {actual}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
