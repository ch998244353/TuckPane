using System.Numerics;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;
using Point = Windows.Foundation.Point;

// This change only: no HWND creation, input simulation, or legacy suite dispatch.
internal static class DragDropFixChecks
{
    internal static async Task RunAsync(string? area)
    {
        if (area is not (null or "boundary" or "dock" or "layering"))
            throw new ArgumentException("Expected --drag-drop-fixes [boundary|dock|layering].");
        if (area is null or "boundary") Boundary();
        if (area is null or "dock") await DockAsync();
        if (area is null or "layering") Layering();
        Console.WriteLine($"PASS --drag-drop-fixes {area ?? "all"}: focused logic/source checks; visual drag/drop remains user acceptance.");
    }

    private static void Boundary()
    {
        var canvas = new NativeMethods.RECT { Left = 100, Top = 200, Right = 500, Bottom = 600 };
        IconDragBoundary icon = IconDragBoundary.Create(canvas, new Rect(8, 4, 64, 64),
            new Point(40, 36), 1, new Point(20, 12), 1);
        // Off-centre grip: the visible icon occupies [pointer - (12,8), pointer + (52,56)].
        (double x, double y, bool outside)[] cases =
        [
            (49, 350, false), (48, 350, true), (511, 350, false), (512, 350, true),
            (300, 145, false), (300, 144, true), (300, 607, false), (300, 608, true),
            (49, 145, false), (48, 145, true), (511, 607, false), (511, 608, true),
            (99, 350, false), (501, 350, false), (300, 199, false), (300, 601, false)
        ];
        foreach (var (x, y, outside) in cases)
            Require(icon.IsFullyOutside(x, y) == outside, $"Incorrect full-icon exit at ({x}, {y}).");

        // Compact row: grip is on its text, but only the 24 DIP icon counts.
        // With 125% drag scale and 150% DPI it occupies X[-150,-105], Y[-26.25,18.75].
        IconDragBoundary compact = IconDragBoundary.Create(canvas, new Rect(12, 6, 24, 24),
            new Point(60, 20), 1.25, new Point(100, 20), 1.5);
        foreach (var (x, y, outside) in new (double, double, bool)[]
        {
            (206, 350, false), (205, 350, true), (649, 350, false), (650, 350, true),
            (400, 182.25, false), (400, 181.25, true), (400, 625.25, false), (400, 626.25, true)
        })
            Require(compact.IsFullyOutside(x, y) == outside, "Compact text grip/DPI must use the visible icon rectangle.");
    }

    private static async Task DockAsync()
    {
        foreach (DockOrientation orientation in new[] { DockOrientation.Horizontal, DockOrientation.Vertical })
        {
            DockGeometry dock = DockLayoutMath.Calculate(3, 64, orientation);
            Vector2 axis = orientation == DockOrientation.Horizontal ? Vector2.UnitX : Vector2.UnitY;
            Vector2 middle = (dock.ItemCenter(0) + dock.ItemCenter(1)) / 2;
            Require(dock.InsertionIndex(dock.ItemCenter(0) - axis * 100) == 0 &&
                dock.InsertionIndex(middle) == 1 && dock.InsertionIndex(dock.ItemCenter(2) + axis * 100) == 3,
                $"{orientation}: incoming items must select head, middle, and tail gaps.");
            DockGeometry empty = DockLayoutMath.Calculate(0, 64, orientation);
            Require(empty.InsertionIndex(Vector2.Zero) == 0, "An empty Dock accepts its first slot.");

            var gap = new DockIncomingGap(1);
            DockGeometry preview = DockLayoutMath.Calculate(4, 64, orientation);
            Require(!gap.Update(preview.ItemCenter(1), preview, 3), "Opening a gap must not displace the selected gap.");
            Require(!gap.Update(preview.ItemCenter(1) + axis * (float)(preview.Pitch * .4), preview, 3),
                "Pointer movement inside the opened slot must not oscillate its position.");
            Require(gap.Update(axis * 10000, preview, 3) && gap.Index == 3, "A preview can move to the tail.");
            Require(gap.Update(axis * -10000, preview, 3) && gap.Index == 0, "A preview can move to the head.");
            gap.Freeze(["a", "b", "c"]);
            Require(!gap.Update(axis * 10000, preview, 3) && gap.Index == 0,
                "Drop-time selection must remain frozen during asynchronous transfer.");
            var emptyGap = new DockIncomingGap(0);
            Require(!emptyGap.Update(axis * 10000, DockLayoutMath.Calculate(1, 64, orientation), 0),
                "The first incoming slot must remain stable on an empty Dock.");
        }

        string root = Path.Combine(Path.GetTempPath(), "TuckPane-drag-drop-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
        try
        {
            string[] original = ["a.txt", "b.txt", "c.txt"];
            var gap = new DockIncomingGap(1);
            DockGapAnchor anchor = gap.Freeze(original);
            TransferOutcome[] outcomes =
            [
                new("same.txt", Path.Combine(root, "same (2).txt"), TransferStatus.Moved, ""),
                new("failed.txt", null, TransferStatus.Failed, "controlled failure"),
                new("link.exe", Path.Combine(root, "link.lnk"), TransferStatus.ShortcutCreated, ""),
                new("cancelled.txt", Path.Combine(root, "cancelled.txt"), TransferStatus.Cancelled, ""),
                new("copied.txt", Path.Combine(root, "copied.txt"), TransferStatus.CopiedSourceRetained, "")
            ];
            string[] expected = ["a.txt", "same (2).txt", "link.lnk", "copied.txt", "b.txt", "c.txt"];
            List<string> result = anchor.Insert(original, DockGapAnchor.SuccessfulNames(outcomes));
            Require(result.SequenceEqual(expected), "Insert only successful actual target names, in input order at the frozen gap.");
            Require(original.SequenceEqual(new[] { "a.txt", "b.txt", "c.txt" }), "Preview/commit calculation must not mutate the original order.");
            Require(anchor.Insert(["watcher.txt", "a.txt", "b.txt", "c.txt"], ["new.txt"])
                .SequenceEqual(new[] { "watcher.txt", "a.txt", "new.txt", "b.txt", "c.txt" }),
                "Frozen neighbour identity must survive unrelated directory refreshes.");
            Require(anchor.Insert(["a.txt", "c.txt"], ["new.txt"])
                .SequenceEqual(new[] { "a.txt", "new.txt", "c.txt" }), "A removed next neighbour must fall back to the surviving previous neighbour.");
            Require(anchor.Insert(["a.txt", "same (2).txt", "b.txt", "c.txt"], ["same (2).txt"])
                .SequenceEqual(new[] { "a.txt", "same (2).txt", "b.txt", "c.txt" }),
                "A directory refresh that already contains the new file must not duplicate it.");

            var organizer = new OrganizerDefinition
            {
                PlacementMode = OrganizerPlacementMode.Dock, StorageAbsolutePath = root, ItemOrder = result
            };
            var store = new StateStore(Path.Combine(root, "state.json"));
            await store.SaveAsync(new AppStateV2 { Organizers = [organizer] });
            AppStateV2 reloaded = await store.LoadAsync();
            Require(reloaded.Organizers.Single(item => item.Id == organizer.Id).ItemOrder.SequenceEqual(expected),
                "Dock insertion order must survive the existing state save/reload path.");

            string incoming = Source("MainWindow.DockIncomingDrop.cs");
            string cleanup = Method(incoming, "internal void ClearIncomingDockGap(");
            Require(cleanup.Contains("_dockIncomingGap = null;") && cleanup.Contains("_dockIncomingOwner = null;") &&
                cleanup.Contains("StopDockIncomingRendering();") && cleanup.Contains("ApplyDockLayout();"),
                "Preview teardown must clear ownership, stop rendering, and restore layout.");
            Require(cleanup.Contains("_dockIncomingGuard?.Stop();") && cleanup.Contains("_dragHasLocalFile = false;") &&
                Method(incoming, "private void StartDockIncomingGuard(").Contains("if (DockFileDragEnded()) ClearIncomingDockGap(_dockFileDragOwner);"),
                "Cancelled file drags must clear the preview and stop its cancellation guard.");
            string main = Source("MainWindow.xaml.cs");
            string catalog = Method(main, "private async Task RefreshCatalogCoreAsync(");
            int pending = catalog.IndexOf("if (_dockIncomingGap is not null)", StringComparison.Ordinal);
            int commit = catalog.IndexOf("Volatile.Write(ref _knownPortableNames", StringComparison.Ordinal);
            Require(pending >= 0 && commit > pending && catalog[pending..commit].Contains("_catalogRefreshPending = true;") &&
                catalog[pending..commit].Contains("return;"),
                "A scan completed during the preview must defer its UI commit and remain pending.");
            string returning = Method(incoming, "internal void UpdateIncomingDockGap(NativeMethods.POINT");
            string candidate = Method(Source("AppHost.cs"), "internal void UpdateOrganizerDragHover(MainWindow source, Guid draggedOrganizerId");
            Require(returning.Contains("ReferenceEquals(owner, this) && IsReturningContainedDockDrag") &&
                returning.Contains("session.UpdateTarget(") && returning.Contains("StartGapTransitionIfReady(session,") &&
                candidate.Contains("candidate.IsDock && draggedOrganizerId != source.OrganizerId") &&
                Method(main, "internal bool TryGetOrganizerDropIndex(")
                    .Contains("returning.TargetIndex + (returning.SourceIndex < returning.TargetIndex ? 1 : 0)") &&
                cleanup.Contains("session.LeaveInternalPreview();") && cleanup.Contains("ResetGapTransitionState();"),
                "Returning a child to its source Dock must reuse internal gap preview, compensate removal index, and clear the preview on exit.");
            string import = Method(main, "private async Task ImportFromDragAsync(");
            int finalCleanup = import.LastIndexOf("finally", StringComparison.Ordinal);
            Require(finalCleanup >= 0 && import[finalCleanup..].Contains("ClearIncomingDockGap(dockToken)"),
                "Failed or cancelled file drops must tear down their own preview in finally.");
        }
        finally
        {
            await AppLogger.FlushAsync();
            Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", previousRoot);
            string resolved = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(resolved) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) &&
                Path.GetFileName(resolved).StartsWith("TuckPane-drag-drop-", StringComparison.Ordinal), "Cleanup escaped the test directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static void Layering()
    {
        string main = Source("MainWindow.xaml.cs");
        string bounds = Method(main, "private void ApplyBounds(");
        Require(bounds.IndexOf("RaiseActiveCompactOrganizerDrags(this)", StringComparison.Ordinal) >
            bounds.IndexOf("NativeMethods.SetWindowPos(", StringComparison.Ordinal),
            "Target bounds/z-order changes must finish before raising the dragged visual.");
        string expand = Method(main, "private async Task ExpandTransitionAsync(");
        Require(expand.LastIndexOf("RaiseActiveCompactOrganizerDrags(this)", StringComparison.Ordinal) >
            expand.LastIndexOf("UpdateCanvasResizeEdgeWindows(show: true)", StringComparison.Ordinal),
            "Asynchronous expansion completion must restore the source above the final target layout without pointer movement.");
        string host = Method(Source("AppHost.cs"), "internal void RaiseActiveCompactOrganizerDrags(");
        string raise = Method(main, "internal void RaiseActiveCompactOrganizerDrag(");
        Require(host.Contains("window.IsOrganizerDragVisualActive") && host.Contains("!ReferenceEquals(window, target)") &&
            main.Contains("IsOrganizerDragVisualActive => !_closing && (IsCompactOrganizerDragActive || _hasContainedDragPreview)") &&
            raise.Contains("if (!IsOrganizerDragVisualActive) return;"),
            "Only active desktop or contained drag visuals may be raised, excluding the target and closed windows.");
        Require(raise.Contains("NativeMethods.HWND_TOPMOST") && raise.Contains("NativeMethods.SWP_NOACTIVATE") &&
            raise.Contains("NativeMethods.SWP_NOMOVE") && raise.Contains("NativeMethods.SWP_NOSIZE"),
            "Re-raising a stationary source must neither activate nor reposition it.");
        Require(Method(main, "internal void SetContained(").Contains("_hasContainedDragPreview = false;"),
            "Finalizing containment must clear the drag visual's eligibility for later raises.");
        Console.WriteLine("Layering: production wiring only; HWND/visual behavior was not exercised.");
    }

    private static string Source(string file)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "src", "TuckPane", file);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException($"Run the checks from the source checkout; missing {file}.");
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Require(start >= 0, $"Missing production method: {signature}");
        int body = source.IndexOf('{', start), depth = 0;
        for (int index = body; index >= 0 && index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            if (source[index] == '}' && --depth == 0) return source[start..(index + 1)];
        }
        throw new InvalidOperationException($"Unterminated production method: {signature}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
