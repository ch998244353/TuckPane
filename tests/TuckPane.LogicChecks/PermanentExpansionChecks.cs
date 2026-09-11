using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class PermanentExpansionChecks
{
    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-permanent-expanded-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            await CheckPersistenceAsync(root);
            await CheckModeChangesAsync();
            CheckContainment();
            CheckAlignmentAndMemory();
            Console.WriteLine("PASS --permanent-expanded: 4 focused groups (persistence/positions/copy, mode rollback, containment, alignment/memory eligibility). No windows or input automation; host/UI acceptance remains manual.");
        }
        finally
        {
            await AppLogger.FlushAsync();
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckPersistenceAsync(string root)
    {
        string legacyPath = Path.Combine(root, "missing-mode.json");
        await File.WriteAllTextAsync(legacyPath,
            $$"""{"SchemaVersion":{{new AppStateV2().SchemaVersion}},"Organizers":[{"Name":"legacy"}]}""");
        Require((await new StateStore(legacyPath).LoadAsync()).Organizers.Single().ExpansionMode ==
            OrganizerExpansionMode.Collapsible, "A saved organizer without the new mode must remain collapsible.");

        var floating = new OrganizerDefinition
        {
            Position = new WidgetPosition { MonitorDevice = "compact", XDip = 40, YDip = 50 },
            ExpandedPosition = new WidgetPosition { MonitorDevice = "expanded", XDip = 320, YDip = 180 }
        };
        var positioned = Permanent(OrganizerPlacementMode.Positioned);
        var station = Permanent(OrganizerPlacementMode.Station);
        var child = Permanent();
        child.ContainerOrganizerId = floating.Id;
        floating.ItemOrder = [OrganizerContainment.ItemKey(child.Id), "keep.txt"];
        var invalid = new OrganizerDefinition { ExpansionMode = (OrganizerExpansionMode)999 };
        var dangling = Permanent();
        dangling.ContainerOrganizerId = Guid.NewGuid();
        var state = new AppStateV2
        {
            GlobalSettings = new GlobalSettings { RememberExpandedOrganizerPosition = false },
            Organizers = [floating, positioned, station, child, invalid, dangling]
        };
        var store = new StateStore(Path.Combine(root, "state.json"));
        await OrganizerExpansion.SetAsync(floating, OrganizerExpansionMode.AlwaysExpanded, () => store.SaveAsync(state));
        AppStateV2 saved = await store.LoadAsync();
        OrganizerDefinition loaded = saved.Organizers.Single(item => item.Id == floating.Id);
        Require(OrganizerExpansion.IsPermanent(loaded) &&
            OrganizerExpansion.IsPermanent(saved.Organizers.Single(item => item.Id == positioned.Id)),
            "Both supported placements must retain permanent expansion through the real save/load path.");
        Require(saved.Organizers.Where(item => item.Id == station.Id || item.Id == child.Id || item.Id == invalid.Id)
            .All(item => item.ExpansionMode == OrganizerExpansionMode.Collapsible),
            "Station, contained organizers and invalid values must normalize to collapsible.");
        Require(saved.Organizers.Single(item => item.Id == child.Id).ContainerOrganizerId == floating.Id &&
            OrganizerExpansion.IsPermanent(saved.Organizers.Single(item => item.Id == dangling.Id)),
            "Mode normalization must respect retained containment and run after dangling parents are repaired.");

        OrganizerDefinition copy = OrganizerInteractionMath.CopySettings(loaded, "copy");
        Require(OrganizerExpansion.IsPermanent(copy) && copy.Id != loaded.Id && copy.Position is null &&
            copy.ExpandedPosition is null && copy.ContainerOrganizerId is null && copy.ItemOrder.Count == 0,
            "Copies must inherit permanent mode while owning independent identity, placement and contents.");
        Require(loaded.Position is { MonitorDevice: "compact", XDip: 40, YDip: 50 } &&
            loaded.ExpandedPosition is { MonitorDevice: "expanded", XDip: 320, YDip: 180 } &&
            !saved.GlobalSettings.RememberExpandedOrganizerPosition,
            "Separate compact/expanded positions must persist with ordinary position memory disabled.");
        loaded.ExpandedPosition!.XDip = 480;
        await OrganizerExpansion.SetAsync(loaded, OrganizerExpansionMode.Collapsible, () => store.SaveAsync(saved));
        OrganizerDefinition collapsed = (await store.LoadAsync()).Organizers.Single(item => item.Id == floating.Id);
        Require(collapsed.ExpansionMode == OrganizerExpansionMode.Collapsible &&
            collapsed.Position is { MonitorDevice: "compact", XDip: 40, YDip: 50 } &&
            collapsed.ExpandedPosition is { MonitorDevice: "expanded", XDip: 480, YDip: 180 },
            "Switching back must save the latest expanded position without overwriting the compact position.");
    }

    private static async Task CheckModeChangesAsync()
    {
        foreach (OrganizerExpansionMode previous in Enum.GetValues<OrganizerExpansionMode>())
        {
            var organizer = new OrganizerDefinition { ExpansionMode = previous };
            OrganizerExpansionMode next = previous == OrganizerExpansionMode.Collapsible
                ? OrganizerExpansionMode.AlwaysExpanded : OrganizerExpansionMode.Collapsible;
            int writes = 0;
            await ExpectAsync<IOException>(() => OrganizerExpansion.SetAsync(organizer, next, () =>
            {
                writes++;
                Require(organizer.ExpansionMode == next, "The save callback must observe the requested mode.");
                return Task.FromException(new IOException("Expected isolated save failure."));
            }));
            Require(writes == 1 && organizer.ExpansionMode == previous,
                "A failed enable or disable save must restore the previous mode and propagate the failure.");
        }

        var station = new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Station };
        var child = new OrganizerDefinition { ContainerOrganizerId = Guid.NewGuid() };
        (OrganizerDefinition Organizer, OrganizerExpansionMode Mode)[] rejected =
        [
            (station, OrganizerExpansionMode.AlwaysExpanded),
            (child, OrganizerExpansionMode.AlwaysExpanded),
            (new OrganizerDefinition(), (OrganizerExpansionMode)999)
        ];
        foreach (var (organizer, mode) in rejected)
        {
            int writes = 0;
            await ExpectAsync<InvalidOperationException>(() => OrganizerExpansion.SetAsync(organizer, mode, () =>
            { writes++; return Task.CompletedTask; }));
            Require(writes == 0 && organizer.ExpansionMode == OrganizerExpansionMode.Collapsible,
                "Illegal mode changes must fail without changing state or invoking persistence.");
        }
    }

    private static void CheckContainment()
    {
        foreach (OrganizerPlacementMode mode in new[] { OrganizerPlacementMode.Floating, OrganizerPlacementMode.Positioned })
        {
            var permanent = Permanent(mode);
            permanent.ItemOrder = ["existing.txt"];
            var target = new OrganizerDefinition { ItemOrder = ["target.txt"] };
            var child = new OrganizerDefinition();
            List<OrganizerDefinition> organizers = [permanent, target, child];
            var before = organizers.Select(item => (item.ContainerOrganizerId, Order: item.ItemOrder.ToArray())).ToArray();
            OrganizerContainmentMoveResult rejected = OrganizerContainment.TryMove(organizers, permanent.Id, target.Id, 0);
            Require(!rejected.Succeeded && rejected.Failure == OrganizerContainmentFailure.PermanentWindowCannotBeContained &&
                organizers.Select((item, index) => item.ContainerOrganizerId == before[index].ContainerOrganizerId &&
                    item.ItemOrder.SequenceEqual(before[index].Order)).All(unchanged => unchanged),
                "Rejecting a permanent source must leave every organizer's ownership and ordering unchanged.");
            OrganizerContainmentMoveResult accepted = OrganizerContainment.TryMove(organizers, child.Id, permanent.Id, 1);
            Require(accepted.Succeeded && child.ContainerOrganizerId == permanent.Id && OrganizerExpansion.IsPermanent(permanent) &&
                permanent.ItemOrder.SequenceEqual(["existing.txt", OrganizerContainment.ItemKey(child.Id)]),
                "A permanent root must still accept an ordinary child at the requested position.");
        }
    }

    private static void CheckAlignmentAndMemory()
    {
        foreach (OrganizerPlacementMode mode in new[] { OrganizerPlacementMode.Floating, OrganizerPlacementMode.Positioned })
        {
            Require(OrganizerInteractionMath.ShouldUseWindowAlignment(true, true, mode, false, permanentlyExpanded: true),
                $"Permanent expanded {mode} must participate in alignment.");
            Require(!OrganizerInteractionMath.ShouldUseWindowAlignment(false, true, mode, false, true) &&
                !OrganizerInteractionMath.ShouldUseWindowAlignment(true, true, mode, true, true) &&
                !OrganizerInteractionMath.ShouldUseWindowAlignment(true, true, mode, false),
                "Disabled alignment, an organizer drop target and temporary expansion must prevent alignment.");
            Require(OrganizerInteractionMath.ShouldRememberExpandedPosition(false, mode, permanentlyExpanded: true) &&
                !OrganizerInteractionMath.ShouldRememberExpandedPosition(false, mode),
                "Only permanent expansion must bypass the ordinary expanded-position memory switch.");
        }
        Require(!OrganizerInteractionMath.ShouldUseWindowAlignment(true, true, OrganizerPlacementMode.Station, false, true) &&
            !OrganizerInteractionMath.ShouldRememberExpandedPosition(true, OrganizerPlacementMode.Station, true),
            "Station must stay outside permanent expanded alignment and position memory.");
        Require(OrganizerInteractionMath.ShouldUseWindowAlignment(true, false, OrganizerPlacementMode.Floating, false) &&
            !OrganizerInteractionMath.ShouldUseWindowAlignment(true, false, OrganizerPlacementMode.Positioned, false),
            "Adding expanded alignment must preserve the existing collapsed placement distinction.");
    }

    private static OrganizerDefinition Permanent(OrganizerPlacementMode mode = OrganizerPlacementMode.Floating) =>
        new() { PlacementMode = mode, ExpansionMode = OrganizerExpansionMode.AlwaysExpanded };

    private static async Task ExpectAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not propagated.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
