using TuckPane.Core;

internal static class NestedPreviewEdgeChecks
{
    internal static void Run()
    {
        OrganizerPreviewEdgeState state = default;
        Require(!state.IsVisible, "Unconfigured previews must not flash an edge.");
        state = new(Enabled: true, IsCompact: false, IsDock: false, IsIconPreview: true);
        Require(state.IsVisible, "Expanded nested icon preview must show the enabled edge.");
        Require(!(state with { Enabled = false }).IsVisible, "Global switch off must hide the reflection.");
        Require(!(state with { IsCompact = true }).IsVisible, "Collapsed miniature must remain unchanged.");
        Require(!(state with { IsDock = true }).IsVisible, "Dock must remain unchanged.");
        Require(!(state with { IsIconPreview = false }).IsVisible, "List and non-organizer items must not show it.");

        // Simulate successive reconfigurations of one recycled preview.
        state = state with { Enabled = false, IsDock = true, IsCompact = true };
        state = state with { Enabled = true };
        Require(!state.IsVisible, "Enabling globally must not bypass the compact/Dock exclusions.");
        state = state with { IsCompact = false };
        Require(!state.IsVisible, "Leaving compact mode must not retain an eligible host from before recycling.");
        state = state with { IsDock = false };
        Require(state.IsVisible, "Returning to an eligible preview must restore the edge.");
        state = state with { IsIconPreview = false };
        Require(!state.IsVisible, "Reusing the item as a non-preview must clear the edge.");
        Console.WriteLine("PASS: nested preview edge (scope, switch, reconfiguration; no UI or visual verification)");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
