using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class IconRowStateChecks
{
    internal static async Task RunAsync()
    {
        VerifyDrop([3], 2, new(1, 0, true), [0, 1, 2], [2, 1]);
        VerifyDrop([3, 2], 4, new(0, 1), [0, 2, 3, 4, 1], [3, 2]);
        string path = Path.Combine(Path.GetTempPath(), $"tuckpane-rows-{Guid.NewGuid():N}.json");
        try
        {
            var window = new OrganizerDefinition { ItemOrder = ["a", "b", "c", "d"], IconRowCounts = [2, 1, 1] };
            List<string> refreshed = ["b", "c", "d", "e"];
            window.IconRowCounts = OrganizerIconRows.Reconcile(window, refreshed);
            window.ItemOrder = refreshed;
            var store = new StateStore(path);
            await store.SaveAsync(new AppStateV2 { Organizers = [window] });
            var saved = (await store.LoadAsync()).Organizers.Single();
            if (!saved.IconRowCounts.SequenceEqual([1, 1, 2]) || !saved.ItemOrder.SequenceEqual(refreshed))
                throw new Exception("Catalog removal and append must retain row boundaries after reload.");
            Console.WriteLine("PASS icon row catalog and state round trip");
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    private static void VerifyDrop(int[] rows, int source, IconRowTarget target, int[] expectedMapping, int[] expectedRows)
    {
        var session = new ItemReorderSession("dragged", source, default, default);
        session.Activate(default);
        session.UpdateRowTarget(default, rows, 3, target);
        int[] mapping = session.SealVisualIndices(expectedMapping.Length);
        session.BeginCommit();
        if (!mapping.SequenceEqual(expectedMapping) || session.PreviewRows?.SequenceEqual(expectedRows) != true)
            throw new Exception("Committed order or row boundaries differ from the requested drop.");
    }
}
