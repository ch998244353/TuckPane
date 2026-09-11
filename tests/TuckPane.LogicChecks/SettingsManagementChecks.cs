using TuckPane.Core;
using TuckPane.Models;

internal static class SettingsManagementChecks
{
    private static readonly ThemeTarget[] Categories = [ThemeTarget.Organizer, ThemeTarget.Station, ThemeTarget.Dock];

    internal static async Task RunAsync()
    {
        await CheckDraftsAndSuccessAsync();
        await CheckFailureAndDuplicateAsync();
        await CheckClosedSessionAsync();
        Console.WriteLine("PASS --settings-management: production draft isolation, category parameters, lifecycle, submission snapshots, duplicate prevention and failure recovery. Memory only; no GUI, input automation or old gates.");
    }

    private static SettingsCreationDraft Draft(ThemeTarget category) => new(new OrganizerDefinition
    {
        Name = category.ToString(),
        PlacementMode = category switch
        {
            ThemeTarget.Organizer => OrganizerPlacementMode.Positioned,
            ThemeTarget.Station => OrganizerPlacementMode.Station,
            _ => OrganizerPlacementMode.Dock
        },
        Layout = new() { Rows = 4, Columns = 5 },
        DockEdge = OrganizerDockEdge.Left,
        Position = new() { MonitorDevice = "test-monitor", XDip = 23 },
        DockOrientation = DockOrientation.Vertical,
        DockIconSizeDip = 72,
        DockSpacingFactor = .9
    }, $"memory-only/{category}", NameWasEdited: true);

    private static SettingsCreationDraft Existing(SettingsCreationSession session, ThemeTarget category) =>
        session.GetOrCreate(category, () => throw new InvalidOperationException($"Lost {category} draft."));

    private static async Task CheckDraftsAndSuccessAsync()
    {
        foreach (ThemeTarget category in Categories)
        {
            var session = new SettingsCreationSession();
            foreach (ThemeTarget other in Categories) session.GetOrCreate(other, () => Draft(other));
            SettingsCreationDraft edit = Existing(session, category);
            edit.Definition.Name = "edited";
            edit.Definition.Layout.Rows = 7;
            edit.Definition.Position!.MonitorDevice = "edited-monitor";
            Require(Existing(session, category).Definition.Layout.Rows == 4 &&
                Existing(session, category).Definition.Position!.MonitorDevice == "test-monitor",
                "Editing a returned draft must not mutate the retained draft or its nested values.");
            session.Save(category, edit);
            edit.Definition.Name = "uncommitted";
            edit.Definition.Layout.Rows = 99;
            Require(Existing(session, category).Definition.Name == "edited" &&
                Existing(session, category).Definition.Layout.Rows == 7,
                "Saved drafts must be cloned and survive switching away and back.");
            int calls = 0;
            SettingsCreationResult? result = await session.SubmitAsync(category, (definition, path) =>
            {
                calls++;
                Require(definition.Name == "edited" && definition.PlacementMode == Draft(category).Definition.PlacementMode &&
                    definition.Layout.Rows == 7 && definition.Layout.Columns == 5 &&
                    definition.Position!.MonitorDevice == "edited-monitor" && definition.DockEdge == OrganizerDockEdge.Left &&
                    definition.DockOrientation == DockOrientation.Vertical && definition.DockIconSizeDip == 72 &&
                    definition.DockSpacingFactor == .9 && path == $"memory-only/{category}",
                    "Creation must receive the selected category and complete edited parameter snapshot.");
                return Task.FromResult(definition);
            });
            Require(calls == 1 && result is { SameSession: true } && !session.IsSubmitting,
                "Success must produce one creation and release submission state.");
            Require(session.GetOrCreate(category, () => Draft(category)).Definition.Name == category.ToString(),
                "Successful creation must clear its own draft.");
            foreach (ThemeTarget other in Categories.Where(other => other != category))
                Require(Existing(session, other).Definition.Name == other.ToString(), "Creation must preserve other categories.");
            int generation = session.Generation;
            session.Close();
            int factories = 0;
            foreach (ThemeTarget other in Categories)
                session.GetOrCreate(other, () => { factories++; return Draft(other); });
            Require(factories == 3 && session.Generation != generation, "Close must clear all categories and advance the session.");
        }
    }

    private static async Task CheckFailureAndDuplicateAsync()
    {
        var session = new SettingsCreationSession();
        SettingsCreationDraft original = Draft(ThemeTarget.Dock);
        session.GetOrCreate(ThemeTarget.Dock, () => original);
        original.Definition.Layout.Rows = 99;
        Require(Existing(session, ThemeTarget.Dock).Definition.Layout.Rows == 4, "Factory output must also be cloned.");
        var pending = new TaskCompletionSource<OrganizerDefinition>();
        var failure = new IOException("controlled creation failure");
        Task<SettingsCreationResult?> first = session.SubmitAsync(ThemeTarget.Dock, (definition, _) =>
        {
            definition.Name = "creator mutation";
            definition.Layout.Rows = 88;
            return pending.Task;
        });
        Require(session.SubmittingCategory == ThemeTarget.Dock && Existing(session, ThemeTarget.Dock).Definition.Layout.Rows == 4,
            "The in-flight creator must receive an isolated snapshot.");
        SettingsCreationResult? duplicate = await session.SubmitAsync(ThemeTarget.Station,
            (_, _) => throw new InvalidOperationException("Duplicate creation was invoked."));
        Require(duplicate is null, "Another submission must be rejected while one is in flight.");
        pending.SetException(failure);
        try { await first; throw new InvalidOperationException("Creation failure was swallowed."); }
        catch (IOException error) when (ReferenceEquals(error, failure)) { }
        Require(!session.IsSubmitting && Existing(session, ThemeTarget.Dock) is { NameWasEdited: true } retained &&
            retained.Definition.Name == "Dock" && retained.Definition.Layout.Rows == 4,
            "Failure must release the guard and retain the original input for retry.");
        Require(await session.SubmitAsync(ThemeTarget.Dock, (definition, _) => Task.FromResult(definition)) is { SameSession: true },
            "A failed submission must allow a successful retry.");
    }

    private static async Task CheckClosedSessionAsync()
    {
        var session = new SettingsCreationSession();
        session.GetOrCreate(ThemeTarget.Organizer, () => Draft(ThemeTarget.Organizer));
        var pending = new TaskCompletionSource<OrganizerDefinition>();
        Task<SettingsCreationResult?> submission = session.SubmitAsync(ThemeTarget.Organizer, (_, _) => pending.Task);
        session.Close();
        SettingsCreationDraft reopened = Draft(ThemeTarget.Organizer);
        reopened.Definition.Name = "reopened draft";
        session.GetOrCreate(ThemeTarget.Organizer, () => reopened);
        pending.SetResult(Draft(ThemeTarget.Organizer).Definition);
        Require(await submission is { SameSession: false } && !session.IsSubmitting &&
            Existing(session, ThemeTarget.Organizer).Definition.Name == "reopened draft",
            "An old submission completing after Close must identify the stale session and preserve its replacement draft.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
