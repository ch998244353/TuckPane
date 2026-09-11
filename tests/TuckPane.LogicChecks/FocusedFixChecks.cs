using System.Text.Json;
using System.Xml.Linq;
using TuckPane;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class FocusedFixChecks
{
    internal static async Task InteractionRecoveryWheelAsync()
    {
        string main = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", "MainWindow.xaml.cs"));
        Require(main.Contains("OrganizerInteractionMath.IsControlPressed("),
            "The wheel path still uses only routed modifiers, with no non-activating window fallback.");
        Require(main.Contains("_widgetGesture.CompleteAsync(") && main.Contains("_resizeGesture.CompleteAsync("),
            "Native gesture completion must be protected against reentrancy and stale callbacks.");

        Require(OrganizerInteractionMath.IsControlPressed(false, unchecked((short)0x8000)) &&
                OrganizerInteractionMath.IsControlPressed(true, 0) &&
                !OrganizerInteractionMath.IsControlPressed(false, 1),
            "Use the current pressed bit as fallback, never the unreliable low bit.");
        foreach (bool list in new[] { false, true })
        {
            Require(OrganizerInteractionMath.ResolveWheelAction(true, false, false, false, false,
                        OrganizerInteractionMath.IsControlPressed(false, unchecked((short)0x8000)), list, true) ==
                    (list ? OrganizerWheelAction.ScaleCompactList : OrganizerWheelAction.ScaleGrid),
                "Ctrl fallback must select scale even when the routed modifiers omit it.");
            Require(OrganizerInteractionMath.ResolveWheelAction(true, false, false, false, false, false, list, true) ==
                    (list ? OrganizerWheelAction.ScrollCompactList : OrganizerWheelAction.ScrollGrid),
                "Ordinary wheel must still scroll.");
        }

        int closedStart = main.IndexOf("private void MainWindow_Closed", StringComparison.Ordinal);
        string closed = main[closedStart..main.IndexOf("private void ShowMessage", closedStart, StringComparison.Ordinal)];
        Require(closed.IndexOf("_transitionCancellation = null;", StringComparison.Ordinal) <
                closed.IndexOf("closingTransition?.Dispose();", StringComparison.Ordinal) &&
                closed.Contains("closingTransition?.Dispose();"),
            "Closed must clear the published CTS before disposing it; animation continuations inspect the field.");

        // Real completion ownership: duplicate/reentrant and stale callbacks, then an exception.
        var session = new InteractionSession();
        Require(session.TryStart(), "First gesture rejected.");
        long first = session.Version;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int completions = 0;
        Task finish = session.CompleteAsync(first, async () => { completions++; await pending.Task; });
        await session.CompleteAsync(first, () => { completions++; return Task.CompletedTask; });
        Require(!session.TryStart() && completions == 1, "Completion reentered or new gesture replaced pending completion.");
        pending.SetResult();
        await finish;
        Require(session.TryStart(), "Completion failed to release the session.");
        await session.CompleteAsync(first, () => { completions++; return Task.CompletedTask; });
        Require(completions == 1, "Stale callback completed the next gesture.");
        try { await session.CompleteAsync(session.Version, () => throw new InvalidOperationException("expected")); }
        catch (InvalidOperationException ex) when (ex.Message == "expected") { }
        Require(!session.IsCompleting && session.TryStart(), "Exception left a stuck session.");

        // The same event waiter used by Loaded/Rendering: signal, no frame, and cancellation.
        Action? signal = null;
        int subscriptions = 0;
        void Subscribe(Action action) { subscriptions++; signal = action; }
        void Unsubscribe(Action action) { subscriptions--; signal = null; }
        Task<bool> signaled = UiEventAwaiter.WaitAsync(Subscribe, Unsubscribe, TimeSpan.FromSeconds(5), default);
        signal!();
        Require(await signaled && subscriptions == 0, "Signaled event leaked its subscription.");
        Require(!await UiEventAwaiter.WaitAsync(Subscribe, Unsubscribe, TimeSpan.Zero, default) && subscriptions == 0,
            "A missing render event must finish with timeout and unsubscribe.");
        using var cancellation = new CancellationTokenSource();
        Task<bool> canceled = UiEventAwaiter.WaitAsync(Subscribe, Unsubscribe, TimeSpan.FromSeconds(5), cancellation.Token);
        cancellation.Cancel();
        try { await canceled; throw new InvalidOperationException("Canceled wait succeeded."); }
        catch (OperationCanceledException) { }
        Require(subscriptions == 0, "Canceled event leaked its subscription.");
        Console.WriteLine("PASS --interaction-recovery-wheel: completion reentrancy/stale/exception and event signal/timeout/cancellation (no UI).");
    }

    internal static void StationStartup()
    {
        string source = Path.Combine(Environment.CurrentDirectory, "src", "TuckPane");
        string main = File.ReadAllText(Path.Combine(source, "MainWindow.xaml.cs"));
        int start = main.IndexOf("private async Task InitializeCoreAsync()", StringComparison.Ordinal);
        if (start < 0) start = main.IndexOf("private async Task InitializeAsync()", StringComparison.Ordinal);
        string init = main[start..main.IndexOf("private async Task WaitForLoadedAsync", start, StringComparison.Ordinal)];
        Require(init.IndexOf("OrganizerPlacementMode.Station", StringComparison.Ordinal) <
                init.IndexOf("await WaitForLoadedAsync", StringComparison.Ordinal),
            "Station native readiness is blocked behind Loaded while the host hides it.");
        int prepare = main.IndexOf("private async Task PrepareStationContentAsync()", StringComparison.Ordinal);
        Require(main[prepare..main.IndexOf("private async Task RunItemReorderProbeAsync", prepare, StringComparison.Ordinal)]
                .Contains("_ = RunSafelyAsync(async () =>"),
            "Station's first expansion must not wait for a directory scan.");

        var display = new DisplayInfo("station", new NativeMethods.RECT { Left = -2560, Top = 0, Right = 0, Bottom = 1440 },
            new NativeMethods.RECT { Left = -2560, Top = 0, Right = 0, Bottom = 1380 }, 1.25);
        Require(DisplayPlacementService.IsStationHotZone(new() { X = -1, Y = 1400 }, display, OrganizerDockEdge.Right, 4),
            "Right physical edge must include the taskbar strip.");
        Require(DisplayPlacementService.IsStationHotZone(new() { X = -5, Y = 500 }, display, OrganizerDockEdge.Right, 4) &&
                !DisplayPlacementService.IsStationHotZone(new() { X = -6, Y = 500 }, display, OrganizerDockEdge.Right, 4),
            "4 DIP at 125% must occupy exactly 5 physical pixels.");
        Require(!DisplayPlacementService.IsStationHotZone(new() { X = 0, Y = 500 }, display, OrganizerDockEdge.Right, 4),
            "Adjacent display must not activate this station.");
        Console.WriteLine("PASS --station-startup-activation: startup wiring + real hot-zone math; actual hidden HWND startup requires user verification.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task DeleteChoiceAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"TuckPane-delete-choice-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool oldSetting in new[] { true, false })
            {
                string path = Path.Combine(root, "state.json");
                var original = new AppStateV2();
                string json = JsonSerializer.Serialize(original).Replace(
                    "\"GlobalSettings\":{", $"\"GlobalSettings\":{{\"MoveOrganizerFilesToDesktopOnDelete\":{oldSetting.ToString().ToLowerInvariant()},");
                await File.WriteAllTextAsync(path, json);
                var store = new StateStore(path);
                AppStateV2 loaded = await store.LoadAsync();
                Require(loaded.SchemaVersion == original.SchemaVersion, "Deletion choice must not bump the schema.");
                await store.SaveAsync(loaded);
                Require(!(await File.ReadAllTextAsync(path)).Contains("MoveOrganizerFilesToDesktopOnDelete"),
                    "Both values of the obsolete preference must be ignored and omitted on save.");
            }
            var method = typeof(AppHost).GetMethod(nameof(AppHost.DeleteOrganizerAsync))!;
            Require(method.GetParameters() is var parameters && parameters.Length == 2 &&
                    parameters[1].ParameterType == typeof(OrganizerDeleteDisposition) && !parameters[1].IsOptional,
                "Deletion must require an explicit per-request disposition.");

            // Wiring checks only: these do not claim to exercise a real dialog or file transfer.
            string source = Path.Combine(Environment.CurrentDirectory, "src", "TuckPane");
            foreach (string file in new[] { "MainWindow.xaml.cs", "ConsoleWindow.xaml.cs" })
            {
                string code = await File.ReadAllTextAsync(Path.Combine(source, file));
                Require(code.Contains("await OwnedDialogWindow.ShowDeleteChoiceAsync(") &&
                        code.Contains("if (choice is not OrganizerDeleteDisposition disposition) return;") &&
                        code.Contains("DeleteOrganizerAsync(") && code.Contains(", disposition)"),
                    $"{file} must use the shared choice dialog and stop on cancellation.");
                Require(!code.Contains("MoveOrganizerFilesToDesktopOnDelete"), "UI still consults old preference.");
            }
            foreach (string language in new[] { "zh-CN", "en-US", "ja-JP" })
            {
                var values = XDocument.Load(Path.Combine(source, "Strings", language, "Resources.resw"))
                    .Root!.Elements("data").Where(x => ((string?)x.Attribute("name"))?.StartsWith("Delete", StringComparison.Ordinal) == true)
                    .ToDictionary(x => (string)x.Attribute("name")!, x => (string)x.Element("value")!);
                foreach (string key in new[] { "DeleteChoiceMessageFormat", "DeleteMoveFolderToDesktop", "DeleteKeepFolderInPlace", "DeleteOrganizer" })
                    Require(values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value), $"Missing {language}/{key}.");
                Require(string.Format(values["DeleteChoiceMessageFormat"], "C:\\isolated\\folder").Contains("C:\\isolated\\folder"), "Choice text must identify the directory.");
            }
            Console.WriteLine("PASS --delete-choice: legacy JSON round-trip, explicit API, cancel wiring, three languages (no UI/file moves).");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
