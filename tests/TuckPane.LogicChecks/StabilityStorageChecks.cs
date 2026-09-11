using TuckPane.Core;
using TuckPane.Models;

internal static class StabilityStorageChecks
{
    internal static void Run(string root)
    {
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "a.txt"), "complete a");
        File.WriteAllText(Path.Combine(source, "b.txt"), "complete b");
        File.Copy(Path.Combine(source, "a.txt"), Path.Combine(target, "a.txt"));
        File.Copy(Path.Combine(source, "b.txt"), Path.Combine(target, "b.txt"));
        TransferOutcome? outcome = null;
        try
        {
            outcome = PublishedMoveCompletion.Complete(source, target, () =>
            {
                File.Delete(Path.Combine(source, "a.txt"));
                throw new IOException("source deletion failed after first file");
            });
        }
        catch (IOException) { }
        Require(File.Exists(Path.Combine(target, "a.txt")) && File.ReadAllText(Path.Combine(target, "b.txt")) == "complete b",
            "A partial source deletion must retain the complete published destination");
        Require(outcome is { Status: TransferStatus.CopiedSourceRetained } && outcome.DestinationPath == target,
            "Partial source cleanup returns the retained destination");

        outcome = PublishedMoveCompletion.Complete(source, target, () => Directory.Delete(source, true),
            () => throw new IOException("empty parent cleanup failed"));
        Require(outcome.Status == TransferStatus.Moved && outcome.DestinationPath == target && Directory.Exists(target),
            "Parent cleanup cannot roll back a completed move");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
