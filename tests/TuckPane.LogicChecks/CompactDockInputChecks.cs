using System.Numerics;
using TuckPane.Core;
using TuckPane.Models;

// Production state/geometry only: no windows, native input, launchers or user files.
internal static class CompactDockInputChecks
{
    internal static void Run(string? area)
    {
        if (area is not (null or "click" or "scroll"))
            throw new ArgumentException($"Unknown compact-dock-input group: {area}.");
        if (area is null or "click") Click();
        if (area is null or "scroll") Scroll();
        Console.WriteLine($"PASS --compact-dock-input {area ?? "click + scroll"}: production state/geometry; no GUI or input automation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static readonly Vector2 Pointer = new(-500, 300); // Physical coordinates can be negative.
    private static void Begin(CompactClickSequence sequence, CompactClickTarget target, long time,
        Vector2? point = null) => sequence.Begin(target, point ?? Pointer, time, 500, new(8, 10), 8);

    private static CompactClickTarget? ClickAt(CompactClickSequence sequence, CompactClickTarget target,
        long time, Vector2? point = null)
    {
        Begin(sequence, target, time, point);
        return sequence.Complete(target, point ?? Pointer);
    }

    private static void Click()
    {
        foreach (WidgetItemKind kind in new[] { WidgetItemKind.File, WidgetItemKind.Folder,
                     WidgetItemKind.Shortcut, WidgetItemKind.InternetShortcut, WidgetItemKind.Note,
                     WidgetItemKind.PortableNote, WidgetItemKind.PortableTodo })
        {
            var original = new WidgetItem("entry", @"C:\fixture\entry", "entry", kind);
            var refreshedProxy = original.CopyValue();
            var target = CompactClickTarget.From(original);
            var sequence = new CompactClickSequence();
            Require(ClickAt(sequence, target, 1000) is null, $"{kind}: first click must not open.");
            CompactClickTarget? opened = ClickAt(sequence, CompactClickTarget.From(refreshedProxy), 1200);
            Require(opened is { } value && value.SameAs(target),
                $"{kind}: a new visual/catalog instance with the same identity must complete the pair.");
            Require(sequence.Complete(target, Pointer) is null && ClickAt(sequence, target, 1300) is null,
                $"{kind}: consumed release and a third click must not reopen the item.");
        }

        var item = new WidgetItem("a", @"C:\fixture\a", "a", WidgetItemKind.File);
        var a = CompactClickTarget.From(item);
        var b = a with { RelativeName = "b", FullPath = @"C:\fixture\b" };
        foreach (var scenario in new[] {
                     (Name: "different item", Target: b, Time: 1200L, Point: Pointer),
                     (Name: "expired pair", Target: a, Time: 1501L, Point: Pointer),
                     (Name: "horizontal distance", Target: a, Time: 1200L, Point: Pointer + new Vector2(5, 0)),
                     (Name: "vertical distance", Target: a, Time: 1200L, Point: Pointer + new Vector2(0, 6)) })
        {
            var sequence = new CompactClickSequence();
            ClickAt(sequence, a, 1000);
            Require(ClickAt(sequence, scenario.Target, scenario.Time, scenario.Point) is null,
                scenario.Name + ": must start a new sequence instead of opening.");
        }

        foreach (var scenario in new[] {
                     (Name: "release over another item", Target: (CompactClickTarget?)b, Point: Pointer),
                     (Name: "release outside", Target: (CompactClickTarget?)null, Point: Pointer),
                     (Name: "drag without a move event", Target: (CompactClickTarget?)a, Point: Pointer + new Vector2(8, 0)) })
        {
            var sequence = new CompactClickSequence();
            ClickAt(sequence, a, 1000);
            Begin(sequence, a, 1200);
            Require(sequence.Complete(scenario.Target, scenario.Point) is null &&
                    ClickAt(sequence, a, 1300) is null,
                scenario.Name + ": reject the release and discard the preceding click.");
        }

        var cancelled = new CompactClickSequence();
        ClickAt(cancelled, a, 1000);
        Begin(cancelled, a, 1200);
        cancelled.Reset(); // Drag, abnormal capture loss, wheel and mode/lifecycle cleanup share this operation.
        Require(cancelled.Complete(a, Pointer) is null && ClickAt(cancelled, a, 1300) is null,
            "Cancellation must discard both the active press and earlier completed click.");

        var child = CompactClickTarget.From(new WidgetItem("child", "", "child", WidgetItemKind.Organizer,
            organizerId: Guid.Parse("067f2537-b3a9-4cce-93f3-4e6c5403b9dd")));
        Require(ClickAt(new CompactClickSequence(), child, 1000) is { } expanded && expanded.SameAs(child),
            "Child organizers must retain single-click expansion.");

        foreach (CompactClickTarget replacement in new[] { a with { Kind = WidgetItemKind.Folder },
                     a with { FullPath = @"D:\fixture\a" }, a with { RelativeName = "renamed" },
                     a with { NoteId = Guid.Parse("37d7b037-f8ba-4db9-8198-36b19d0bfefb") },
                     a with { OrganizerId = Guid.Parse("3310aa2f-305f-482a-8f9b-f485345ab2dc") } })
        {
            var sequence = new CompactClickSequence();
            ClickAt(sequence, a, 1000);
            Begin(sequence, a, 1200);
            item.ApplyValue(new WidgetItem("refreshed", replacement.FullPath, replacement.RelativeName,
                replacement.Kind, replacement.NoteId, replacement.OrganizerId));
            Require(!a.SameAs(CompactClickTarget.From(item)) &&
                    sequence.Complete(CompactClickTarget.From(item), Pointer) is null,
                "A refreshed mutable item must not change the press snapshot or match a replaced identity.");
        }
    }

    private static void Scroll()
    {
        var wave = new OrganizerHoverWave();
        wave.SetAvailability(enabled: true, suspended: false);
        Vector2 pointer = new(60, 62), pitch = new(180, 44);
        HoverRect viewport = new(0, 0, 220, 132);
        HoverRect Row(int index, float offset) => new(10, index * 44 - offset, 180, 36);
        HoverLayoutPose[] Layout(float offset)
        {
            var items = Enumerable.Range(0, 4).Select(i => new HoverLayoutItem(Row(i, offset),
                wave.GetTargetScale(Row(i, offset).Center, pitch, compactList: true))).ToArray();
            return HoverLayoutMath.Calculate(items, pointer.Y, horizontal: false, compactList: true);
        }
        int Hit(float offset, HoverLayoutPose[] poses) => HoverLayoutMath.HitTestItems(
            Enumerable.Range(0, poses.Length).Select(i => new HoverHitRegion(Row(i, offset), Row(i, offset),
                poses[i].Bounds.Intersect(viewport), poses[i].Translation, poses[i].Scale, true)).ToArray(),
            compactList: true, viewport, default, pointer);

        Require(wave.MovePointer(pointer), "An available compact list must record the pointer.");
        HoverLayoutPose[] before = Layout(0);
        Require(Hit(0, before) == 1 && before[1].Scale > before[2].Scale,
            "The initial row under the stationary pointer must receive the largest scale.");
        var motion = new HoverWaveMotion();
        motion.Retarget(before[1].Scale);
        motion.Step(1d / 60);
        float inFlight = motion.Scale;
        wave.SetScrolling(true, preservePointer: true);
        HoverLayoutPose[] after = Layout(44); // Change viewport geometry without another pointer move.
        Require(wave.Pointer == pointer && Hit(44, after) == 2 && after[2].Scale > after[1].Scale,
            "Scrolling one row must transfer magnification and hit geometry to the next item without mouse movement.");
        motion.Retarget(after[1].Scale);
        Require(inFlight > 1 && motion.Scale == inFlight,
            "The old row must retarget its existing animation without resetting its visible scale.");
        Require(wave.MovePointer(pointer), "Preserved scrolling must still accept wheel/pointer updates.");
        wave.SetScrolling(false, preservePointer: true);
        Require(wave.Pointer == pointer && Layout(44)[2].Scale == after[2].Scale,
            "Scroll completion or a boundary with unchanged geometry must retain magnification.");

        foreach (string reason in new[] { "outside", "disabled", "suspended" })
        {
            wave.SetAvailability(enabled: true, suspended: false);
            wave.MovePointer(pointer);
            wave.SetScrolling(true, preservePointer: true);
            if (reason == "outside") wave.ObservePointerPresence(true, viewport.Contains(new(-1, 62)));
            else wave.SetAvailability(enabled: reason != "disabled", suspended: reason == "suspended");
            Require(!wave.HasPointer && Layout(44).All(pose => pose.Scale == 1),
                reason + ": preserving scroll must not retain a stale hover after exit/unavailability.");
            wave.SetScrolling(false, preservePointer: true);
        }

        wave.SetAvailability(enabled: true, suspended: false);
        wave.MovePointer(pointer);
        wave.SetScrolling(true);
        Require(!wave.HasPointer && !wave.MovePointer(pointer),
            "Modes that do not opt into preservation must retain their existing scrolling suppression.");
    }
}
