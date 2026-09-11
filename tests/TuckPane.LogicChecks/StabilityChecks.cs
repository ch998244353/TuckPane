using TuckPane.Services;

internal static class StabilityChecks
{
    internal static async Task RunAsync(string area)
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-stability-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        try
        {
            switch (area)
            {
                case "queue":
                    await CheckQueueAsync();
                    break;
                case "storage":
                    StabilityStorageChecks.Run(root);
                    Console.WriteLine("PASS --stability storage: published destination retention and parent cleanup; isolated files, no GUI.");
                    break;
                case "lifecycle":
                    await StabilityLifecycleChecks.RunAsync();
                    break;
                case "drop":
                    await StabilityInteractionChecks.DropAsync();
                    break;
                case "launch":
                    await StabilityInteractionChecks.LaunchAsync(root);
                    break;
                case "watcher":
                    await StabilityInteractionChecks.WatcherAsync();
                    break;
                case "diagnostics":
                    await StabilityDiagnosticChecks.RunAsync(root);
                    break;
                default:
                    throw new ArgumentException($"Unknown stability area: {area}.");
            }
        }
        finally
        {
            try
            {
                // Drain all writes while the application still points at this isolated root.
                await AppLogger.FlushAsync();
            }
            finally
            {
                Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
                string fullRoot = Path.GetFullPath(root);
                string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (!string.Equals(Path.GetDirectoryName(fullRoot), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(fullRoot).StartsWith("TuckPane-stability-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing cleanup outside the isolated stability test directory.");
                Directory.Delete(fullRoot, recursive: true);
            }
        }
    }

    private static async Task CheckQueueAsync()
    {
        var failures = new List<string>();
        void Require(bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        var startQueue = new TransferQueue();
        startQueue.StateChanged += (_, _) =>
        {
            if (startQueue.IsActive) throw new InvalidOperationException("expected start observer failure");
        };
        try { await startQueue.RunAsync(_ => Task.FromResult(17)); }
        catch (InvalidOperationException ex) when (ex.Message == "expected start observer failure") { }
        Require(!startQueue.IsActive && startQueue.WaitForIdleAsync().IsCompletedSuccessfully,
            "A start observer exception must not leak pending work or leave the idle signal incomplete.");

        var finishQueue = new TransferQueue();
        finishQueue.StateChanged += (_, _) =>
        {
            if (!finishQueue.IsActive) throw new InvalidOperationException("expected finish observer failure");
        };
        int? result = null;
        Exception? completionError = null;
        try { result = await finishQueue.RunAsync(_ => Task.FromResult(23)); }
        catch (Exception ex) { completionError = ex; }
        Require(result == 23 && completionError is null,
            "A finish observer exception must not replace the successful operation result.");
        Require(!finishQueue.IsActive && finishQueue.WaitForIdleAsync().IsCompletedSuccessfully,
            "The queue must be idle after the operation even when the finish observer throws.");

        if (failures.Count > 0)
        {
            foreach (string failure in failures) Console.Error.WriteLine("FAIL --stability queue: " + failure);
            throw new InvalidOperationException($"Stability queue checks failed: {failures.Count}.");
        }
        Console.WriteLine("PASS --stability queue: start notification cleanup and finish notification result preservation; no GUI or input automation.");
    }
}
