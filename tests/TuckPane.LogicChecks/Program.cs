using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using TuckPane;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using System.Text.Json;
using System.Xml.Linq;

// Sep 04 drag/resize stability gate.  This entry point is intentionally
// source/pure-logic based: it never creates a WinUI window, starts OLE, or
// drives a mouse/keyboard.  Keep this branch focused on the new performance
// contracts only; legacy selectors below remain unchanged.
if (args is ["--sep04-drag-stability"])
{
    var failures = new List<string>();

    static void Require(List<string> list, bool condition, string message)
    {
        if (!condition) list.Add(message);
    }

    static string ExtractBlock(string source, string marker)
    {
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int open = source.IndexOf('{', start);
        if (open < 0) return source[start..];
        int depth = 0;
        bool inString = false;
        bool inChar = false;
        bool escaped = false;
        for (int index = open; index < source.Length; index++)
        {
            char ch = source[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if ((inString || inChar) && ch == '\\')
            {
                escaped = true;
                continue;
            }
            if (!inChar && ch == '"')
            {
                inString = !inString;
                continue;
            }
            if (!inString && ch == '\'')
            {
                inChar = !inChar;
                continue;
            }
            if (inString || inChar) continue;
            if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return source[start..(index + 1)];
        }
        return source[start..];
    }

    static int CountOccurrences(string source, string value)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value)) return 0;
        int count = 0;
        for (int offset = 0; (offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0;
             offset += value.Length)
        {
            count++;
        }
        return count;
    }

    string sourceRoot = Environment.CurrentDirectory;
    while (!File.Exists(Path.Combine(sourceRoot, "src", "TuckPane", "TuckPane.csproj")) &&
           Directory.GetParent(sourceRoot) is DirectoryInfo parent)
    {
        sourceRoot = parent.FullName;
    }

    string Read(string relative) => File.ReadAllText(Path.Combine(sourceRoot, "src", "TuckPane", relative));
    string mainSource = Read("MainWindow.xaml.cs");
    string storageSource = Read("Services\\StorageService.cs");
    string shellSource = Read("Services\\ShellDragService.cs");
    string iconSource = Read("Services\\IconCacheService.cs");
    string loggerSource = Read("Services\\AppLogger.cs");

    // Drag-in progress must not create a Dispatcher-capturing Progress<T>
    // for every copied chunk.  Null or an explicitly non-capturing/throttled
    // sink is acceptable for these no-progress UI paths.
    string importBlock = ExtractBlock(mainSource, "private async Task ImportFromDragAsync");
    string pasteBlock = ExtractBlock(mainSource, "private async void PasteMenuItem_Click");
    Require(failures,
        !importBlock.Contains("new Progress<TransferProgress>(_ => { })", StringComparison.Ordinal) &&
        !pasteBlock.Contains("new Progress<TransferProgress>(_ => { })", StringComparison.Ordinal),
        "拖入/粘贴路径仍创建捕获 UI 上下文的空 Progress<TransferProgress>。");
    Require(failures,
        importBlock.Contains("progress = null", StringComparison.Ordinal) ||
        importBlock.Contains("NonCapturing", StringComparison.OrdinalIgnoreCase) ||
        importBlock.Contains("Throttl", StringComparison.OrdinalIgnoreCase),
        "拖入路径没有使用 null、非捕获或节流进度 sink。");

    // FileSystemWatcher notifications must collapse to one pending dispatcher
    // callback instead of flooding the UI queue.
    string watcherBlock = ExtractBlock(mainSource, "private void Watcher_Changed");
    Require(failures,
        watcherBlock.Contains("Interlocked.Exchange", StringComparison.Ordinal) &&
        watcherBlock.Contains("_watcherRefreshPosted", StringComparison.Ordinal),
        "FileSystemWatcher 事件没有使用原子 pending 标志合并刷新。");

    // Refresh uses a cancellation/generation gate and performs filesystem
    // enumeration off the UI thread.  Icon completion must be bounded rather
    // than issuing unbounded concurrent Shell work.
    Require(failures,
        mainSource.Contains("_catalogRefreshGeneration", StringComparison.Ordinal) &&
        mainSource.Contains("_catalogRefreshCancellation", StringComparison.Ordinal) &&
        mainSource.Contains("Interlocked.Increment(ref _catalogRefreshGeneration)", StringComparison.Ordinal),
        "catalog refresh 缺少可取消、带 generation 的旧任务淘汰契约。");
    string refreshBlock = ExtractBlock(mainSource, "private async Task RefreshCatalogCoreAsync");
    Require(failures,
        refreshBlock.Contains("Task.Run", StringComparison.Ordinal) &&
        refreshBlock.Contains("generation != Volatile.Read", StringComparison.Ordinal),
        "catalog 刷新没有在 worker 线程读取目录，或没有阻止旧 generation 覆盖新列表。");
    string iconBlock = ExtractBlock(mainSource, "private async Task LoadIconAsync");
    Require(failures,
        (mainSource + iconSource).Contains("SemaphoreSlim", StringComparison.Ordinal) ||
        (mainSource + iconSource).Contains("Parallel.ForEachAsync", StringComparison.Ordinal) ||
        (mainSource + iconSource).Contains("MaxDegreeOfParallelism", StringComparison.Ordinal) ||
        iconBlock.Contains("WaitAsync", StringComparison.Ordinal),
        "图标后台补全没有可观察的限并发门控（SemaphoreSlim/等价队列）。");

    // DragOver must be a cheap read of DragEnter's cached classification.  A
    // synchronous IDataObject COM probe on every pointer update is a freeze
    // amplifier, especially when the shell owns the drag loop.
    string itemsDragOver = ExtractBlock(mainSource, "private void ItemsGrid_DragOver");
    string rootDragOver = ExtractBlock(mainSource, "private void WindowRoot_DragOver");
    Require(failures,
        !itemsDragOver.Contains("HasLocalFileDrop(e.DataView)", StringComparison.Ordinal) &&
        !rootDragOver.Contains("HasLocalFileDrop(e.DataView)", StringComparison.Ordinal),
        "DragOver 仍重复执行同步 DataView/Shell 文件探测，没有复用 DragEnter 缓存。");
    Require(failures,
        mainSource.Contains("DragEnter", StringComparison.Ordinal) &&
        (mainSource.Contains("_dragHasLocalFile", StringComparison.OrdinalIgnoreCase) ||
         mainSource.Contains("_externalDragHasFiles", StringComparison.OrdinalIgnoreCase) ||
         mainSource.Contains("_cachedFileDrop", StringComparison.OrdinalIgnoreCase)),
        "拖动文件类型没有在 DragEnter 阶段缓存供 DragOver/Drop 复用。");

    // OLE callbacks must not enumerate windows or synchronously probe the
    // desktop on every QueryContinueDrag call.  A per-drag target snapshot is
    // required; callback work should remain bounded and lightweight.
    string queryBlock = ExtractBlock(shellSource, "public int QueryContinueDrag");
    Require(failures,
        (!queryBlock.Contains("IsDesktopTarget(", StringComparison.Ordinal) ||
         queryBlock.Contains("desktopSnapshot.IsDesktopTarget(", StringComparison.Ordinal)) &&
        !queryBlock.Contains("FindDesktopIconView", StringComparison.Ordinal) &&
        !queryBlock.Contains("EnumWindows", StringComparison.Ordinal) &&
        !queryBlock.Contains("SendMessageTimeout", StringComparison.Ordinal) &&
        !queryBlock.Contains("GetProcessById", StringComparison.Ordinal),
        "Shell OLE QueryContinueDrag 仍包含桌面窗口/进程同步探测，可能阻塞拖动回调。");
    Require(failures,
        shellSource.Contains("DesktopTargetSnapshot", StringComparison.OrdinalIgnoreCase) ||
        shellSource.Contains("CachedDesktopTarget", StringComparison.OrdinalIgnoreCase) ||
        shellSource.Contains("desktopTargetCache", StringComparison.OrdinalIgnoreCase) ||
        shellSource.Contains("_desktopTarget", StringComparison.OrdinalIgnoreCase),
        "Shell 拖动没有可复用的桌面目标快照/缓存入口。");

    // Hook startup/shutdown must never synchronously wait on a hook thread
    // from the UI thread.  Rendering-loop fallback remains the safe path.
    string ensureHook = ExtractBlock(mainSource, "private bool EnsureItemDragBoundaryHook");
    string shutdownHook = ExtractBlock(mainSource, "private void ShutdownItemDragBoundaryHook");
    Require(failures,
        !ensureHook.Contains(".Wait(500)", StringComparison.Ordinal) &&
        !shutdownHook.Contains(".Join(500)", StringComparison.Ordinal),
        "项目外拖 hook 安装/退出仍在 UI 线程同步 Wait/Join 500ms。");
    Require(failures,
        mainSource.Contains("渲染循环边界检测", StringComparison.Ordinal) ||
        mainSource.Contains("rendering loop", StringComparison.OrdinalIgnoreCase),
        "hook 不可用时缺少渲染循环边界检测降级路径。");

    // Drag entry points need Task-returning boundaries so cleanup exceptions
    // cannot escape async-void event handlers.  The shared RunSafelyAsync
    // wrapper is still required at event seams.
    foreach (string marker in new[]
    {
        "private async void BeginContainedOrganizerDrag",
        "private async void BeginNativeShellDrag",
        "private async void BeginXamlShellDrag"
    })
    {
        Require(failures, !mainSource.Contains(marker, StringComparison.Ordinal),
            $"拖动入口仍是 {marker}，清理异常可能逃逸 Dispatcher。");
    }
    Require(failures,
        mainSource.Contains("RunSafelyAsync", StringComparison.Ordinal) &&
        mainSource.Contains("cleanupError", StringComparison.Ordinal),
        "拖动清理没有统一安全异常边界。");

    // Contained organizer dragging must have explicit cancellation, capture
    // loss and maximum-duration exits; a bare while + Delay(16) can otherwise
    // leave _shellDragActive set forever.
    string nestedDrag = ExtractBlock(mainSource, "private async Task BeginContainedOrganizerDrag");
    if (nestedDrag.Length == 0)
        nestedDrag = ExtractBlock(mainSource, "private async void BeginContainedOrganizerDrag");
    Require(failures,
        nestedDrag.Contains("CancellationToken", StringComparison.Ordinal) ||
        nestedDrag.Contains("CancellationTokenSource", StringComparison.Ordinal),
        "嵌套收纳窗拖动没有可传播的取消 token。");
    Require(failures,
        nestedDrag.Contains("GetElapsedTime", StringComparison.Ordinal) ||
        nestedDrag.Contains("Max", StringComparison.OrdinalIgnoreCase) &&
        nestedDrag.Contains("TimeSpan", StringComparison.Ordinal),
        "嵌套收纳窗拖动没有最大持续时间/超时出口。");

    // Window movement must be consumed by one render tick.  Pointer handlers
    // may record input, but must not submit competing SetWindowPos calls.
    string dragRendering = ExtractBlock(mainSource, "private void DragRendering");
    Require(failures,
        dragRendering.Contains("CommitWidgetDrag", StringComparison.Ordinal) &&
        mainSource.Contains("_pendingWidgetDragCursor", StringComparison.Ordinal) &&
        mainSource.Contains("_hasPendingWidgetDragCursor", StringComparison.Ordinal) &&
        dragRendering.Count(ch => ch == ';') > 0,
        "窗口拖动没有通过单一渲染节拍消费待处理光标。");
    string expandedMoved = ExtractBlock(mainSource, "private void ExpandedView_PointerMoved");
    string compactMoved = ExtractBlock(mainSource, "private void CompactTile_PointerMoved");
    Require(failures,
        !expandedMoved.Contains("UpdateWidgetDragFromCursor", StringComparison.Ordinal) &&
        !compactMoved.Contains("UpdateWidgetDragFromCursor", StringComparison.Ordinal),
        "PointerMoved 仍直接提交窗口位置，和渲染循环形成竞争。");
    string commitBlock = ExtractBlock(mainSource, "private void CommitWidgetDrag");
    Require(failures,
        CountOccurrences(commitBlock, "NativeMethods.SetWindowPos(") <= 1,
        "单次窗口拖动提交路径包含多次 SetWindowPos，未满足每帧单次提交契约。");

    // Runtime-only pure TransferQueue checks.  No windows, COM or real input
    // are involved; this verifies serial ordering, cancellation and idle
    // convergence without re-running legacy transfer tests.
    try
    {
        var queue = new TransferQueue();
        int active = 0;
        int maximumActive = 0;
        var order = new List<int>();
        Task<int>[] serialTasks = Enumerable.Range(0, 3).Select(index => queue.RunAsync(async token =>
        {
            int now = Interlocked.Increment(ref active);
            int observed;
            do
            {
                observed = Volatile.Read(ref maximumActive);
                if (now <= observed) break;
            }
            while (Interlocked.CompareExchange(ref maximumActive, now, observed) != observed);
            await Task.Delay(20, token);
            lock (order) order.Add(index);
            Interlocked.Decrement(ref active);
            return index;
        })).ToArray();
        await Task.WhenAll(serialTasks);
        Require(failures, maximumActive == 1, "TransferQueue 同时执行了多个传输 action。");
        Require(failures, order.Count == 3 && order.Distinct().Count() == 3,
            "TransferQueue 没有完成每个排队 action，或发生了重复执行。");
        Require(failures, await queue.WaitForIdleAsync(TimeSpan.FromSeconds(1)), "TransferQueue 完成后未及时进入 idle。");

        var cancelQueue = new TransferQueue();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> cancelled = cancelQueue.RunAsync(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 1;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancelQueue.CancelCurrent();
        try
        {
            await cancelled;
            failures.Add("TransferQueue.CancelCurrent 未取消当前 action。");
        }
        catch (OperationCanceledException) { }
        Require(failures, await cancelQueue.WaitForIdleAsync(TimeSpan.FromSeconds(1)), "CancelCurrent 后 TransferQueue 未及时 idle。");
    }
    catch (Exception ex)
    {
        failures.Add($"TransferQueue 纯逻辑专项执行异常：{ex.GetType().Name}: {ex.Message}");
    }

    // Recursive filesystem operations must check cancellation in enumeration,
    // verification and copy loops, not only at the outer batch boundary.
    string copyDirectoryBlock = ExtractBlock(storageSource, "private static async Task CopyDirectoryAsync");
    Require(failures,
        copyDirectoryBlock.Contains("CancellationToken", StringComparison.Ordinal) &&
        copyDirectoryBlock.Contains("ThrowIfCancellationRequested", StringComparison.Ordinal),
        "CopyDirectoryAsync 缺少递归枚举循环中的取消检查。");
    string copyFileBlock = ExtractBlock(storageSource, "private static async Task CopyFileAsync");
    Require(failures,
        copyFileBlock.Contains("CancellationToken", StringComparison.Ordinal) &&
        copyFileBlock.Contains("ReadAsync", StringComparison.Ordinal) &&
        copyFileBlock.Contains("WriteAsync", StringComparison.Ordinal) &&
        copyFileBlock.Contains("cancellationToken", StringComparison.Ordinal),
        "CopyFileAsync 没有把取消 token 传递到异步读写 I/O。");
    string verifyBlock = ExtractBlock(storageSource, "private static void VerifyEquivalent");
    Require(failures,
        verifyBlock.Contains("CancellationToken", StringComparison.Ordinal) &&
        verifyBlock.Contains("BuildManifest(source, cancellationToken)", StringComparison.Ordinal) &&
        verifyBlock.Contains("BuildManifest(destination, cancellationToken)", StringComparison.Ordinal),
        "VerifyEquivalent 没有把取消 token 传播到递归清单校验。");
    string manifestBlock = ExtractBlock(storageSource, "private static DirectoryManifest BuildManifest");
    Require(failures,
        manifestBlock.Contains("CancellationToken", StringComparison.Ordinal) &&
        manifestBlock.Contains("ThrowIfCancellationRequested", StringComparison.Ordinal),
        "BuildManifest 缺少递归枚举循环中的取消检查。");
    Require(failures,
        storageSource.Contains("TryDelete(staging)", StringComparison.Ordinal) &&
        storageSource.Contains("OperationCanceledException", StringComparison.Ordinal),
        "传输取消后没有统一清理 staging 并返回 Cancelled 状态。");

    // Performance trace is opt-in and logging failures must not affect the app.
    Require(failures,
        loggerSource.Contains("TUCKPANE_PERF_TRACE", StringComparison.Ordinal) &&
        loggerSource.Contains("Channel.CreateBounded", StringComparison.Ordinal) &&
        loggerSource.Contains("catch", StringComparison.Ordinal),
        "AppLogger 缺少默认关闭的性能开关/有界异步日志及失败隔离。");

    if (failures.Count > 0)
        throw new InvalidOperationException(
            $"Sep 04 drag stability gate failed ({failures.Count}): {string.Join("；", failures)}");

    Console.WriteLine("PASS: Sep 04 drag stability");
    return;
}

// Sep 04 focused contracts for the bottom Station layer transition, organizer
// text-color migration/contrast and smooth wheel input.  This selector is
// intentionally pure-logic/source based: it never creates a window or drives
// real input devices.
if (args is ["--sep04-bottom-name-wheel"])
{
    var failures = new List<string>();
    static void Require(List<string> list, bool condition, string message)
    {
        if (!condition) list.Add(message);
    }

    string sourceRoot = Environment.CurrentDirectory;
    while (!Directory.Exists(Path.Combine(sourceRoot, "src")) &&
           Directory.GetParent(sourceRoot) is DirectoryInfo parent)
        sourceRoot = parent.FullName;
    string Read(string relative) => File.ReadAllText(Path.Combine(sourceRoot, "src", "TuckPane", relative));
    string mainSource = Read("MainWindow.xaml.cs");
    string desktopLayer = Read("Services\\DesktopLayerService.cs");
    string consoleSource = Read("ConsoleWindow.xaml.cs");
    string consoleXaml = Read("ConsoleWindow.xaml");

    // Bottom Station must leave the desktop owner before it is shown/raised;
    // restoring the owner must not carry SWP_SHOWWINDOW.  Keep this as a
    // source-level contract so it remains deterministic without HWND tests.
    int expandStart = mainSource.IndexOf("private async Task ExpandAsync", StringComparison.Ordinal);
    int collapseStart = mainSource.IndexOf("private async Task CollapseAsync", StringComparison.Ordinal);
    int expandEnd = collapseStart > expandStart ? collapseStart : mainSource.Length;
    string expandSource = expandStart >= 0 ? mainSource[expandStart..expandEnd] : string.Empty;
    int ownerDetach = expandSource.IndexOf("SetExpanded(true, stayTopmost: true)", StringComparison.Ordinal);
    int expandedShow = expandSource.IndexOf("ApplyBounds(expandedBounds, show: true", StringComparison.Ordinal);
    Require(failures, ownerDetach >= 0 && expandedShow >= 0 && ownerDetach < expandedShow,
        "Station 展开未先脱离 desktop owner/topmost，再移动并显示自身。");
    Require(failures,
        desktopLayer.Contains("SWP_NOOWNERZORDER", StringComparison.Ordinal) &&
        desktopLayer.Contains("HWND_TOPMOST", StringComparison.Ordinal) &&
        desktopLayer.Contains("HWND_NOTOPMOST", StringComparison.Ordinal),
        "DesktopLayerService 缺少安全的 topmost/no-owner-z-order 转换契约。");
    var bottomDisplay = new DisplayInfo(
        "sep04-bottom-display",
        new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
        new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 },
        1);
    Require(failures,
        DisplayPlacementService.IsStationHotZone(
            new NativeMethods.POINT { X = 960, Y = 1075 }, bottomDisplay,
            OrganizerDockEdge.Bottom, GlobalSettings.DefaultStationActivationDistanceDip) &&
        !DisplayPlacementService.IsStationHotZone(
            new NativeMethods.POINT { X = 960, Y = 5 }, bottomDisplay,
            OrganizerDockEdge.Bottom, GlobalSettings.DefaultStationActivationDistanceDip) &&
        !DisplayPlacementService.IsStationHotZone(
            new NativeMethods.POINT { X = 1920, Y = 1075 }, bottomDisplay,
            OrganizerDockEdge.Bottom, GlobalSettings.DefaultStationActivationDistanceDip),
        "底部 Station 热区未限制在配置显示器底边，或误触发其他边缘/显示器。");
    Type? layerMath = typeof(OrganizerInteractionMath).Assembly.GetType("TuckPane.Core.StationLayerTransitionMath");
    var expandPlanProperty = layerMath?.GetProperty("ExpandPlan",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
    var collapsePlanProperty = layerMath?.GetProperty("CollapsePlan",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
    string[] expandPlan = (expandPlanProperty?.GetValue(null) as System.Collections.IEnumerable)?.Cast<object>()
        .Select(item => item.ToString() ?? string.Empty).ToArray() ?? [];
    string[] collapsePlan = (collapsePlanProperty?.GetValue(null) as System.Collections.IEnumerable)?.Cast<object>()
        .Select(item => item.ToString() ?? string.Empty).ToArray() ?? [];
    Require(failures,
        expandPlan.SequenceEqual(["DetachDesktopOwner", "SetTopmostNoActivate", "MoveAndShow"]),
        "Station 展开纯逻辑计划未按脱离 owner→topmost→移动显示顺序定义。");
    Require(failures,
        collapsePlan.SequenceEqual(["Hide", "ClearTopmost", "AttachDesktopOwner"]),
        "Station 收缩纯逻辑计划未按隐藏→解除 topmost→恢复 owner 顺序定义。");
    int reattachOwner = desktopLayer.IndexOf("if (_desktopIconView", StringComparison.Ordinal);
    if (reattachOwner >= 0)
    {
        string ownerRestore = desktopLayer[reattachOwner..];
        int showFlag = ownerRestore.IndexOf("SWP_SHOWWINDOW", StringComparison.Ordinal);
        int ownerChanged = ownerRestore.IndexOf("ownerChanged", StringComparison.Ordinal);
        Require(failures, showFlag < 0 || ownerChanged < 0 || showFlag > ownerRestore.IndexOf("if (!ownerChanged)", StringComparison.Ordinal),
            "恢复 desktop owner 时仍可能使用 SWP_SHOWWINDOW，导致 peer 窗口被整体显示/抬升。");
    }

    // Text color enum/persistence: legacy 0/1 remain White/Black, value 2 is
    // Auto, and missing/invalid values normalize to Auto.
    Require(failures, Enum.GetNames<OrganizerTextColor>().Contains("Auto"),
        "OrganizerTextColor 缺少 Auto 枚举值。");
    Require(failures, (int)OrganizerTextColor.White == 0 && (int)OrganizerTextColor.Black == 1,
        "OrganizerTextColor 旧磁盘数值 0/1 未保持 White/Black 含义。");
    OrganizerTextColor autoValue = (OrganizerTextColor)2;
    Require(failures, GlobalSettings.DefaultOrganizerTextColor == autoValue,
        "名称颜色默认值不是 Auto。");
    Require(failures, GlobalSettings.NormalizeOrganizerTextColor((OrganizerTextColor)99) == autoValue,
        "非法名称颜色没有归一为 Auto。");

    string sep04MigrationRoot = Path.Combine(Path.GetTempPath(), $"TuckPane-sep04-{Guid.NewGuid():N}");
    Directory.CreateDirectory(sep04MigrationRoot);
    try
    {
        async Task<AppStateV2> Load(string json)
        {
            string path = Path.Combine(sep04MigrationRoot, Guid.NewGuid() + ".json");
            await File.WriteAllTextAsync(path, json);
            return await new StateStore(path).LoadAsync();
        }
        AppStateV2 legacy = await Load("{\"SchemaVersion\":10,\"GlobalSettings\":{},\"Organizers\":[]}");
        AppStateV2 missing = await Load("{\"SchemaVersion\":15,\"GlobalSettings\":{},\"Organizers\":[]}");
        AppStateV2 explicitWhite = await Load("{\"SchemaVersion\":15,\"GlobalSettings\":{\"OrganizerTextColor\":0},\"Organizers\":[]}");
        AppStateV2 explicitBlack = await Load("{\"SchemaVersion\":15,\"GlobalSettings\":{\"OrganizerTextColor\":1},\"Organizers\":[]}");
        AppStateV2 explicitAuto = await Load("{\"SchemaVersion\":15,\"GlobalSettings\":{\"OrganizerTextColor\":2},\"Organizers\":[]}");
        AppStateV2 invalid = await Load("{\"SchemaVersion\":15,\"GlobalSettings\":{\"OrganizerTextColor\":99},\"Organizers\":[]}");
        Require(failures, legacy.GlobalSettings.OrganizerTextColor == autoValue && missing.GlobalSettings.OrganizerTextColor == autoValue,
            "旧 Schema/当前 Schema 缺失名称颜色字段没有迁移为 Auto。");
        Require(failures, explicitWhite.GlobalSettings.OrganizerTextColor == OrganizerTextColor.White &&
            explicitBlack.GlobalSettings.OrganizerTextColor == OrganizerTextColor.Black &&
            explicitAuto.GlobalSettings.OrganizerTextColor == autoValue,
            "显式保存的名称颜色值 0/1/2 没有保持 White/Black/Auto。");
        Require(failures, invalid.GlobalSettings.OrganizerTextColor == autoValue,
            "JSON 非法名称颜色值没有归一为 Auto。");
    }
    finally
    {
        if (Directory.Exists(sep04MigrationRoot)) Directory.Delete(sep04MigrationRoot, recursive: true);
    }

    // ThemePalette contrast resolver is pure and deterministic.  Reflection
    // keeps this test compiling against the pre-feature baseline (red first).
    var resolveText = typeof(ThemePalette).GetMethod(
        "ResolveOrganizerTextColor",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
    Require(failures, resolveText is not null,
        "ThemePalette 缺少 ResolveOrganizerTextColor 纯逻辑入口。");
    if (resolveText is not null)
    {
        object? Resolve(OrganizerTextColor mode, ThemeValues theme) =>
            resolveText.Invoke(null, [mode, theme]);
        ThemeValues light = new(0xFFF4F5F7, .35);
        ThemeValues dark = new(0xFF202124, .35);
        object? autoLight = Resolve(autoValue, light);
        object? autoDark = Resolve(autoValue, dark);
        object? white = Resolve(OrganizerTextColor.White, light);
        object? black = Resolve(OrganizerTextColor.Black, dark);
        Require(failures, autoLight is Windows.UI.Color lightColor && lightColor.R < 64 && lightColor.G < 64 && lightColor.B < 64,
            "亮色主题 Auto 名称颜色没有选择高对比度黑色。");
        Require(failures, autoDark is Windows.UI.Color darkColor && darkColor.R > 220 && darkColor.G > 220 && darkColor.B > 220,
            "暗色主题 Auto 名称颜色没有选择高对比度白色。");
        Require(failures, white is Windows.UI.Color wc && wc.R > 240 && wc.G > 240 && wc.B > 240 &&
            black is Windows.UI.Color bc && bc.R < 40 && bc.G < 40 && bc.B < 40,
            "显式白/黑没有返回稳定纯色覆盖。");
    }
    Require(failures,
        consoleXaml.Contains("OrganizerTextColorAuto", StringComparison.Ordinal) &&
        consoleSource.Contains("OrganizerTextColorAuto", StringComparison.Ordinal) &&
        consoleSource.Contains("Tag", StringComparison.Ordinal) &&
        !consoleSource.Contains("SelectedIndex = (int)GlobalSettings.NormalizeOrganizerTextColor", StringComparison.Ordinal),
        "名称颜色设置缺少 Auto 资源或仍依赖 ComboBox.SelectedIndex 数值映射。");
    Require(failures,
        mainSource.Contains("ResolveOrganizerTextColor", StringComparison.Ordinal) &&
        (mainSource.Contains("CompactName", StringComparison.Ordinal) || mainSource.Contains("NameBrush", StringComparison.Ordinal)),
        "收起标题、展开标题和项目名称没有统一接入动态名称画刷刷新。");

    // Wheel action and smooth-state contracts.
    Type wheelType = typeof(OrganizerInteractionMath).Assembly.GetType("TuckPane.Core.OrganizerWheelAction")!;
    Require(failures, wheelType.IsEnum && Enum.GetNames(wheelType).Contains("ScrollGrid"),
        "OrganizerWheelAction 缺少图标模式普通滚动动作 ScrollGrid。");
    var resolveWheel = typeof(OrganizerInteractionMath).GetMethod("ResolveWheelAction",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    if (resolveWheel is not null)
    {
        object? iconAction = resolveWheel.Invoke(null, [true, false, false, false, false, false, false, true]);
        Require(failures, iconAction?.ToString() == "ScrollGrid",
            "图标模式普通滚轮没有进入 ScrollGrid 动作。");
    }
    Require(failures,
        mainSource.Contains("ScrollGrid", StringComparison.Ordinal) &&
        mainSource.Contains("ScrollTo", StringComparison.Ordinal) &&
        mainSource.Contains("PointerWheelChanged", StringComparison.Ordinal),
        "图标/精简滚轮没有接入统一 ScrollTo 平滑滚动入口。");
    Require(failures,
        mainSource.Contains("Handled = true", StringComparison.Ordinal) ||
        mainSource.Contains("e.Handled", StringComparison.Ordinal),
        "图标和精简模式未屏蔽 ScrollView 默认鼠标滚轮，可能发生双重滚动。");
    Require(failures,
        mainSource.Contains("ScrollableHeight", StringComparison.Ordinal) &&
        mainSource.Contains("Remainder", StringComparison.Ordinal) &&
        (mainSource.Contains("PowerSaver", StringComparison.Ordinal) || mainSource.Contains("ReducedMotion", StringComparison.Ordinal)),
        "平滑滚动状态缺少余量、动态 ScrollableHeight 边界或减少动画收敛处理。");

    // Exercise the pure scroll state seam when present: high-resolution wheel
    // remainder, queued targets, hard bounds and critically-damped stepping.
    Type? scrollMath = typeof(OrganizerInteractionMath).Assembly.GetType("TuckPane.Core.OrganizerScrollMath");
    Type? scrollStateType = typeof(OrganizerInteractionMath).Assembly.GetType("TuckPane.Core.SmoothScrollState");
    Require(failures, scrollMath is not null && scrollStateType is not null,
        "缺少 OrganizerScrollMath/SmoothScrollState 纯逻辑滚动入口。");
    if (scrollMath is not null && scrollStateType is not null)
    {
        var state = new SmoothScrollState(0, 0, 0, 0);
        SmoothScrollState half = OrganizerScrollMath.ConsumeWheelDelta(state, 60, 36, 500);
        SmoothScrollState full = OrganizerScrollMath.ConsumeWheelDelta(half, 60, 36, 500);
        Require(failures, half.Remainder == 60 && Math.Abs(half.TargetOffset) < .001,
            "+60 高精度滚轮未保留余量且未提前改变目标。");
        Require(failures, full.Remainder == 0 && Math.Abs(full.TargetOffset) < .001,
            "第二个 +60 未与已有余量合并并在顶部硬边界夹紧。");

        SmoothScrollState queued = OrganizerScrollMath.QueueTarget(
            new SmoothScrollState(24, 80, 0, 0), 36, 200);
        Require(failures, Math.Abs(queued.TargetOffset - 116) < .001,
            "连续输入没有累加到已有目标偏移。");
        SmoothScrollState clamped = OrganizerScrollMath.ClampState(
            new SmoothScrollState(180, 240, 0, 0), 100);
        Require(failures, clamped.CurrentOffset == 100 && clamped.TargetOffset == 100,
            "ScrollableHeight 缩小时 current/target 没有同步夹紧。");
        SmoothScrollState down = OrganizerScrollMath.ConsumeWheelDelta(
            new SmoothScrollState(200, 200, 0, 0), -240, 36, 500);
        Require(failures, down.TargetOffset > 200 && down.Remainder == 0,
            "-240 滚轮未按 Windows 语义向下累计两行。");
        SmoothScrollState stepped = OrganizerScrollMath.Step(
            new SmoothScrollState(0, 180, 0, 0), .016, 300, false);
        Require(failures, stepped.CurrentOffset > 0 && stepped.CurrentOffset < 180,
            "临界阻尼动画帧未在 current 与 target 之间推进。");
        SmoothScrollState reduced = OrganizerScrollMath.Step(
            new SmoothScrollState(0, 180, 0, 0), .016, 300, true);
        Require(failures, reduced.CurrentOffset == 180 && reduced.Velocity == 0,
            "减少动画效果时滚动没有直接收敛到目标。");
        SmoothScrollState invalidRow = OrganizerScrollMath.ConsumeWheelDelta(
            state, 120, double.NaN, 500);
        Require(failures, !double.IsNaN(invalidRow.CurrentOffset) && invalidRow.Remainder == 0,
            "非法行高导致滚动状态 NaN 或残余未清理。");
    }

    if (failures.Count > 0)
        throw new InvalidOperationException($"{failures[0]}（本专项共 {failures.Count} 项契约未满足）");

    Console.WriteLine("PASS: Sep 04 bottom Station, name color and smooth wheel");
    return;
}

if (args is ["--sep03-organizer-visual-input-fixes"])
{
    var failures = new List<string>();
    static void Require(List<string> failures, bool condition, string message)
    {
        if (!condition) failures.Add(message);
    }

    object? InvokeOrganizerMath(string methodName, Type[] parameterTypes, object?[] arguments)
    {
        System.Reflection.MethodInfo? method = typeof(OrganizerInteractionMath).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        Require(failures, method is not null, $"缺少收纳窗专项纯逻辑入口：OrganizerInteractionMath.{methodName}。");
        return method?.Invoke(null, arguments);
    }

    string sourceRoot = Environment.CurrentDirectory;
    string mainWindowXamlPath = Path.Combine(sourceRoot, "src", "TuckPane", "MainWindow.xaml");
    string mainWindowSourcePath = Path.Combine(sourceRoot, "src", "TuckPane", "MainWindow.xaml.cs");
    string consoleXamlPath = Path.Combine(sourceRoot, "src", "TuckPane", "ConsoleWindow.xaml");
    string consoleSourcePath = Path.Combine(sourceRoot, "src", "TuckPane", "ConsoleWindow.xaml.cs");
    string edgeSurfacePath = Path.Combine(sourceRoot, "src", "TuckPane", "Services", "ThemeEdgeSurface.cs");

    string mainWindowXaml = await File.ReadAllTextAsync(mainWindowXamlPath);
    string mainWindowSource = await File.ReadAllTextAsync(mainWindowSourcePath);
    string consoleXaml = await File.ReadAllTextAsync(consoleXamlPath);
    string consoleSource = await File.ReadAllTextAsync(consoleSourcePath);
    string edgeSurfaceSource = await File.ReadAllTextAsync(edgeSurfacePath);

    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
    XElement? fallbackElement = XDocument.Parse(mainWindowXaml)
        .Descendants()
        .FirstOrDefault(element => (string?)element.Attribute(x + "Name") == "ItemFallbackIcon");
    Require(failures, fallbackElement is not null, "项目模板缺少 ItemFallbackIcon。");
    Require(failures, (string?)fallbackElement?.Attribute("Visibility") == "Collapsed",
        "ItemFallbackIcon 的初始状态不是 Collapsed，透明图标仍会露出白色文档轮廓。");

    object? Fallback(bool hasImageSource, bool iconLoadPending, bool organizerPreviewVisible) =>
        InvokeOrganizerMath(
            "ShouldShowItemFallback",
            [typeof(bool), typeof(bool), typeof(bool)],
            [hasImageSource, iconLoadPending, organizerPreviewVisible]);
    object? loadedFallback = Fallback(true, false, false);
    object? loadingFallback = Fallback(false, true, false);
    object? failedFallback = Fallback(false, false, false);
    object? builtInFallback = Fallback(true, false, false);
    object? organizerPreviewFallback = Fallback(false, false, true);
    Require(failures,
        loadedFallback is false && loadingFallback is false && failedFallback is true &&
        builtInFallback is false && organizerPreviewFallback is false,
        "真实图标、加载中、加载失败、内置图标和收纳窗预览没有遵守图片/兜底互斥矩阵。");
    Require(failures,
        mainWindowSource.Contains("ShouldShowItemFallback", StringComparison.Ordinal),
        "MainWindow 未统一使用图片/兜底互斥逻辑，刷新或收缩仍可能重新显示兜底。");

    System.Reflection.PropertyInfo? edgeGlowProperty = typeof(GlobalSettings).GetProperty("EdgeGlowEnabled");
    Require(failures, edgeGlowProperty?.PropertyType == typeof(bool),
        "GlobalSettings 缺少持久化布尔设置 EdgeGlowEnabled。");
    Require(failures,
        edgeGlowProperty?.GetValue(new GlobalSettings()) is true,
        "EdgeGlowEnabled 默认值不是 true。");

    string persistenceRoot = Path.Combine(Path.GetTempPath(), $"TuckPane-sep03-edge-glow-{Guid.NewGuid():N}");
    Directory.CreateDirectory(persistenceRoot);
    try
    {
        string legacyPath = Path.Combine(persistenceRoot, "schema15-missing.json");
        await File.WriteAllTextAsync(legacyPath,
            """
            {
              "SchemaVersion": 15,
              "GlobalSettings": {},
              "Organizers": []
            }
            """);
        AppStateV2 missingField = await new StateStore(legacyPath).LoadAsync();
        Require(failures,
            edgeGlowProperty?.GetValue(missingField.GlobalSettings) is true,
            "Schema 15 配置缺少 EdgeGlowEnabled 时没有自然加载为 true。");

        if (edgeGlowProperty is not null)
        {
            string disabledPath = Path.Combine(persistenceRoot, "disabled.json");
            var disabledState = new AppStateV2();
            edgeGlowProperty.SetValue(disabledState.GlobalSettings, false);
            var store = new StateStore(disabledPath);
            await store.SaveAsync(disabledState);
            AppStateV2 reloaded = await store.LoadAsync();
            Require(failures,
                edgeGlowProperty.GetValue(reloaded.GlobalSettings) is false,
                "关闭 EdgeGlowEnabled 后保存重载没有保持 false。");
        }
    }
    finally
    {
        Directory.Delete(persistenceRoot, recursive: true);
    }

    Require(failures,
        consoleXaml.Contains("x:Name=\"EdgeGlowToggle\"", StringComparison.Ordinal) &&
        consoleXaml.Contains("Toggled=\"EdgeGlowToggle_Toggled\"", StringComparison.Ordinal),
        "设置的显示页面缺少边缘弧光 ToggleSwitch 或切换事件。");
    Require(failures,
        edgeSurfaceSource.Contains("internal void SetEnabled(bool enabled)", StringComparison.Ordinal) &&
        edgeSurfaceSource.Contains("_visual.IsVisible = enabled", StringComparison.Ordinal),
        "ThemeEdgeSurface 未通过 Composition Visual 可见性提供 SetEnabled。");
    Require(failures,
        mainWindowSource.Contains("_compactEdgeSurface.SetEnabled", StringComparison.Ordinal) &&
        mainWindowSource.Contains("_expandedEdgeSurface.SetEnabled", StringComparison.Ordinal) &&
        consoleSource.Contains("_settingsEdgeSurface.SetEnabled", StringComparison.Ordinal),
        "收起、展开和设置内容区三处边缘层没有全部接入 EdgeGlowEnabled。");

    Type? wheelActionType = typeof(OrganizerInteractionMath).Assembly.GetType("TuckPane.Core.OrganizerWheelAction");
    Require(failures, wheelActionType?.IsEnum == true, "缺少 OrganizerWheelAction 枚举。");
    if (wheelActionType?.IsEnum == true)
    {
        string[] requiredActions = ["Ignore", "ScrollCompactList", "ScaleCompactList", "ScaleGrid"];
        string[] actualActions = Enum.GetNames(wheelActionType);
        Require(failures, requiredActions.All(actualActions.Contains),
            "OrganizerWheelAction 缺少 Ignore、ScrollCompactList、ScaleCompactList 或 ScaleGrid。");
    }

    string? ResolveAction(
        bool expanded,
        bool animating,
        bool resizing,
        bool reordering,
        bool shellDragging,
        bool controlPressed,
        bool compactList,
        bool pointerInsideList) => InvokeOrganizerMath(
            "ResolveWheelAction",
            [typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool)],
            [expanded, animating, resizing, reordering, shellDragging, controlPressed, compactList, pointerInsideList])?.ToString();

    Require(failures,
        ResolveAction(true, false, false, false, false, false, true, true) == "ScrollCompactList" &&
        ResolveAction(true, false, false, false, false, true, true, true) == "ScaleCompactList" &&
        ResolveAction(true, false, false, false, false, true, false, true) == "ScaleGrid",
        "普通精简滚动、Ctrl 精简缩放和 Ctrl 图标缩放没有解析为独立动作。");
    Require(failures,
        ResolveAction(false, false, false, false, false, false, true, true) == "Ignore" &&
        ResolveAction(true, true, false, false, false, false, true, true) == "Ignore" &&
        ResolveAction(true, false, true, false, false, false, true, true) == "Ignore" &&
        ResolveAction(true, false, false, true, false, false, true, true) == "Ignore" &&
        ResolveAction(true, false, false, false, true, false, true, true) == "Ignore" &&
        ResolveAction(true, false, false, false, false, false, false, true) == "Ignore" &&
        ResolveAction(true, false, false, false, false, false, true, false) == "Ignore",
        "滚轮动作未完整排除收起、动画、缩放、换序、Shell 拖动、图标普通滚轮或列表外命中。");

    (double ScrollDeltaDip, int Remainder)? ConsumeWheel(int remainder, int delta, double rowHeightDip)
    {
        object? result = InvokeOrganizerMath(
            "ConsumeCompactListWheelDelta",
            [typeof(int), typeof(int), typeof(double)],
            [remainder, delta, rowHeightDip]);
        if (result is not System.Runtime.CompilerServices.ITuple tuple || tuple.Length != 2) return null;
        return (Convert.ToDouble(tuple[0]), Convert.ToInt32(tuple[1]));
    }

    (double ScrollDeltaDip, int Remainder)? upward = ConsumeWheel(0, 120, 45);
    (double ScrollDeltaDip, int Remainder)? downward = ConsumeWheel(0, -120, 45);
    (double ScrollDeltaDip, int Remainder)? firstHalf = ConsumeWheel(0, 60, 45);
    (double ScrollDeltaDip, int Remainder)? secondHalf = ConsumeWheel(firstHalf?.Remainder ?? 0, 60, 45);
    Require(failures,
        upward is { ScrollDeltaDip: -45, Remainder: 0 } &&
        downward is { ScrollDeltaDip: 45, Remainder: 0 },
        "精简列表滚轮 ±120 的方向错误，或每格没有移动一条当前高度为 45 DIP 的行。");
    Require(failures,
        firstHalf is { ScrollDeltaDip: 0, Remainder: 60 } &&
        secondHalf is { ScrollDeltaDip: -45, Remainder: 0 },
        "高精度滚轮两个 +60 增量没有累计为向上一行。");

    int wheelHandlerStart = mainWindowSource.IndexOf(
        "private void ExpandedView_PointerWheelChanged", StringComparison.Ordinal);
    int wheelHandlerEnd = mainWindowSource.IndexOf(
        "private void CommitCanvasResize", Math.Max(0, wheelHandlerStart), StringComparison.Ordinal);
    string wheelHandlerSource = wheelHandlerStart >= 0 && wheelHandlerEnd > wheelHandlerStart
        ? mainWindowSource[wheelHandlerStart..wheelHandlerEnd]
        : string.Empty;
    Require(failures,
        wheelHandlerSource.Contains("e.KeyModifiers", StringComparison.Ordinal) &&
        !wheelHandlerSource.Contains("GetAsyncKeyState", StringComparison.Ordinal) &&
        wheelHandlerSource.Contains("ResolveWheelAction", StringComparison.Ordinal) &&
        wheelHandlerSource.Contains("ConsumeCompactListWheelDelta", StringComparison.Ordinal) &&
        wheelHandlerSource.Contains("ItemsScrollView.ScrollBy", StringComparison.Ordinal),
        "顶层滚轮入口未使用事件修饰键和统一动作解析来驱动精简列表 ScrollBy。");

    if (failures.Count > 0)
        throw new InvalidOperationException($"{failures[0]}（本专项共 {failures.Count} 项契约未满足）");

    Console.WriteLine("PASS: Sep 03 organizer visual and input fixes");
    return;
}

if (args is ["--organizer-drag-hover"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    Require(OrganizerInteractionMath.ShouldExpandForOrganizerDragHover(
        true, false, OrganizerPlacementMode.Floating, false, false, false),
        "有效的 Floating 拖动目标未触发展开。");
    Require(OrganizerInteractionMath.ShouldExpandForOrganizerDragHover(
        true, false, OrganizerPlacementMode.Positioned, false, false, false),
        "有效的 Positioned 拖动目标未触发展开。");
    Require(!OrganizerInteractionMath.ShouldExpandForOrganizerDragHover(
        true, true, OrganizerPlacementMode.Floating, false, false, false) &&
        !OrganizerInteractionMath.ShouldExpandForOrganizerDragHover(
            true, false, OrganizerPlacementMode.Station, false, false, false) &&
        !OrganizerInteractionMath.ShouldExpandForOrganizerDragHover(
            true, false, OrganizerPlacementMode.Floating, true, false, false) &&
        !OrganizerInteractionMath.ShouldExpandForOrganizerDragHover(
            true, false, OrganizerPlacementMode.Floating, false, true, false) &&
        !OrganizerInteractionMath.ShouldExpandForOrganizerDragHover(
            true, false, OrganizerPlacementMode.Floating, false, false, true) &&
        !OrganizerInteractionMath.ShouldExpandForOrganizerDragHover(
            false, false, OrganizerPlacementMode.Floating, false, false, false),
        "拖动悬停展开排除条件不完整。");

    string mainWindowSource = await File.ReadAllTextAsync(Path.Combine(
        Environment.CurrentDirectory, "src", "TuckPane", "MainWindow.xaml.cs"));
    Require(mainWindowSource.Contains("TryGetCompactDropBounds", StringComparison.Ordinal) &&
            mainWindowSource.Contains("UpdateOrganizerDragHover(this, cursor)", StringComparison.Ordinal) &&
            mainWindowSource.Contains("WaitForOrganizerDragHoverAsync(this)", StringComparison.Ordinal),
        "MainWindow 未接入紧凑卡片命中、拖动悬停展开和释放等待。");
    string appHostSource = await File.ReadAllTextAsync(Path.Combine(
        Environment.CurrentDirectory, "src", "TuckPane", "AppHost.cs"));
    Require(appHostSource.Contains("UpdateOrganizerDragHover", StringComparison.Ordinal) &&
            appHostSource.Contains("ExpandForOrganizerDragAsync", StringComparison.Ordinal) &&
            appHostSource.Contains("TryGetCompactDropBounds", StringComparison.Ordinal),
        "AppHost 未提供统一拖动悬停目标扫描与展开入口。");

    Console.WriteLine("PASS: organizer drag hover");
    return;
}

if (args is ["--dialog-window-unification"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string mainSource2 = await File.ReadAllTextAsync(Path.Combine(
        Environment.CurrentDirectory, "src", "TuckPane", "MainWindow.xaml.cs"));
    int start = mainSource2.IndexOf("private async Task ShowDeleteDialogAsync()", StringComparison.Ordinal);
    int end = mainSource2.IndexOf("private async Task ShowRenameDialogAsync()", start, StringComparison.Ordinal);
    Require(start >= 0 && end > start, "无法定位收纳窗删除对话框方法。");
    string deleteSource = mainSource2[start..end];
    Require(deleteSource.Contains("OwnedDialogWindow.ShowConfirmationAsync", StringComparison.Ordinal) &&
            !deleteSource.Contains("new ContentDialog", StringComparison.Ordinal) &&
            !deleteSource.Contains("XamlRoot", StringComparison.Ordinal),
        "删除收纳窗仍使用内嵌 ContentDialog。");
    foreach (string marker in new[]
    {
        "OwnedDialogWindow.ShowTextInputAsync", "ShowRenameFileDialogAsync",
        "ShowRenameNoteDialogAsync", "ShowDeleteNoteDialogAsync", "CreateUniqueFolder"
    })
        Require(mainSource2.Contains(marker, StringComparison.Ordinal), $"缺少收纳窗右键弹窗统一接线：{marker}");

    Console.WriteLine("PASS: dialog window unification");
    return;
}

if (args is ["--drag-input-safety"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    IntPtr packed = DragMessageRelay.PackClientPosition(new NativeMethods.POINT { X = -25, Y = 320 });
    Require(unchecked((short)(packed.ToInt64() & 0xFFFF)) == -25 &&
            unchecked((short)((packed.ToInt64() >> 16) & 0xFFFF)) == 320,
        "拖动消息坐标打包未保留负数与高位坐标。");

    string mainWindowSource = await File.ReadAllTextAsync(Path.Combine(
        Environment.CurrentDirectory, "src", "TuckPane", "MainWindow.xaml.cs"));
    int hookStart = mainWindowSource.IndexOf("private IntPtr ItemDragBoundaryHookProc", StringComparison.Ordinal);
    int hookEnd = mainWindowSource.IndexOf("private static uint GetForwardedMouseKeyState", hookStart, StringComparison.Ordinal);
    Require(hookStart >= 0 && hookEnd > hookStart, "无法定位拖动边界钩子实现。");
    string hookSource = mainWindowSource[hookStart..hookEnd];
    Require(hookSource.Contains("ScreenToClient", StringComparison.Ordinal) &&
            hookSource.Contains("PackClientPosition", StringComparison.Ordinal) &&
            !hookSource.Contains("keys, IntPtr.Zero", StringComparison.Ordinal),
        "低级拖动钩子仍在向输入栈发送零坐标消息。");
    Require(hookSource.Contains("catch (Exception ex)", StringComparison.Ordinal) &&
            hookSource.Contains("CallNextHookEx", StringComparison.Ordinal),
        "低级拖动钩子缺少异常隔离或后续钩子调用。");

    string appSource = await File.ReadAllTextAsync(Path.Combine(
        Environment.CurrentDirectory, "src", "TuckPane", "App.xaml.cs"));
    Require(appSource.Contains("args.Handled = true", StringComparison.Ordinal) &&
            appSource.Contains("处理应用激活失败", StringComparison.Ordinal),
        "应用激活/未处理异常边界未建立。");

    Console.WriteLine("PASS: drag input safety");
    return;
}

if (args is ["--organizer-resize-rename-sync"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    var getResizeEdge = typeof(MainWindow).GetMethod(
        "GetCanvasResizeEdge",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
        binder: null,
        types: [typeof(Windows.Foundation.Point), typeof(double), typeof(double)],
        modifiers: null);
    Require(getResizeEdge is not null, "缺少展开收纳窗 resize 命中判定入口。");
    CanvasResizeEdge Edge(double x, double y) => (CanvasResizeEdge)getResizeEdge!.Invoke(
        null,
        [new Windows.Foundation.Point(x, y), 200d, 120d])!;

    Require(Edge(28, 60) == CanvasResizeEdge.Left, "左边 28 DIP 边界未命中。");
    Require(Edge(172, 60) == CanvasResizeEdge.Right, "右边 28 DIP 边界未命中。");
    Require(Edge(100, 28) == CanvasResizeEdge.Top, "上边 28 DIP 边界未命中。");
    Require(Edge(100, 92) == CanvasResizeEdge.Bottom, "下边 28 DIP 边界未命中。");
    Require(Edge(28, 28) == (CanvasResizeEdge.Left | CanvasResizeEdge.Top), "左上角未命中双向 resize。");
    Require(Edge(172, 28) == (CanvasResizeEdge.Right | CanvasResizeEdge.Top), "右上角未命中双向 resize。");
    Require(Edge(28, 92) == (CanvasResizeEdge.Left | CanvasResizeEdge.Bottom), "左下角未命中双向 resize。");
    Require(Edge(172, 92) == (CanvasResizeEdge.Right | CanvasResizeEdge.Bottom), "右下角未命中双向 resize。");
    Require(Edge(29, 60) == CanvasResizeEdge.None && Edge(100, 29) == CanvasResizeEdge.None,
        "28 DIP 命中带外仍被判定为 resize。");
    Require(Edge(-1, 60) == CanvasResizeEdge.None && Edge(201, 60) == CanvasResizeEdge.None,
        "窗口边界外仍被判定为 resize。");

    var getHitTest = typeof(MainWindow).GetMethod(
        "GetCanvasResizeHitTest",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
        binder: null,
        types: [typeof(CanvasResizeEdge)],
        modifiers: null);
    Require(getHitTest is not null, "缺少 resize edge 到原生命中值的映射。");
    int Hit(CanvasResizeEdge edge) => (int)getHitTest!.Invoke(null, [edge])!;
    Require(Hit(CanvasResizeEdge.Left) == NativeMethods.HTLEFT &&
            Hit(CanvasResizeEdge.Right) == NativeMethods.HTRIGHT &&
            Hit(CanvasResizeEdge.Top) == NativeMethods.HTTOP &&
            Hit(CanvasResizeEdge.Bottom) == NativeMethods.HTBOTTOM &&
            Hit(CanvasResizeEdge.Left | CanvasResizeEdge.Top) == NativeMethods.HTTOPLEFT &&
            Hit(CanvasResizeEdge.Right | CanvasResizeEdge.Top) == NativeMethods.HTTOPRIGHT &&
            Hit(CanvasResizeEdge.Left | CanvasResizeEdge.Bottom) == NativeMethods.HTBOTTOMLEFT &&
            Hit(CanvasResizeEdge.Right | CanvasResizeEdge.Bottom) == NativeMethods.HTBOTTOMRIGHT,
        "四边四角没有全部映射到正确的原生命中值。");

    Guid stationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    var station = new OrganizerDefinition
    {
        Id = stationId,
        Name = "Station",
        PlacementMode = OrganizerPlacementMode.Station,
        StorageAbsolutePath = Path.GetTempPath()
    };
    var child = new OrganizerDefinition
    {
        Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Name = "改名前",
        ContainerOrganizerId = stationId,
        StorageAbsolutePath = Path.GetTempPath()
    };
    var host = new AppHost();
    host.State.Organizers.AddRange([station, child]);
    Require(host.GetContainedOrganizerItems(stationId).Single().Name == "改名前",
        "Station 初始投影没有读取 contained organizer 名称。");
    child.Name = "改名后";
    Require(host.GetContainedOrganizerItems(stationId).Single().Name == "改名后",
        "Station 重新投影没有读取 contained organizer 的最新名称。");

    string mainWindowSource = await File.ReadAllTextAsync(Path.Combine(
        Environment.CurrentDirectory,
        "src",
        "TuckPane",
        "MainWindow.xaml.cs"));
    int renameStart = mainWindowSource.IndexOf("private async Task ShowRenameDialogAsync()", StringComparison.Ordinal);
    Require(renameStart >= 0, "无法定位收纳窗重命名方法源码。");
    int renameEnd = mainWindowSource.IndexOf("private void OpenStorageDirectory()", renameStart, StringComparison.Ordinal);
    Require(renameEnd > renameStart, "无法确定收纳窗重命名方法源码边界。");
    string renameSource = mainWindowSource[renameStart..renameEnd];
    Require(renameSource.Contains("OwnedDialogWindow.ShowTextInputAsync", StringComparison.Ordinal) &&
            !renameSource.Contains("new ContentDialog", StringComparison.Ordinal),
        "收纳窗重命名没有改用独立 owned dialog。");
    Require(renameSource.Contains("maxLength: 40", StringComparison.Ordinal) &&
            renameSource.Contains("ApplyOrganizerRuntime(_definition, OrganizerVisualChange.Name)", StringComparison.Ordinal) &&
            renameSource.Contains("Console.RefreshAll(_definition.Id)", StringComparison.Ordinal),
        "收纳窗重命名缺少长度限制或实时名称刷新链。");
    Require(mainWindowSource.Contains("!show || _definition.PlacementMode == OrganizerPlacementMode.Station", StringComparison.Ordinal),
        "Station 没有在共享入口排除 resize 边窗。");

    string appHostSource = await File.ReadAllTextAsync(Path.Combine(
        Environment.CurrentDirectory,
        "src",
        "TuckPane",
        "AppHost.cs"));
    int applyStart = appHostSource.IndexOf("internal string? ApplyOrganizerRuntime", StringComparison.Ordinal);
    Require(applyStart >= 0, "无法定位 organizer runtime 应用方法源码。");
    int applyEnd = appHostSource.IndexOf("internal DesktopGridPlacement? FindNearestPositionedPlacement", applyStart, StringComparison.Ordinal);
    Require(applyEnd > applyStart, "无法确定 organizer runtime 应用方法源码边界。");
    string applySource = appHostSource[applyStart..applyEnd];
    Require(applySource.Contains("ContainerOrganizerId", StringComparison.Ordinal) &&
            applySource.Contains("RefreshContainedOrganizerItemsAsync", StringComparison.Ordinal),
        "名称 runtime 更新没有复用父收纳窗刷新链。");

    Console.WriteLine("PASS: organizer resize rename sync");
    return;
}

if (args is ["--file-drop-types"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-file-drop-types-{Guid.NewGuid():N}");
    string sourceRoot = Path.Combine(root, "source");
    string copyRoot = Path.Combine(root, "copy");
    string lockedRoot = Path.Combine(root, "locked");
    Directory.CreateDirectory(sourceRoot);
    Directory.CreateDirectory(copyRoot);
    Directory.CreateDirectory(lockedRoot);
    try
    {
        string[] names = ["track.mp3", "TRACK.MP3", "voice.m4a", "archive.unknown", "LICENSE"];
        string[] paths = names.Select(name => Path.Combine(sourceRoot, name)).ToArray();
        for (int index = 0; index < paths.Length; index++)
            await File.WriteAllBytesAsync(paths[index], [(byte)(index + 1), 2, 3, 4]);

        foreach (string path in paths)
        {
            Require(DropValidator.TryGetKind(path, out WidgetItemKind kind) && kind == WidgetItemKind.File,
                $"普通本地文件没有按 File 接收：{Path.GetFileName(path)}");
        }

        var copyStorage = new StorageService(copyRoot);
        IReadOnlyList<TransferOutcome> copied = await copyStorage.CopyBatchAsync(paths, null, CancellationToken.None);
        Require(copied.Count == paths.Length && copied.All(outcome => outcome.Status == TransferStatus.Copied),
            "普通本地文件没有全部复制到收纳目录。");
        Require(copyStorage.ReadItems().Count == paths.Length &&
                copyStorage.ReadItems().All(item => item.Kind == WidgetItemKind.File),
            "复制后的普通文件没有全部显示为 File。");

        string lockedSource = Path.Combine(sourceRoot, "playing.mp3");
        byte[] lockedBytes = [9, 8, 7, 6, 5];
        await File.WriteAllBytesAsync(lockedSource, lockedBytes);
        TransferOutcome lockedOutcome;
        using (var locked = new FileStream(lockedSource, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            lockedOutcome = (await new StorageService(lockedRoot)
                .ImportBatchAsync([lockedSource], null, CancellationToken.None)).Single();
            Require(lockedOutcome.Status == TransferStatus.CopiedSourceRetained,
                $"被占用的同盘 MP3 没有复制并保留源文件，实际状态：{lockedOutcome.Status}。");
            Require(File.Exists(lockedSource) && lockedOutcome.DestinationPath is not null &&
                    File.Exists(lockedOutcome.DestinationPath),
                "被占用文件退化后没有同时保留完整源文件和目标副本。");
        }
        Require(File.ReadAllBytes(lockedSource).SequenceEqual(lockedBytes) &&
                File.ReadAllBytes(lockedOutcome.DestinationPath!).SequenceEqual(lockedBytes),
            "被占用文件的源或目标内容发生变化。");

        StorageFile first = await StorageFile.GetFileFromPathAsync(paths[0]);
        StorageFile second = await StorageFile.GetFileFromPathAsync(paths[2]);
        var package = new DataPackage();
        package.SetStorageItems([first, second], readOnly: false);
        var reader = typeof(ShellDragService).GetMethod(
            "TryGetFileDropPaths",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: [typeof(DataPackageView), typeof(string[]).MakeByRefType()],
            modifiers: null);
        Require(reader is not null, "缺少 DataPackageView 的原生 CF_HDROP 路径读取入口。");
        object?[] arguments = [package.GetView(), null];
        bool read = (bool)reader!.Invoke(null, arguments)!;
        string[] fileDropPaths = arguments[1] as string[] ?? [];
        Require(read && fileDropPaths.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(new[] { paths[0], paths[2] }),
            "DataPackageView 没有从 CF_HDROP 返回全部真实路径并去重。");

        Console.WriteLine("PASS: file drop types");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--default-storage-directory"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-default-storage-{Guid.NewGuid():N}");
    string defaultRoot = Path.Combine(root, "DefaultRoot");
    Directory.CreateDirectory(defaultRoot);
    try
    {
        string normalizedDefaultRoot = Path.GetFullPath(defaultRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string statePath = Path.Combine(root, "state.json");
        var store = new StateStore(statePath);
        await store.SaveAsync(new AppStateV2
        {
            GlobalSettings = new GlobalSettings { DefaultStorageDirectory = defaultRoot }
        });
        AppStateV2 reloaded = await store.LoadAsync();
        Require(reloaded.SchemaVersion == 10 &&
                reloaded.GlobalSettings.DefaultStorageDirectory == normalizedDefaultRoot,
            "默认存储根目录没有按 Schema 10 持久化。 ");

        Guid relativeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Guid absoluteId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        string absoluteStorage = Path.Combine(root, "ExistingManualStorage");
        Directory.CreateDirectory(absoluteStorage);
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 8,
            GlobalSettings = new { },
            Organizers = new object[]
            {
                new { Id = relativeId, Name = "relative", StorageRelativePath = Path.Combine("Windows", "Legacy-aaaaaaaa") },
                new { Id = absoluteId, Name = "absolute", StorageAbsolutePath = absoluteStorage }
            }
        }));
        AppStateV2 migrated = await new StateStore(statePath).LoadAsync();
        Require(migrated.SchemaVersion == 10 &&
                migrated.Organizers.Single(item => item.Id == relativeId).StorageOwnedByApp &&
                !migrated.Organizers.Single(item => item.Id == absoluteId).StorageOwnedByApp,
            "Schema 8 目录所有权没有按相对/绝对路径迁移。 ");

        Guid fixedId = Guid.Parse("0123ABCD-4567-89AB-CDEF-0123456789AB");
        string expected = Path.Combine(normalizedDefaultRoot, "收纳窗-0123abcd");
        Require(AppPaths.CreateDefaultOrganizerStoragePath(defaultRoot, fixedId) == expected,
            "固定 ID 没有生成精确的单层收纳窗-8位小写十六进制路径。 ");
        string created = AppPaths.CreateDefaultOrganizerStorageDirectory(defaultRoot, fixedId, []);
        Require(created == expected && Directory.Exists(expected), "默认收纳目录没有自动创建。 ");

        string missingRoot = Path.Combine(root, "MissingRoot");
        bool missingRejected = false;
        try { _ = AppPaths.CreateDefaultOrganizerStorageDirectory(missingRoot, Guid.NewGuid(), []); }
        catch (DirectoryNotFoundException) { missingRejected = true; }
        Require(missingRejected && !Directory.Exists(missingRoot), "缺失的默认根目录被重建或未被拒绝。 ");

        bool occupiedRejected = false;
        try { _ = AppPaths.CreateDefaultOrganizerStorageDirectory(defaultRoot, fixedId, []); }
        catch (InvalidOperationException) { occupiedRejected = true; }
        Require(occupiedRejected && Directory.Exists(expected), "已存在的目标目录没有阻止创建。 ");

        Guid overlapId = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
        string overlapCandidate = AppPaths.CreateDefaultOrganizerStoragePath(defaultRoot, overlapId);
        bool overlapRejected = false;
        try
        {
            _ = AppPaths.CreateDefaultOrganizerStorageDirectory(
                defaultRoot,
                overlapId,
                [Path.Combine(overlapCandidate, "Child")]);
        }
        catch (InvalidOperationException) { overlapRejected = true; }
        Require(overlapRejected && !Directory.Exists(overlapCandidate),
            "与现有目录重叠的目标没有被阻止，或被自动换 ID 创建。 ");

        string manualStorage = Path.Combine(root, "ManualStorage");
        Directory.CreateDirectory(manualStorage);
        await File.WriteAllTextAsync(Path.Combine(manualStorage, "existing.txt"), "existing");
        Require(AppPaths.ValidateCustomStoragePath(manualStorage) == Path.GetFullPath(manualStorage) &&
                File.Exists(Path.Combine(manualStorage, "existing.txt")),
            "非空且未占用的手选最终目录没有保持可用。 ");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }

    Console.WriteLine("PASS: default storage directory");
    return;
}

if (args is ["--performance-profile"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void RequireTuning(
        PerformanceProfile profile,
        int pointerPollMilliseconds,
        int desktopRepairMilliseconds,
        bool customAnimationsEnabled)
    {
        var settings = new GlobalSettings { PerformanceProfile = profile };
        PerformanceTuning tuning = settings.PerformanceTuning;
        Require(tuning.PointerPollMilliseconds == pointerPollMilliseconds,
            $"{profile} 指针轮询周期错误。");
        Require(tuning.DesktopRepairMilliseconds == desktopRepairMilliseconds,
            $"{profile} 桌面修复周期错误。");
        Require(tuning.CustomAnimationsEnabled == customAnimationsEnabled,
            $"{profile} 动画策略错误。");
    }

    RequireTuning(PerformanceProfile.PowerSaver, 100, 8000, false);
    RequireTuning(PerformanceProfile.Balanced, 50, 4000, true);
    RequireTuning(PerformanceProfile.HighPerformance, 25, 2000, true);

    Require(!new GlobalSettings { PerformanceProfile = PerformanceProfile.PowerSaver }
            .ShouldUseCustomAnimations(systemAnimationsEnabled: true),
        "节能档仍启用了自定义动画。");
    Require(new GlobalSettings { PerformanceProfile = PerformanceProfile.Balanced }
            .ShouldUseCustomAnimations(systemAnimationsEnabled: true) &&
            new GlobalSettings { PerformanceProfile = PerformanceProfile.HighPerformance }
                .ShouldUseCustomAnimations(systemAnimationsEnabled: true),
        "平衡或高性能档没有保留动画。");
    Require(!new GlobalSettings { PerformanceProfile = PerformanceProfile.Balanced }
            .ShouldUseCustomAnimations(systemAnimationsEnabled: false) &&
            !new GlobalSettings { PerformanceProfile = PerformanceProfile.HighPerformance }
                .ShouldUseCustomAnimations(systemAnimationsEnabled: false),
        "Windows 关闭动画后仍启用了自定义动画。");

    Require(!OrganizerInteractionMath.ShouldPollPointer(
            OrganizerPlacementMode.Floating, true, false, false, false, false),
        "默认普通收纳窗仍在轮询。");
    Require(OrganizerInteractionMath.ShouldPollPointer(
            OrganizerPlacementMode.Floating, true, false, false, true, false),
        "启用悬浮展开后普通收纳窗未轮询。");
    Require(OrganizerInteractionMath.ShouldPollPointer(
            OrganizerPlacementMode.Positioned, true, false, true, false, true),
        "展开后启用离开收缩时未轮询。");
    Require(OrganizerInteractionMath.ShouldPollPointer(
            OrganizerPlacementMode.Station, true, false, false, false, false),
        "可见 Station 未轮询。");
    Require(!OrganizerInteractionMath.ShouldPollPointer(
            OrganizerPlacementMode.Station, false, false, false, true, true) &&
            !OrganizerInteractionMath.ShouldPollPointer(
                OrganizerPlacementMode.Floating, true, true, false, true, true),
        "隐藏 Station 或被收纳的收起窗仍在轮询。");

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-performance-profile-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        string statePath = Path.Combine(root, "state.json");
        await File.WriteAllTextAsync(statePath,
            """{"SchemaVersion":8,"GlobalSettings":{},"Organizers":[]}""");
        AppStateV2 legacy = await new StateStore(statePath).LoadAsync();
        Require(legacy.SchemaVersion == 8 &&
                legacy.GlobalSettings.PerformanceProfile == PerformanceProfile.Balanced,
            "旧状态缺少性能档位时未默认平衡。");

        await File.WriteAllTextAsync(statePath,
            """{"SchemaVersion":8,"GlobalSettings":{"PerformanceProfile":99},"Organizers":[]}""");
        Require((await new StateStore(statePath).LoadAsync()).GlobalSettings.PerformanceProfile ==
                PerformanceProfile.Balanced,
            "非法性能档位未归一为平衡。");

        var store = new StateStore(statePath);
        await store.SaveAsync(new AppStateV2
        {
            GlobalSettings = new GlobalSettings { PerformanceProfile = PerformanceProfile.HighPerformance }
        });
        Require((await store.LoadAsync()).GlobalSettings.PerformanceProfile ==
                PerformanceProfile.HighPerformance,
            "性能档位没有持久化。");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }

    Console.WriteLine("PASS: performance profile");
    return;
}

if (args is ["--note-always-on-top-setting"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-note-topmost-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        string statePath = Path.Combine(root, "state.json");
        Require(!new GlobalSettings().NoteAlwaysOnTop, "便签永远置顶没有默认关闭。");
        await File.WriteAllTextAsync(statePath, """{"SchemaVersion":8,"GlobalSettings":{},"Organizers":[]}""");
        Require(!(await new StateStore(statePath).LoadAsync()).GlobalSettings.NoteAlwaysOnTop,
            "旧状态缺少便签置顶字段时没有保持关闭。");

        var store = new StateStore(statePath);
        await store.SaveAsync(new AppStateV2 { GlobalSettings = new GlobalSettings { NoteAlwaysOnTop = true } });
        Require((await store.LoadAsync()).GlobalSettings.NoteAlwaysOnTop, "便签永远置顶设置没有持久化。");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }

    Console.WriteLine("PASS: note always-on-top setting");
    return;
}

if (args is ["--aug31-note-station-fixes"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    Require(
        OrganizerInteractionMath.ShouldUseWindowAlignment(
            enabled: true,
            draggingExpanded: false,
            OrganizerPlacementMode.Floating,
            overOrganizerDropTarget: false),
        "普通 Floating 收起拖动没有保留窗口对齐。");
    Require(
        !OrganizerInteractionMath.ShouldUseWindowAlignment(
            enabled: true,
            draggingExpanded: false,
            OrganizerPlacementMode.Floating,
            overOrganizerDropTarget: true),
        "进入 Station 投放区后仍启用了窗口对齐。");
    Require(
        OrganizerInteractionMath.ShouldRememberExpandedPosition(true, OrganizerPlacementMode.Floating) &&
        OrganizerInteractionMath.ShouldRememberExpandedPosition(true, OrganizerPlacementMode.Positioned) &&
        !OrganizerInteractionMath.ShouldRememberExpandedPosition(true, OrganizerPlacementMode.Station),
        "展开位置记忆没有正确区分普通收纳窗与 Station。");
    Require(
        DisplayPlacementService.ResolveExpandedSideInset(
            OrganizerPlacementMode.Floating,
            OrganizerExpandedContentMode.CompactList) == DisplayPlacementService.StationSideInsetDip &&
        DisplayPlacementService.ResolveExpandedSideInset(
            OrganizerPlacementMode.Positioned,
            OrganizerExpandedContentMode.Icon) == DisplayPlacementService.ExpandedSideInsetDip &&
        DisplayPlacementService.ResolveExpandedSideInset(
            OrganizerPlacementMode.Station,
            OrganizerExpandedContentMode.Icon) == DisplayPlacementService.StationSideInsetDip,
        "展开内容左右边距没有保持精简列表/Station 12 DIP、普通图标模式 28 DIP。");

    Console.WriteLine("PASS: Aug 31 note and Station fixes");
    return;
}

if (args is ["--window-behavior-fixes"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static NativeMethods.RECT RestoreExpanded(WidgetPosition saved, DisplayInfo fallback, int width, int height)
    {
        NativeMethods.RECT restored = DisplayPlacementService.RestoreToDisplay(saved, fallback, width, height);
        return DisplayPlacementService.Clamp(restored, DisplayPlacementService.GetExpandedWorkArea(fallback));
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-window-behavior-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        Require(!new GlobalSettings().RememberExpandedOrganizerPosition,
            "展开位置记忆开关默认值不是关闭。");

        string legacyPath = Path.Combine(root, "legacy.json");
        await File.WriteAllTextAsync(legacyPath,
            """{"SchemaVersion":8,"GlobalSettings":{},"Organizers":[{"Name":"legacy"}]}""");
        AppStateV2 legacy = await new StateStore(legacyPath).LoadAsync();
        Require(!legacy.GlobalSettings.RememberExpandedOrganizerPosition &&
                legacy.Organizers.Single().ExpandedPosition is null,
            "旧状态缺少展开位置字段时没有保持关闭和空位置。");

        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        var state = new AppStateV2
        {
            GlobalSettings = new GlobalSettings { RememberExpandedOrganizerPosition = true },
            Organizers =
            [
                new OrganizerDefinition
                {
                    Id = firstId,
                    ExpandedPosition = new WidgetPosition { MonitorDevice = "left", XDip = 40, YDip = 50 }
                },
                new OrganizerDefinition
                {
                    Id = secondId,
                    ExpandedPosition = new WidgetPosition { MonitorDevice = "primary", XDip = 320, YDip = 180 }
                }
            ]
        };
        var store = new StateStore(Path.Combine(root, "round-trip.json"));
        await store.SaveAsync(state);
        AppStateV2 reloaded = await store.LoadAsync();
        Require(reloaded.GlobalSettings.RememberExpandedOrganizerPosition &&
                reloaded.Organizers.Single(item => item.Id == firstId).ExpandedPosition is { MonitorDevice: "left", XDip: 40, YDip: 50 } &&
                reloaded.Organizers.Single(item => item.Id == secondId).ExpandedPosition is { MonitorDevice: "primary", XDip: 320, YDip: 180 },
            "开关或两个收纳窗的独立展开位置没有正确保存重载。");

        var negativeDisplay = new DisplayInfo(
            "left",
            new NativeMethods.RECT { Left = -1920, Top = 0, Right = 0, Bottom = 1080 },
            new NativeMethods.RECT { Left = -1920, Top = 0, Right = 0, Bottom = 1040 },
            1);
        NativeMethods.RECT negative = RestoreExpanded(state.Organizers[0].ExpandedPosition!, negativeDisplay, 640, 480);
        Require(negative.Left < 0 && negative.Left >= negativeDisplay.Work.Left && negative.Right <= negativeDisplay.Work.Right &&
                negative.Top >= negativeDisplay.Work.Top && negative.Bottom <= negativeDisplay.Work.Bottom,
            "负坐标副屏恢复后没有完整可见。");

        var primaryDisplay = new DisplayInfo(
            "primary",
            new NativeMethods.RECT { Left = 0, Top = 0, Right = 1280, Bottom = 720 },
            new NativeMethods.RECT { Left = 0, Top = 0, Right = 1280, Bottom = 680 },
            1);
        var stalePosition = new WidgetPosition { MonitorDevice = "missing", XDip = 1800, YDip = 900 };
        NativeMethods.RECT fallback = RestoreExpanded(stalePosition, primaryDisplay, 900, 600);
        Require(fallback.Left >= primaryDisplay.Work.Left && fallback.Right <= primaryDisplay.Work.Right &&
                fallback.Top >= primaryDisplay.Work.Top && fallback.Bottom <= primaryDisplay.Work.Bottom,
            "显示器丢失或工作区缩小时恢复结果没有完整可见。");

        Console.WriteLine("PASS: window behavior fixes");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--organizer-nesting"])
{
    static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    static string Hierarchy(IReadOnlyList<OrganizerDefinition> items) => string.Join("|", items.Select(item =>
        $"{item.Id:N}>{item.ContainerOrganizerId?.ToString("N") ?? "-"}[{string.Join(",", item.ItemOrder)}]"));
    static string Settings(OrganizerDefinition item) => JsonSerializer.Serialize(new
    {
        item.PlacementMode, item.DockEdge, item.ExpandedContentMode,
        item.CompactScale, item.CanvasScale, item.ItemScale, item.NameScale, item.CompactListItemScale,
        item.StorageRelativePath, item.StorageAbsolutePath, item.StorageOwnedByApp
    });
    static void Reject(IReadOnlyList<OrganizerDefinition> items, Guid source, Guid target,
        OrganizerContainmentFailure failure, string message)
    {
        string before = Hierarchy(items);
        OrganizerContainmentMoveResult result = OrganizerContainment.TryMove(items, source, target, 0);
        Require(!result.Succeeded && result.Failure == failure && Hierarchy(items) == before, message);
    }

    static bool HasNoGhostKeys(IReadOnlyList<OrganizerDefinition> organizers) => organizers.All(container =>
        container.ItemOrder.All(key => !OrganizerContainment.TryParseItemKey(key, out Guid childId) ||
            organizers.Any(child => child.Id == childId && child.ContainerOrganizerId == container.Id)));
    static bool SameRect(NativeMethods.RECT first, NativeMethods.RECT second) =>
        first.Left == second.Left && first.Top == second.Top && first.Right == second.Right && first.Bottom == second.Bottom;
    static bool Intersects(NativeMethods.RECT first, NativeMethods.RECT second) =>
        first.Left < second.Right && first.Right > second.Left && first.Top < second.Bottom && first.Bottom > second.Top;

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-organizer-nesting-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        foreach (OrganizerPlacementMode sourceMode in new[] { OrganizerPlacementMode.Floating, OrganizerPlacementMode.Positioned })
        {
            foreach (OrganizerPlacementMode targetMode in Enum.GetValues<OrganizerPlacementMode>())
            {
                var matrixSource = new OrganizerDefinition { PlacementMode = sourceMode };
                var target = new OrganizerDefinition { PlacementMode = targetMode };
                OrganizerContainmentMoveResult result = OrganizerContainment.TryMove([matrixSource, target], matrixSource.Id, target.Id, 0);
                Require(result.Succeeded && result.Failure == OrganizerContainmentFailure.None &&
                        matrixSource.ContainerOrganizerId == target.Id && target.ItemOrder.SequenceEqual([OrganizerContainment.ItemKey(matrixSource.Id)]),
                    $"{sourceMode} 来源无法放入 {targetMode} 收纳窗。");
            }
        }

        var stationSource = new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Station };
        var stationTarget = new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Floating };
        Reject([stationSource, stationTarget], stationSource.Id, stationTarget.Id,
            OrganizerContainmentFailure.StationCannotBeContained,
            "Station 来源未被拒绝，或拒绝时修改了层级状态。");

        var self = new OrganizerDefinition();
        Reject([self], self.Id, self.Id, OrganizerContainmentFailure.SameOrganizer,
            "自引用未被拒绝，或拒绝时修改了层级状态。");

        var sourceWithChild = new OrganizerDefinition();
        var directChild = new OrganizerDefinition { ContainerOrganizerId = sourceWithChild.Id };
        sourceWithChild.ItemOrder = [OrganizerContainment.ItemKey(directChild.Id)];
        var sourceTarget = new OrganizerDefinition();
        Require(OrganizerContainment.TryMove(
                    [sourceWithChild, directChild, sourceTarget], sourceWithChild.Id, sourceTarget.Id, 0).Succeeded &&
                sourceWithChild.ContainerOrganizerId == sourceTarget.Id &&
                directChild.ContainerOrganizerId == sourceWithChild.Id,
            "已有子树的来源无法整体移动，或后代关系被破坏。");

        var occupiedTargetParent = new OrganizerDefinition();
        var occupiedTarget = new OrganizerDefinition { ContainerOrganizerId = occupiedTargetParent.Id };
        occupiedTargetParent.ItemOrder = [OrganizerContainment.ItemKey(occupiedTarget.Id)];
        var occupiedTargetSource = new OrganizerDefinition();
        Require(OrganizerContainment.TryMove(
                    [occupiedTargetParent, occupiedTarget, occupiedTargetSource],
                    occupiedTargetSource.Id, occupiedTarget.Id, 0).Succeeded &&
                occupiedTargetSource.ContainerOrganizerId == occupiedTarget.Id,
            "已被收纳的目标无法继续作为嵌套父窗。");

        var cycleRoot = new OrganizerDefinition();
        var cycleMiddle = new OrganizerDefinition { ContainerOrganizerId = cycleRoot.Id };
        var cycleLeaf = new OrganizerDefinition { ContainerOrganizerId = cycleMiddle.Id };
        cycleRoot.ItemOrder = [OrganizerContainment.ItemKey(cycleMiddle.Id)];
        cycleMiddle.ItemOrder = [OrganizerContainment.ItemKey(cycleLeaf.Id)];
        OrganizerContainmentMoveResult cycleMove = OrganizerContainment.TryMove(
            [cycleRoot, cycleMiddle, cycleLeaf], cycleRoot.Id, cycleLeaf.Id, 0);
        Require(!cycleMove.Succeeded && cycleMove.Failure == OrganizerContainmentFailure.TargetIsDescendant,
            "移动到自身后代没有被拒绝。");

        var parentA = new OrganizerDefinition { ItemOrder = ["alpha.txt"] };
        var parentB = new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Station, ItemOrder = ["beta.txt"] };
        var firstChild = new OrganizerDefinition {
            PlacementMode = OrganizerPlacementMode.Floating,
            ExpandedContentMode = OrganizerExpandedContentMode.Icon,
            CompactScale = 2.25, CanvasScale = .8, ItemScale = .75, NameScale = .65, CompactListItemScale = 1.1,
            StorageAbsolutePath = Path.Combine(root, "absolute-child"), StorageRelativePath = string.Empty, StorageOwnedByApp = false
        };
        var secondChild = new OrganizerDefinition {
            PlacementMode = OrganizerPlacementMode.Positioned,
            ExpandedContentMode = OrganizerExpandedContentMode.CompactList,
            CompactScale = 1.4, CanvasScale = .7, ItemScale = 1.2, NameScale = .9, CompactListItemScale = 1.3,
            StorageRelativePath = Path.Combine("Windows", "relative-child"), StorageOwnedByApp = true
        };
        List<OrganizerDefinition> siblings = [parentA, parentB, firstChild, secondChild];
        string firstSettings = Settings(firstChild);
        string secondSettings = Settings(secondChild);
        string firstKey = OrganizerContainment.ItemKey(firstChild.Id);
        string secondKey = OrganizerContainment.ItemKey(secondChild.Id);

        Require(OrganizerContainment.TryMove(siblings, firstChild.Id, parentA.Id, int.MaxValue).Succeeded &&
                OrganizerContainment.TryMove(siblings, secondChild.Id, parentA.Id, int.MaxValue).Succeeded &&
                OrganizerContainment.GetDirectChildren(siblings, parentA.Id)
                    .Select(item => item.Id).SequenceEqual([firstChild.Id, secondChild.Id]),
            "同一父窗无法保留多个直接子窗的顺序。");
        Require(OrganizerContainment.TryMove(siblings, secondChild.Id, parentA.Id, 1).Succeeded &&
                parentA.ItemOrder.SequenceEqual(["alpha.txt", secondKey, firstKey]),
            "同父换序没有生成唯一、稳定的顺序键。");
        Require(OrganizerContainment.TryMove(siblings, secondChild.Id, parentB.Id, 1).Succeeded &&
                secondChild.ContainerOrganizerId == parentB.Id &&
                !parentA.ItemOrder.Contains(secondKey, StringComparer.OrdinalIgnoreCase) &&
                parentB.ItemOrder.SequenceEqual(["beta.txt", secondKey]),
            "跨父移动没有保持唯一归属和顺序键。");
        Require(OrganizerContainment.Detach(siblings, firstChild.Id) == parentA.Id &&
                firstChild.ContainerOrganizerId is null &&
                siblings.All(item => !item.ItemOrder.Contains(firstKey, StringComparer.OrdinalIgnoreCase)),
            "Detach 没有解除归属并清理全部顺序键。");
        Require(Settings(firstChild) == firstSettings && Settings(secondChild) == secondSettings,
            "收纳、换序、跨父或 Detach 改变了存储字段、模式或缩放设置。");

        Guid legacyParentId = Guid.NewGuid();
        Guid legacyChildId = Guid.NewGuid();
        string legacyKey = OrganizerContainment.ItemKey(legacyChildId);
        string legacyPath = Path.Combine(root, "legacy-containment.json");
        await File.WriteAllTextAsync(legacyPath, $$"""{"SchemaVersion":8,"GlobalSettings":{},"Organizers":[{"Id":"{{legacyParentId}}","PlacementMode":0,"ItemOrder":["{{legacyKey}}"]},{"Id":"{{legacyChildId}}","PlacementMode":1,"ContainerStationId":"{{legacyParentId}}"}]}""");
        var legacyStore = new StateStore(legacyPath);
        AppStateV2 legacyState = await legacyStore.LoadAsync();
        Require(legacyState.Organizers.Single(item => item.Id == legacyParentId).PlacementMode == OrganizerPlacementMode.Floating &&
                legacyState.Organizers.Single(item => item.Id == legacyChildId).ContainerOrganizerId == legacyParentId &&
                legacyState.Organizers.Single(item => item.Id == legacyParentId).ItemOrder.Contains(legacyKey, StringComparer.OrdinalIgnoreCase),
            "旧 ContainerStationId 未读取为普通父窗关系。");
        await legacyStore.SaveAsync(legacyState);
        string persistedLegacy = await File.ReadAllTextAsync(legacyPath);
        AppStateV2 legacyReloaded = await legacyStore.LoadAsync();
        Require(persistedLegacy.Contains("\"ContainerStationId\"", StringComparison.Ordinal) &&
                !persistedLegacy.Contains("\"ContainerOrganizerId\"", StringComparison.Ordinal) &&
                legacyReloaded.Organizers.Single(item => item.Id == legacyChildId).ContainerOrganizerId == legacyParentId,
            "兼容 JSON 键未稳定写回，或普通父窗关系重载丢失。");

        Guid missingId = Guid.NewGuid();
        var repairParent = new OrganizerDefinition { ItemOrder = ["keep.txt"] };
        var dangling = new OrganizerDefinition { ContainerOrganizerId = missingId };
        repairParent.ItemOrder.Add(OrganizerContainment.ItemKey(dangling.Id));
        repairParent.ItemOrder.Add(OrganizerContainment.ItemKey(missingId));
        var deepRoot = new OrganizerDefinition();
        var deepMiddle = new OrganizerDefinition { ContainerOrganizerId = deepRoot.Id };
        var deepLeaf = new OrganizerDefinition { ContainerOrganizerId = deepMiddle.Id };
        deepRoot.ItemOrder = [OrganizerContainment.ItemKey(deepMiddle.Id)];
        deepMiddle.ItemOrder = [OrganizerContainment.ItemKey(deepLeaf.Id)];
        var cycleA = new OrganizerDefinition();
        var cycleB = new OrganizerDefinition();
        cycleA.ContainerOrganizerId = cycleB.Id;
        cycleB.ContainerOrganizerId = cycleA.Id;
        cycleA.ItemOrder = [OrganizerContainment.ItemKey(cycleB.Id)];
        cycleB.ItemOrder = [OrganizerContainment.ItemKey(cycleA.Id)];
        var corrupted = new AppStateV2 { Organizers = [repairParent, dangling, deepRoot, deepMiddle, deepLeaf, cycleA, cycleB] };
        StateStore.Normalize(corrupted);
        Require(dangling.ContainerOrganizerId is null && repairParent.ItemOrder.SequenceEqual(["keep.txt"]) &&
                deepMiddle.ContainerOrganizerId == deepRoot.Id &&
                deepLeaf.ContainerOrganizerId == deepMiddle.Id &&
                HasNoGhostKeys(corrupted.Organizers) &&
                !(cycleA.ContainerOrganizerId == cycleB.Id && cycleB.ContainerOrganizerId == cycleA.Id),
            "Normalize 未同时修复悬空、深层、循环关系或幽灵顺序键。");

        OrganizerContainment.Detach(siblings, secondChild.Id);
        firstChild.ContainerOrganizerId = parentA.Id;
        secondChild.ContainerOrganizerId = parentA.Id;
        var grandchild = new OrganizerDefinition { ContainerOrganizerId = firstChild.Id };
        firstChild.ItemOrder = [OrganizerContainment.ItemKey(grandchild.Id)];
        parentA.ItemOrder = [secondKey, "document.txt", firstKey];
        IReadOnlyList<OrganizerDefinition> released = OrganizerContainment.ReleaseDirectChildren(
            [parentA, firstChild, secondChild, grandchild], parentA.Id);
        Require(released.Select(item => item.Id).SequenceEqual([secondChild.Id, firstChild.Id]) &&
                firstChild.ContainerOrganizerId is null && secondChild.ContainerOrganizerId is null &&
                grandchild.ContainerOrganizerId == firstChild.Id && parentA.ItemOrder.SequenceEqual(["document.txt"]),
            "删除前释放未按父窗顺序仅解除直接子窗。");
        Require(Settings(firstChild) == firstSettings && Settings(secondChild) == secondSettings,
            "释放直接子窗改变了存储字段、模式或缩放设置。");

        Guid[] releaseIds = Enumerable.Range(1, 4).Select(_ => Guid.NewGuid()).ToArray();
        OrganizerReleaseItem[] releaseItems = releaseIds.Select(id => new OrganizerReleaseItem(id, 40, 30)).ToArray();
        var workArea = new NativeMethods.RECT { Left = 0, Top = 0, Right = 500, Bottom = 500 };
        var lowerParentBounds = new NativeMethods.RECT { Left = 100, Top = 100, Right = 220, Bottom = 180 };
        IReadOnlyDictionary<Guid, NativeMethods.RECT> lowerPlan = OrganizerReleasePlanner.PlanFloating(lowerParentBounds, workArea, 1, releaseItems);
        IReadOnlyDictionary<Guid, NativeMethods.RECT> repeatedLowerPlan = OrganizerReleasePlanner.PlanFloating(lowerParentBounds, workArea, 1, releaseItems);
        Require(lowerPlan.Count == 4 && releaseIds.All(id => SameRect(lowerPlan[id], repeatedLowerPlan[id])) &&
                lowerPlan[releaseIds[0]].Top >= lowerParentBounds.Bottom + 12 &&
                lowerPlan[releaseIds[0]].Left == lowerPlan[releaseIds[3]].Left &&
                lowerPlan[releaseIds[3]].Top > lowerPlan[releaseIds[0]].Top &&
                releaseIds.Take(3).Select(id => lowerPlan[id].Left).Distinct().Count() == 3,
            "Floating 释放排列不稳定、未优先置于下方，或超过三列规则错误。");
        var upperParentBounds = new NativeMethods.RECT { Left = 100, Top = 400, Right = 220, Bottom = 480 };
        IReadOnlyDictionary<Guid, NativeMethods.RECT> upperPlan = OrganizerReleasePlanner.PlanFloating(upperParentBounds, workArea, 1, releaseItems);
        Require(upperPlan.Values.All(bounds => bounds.Bottom <= upperParentBounds.Top - 12) &&
                upperPlan.Values.All(bounds => bounds.Left >= workArea.Left && bounds.Top >= workArea.Top &&
                    bounds.Right <= workArea.Right && bounds.Bottom <= workArea.Bottom),
            "Floating 释放在下方不足时未稳定切换到上方夹区或越出工作区。");

        var nestingGridDisplay = new DisplayInfo("nesting-grid",
            new NativeMethods.RECT { Left = 0, Top = 0, Right = 128, Bottom = 128 },
            new NativeMethods.RECT { Left = 0, Top = 0, Right = 128, Bottom = 128 }, 1);
        var nestingGridSnapshot = new DesktopGridSnapshot(nestingGridDisplay, 64, 64, [], true);
        var occupied = new List<NativeMethods.RECT>();
        for (int index = 0; index < 4; index++)
        {
            DesktopGridPlacement? placement = DesktopGridService.Find(nestingGridSnapshot, occupied, null, 1.2);
            Require(placement is not null && occupied.All(bounds => !Intersects(bounds, placement.Bounds)),
                $"第 {index + 1} 个定位子窗未取得无冲突桌面网格。");
            occupied.Add(placement!.Bounds);
        }
        Require(DesktopGridService.Find(nestingGridSnapshot, occupied, null, 1.2) is null,
            "桌面网格已满时仍返回了冲突位置。");

        Console.WriteLine("PASS: organizer nesting");
    }
    finally { try { Directory.Delete(root, recursive: true); } catch { } }
    return;
}

if (args is ["--aug31-organizer-requirements"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-aug31-{Guid.NewGuid():N}");
    Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
    Directory.CreateDirectory(root);
    try
    {
        var defaults = new AppStateV2();
        Require(defaults.SchemaVersion == 8 && defaults.GlobalSettings.MoveOrganizerFilesToDesktopOnDelete,
            "新状态没有保持 Schema 8 或删除转移默认值不是开启。");
        Require(defaults.GlobalSettings.ResolveCompactNameScale(OrganizerPlacementMode.Floating) == 1 &&
                defaults.GlobalSettings.ResolveCompactNameScale(OrganizerPlacementMode.Positioned) == 1 &&
                defaults.GlobalSettings.ExpandedNameScale == 1,
            "全局收起/展开名称默认值错误。");
        string missingSettingsPath = Path.Combine(root, "missing-settings.json");
        await File.WriteAllTextAsync(missingSettingsPath, """{"SchemaVersion":8,"GlobalSettings":{},"Organizers":[]}""");
        Require((await new StateStore(missingSettingsPath).LoadAsync()).GlobalSettings.MoveOrganizerFilesToDesktopOnDelete,
            "旧状态缺少删除字段时没有回退为开启。");
        var clampedNames = StateStore.Normalize(new AppStateV2
        {
            GlobalSettings = new GlobalSettings
            {
                UniformFloatingCompactNameScale = .2,
                UniformPositionedCompactNameScale = 2,
                ExpandedNameScale = 2
            }
        }).GlobalSettings;
        Require(clampedNames.UniformFloatingCompactNameScale == .6 &&
                clampedNames.ExpandedNameScale == 1,
            "全局收起/展开名称比例没有限制在 60%–100%。");
        Require(new OrganizerDefinition().CompactListItemScale == 1 &&
                OrganizerInteractionMath.ApplyWheelSteps(1, 1, .5, 1.65) == 1.05 &&
                OrganizerInteractionMath.ApplyWheelSteps(.51, -1, .5, 1.65) == .5 &&
                OrganizerInteractionMath.ApplyWheelSteps(1.64, 1, .5, 1.65) == 1.65,
            "精简列表缩放默认值、步进或上下限错误。");
        Require(OrganizerInteractionMath.ShouldApplyCtrlWheelScale(true, false, false, false, false, true) &&
                !OrganizerInteractionMath.ShouldApplyCtrlWheelScale(true, false, false, false, false, false) &&
                !OrganizerInteractionMath.ShouldApplyCtrlWheelScale(false, false, false, false, false, true),
            "精简列表 Ctrl+滚轮交互条件错误。");
        Require(OrganizerInteractionMath.CanChangePlacementMode(OrganizerPlacementMode.Floating, OrganizerPlacementMode.Positioned) &&
                OrganizerInteractionMath.CanChangePlacementMode(OrganizerPlacementMode.Positioned, OrganizerPlacementMode.Floating) &&
                OrganizerInteractionMath.CanChangePlacementMode(OrganizerPlacementMode.Station, OrganizerPlacementMode.Station) &&
                !OrganizerInteractionMath.CanChangePlacementMode(OrganizerPlacementMode.Station, OrganizerPlacementMode.Floating) &&
                !OrganizerInteractionMath.CanChangePlacementMode(OrganizerPlacementMode.Positioned, OrganizerPlacementMode.Station),
            "收纳窗模式转换矩阵错误。");

        defaults.GlobalSettings.MoveOrganizerFilesToDesktopOnDelete = false;
        defaults.GlobalSettings.UseUniformFloatingCompactNameScale = true;
        defaults.GlobalSettings.UniformFloatingCompactNameScale = .72;
        defaults.GlobalSettings.UseUniformPositionedCompactNameScale = true;
        defaults.GlobalSettings.UniformPositionedCompactNameScale = .88;
        defaults.GlobalSettings.ExpandedNameScale = .84;
        defaults.Organizers =
        [
            new OrganizerDefinition { Name = "A", CompactListItemScale = .55, ItemScale = 1.4 },
            new OrganizerDefinition { Name = "B", CompactListItemScale = 1.65, ItemScale = .8 }
        ];
        Require(defaults.GlobalSettings.ResolveCompactNameScale(OrganizerPlacementMode.Floating) == .72 &&
                defaults.GlobalSettings.ResolveCompactNameScale(OrganizerPlacementMode.Positioned) == .72 &&
                defaults.GlobalSettings.ResolveCompactNameScale(OrganizerPlacementMode.Station) == 1 &&
                defaults.GlobalSettings.ResolveExpandedNameScale(OrganizerPlacementMode.Floating) == .84 &&
                defaults.GlobalSettings.ResolveExpandedNameScale(OrganizerPlacementMode.Station) == 1,
            "全局名称比例没有统一应用或错误影响了 Station。");
        string settingsPath = Path.Combine(root, "settings.json");
        var settingsStore = new StateStore(settingsPath);
        await settingsStore.SaveAsync(defaults);
        AppStateV2 reloaded = await settingsStore.LoadAsync();
        Require(!reloaded.GlobalSettings.MoveOrganizerFilesToDesktopOnDelete &&
                reloaded.GlobalSettings.UniformFloatingCompactNameScale == .72 &&
                reloaded.GlobalSettings.ExpandedNameScale == .84 &&
                reloaded.Organizers[0].CompactListItemScale == .55 && reloaded.Organizers[0].ItemScale == 1.4 &&
                reloaded.Organizers[1].CompactListItemScale == 1.65 && reloaded.Organizers[1].ItemScale == .8,
            "删除、全局名称或精简列表独立比例没有保存重载。");

        string createdRoot = Path.Combine(root, "created");
        var createdOrganizer = new OrganizerDefinition { Name = "新便签", StorageAbsolutePath = createdRoot };
        var aug31NoteStore = new NoteStore(Path.Combine(root, "legacy-documents"));
        string createdPath = await AppHost.CreateOrganizerPortableNoteAsync(
            createdOrganizer,
            aug31NoteStore,
            "顶层便签",
            new PortableNoteDocument { Theme = NoteTheme.RainBlue, Html = "<p>created</p>" },
            () => Task.CompletedTask);
        Require(Path.GetDirectoryName(createdPath) == Path.GetFullPath(createdRoot) &&
                Path.GetExtension(createdPath).Equals(".tucknote", StringComparison.OrdinalIgnoreCase) &&
                createdOrganizer.ItemOrder.SequenceEqual([Path.GetFileName(createdPath)]) &&
                new StorageService(createdRoot, createIfMissing: false).ReadItems().Single().Kind == WidgetItemKind.PortableNote &&
                (await aug31NoteStore.LoadPortableAsync(createdPath)).Html == "<p>created</p>",
            "新建便签没有成为收纳目录顶层的有效 .tucknote。");

        string aug31MigrationRoot = Path.Combine(root, "migration");
        Directory.CreateDirectory(aug31MigrationRoot);
        Guid goodId = Guid.NewGuid();
        Guid brokenId = Guid.NewGuid();
        await aug31NoteStore.SaveAsync(goodId, new NoteDocument { Html = "<p>good</p>" });
        await File.WriteAllTextAsync(Path.Combine(root, "legacy-documents", $"{brokenId:N}.json"), "{broken");
        var good = new NoteDefinition
        {
            Id = goodId,
            Name = "成功",
            Theme = NoteTheme.Graphite,
            FontSize = 19,
            ShowRuledLines = true,
            Placement = new NoteWindowPlacement { MonitorDevice = "display", XDip = 1, YDip = 2, WidthDip = 360, HeightDip = 300 }
        };
        var broken = new NoteDefinition { Id = brokenId, Name = "损坏", Theme = NoteTheme.CloudPaper };
        var migrating = new OrganizerDefinition
        {
            Name = "迁移",
            StorageAbsolutePath = aug31MigrationRoot,
            Notes = [good, broken],
            ItemOrder = ["before.txt", OrganizerNoteRules.ItemKey(goodId), OrganizerNoteRules.ItemKey(brokenId), "after.txt"]
        };
        var migrationState = new AppStateV2 { Organizers = [migrating] };
        var migrationStateStore = new StateStore(Path.Combine(root, "migration-state.json"));
        await AppHost.MigrateLegacyOrganizerNotesAsync(migrating, aug31NoteStore, () => migrationStateStore.SaveAsync(migrationState));
        string[] firstMigrationFiles = Directory.GetFiles(aug31MigrationRoot, "*.tucknote", SearchOption.TopDirectoryOnly);
        PortableNoteDocument firstMigrated = await aug31NoteStore.LoadPortableAsync(firstMigrationFiles.Single());
        AppStateV2 persistedAfterFirst = await migrationStateStore.LoadAsync();
        Require(migrating.Notes.Select(note => note.Id).SequenceEqual([brokenId]) && firstMigrationFiles.Length == 1 &&
                migrating.ItemOrder[0] == "before.txt" && migrating.ItemOrder[1] == Path.GetFileName(firstMigrationFiles[0]) &&
                migrating.ItemOrder[2] == OrganizerNoteRules.ItemKey(brokenId) && migrating.ItemOrder[3] == "after.txt" &&
                firstMigrated.Html == "<p>good</p>" && firstMigrated.Theme == NoteTheme.Graphite &&
                firstMigrated.FontSize == 19 && firstMigrated.ShowRuledLines && firstMigrated.Placement?.WidthDip == 360 &&
                persistedAfterFirst.Organizers.Single().Notes.Select(note => note.Id).SequenceEqual([brokenId]) &&
                !(await aug31NoteStore.ExistsAsync(goodId)) && await aug31NoteStore.ExistsAsync(brokenId),
            "逐张迁移没有独立提交成功项并保留损坏项与顺序。");

        string rollbackRoot = Path.Combine(root, "migration-save-failure");
        Directory.CreateDirectory(rollbackRoot);
        Guid rollbackId = Guid.NewGuid();
        await aug31NoteStore.SaveAsync(rollbackId, new NoteDocument { Html = "<p>rollback</p>" });
        var rollbackOrganizer = new OrganizerDefinition
        {
            Name = "回滚",
            StorageAbsolutePath = rollbackRoot,
            Notes = [new NoteDefinition { Id = rollbackId, Name = "回滚便签" }],
            ItemOrder = [OrganizerNoteRules.ItemKey(rollbackId)]
        };
        IReadOnlyDictionary<Guid, string> rollbackResult = await AppHost.MigrateLegacyOrganizerNotesAsync(
            rollbackOrganizer,
            aug31NoteStore,
            () => Task.FromException(new IOException("expected save failure")));
        Require(rollbackResult.Count == 0 && rollbackOrganizer.Notes.Single().Id == rollbackId &&
                rollbackOrganizer.ItemOrder.SequenceEqual([OrganizerNoteRules.ItemKey(rollbackId)]) &&
                await aug31NoteStore.ExistsAsync(rollbackId) &&
                Directory.GetFiles(rollbackRoot, "*.tucknote", SearchOption.TopDirectoryOnly).Length == 0,
            "迁移状态保存失败时没有恢复旧定义、顺序和正文，或遗留了目标文件。");
        await aug31NoteStore.SaveAsync(brokenId, new NoteDocument { Html = "<p>repaired</p>" });
        await AppHost.MigrateLegacyOrganizerNotesAsync(migrating, aug31NoteStore, () => migrationStateStore.SaveAsync(migrationState));
        string[] repairedFiles = Directory.GetFiles(aug31MigrationRoot, "*.tucknote", SearchOption.TopDirectoryOnly);
        Require(migrating.Notes.Count == 0 && repairedFiles.Length == 2 &&
                migrating.ItemOrder[0] == "before.txt" && migrating.ItemOrder[3] == "after.txt" &&
                migrating.ItemOrder.Skip(1).Take(2).All(item => item.EndsWith(".tucknote", StringComparison.OrdinalIgnoreCase)) &&
                !(await aug31NoteStore.ExistsAsync(brokenId)),
            "损坏便签修复后没有无重复地完成重试并保持顺序。");
        await AppHost.MigrateLegacyOrganizerNotesAsync(migrating, aug31NoteStore, () => migrationStateStore.SaveAsync(migrationState));
        Require(Directory.GetFiles(aug31MigrationRoot, "*.tucknote", SearchOption.TopDirectoryOnly).Length == 2,
            "无待迁移项时重复运行产生了重复文件。");

        string themeRoot = Path.Combine(root, "theme-registered");
        string nestedRoot = Path.Combine(themeRoot, "nested");
        string unregisteredRoot = Path.Combine(root, "theme-unregistered");
        Directory.CreateDirectory(themeRoot);
        Directory.CreateDirectory(nestedRoot);
        Directory.CreateDirectory(unregisteredRoot);
        var preserved = new PortableNoteDocument
        {
            Theme = NoteTheme.SunYellow,
            FontSize = 23,
            ShowRuledLines = true,
            Placement = new PortableNotePlacement { MonitorDevice = "display", XDip = 3, YDip = 4, WidthDip = 320, HeightDip = 240 },
            Html = "<p>preserve</p>"
        };
        string themedPath = await aug31NoteStore.CreatePortableAsync(themeRoot, "theme", preserved);
        string excludedPath = await aug31NoteStore.CreatePortableAsync(themeRoot, "open", new PortableNoteDocument { Theme = NoteTheme.SunYellow, Html = "open" });
        string brokenThemePath = Path.Combine(themeRoot, "broken.tucknote");
        await File.WriteAllTextAsync(brokenThemePath, "broken");
        byte[] brokenBefore = await File.ReadAllBytesAsync(brokenThemePath);
        string nestedPath = await aug31NoteStore.CreatePortableAsync(nestedRoot, "nested", new PortableNoteDocument { Theme = NoteTheme.SunYellow, Html = "nested" });
        string unregisteredPath = await aug31NoteStore.CreatePortableAsync(unregisteredRoot, "other", new PortableNoteDocument { Theme = NoteTheme.SunYellow, Html = "other" });
        IReadOnlyList<string> themeFailures = await aug31NoteStore.ApplyThemeToTopLevelPortableFilesAsync(
            themeRoot,
            NoteTheme.WheatPaper,
            new HashSet<string>([Path.GetFullPath(excludedPath)], StringComparer.OrdinalIgnoreCase));
        PortableNoteDocument themed = await aug31NoteStore.LoadPortableAsync(themedPath);
        Require(themed.Theme == NoteTheme.WheatPaper && themed.Html == preserved.Html && themed.FontSize == 23 && themed.ShowRuledLines &&
                themed.Placement?.WidthDip == 320 &&
                (await aug31NoteStore.LoadPortableAsync(excludedPath)).Theme == NoteTheme.SunYellow &&
                (await aug31NoteStore.LoadPortableAsync(nestedPath)).Theme == NoteTheme.SunYellow &&
                (await aug31NoteStore.LoadPortableAsync(unregisteredPath)).Theme == NoteTheme.SunYellow &&
                themeFailures.SequenceEqual([Path.GetFullPath(brokenThemePath)]) &&
                (await File.ReadAllBytesAsync(brokenThemePath)).SequenceEqual(brokenBefore),
            "主题同步没有限制在注册目录顶层、没有排除已打开文件，或破坏了正文/布局/损坏文件。");

        string sourceRoot = Path.Combine(root, "move-source");
        string destinationRoot = Path.Combine(root, "move-destination");
        string sourceNote = Path.Combine(sourceRoot, "note.tucknote");
        string nestedNote = Path.Combine(sourceRoot, "child", "nested.tucknote");
        Require(OrganizerNoteRules.ResolvePortablePathAfterMove(sourceRoot, destinationRoot, sourceNote, true) ==
                    Path.Combine(Path.GetFullPath(destinationRoot), "note.tucknote") &&
                OrganizerNoteRules.ResolvePortablePathAfterMove(sourceRoot, destinationRoot, sourceNote, false) == Path.GetFullPath(sourceNote) &&
                OrganizerNoteRules.ResolvePortablePathAfterMove(sourceRoot, null, sourceNote, true) == Path.GetFullPath(sourceNote) &&
                OrganizerNoteRules.ResolvePortablePathAfterMove(sourceRoot, destinationRoot, nestedNote, true) == Path.GetFullPath(nestedNote),
            "删除移动成功、移动失败、关闭转移或子目录路径的重绑规则错误。");

        Console.WriteLine("PASS: aug31 organizer requirements");
    }
    finally
    {
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", null);
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--expanded-content-mode"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-expanded-content-{Guid.NewGuid():N}");
    Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
    Directory.CreateDirectory(root);
    try
    {
        var defaults = new OrganizerDefinition();
        Require(defaults.ExpandedContentMode == OrganizerExpandedContentMode.Icon &&
                defaults.CompactListCanvasWidthDip == OrganizerLimits.DefaultCompactListCanvasWidthDip &&
                defaults.CompactListCanvasHeightDip == OrganizerLimits.DefaultCompactListCanvasHeightDip,
            "新收纳窗没有默认使用图标模式或精简画布默认尺寸错误。");

        string legacyPath = Path.Combine(root, "legacy.json");
        await File.WriteAllTextAsync(legacyPath,
            """{"SchemaVersion":8,"GlobalSettings":{},"Organizers":[{"Name":"旧窗口","PlacementMode":0}]}""");
        AppStateV2 legacy = await new StateStore(legacyPath).LoadAsync();
        Require(legacy.Organizers.Single().ExpandedContentMode == OrganizerExpandedContentMode.Icon,
            "缺少展开方式字段的旧状态没有回退到图标模式。");

        string statePath = Path.Combine(root, "round-trip.json");
        var store = new StateStore(statePath);
        await store.SaveAsync(new AppStateV2
        {
            Organizers =
            [
                new OrganizerDefinition
                {
                    Name = "精简窗口",
                    ExpandedContentMode = OrganizerExpandedContentMode.CompactList,
                    CompactListCanvasWidthDip = 333,
                    CompactListCanvasHeightDip = 444,
                    ManualCanvasBaseWidthDip = 800,
                    ManualCanvasBaseHeightDip = 600
                },
                new OrganizerDefinition
                {
                    Name = "中转站",
                    PlacementMode = OrganizerPlacementMode.Station,
                    ExpandedContentMode = OrganizerExpandedContentMode.CompactList
                }
            ]
        });
        AppStateV2 reloaded = await store.LoadAsync();
        OrganizerDefinition compact = reloaded.Organizers.Single(item => item.Name == "精简窗口");
        OrganizerDefinition station = reloaded.Organizers.Single(item => item.Name == "中转站");
        Require(compact.ExpandedContentMode == OrganizerExpandedContentMode.CompactList &&
                compact.CompactListCanvasWidthDip == 333 && compact.CompactListCanvasHeightDip == 444 &&
                compact.ManualCanvasBaseWidthDip == 800 && compact.ManualCanvasBaseHeightDip == 600,
            "精简画布与图标画布尺寸没有独立保存重载。");
        Require(station.ExpandedContentMode == OrganizerExpandedContentMode.Icon,
            "中转站状态没有强制归一为图标模式。");

        OrganizerDefinition contentModeCopy = OrganizerInteractionMath.CopySettings(compact, "副本");
        Require(contentModeCopy.ExpandedContentMode == OrganizerExpandedContentMode.CompactList &&
                contentModeCopy.CompactListCanvasWidthDip == 333 && contentModeCopy.CompactListCanvasHeightDip == 444,
            "复制收纳窗时丢失了精简展开设置。");

        const int resizeStartLeft = 100;
        const int resizeStartTop = 100;
        const int width = 300;
        const int height = 250;
        var right = OrganizerInteractionMath.ResizeFixedEdges(
            CanvasResizeEdge.Right, resizeStartLeft, resizeStartTop, width, height, 40, 0, 180, 160, 0, 0, 1000, 800);
        var bottom = OrganizerInteractionMath.ResizeFixedEdges(
            CanvasResizeEdge.Bottom, resizeStartLeft, resizeStartTop, width, height, 0, 30, 180, 160, 0, 0, 1000, 800);
        var corner = OrganizerInteractionMath.ResizeFixedEdges(
            CanvasResizeEdge.Right | CanvasResizeEdge.Bottom, resizeStartLeft, resizeStartTop, width, height, 40, 30, 180, 160, 0, 0, 1000, 800);
        var minimum = OrganizerInteractionMath.ResizeFixedEdges(
            CanvasResizeEdge.Right, resizeStartLeft, resizeStartTop, width, height, -500, 0, 180, 160, 0, 0, 1000, 800);
        Require(right == (resizeStartLeft, resizeStartTop, 340, height), "拖动右边没有只改变宽度并固定左边。");
        Require(bottom == (resizeStartLeft, resizeStartTop, width, 280), "拖动下边没有只改变高度并固定上边。");
        Require(corner == (resizeStartLeft, resizeStartTop, 340, 280), "拖动右下角没有同时改变宽高并固定左上角。");
        Require(minimum == (resizeStartLeft, resizeStartTop, 180, height), "最小宽度钳制移动了固定对边。");

        Console.WriteLine("PASS: expanded content mode");
    }
    finally
    {
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", null);
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args.Length is 1 or 2 && args[0] == "--organizer-icon-content")
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void CreateShortcut(string shortcutPath, string? targetPath, string iconLocation)
    {
        object? shell = null;
        object? shortcutObject = null;
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell") ??
                throw new InvalidOperationException("WScript.Shell unavailable.");
            shell = Activator.CreateInstance(shellType) ??
                throw new InvalidOperationException("Could not create WScript.Shell.");
            shortcutObject = ((dynamic)shell).CreateShortcut(shortcutPath);
            if (!string.IsNullOrWhiteSpace(targetPath)) ((dynamic)shortcutObject).TargetPath = targetPath;
            ((dynamic)shortcutObject).IconLocation = iconLocation;
            ((dynamic)shortcutObject).Save();
        }
        finally
        {
            if (shortcutObject is not null && System.Runtime.InteropServices.Marshal.IsComObject(shortcutObject))
                _ = System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcutObject);
            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                _ = System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    static (string Path, int Index) ResolveExpectedShortcutIcon(string shortcutPath)
    {
        object? shell = null;
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell") ??
                throw new InvalidOperationException("WScript.Shell unavailable.");
            shell = Activator.CreateInstance(shellType) ??
                throw new InvalidOperationException("Could not create WScript.Shell.");
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string current = Path.GetFullPath(shortcutPath);
            for (int depth = 0; depth < 4 && visited.Add(current); depth++)
            {
                object? shortcutObject = null;
                try
                {
                    shortcutObject = ((dynamic)shell).CreateShortcut(current);
                    string location = ((string)((dynamic)shortcutObject).IconLocation).Trim();
                    int comma = location.LastIndexOf(',');
                    string iconPath = (comma > 1 ? location[..comma] : location).Trim().Trim('"');
                    int iconIndex = comma > 1 && int.TryParse(location[(comma + 1)..], out int parsed) ? parsed : 0;
                    iconPath = Environment.ExpandEnvironmentVariables(iconPath);
                    if (!Path.IsPathFullyQualified(iconPath))
                        iconPath = Path.Combine(Path.GetDirectoryName(current)!, iconPath);
                    iconPath = Path.GetFullPath(iconPath);
                    if (!File.Exists(iconPath))
                        throw new InvalidOperationException($"快捷方式图标资源不存在：{iconPath}");
                    if (!Path.GetExtension(iconPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                        return (iconPath, iconIndex);
                    current = iconPath;
                }
                finally
                {
                    if (shortcutObject is not null && System.Runtime.InteropServices.Marshal.IsComObject(shortcutObject))
                        _ = System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcutObject);
                }
            }
            throw new InvalidOperationException("快捷方式图标链为空、循环或超过四层。");
        }
        finally
        {
            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                _ = System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    static double Similarity(IconCacheService.IconSnapshot expected, IconCacheService.IconSnapshot actual)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height) return 0;
        long difference = 0;
        long range = 0;
        for (int index = 0; index < expected.Pixels.Length; index += 4)
        {
            if (expected.Pixels[index + 3] == 0 && actual.Pixels[index + 3] == 0) continue;
            for (int channel = 0; channel < 4; channel++)
            {
                difference += Math.Abs(expected.Pixels[index + channel] - actual.Pixels[index + channel]);
                range += byte.MaxValue;
            }
        }
        return range == 0 ? 0 : 1d - (double)difference / range;
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-organizer-icon-content-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        string sourceIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TuckPane.ico");
        string innerShortcutPath = Path.Combine(root, "inner.lnk");
        string outerShortcutPath = Path.Combine(root, "outer.lnk");
        CreateShortcut(innerShortcutPath, Environment.ProcessPath ?? sourceIconPath, $"{sourceIconPath},0");
        CreateShortcut(outerShortcutPath, null, $"{innerShortcutPath},0");
        string urlChainPath = Path.Combine(root, "outer.url");
        await File.WriteAllTextAsync(urlChainPath, $"[InternetShortcut]{Environment.NewLine}URL=about:blank{Environment.NewLine}IconFile=\"{Path.GetFileName(innerShortcutPath)}\"{Environment.NewLine}IconIndex=0{Environment.NewLine}");

        IconCacheService.IconSnapshot expected = IconCacheService.ExtractShellIconPixels(sourceIconPath);
        IconCacheService.IconSnapshot actual = IconCacheService.ExtractShellIconPixels(outerShortcutPath);
        Require(IconCacheService.TryGetVisiblePixelBounds(expected, out IconCacheService.IconVisibleBounds expectedBounds) &&
                expectedBounds.Width >= expected.Width / 3 && expectedBounds.Height >= expected.Height / 3,
            $"直接资源图标主体占比异常：{expectedBounds.Width}x{expectedBounds.Height}/{expected.Width}x{expected.Height}。");
        double fixtureSimilarity = Similarity(expected, actual);
        Require(fixtureSimilarity >= .95,
            $"二级快捷方式没有解析到最终图标，像素相似度仅 {fixtureSimilarity:F4}。");
        Require(IconCacheService.TryGetVisiblePixelBounds(actual, out IconCacheService.IconVisibleBounds actualBounds) &&
                actualBounds.Width >= expectedBounds.Width * .8 && actualBounds.Height >= expectedBounds.Height * .8,
            $"二级快捷方式图标主体被缩小：资源 {expectedBounds.Width}x{expectedBounds.Height}，快捷方式 {actualBounds.Width}x{actualBounds.Height}。");

        IconCacheService.IconSnapshot urlChainIcon = IconCacheService.ExtractShellIconPixels(urlChainPath);
        Require(Similarity(expected, urlChainIcon) >= .95 &&
                IconCacheService.TryGetVisiblePixelBounds(urlChainIcon, out IconCacheService.IconVisibleBounds urlBounds) &&
                urlBounds.Width >= expectedBounds.Width * .8 && urlBounds.Height >= expectedBounds.Height * .8,
            "URL→LNK→资源图标链没有解析到最终资源，或主体被缩小。");
        string chainIdentityBefore = IconCacheService.BuildCacheIdentity(outerShortcutPath);
        File.SetLastWriteTimeUtc(innerShortcutPath, File.GetLastWriteTimeUtc(innerShortcutPath).AddSeconds(2));
        string chainIdentityAfter = IconCacheService.BuildCacheIdentity(outerShortcutPath);
        Require(!string.Equals(chainIdentityBefore, chainIdentityAfter, StringComparison.Ordinal),
            "中间快捷方式元数据变化后，外层图标缓存身份没有变化。");

        if (args.Length == 2)
        {
            string actualShortcutPath = Path.GetFullPath(args[1]);
            Require(File.Exists(actualShortcutPath), $"真实快捷方式不存在：{actualShortcutPath}");
            (string expectedPath, _) = ResolveExpectedShortcutIcon(actualShortcutPath);
            expected = IconCacheService.ExtractShellIconPixels(expectedPath);
            actual = IconCacheService.ExtractShellIconPixels(actualShortcutPath);
            IconCacheService.IconVisibleBounds realExpectedBounds = default;
            IconCacheService.IconVisibleBounds realActualBounds = default;
            Require(IconCacheService.TryGetVisiblePixelBounds(expected, out realExpectedBounds) &&
                    IconCacheService.TryGetVisiblePixelBounds(actual, out realActualBounds) &&
                    realExpectedBounds.Width >= expected.Width / 3 && realExpectedBounds.Height >= expected.Height / 3 &&
                    realActualBounds.Width >= realExpectedBounds.Width * .8 &&
                    realActualBounds.Height >= realExpectedBounds.Height * .8,
                $"真实快捷方式图标主体占比异常：资源 {realExpectedBounds.Width}x{realExpectedBounds.Height}，快捷方式 {realActualBounds.Width}x{realActualBounds.Height}。");
            double actualSimilarity = Similarity(expected, actual);
            Require(actualSimilarity >= .95,
                $"真实快捷方式没有解析到最终图标，像素相似度仅 {actualSimilarity:F4}：{actualShortcutPath}");
        }

        foreach (string extension in new[] { ".iso", ".img", ".vhd", ".unknown" })
        {
            string filePath = Path.Combine(root, "shell-item" + extension);
            File.WriteAllBytes(filePath, []);
            IconCacheService.IconSnapshot icon = IconCacheService.ExtractShellIconPixels(filePath);
            Require(icon.Width > 0 && icon.Height > 0 &&
                    icon.Pixels.Length == icon.Width * icon.Height * 4 &&
                    icon.Pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0),
                $"{extension} 没有返回可见 Shell 图标或通用回退。");
        }

        var organizer = new OrganizerDefinition
        {
            PlacementMode = OrganizerPlacementMode.Floating,
            ExpandedContentMode = OrganizerExpandedContentMode.Icon,
            CompactListCanvasWidthDip = 333,
            CompactListCanvasHeightDip = 444,
            ManualCanvasBaseWidthDip = 555,
            ManualCanvasBaseHeightDip = 666
        };
        Require(OrganizerInteractionMath.TryToggleExpandedContentMode(organizer) &&
                organizer.ExpandedContentMode == OrganizerExpandedContentMode.CompactList,
            "图标模式没有切换为精简模式。");
        Require(OrganizerInteractionMath.TryToggleExpandedContentMode(organizer) &&
                organizer.ExpandedContentMode == OrganizerExpandedContentMode.Icon &&
                organizer.CompactListCanvasWidthDip == 333 && organizer.CompactListCanvasHeightDip == 444 &&
                organizer.ManualCanvasBaseWidthDip == 555 && organizer.ManualCanvasBaseHeightDip == 666,
            "精简模式没有切回图标模式，或两套画布尺寸被覆盖。");

        var station = new OrganizerDefinition
        {
            PlacementMode = OrganizerPlacementMode.Station,
            ExpandedContentMode = OrganizerExpandedContentMode.Icon
        };
        Require(!OrganizerInteractionMath.TryToggleExpandedContentMode(station) &&
                station.ExpandedContentMode == OrganizerExpandedContentMode.Icon,
            "Station 不应允许切换内容模式。");

        Require(OrganizerInteractionMath.TryToggleExpandedContentMode(organizer),
            "保存前无法切换为精简模式。");
        var store = new StateStore(Path.Combine(root, "state.json"));
        await store.SaveAsync(new AppStateV2 { Organizers = [organizer, station] });
        AppStateV2 reloaded = await store.LoadAsync();
        Require(reloaded.Organizers.Single(item => item.Id == organizer.Id).ExpandedContentMode ==
                OrganizerExpandedContentMode.CompactList,
            "内容模式保存后没有正确重载。");
        Require(reloaded.Organizers.Single(item => item.Id == station.Id).ExpandedContentMode ==
                OrganizerExpandedContentMode.Icon,
            "Station 重载后没有保持图标模式。");

        Console.WriteLine("PASS: organizer icon and content mode");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--shortcut-clipboard-fixes"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-shortcut-clipboard-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        string sourceIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TuckPane.ico");
        string shortcutPath = Path.Combine(root, "TuckPane.lnk");
        object? shell = null;
        object? shortcutObject = null;
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell") ??
                throw new InvalidOperationException("WScript.Shell unavailable.");
            shell = Activator.CreateInstance(shellType) ??
                throw new InvalidOperationException("Could not create WScript.Shell.");
            shortcutObject = ((dynamic)shell).CreateShortcut(shortcutPath);
            ((dynamic)shortcutObject).TargetPath = Environment.ProcessPath ?? sourceIconPath;
            ((dynamic)shortcutObject).IconLocation = $"{sourceIconPath},0";
            ((dynamic)shortcutObject).Save();
        }
        finally
        {
            if (shortcutObject is not null && System.Runtime.InteropServices.Marshal.IsComObject(shortcutObject))
                _ = System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcutObject);
            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                _ = System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }

        IconCacheService.IconSnapshot expected = IconCacheService.ExtractShellIconPixels(sourceIconPath);
        IconCacheService.IconSnapshot actual = IconCacheService.ExtractShellIconPixels(shortcutPath);
        Require(expected.Width == actual.Width && expected.Height == actual.Height,
            "快捷方式基础图标尺寸与显式图标不一致。");
        long difference = 0;
        long range = 0;
        int maximumX = expected.Width * 45 / 100;
        int minimumY = expected.Height * 55 / 100;
        for (int y = minimumY; y < expected.Height; y++)
        {
            for (int x = 0; x < maximumX; x++)
            {
                int index = (y * expected.Width + x) * 4;
                if (expected.Pixels[index + 3] == 0 && actual.Pixels[index + 3] == 0) continue;
                for (int channel = 0; channel < 4; channel++)
                {
                    difference += Math.Abs(expected.Pixels[index + channel] - actual.Pixels[index + channel]);
                    range += byte.MaxValue;
                }
            }
        }
        double similarity = range == 0 ? 0 : 1d - (double)difference / range;
        Require(similarity >= .99, $"快捷方式左下区域仍含 Shell overlay，相似度仅 {similarity:F4}。");

        string editorPath = Path.Combine(AppContext.BaseDirectory, "Assets", "NoteEditor.html");
        string editor = await File.ReadAllTextAsync(editorPath);
        int copyStart = editor.IndexOf("editor.addEventListener('copy'", StringComparison.Ordinal);
        int copyEnd = editor.IndexOf("editor.addEventListener('wheel'", copyStart, StringComparison.Ordinal);
        Require(copyStart >= 0 && copyEnd > copyStart, "找不到便签复制处理器。");
        string copyHandler = editor[copyStart..copyEnd];
        Require(copyHandler.Contains("event.preventDefault();", StringComparison.Ordinal) &&
                copyHandler.Contains("setTimeout(() => post({ type: 'copyText', text }), 0);", StringComparison.Ordinal) &&
                !copyHandler.Contains("clipboardData.setData", StringComparison.Ordinal),
            "便签复制仍存在 WebView 与宿主双写，或未延迟到复制事件结束后投递。");

        Console.WriteLine("PASS: shortcut and clipboard fixes");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--portable-note-placement"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void RequireRejected(string path, string message)
    {
        try
        {
            _ = NoteStore.ValidatePortableDirectory(path);
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException ex) when (ex.Message != message)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-portable-note-placement-{Guid.NewGuid():N}");
    Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
    Directory.CreateDirectory(root);
    try
    {
        AppPaths.EnsureCreated();
        Directory.CreateDirectory(AppPaths.DesktopRoot);
        string target = Path.Combine(root, "中文 空格目录");
        Directory.CreateDirectory(target);
        Require(NoteStore.ValidatePortableDirectory(target) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)),
            "中文空格目录未通过本地目录验证。");
        Require(NoteStore.ValidatePortableDirectory(AppPaths.DesktopRoot) ==
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DesktopRoot)),
            "隔离桌面目录未通过本地目录验证。");
        Require(DesktopLayerService.IsDesktopHostClassName("Progman") &&
                DesktopLayerService.IsDesktopHostClassName("WorkerW") &&
                !DesktopLayerService.IsDesktopHostClassName("CabinetWClass"),
            "普通资源管理器文件夹窗口被错误识别为桌面宿主。");
        RequireRejected("relative-folder", "相对路径未被拒绝。");
        RequireRejected(@"\\server\share", "UNC 路径未被拒绝。");
        RequireRejected(Path.Combine(root, "missing"), "不存在的目录未被拒绝。");

        var document = new PortableNoteDocument
        {
            Theme = NoteTheme.Graphite,
            FontSize = 14,
            ShowRuledLines = false,
            Placement = new PortableNotePlacement
            {
                MonitorDevice = "test-display",
                XDip = 10,
                YDip = 20,
                WidthDip = 360,
                HeightDip = 300
            },
            Html = string.Empty
        };
        var store = new NoteStore(Path.Combine(root, "internal-notes"));
        string first = await store.CreatePortableAsync(target, "新建便签", document);
        byte[] firstBytes = await File.ReadAllBytesAsync(first);
        string second = await store.CreatePortableAsync(target, "新建便签", document);
        Require(Path.GetFileName(first) == "新建便签.tucknote" &&
                Path.GetFileName(second) == "新建便签 2.tucknote",
            "便携便签没有按无括号规则自动编号。");
        Require((await File.ReadAllBytesAsync(first)).SequenceEqual(firstBytes), "自动编号覆盖了首个便签。");

        var concurrentStore = new NoteStore(Path.Combine(root, "other-internal-notes"));
        string[] concurrent = await Task.WhenAll(
            store.CreatePortableAsync(target, "新建便签", document),
            concurrentStore.CreatePortableAsync(target, "新建便签", document));
        Require(concurrent.Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(
                new[] { "新建便签 3.tucknote", "新建便签 4.tucknote" }.OrderBy(name => name, StringComparer.Ordinal)),
            "并发创建没有保留两个独立的自动编号文件。");
        PortableNoteDocument loaded = await store.LoadPortableAsync(first);
        Require(loaded.Format == "TuckPane.Note" && loaded.Version == 1 &&
                loaded.Theme == document.Theme && loaded.FontSize == 14 &&
                !loaded.ShowRuledLines && loaded.Html.Length == 0 && loaded.Placement?.WidthDip == 360,
            "新建文件无法按严格 .tucknote v1 重新读取。");
        Require(!Directory.EnumerateFiles(target, "*.tmp", SearchOption.TopDirectoryOnly).Any(),
            "原子创建后残留了临时文件。");

        DataPackageOperation noteAllowed = OrganizerInteractionMath.ExternalDragAllowedOperations(WidgetItemKind.Note);
        Require(noteAllowed == (DataPackageOperation.Copy | DataPackageOperation.Move) &&
                !noteAllowed.HasFlag(DataPackageOperation.Link) &&
                OrganizerInteractionMath.ExternalDragRequestedOperation(WidgetItemKind.Note) == DataPackageOperation.Move,
            "内部便签拖出未保持默认移动、允许复制且禁止链接。");
        Require(OrganizerInteractionMath.ExternalDragMovedSource(DataPackageOperation.Move, internalDropAccepted: false, sourceItemExists: true) &&
                OrganizerInteractionMath.ExternalDragMovedSource(DataPackageOperation.None, internalDropAccepted: false, sourceItemExists: false) &&
                !OrganizerInteractionMath.ExternalDragMovedSource(DataPackageOperation.Copy, internalDropAccepted: false, sourceItemExists: true) &&
                !OrganizerInteractionMath.ExternalDragMovedSource(DataPackageOperation.None, internalDropAccepted: false, sourceItemExists: true) &&
                !OrganizerInteractionMath.ExternalDragMovedSource(DataPackageOperation.Move, internalDropAccepted: true, sourceItemExists: false),
            "内部便签拖出未正确区分显式 Move、None 但暂存源消失、Copy 与内部重排。");

        string staging = await store.CreatePortableStagingAsync("寿命检查", document);
        StorageFile storageFile = await StorageFile.GetFileFromPathAsync(staging);
        var package = new DataPackage
        {
            RequestedOperation = OrganizerInteractionMath.ExternalDragRequestedOperation(WidgetItemKind.Note)
        };
        package.SetStorageItems([storageFile], readOnly: false);
        DataPackageView view = package.GetView();
        Require(view.Contains(StandardDataFormats.StorageItems), "便签暂存文件未写入 StorageItems 数据格式。");
        IReadOnlyList<IStorageItem> storageItems = await view.GetStorageItemsAsync();
        Require(storageItems.Count == 1 &&
                Path.GetFullPath(storageItems[0].Path).Equals(Path.GetFullPath(staging), StringComparison.OrdinalIgnoreCase),
            "便签暂存文件无法作为单个 StorageItem 回读。");
        Require(File.Exists(staging), "StorageItems 拖放数据准备期间暂存文件已消失。");
        string stagingDirectory = Path.GetDirectoryName(staging)!;
        File.Delete(staging);
        Directory.Delete(stagingDirectory);
        Require(!Directory.Exists(stagingDirectory), "显式清理没有移除便签暂存目录。");

        Console.WriteLine("PASS: portable note placement");
    }
    finally
    {
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", null);
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--todo-checkbox-scale"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    var row = new TodoWindow.TodoRow(new PortableTodoTask { Text = "缩放检查" }, 14);
    var changed = new List<string?>();
    row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
    Require(row.CheckBoxSize == 20, "14 号的复选框基准尺寸错误。");
    row.FontSize = 15;
    Require(row.CheckBoxSize == 21 && changed.Contains(nameof(TodoWindow.TodoRow.CheckBoxSize)),
        "复选框没有随字号增大，或缺少属性变更通知。");
    row.FontSize = 8;
    Require(row.CheckBoxSize == 20, "14 号以下的复选框不应小于 20 DIP。");

    string xamlPath = Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", "TodoWindow.xaml");
    XDocument document = XDocument.Parse(await File.ReadAllTextAsync(xamlPath));
    XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    XElement taskCheckBox = document.Descendants(presentation + "CheckBox")
        .Single(element => (string?)element.Attribute("Click") == "TaskCheckBox_Click");
    XElement? viewbox = taskCheckBox.Parent;
    Require(viewbox?.Name == presentation + "Viewbox" &&
            (string?)viewbox.Attribute("Width") == "{Binding CheckBoxSize}" &&
            (string?)viewbox.Attribute("Height") == "{Binding CheckBoxSize}" &&
            (string?)taskCheckBox.Attribute("Width") == "20" &&
            (string?)taskCheckBox.Attribute("Height") == "20" &&
            (string?)taskCheckBox.Attribute("DataContextChanged") == "TaskCheckBox_DataContextChanged" &&
            (string?)taskCheckBox.Attribute("IsChecked") == "False" &&
            (string?)taskCheckBox.Attribute("Tag") == "{Binding}",
        "复选框的缩放容器、基准尺寸或状态同步绑定错误。");

    Console.WriteLine("PASS: todo checkbox scale");
    return;
}

if (args is ["--portable-todo"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static double ContrastRatio(Windows.UI.Color first, Windows.UI.Color second)
    {
        static double Luminance(Windows.UI.Color color) =>
            .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }

        double firstLuminance = Luminance(first);
        double secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + .05) /
            (Math.Min(firstLuminance, secondLuminance) + .05);
    }

    static async Task RequireRejectedAsync(NoteStore store, string path, string json, string message)
    {
        await File.WriteAllTextAsync(path, json);
        byte[] before = await File.ReadAllBytesAsync(path);
        try
        {
            _ = await store.LoadTodoAsync(path);
            throw new InvalidOperationException(message);
        }
        catch (InvalidDataException)
        {
        }
        Require((await File.ReadAllBytesAsync(path)).SequenceEqual(before), message + " 原文件被改写。");
    }

    static string Mutate(string json, Action<System.Text.Json.Nodes.JsonObject> change)
    {
        System.Text.Json.Nodes.JsonObject root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        change(root);
        return root.ToJsonString();
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-portable-todo-{Guid.NewGuid():N}");
    Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
    Directory.CreateDirectory(root);
    try
    {
        string files = Path.Combine(root, "files");
        Directory.CreateDirectory(files);
        var store = new NoteStore(Path.Combine(root, "internal-notes"));
        Guid firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var document = new PortableTodoDocument
        {
            Theme = NoteTheme.SunYellow,
            FontSize = 17,
            Placement = new PortableNotePlacement
            {
                MonitorDevice = "test-display",
                XDip = 12,
                YDip = 24,
                WidthDip = 360,
                HeightDip = 480
            },
            Tasks =
            [
                new PortableTodoTask { Id = firstId, Text = "第一项" },
                new PortableTodoTask { Id = secondId, Text = "第二项" }
            ]
        };

        string first = await store.CreateTodoAsync(files, "新建待办", document);
        byte[] firstBytes = await File.ReadAllBytesAsync(first);
        string second = await store.CreateTodoAsync(files, "新建待办", document);
        Require(Path.GetFileName(first) == "新建待办.tucktodo" &&
                Path.GetFileName(second) == "新建待办 2.tucktodo" &&
                (await File.ReadAllBytesAsync(first)).SequenceEqual(firstBytes),
            "待办自动编号覆盖了已有文件。");
        PortableTodoDocument loaded = await store.LoadTodoAsync(first);
        Require(loaded.Format == "TuckPane.Todo" && loaded.Version == 1 &&
                loaded.Theme == NoteTheme.SunYellow && loaded.FontSize == 17 &&
                loaded.Placement is { WidthDip: 360, HeightDip: 480 } &&
                loaded.Tasks.Select(task => task.Id).SequenceEqual([firstId, secondId]),
            "严格 .tucktodo v1 创建后无法完整回读。");
        Require(!Directory.EnumerateFiles(files, "*.tmp", SearchOption.TopDirectoryOnly).Any(),
            "待办原子创建残留了临时文件。");

        WidgetItem todoItem = new StorageService(files, createIfMissing: false).ReadItems()
            .Single(item => item.RelativeName == Path.GetFileName(first));
        Require(todoItem.Kind == WidgetItemKind.PortableTodo && todoItem.Name == "新建待办",
            ".tucktodo 未使用专用分类或未隐藏扩展名。");

        TodoRules.Move(loaded, secondId, 0);
        loaded.FontSize = 21;
        loaded.Placement!.XDip = 33;
        loaded.Tasks[0].Text = "修改后的第二项";
        await store.SaveTodoAsync(first, loaded);
        PortableTodoDocument saved = await store.LoadTodoAsync(first);
        Require(saved.FontSize == 21 && saved.Placement?.XDip == 33 &&
                saved.Tasks.Select(task => task.Id).SequenceEqual([secondId, firstId]) &&
                saved.Tasks[0].Text == "修改后的第二项",
            "待办保存重载没有保持顺序、字号、位置或文字。");

        string moving = await store.CreateTodoAsync(files, "移动检查", new PortableTodoDocument());
        PortableTodoDocument movingDocument = await store.LoadTodoAsync(moving);
        string moved = Path.Combine(files, "已移动.tucktodo");
        File.Move(moving, moved);
        try
        {
            await store.SaveTodoAsync(moving, movingDocument);
            throw new InvalidOperationException("旧路径保存未拒绝。 ");
        }
        catch (FileNotFoundException)
        {
        }
        Require(!File.Exists(moving) && File.Exists(moved), "旧路径保存重新创建了已移动文件。");

        string validJson = await File.ReadAllTextAsync(second);
        string invalid = Path.Combine(files, "invalid.tucktodo");
        var invalidDocuments = new List<(string Json, string Message)>
        {
            ("{broken", "损坏 JSON 未拒绝。"),
            (Mutate(validJson, rootNode => rootNode.Remove("placement")), "缺失根字段未拒绝。"),
            (Mutate(validJson, rootNode => rootNode["extra"] = true), "额外根字段未拒绝。"),
            (Mutate(validJson, rootNode => rootNode["format"] = "Other"), "错误格式未拒绝。"),
            (Mutate(validJson, rootNode => rootNode["version"] = 2), "错误版本未拒绝。"),
            (Mutate(validJson, rootNode => rootNode["theme"] = 999), "非法主题未拒绝。"),
            (Mutate(validJson, rootNode => rootNode["fontSize"] = 49), "非法字号未拒绝。"),
            (Mutate(validJson, rootNode => rootNode["placement"]!["widthDip"] = 279), "非法位置未拒绝。"),
            (Mutate(validJson, rootNode => rootNode["tasks"]![1]!["id"] = rootNode["tasks"]![0]!["id"]!.GetValue<string>()), "重复 ID 未拒绝。"),
            (Mutate(validJson, rootNode => rootNode["tasks"]![0]!["text"] = "  "), "空白任务未拒绝。"),
            (Mutate(validJson, rootNode => rootNode["tasks"]![0]!["extra"] = true), "额外任务字段未拒绝。"),
            (Mutate(validJson, rootNode => rootNode["tasks"]![0]!.AsObject().Remove("done")), "缺失任务字段未拒绝。"),
            (Mutate(validJson, rootNode =>
            {
                rootNode["tasks"]![0]!["done"] = true;
                rootNode["tasks"]![0]!["completedAtUtc"] = "2026-09-01T12:00:00+08:00";
            }), "非 UTC 完成时间未拒绝。")
        };
        foreach ((string json, string message) in invalidDocuments)
            await RequireRejectedAsync(store, invalid, json, message);

        byte[] validBefore = await File.ReadAllBytesAsync(second);
        PortableTodoDocument invalidSave = await store.LoadTodoAsync(second);
        invalidSave.Tasks[0].Text = "  invalid  ";
        try
        {
            await store.SaveTodoAsync(second, invalidSave);
            throw new InvalidOperationException("非法文档保存未拒绝。");
        }
        catch (InvalidDataException)
        {
        }
        Require((await File.ReadAllBytesAsync(second)).SequenceEqual(validBefore),
            "非法待办保存改写了原文件。");

        var rules = new PortableTodoDocument();
        PortableTodoTask normalized = TodoRules.Add(rules, "  先做\n  这件事  ");
        PortableTodoTask other = TodoRules.Add(rules, "另一项");
        Require(!normalized.Done && normalized.CompletedAtUtc is null &&
                normalized.Text == "先做 这件事" && TodoRules.UpdateText(normalized, "  已修改  ") &&
                normalized.Text == "已修改" && TodoRules.Move(rules, other.Id, 0) && rules.Tasks[0] == other,
            "待办新增、编辑或显式排序规则错误。");
        DateTimeOffset completedAt = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        TodoRules.SetDone(other, done: true, completedAt);
        Require(TodoRules.RemoveExpired(rules, completedAt.AddMilliseconds(2999)) == 0 &&
                TodoRules.GetOpacity(other, completedAt.AddMilliseconds(2999)) is > 0 and < 1,
            "三秒删除前边界或渐隐错误。");
        TodoRules.SetDone(other, done: false, completedAt.AddMilliseconds(2999));
        Require(!other.Done && other.CompletedAtUtc is null && rules.Tasks.Contains(other), "三秒内撤销失败。");
        TodoRules.SetDone(other, done: true, completedAt);
        Require(TodoRules.RemoveExpired(rules, completedAt.AddSeconds(3)) == 1 && !rules.Tasks.Contains(other),
            "三秒删除闭边界错误。");

        string themeRoot = Path.Combine(root, "theme");
        Directory.CreateDirectory(themeRoot);
        string notePath = await store.CreatePortableAsync(themeRoot, "note", new PortableNoteDocument
        {
            Theme = NoteTheme.SunYellow,
            FontSize = 19,
            ShowRuledLines = true,
            Html = "<p>正文保持</p>"
        });
        string todoPath = await store.CreateTodoAsync(themeRoot, "todo", new PortableTodoDocument
        {
            Theme = NoteTheme.SunYellow,
            FontSize = 18,
            Tasks = [new PortableTodoTask { Text = "任务保持" }]
        });
        _ = await store.ApplyThemeToTopLevelPortableFilesAsync(themeRoot, NoteTheme.Graphite);
        _ = await store.ApplyThemeToTopLevelTodoFilesAsync(themeRoot, NoteTheme.Graphite);
        PortableNoteDocument themedNote = await store.LoadPortableAsync(notePath);
        PortableTodoDocument themedTodo = await store.LoadTodoAsync(todoPath);
        Require(themedNote.Theme == NoteTheme.Graphite && themedNote.Html == "<p>正文保持</p>" &&
                themedNote.FontSize == 19 && themedNote.ShowRuledLines &&
                themedTodo.Theme == NoteTheme.Graphite && themedTodo.FontSize == 18 &&
                themedTodo.Tasks.Single().Text == "任务保持",
            "统一主题同步改写了便签正文或待办任务。");

        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        Require(File.Exists(Path.Combine(assets, "Todo.png")) && File.Exists(Path.Combine(assets, "Todo.ico")),
            "Todo 资产未进入输出目录。");

        string todoWindowXamlPath = Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", "TodoWindow.xaml");
        string todoWindowCodePath = Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", "TodoWindow.xaml.cs");
        string todoWindowXaml = await File.ReadAllTextAsync(todoWindowXamlPath);
        string todoWindowCode = await File.ReadAllTextAsync(todoWindowCodePath);
        XDocument todoWindow = XDocument.Parse(todoWindowXaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement newTaskBox = todoWindow.Descendants(presentation + "TextBox")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "NewTaskBox");
        XElement taskText = todoWindow.Descendants(presentation + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "{Binding Text}");
        XElement taskEditor = todoWindow.Descendants(presentation + "TextBox")
            .Single(element => (string?)element.Attribute("Text") == "{Binding EditText, Mode=TwoWay}");
        XElement taskCheckBox = todoWindow.Descendants(presentation + "CheckBox")
            .Single(element => (string?)element.Attribute("Click") == "TaskCheckBox_Click");
        XElement taskList = todoWindow.Descendants(presentation + "ListView")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "TaskList");
        Require(todoWindow.Descendants(presentation + "RowDefinition").Count() == 3 &&
                (string?)newTaskBox.Attribute("Grid.Row") == "1" &&
                (string?)taskList.Attribute("Grid.Row") == "2" &&
                !todoWindowXaml.Contains("RemainingText", StringComparison.Ordinal) &&
                !todoWindowXaml.Contains("FontSizeText", StringComparison.Ordinal),
            "待办顶部数量/字号信息行未完整移除。");
        Require((string?)newTaskBox.Attribute("TextAlignment") == "Left" &&
                newTaskBox.Attribute("PlaceholderText") is null &&
                (string?)taskText.Attribute("HorizontalAlignment") == "Stretch" &&
                (string?)taskText.Attribute("TextAlignment") == "Left" &&
                (string?)taskText.Attribute("TextWrapping") == "WrapWholeWords" &&
                (string?)taskEditor.Attribute("HorizontalAlignment") == "Stretch" &&
                (string?)taskEditor.Attribute("TextAlignment") == "Left" &&
                !todoWindowCode.Contains(".PlaceholderText", StringComparison.Ordinal) &&
                !todoWindowCode.Contains("RemainingText", StringComparison.Ordinal) &&
                !todoWindowCode.Contains("FontSizeText", StringComparison.Ordinal) &&
                !todoWindowCode.Contains("RefreshSummary", StringComparison.Ordinal) &&
                todoWindowCode.Contains(
                    "AutomationProperties.SetName(NewTaskBox, AppStrings.Get(\"TodoAddPlaceholder\"))",
                    StringComparison.Ordinal),
            "待办输入提示、左对齐或无障碍名称不符合要求。");
        Require((string?)taskCheckBox.Attribute("IsChecked") == "False" &&
                (string?)taskCheckBox.Attribute("DataContextChanged") == "TaskCheckBox_DataContextChanged" &&
                (string?)taskCheckBox.Attribute("Width") == "20" &&
                (string?)taskCheckBox.Attribute("MinWidth") == "0" &&
                (string?)taskCheckBox.Attribute("Padding") == "0" &&
                todoWindowCode.Contains("SetTaskCheckBoxState(box, row.Done)", StringComparison.Ordinal) &&
                todoWindowCode.Contains("if (_syncingTaskCheckBox ||", StringComparison.Ordinal) &&
                !todoWindowCode.Contains("CheckedState", StringComparison.Ordinal),
            "待办复选框未按模型显式同步，或默认最小宽度仍会挤压正文。");
        Require(todoWindowCode.Contains("WindowRoot.RequestedTheme = colors.TextColor.R > 128", StringComparison.Ordinal) &&
                todoWindowCode.Contains("TextControlBackgroundFocused", StringComparison.Ordinal) &&
                todoWindowCode.Contains("TextControlForegroundFocused", StringComparison.Ordinal) &&
                todoWindowCode.Contains("TextControlBorderBrushFocused", StringComparison.Ordinal),
            "待办窗口未按配色切换控件主题或覆盖输入框焦点资源。");

        foreach (NoteThemeColors colors in NoteThemePalette.All)
        {
            Require(ContrastRatio(colors.TextColor, colors.EditorColor) >= 4.5 &&
                    ContrastRatio(colors.TextColor, colors.SurfaceColor) >= 4.5 &&
                    ContrastRatio(colors.MutedColor, colors.EditorColor) >= 4.5 &&
                    ContrastRatio(colors.AccentColor, colors.SurfaceColor) >= 3 &&
                    ContrastRatio(colors.BorderColor, colors.EditorColor) >= 3,
                $"待办主题 {colors.Theme} 的文字、强调色或边框对比度不足。");
        }

        string appSource = await File.ReadAllTextAsync(
            Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", "App.xaml.cs"));
        int todoFilter = appSource.IndexOf(
            "EndsWith(\".tucktodo\", StringComparison.OrdinalIgnoreCase)",
            StringComparison.Ordinal);
        int absolutePath = todoFilter < 0
            ? -1
            : appSource.IndexOf(".Select(Path.GetFullPath)", todoFilter, StringComparison.Ordinal);
        int todoOpen = absolutePath < 0
            ? -1
            : appSource.IndexOf("OpenExternalTodoAsync(path)", absolutePath, StringComparison.Ordinal);
        Require(todoFilter >= 0 && absolutePath > todoFilter && todoOpen > absolutePath,
            "桌面绝对 .tucktodo 路径未复用现有待办打开链路。");

        string installer = await File.ReadAllTextAsync(Path.Combine(Environment.CurrentDirectory, "installer", "TuckPane.iss"));
        Require(installer.Contains("Software\\Classes\\.tucktodo", StringComparison.Ordinal) &&
                installer.Contains("TuckPane.Todo", StringComparison.Ordinal) &&
                installer.Contains("Assets\\Todo.ico,0", StringComparison.Ordinal) &&
                installer.Contains("\"\"\"{app}\\TuckPane.exe\"\" \"\"%1\"\"\"", StringComparison.Ordinal),
            "安装器缺少 .tucktodo ProgID、图标或带引号打开命令。");

        Console.WriteLine("PASS: portable todo");
    }
    finally
    {
        Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", null);
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--unified-compact-scale"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-unified-compact-scale-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var defaults = new GlobalSettings();
        Require(!defaults.UseUniformFloatingCompactScale && !defaults.UseUniformPositionedCompactScale &&
                defaults.UniformFloatingCompactScale == OrganizerLimits.DefaultCompactScale &&
                defaults.UniformPositionedCompactScale == OrganizerLimits.DefaultCompactScale,
            "统一入口大小的默认开关或默认值错误。");

        string legacyPath = Path.Combine(root, "legacy.json");
        await File.WriteAllTextAsync(legacyPath,
            """{"SchemaVersion":6,"GlobalSettings":{},"Organizers":[{"Name":"悬浮","PlacementMode":0,"CompactScale":2.2},{"Name":"定位","PlacementMode":1,"CompactScale":1.7}]}""");
        AppStateV2 legacy = await new StateStore(legacyPath).LoadAsync();
        Require(legacy.SchemaVersion == 8 &&
                !legacy.GlobalSettings.UseUniformFloatingCompactScale &&
                !legacy.GlobalSettings.UseUniformPositionedCompactScale &&
                legacy.Organizers[0].CompactScale == 2.2 && legacy.Organizers[1].CompactScale == 1.7,
            "旧 Schema 6 状态缺少新字段时不应改变已有窗口大小。");

        AppStateV2 floatingOnly = StateStore.Normalize(new AppStateV2
        {
            GlobalSettings = new GlobalSettings
            {
                UseUniformFloatingCompactScale = true,
                UniformFloatingCompactScale = 2.25,
                UniformPositionedCompactScale = 1.65
            },
            Organizers =
            [
                new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Floating, CompactScale = 1.4 },
                new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Positioned, CompactScale = 1.7 },
                new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Station, CompactScale = 2.4 }
            ]
        });
        Require(floatingOnly.Organizers[0].CompactScale == 2.25 &&
                floatingOnly.Organizers[1].CompactScale == 1.7 &&
                floatingOnly.Organizers[2].CompactScale == 2.4,
            "悬浮约束影响了定位或 Station，或没有覆盖悬浮窗口。");

        AppStateV2 both = StateStore.Normalize(new AppStateV2
        {
            GlobalSettings = new GlobalSettings
            {
                UseUniformFloatingCompactScale = true,
                UniformFloatingCompactScale = 4,
                UseUniformPositionedCompactScale = true,
                UniformPositionedCompactScale = .5
            },
            Organizers =
            [
                new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Floating, CompactScale = 1.4 },
                new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Positioned, CompactScale = 1.7 },
                new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Station, CompactScale = 2.4 }
            ]
        });
        Require(both.GlobalSettings.UniformFloatingCompactScale == 3 &&
                both.GlobalSettings.UniformPositionedCompactScale == 1.2 &&
                both.Organizers[0].CompactScale == 3 && both.Organizers[1].CompactScale == 1.2 &&
                both.Organizers[2].CompactScale == 2.4,
            "两个统一值没有按各自范围归一化，或 Station 被约束。");

        both.GlobalSettings.UseUniformFloatingCompactScale = false;
        both.GlobalSettings.UseUniformPositionedCompactScale = false;
        StateStore.Normalize(both);
        Require(both.Organizers[0].CompactScale == 3 && both.Organizers[1].CompactScale == 1.2,
            "关闭统一约束后不应恢复未知的历史大小。");

        var resolved = new GlobalSettings
        {
            UseUniformFloatingCompactScale = true,
            UniformFloatingCompactScale = 2.1,
            UseUniformPositionedCompactScale = true,
            UniformPositionedCompactScale = 1.7
        };
        Require(resolved.ResolveCompactScale(OrganizerPlacementMode.Floating, 1.3) == 2.1 &&
                resolved.ResolveCompactScale(OrganizerPlacementMode.Positioned, 1.3) == 1.7 &&
                resolved.ResolveCompactScale(OrganizerPlacementMode.Station, 2.2) == 2.2,
            "创建和模式切换共用的有效大小规则错误。");

        string roundTripPath = Path.Combine(root, "round-trip.json");
        var store = new StateStore(roundTripPath);
        both.GlobalSettings.UseUniformFloatingCompactScale = true;
        both.GlobalSettings.UniformFloatingCompactScale = 2.05;
        both.GlobalSettings.UseUniformPositionedCompactScale = true;
        both.GlobalSettings.UniformPositionedCompactScale = 1.55;
        await store.SaveAsync(both);
        AppStateV2 reloaded = await store.LoadAsync();
        Require(reloaded.SchemaVersion == 8 && reloaded.GlobalSettings.UseUniformFloatingCompactScale &&
                reloaded.GlobalSettings.UniformFloatingCompactScale == 2.05 &&
                reloaded.GlobalSettings.UseUniformPositionedCompactScale &&
                reloaded.GlobalSettings.UniformPositionedCompactScale == 1.55 &&
                reloaded.Organizers[0].CompactScale == 2.05 && reloaded.Organizers[1].CompactScale == 1.55,
            "统一入口大小保存重载失败或错误升级了 Schema。");

        Console.WriteLine("PASS: unified compact scale");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--window-alignment"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static NativeMethods.RECT Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-window-alignment-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        Require(!new GlobalSettings().WindowAlignmentEnabled, "窗口拖动对齐必须默认关闭。");
        string missingPath = Path.Combine(root, "missing.json");
        await File.WriteAllTextAsync(missingPath,
            """{"SchemaVersion":6,"GlobalSettings":{},"Organizers":[]}""");
        Require(!(await new StateStore(missingPath).LoadAsync()).GlobalSettings.WindowAlignmentEnabled,
            "缺失的窗口拖动对齐字段没有回退为关闭。");

        string enabledPath = Path.Combine(root, "enabled.json");
        var enabledStore = new StateStore(enabledPath);
        await enabledStore.SaveAsync(new AppStateV2
        {
            GlobalSettings = new GlobalSettings { WindowAlignmentEnabled = true }
        });
        AppStateV2 enabled = await enabledStore.LoadAsync();
        Require(enabled.SchemaVersion == 8 && enabled.GlobalSettings.WindowAlignmentEnabled,
            "窗口拖动对齐保存重载失败。");

        NativeMethods.RECT work = Rect(0, 0, 1000, 800);
        WindowAlignmentResult screenLeft = WindowAlignmentMath.Align(
            Rect(9, 220, 129, 310), work, [], 12, 20, default);
        WindowAlignmentResult screenRight = WindowAlignmentMath.Align(
            Rect(873, 220, 993, 310), work, [], 12, 20, default);
        WindowAlignmentResult screenTop = WindowAlignmentMath.Align(
            Rect(220, 10, 340, 90), work, [], 12, 20, default);
        WindowAlignmentResult screenBottom = WindowAlignmentMath.Align(
            Rect(220, 713, 340, 793), work, [], 12, 20, default);
        Require(screenLeft.Bounds.Left == 0 && screenRight.Bounds.Right == 1000 &&
                screenTop.Bounds.Top == 0 && screenBottom.Bounds.Bottom == 800,
            "工作区四条边没有在 12px 内吸附。");

        WindowAlignmentResult screenCenter = WindowAlignmentMath.Align(
            Rect(455, 347, 555, 447), work, [], 12, 20, default);
        Require(screenCenter.Bounds.Left == 455 && screenCenter.Bounds.Top == 347 &&
                screenCenter.Bounds.Right == 555 && screenCenter.Bounds.Bottom == 447 &&
                screenCenter.State.X is null && screenCenter.State.Y is null,
            "工作区中心线不应再吸附。");

        Guid peerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        WindowAlignmentTarget[] peers = [new(peerId, Rect(600, 100, 780, 260))];
        WindowAlignmentResult leftToLeft = WindowAlignmentMath.Align(
            Rect(609, 500, 729, 580), work, peers, 12, 20, default);
        WindowAlignmentResult rightToRight = WindowAlignmentMath.Align(
            Rect(653, 420, 773, 500), work, peers, 12, 20, default);
        WindowAlignmentResult topToTop = WindowAlignmentMath.Align(
            Rect(200, 109, 320, 189), work, peers, 12, 20, default);
        WindowAlignmentResult bottomToBottom = WindowAlignmentMath.Align(
            Rect(200, 173, 320, 253), work, peers, 12, 20, default);
        Require(leftToLeft.Bounds.Left == 600 && rightToRight.Bounds.Right == 780 &&
                topToTop.Bounds.Top == 100 && bottomToBottom.Bounds.Bottom == 260,
            "不同宽高窗口没有按左/右/上/下同名边对齐。");
        Require(leftToLeft.XGuide == new WindowAlignmentGuide(true, 600, 100, 580),
            "相距较远的同名边对齐线没有连接两个窗口的范围。");

        WindowAlignmentResult peerCenter = WindowAlignmentMath.Align(
            Rect(630, 400, 750, 480), work, peers, 12, 20, default);
        WindowAlignmentResult leftToRight = WindowAlignmentMath.Align(
            Rect(787, 400, 907, 480), work, peers, 12, 20, default);
        WindowAlignmentResult rightToLeft = WindowAlignmentMath.Align(
            Rect(473, 400, 593, 480), work, peers, 12, 20, default);
        WindowAlignmentResult topToBottom = WindowAlignmentMath.Align(
            Rect(300, 267, 420, 347), work, peers, 12, 20, default);
        WindowAlignmentResult bottomToTop = WindowAlignmentMath.Align(
            Rect(300, 13, 420, 93), work, peers, 12, 20, default);
        Require(peerCenter.State.X is null && leftToRight.State.X is null && rightToLeft.State.X is null &&
                topToBottom.State.Y is null && bottomToTop.State.Y is null,
            "窗口中心或相邻异名边不应再吸附。");

        WindowAlignmentResult doubleAxis = WindowAlignmentMath.Align(
            Rect(609, 109, 729, 189), work, peers, 12, 20, default);
        Require(doubleAxis.Bounds.Left == 600 && doubleAxis.Bounds.Top == 100 &&
                doubleAxis.State.X is not null && doubleAxis.State.Y is not null,
            "同名边双轴同时吸附错误。");

        WindowAlignmentResult outsideTrigger = WindowAlignmentMath.Align(
            Rect(613, 500, 733, 580), work, peers, 12, 20, default);
        Require(outsideTrigger.Bounds.Left == 613 && outsideTrigger.State.X is null,
            "超过 12px 的目标错误触发吸附。");
        WindowAlignmentResult retained = WindowAlignmentMath.Align(
            Rect(613, 500, 733, 580), work, peers, 12, 20, leftToLeft.State);
        Require(retained.Bounds.Left == 600 && retained.State.X is not null,
            "已吸附轴没有在 20px 脱离阈值内保持锁定。");
        WindowAlignmentResult released = WindowAlignmentMath.Align(
            Rect(621, 500, 741, 580), work, peers, 12, 20, leftToLeft.State);
        Require(released.Bounds.Left == 621 && released.State.X is null,
            "超过 20px 后吸附轴没有解除。");

        WindowAlignmentTarget[] screenTie = [new(Guid.Empty, Rect(0, 500, 100, 600))];
        WindowAlignmentResult tie = WindowAlignmentMath.Align(
            Rect(8, 220, 108, 320), work, screenTie, 12, 20, default);
        Require(tie.State.X?.TargetKind == WindowAlignmentTargetKind.Screen,
            "完全同距时没有优先选择稳定的屏幕基准。");

        Guid laterPeerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        WindowAlignmentResult peerTie = WindowAlignmentMath.Align(
            Rect(609, 500, 729, 580), work,
            [new(laterPeerId, Rect(600, 100, 780, 260)), new(peerId, Rect(600, 100, 780, 260))],
            12, 20, default);
        Require(peerTie.State.X?.TargetId == peerId, "同距目标窗没有按窗口 ID 确定性选择。");

        WindowAlignmentResult anchorTie = WindowAlignmentMath.Align(
            Rect(609, 500, 709, 580), work,
            [new(peerId, Rect(600, 100, 718, 260))], 12, 20, default);
        Require(anchorTie.State.X?.MovingAnchor == WindowAlignmentAnchor.Start && anchorTie.Bounds.Left == 600,
            "完全同距时没有优先选择左/上边。");

        int snap144 = WindowAlignmentMath.DipToPx(WindowAlignmentMath.SnapDistanceDip, 144);
        int release144 = WindowAlignmentMath.DipToPx(WindowAlignmentMath.ReleaseDistanceDip, 144);
        Require(snap144 == 18 && release144 == 30, "非 100% DPI 的吸附距离换算错误。");
        NativeMethods.RECT secondaryWork = Rect(-1920, 0, 0, 1040);
        WindowAlignmentResult negativeDisplay = WindowAlignmentMath.Align(
            Rect(-1903, 200, -1803, 300), secondaryWork, [], snap144, release144, default);
        Require(negativeDisplay.Bounds.Left == -1920, "负坐标副屏边缘没有按 DPI 阈值吸附。");
        WindowAlignmentResult switchedDisplay = WindowAlignmentMath.Align(
            Rect(-1500, 400, -1400, 500), secondaryWork, [], snap144, release144, leftToLeft.State);
        Require(switchedDisplay.State.X is null && switchedDisplay.State.Y is null,
            "切换显示器并移除旧目标后仍保留旧轴锁定。");

        NativeMethods.RECT largeWindow = Rect(100, 100, 290, 266);
        NativeMethods.RECT largeFrame = Rect(146, 100, 243, 197);
        WindowAlignmentInsets largeInsets = WindowAlignmentInsets.From(largeWindow, largeFrame);
        NativeMethods.RECT smallWindow = Rect(127, 500, 241, 600);
        WindowAlignmentInsets smallInsets = WindowAlignmentInsets.From(
            Rect(400, 300, 514, 400),
            Rect(428, 300, 487, 359));
        WindowAlignmentTarget[] differentScaleTarget = [new(peerId, largeInsets.ToFrame(largeWindow))];
        WindowAlignmentResult differentScaleLeft = WindowAlignmentMath.Align(
            smallInsets.ToFrame(smallWindow), work,
            differentScaleTarget, 12, 20, default);
        WindowAlignmentResult differentScaleRight = WindowAlignmentMath.Align(
            smallInsets.ToFrame(Rect(148, 500, 262, 600)), work,
            differentScaleTarget, 12, 20, default);
        WindowAlignmentResult differentScaleTop = WindowAlignmentMath.Align(
            smallInsets.ToFrame(Rect(500, 109, 614, 209)), work,
            differentScaleTarget, 12, 20, default);
        WindowAlignmentResult differentScaleBottom = WindowAlignmentMath.Align(
            smallInsets.ToFrame(Rect(500, 130, 614, 230)), work,
            differentScaleTarget, 12, 20, default);
        NativeMethods.RECT alignedLeftWindow = smallInsets.ToWindow(differentScaleLeft.Bounds);
        Require(smallInsets.ToFrame(alignedLeftWindow).Left == largeFrame.Left &&
                smallInsets.ToFrame(smallInsets.ToWindow(differentScaleRight.Bounds)).Right == largeFrame.Right &&
                smallInsets.ToFrame(smallInsets.ToWindow(differentScaleTop.Bounds)).Top == largeFrame.Top &&
                smallInsets.ToFrame(smallInsets.ToWindow(differentScaleBottom.Bounds)).Bottom == largeFrame.Bottom &&
                alignedLeftWindow.Left != largeWindow.Left,
            "不同缩放入口错误地按整个窗口外框而不是白色圆角框的四条边对齐。");

        NativeMethods.RECT clamped = WindowAlignmentMath.ClampFrame(
            Rect(-1950, -10, -1836, 90), secondaryWork, smallInsets);
        Require(smallInsets.ToFrame(clamped).Left == secondaryWork.Left &&
                smallInsets.ToFrame(clamped).Top == secondaryWork.Top,
            "白色圆角框没有保持贴合工作区边缘。");

        Console.WriteLine("PASS: window alignment");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--aug29-shell-hover"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-aug29-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        string folderPath = Path.Combine(root, "中文 空格目录");
        Directory.CreateDirectory(folderPath);
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法取得测试进程路径。");
        string[] parsed = TuckPane.App.ParseRedirectedArguments(
            $"\"{executable}\" --create-organizer-in \"{folderPath}\"");
        Require(parsed is ["--create-organizer-in", var parsedPath] && parsedPath == folderPath,
            "--create-organizer-in 没有保留中文及空格绝对路径为独立参数。");

        var defaults = new GlobalSettings();
        Require(defaults.HoverExpandDelayMs == 350 && defaults.PointerLeaveCollapseDelayMs == 400 &&
                defaults.StationPointerLeaveCollapseDelayMs == 400,
            "悬浮展开、普通离开收缩或中转站收缩的默认延迟错误。");
        Require(GlobalSettings.MinimumHoverDelayMs == 100 && GlobalSettings.MaximumHoverDelayMs == 2000 &&
                GlobalSettings.HoverDelayStepMs == 50 &&
                GlobalSettings.NormalizeHoverDelayMs(124) == 100 &&
                GlobalSettings.NormalizeHoverDelayMs(125) == 150,
            "悬浮延迟的范围、步进或半步取整规则错误。");

        AppStateV2 normalized = StateStore.Normalize(new AppStateV2
        {
            GlobalSettings = new GlobalSettings
            {
                HoverExpandDelayMs = 99,
                PointerLeaveCollapseDelayMs = 2001,
                StationPointerLeaveCollapseDelayMs = 124
            }
        });
        Require(normalized.GlobalSettings.HoverExpandDelayMs == 100 &&
                normalized.GlobalSettings.PointerLeaveCollapseDelayMs == 2000 &&
                normalized.GlobalSettings.StationPointerLeaveCollapseDelayMs == 100,
            "StateStore 没有把三项悬浮延迟限制在 100–2000ms。");

        string statePath = Path.Combine(root, "round-trip.json");
        var store = new StateStore(statePath);
        await store.SaveAsync(new AppStateV2
        {
            GlobalSettings = new GlobalSettings
            {
                HoverExpandDelayMs = 650,
                PointerLeaveCollapseDelayMs = 1150,
                StationPointerLeaveCollapseDelayMs = 1750
            }
        });
        AppStateV2 reloaded = await store.LoadAsync();
        Require(reloaded.GlobalSettings.HoverExpandDelayMs == 650 &&
                reloaded.GlobalSettings.PointerLeaveCollapseDelayMs == 1150 &&
                reloaded.GlobalSettings.StationPointerLeaveCollapseDelayMs == 1750,
            "StateStore 保存重载后没有保留三项独立延迟。");

        string legacyPath = Path.Combine(root, "legacy-center.json");
        await File.WriteAllTextAsync(legacyPath,
            """{"SchemaVersion":6,"GlobalSettings":{"CollapseToCenter":true},"Organizers":[]}""");
        var legacyStore = new StateStore(legacyPath);
        AppStateV2 legacy = await legacyStore.LoadAsync();
        Require(legacy.GlobalSettings.HoverExpandDelayMs == 350 &&
                legacy.GlobalSettings.PointerLeaveCollapseDelayMs == 400 &&
                legacy.GlobalSettings.StationPointerLeaveCollapseDelayMs == 400,
            "旧状态中的未知字段影响了新延迟默认值。");
        await legacyStore.SaveAsync(legacy);
        string savedLegacy = await File.ReadAllTextAsync(legacyPath);
        Require(!savedLegacy.Contains("CollapseToCenter", StringComparison.Ordinal),
            "旧 CollapseToCenter 字段在正常保存后仍然存在。");

        Console.WriteLine("PASS: aug29 shell and hover settings");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--aug28-organizer-behavior"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    var work = new NativeMethods.RECT { Left = 100, Top = 200, Right = 1100, Bottom = 1000 };
    var display = new DisplayInfo("safe-region", work, work, 1);
    foreach ((OrganizerDockEdge edge, NativeMethods.RECT bounds, NativeMethods.POINT inside,
        NativeMethods.POINT hot, NativeMethods.POINT connector, NativeMethods.POINT outside) in new[]
    {
        (OrganizerDockEdge.Left,
            new NativeMethods.RECT { Left = 130, Top = 400, Right = 430, Bottom = 700 },
            new NativeMethods.POINT { X = 200, Y = 500 }, new NativeMethods.POINT { X = 101, Y = 250 },
            new NativeMethods.POINT { X = 110, Y = 500 }, new NativeMethods.POINT { X = 700, Y = 500 }),
        (OrganizerDockEdge.Top,
            new NativeMethods.RECT { Left = 400, Top = 230, Right = 700, Bottom = 530 },
            new NativeMethods.POINT { X = 500, Y = 300 }, new NativeMethods.POINT { X = 200, Y = 201 },
            new NativeMethods.POINT { X = 500, Y = 215 }, new NativeMethods.POINT { X = 500, Y = 800 }),
        (OrganizerDockEdge.Right,
            new NativeMethods.RECT { Left = 770, Top = 400, Right = 1070, Bottom = 700 },
            new NativeMethods.POINT { X = 900, Y = 500 }, new NativeMethods.POINT { X = 1099, Y = 250 },
            new NativeMethods.POINT { X = 1080, Y = 500 }, new NativeMethods.POINT { X = 500, Y = 500 }),
        (OrganizerDockEdge.Bottom,
            new NativeMethods.RECT { Left = 400, Top = 670, Right = 700, Bottom = 970 },
            new NativeMethods.POINT { X = 500, Y = 800 }, new NativeMethods.POINT { X = 200, Y = 999 },
            new NativeMethods.POINT { X = 500, Y = 980 }, new NativeMethods.POINT { X = 900, Y = 500 })
    })
    {
        Require(DisplayPlacementService.IsStationExpandedSafeRegion(
                inside,
                display,
                edge,
                bounds,
                GlobalSettings.DefaultStationActivationDistanceDip),
            $"{edge} 展开窗口内部不在安全区。");
        Require(DisplayPlacementService.IsStationExpandedSafeRegion(
                hot,
                display,
                edge,
                bounds,
                GlobalSettings.DefaultStationActivationDistanceDip),
            $"{edge} 整条边缘热区不在安全区。");
        Require(DisplayPlacementService.IsStationExpandedSafeRegion(
                connector,
                display,
                edge,
                bounds,
                GlobalSettings.DefaultStationActivationDistanceDip),
            $"{edge} 热区与窗口之间的连接区域不在安全区。");
        Require(!DisplayPlacementService.IsStationExpandedSafeRegion(
                outside,
                display,
                edge,
                bounds,
                GlobalSettings.DefaultStationActivationDistanceDip),
            $"{edge} 真正离开后仍被误判为安全区。");
    }

    var secondary = new DisplayInfo(
        "secondary",
        new NativeMethods.RECT { Left = -1600, Top = 100, Right = -400, Bottom = 900 },
        new NativeMethods.RECT { Left = -1580, Top = 120, Right = -420, Bottom = 880 },
        1.25);
    NativeMethods.RECT available = DisplayPlacementService.FindAvailable(secondary, [], 240, 160);
    Require(available.Left >= secondary.Work.Left && available.Top >= secondary.Work.Top &&
            available.Right <= secondary.Work.Right && available.Bottom <= secondary.Work.Bottom,
        "默认收纳窗没有落在指定显示器工作区内。");

    Console.WriteLine("PASS: aug28 organizer behavior");
    return;
}

if (args is ["--station-hot-zone"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-station-hot-zone-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        string legacyPath = Path.Combine(root, "legacy.json");
        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 8,
            GlobalSettings = new { },
            Organizers = Array.Empty<object>()
        }));
        AppStateV2 legacy = await new StateStore(legacyPath).LoadAsync();
        Require(legacy.GlobalSettings.StationActivationDistanceDip == 16 &&
                legacy.GlobalSettings.StationHoverExpandDelayMs == 120,
            "旧状态没有获得 16 DIP / 120ms 的中转站触发默认值。");

        AppStateV2 normalized = StateStore.Normalize(new AppStateV2
        {
            GlobalSettings = new GlobalSettings
            {
                StationActivationDistanceDip = 51,
                StationHoverExpandDelayMs = 511
            }
        });
        Require(normalized.GlobalSettings.StationActivationDistanceDip == 48 &&
                normalized.GlobalSettings.StationHoverExpandDelayMs == 500 &&
                GlobalSettings.NormalizeStationActivationDistanceDip(-1) == 4 &&
                GlobalSettings.NormalizeStationHoverExpandDelayMs(-1) == 0 &&
                GlobalSettings.NormalizeStationActivationDistanceDip(18) == 20 &&
                GlobalSettings.NormalizeStationHoverExpandDelayMs(111) == 120,
            "中转站触发距离或等待时间没有按范围与步进归一化。");

        normalized.GlobalSettings.StationActivationDistanceDip = 20;
        normalized.GlobalSettings.StationHoverExpandDelayMs = 140;
        string roundTripPath = Path.Combine(root, "round-trip.json");
        var store = new StateStore(roundTripPath);
        await store.SaveAsync(normalized);
        AppStateV2 reloaded = await store.LoadAsync();
        Require(reloaded.GlobalSettings.StationActivationDistanceDip == 20 &&
                reloaded.GlobalSettings.StationHoverExpandDelayMs == 140,
            "中转站触发设置没有保存并重载。");

        var display = new DisplayInfo(
            "negative-scaled-display",
            new NativeMethods.RECT { Left = -1920, Top = -200, Right = 0, Bottom = 1080 },
            new NativeMethods.RECT { Left = -1920, Top = -200, Right = 0, Bottom = 1080 },
            1.5);
        const int distanceDip = 16;
        int centerX = (display.Monitor.Left + display.Monitor.Right) / 2;
        int centerY = (display.Monitor.Top + display.Monitor.Bottom) / 2;
        foreach (OrganizerDockEdge edge in Enum.GetValues<OrganizerDockEdge>())
        {
            NativeMethods.POINT inside = edge switch
            {
                OrganizerDockEdge.Left => new() { X = display.Monitor.Left + 23, Y = centerY },
                OrganizerDockEdge.Top => new() { X = centerX, Y = display.Monitor.Top + 23 },
                OrganizerDockEdge.Right => new() { X = display.Monitor.Right - 24, Y = centerY },
                _ => new() { X = centerX, Y = display.Monitor.Bottom - 24 }
            };
            NativeMethods.POINT cold = edge switch
            {
                OrganizerDockEdge.Left => inside with { X = display.Monitor.Left + 24 },
                OrganizerDockEdge.Top => inside with { Y = display.Monitor.Top + 24 },
                OrganizerDockEdge.Right => inside with { X = display.Monitor.Right - 25 },
                _ => inside with { Y = display.Monitor.Bottom - 25 }
            };
            NativeMethods.POINT fullEdge = edge is OrganizerDockEdge.Left or OrganizerDockEdge.Right
                ? inside with { Y = display.Monitor.Top }
                : inside with { X = display.Monitor.Right - 1 };
            NativeMethods.POINT outsideDisplay = edge switch
            {
                OrganizerDockEdge.Left => inside with { X = display.Monitor.Left - 1 },
                OrganizerDockEdge.Top => inside with { Y = display.Monitor.Top - 1 },
                OrganizerDockEdge.Right => inside with { X = display.Monitor.Right },
                _ => inside with { Y = display.Monitor.Bottom }
            };
            var expanded = new NativeMethods.RECT { Left = -700, Top = 100, Right = -100, Bottom = 700 };
            Require(DisplayPlacementService.IsStationHotZone(inside, display, edge, distanceDip) &&
                    DisplayPlacementService.IsStationHotZone(fullEdge, display, edge, distanceDip) &&
                    !DisplayPlacementService.IsStationHotZone(cold, display, edge, distanceDip) &&
                    !DisplayPlacementService.IsStationHotZone(outsideDisplay, display, edge, distanceDip) &&
                    DisplayPlacementService.IsStationExpandedSafeRegion(
                        inside,
                        display,
                        edge,
                        expanded,
                        distanceDip),
                $"{edge} 没有使用本站屏幕内整条 16 DIP（24px）热区。");
        }

        Require(!DisplayPlacementService.IsStationHotZone(
                new NativeMethods.POINT { X = 0, Y = centerY },
                display,
                OrganizerDockEdge.Right,
                distanceDip),
            "双屏拼接线的热区错误延伸到了相邻屏幕。");

        Console.WriteLine("PASS: station hot zone");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--theme-material-removal"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string sourceRoot = Path.Combine(Environment.CurrentDirectory, "src", "TuckPane");
    string model = File.ReadAllText(Path.Combine(sourceRoot, "Models", "AppState.cs"));
    string console = File.ReadAllText(Path.Combine(sourceRoot, "ConsoleWindow.xaml"));
    Require(!model.Contains("ThemeMaterial", StringComparison.Ordinal) &&
            !model.Contains("SettingsThemeMaterial", StringComparison.Ordinal) &&
            !console.Contains("ThemeMaterial", StringComparison.Ordinal),
        "材质枚举、持久化字段或设置入口仍然存在。");

    Console.WriteLine("PASS: theme material removal");
    return;
}

if (args is ["--theme-opacity-blur-arc"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void Near(double actual, double expected, string message)
    {
        Require(Math.Abs(actual - expected) < .0001, $"{message} 实际={actual:0.####}，期望={expected:0.####}。");
    }

    string sourceRoot = Path.Combine(Environment.CurrentDirectory, "src", "TuckPane");
    string modelSource = File.ReadAllText(Path.Combine(sourceRoot, "Models", "AppState.cs"));
    string paletteSource = File.ReadAllText(Path.Combine(sourceRoot, "Services", "ThemePalette.cs"));
    string surfaceSource = File.ReadAllText(Path.Combine(sourceRoot, "Services", "ThemeSurface.cs"));
    string consoleSource = File.ReadAllText(Path.Combine(sourceRoot, "ConsoleWindow.xaml.cs"));
    string mainSource = File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml.cs"));
    string mainXamlSource = File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml"));
    string consoleXamlSource = File.ReadAllText(Path.Combine(sourceRoot, "ConsoleWindow.xaml"));
    string edgePath = Path.Combine(sourceRoot, "Services", "ThemeEdgeSurface.cs");

    Require(modelSource.Contains("SchemaVersion { get; set; } = 15", StringComparison.Ordinal) &&
            modelSource.Contains("MaximumThemeTransparency = .99", StringComparison.Ordinal) &&
            modelSource.Contains("MaximumThemeBlurStrength = 2", StringComparison.Ordinal) &&
            modelSource.Contains("SolidColorMode", StringComparison.Ordinal),
        "主题状态未写回 Schema 15，或玻璃/纯色上限字段缺失。 ");
    Require(GlobalSettings.NormalizeThemeTransparency(-1) == 0 &&
            GlobalSettings.NormalizeThemeTransparency(.99) == .99 &&
            GlobalSettings.NormalizeThemeTransparency(1) == .99 &&
            GlobalSettings.NormalizeThemeBlurStrength(.01) == .05 &&
            GlobalSettings.NormalizeThemeBlurStrength(2) == 2,
        "玻璃不透明度或模糊强度端点归一化错误。 ");

    const uint color = 0xFF1A80E3;
    ThemeCompositionPlan transparent = ThemePalette.BuildCompositionPlan(
        new ThemeValues(color, 0, 1), useEffects: true);
    Require(!transparent.RequiresHostBackdrop && !transparent.UsesGaussianBlur &&
            transparent.HighlightOpacity == 0 && transparent.SurfaceOpacity == 0,
        "透明端点仍创建玻璃效果或高光。 ");
    ThemeCompositionPlan solid = ThemePalette.BuildCompositionPlan(
        new ThemeValues(color, .35, 1, SolidColorMode: true, SolidOpacity: .35), useEffects: true);
    Require(!solid.RequiresHostBackdrop && !solid.UsesGaussianBlur &&
            solid.DesktopOpacity == 0 && Math.Abs(solid.SurfaceOpacity - .35f) < .0001f &&
            Math.Abs(solid.TintOpacity - .35f) < .0001f &&
            solid.HighlightOpacity == 0,
        "纯色模式未旁路桌面、模糊或高光。 ");
    ThemeCompositionPlan glass = ThemePalette.BuildCompositionPlan(
        new ThemeValues(color, .5, 2), useEffects: true);
    Near(glass.BlurAmount, 20, "模糊 200% 未按比例放大");
    Near(glass.Saturation, 2, "模糊饱和度未在 100% 封顶");
    Near(glass.LuminosityOpacity, .06, "模糊明度未在 100% 封顶");
    Near(glass.HighlightOpacity, 1, "玻璃高光端点公式错误");

    Require(ThemePalette.GlassArcStops.Count >= 4 &&
            ThemePalette.GlassTextureStops.Count >= 6 &&
            ThemePalette.GlassArcStops.First().Color.A == 0 &&
            ThemePalette.GlassArcStops.Last().Color.A == 0 &&
            ThemePalette.GlassTextureStops.First().Color.A == 0 &&
            ThemePalette.GlassTextureStops.Last().Color.A == 0,
        "弧光/纹理渐变未保持透明端点。 ");
    Require(File.Exists(edgePath),
        "缺少独立 ThemeEdgeSurface 边缘渲染层，边缘不可由 ThemeSurface 兼任。 ");
    string edgeSource = File.ReadAllText(edgePath);
    Require((edgeSource.Contains("CompositionShapeVisual", StringComparison.Ordinal) ||
             edgeSource.Contains("ShapeVisual", StringComparison.Ordinal)) &&
            edgeSource.Contains("CompositionRoundedRectangleGeometry", StringComparison.Ordinal) &&
            edgeSource.Contains("CreateShapeVisual", StringComparison.Ordinal) &&
            edgeSource.Contains("CreateRoundedRectangleGeometry", StringComparison.Ordinal) &&
            edgeSource.Contains("CompositionBorderMode.Soft", StringComparison.Ordinal) &&
            edgeSource.Contains("StrokeBrush", StringComparison.Ordinal) &&
            edgeSource.Contains("StrokeThickness", StringComparison.Ordinal) &&
            edgeSource.Contains("CornerRadius", StringComparison.Ordinal) &&
            edgeSource.Contains("StrokeLineJoin.Round", StringComparison.Ordinal),
        "ThemeEdgeSurface 未使用双层圆角 ShapeVisual 描边，或缺少 Soft 抗锯齿。 ");
    Require(edgeSource.Contains("OrganizerGlassOuterEdgeStops", StringComparison.Ordinal) &&
            edgeSource.Contains("OrganizerGlassInnerEdgeStops", StringComparison.Ordinal) &&
            edgeSource.Contains("GlassEdgeHighlightStops", StringComparison.Ordinal) &&
            edgeSource.Contains("GlassEdgeTextureStops", StringComparison.Ordinal) &&
            !edgeSource.Contains("CreateHostBackdropBrush", StringComparison.Ordinal) &&
            !edgeSource.Contains("GaussianBlurEffect", StringComparison.Ordinal),
        "ThemeEdgeSurface 应使用固定中性边缘渐变，不能创建第二个 backdrop 或 Gaussian blur。 ");
    object? minimumEdgeOpacity = typeof(ThemePalette).GetField(
        "GlassEdgeMinimumOpacity",
        System.Reflection.BindingFlags.Static |
        System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
    Require(minimumEdgeOpacity is float minimum && minimum > 0 && minimum <= 1 &&
            paletteSource.Contains("GlassEdgeMinimumOpacity", StringComparison.Ordinal) &&
            edgeSource.Contains("GlassEdgeMinimumOpacity", StringComparison.Ordinal) &&
            edgeSource.Contains("_visual.Opacity = ThemePalette.GlassEdgeMinimumOpacity", StringComparison.Ordinal),
        "透明、纯色、零模糊和普通玻璃状态没有保留可见的中性边缘强度。 ");
    Require(edgeSource.Contains("RefreshGeometry", StringComparison.Ordinal) &&
            edgeSource.Contains("ActualWidth", StringComparison.Ordinal) &&
            edgeSource.Contains("ActualHeight", StringComparison.Ordinal) &&
            edgeSource.Contains("RasterizationScale", StringComparison.Ordinal) &&
            edgeSource.Contains("SizeChanged", StringComparison.Ordinal) &&
            edgeSource.Contains("Loaded", StringComparison.Ordinal) &&
            edgeSource.Contains("XamlRoot", StringComparison.Ordinal),
        "ThemeEdgeSurface 没有覆盖首次布局、尺寸变化或 DPI 变化后的几何刷新。 ");
    Require(edgeSource.Contains("Math.Max(0", StringComparison.Ordinal) &&
            edgeSource.Contains("Math.Min", StringComparison.Ordinal) &&
            edgeSource.Contains("ConfigureGeometry", StringComparison.Ordinal),
        "ThemeEdgeSurface 未钳制圆角/内缩几何，0×0 或极小尺寸可能产生负尺寸。 ");
    Require(!surfaceSource.Contains("GlassArcStops", StringComparison.Ordinal) &&
            !surfaceSource.Contains("GlassTextureStops", StringComparison.Ordinal) &&
            !surfaceSource.Contains("showPersistentGlassEdge", StringComparison.Ordinal) &&
            !surfaceSource.Contains("showArcGlow", StringComparison.Ordinal),
        "ThemeSurface 仍承担整面弧光、纹理或永久边缘职责，可能覆盖独立边缘层。 ");
    Require(!mainSource.Contains("showPersistentGlassEdge", StringComparison.Ordinal) &&
            !mainSource.Contains("showArcGlow", StringComparison.Ordinal) &&
            !consoleSource.Contains("showPersistentGlassEdge", StringComparison.Ordinal) &&
            !consoleSource.Contains("showArcGlow", StringComparison.Ordinal),
        "窗口接线仍通过旧 showArcGlow/showPersistentGlassEdge 参数控制边缘。 ");
    string compactEdgeCallSource = string.Concat(mainSource.Where(character => !char.IsWhiteSpace(character)));
    string consoleEdgeCallSource = string.Concat(consoleSource.Where(character => !char.IsWhiteSpace(character)));
    Require(compactEdgeCallSource.Contains("newThemeEdgeSurface(CompactEdgeOverlay", StringComparison.Ordinal) &&
            compactEdgeCallSource.Contains("newThemeEdgeSurface(ExpandedEdgeOverlay", StringComparison.Ordinal) &&
            consoleEdgeCallSource.Contains("newThemeEdgeSurface(SettingsEdgeOverlay", StringComparison.Ordinal),
        "收起态、展开态或设置页没有分别创建独立 ThemeEdgeSurface。 ");

    static XNamespace XamlNamespace() =>
        XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

    static XElement? FindNamedElement(XDocument document, string name)
    {
        XNamespace x = XamlNamespace();
        return document.Descendants().FirstOrDefault(element =>
            (string?)element.Attribute(x + "Name") == name);
    }

    static bool IsLastElement(XElement element) =>
        element.Parent is not null && element.Parent.Elements().LastOrDefault() == element;

    static void RequireOverlay(XDocument document, string overlayName, string expectedParent)
    {
        XElement overlay = FindNamedElement(document, overlayName) ??
            throw new InvalidOperationException($"XAML 缺少 {overlayName}。 ");
        XElement parent = overlay.Parent ??
            throw new InvalidOperationException($"{overlayName} 没有宿主父节点。 ");
        XNamespace x = XamlNamespace();
        string? parentName = (string?)parent.Attribute(x + "Name");
        Require(parentName == expectedParent,
            $"{overlayName} 错误挂载在 {parentName ?? parent.Name.LocalName}，应位于 {expectedParent}。 ");
        Require(IsLastElement(overlay),
            $"{overlayName} 不是 {expectedParent} 的最后一个子元素，可能被内容兄弟节点覆盖。 ");
        Require(string.Equals((string?)overlay.Attribute("IsHitTestVisible"), "False", StringComparison.OrdinalIgnoreCase),
            $"{overlayName} 必须不可命中，不能拦截收纳窗交互。 ");
        string? zIndex = (string?)overlay.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation") + "ZIndex") ??
            (string?)overlay.Attribute("Canvas.ZIndex");
        Require(int.TryParse(zIndex, out int parsedZ) && parsedZ >= 100,
            $"{overlayName} 没有置于内容之上（Canvas.ZIndex 应至少为 100）。 ");
    }

    XDocument mainDocument = XDocument.Parse(mainXamlSource, LoadOptions.PreserveWhitespace);
    XDocument consoleDocument = XDocument.Parse(consoleXamlSource, LoadOptions.PreserveWhitespace);
    RequireOverlay(mainDocument, "CompactEdgeOverlay", "CompactThumbnailHost");
    RequireOverlay(mainDocument, "ExpandedEdgeOverlay", "ExpandedPanel");
    RequireOverlay(consoleDocument, "SettingsEdgeOverlay", "SettingsContentRoot");
    XElement settingsRoot = FindNamedElement(consoleDocument, "SettingsContentRoot") ??
        throw new InvalidOperationException("设置页缺少 SettingsContentRoot。 ");
    XElement? navigation = settingsRoot.Ancestors().FirstOrDefault(element => element.Name.LocalName == "NavigationView");
    Require(navigation is not null &&
            navigation.Attribute(XamlNamespace() + "Name")?.Value == "RootNavigation",
        "SettingsContentRoot 没有位于 RootNavigation 的右侧内容树中。 ");
    Require(settingsRoot.Parent?.Name.LocalName == "NavigationView",
        "SettingsContentRoot 必须是 NavigationView 的内容根，不能覆盖标题栏或左侧导航栏。 ");
    XElement settingsOverlay = FindNamedElement(consoleDocument, "SettingsEdgeOverlay")!;
    Require(settingsOverlay.Ancestors().Any(element => element == settingsRoot) &&
            !settingsOverlay.Ancestors().Any(element => element.Attribute(XamlNamespace() + "Name")?.Value == "TitleBarDragRegion"),
        "SettingsEdgeOverlay 的覆盖范围越过了设置右侧内容区。 ");
    Require(!consoleDocument.Descendants().Any(element =>
            element.Attribute(XamlNamespace() + "Name")?.Value == "SettingsEdgeOverlay" &&
            element.Ancestors().Any(ancestor => ancestor.Attribute(XamlNamespace() + "Name")?.Value == "RootNavigation" &&
                                                 ancestor.Name.LocalName == "NavigationViewItem")),
        "SettingsEdgeOverlay 错误放入左侧导航菜单项。 ");

    static void CheckEdgeGeometry(
        double width,
        double height,
        double radius,
        double scale,
        bool requirePositiveInnerAndOuter = true)
    {
        double pixelWidth = Math.Round(Math.Max(0, width) * Math.Max(1, scale)) / Math.Max(1, scale);
        double pixelHeight = Math.Round(Math.Max(0, height) * Math.Max(1, scale)) / Math.Max(1, scale);
        double clampedRadius = Math.Min(Math.Max(0, radius), Math.Min(pixelWidth, pixelHeight) / 2);
        double outerHalf = ThemePalette.OrganizerGlassOuterEdgeThicknessDip / 2;
        double innerOffset = ThemePalette.OrganizerGlassInnerEdgeInsetDip +
            ThemePalette.OrganizerGlassInnerEdgeThicknessDip / 2;
        double outerWidth = Math.Max(0, pixelWidth - outerHalf * 2);
        double outerHeight = Math.Max(0, pixelHeight - outerHalf * 2);
        double innerWidth = Math.Max(0, pixelWidth - innerOffset * 2);
        double innerHeight = Math.Max(0, pixelHeight - innerOffset * 2);
        Require(!requirePositiveInnerAndOuter || pixelWidth == 0 || pixelHeight == 0 ||
                (outerWidth > 0 && outerHeight > 0 && innerWidth > 0 && innerHeight > 0),
            $"{width}×{height}@{scale} 的边缘几何没有产生非零内外缘。 ");
        Require(clampedRadius <= Math.Min(pixelWidth, pixelHeight) / 2 + .0001 &&
                outerWidth >= 0 && outerHeight >= 0 && innerWidth >= 0 && innerHeight >= 0,
            "边缘圆角或内缩几何超出宿主边界。 ");
    }

    CheckEdgeGeometry(39, 39, 12, 1);
    CheckEdgeGeometry(640, 420, 18, 1);
    CheckEdgeGeometry(640, 420, 18, 1.25);
    CheckEdgeGeometry(2, 2, 18, 2, requirePositiveInnerAndOuter: false);
    Require(ThemePalette.OrganizerGlassOuterEdgeThicknessDip == 1.25f &&
            ThemePalette.OrganizerGlassInnerEdgeThicknessDip == .75f &&
            ThemePalette.OrganizerGlassInnerEdgeInsetDip == 1.5f,
        "边缘 DIP 参数不是固定的 1.25/0.75/1.5。 ");

    static void RequireVisibleGradient(
        IReadOnlyList<(float Offset, Windows.UI.Color Color)> stops,
        string label)
    {
        Require(stops.Count >= 3 && stops.First().Color.A == 0 && stops.Last().Color.A == 0 &&
                stops.Skip(1).Take(stops.Count - 2).Any(stop => stop.Color.A >= 4),
            $"{label} 必须拥有透明端点和可见中间 alpha。 ");
    }
    RequireVisibleGradient(ThemePalette.GlassArcStops, "玻璃弧光");
    RequireVisibleGradient(ThemePalette.GlassTextureStops, "玻璃拉丝纹理");
    Require(ThemePalette.OrganizerGlassOuterEdgeStops.First().Color.A >= 80 &&
            ThemePalette.OrganizerGlassOuterEdgeStops.Skip(1).Any(stop => stop.Color.A > 0) &&
            ThemePalette.OrganizerGlassInnerEdgeStops.First().Color.A >= 40 &&
            ThemePalette.OrganizerGlassInnerEdgeStops.Skip(1).Any(stop => stop.Color.A > 0),
        "四边圆角外缘/内缘渐变 alpha 过低，可能再次出现代码存在但肉眼不可见。 ");

    Require(consoleXamlSource.Contains("Maximum=\"1\"", StringComparison.Ordinal) &&
            consoleSource.Contains("Maximum = theme.SolidColorMode ? 1 : GlobalSettings.MaximumThemeTransparency", StringComparison.Ordinal) &&
            consoleXamlSource.Contains("Minimum=\".05\"", StringComparison.Ordinal) &&
            consoleXamlSource.Contains("Maximum=\"2\"", StringComparison.Ordinal),
        "设置页滑块未声明 99% 玻璃不透明度和 5%–200% 模糊范围。 ");

    Console.WriteLine("PASS: theme opacity blur arc");
    return;
}

if (args is ["--theme-material-depth"] || args is ["--theme-visual-zero-endpoints"])
{
    bool zeroEndpointOnly = args is ["--theme-visual-zero-endpoints"];

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void Near(double actual, double expected, string message)
    {
        Require(Math.Abs(actual - expected) < .0001, $"{message} 实际={actual:0.####}，期望={expected:0.####}。");
    }

    static string Read(string root, params string[] parts) =>
        File.ReadAllText(Path.Combine([root, .. parts]));

    static int Count(string source, string token)
    {
        int count = 0;
        for (int offset = 0; (offset = source.IndexOf(token, offset, StringComparison.Ordinal)) >= 0; offset += token.Length)
            count++;
        return count;
    }

    if (zeroEndpointOnly)
    {
        var failures = new List<string>();

        void CheckEndpoint(bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        void CheckNear(double actual, double expected, string message)
        {
            if (Math.Abs(actual - expected) >= .0001)
                failures.Add($"{message} 实际={actual:0.####}，期望={expected:0.####}。");
        }

        float ReadPlanFloat(ThemeCompositionPlan plan, string propertyName, string message)
        {
            var property = typeof(ThemeCompositionPlan).GetProperty(propertyName);
            if (property?.GetValue(plan) is not float value)
            {
                failures.Add(message);
                return float.NaN;
            }
            return value;
        }

        bool ReadPlanBool(ThemeCompositionPlan plan, string propertyName, string message)
        {
            var property = typeof(ThemeCompositionPlan).GetProperty(propertyName);
            if (property?.GetValue(plan) is not bool value)
            {
                failures.Add(message);
                return false;
            }
            return value;
        }

        static object? ReadStaticMember(Type type, string memberName)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            return type.GetProperty(memberName, flags)?.GetValue(null) ??
                type.GetField(memberName, flags)?.GetValue(null);
        }

        const uint endpointColor = 0xFF1A80E3;
        string modelSource = Read(Environment.CurrentDirectory, "src", "TuckPane", "Models", "AppState.cs");
        string themeConsoleXaml = Read(Environment.CurrentDirectory, "src", "TuckPane", "ConsoleWindow.xaml");
        string themeConsoleCode = Read(Environment.CurrentDirectory, "src", "TuckPane", "ConsoleWindow.xaml.cs");
        CheckEndpoint(modelSource.Contains("SchemaVersion { get; set; } = 14", StringComparison.Ordinal) &&
                      modelSource.Contains("MaximumThemeTransparency = .99", StringComparison.Ordinal) &&
                      modelSource.Contains("MaximumThemeBlurStrength = 2", StringComparison.Ordinal) &&
                      modelSource.Contains("SolidColorMode", StringComparison.Ordinal),
            "主题状态未升级到 Schema 14 或缺少纯色/新上限字段。 ");
        CheckEndpoint(themeConsoleXaml.Contains("ThemeGlassModeButton", StringComparison.Ordinal) &&
                      themeConsoleXaml.Contains("ThemeSolidModeButton", StringComparison.Ordinal) &&
                      themeConsoleXaml.Contains("Maximum=\".99\"", StringComparison.Ordinal) &&
                      themeConsoleXaml.Contains("Maximum=\"2\"", StringComparison.Ordinal),
            "主题设置页缺少玻璃/纯色模式或滑块上限。 ");
        CheckEndpoint(themeConsoleCode.Contains("ThemeTransparencyRow.Visibility", StringComparison.Ordinal) &&
                      themeConsoleCode.Contains("ThemeBlurStrengthRow.Visibility", StringComparison.Ordinal) &&
                      themeConsoleCode.Contains("solidColorMode", StringComparison.Ordinal),
            "纯色模式没有隐藏透明度/模糊控件或接入状态更新。 ");
        ThemeCompositionPlan transparentEndpoint = ThemePalette.BuildCompositionPlan(
            new ThemeValues(endpointColor, 0, 1), useEffects: true);
        CheckNear(transparentEndpoint.TintOpacity, 0, "不透明度 0% 的主题色比例未归零");
        CheckNear(transparentEndpoint.DesktopOpacity, 1, "不透明度 0% 的玻璃混合比例错误");
        CheckNear(ReadPlanFloat(
                transparentEndpoint,
                "SurfaceOpacity",
                "主题合成计划缺少最终整层不透明度 SurfaceOpacity。"),
            0,
            "不透明度 0% 的最终整层输出未完全透明");
        CheckEndpoint(!ReadPlanBool(
                transparentEndpoint,
                "RequiresHostBackdrop",
                "主题合成计划缺少精确的 HostBackdrop 门槛 RequiresHostBackdrop。") &&
              !transparentEndpoint.UsesGaussianBlur &&
              transparentEndpoint.BlurAmount == 0 &&
              transparentEndpoint.Saturation == 1 &&
              transparentEndpoint.LuminosityOpacity == 0 &&
              transparentEndpoint.HighlightOpacity == 0,
            "不透明度 0% 仍请求 HostBackdrop、Gaussian、调色或高光。 ");

        ThemeCompositionPlan clearHalf = ThemePalette.BuildCompositionPlan(
            new ThemeValues(endpointColor, .5, 0), useEffects: true);
        CheckNear(clearHalf.TintOpacity, .5, "不透明度 50% 的主题色混合比例错误");
        CheckNear(clearHalf.DesktopOpacity, .5, "不透明度 50% 的桌面混合比例错误");
        CheckNear(ReadPlanFloat(
                clearHalf,
                "SurfaceOpacity",
                "主题合成计划缺少最终整层不透明度 SurfaceOpacity。"),
            .5,
            "不透明度 50% 的最终整层输出错误");
        CheckEndpoint(!ReadPlanBool(
                clearHalf,
                "RequiresHostBackdrop",
                "主题合成计划缺少精确的 HostBackdrop 门槛 RequiresHostBackdrop。") &&
              !clearHalf.UsesGaussianBlur &&
              clearHalf.BlurAmount == 0 &&
              clearHalf.Saturation == 1 &&
              clearHalf.LuminosityOpacity == 0 &&
              clearHalf.HighlightOpacity == 0 &&
              ThemePalette.WithOpacity(clearHalf.TintColor, clearHalf.TintOpacity).A == 128,
            "模糊 0% 仍创建光学处理，或半透明清晰主题色 alpha 不正确。 ");

        ThemeCompositionPlan glassMaximum = ThemePalette.BuildCompositionPlan(
            new ThemeValues(endpointColor, GlobalSettings.MaximumThemeTransparency, 1), useEffects: true);
        CheckNear(glassMaximum.TintOpacity, .99, "玻璃模式最大不透明度未限制为 99%");
        CheckNear(glassMaximum.DesktopOpacity, .01, "玻璃模式最大不透明度的桌面比例错误");
        ThemeCompositionPlan solidEndpoint = ThemePalette.BuildCompositionPlan(
            new ThemeValues(endpointColor, .35, 1, SolidColorMode: true), useEffects: true);
        CheckNear(solidEndpoint.TintOpacity, 1, "纯色模式的主题色比例错误");
        CheckNear(solidEndpoint.DesktopOpacity, 0, "纯色模式的桌面比例未归零");
        CheckNear(ReadPlanFloat(
                solidEndpoint,
                "SurfaceOpacity",
                "主题合成计划缺少最终整层不透明度 SurfaceOpacity。"),
            1,
            "纯色模式的最终整层输出错误");
        CheckEndpoint(!ReadPlanBool(
                solidEndpoint,
                "RequiresHostBackdrop",
                "主题合成计划缺少精确的 HostBackdrop 门槛 RequiresHostBackdrop。") &&
              !solidEndpoint.UsesGaussianBlur &&
              solidEndpoint.HighlightOpacity == 0,
            "纯色模式仍请求 HostBackdrop、Gaussian 或高光。 ");

        ThemeCompositionPlan glass = ThemePalette.BuildCompositionPlan(
            new ThemeValues(endpointColor, .5, 1), useEffects: true);
        CheckEndpoint(ReadPlanBool(
                glass,
                "RequiresHostBackdrop",
                "主题合成计划缺少精确的 HostBackdrop 门槛 RequiresHostBackdrop。") &&
              glass.UsesGaussianBlur,
            "中间不透明度且非零模糊没有进入唯一玻璃分支。 ");
        CheckNear(ReadPlanFloat(
                glass,
                "SurfaceOpacity",
                "主题合成计划缺少最终整层不透明度 SurfaceOpacity。"),
            .5,
            "玻璃分支没有按不透明度控制最终整层输出");
        CheckNear(glass.TintOpacity, .5, "玻璃分支没有按不透明度混合主题色");
        CheckNear(glass.DesktopOpacity, .5, "玻璃分支的桌面混合比例错误");
        CheckNear(glass.BlurAmount, 10, "模糊 100% 没有产生 10px GaussianBlur");
        CheckNear(glass.Saturation, 2, "模糊 100% 的饱和度错误");
        CheckNear(glass.LuminosityOpacity, .06, "模糊 100% 的明度错误");
        CheckNear(glass.HighlightOpacity, 1, "内部高光没有使用 4×o×(1-o)×min(b,1)");

        ThemeCompositionPlan endpointFallback = ThemePalette.BuildCompositionPlan(
            new ThemeValues(endpointColor, .5, 1), useEffects: false);
        CheckEndpoint(!ReadPlanBool(
                endpointFallback,
                "RequiresHostBackdrop",
                "主题合成计划缺少精确的 HostBackdrop 门槛 RequiresHostBackdrop。") &&
              !endpointFallback.UsesGaussianBlur &&
              endpointFallback.BlurAmount == 0 &&
              endpointFallback.Saturation == 1 &&
              endpointFallback.LuminosityOpacity == 0 &&
              endpointFallback.HighlightOpacity == 0 &&
              ThemePalette.WithOpacity(endpointFallback.TintColor, endpointFallback.TintOpacity).A == 128,
            "高级效果关闭时没有降级为 alpha=o 的清晰主题色。 ");

        string endpointSourceRoot = Path.Combine(Environment.CurrentDirectory, "src", "TuckPane");
        string endpointServicesRoot = Path.Combine(endpointSourceRoot, "Services");
        string endpointBackdrop = Read(endpointServicesRoot, "ThemeBackdrop.cs");
        int endpointBuildStart = endpointBackdrop.IndexOf("private Wuc.CompositionBrush BuildBrush", StringComparison.Ordinal);
        int endpointHostCall = endpointBackdrop.IndexOf("CreateHostBackdropBrush()", endpointBuildStart, StringComparison.Ordinal);
        int endpointFallbackStart = endpointBackdrop.IndexOf("private Wuc.CompositionBrush BuildColorFallbackBrush", endpointBuildStart, StringComparison.Ordinal);
        string endpointBeforeHost = endpointBuildStart >= 0 && endpointHostCall > endpointBuildStart
            ? endpointBackdrop[endpointBuildStart..endpointHostCall]
            : string.Empty;
        string endpointFallbackBody = endpointFallbackStart >= 0 ? endpointBackdrop[endpointFallbackStart..] : string.Empty;
        CheckEndpoint(endpointBuildStart >= 0 && endpointHostCall > endpointBuildStart &&
              endpointBeforeHost.Contains("RequiresHostBackdrop", StringComparison.Ordinal) &&
              endpointBeforeHost.Contains("BuildColorFallbackBrush", StringComparison.Ordinal) &&
              Count(endpointBackdrop, "CreateHostBackdropBrush()") == 1 &&
              Count(endpointBackdrop, "new GaussianBlurEffect") == 1,
            "ThemeBackdrop 没有在 HostBackdrop/Gaussian 创建前应用唯一玻璃门槛。 ");
        CheckEndpoint(endpointBackdrop.Contains("_plan.SurfaceOpacity", StringComparison.Ordinal) &&
              endpointBackdrop.Contains("new OpacityEffect", StringComparison.Ordinal),
            "玻璃分支没有按 SurfaceOpacity 控制最终整层输出。 ");
        CheckEndpoint(endpointFallbackBody.Contains("ThemePalette.WithOpacity", StringComparison.Ordinal) &&
              endpointFallbackBody.Contains("_plan.TintOpacity", StringComparison.Ordinal) &&
              !endpointFallbackBody.Contains("GaussianBlurEffect", StringComparison.Ordinal),
            "主题色 fallback 没有使用 alpha=o，或重新引入了模糊。 ");
        CheckEndpoint(endpointBackdrop.Contains("BorderMode = EffectBorderMode.Hard", StringComparison.Ordinal),
            "GaussianBlur 不再保留 Hard 边界模式。 ");

        object? outerThickness = ReadStaticMember(
            typeof(ThemePalette), "OrganizerGlassOuterEdgeThicknessDip");
        object? innerThickness = ReadStaticMember(
            typeof(ThemePalette), "OrganizerGlassInnerEdgeThicknessDip");
        object? innerInset = ReadStaticMember(
            typeof(ThemePalette), "OrganizerGlassInnerEdgeInsetDip");
        CheckEndpoint(outerThickness is float outerThicknessValue &&
              Math.Abs(outerThicknessValue - 1.25f) < .0001f &&
              innerThickness is float innerThicknessValue &&
              Math.Abs(innerThicknessValue - .75f) < .0001f &&
              innerInset is float innerInsetValue &&
              Math.Abs(innerInsetValue - 1.5f) < .0001f,
            "收纳窗永久玻璃边缘缺少固定的 1.25/0.75/1.5 DIP 参数。 ");

        var outerEdgeStops = ReadStaticMember(
            typeof(ThemePalette), "OrganizerGlassOuterEdgeStops") as
            IReadOnlyList<(float Offset, Windows.UI.Color Color)>;
        var innerEdgeStops = ReadStaticMember(
            typeof(ThemePalette), "OrganizerGlassInnerEdgeStops") as
            IReadOnlyList<(float Offset, Windows.UI.Color Color)>;
        CheckEndpoint(outerEdgeStops is { Count: >= 3 } &&
              outerEdgeStops.First().Color == Windows.UI.Color.FromArgb(112, 255, 255, 255) &&
              outerEdgeStops.Any(stop =>
                  stop.Color == Windows.UI.Color.FromArgb(16, 255, 255, 255)) &&
              outerEdgeStops.Last().Color == Windows.UI.Color.FromArgb(44, 0, 0, 0),
            "收纳窗外缘没有使用固定的左上白色 112→16、右下黑色 44 渐变。 ");
        CheckEndpoint(innerEdgeStops is { Count: >= 3 } &&
              innerEdgeStops.First().Color == Windows.UI.Color.FromArgb(64, 255, 255, 255) &&
              innerEdgeStops.Any(stop =>
                  stop.Color == Windows.UI.Color.FromArgb(8, 255, 255, 255)) &&
              innerEdgeStops.Last().Color == Windows.UI.Color.FromArgb(32, 0, 0, 0),
            "收纳窗内缘没有使用固定的左上白色 64→8、右下黑色 32 渐变。 ");

        string endpointSurface = Read(endpointServicesRoot, "ThemeSurface.cs");
        int endpointSetThemeStart = endpointSurface.IndexOf(
            "internal void SetTheme", StringComparison.Ordinal);
        int endpointSetCornerRadiusStart = endpointSurface.IndexOf(
            "internal void SetCornerRadius", endpointSetThemeStart, StringComparison.Ordinal);
        string endpointSetThemeBody = endpointSetThemeStart >= 0 &&
            endpointSetCornerRadiusStart > endpointSetThemeStart
                ? endpointSurface[endpointSetThemeStart..endpointSetCornerRadiusStart]
                : string.Empty;
        CheckEndpoint(endpointSurface.Contains("bool showPersistentGlassEdge = false", StringComparison.Ordinal) &&
              endpointSurface.Contains("CreateShapeVisual", StringComparison.Ordinal) &&
              Count(endpointSurface, "CreateRoundedRectangleGeometry()") >= 2 &&
              Count(endpointSurface, "CreateSpriteShape(") >= 2 &&
              Count(endpointSurface, "StrokeBrush =") >= 2 &&
              Count(endpointSurface, "StrokeThickness =") >= 2,
            "ThemeSurface 缺少默认关闭的永久双层圆角玻璃描边。 ");
        CheckEndpoint(Count(endpointSurface, "BorderMode = CompositionBorderMode.Soft") >= 3,
            "ThemeSurface 根视觉、高光视觉或边缘视觉没有显式使用 Soft 抗锯齿。 ");
        CheckEndpoint(!endpointSetThemeBody.Contains("OrganizerGlassOuterEdge", StringComparison.Ordinal) &&
              !endpointSetThemeBody.Contains("OrganizerGlassInnerEdge", StringComparison.Ordinal) &&
              !endpointSetThemeBody.Contains("StrokeBrush", StringComparison.Ordinal),
            "SetTheme 仍会根据主题端点重建或关闭永久玻璃边缘。 ");

        string transparentHost = Read(endpointServicesRoot, "TransparentWindowBackdrop.cs");
        CheckEndpoint(transparentHost.Contains("DwmBlurBehindBlurRegion", StringComparison.Ordinal) &&
              transparentHost.Contains("Enable = true", StringComparison.Ordinal) &&
              transparentHost.Contains("CreateRectRgn(", StringComparison.Ordinal) &&
              transparentHost.Contains("Region =", StringComparison.Ordinal) &&
              transparentHost.Contains("DwmEnableBlurBehindWindow", StringComparison.Ordinal),
            "透明宿主没有启用带空区域的 DWM alpha 合成初始化。 ");
        CheckEndpoint(transparentHost.Contains("WM_ERASEBKGND", StringComparison.Ordinal) &&
              transparentHost.Contains("GetDC(", StringComparison.Ordinal) &&
              transparentHost.Contains("CreateSolidBrush(0", StringComparison.Ordinal) &&
              transparentHost.Contains("FillRect(", StringComparison.Ordinal) &&
              transparentHost.Contains("ReleaseDC(", StringComparison.Ordinal) &&
              Count(transparentHost, "DeleteObject(") >= 3,
            "透明宿主没有用黑色清底，或未完整释放 HDC、HRGN、HBRUSH。 ");
        CheckEndpoint(transparentHost.Contains("DwmExtendFrameIntoClientArea", StringComparison.Ordinal) &&
              transparentHost.Contains("new DwmMargins()", StringComparison.Ordinal),
            "透明宿主没有维持零 frame margin。 ");
        int messageHandlerStart = transparentHost.IndexOf(
            "private void MessageMonitor_WindowMessageReceived", StringComparison.Ordinal);
        int configureStart = transparentHost.IndexOf("private static void Configure", messageHandlerStart, StringComparison.Ordinal);
        string messageHandler = messageHandlerStart >= 0 && configureStart > messageHandlerStart
            ? transparentHost[messageHandlerStart..configureStart]
            : string.Empty;
        int compositionChangedStart = messageHandler.IndexOf("WM_DWMCOMPOSITIONCHANGED", StringComparison.Ordinal);
        string compositionChangedBranch = compositionChangedStart >= 0
            ? messageHandler[compositionChangedStart..]
            : string.Empty;
        CheckEndpoint(compositionChangedBranch.Contains("Configure(e.Message.Hwnd)", StringComparison.Ordinal) &&
              !compositionChangedBranch.Contains("e.Handled = true", StringComparison.Ordinal),
            "DWM 重建后没有重应用透明宿主，或错误截断了其他 HWND subclass 消息链。 ");

        string mainXaml = Read(endpointSourceRoot, "MainWindow.xaml");
        string mainCode = Read(endpointSourceRoot, "MainWindow.xaml.cs");
        CheckEndpoint(mainXaml.Contains("x:Name=\"WindowRoot\"", StringComparison.Ordinal) &&
              mainXaml.Contains("Background=\"Transparent\"", StringComparison.Ordinal) &&
              mainXaml.Contains("x:Name=\"CompactSurfaceHost\"", StringComparison.Ordinal) &&
              mainXaml.Contains("x:Name=\"ExpandedSurfaceHost\"", StringComparison.Ordinal) &&
              mainCode.Contains("CompactSurfaceHost.CornerRadius", StringComparison.Ordinal) &&
              mainCode.Contains("ExpandedSurfaceHost.CornerRadius", StringComparison.Ordinal),
            "收纳根节点未透明，或局部主题背景未受圆角约束。 ");
        CheckEndpoint(mainCode.Contains(
                  "new ThemeSurface(CompactSurfaceHost, showPersistentGlassEdge: true)",
                  StringComparison.Ordinal) &&
              mainCode.Contains(
                  "new ThemeSurface(ExpandedSurfaceHost, showPersistentGlassEdge: true)",
                  StringComparison.Ordinal) &&
              Count(mainCode, "showPersistentGlassEdge: true") == 2,
            "MainWindow 没有仅为 compact/expanded 两个收纳表面启用永久玻璃边缘。 ");
        CheckEndpoint(mainCode.Contains("GetElementVisual(CompactSurfaceHost)", StringComparison.Ordinal) &&
              mainCode.Contains("GetElementVisual(ExpandedSurfaceHost)", StringComparison.Ordinal) &&
              mainCode.Contains("GetElementVisual(CompactIconPresenter)", StringComparison.Ordinal) &&
              mainCode.Contains("GetElementVisual(ExpandedContentLayer)", StringComparison.Ordinal) &&
              Count(mainCode, "BorderMode = CompositionBorderMode.Soft") >= 4,
            "MainWindow 的两个背景宿主或两个内容裁剪 Visual 没有显式使用 Soft 抗锯齿。 ");
        string endpointConsoleCode = Read(endpointSourceRoot, "ConsoleWindow.xaml.cs");
        string endpointOwnedDialog = Read(endpointServicesRoot, "OwnedDialogWindow.cs");
        CheckEndpoint(endpointConsoleCode.Contains(
                  "new ThemeSurface(ConsoleSurfaceHost)", StringComparison.Ordinal) &&
              !endpointConsoleCode.Contains("showPersistentGlassEdge", StringComparison.Ordinal) &&
              endpointOwnedDialog.Contains("new ThemeSurface(surfaceHost)", StringComparison.Ordinal) &&
              !endpointOwnedDialog.Contains("showPersistentGlassEdge", StringComparison.Ordinal),
            "设置窗口或 OwnedDialog 错误启用了收纳窗专属永久玻璃边缘。 ");
        string endpointDesktopLayer = Read(endpointServicesRoot, "DesktopLayerService.cs");
        CheckEndpoint(endpointDesktopLayer.Contains("DWMWCP_DONOTROUND", StringComparison.Ordinal) &&
              endpointDesktopLayer.Contains("DWMWA_WINDOW_CORNER_PREFERENCE", StringComparison.Ordinal) &&
              endpointDesktopLayer.Contains("DWMWA_BORDER_COLOR", StringComparison.Ordinal) &&
              endpointDesktopLayer.Contains("DWMWA_COLOR_NONE", StringComparison.Ordinal) &&
              endpointDesktopLayer.Contains("WM_DWMCOMPOSITIONCHANGED", StringComparison.Ordinal) &&
              Count(endpointDesktopLayer, "ApplyTransparentChrome();") == 2,
            "DWM 禁用圆角/系统边框没有覆盖初始应用与 DWM 重建生命周期。 ");

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "theme visual zero endpoints failed:\n - " + string.Join("\n - ", failures));
        }

        Console.WriteLine("PASS: theme visual zero endpoints");
        return;
    }

    Require(GlobalSettings.NormalizeThemeTransparency(-1) == 0 &&
            GlobalSettings.NormalizeThemeTransparency(.5) == .5 &&
            GlobalSettings.NormalizeThemeTransparency(1) == .99 &&
            GlobalSettings.NormalizeThemeTransparency(2) == .99 &&
            GlobalSettings.NormalizeThemeTransparency(double.NaN) == GlobalSettings.DefaultThemeTransparency,
        "透明度未按 0..1 归一化。");
    Require(GlobalSettings.NormalizeThemeBlurStrength(-1) == 0 &&
            GlobalSettings.NormalizeThemeBlurStrength(.83) == .83 &&
            GlobalSettings.NormalizeThemeBlurStrength(2) == 2 &&
            GlobalSettings.NormalizeThemeBlurStrength(double.NaN) == 1,
        "模糊强度未按 0..1.5 归一化。");

    const uint color = 0xFF1A80E3;
    ThemeEffectParameters glassEffect = ThemePalette.Effect();
    Require(glassEffect == new ThemeEffectParameters(10, 2, .06f),
        "唯一 Glass 参数不是基础模糊 10、饱和度 2、明度 0.06。");

    ThemeCompositionPlan transparent = ThemePalette.BuildCompositionPlan(
        new ThemeValues(color, 0, 1), useEffects: true);
    ThemeCompositionPlan half = ThemePalette.BuildCompositionPlan(
        new ThemeValues(color, .5, 1), useEffects: true);
    ThemeCompositionPlan opaque = ThemePalette.BuildCompositionPlan(
        new ThemeValues(color, .35, 1, SolidColorMode: true), useEffects: true);
    Near(transparent.TintOpacity, 0, "不透明度 0% 主题比例错误");
    Near(transparent.DesktopOpacity, 1, "不透明度 0% 桌面混合比例错误");
    Near(transparent.SurfaceOpacity, 0, "不透明度 0% 最终输出未透明");
    Require(!transparent.RequiresHostBackdrop && !transparent.UsesGaussianBlur,
        "不透明度 0% 仍创建 HostBackdrop 或 GaussianBlur。");
    Near(half.TintOpacity, .5, "不透明度 50% 主题比例错误");
    Near(half.DesktopOpacity, .5, "不透明度 50% 桌面混合比例错误");
    Near(half.SurfaceOpacity, .5, "不透明度 50% 最终输出比例错误");
    Require(half.RequiresHostBackdrop, "中间不透明度与非零模糊未创建玻璃分支。");
    Near(opaque.TintOpacity, 1, "纯色模式主题比例错误");
    Near(opaque.DesktopOpacity, 0, "纯色模式桌面比例错误");
    Near(opaque.SurfaceOpacity, 1, "纯色模式最终输出错误");
    Require(!opaque.RequiresHostBackdrop && !opaque.UsesGaussianBlur,
        "纯色模式仍创建 HostBackdrop 或 GaussianBlur。");
    Require(opaque.TintColor.R == 0x1A &&
            opaque.TintColor.G == 0x80 &&
            opaque.TintColor.B == 0xE3,
        "唯一 Glass 管线覆盖了用户选色。");

    ThemeCompositionPlan zeroBlur = ThemePalette.BuildCompositionPlan(
        new ThemeValues(color, .35, 0), useEffects: true);
    Near(zeroBlur.BlurAmount, 0, "模糊 0% 未归零");
    Require(!zeroBlur.RequiresHostBackdrop &&
            !zeroBlur.UsesGaussianBlur &&
            Math.Abs(zeroBlur.Saturation - 1) < .0001 &&
            Math.Abs(zeroBlur.LuminosityOpacity) < .0001 &&
            Math.Abs(zeroBlur.HighlightOpacity) < .0001,
        "模糊 0% 仍保留 HostBackdrop、Gaussian、色调处理或高光。");

    ThemeCompositionPlan halfBlur = ThemePalette.BuildCompositionPlan(
        new ThemeValues(color, .35, .5), useEffects: true);
    Near(halfBlur.BlurAmount, 5, "模糊 50% 空间强度错误");
    Near(halfBlur.Saturation, 1.5, "模糊 50% 饱和度没有平滑过渡");
    Near(halfBlur.LuminosityOpacity, .03, "模糊 50% 明度没有平滑过渡");
    Near(halfBlur.HighlightOpacity, .455, "模糊 50% 高光公式错误");

    ThemeCompositionPlan normalBlur = ThemePalette.BuildCompositionPlan(
        new ThemeValues(color, .35, 1), useEffects: true);
    Near(normalBlur.BlurAmount, glassEffect.BlurAmount, "模糊 100% 未使用 Glass 基础强度");
    Near(normalBlur.Saturation, glassEffect.Saturation, "模糊 100% 饱和度错误");
    Near(normalBlur.LuminosityOpacity, glassEffect.LuminosityOpacity, "模糊 100% 明度错误");
    Near(normalBlur.HighlightOpacity, .91, "模糊 100% 高光公式错误");
    Require(normalBlur.UsesGaussianBlur, "非零模糊未创建 GaussianBlur");

        ThemeCompositionPlan maxBlur = ThemePalette.BuildCompositionPlan(
            new ThemeValues(color, .35, 2), useEffects: true);
        Near(maxBlur.BlurAmount, glassEffect.BlurAmount * 2, "模糊 200% 未按比例放大");
    Near(maxBlur.Saturation, glassEffect.Saturation, "模糊 150% 饱和度没有在 100% 封顶");
    Near(maxBlur.LuminosityOpacity, glassEffect.LuminosityOpacity, "模糊 150% 明度没有在 100% 封顶");
    Near(maxBlur.HighlightOpacity, .91, "模糊 150% 高光没有在 100% 封顶");

    Near(ThemePalette.HighlightOpacity(new ThemeValues(color, 1, 0)), 0,
        "完全实色且 0% 模糊时高光仍可见");
    Near(ThemePalette.HighlightOpacity(new ThemeValues(color, 1, .5)), 0,
        "完全实色端点仍显示高光");
    Near(ThemePalette.HighlightOpacity(new ThemeValues(color, .5, 0)), 0,
        "零模糊背景仍显示高光");
    Near(ThemePalette.HighlightOpacity(new ThemeValues(color, 0, 0)), 0,
        "完全透明端点仍显示高光");
    Near(ThemePalette.HighlightOpacity(new ThemeValues(color, 0, 1)), 0,
        "完全透明且非零模糊时仍显示高光");
    Near(ThemePalette.HighlightOpacity(new ThemeValues(color, .5, 1)), 1,
        "中间不透明度的内部高光没有使用端点衰减公式");
    Near(ThemePalette.HighlightOpacity(new ThemeValues(color, .5, 1.5)), 1,
        "150% 模糊的内部高光没有在 100% 封顶");
    Require(ThemePalette.GlassHighlightStops.First().Color.A == 0 &&
            ThemePalette.GlassHighlightStops.Last().Color.A == 0 &&
            ThemePalette.GlassHighlightStops.Skip(1).SkipLast(1).Any(stop => stop.Color.A > 0) &&
            ThemePalette.GlassHighlightStops.Max(stop => stop.Color.A) <= 64,
        "Glass 内部高光缺失、触及边缘或强度不再保持淡化。 ");

    ThemeCompositionPlan fallback = ThemePalette.BuildCompositionPlan(
        new ThemeValues(color, .5, 1), useEffects: false);
    Require(fallback.TintOpacity == .5f && fallback.DesktopOpacity == .5f &&
            fallback.SurfaceOpacity == .5f && !fallback.RequiresHostBackdrop &&
            fallback.BlurAmount == 0 && !fallback.UsesGaussianBlur &&
            fallback.Saturation == 1 && fallback.LuminosityOpacity == 0 &&
            fallback.HighlightOpacity == 0 && !fallback.UseEffects &&
            ThemePalette.WithOpacity(fallback.TintColor, 0).A == 0 &&
            ThemePalette.WithOpacity(fallback.TintColor, .5f).A == 128 &&
            ThemePalette.WithOpacity(fallback.TintColor, 1).A == 255,
        "高级效果 fallback 未保持透明度比例或伪造模糊/高光。");

    string sourceRoot = Path.Combine(Environment.CurrentDirectory, "src", "TuckPane");
    string servicesRoot = Path.Combine(sourceRoot, "Services");
    string palette = Read(servicesRoot, "ThemePalette.cs");
    int tintStart = palette.IndexOf("internal static Color TintColor", StringComparison.Ordinal);
    int foregroundStart = palette.IndexOf("internal static Color ForegroundColor", tintStart, StringComparison.Ordinal);
    Require(tintStart >= 0 && foregroundStart > tintStart &&
            !palette.Contains("ThemeMaterial", StringComparison.Ordinal),
        "ThemePalette 仍包含可选材质参数。");

    string backdrop = Read(servicesRoot, "ThemeBackdrop.cs");
    int buildStart = backdrop.IndexOf("private Wuc.CompositionBrush BuildBrush", StringComparison.Ordinal);
    int hostCall = backdrop.IndexOf("CreateHostBackdropBrush()", buildStart, StringComparison.Ordinal);
    int fallbackStart = backdrop.IndexOf("private Wuc.CompositionBrush BuildColorFallbackBrush", buildStart, StringComparison.Ordinal);
    Require(backdrop.Contains("BuildCompositionPlan", StringComparison.Ordinal) &&
            backdrop.Contains("if (_plan.UsesGaussianBlur)", StringComparison.Ordinal) &&
            Count(backdrop, "new GaussianBlurEffect") == 1 &&
            !backdrop.Contains("ThemeMaterial", StringComparison.Ordinal) &&
            !backdrop.Contains("Noise", StringComparison.Ordinal) &&
            buildStart >= 0 && hostCall > buildStart && fallbackStart > hostCall,
        "ThemeBackdrop 缺少单一 Glass 桌面/主题分支，或仍保留材质噪点。");
    string beforeHost = backdrop[buildStart..hostCall];
    Require(beforeHost.Contains("if (!_plan.RequiresHostBackdrop)", StringComparison.Ordinal) &&
            beforeHost.Contains("_hostBackdropCapabilityAvailable", StringComparison.Ordinal) &&
            beforeHost.Contains("BuildColorFallbackBrush", StringComparison.Ordinal),
        "HostBackdrop 未受唯一玻璃门槛和能力 fallback 守卫。");
    string fallbackBody = backdrop[fallbackStart..];
    Require(fallbackBody.Contains("ThemePalette.WithOpacity", StringComparison.Ordinal) &&
            !fallbackBody.Contains("GaussianBlurEffect", StringComparison.Ordinal),
        "主题色 fallback 重新引入了空间模糊。");
    Require(backdrop.Contains("bool shouldEnable = _plan.RequiresHostBackdrop", StringComparison.Ordinal) &&
            backdrop.Contains("bool applied = NativeMethods.SetHostBackdropBrushEnabled", StringComparison.Ordinal),
        "HostBackdrop 能力更新未检查唯一玻璃门槛和 DWM opt-in 结果。");

    string surface = Read(servicesRoot, "ThemeSurface.cs");
    Require(surface.Contains("CreateHighlightBrush", StringComparison.Ordinal) &&
            surface.Contains("HighlightOpacity", StringComparison.Ordinal) &&
            !surface.Contains("CreateHostBackdropBrush()", StringComparison.Ordinal) &&
            !surface.Contains("CreateBackdropBrush()", StringComparison.Ordinal) &&
            !surface.Contains("GaussianBlurEffect", StringComparison.Ordinal) &&
            !surface.Contains("CreateColorBrush(", StringComparison.Ordinal),
        "ThemeSurface 仍负责主背景或模糊。");

    string desktopLayer = Read(servicesRoot, "DesktopLayerService.cs");
    int desktopConstructor = desktopLayer.IndexOf("public DesktopLayerService(", StringComparison.Ordinal);
    int reattachMethod = desktopConstructor >= 0
        ? desktopLayer.IndexOf("public void Reattach()", desktopConstructor, StringComparison.Ordinal)
        : -1;
    int activationGuard = desktopLayer.IndexOf("private IntPtr ActivationGuard(", StringComparison.Ordinal);
    int findDesktop = activationGuard >= 0
        ? desktopLayer.IndexOf("internal static IntPtr FindDesktopIconView()", activationGuard, StringComparison.Ordinal)
        : -1;
    Require(desktopConstructor >= 0 && reattachMethod > desktopConstructor &&
            activationGuard >= 0 && findDesktop > activationGuard &&
            Count(desktopLayer, "ApplyTransparentChrome();") == 2 &&
            desktopLayer[desktopConstructor..reattachMethod].Contains("ApplyTransparentChrome();", StringComparison.Ordinal) &&
            desktopLayer[activationGuard..findDesktop].Contains("ApplyTransparentChrome();", StringComparison.Ordinal) &&
            desktopLayer.Contains("DWMWA_BORDER_COLOR", StringComparison.Ordinal) &&
            desktopLayer.Contains("DWMWA_COLOR_NONE", StringComparison.Ordinal) &&
            desktopLayer[activationGuard..findDesktop].Contains("WM_THEMECHANGED", StringComparison.Ordinal) &&
            desktopLayer[activationGuard..findDesktop].Contains("WM_DWMCOMPOSITIONCHANGED", StringComparison.Ordinal) &&
            desktopLayer[activationGuard..findDesktop].Contains("WM_SETTINGCHANGE", StringComparison.Ordinal),
        "收纳窗 DWM 无边框属性没有覆盖系统主题、设置和合成重建生命周期。");

    string transparentBackdropSource = Read(servicesRoot, "TransparentWindowBackdrop.cs");
    Require(transparentBackdropSource.Contains("WM_ERASEBKGND", StringComparison.Ordinal) &&
            transparentBackdropSource.Contains("e.Result = 1", StringComparison.Ordinal) &&
            transparentBackdropSource.Contains("e.Handled = true", StringComparison.Ordinal) &&
            transparentBackdropSource.Contains("DwmExtendFrameIntoClientArea", StringComparison.Ordinal) &&
            transparentBackdropSource.Contains("DwmBlurBehindBlurRegion", StringComparison.Ordinal) &&
            transparentBackdropSource.Contains("Enable = true", StringComparison.Ordinal) &&
            transparentBackdropSource.Contains("CreateRectRgn(", StringComparison.Ordinal) &&
            transparentBackdropSource.Contains("CreateSolidBrush(0", StringComparison.Ordinal) &&
            transparentBackdropSource.Contains("FillRect(", StringComparison.Ordinal) &&
            transparentBackdropSource.Contains("GetDC(", StringComparison.Ordinal) &&
            transparentBackdropSource.Contains("ReleaseDC(", StringComparison.Ordinal) &&
            Count(transparentBackdropSource, "DeleteObject(") >= 3 &&
            Count(transparentBackdropSource, "DwmEnableBlurBehindWindow(") == 2,
        "透明承载缺少 DWM alpha 初始化、黑色清底或 GDI 资源释放。");
    string[] blurSources = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
        .Where(path => File.ReadAllText(path).Contains("DwmEnableBlurBehindWindow(", StringComparison.Ordinal))
        .ToArray();
    Require(blurSources.Length == 1 &&
            Path.GetFileName(blurSources[0]).Equals("TransparentWindowBackdrop.cs", StringComparison.Ordinal),
        "窗口级 DWM blur 出现在透明承载之外。");

    string[] targets =
    [
        Read(sourceRoot, "ConsoleWindow.xaml.cs"),
        Read(sourceRoot, "MainWindow.xaml.cs"),
        Read(servicesRoot, "OwnedDialogWindow.cs")
    ];
    Require(targets.All(text => text.Contains("TransparentWindowBackdrop", StringComparison.Ordinal) &&
                                !text.Contains("TransparentTintBackdrop", StringComparison.Ordinal) &&
                                !text.Contains("DwmEnableBlurBehindWindow", StringComparison.Ordinal) &&
                                text.Contains("ThemeSurface", StringComparison.Ordinal)),
        "目标窗口接线仍使用旧透明 tint 或缺少 ThemeSurface。");
    string consoleXaml = Read(sourceRoot, "ConsoleWindow.xaml");
    string consoleCode = Read(sourceRoot, "ConsoleWindow.xaml.cs");
    string modelCode = Read(sourceRoot, "Models", "AppState.cs");
    Require(consoleXaml.Contains("<SystemBackdropElement", StringComparison.Ordinal) &&
            Read(sourceRoot, "MainWindow.xaml").Contains("CompactSurfaceHost", StringComparison.Ordinal) &&
            Read(sourceRoot, "MainWindow.xaml").Contains("ExpandedSurfaceHost", StringComparison.Ordinal) &&
            Read(servicesRoot, "OwnedDialogWindow.cs").Contains("new SystemBackdropElement", StringComparison.Ordinal),
        "设置、收纳或 OwnedDialog 未使用局部 SystemBackdropElement。");
    Require(!consoleXaml.Contains("ThemeMaterial", StringComparison.Ordinal) &&
            !consoleCode.Contains("ThemeMaterial", StringComparison.Ordinal) &&
            !modelCode.Contains("ThemeMaterial", StringComparison.Ordinal) &&
            !modelCode.Contains("SettingsThemeMaterial", StringComparison.Ordinal),
        "材质选择 UI、事件或持久化类型仍然存在。");
    string[] themeProductionSources = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetExtension(path) is ".cs" or ".xaml" or ".resw")
        .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj"))
        .ToArray();
    string[] removedMaterialTokens =
    [
        "ThemeMaterial",
        "SettingsThemeMaterial",
        "AcrylicMaterial",
        "GlassMaterial",
        "MatteMaterial"
    ];
    Require(themeProductionSources.All(path => removedMaterialTokens.All(token =>
            !File.ReadAllText(path).Contains(token, StringComparison.Ordinal))),
        "生产代码、XAML 或资源中仍有已删除的可选材质符号。");
    string[] resourceCultures = ["zh-CN", "en-US", "ja-JP"];
    Require(resourceCultures.All(culture =>
            !Read(sourceRoot, "Strings", culture, "Resources.resw")
                .Contains("ThemeMaterial", StringComparison.Ordinal)),
        "中英日资源仍包含已删除的材质入口文案。");

    string tempRoot = Path.Combine(Path.GetTempPath(), $"TuckPane-theme-depth-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempRoot);
    try
    {
        var settings = new GlobalSettings();
        settings.SetTheme(ThemeTarget.Organizer, new ThemeValues(0xFF123456, .5, .25));
        settings.SetTheme(ThemeTarget.Settings, new ThemeValues(0xFFABCDEF, .25, 1.35));
        string statePath = Path.Combine(tempRoot, "state.json");
        var store = new StateStore(statePath);
        await store.SaveAsync(new AppStateV2 { GlobalSettings = settings });
        AppStateV2 roundTrip = await store.LoadAsync();
        Require(roundTrip.SchemaVersion == 13 &&
                roundTrip.GlobalSettings.GetTheme(ThemeTarget.Organizer) ==
                    new ThemeValues(0xFF123456, .5, .25) &&
                roundTrip.GlobalSettings.GetTheme(ThemeTarget.Settings) ==
                    new ThemeValues(0xFFABCDEF, .25, 1.35),
            "设置与收纳主题没有独立保存/重载。");

        string legacyPath = Path.Combine(tempRoot, "schema-12.json");
        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 12,
            GlobalSettings = new
            {
                ThemeColorArgb = 0xFF102030u,
                Material = 0,
                ThemeTransparency = .82,
                ThemeBlurStrength = .25,
                SettingsThemeColorArgb = 0xFF405060u,
                SettingsThemeMaterial = 3,
                SettingsThemeTransparency = .18,
                SettingsThemeBlurStrength = 1.35,
                NoteTheme = NoteTheme.RainBlue
            },
            Organizers = Array.Empty<object>()
        }));
        var legacyStore = new StateStore(legacyPath);
        AppStateV2 migrated = await legacyStore.LoadAsync();
        Require(migrated.SchemaVersion == 13 &&
                migrated.GlobalSettings.ThemeTransparency == .82 &&
                migrated.GlobalSettings.SettingsThemeTransparency == .18 &&
                migrated.GlobalSettings.ThemeColorArgb == 0xFF102030u &&
                migrated.GlobalSettings.ThemeBlurStrength == .25 &&
                migrated.GlobalSettings.SettingsThemeColorArgb == 0xFF405060u &&
                migrated.GlobalSettings.SettingsThemeBlurStrength == 1.35 &&
                migrated.GlobalSettings.NoteTheme == NoteTheme.RainBlue,
            "Schema 12 迁移没有保留双目标颜色、透明度与模糊强度。");
        using (JsonDocument migratedJson = JsonDocument.Parse(await File.ReadAllTextAsync(legacyPath)))
        {
            JsonElement global = migratedJson.RootElement.GetProperty("GlobalSettings");
            Require(!global.TryGetProperty("Material", out _) &&
                    !global.TryGetProperty("SettingsThemeMaterial", out _),
                "Schema 13 写回仍包含已删除的材质字段。");
        }
        migrated.GlobalSettings.ThemeTransparency = .12;
        migrated.GlobalSettings.SettingsThemeTransparency = .87;
        await legacyStore.SaveAsync(migrated);
        AppStateV2 loadedAgain = await legacyStore.LoadAsync();
        Require(loadedAgain.GlobalSettings.ThemeTransparency == .12 &&
                loadedAgain.GlobalSettings.SettingsThemeTransparency == .87,
            "Schema 13 后续加载重复重置透明度。");
    }
    finally
    {
        try { Directory.Delete(tempRoot, recursive: true); }
        catch { }
    }

    Console.WriteLine("PASS: theme material depth");
    return;
}
if (args is ["--theme-targets"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-theme-targets-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        ThemeValues legacyTheme = new(0xFF123456, .42);
        string legacyPath = Path.Combine(root, "schema-7.json");
        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 7,
            GlobalSettings = new
            {
                ThemeColorArgb = legacyTheme.ColorArgb,
                Material = 3,
                ThemeTransparency = legacyTheme.Transparency
            },
            Organizers = Array.Empty<object>()
        }));
        AppStateV2 migrated = await new StateStore(legacyPath).LoadAsync();
        ThemeValues migratedLegacyTheme = new(
            legacyTheme.ColorArgb,
            GlobalSettings.DefaultThemeTransparency,
            GlobalSettings.DefaultThemeBlurStrength);
        Require(migrated.SchemaVersion == 13 &&
                migrated.GlobalSettings.GetTheme(ThemeTarget.Organizer) == migratedLegacyTheme &&
                migrated.GlobalSettings.GetTheme(ThemeTarget.Settings) == migratedLegacyTheme,
            "Schema 7 主题没有同时迁移到设置界面和收纳窗。");

        var settings = new GlobalSettings();
        ThemeValues organizerTheme = new(0xFF203040, .2);
        ThemeValues settingsTheme = new(0xFF506070, .6);
        settings.SetTheme(ThemeTarget.Organizer, organizerTheme);
        settings.SetTheme(ThemeTarget.Settings, settingsTheme);
        Require(settings.GetTheme(ThemeTarget.Organizer) == organizerTheme &&
                settings.GetTheme(ThemeTarget.Settings) == settingsTheme,
            "修改一个主题目标意外改变了另一个目标。");

        string roundTripPath = Path.Combine(root, "round-trip.json");
        var store = new StateStore(roundTripPath);
        await store.SaveAsync(new AppStateV2 { GlobalSettings = settings });
        AppStateV2 reloaded = await store.LoadAsync();
        Require(reloaded.GlobalSettings.GetTheme(ThemeTarget.Organizer) == organizerTheme &&
                reloaded.GlobalSettings.GetTheme(ThemeTarget.Settings) == settingsTheme,
            "设置界面和收纳窗主题没有分别保存并重载。");

        AppStateV2 normalized = StateStore.Normalize(new AppStateV2
        {
            GlobalSettings = new GlobalSettings
            {
                ThemeColorArgb = 0x00112233,
                ThemeTransparency = double.NaN,
                SettingsThemeColorArgb = 0x00445566,
                SettingsThemeTransparency = 2
            }
        });
        Require(normalized.GlobalSettings.GetTheme(ThemeTarget.Organizer) ==
                    new ThemeValues(0xFF112233, GlobalSettings.DefaultThemeTransparency) &&
                normalized.GlobalSettings.GetTheme(ThemeTarget.Settings) ==
                    new ThemeValues(0xFF445566, GlobalSettings.MaximumThemeTransparency),
            "两套主题的非法值没有分别归一化。");

        var work = new NativeMethods.RECT { Left = 0, Top = 0, Right = 2000, Bottom = 1500 };
        var display = new DisplayInfo("theme-title", work, work, 1.5);
        var compact = new NativeMethods.RECT { Left = 0, Top = 0, Right = 120, Bottom = 80 };
        NativeMethods.RECT expanded = DisplayPlacementService.CalculateExpandedBounds(
            compact,
            display,
            new OrganizerLayout { Rows = 2, Columns = 2 },
            canvasScale: 1,
            manualCanvasBaseWidthDip: 420,
            manualCanvasBaseHeightDip: 300);
        NativeMethods.RECT expandedWork = DisplayPlacementService.GetExpandedWorkArea(display);
        int titleHeightPx = (int)Math.Round(DisplayPlacementService.ExpandedTitleBandDip * display.Scale);
        int panelWidthPx = (int)Math.Round(420 * display.Scale);
        int panelHeightPx = (int)Math.Round(300 * display.Scale);
        Require(expanded.Width == panelWidthPx && expanded.Height - titleHeightPx == panelHeightPx &&
                expanded.Left >= expandedWork.Left && expanded.Top >= expandedWork.Top &&
                expanded.Right <= expandedWork.Right && expanded.Bottom <= expandedWork.Bottom,
            "普通展开外框没有保留面板尺寸、包含 56 DIP 标题带并限制在工作区内。");

        var stationLayout = new OrganizerLayout { Rows = 2, Columns = 3 };
        const double stationItemScale = .8;
        double effectiveStationScale = Math.Min(
            stationItemScale,
            DisplayPlacementService.CalculateMaximumStationItemScale(display, stationLayout));
        (double stationCellWidthDip, double stationCellHeightDip) =
            DisplayPlacementService.CalculateRequiredStationCellSizeDip(effectiveStationScale);
        int expectedStationWidth = Math.Min(display.Work.Width, Math.Max(1, (int)Math.Round((
            stationCellWidthDip * stationLayout.Columns +
            DisplayPlacementService.ItemGapDip * (stationLayout.Columns - 1) +
            DisplayPlacementService.StationSideInsetDip * 2) * display.Scale)));
        int expectedStationHeight = Math.Min(display.Work.Height, Math.Max(1, (int)Math.Round((
            stationCellHeightDip * stationLayout.Rows +
            DisplayPlacementService.ItemGapDip * (stationLayout.Rows - 1) +
            DisplayPlacementService.StationTopInsetDip +
            DisplayPlacementService.StationBottomInsetDip) * display.Scale)));
        NativeMethods.RECT station = DisplayPlacementService.CalculateStationBounds(
            display,
            OrganizerDockEdge.Left,
            stationLayout,
            canvasScale: 1,
            itemScale: stationItemScale);
        Require(station.Width == expectedStationWidth && station.Height == expectedStationHeight,
            "中转站展开尺寸错误地增加了普通收纳窗标题带。");

        Console.WriteLine("PASS: theme targets");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--unified-theme"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-unified-theme-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var defaults = new GlobalSettings();
        Require(defaults.ThemeColorArgb == 0xFFE2E5E9 && defaults.ThemeTransparency == .35,
            "统一主题默认值错误。");

        string legacyPath = Path.Combine(root, "legacy.json");
        await File.WriteAllTextAsync(legacyPath,
            """{"SchemaVersion":6,"GlobalSettings":{"Theme":5,"NoteTheme":2},"Organizers":[{"Name":"A","ThemeOverride":3},{"Name":"B","ThemeOverride":4}]}""");
        AppStateV2 migrated = await new StateStore(legacyPath).LoadAsync();
        Require(migrated.SchemaVersion == 13 && migrated.GlobalSettings.ThemeColorArgb == 0xFFE2E5E9 &&
                migrated.GlobalSettings.ThemeTransparency == .35 &&
                migrated.GlobalSettings.NoteTheme == NoteTheme.SunYellow,
            "旧状态没有重置为 Schema 7 默认统一主题，或错误改变便签主题。");
        using (JsonDocument savedLegacy = JsonDocument.Parse(await File.ReadAllTextAsync(legacyPath)))
        {
            JsonElement rootElement = savedLegacy.RootElement;
            Require(!rootElement.GetProperty("GlobalSettings").TryGetProperty("Theme", out _),
                "迁移后仍保存旧全局 Theme 字段。");
            Require(rootElement.GetProperty("Organizers").EnumerateArray().All(item => !item.TryGetProperty("ThemeOverride", out _)),
                "迁移后仍保存 ThemeOverride 字段。");
        }

        string roundTripPath = Path.Combine(root, "round-trip.json");
        var store = new StateStore(roundTripPath);
        var state = new AppStateV2
        {
            GlobalSettings = new GlobalSettings
            {
                ThemeColorArgb = 0xFF112233
            }
        };
        foreach (double transparency in new[] { 0d, .35d, .9d })
        {
            state.GlobalSettings.ThemeTransparency = transparency;
            await store.SaveAsync(state);
            AppStateV2 reloaded = await store.LoadAsync();
            Require(reloaded.SchemaVersion == 13 && reloaded.GlobalSettings.ThemeColorArgb == 0xFF112233 &&
                    reloaded.GlobalSettings.ThemeTransparency == transparency,
                $"统一主题保存重载失败：{transparency}。");
        }

        AppStateV2 invalid = StateStore.Normalize(new AppStateV2
        {
            GlobalSettings = new GlobalSettings
            {
                ThemeColorArgb = 0x00112233,
                ThemeTransparency = double.NaN
            }
        });
        Require(invalid.GlobalSettings.ThemeColorArgb == 0xFF112233 &&
                invalid.GlobalSettings.ThemeTransparency == .35,
            "统一主题非法值回退错误。");
        Require(StateStore.Normalize(new AppStateV2
            {
                GlobalSettings = new GlobalSettings { ThemeTransparency = -1 }
            }).GlobalSettings.ThemeTransparency == 0 &&
            StateStore.Normalize(new AppStateV2
            {
                GlobalSettings = new GlobalSettings { ThemeTransparency = 2 }
            }).GlobalSettings.ThemeTransparency == 1,
            "统一主题透明度边界没有限制在 0–100%。");

        Require(ThemePalette.TintOpacity(new ThemeValues(0, 0)) == 1 &&
                Math.Abs(ThemePalette.TintOpacity(new ThemeValues(0, .35)) - .65f) < .0001f &&
                Math.Abs(ThemePalette.TintOpacity(new ThemeValues(0, .9)) - .1f) < .0001f,
            "背景色层不透明度没有使用 1 - 主题透明度。");
        Require(ThemePalette.ForegroundColor(new ThemeValues(0xFFF5F6F8, 0)).R < 128 &&
                ThemePalette.ForegroundColor(new ThemeValues(0xFF2F2D2D, 0)).R > 128,
            "浅色/深色背景没有选择可读前景。");
        Require(typeof(OrganizerDefinition).GetProperty("ThemeOverride") is null,
            "OrganizerDefinition 仍暴露单窗主题覆盖。");

        Console.WriteLine("PASS: unified theme");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--aug27-visual-fixes"])
{
    var failures = new List<string>();
    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-aug27-{Guid.NewGuid():N}");
    Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", root);
    Directory.CreateDirectory(root);
    try
    {
        AppPaths.EnsureCreated();
        string sourcePath = Path.Combine(root, "thumbnail-source.png");
        string cachePath = Path.Combine(AppPaths.IconCacheRoot, "thumbnail-cache.png");
        using (var thumbnailSource = new System.Drawing.Bitmap(120, 60))
        {
            using System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(thumbnailSource);
            graphics.Clear(System.Drawing.Color.Red);
            graphics.FillRectangle(System.Drawing.Brushes.Blue, 60, 0, 60, 60);
            thumbnailSource.Save(sourcePath, System.Drawing.Imaging.ImageFormat.Png);
        }

        try
        {
            var refresh = typeof(IconCacheService).GetMethod(
                "RefreshAsync",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (refresh is null)
            {
                failures.Add("IconCacheService.RefreshAsync 不存在。");
            }
            else
            {
                await (Task)refresh.Invoke(null, [sourcePath, cachePath])!;
                using var cached = new System.Drawing.Bitmap(cachePath);
                double ratio = (double)cached.Width / cached.Height;
                if (Math.Abs(ratio - 2d) > .02d)
                    failures.Add($"图片缓存未保留 2:1 宽高比，实际为 {cached.Width}x{cached.Height}。");

                System.Drawing.Color leftSample = cached.GetPixel(cached.Width / 4, cached.Height / 2);
                System.Drawing.Color rightSample = cached.GetPixel(cached.Width * 3 / 4, cached.Height / 2);
                if (leftSample.R < 200 || leftSample.G > 80 || leftSample.B > 80 ||
                    rightSample.B < 200 || rightSample.R > 80 || rightSample.G > 80)
                {
                    failures.Add($"图片缓存未保留红/蓝原图内容，采样为左({leftSample.R},{leftSample.G},{leftSample.B})、右({rightSample.R},{rightSample.G},{rightSample.B})。");
                }
            }

            var buildCacheIdentity = typeof(IconCacheService).GetMethod(
                "BuildCacheIdentity",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (buildCacheIdentity is null)
            {
                failures.Add("IconCacheService.BuildCacheIdentity 不存在。");
            }
            else
            {
                string firstIdentity = (string)buildCacheIdentity.Invoke(null, [sourcePath])!;
                string repeatedIdentity = (string)buildCacheIdentity.Invoke(null, [sourcePath])!;
                File.SetLastWriteTimeUtc(sourcePath, File.GetLastWriteTimeUtc(sourcePath).AddSeconds(2));
                string changedIdentity = (string)buildCacheIdentity.Invoke(null, [sourcePath])!;
                if (!string.Equals(firstIdentity, repeatedIdentity, StringComparison.Ordinal))
                    failures.Add("未变化文件的图标缓存身份不稳定。");
                if (string.Equals(firstIdentity, changedIdentity, StringComparison.Ordinal))
                    failures.Add("文件元数据变化后图标缓存身份没有变化。");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"图片缓存真实刷新链执行失败：{ex.GetBaseException().Message}");
        }

        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("PASS: aug27 visual fixes");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}

if (args is ["--external-file-drop"])
{
    await ExternalFileDropProbe.RunAsync();
    return;
}
if (args is ["--external-file-drop-target", var effect])
{
    ExternalFileDropProbe.RunTarget(effect);
    return;
}
if (args is ["--aug26-fixes"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-aug26-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        Require(new GlobalSettings().Language == AppLanguage.ChineseSimplified,
            "新配置没有默认使用中文。");
        var exclusiveProperty = typeof(GlobalSettings).GetProperty("ExclusiveExpansion");
        Require(exclusiveProperty is not null && exclusiveProperty.GetValue(new GlobalSettings()) is true,
            "全局单窗展开开关不存在或没有默认开启。");

        string missingLanguagePath = Path.Combine(root, "missing-language.json");
        await File.WriteAllTextAsync(missingLanguagePath,
            """{"SchemaVersion":5,"GlobalSettings":{"Theme":0},"Organizers":[]}""");
        AppStateV2 missingLanguage = await new StateStore(missingLanguagePath).LoadAsync();
        Require(missingLanguage.GlobalSettings.Language == AppLanguage.ChineseSimplified,
            "缺失语言字段没有回退中文。");

        AppStateV2 explicitEnglish = StateStore.Normalize(new AppStateV2
        {
            GlobalSettings = new GlobalSettings { Language = AppLanguage.English }
        });
        AppStateV2 explicitJapanese = StateStore.Normalize(new AppStateV2
        {
            GlobalSettings = new GlobalSettings { Language = AppLanguage.Japanese }
        });
        Require(explicitEnglish.GlobalSettings.Language == AppLanguage.English &&
                explicitJapanese.GlobalSettings.Language == AppLanguage.Japanese,
            "显式保存的英文或日文被默认语言覆盖。");
        Require(StateStore.Normalize(new AppStateV2
            {
                GlobalSettings = new GlobalSettings { Language = (AppLanguage)999 }
            }).GlobalSettings.Language == AppLanguage.ChineseSimplified,
            "无效语言值没有回退中文。");

        var selector = typeof(OrganizerInteractionMath).GetMethod(
            "SelectDropOperation",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Require(selector is not null, "拖入操作选择器不存在。");
        var select = (DataPackageOperation operation) =>
            (DataPackageOperation)selector!.Invoke(null, [operation])!;
        Require(select(DataPackageOperation.Move | DataPackageOperation.Copy) == DataPackageOperation.Move,
            "同时支持移动和复制时没有优先移动。");
        Require(select(DataPackageOperation.Copy | DataPackageOperation.Link) == DataPackageOperation.Copy,
            "浏览器 Copy/Link 来源没有选择复制。");
        Require(select(DataPackageOperation.Link) == DataPackageOperation.None,
            "仅支持链接的来源不应被接收。");

        Console.WriteLine("PASS: aug26 focused fixes");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
    return;
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

string logicRoot = Path.Combine(Path.GetTempPath(), $"TuckPane-logic-{Guid.NewGuid():N}");
Environment.SetEnvironmentVariable("TUCKPANE_TEST_ROOT", logicRoot);
Directory.CreateDirectory(logicRoot);
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try { if (Directory.Exists(logicRoot)) Directory.Delete(logicRoot, recursive: true); }
    catch { }
};

Check(new AppStateV2().SchemaVersion == 8, "新状态版本不是 8。");
Check(new GlobalSettings().Language == AppLanguage.ChineseSimplified, "新配置没有默认使用中文。");
Check(new GlobalSettings().ExclusiveExpansion, "全局单窗展开没有默认开启。");
Check(!new GlobalSettings().CollapseOnOutsideClick, "窗口外点击收缩没有默认关闭。");
Check(!new GlobalSettings().ExpandOnHover, "鼠标悬浮展开没有默认关闭。");
Check(!new GlobalSettings().CollapseOnPointerLeave, "鼠标离开收缩没有默认关闭。");
Check(OrganizerInteractionMath.ShouldStartHoverExpand(
        enabled: true, station: false, expanded: false, animating: false, interactionActive: false),
    "空闲的普通收纳窗没有允许悬浮展开。");
Check(!OrganizerInteractionMath.ShouldStartHoverExpand(
        enabled: true, station: false, expanded: false, animating: false, interactionActive: true),
    "鼠标按下或长按拖动时仍然允许悬浮展开。");
Check(!OrganizerInteractionMath.ShouldStartHoverExpand(
        enabled: true, station: true, expanded: false, animating: false, interactionActive: false),
    "普通窗口悬浮状态机错误接管了中转站。");

string migrationRoot = Path.Combine(logicRoot, "Migration");
Directory.CreateDirectory(migrationRoot);
try
{
    string statePath = Path.Combine(migrationRoot, "state.json");
    await File.WriteAllTextAsync(statePath, """
        {
          "SchemaVersion": 3,
          "GlobalSettings": { "Theme": 0, "StartWithWindows": false, "Language": 2 },
          "Organizers": [
            {
              "Id": "33333333-3333-3333-3333-333333333333",
              "Name": "旧窗口",
              "PlacementMode": 1,
              "Layout": { "Mode": 0, "Rows": 3, "Columns": 3 },
              "ItemOrder": ["note:44444444444444444444444444444444"],
              "Notes": [
                {
                  "Id": "44444444-4444-4444-4444-444444444444",
                  "Name": "",
                  "Theme": 99,
                  "FontSize": 100,
                  "Placement": { "XDip": 10, "YDip": 20, "WidthDip": 10, "HeightDip": 10 }
                },
                {
                  "Id": "55555555-5555-5555-5555-555555555555",
                  "Name": "便签 1"
                }
              ]
            }
          ]
        }
        """);
    var migrationStore = new StateStore(statePath);
    AppStateV2 migrated = await migrationStore.LoadAsync();
    Check(migrated.SchemaVersion == 8 && migrated.GlobalSettings.Language == AppLanguage.Japanese &&
          !migrated.GlobalSettings.ExpandOnHover && !migrated.GlobalSettings.CollapseOnPointerLeave &&
          migrated.Organizers.Count == 1 && migrated.Organizers[0].Name == "旧窗口" &&
          migrated.Organizers[0].PlacementMode == OrganizerPlacementMode.Positioned &&
          migrated.Organizers[0].Notes.Count == 2 &&
          migrated.Organizers[0].Notes[0].Name == "便签 1" &&
          migrated.Organizers[0].Notes[1].Name == "便签 2" &&
          migrated.Organizers[0].Notes[0].Theme == NoteTheme.RainBlue &&
          migrated.Organizers[0].Notes[0].FontSize == 48 &&
          migrated.Organizers[0].Notes.All(note => !note.ShowRuledLines) &&
          migrated.Organizers[0].Notes[0].Placement is { WidthDip: 280, HeightDip: 220 },
        "版本 3 状态没有无损迁移到版本 5，或旧便签没有默认关闭横线背景。");

    migrated.GlobalSettings.Language = AppLanguage.Japanese;
    migrated.GlobalSettings.ExpandOnHover = true;
    migrated.GlobalSettings.CollapseOnPointerLeave = true;
    await migrationStore.SaveAsync(migrated);
    AppStateV2 reloaded = await migrationStore.LoadAsync();
    Check(reloaded.GlobalSettings.Language == AppLanguage.Japanese && reloaded.GlobalSettings.ExpandOnHover &&
          reloaded.GlobalSettings.CollapseOnPointerLeave,
        "版本 5 没有保留用户重新选择的语言、悬浮展开或鼠标离开收缩设置。");
}
finally
{
    Directory.Delete(migrationRoot, recursive: true);
}

var noteNames = new[] { "便签 1", "便签 3", "计划" };
Check(OrganizerNoteRules.CreateDefaultName(noteNames) == "便签 2", "便签默认名称没有复用最小空闲编号。");
Check(OrganizerNoteRules.IsNameAvailable(noteNames, " 计划 ") == false,
    "便签重命名没有阻止同一收纳窗内的重复名称。");
Check(OrganizerNoteRules.PlainTextToHtml("<计划>\r\n第二行") == "&lt;计划&gt;<br>第二行",
    "剪贴板文字没有按纯文本安全写入便签正文。");
Check(ShellDragService.RequiresNativeDrag(WidgetItemKind.Note) &&
      ShellDragService.RequiresNativeDrag(WidgetItemKind.File),
    "便签或真实文件没有进入共享外拖流程。");

string noteRoot = Path.Combine(logicRoot, "Notes");
var noteStore = new NoteStore(noteRoot);
Guid noteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
Guid copiedNoteId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
var noteDocument = new NoteDocument { Html = "<div>正文<img src=\"data:image/png;base64,AA==\"></div>" };
await noteStore.SaveAsync(noteId, noteDocument);
Check((await noteStore.LoadAsync(noteId)).Html == noteDocument.Html, "便签正文没有从独立文件往返保存。");
await noteStore.CopyAsync(noteId, copiedNoteId);
Check((await noteStore.LoadAsync(copiedNoteId)).Html == noteDocument.Html, "复制便签没有复制正文文件。");
await noteStore.DeleteAsync(noteId);
Check(!(await noteStore.ExistsAsync(noteId)) && await noteStore.ExistsAsync(copiedNoteId),
    "删除便签正文时影响了其他便签文件。");
Guid corruptNoteId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
await File.WriteAllTextAsync(Path.Combine(noteRoot, $"{corruptNoteId:N}.json"), "not json");
bool corruptNoteRejected = false;
try { _ = await noteStore.LoadAsync(corruptNoteId); }
catch (InvalidDataException) { corruptNoteRejected = true; }
Check(corruptNoteRejected, "损坏的便签正文会被当成空白内容覆盖。");

var portableNote = new PortableNoteDocument
{
    Format = "TuckPane.Note",
    Version = 1,
    Theme = NoteTheme.WheatPaper,
    FontSize = 17,
    ShowRuledLines = true,
    Placement = new PortableNotePlacement
    {
        MonitorDevice = "DISPLAY-2",
        XDip = 120,
        YDip = 80,
        WidthDip = 420,
        HeightDip = 360
    },
    Html = "<div>第一行</div><div>第二行<img src=\"data:image/png;base64,AA==\"></div>"
};
string portablePath = await noteStore.CreatePortableStagingAsync("会议记录", portableNote);
PortableNoteDocument portableRoundTrip = await noteStore.LoadPortableAsync(portablePath);
Check(Path.GetExtension(portablePath) == ".tucknote" &&
      Path.GetFullPath(portablePath).StartsWith(
          Path.GetFullPath(AppPaths.NoteStagingRoot) + Path.DirectorySeparatorChar,
          StringComparison.OrdinalIgnoreCase) &&
      portableRoundTrip.Format == "TuckPane.Note" && portableRoundTrip.Version == 1 &&
      portableRoundTrip.Theme == NoteTheme.WheatPaper && portableRoundTrip.FontSize == 17 &&
      portableRoundTrip.ShowRuledLines &&
      portableRoundTrip.Placement is { MonitorDevice: "DISPLAY-2", XDip: 120, YDip: 80, WidthDip: 420, HeightDip: 360 } &&
      portableRoundTrip.Html == portableNote.Html,
    "便携便签没有按 UTF-8 JSON v1 在隔离暂存目录中完整往返。");
byte[] portableBytes = await File.ReadAllBytesAsync(portablePath);
using (System.Text.Json.JsonDocument portableJson = System.Text.Json.JsonDocument.Parse(portableBytes))
{
    string[] propertyNames = portableJson.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
    Check(!portableBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) &&
          propertyNames.SequenceEqual(["format", "version", "theme", "fontSize", "showRuledLines", "placement", "html"]),
        "便携便签不是无 BOM UTF-8 或没有使用固定的 JSON v1 字段。");
}

portableRoundTrip.Html = "<div>原子更新后的正文</div>";
await noteStore.SavePortableAsync(portablePath, portableRoundTrip);
Check((await noteStore.LoadPortableAsync(portablePath)).Html == portableRoundTrip.Html &&
      !Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(portablePath)!)
          .Any(path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)),
    "便携便签没有通过同目录临时文件完成原子更新。");

string movedPortablePath = Path.Combine(Path.GetDirectoryName(portablePath)!, "moved.tucknote");
File.Move(portablePath, movedPortablePath);
bool missingPortableRefused = false;
try { await noteStore.SavePortableAsync(portablePath, portableRoundTrip); }
catch (FileNotFoundException) { missingPortableRefused = true; }
catch (IOException) { missingPortableRefused = true; }
Check(missingPortableRefused && !File.Exists(portablePath) && File.Exists(movedPortablePath),
    "外部移动便携便签后，保存操作在旧路径重建了文件。");

string invalidPortablePath = Path.Combine(logicRoot, "invalid.tucknote");
string[] invalidPortableDocuments =
[
    "not json",
    """{"format":"TuckPane.Note","version":2,"theme":0,"fontSize":14,"showRuledLines":false,"placement":null,"html":""}""",
    """{"format":"Other.Note","version":1,"theme":0,"fontSize":14,"showRuledLines":false,"placement":null,"html":""}""",
    """{"format":"TuckPane.Note","version":1,"theme":99,"fontSize":14,"showRuledLines":false,"placement":null,"html":""}""",
    """{"format":"TuckPane.Note","version":1,"theme":0,"fontSize":7,"showRuledLines":false,"placement":null,"html":""}""",
    """{"format":"TuckPane.Note","version":1,"theme":0,"fontSize":14,"showRuledLines":false,"placement":{"monitorDevice":"","xDip":0,"yDip":0,"widthDip":279,"heightDip":300},"html":""}""",
    """{"format":"TuckPane.Note","version":1,"theme":0,"fontSize":14,"placement":null,"html":""}""",
    """{"format":"TuckPane.Note","version":1,"theme":0,"fontSize":14,"showRuledLines":false,"placement":null,"html":"","extra":true}"""
];
foreach (string invalidPortableDocument in invalidPortableDocuments)
{
    await File.WriteAllTextAsync(invalidPortablePath, invalidPortableDocument, new System.Text.UTF8Encoding(false));
    bool rejected = false;
    try { _ = await noteStore.LoadPortableAsync(invalidPortablePath); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, $"损坏或不兼容的便携便签未被严格拒绝：{invalidPortableDocument}");
}

await using (FileStream oversizedPortable = new(invalidPortablePath, FileMode.Create, FileAccess.Write, FileShare.None))
    oversizedPortable.SetLength(64L * 1024 * 1024 + 1);
bool oversizedPortableRejected = false;
try { _ = await noteStore.LoadPortableAsync(invalidPortablePath); }
catch (InvalidDataException) { oversizedPortableRejected = true; }
Check(oversizedPortableRejected, "超过 64 MiB 的便携便签未被读取边界拒绝。");
Check(!new PortableNoteDocument().ShowRuledLines,
    "旧便签进入便携格式时没有默认关闭横线背景。");
foreach ((string sourceName, string expectedName) in new[]
{
    ("", "便签.tucknote"),
    ("bad:name. ", "bad_name.tucknote"),
    ("CON", "_CON.tucknote"),
    ("Lpt1.txt", "_Lpt1.txt.tucknote")
})
{
    Check(NoteStore.CreatePortableFileName(sourceName) == expectedName,
        $"便携便签文件名净化错误：{sourceName} -> {NoteStore.CreatePortableFileName(sourceName)}");
}

string staleStaging = Path.Combine(AppPaths.NoteStagingRoot, "11111111111111111111111111111111");
string unrelatedStaging = Path.Combine(AppPaths.NoteStagingRoot, "keep-me");
string stagingSentinel = Path.Combine(AppPaths.NoteStagingRoot, "keep-me.txt");
Directory.CreateDirectory(staleStaging);
Directory.CreateDirectory(unrelatedStaging);
await File.WriteAllTextAsync(Path.Combine(staleStaging, "old.tucknote"), "old");
await File.WriteAllTextAsync(Path.Combine(unrelatedStaging, "sentinel.txt"), "keep");
await File.WriteAllTextAsync(stagingSentinel, "keep");
AppPaths.CleanupNoteStaging();
Check(!Directory.Exists(staleStaging) &&
      Directory.Exists(unrelatedStaging) && File.Exists(Path.Combine(unrelatedStaging, "sentinel.txt")) &&
      File.Exists(stagingSentinel),
    "启动暂存清理越过了 NoteStagingRoot 的直接 GUID 子目录边界。");
AppPaths.EnsureCreated();
string activeStaging = Path.Combine(AppPaths.NoteStagingRoot, "22222222222222222222222222222222");
Directory.CreateDirectory(activeStaging);
await File.WriteAllTextAsync(Path.Combine(activeStaging, "active.tucknote"), "active");
AppPaths.EnsureCreated();
Check(File.Exists(Path.Combine(activeStaging, "active.tucknote")),
    "重复 EnsureCreated 清理了当前进程正在使用的便签暂存文件。");

string newStoragePath = AppPaths.CreateStorageRelativePath(
    "Storage",
    Guid.Parse("22222222-2222-2222-2222-222222222222"));
Check(!Path.GetFileName(newStoragePath).Equals("Items", StringComparison.OrdinalIgnoreCase),
    "新建默认目录仍包含末尾 Items 层。");

string customStorage = Path.Combine(logicRoot, "SelectedStorage");
Directory.CreateDirectory(customStorage);
await File.WriteAllTextAsync(Path.Combine(customStorage, "existing.txt"), "existing");
Check(AppPaths.ValidateCustomStoragePath(customStorage) == Path.GetFullPath(customStorage),
    "手选目录没有被直接作为最终存储目录。");
Check(new StorageService(customStorage, createIfMissing: false).ReadItems().Count == 1,
    "手选目录的已有顶层内容没有直接显示。");
Check(AppPaths.PathsOverlap(customStorage, Path.Combine(customStorage, "Child")),
    "父子收纳目录重叠没有被识别。");
Check(!AppPaths.PathsOverlap(customStorage, Path.Combine(logicRoot, "Sibling")),
    "无关目录被错误判定为重叠。");
bool rejectedProtectedPath = false;
try { _ = AppPaths.ValidateCustomStoragePath(logicRoot); }
catch (InvalidOperationException) { rejectedProtectedPath = true; }
Check(rejectedProtectedPath, "危险上级目录没有被拒绝。");

var oldStorageState = new AppStateV2
{
    Organizers = [new OrganizerDefinition { StorageRelativePath = Path.Combine("Windows", "Legacy-11111111", "Items") }]
};
StateStore.Normalize(oldStorageState);
Check(Path.GetFileName(oldStorageState.Organizers[0].StorageRelativePath).Equals("Items", StringComparison.OrdinalIgnoreCase),
    "旧版 Items 存储路径被意外迁移。");

TransferOutcome exportOutcome = await new StorageService(customStorage, createIfMissing: false)
    .ExportToDesktopAsync("Direct storage", null, CancellationToken.None);
Check(exportOutcome.Status == TransferStatus.Moved && !Directory.Exists(customStorage) &&
      exportOutcome.DestinationPath is not null && File.Exists(Path.Combine(exportOutcome.DestinationPath, "existing.txt")),
    "手选目录没有作为整个目录导出并删除原目录。");

string emptyCustomStorage = Path.Combine(logicRoot, "EmptySelectedStorage");
Directory.CreateDirectory(emptyCustomStorage);
TransferOutcome emptyExportOutcome = await new StorageService(
        emptyCustomStorage,
        createIfMissing: false,
        exportEmptyDirectory: true)
    .ExportToDesktopAsync("Empty direct storage", null, CancellationToken.None);
Check(emptyExportOutcome.Status == TransferStatus.Moved && !Directory.Exists(emptyCustomStorage) &&
      emptyExportOutcome.DestinationPath is not null && Directory.Exists(emptyExportOutcome.DestinationPath),
    "空的手选目录没有整体导出到桌面。");

string pasteSourceRoot = Path.Combine(logicRoot, "PasteSources");
string pasteStorageRoot = Path.Combine(logicRoot, "PasteStorage");
Directory.CreateDirectory(pasteSourceRoot);
string copiedFileSource = Path.Combine(pasteSourceRoot, "note.txt");
await File.WriteAllTextAsync(copiedFileSource, "copy-source");
string copiedFolderSource = Path.Combine(pasteSourceRoot, "Folder");
Directory.CreateDirectory(Path.Combine(copiedFolderSource, "Nested"));
await File.WriteAllTextAsync(Path.Combine(copiedFolderSource, "Nested", "inside.txt"), "inside");
var pasteStorage = new StorageService(pasteStorageRoot);
IReadOnlyList<TransferOutcome> copiedOutcomes = await pasteStorage.CopyBatchAsync(
    [copiedFileSource, copiedFolderSource],
    null,
    CancellationToken.None);
Check(copiedOutcomes.All(outcome => outcome.Status == TransferStatus.Copied) &&
      File.Exists(copiedFileSource) && Directory.Exists(copiedFolderSource) &&
      File.Exists(Path.Combine(pasteStorageRoot, "note.txt")) &&
      File.Exists(Path.Combine(pasteStorageRoot, "Folder", "Nested", "inside.txt")),
    "剪贴板复制导入没有保留源项目或递归复制文件夹。");
IReadOnlyList<TransferOutcome> duplicateCopy = await pasteStorage.CopyBatchAsync(
    [copiedFileSource],
    null,
    CancellationToken.None);
Check(duplicateCopy.Single().Status == TransferStatus.Copied && File.Exists(Path.Combine(pasteStorageRoot, "note 2.txt")),
    "复制导入重名时没有自动编号。");

string movedFileSource = Path.Combine(pasteSourceRoot, "cut.txt");
await File.WriteAllTextAsync(movedFileSource, "cut-source");
IReadOnlyList<TransferOutcome> movedOutcomes = await pasteStorage.ImportBatchAsync(
    [movedFileSource],
    null,
    CancellationToken.None);
Check(movedOutcomes.Single().Status == TransferStatus.Moved && !File.Exists(movedFileSource) &&
      File.Exists(Path.Combine(pasteStorageRoot, "cut.txt")),
    "剪切粘贴没有复用移动导入路径。");

string executableSource = Environment.ProcessPath ?? throw new InvalidOperationException("无法取得测试程序路径。");
IReadOnlyList<TransferOutcome> executablePaste = await pasteStorage.CopyBatchAsync(
    [executableSource],
    null,
    CancellationToken.None);
Check(executablePaste.Single().Status == TransferStatus.ShortcutCreated && File.Exists(executableSource) &&
      executablePaste.Single().DestinationPath is string shortcut && File.Exists(shortcut),
    "粘贴程序时没有保留程序本体并创建快捷方式。");

string createdFolder = pasteStorage.CreateUniqueFolder("New Folder");
string numberedFolder = pasteStorage.CreateUniqueFolder("New Folder");
Check(Path.GetFileName(createdFolder) == "New Folder" && Path.GetFileName(numberedFolder) == "New Folder 2",
    "新建文件夹重名时没有自动编号。");
foreach (string invalidName in new[] { "", "bad:name", "trailing.", "CON", "LPT1.txt" })
{
    bool rejected = false;
    try { _ = StorageService.ValidateNewFolderName(invalidName); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected, $"非法文件夹名称未被拒绝：{invalidName}");
}

using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    bool cancelledCopy = false;
    try { _ = await pasteStorage.CopyBatchAsync([copiedFileSource], null, cancelled.Token); }
    catch (OperationCanceledException) { cancelledCopy = true; }
    Check(cancelledCopy && !Directory.EnumerateFileSystemEntries(pasteStorageRoot)
            .Any(path => Path.GetFileName(path).StartsWith(".glassfolder-staging-", StringComparison.OrdinalIgnoreCase)),
        "复制取消后留下了临时文件。");
}

string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TuckPane.ico");
string shortcutRoot = Path.Combine(logicRoot, "Shortcut");
Directory.CreateDirectory(shortcutRoot);
string shortcutIconPath = Path.Combine(shortcutRoot, "TuckPane.ico");
File.Copy(iconPath, shortcutIconPath);
string internetShortcutPath = Path.Combine(shortcutRoot, "Steam.url");
await File.WriteAllTextAsync(internetShortcutPath, """
    [InternetShortcut]
    URL=steam://rungameid/431960
    IconFile=TuckPane.ico
    IconIndex=0
    """);
IconCacheService.IconSnapshot expectedIcon = IconCacheService.ExtractShellIconPixels(shortcutIconPath);
IconCacheService.IconSnapshot shortcutIcon = IconCacheService.ExtractShellIconPixels(internetShortcutPath);
long iconDifference = 0;
long iconRange = 0;
for (int index = 0; index < expectedIcon.Pixels.Length; index += 4)
{
    if (expectedIcon.Pixels[index + 3] == 0 && shortcutIcon.Pixels[index + 3] == 0) continue;
    for (int channel = 0; channel < 4; channel++)
    {
        iconDifference += Math.Abs(expectedIcon.Pixels[index + channel] - shortcutIcon.Pixels[index + channel]);
        iconRange += byte.MaxValue;
    }
}
double iconSimilarity = iconRange == 0 ? 0 : 1d - (double)iconDifference / iconRange;
Check(expectedIcon.Size == shortcutIcon.Size && iconSimilarity >= .95,
    $"Steam .url 没有使用声明图标，相似度仅 {iconSimilarity:F4}。");

string copyName = OrganizerInteractionMath.CreateCopyName(
    "学习",
    ["学习", "学习 - 副本", "学习 - 副本 (2)"],
    " - 副本");
Check(copyName == "学习 - 副本 (3)", "副本名称编号错误。");

var source = new OrganizerDefinition
{
    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
    Name = "学习",
    PlacementMode = OrganizerPlacementMode.Positioned,
    DockEdge = OrganizerDockEdge.Bottom,
    Layout = new OrganizerLayout { Rows = 4, Columns = 5 },
    CompactScale = 1.8,
    CanvasScale = .72,
    ItemScale = 1.15,
    NameScale = .9,
    ManualCanvasBaseWidthDip = 800,
    ManualCanvasBaseHeightDip = 600,
    Position = new WidgetPosition { MonitorDevice = "test" },
    StorageAbsolutePath = @"D:\source\Items",
    ItemOrder = ["one.txt"]
};
OrganizerDefinition copy = OrganizerInteractionMath.CopySettings(source, copyName);
Check(copy.Id != source.Id && copy.Name == copyName, "副本身份未重建。");
Check(copy.Layout.Rows == 4 && copy.Layout.Columns == 5,
    "外观设置未完整复制。");
Check(copy.DockEdge == OrganizerDockEdge.Bottom, "贴靠边设置未复制。");
Check(copy.ManualCanvasBaseWidthDip == 800 && copy.ManualCanvasBaseHeightDip == 600,
    "手动画布形状未复制。");
Check(copy.Position is null && copy.StorageAbsolutePath is null && copy.StorageRelativePath.Length == 0 && copy.ItemOrder.Count == 0,
    "副本错误复制了位置、目录或文件顺序。");

CanvasResizeEdge[] edges =
[
    CanvasResizeEdge.Left,
    CanvasResizeEdge.Top,
    CanvasResizeEdge.Right,
    CanvasResizeEdge.Bottom,
    CanvasResizeEdge.Left | CanvasResizeEdge.Top,
    CanvasResizeEdge.Right | CanvasResizeEdge.Top,
    CanvasResizeEdge.Left | CanvasResizeEdge.Bottom,
    CanvasResizeEdge.Right | CanvasResizeEdge.Bottom
];
foreach (CanvasResizeEdge edge in edges)
{
    double deltaX = edge.HasFlag(CanvasResizeEdge.Left) ? -40 : edge.HasFlag(CanvasResizeEdge.Right) ? 40 : 0;
    double deltaY = edge.HasFlag(CanvasResizeEdge.Top) ? -30 : edge.HasFlag(CanvasResizeEdge.Bottom) ? 30 : 0;
    double factor = OrganizerInteractionMath.CalculateResizeFactor(edge, deltaX, deltaY, 400, 300);
    Check(Math.Abs(factor - 1.2) < .0001, $"{edge} 缩放倍率错误：{factor}");
    double width = 400 * factor;
    double height = 300 * factor;
    Check(Math.Abs(width / height - 4d / 3d) < .0001, $"{edge} 未保持宽高比。");
}

(int left, int top, int roundedWidth, int roundedHeight) =
    OrganizerInteractionMath.CreateCenteredBounds(1000, 600, 487.3, 365.475);
Check(Math.Abs((left + roundedWidth / 2d) - 1000) <= .5 &&
      Math.Abs((top + roundedHeight / 2d) - 600) <= .5,
    "整数像素取整后的缩放中心误差超过 1 像素。");
Check(Math.Abs(roundedWidth - roundedHeight * 4d / 3d) <= 1,
    "整数像素取整后的宽高比误差超过 1 像素。");

Check(OrganizerInteractionMath.ApplyWheelSteps(1, 1, .5, 1.65) == 1.05, "滚轮放大步长错误。");
Check(OrganizerInteractionMath.ApplyWheelSteps(1, -1, .5, 1.65) == .95, "滚轮缩小步长错误。");
Check(OrganizerInteractionMath.ApplyWheelSteps(1.64, 1, .5, 1.65) == 1.65, "滚轮上限错误。");
Check(OrganizerInteractionMath.ApplyWheelSteps(.51, -1, .5, 1.65) == .5, "滚轮下限错误。");

var layout = new OrganizerLayout { Rows = 3, Columns = 3 };
(double minimumWidth, double minimumHeight) = DisplayPlacementService.CalculateMinimumExpandedSizeDip(layout, .5);
Check(DisplayPlacementService.CalculateMaximumItemScaleForExpandedSize(layout, minimumWidth, minimumHeight) == .5,
    "最小画布没有把内容比例限制为 50%。");
Check(DisplayPlacementService.CalculateMaximumItemScaleForExpandedSize(layout, minimumWidth * 2, minimumHeight * 2) > .5,
    "放大画布后内容比例上限没有提高。");

var layoutLimits = new AppStateV2
{
    Organizers =
    [
        new OrganizerDefinition
        {
            PlacementMode = OrganizerPlacementMode.Floating,
            Layout = new OrganizerLayout { Rows = 99, Columns = 1 }
        },
        new OrganizerDefinition
        {
            PlacementMode = OrganizerPlacementMode.Station,
            Layout = new OrganizerLayout { Rows = 99, Columns = 0 }
        },
        new OrganizerDefinition
        {
            PlacementMode = OrganizerPlacementMode.Station,
            DockEdge = OrganizerDockEdge.Left,
            Layout = new OrganizerLayout { Rows = 1, Columns = 99 }
        }
    ]
};
StateStore.Normalize(layoutLimits);
Check(layoutLimits.Organizers[0].Layout.Rows == 6 && layoutLimits.Organizers[0].Layout.Columns == 2,
    "普通窗口没有保持 2–6 行列限制。");
Check(layoutLimits.Organizers[1].Layout.Rows == 9 && layoutLimits.Organizers[1].Layout.Columns == 1 &&
      layoutLimits.Organizers[2].Layout.Rows == 1 && layoutLimits.Organizers[2].Layout.Columns == 9,
    "中转站没有使用 1–9 行列限制。");

var invalidPair = new AppStateV2
{
    Organizers = [new OrganizerDefinition { ManualCanvasBaseWidthDip = 800 }]
};
StateStore.Normalize(invalidPair);
Check(invalidPair.Organizers[0].ManualCanvasBaseWidthDip is null &&
      invalidPair.Organizers[0].ManualCanvasBaseHeightDip is null,
    "不完整的手动画布尺寸未被清理。");

var stationManualCanvas = new AppStateV2
{
    Organizers =
    [
        new OrganizerDefinition
        {
            PlacementMode = OrganizerPlacementMode.Station,
            ManualCanvasBaseWidthDip = 867.5,
            ManualCanvasBaseHeightDip = 2564.6
        }
    ]
};
StateStore.Normalize(stationManualCanvas);
Check(stationManualCanvas.Organizers[0].ManualCanvasBaseWidthDip is null &&
      stationManualCanvas.Organizers[0].ManualCanvasBaseHeightDip is null,
    "中转站仍然保留会破坏内容自适应的自由长宽比。");

var compactScaleLimits = new AppStateV2
{
    Organizers =
    [
        new OrganizerDefinition { Name = "悬浮下限", PlacementMode = OrganizerPlacementMode.Floating, CompactScale = .5 },
        new OrganizerDefinition { Name = "悬浮上限", PlacementMode = OrganizerPlacementMode.Floating, CompactScale = 4 },
        new OrganizerDefinition { Name = "定位下限", PlacementMode = OrganizerPlacementMode.Positioned, CompactScale = .5 },
        new OrganizerDefinition { Name = "定位上限", PlacementMode = OrganizerPlacementMode.Positioned, CompactScale = 4 },
        new OrganizerDefinition { Name = "定位旧值", PlacementMode = OrganizerPlacementMode.Positioned, CompactScale = 1.8 }
    ]
};
StateStore.Normalize(compactScaleLimits);
Check(compactScaleLimits.Organizers[0].CompactScale == 1.2, "悬浮入口下限不是 120%。");
Check(compactScaleLimits.Organizers[1].CompactScale == 3, "悬浮入口上限不是 300%。");
Check(compactScaleLimits.Organizers[2].CompactScale == 1.2, "定位入口下限不是 120%。");
Check(compactScaleLimits.Organizers[3].CompactScale == 1.8, "定位入口上限不是 180%。");
Check(compactScaleLimits.Organizers[4].CompactScale == 1.8, "旧定位入口 180% 没有保持不变。");

var organizerLimits = new AppStateV2
{
    Organizers = Enumerable.Range(0, 13)
        .Select(index => new OrganizerDefinition { Name = $"普通 {index}" })
        .Concat(Enum.GetValues<OrganizerDockEdge>().Select(edge => new OrganizerDefinition
        {
            Name = $"{edge} 中转站",
            PlacementMode = OrganizerPlacementMode.Station,
            DockEdge = edge
        }))
        .ToList()
};
StateStore.Normalize(organizerLimits);
Check(organizerLimits.Organizers.Count(item => item.PlacementMode != OrganizerPlacementMode.Station) == 12 &&
      organizerLimits.Organizers.Count(item => item.PlacementMode == OrganizerPlacementMode.Station) == 4,
    "12 个普通窗口和 4 个中转站没有使用独立上限。");

var duplicateStationEdge = new AppStateV2
{
    Organizers =
    [
        new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Station, DockEdge = OrganizerDockEdge.Left },
        new OrganizerDefinition { PlacementMode = OrganizerPlacementMode.Station, DockEdge = OrganizerDockEdge.Left }
    ]
};
StateStore.Normalize(duplicateStationEdge);
Check(duplicateStationEdge.Organizers.Count(item => item.PlacementMode == OrganizerPlacementMode.Station) == 1,
    "同一边保留了多个中转站。");

var stationDisplay = new DisplayInfo(
    "station-test",
    new NativeMethods.RECT { Left = 100, Top = 20, Right = 2020, Bottom = 1100 },
    new NativeMethods.RECT { Left = 100, Top = 60, Right = 2020, Bottom = 1100 },
    1);

var scaledStationDisplay = new DisplayInfo(
    "station-scaled-test",
    new NativeMethods.RECT { Left = 0, Top = 0, Right = 2400, Bottom = 1350 },
    new NativeMethods.RECT { Left = 0, Top = 0, Right = 2400, Bottom = 1300 },
    1.25);
NativeMethods.RECT centeredDialog = DisplayPlacementService.CalculateCenteredDialogBounds(scaledStationDisplay);
Check(centeredDialog.Width == 550 && centeredDialog.Height == 350 &&
      Math.Abs(centeredDialog.Left + centeredDialog.Width / 2d - 1200) <= .5 &&
      Math.Abs(centeredDialog.Top + centeredDialog.Height / 2d - 650) <= .5,
    "独立对话框没有按目标显示器 DPI 居中。");
var smallDialogDisplay = new DisplayInfo(
    "dialog-small-test",
    new NativeMethods.RECT { Left = 100, Top = 200, Right = 600, Bottom = 500 },
    new NativeMethods.RECT { Left = 100, Top = 200, Right = 600, Bottom = 500 },
    1);
NativeMethods.RECT clampedDialog = DisplayPlacementService.CalculateCenteredDialogBounds(smallDialogDisplay);
Check(clampedDialog.Left == 130 && clampedDialog.Top == 224 &&
      clampedDialog.Right == 570 && clampedDialog.Bottom == 476,
    "独立对话框没有保留 24 DIP 工作区边距。 ");

foreach (OrganizerDockEdge edge in Enum.GetValues<OrganizerDockEdge>())
{
    StationTransitionFrame start = StationTransitionMath.GetFrame(edge, 300, 500, 0, .8, reducedMotion: false);
    StationTransitionFrame middle = StationTransitionMath.GetFrame(edge, 300, 500, .5, .8, reducedMotion: false);
    StationTransitionFrame end = StationTransitionMath.GetFrame(edge, 300, 500, 1, .8, reducedMotion: false);
    Check(end.ClipLeft == 0 && end.ClipTop == 0 && end.ClipRight == 300 && end.ClipBottom == 500 &&
          end.TranslationX == 0 && end.TranslationY == 0 && end.Opacity == 1,
        $"{edge} 中转站展开终点不是完整画布。 ");
    Check(start.TranslationX == 0 || start.TranslationY == 0,
        $"{edge} 中转站仍包含斜向位移。 ");
    Check(edge switch
    {
        OrganizerDockEdge.Left => start.ClipLeft == 0 && start.ClipRight == .8 && middle.ClipRight > start.ClipRight && start.TranslationX < 0,
        OrganizerDockEdge.Top => start.ClipTop == 0 && start.ClipBottom == .8 && middle.ClipBottom > start.ClipBottom && start.TranslationY < 0,
        OrganizerDockEdge.Right => start.ClipRight == 300 && Math.Abs(start.ClipLeft - 299.2) < .0001 && middle.ClipLeft < start.ClipLeft && start.TranslationX > 0,
        _ => start.ClipBottom == 500 && Math.Abs(start.ClipTop - 499.2) < .0001 && middle.ClipTop < start.ClipTop && start.TranslationY > 0
    }, $"{edge} 中转站没有从所属边缘单轴揭开。 ");
    StationTransitionFrame reduced = StationTransitionMath.GetFrame(edge, 300, 500, .4, .8, reducedMotion: true);
    Check(reduced.ClipLeft == 0 && reduced.ClipTop == 0 && reduced.ClipRight == 300 && reduced.ClipBottom == 500 &&
          reduced.TranslationX == 0 && reduced.TranslationY == 0 && reduced.Opacity == .4,
        $"{edge} 减少动态效果仍包含位移或裁剪。 ");
}

var oneColumnStationLayout = new OrganizerLayout { Rows = 6, Columns = 1 };
NativeMethods.RECT oneColumnStation = DisplayPlacementService.CalculateStationBounds(
    scaledStationDisplay,
    OrganizerDockEdge.Right,
    oneColumnStationLayout,
    .1,
    .5);
NativeMethods.RECT twoColumnStation = DisplayPlacementService.CalculateStationBounds(
    scaledStationDisplay,
    OrganizerDockEdge.Right,
    new OrganizerLayout { Rows = 6, Columns = 2 },
    .1,
    .5);
Check(oneColumnStation.Width == 97 && twoColumnStation.Width == 178,
    $"侧边中转站没有按内容贴合：一列={oneColumnStation.Width}px，两列={twoColumnStation.Width}px。");
Check(oneColumnStation.Right == scaledStationDisplay.Work.Right,
    "一列中转站没有保持右侧贴边。");

NativeMethods.RECT enlargedStation = DisplayPlacementService.CalculateStationBounds(
    scaledStationDisplay,
    OrganizerDockEdge.Right,
    oneColumnStationLayout,
    .1,
    DisplayPlacementService.MaximumItemScale);
Check(enlargedStation.Width > oneColumnStation.Width && enlargedStation.Height > oneColumnStation.Height &&
      enlargedStation.Right == scaledStationDisplay.Work.Right && enlargedStation.Height <= scaledStationDisplay.Work.Height,
    "中转站内容放大后没有同步贴合外框或超出了工作区。");
NativeMethods.RECT restoredStation = DisplayPlacementService.CalculateStationBounds(
    scaledStationDisplay,
    OrganizerDockEdge.Right,
    oneColumnStationLayout,
    .1,
    .5);
Check(restoredStation.Width == oneColumnStation.Width && restoredStation.Height == oneColumnStation.Height,
    "中转站内容缩小后没有恢复内容贴合尺寸。");
NativeMethods.RECT legacyManualStation = DisplayPlacementService.CalculateStationBounds(
    scaledStationDisplay,
    OrganizerDockEdge.Right,
    oneColumnStationLayout,
    .35,
    .5,
    manualCanvasBaseWidthDip: 867.5,
    manualCanvasBaseHeightDip: 2564.6);
Check(legacyManualStation.Width == oneColumnStation.Width && legacyManualStation.Height == oneColumnStation.Height,
    "旧的中转站自由长宽比仍然覆盖内容自适应尺寸。");

var topNineColumnLayout = new OrganizerLayout { Rows = 1, Columns = 9 };
NativeMethods.RECT topNineColumnStation = DisplayPlacementService.CalculateStationBounds(
    scaledStationDisplay,
    OrganizerDockEdge.Top,
    topNineColumnLayout,
    .1,
    .5);
NativeMethods.RECT topNineColumnStationLargeItems = DisplayPlacementService.CalculateStationBounds(
    scaledStationDisplay,
    OrganizerDockEdge.Top,
    topNineColumnLayout,
    .1,
    DisplayPlacementService.MaximumItemScale);
Check(topNineColumnStation.Width == 750 && topNineColumnStation.Height == 98,
    $"顶部 1×9 中转站没有按单行内容贴合：{topNineColumnStation.Width}×{topNineColumnStation.Height}px。");
Check(topNineColumnStationLargeItems.Width > topNineColumnStation.Width &&
      topNineColumnStationLargeItems.Height > topNineColumnStation.Height,
    "顶部 1×9 中转站内容放大后外框没有同步贴合。");
(double stationCellWidth, double stationCellHeight) = DisplayPlacementService.CalculateItemCellSizeDip(
    582,
    54,
    topNineColumnLayout);
int fixedColumns = (int)Math.Floor((582 + DisplayPlacementService.ItemGapDip) /
    (stationCellWidth + DisplayPlacementService.ItemGapDip));
Check(stationCellWidth == stationCellHeight && fixedColumns == 9,
    "放大内容后顶部 1×9 中转站没有保持 9 个固定列。");
(double normalCellWidth, double normalCellHeight) = DisplayPlacementService.CalculateItemCellSizeDip(
    582,
    54,
    topNineColumnLayout);
Check(normalCellWidth == stationCellWidth && normalCellHeight == stationCellHeight,
    "相同网格的普通窗口与中转站使用了不同的固定单元格尺寸。");

Check(ShellDragService.ClassifyOutcome(false, 1) == ShellDragOutcome.ExternalCopied &&
      ShellDragService.ClassifyOutcome(false, 2) == ShellDragOutcome.ExternalMoved &&
      ShellDragService.ClassifyOutcome(false, 4) == ShellDragOutcome.ExternalLinked &&
      ShellDragService.ClassifyOutcome(true, 1) == ShellDragOutcome.DesktopRequested &&
      ShellDragService.ClassifyOutcome(false, 0) == ShellDragOutcome.Cancelled,
    "Shell 拖放没有按目标返回的复制、移动或链接效果分类。");
IntPtr packedRelayPoint = DragMessageRelay.PackClientPosition(new NativeMethods.POINT { X = -25, Y = 320 });
Check(unchecked((short)(packedRelayPoint.ToInt64() & 0xFFFF)) == -25 &&
      unchecked((short)((packedRelayPoint.ToInt64() >> 16) & 0xFFFF)) == 320,
    "Shell 拖放转发消息没有保留窗口外的真实客户区坐标。");

foreach (OrganizerDockEdge edge in Enum.GetValues<OrganizerDockEdge>())
{
    NativeMethods.RECT stationBounds = DisplayPlacementService.CalculateStationBounds(
        stationDisplay,
        edge,
        new OrganizerLayout { Rows = 3, Columns = 4 },
        1,
        1,
        position: null,
        manualCanvasBaseWidthDip: null,
        manualCanvasBaseHeightDip: null);
    Check(stationBounds.Left >= stationDisplay.Work.Left && stationBounds.Top >= stationDisplay.Work.Top &&
          stationBounds.Right <= stationDisplay.Work.Right && stationBounds.Bottom <= stationDisplay.Work.Bottom,
        $"{edge} 中转站超出工作区。");
    Check(edge switch
    {
        OrganizerDockEdge.Left => stationBounds.Left == stationDisplay.Work.Left &&
            Math.Abs(stationBounds.Top + stationBounds.Height / 2d - (stationDisplay.Work.Top + stationDisplay.Work.Height / 2d)) <= .5,
        OrganizerDockEdge.Top => stationBounds.Top == stationDisplay.Work.Top &&
            Math.Abs(stationBounds.Left + stationBounds.Width / 2d - (stationDisplay.Work.Left + stationDisplay.Work.Width / 2d)) <= .5,
        OrganizerDockEdge.Right => stationBounds.Right == stationDisplay.Work.Right &&
            Math.Abs(stationBounds.Top + stationBounds.Height / 2d - (stationDisplay.Work.Top + stationDisplay.Work.Height / 2d)) <= .5,
        _ => stationBounds.Bottom == stationDisplay.Work.Bottom &&
            Math.Abs(stationBounds.Left + stationBounds.Width / 2d - (stationDisplay.Work.Left + stationDisplay.Work.Width / 2d)) <= .5
    }, $"{edge} 中转站没有贴边居中。");
}

var quarterAnchor = new WidgetPosition
{
    MonitorDevice = "old-display",
    XDip = 250,
    YDip = 250,
    SavedWorkAreaWidthDip = 1000,
    SavedWorkAreaHeightDip = 1000
};
NativeMethods.RECT proportionalAnchor = DisplayPlacementService.CalculateStationAnchor(
    stationDisplay,
    OrganizerDockEdge.Right,
    quarterAnchor);
Check(proportionalAnchor.Left == stationDisplay.Work.Right - 1 &&
      proportionalAnchor.Top == stationDisplay.Work.Top + stationDisplay.Work.Height / 4,
    "中转站没有按原工作区比例恢复沿边位置。");
NativeMethods.RECT anchoredBounds = DisplayPlacementService.CalculateStationBounds(
    stationDisplay,
    OrganizerDockEdge.Right,
    new OrganizerLayout { Rows = 1, Columns = 1 },
    .1,
    .5,
    quarterAnchor);
Check(anchoredBounds.Right == stationDisplay.Work.Right &&
      Math.Abs(anchoredBounds.Top + anchoredBounds.Height / 2d - proportionalAnchor.Top) <= .5,
    "中转站画布没有围绕保存锚点贴边。");
NativeMethods.RECT verticalDrag = DisplayPlacementService.CalculateStationDraggedBounds(
    anchoredBounds,
    new NativeMethods.POINT { X = 1900, Y = 300 },
    new NativeMethods.POINT { X = 1200, Y = 5000 },
    stationDisplay,
    OrganizerDockEdge.Right);
Check(verticalDrag.Right == stationDisplay.Work.Right && verticalDrag.Bottom == stationDisplay.Work.Bottom &&
      verticalDrag.Width == anchoredBounds.Width,
    "右侧中转站拖动没有固定贴边、仅纵向移动并限制在工作区。");
NativeMethods.RECT horizontalDrag = DisplayPlacementService.CalculateStationDraggedBounds(
    anchoredBounds,
    new NativeMethods.POINT { X = 1900, Y = 300 },
    new NativeMethods.POINT { X = -5000, Y = 900 },
    stationDisplay,
    OrganizerDockEdge.Top);
Check(horizontalDrag.Left == stationDisplay.Work.Left && horizontalDrag.Top == stationDisplay.Work.Top &&
      horizontalDrag.Height == anchoredBounds.Height,
    "顶部中转站拖动没有固定贴边、仅横向移动并限制在工作区。");
WidgetPosition capturedAnchor = DisplayPlacementService.CaptureStationPosition(
    stationDisplay,
    OrganizerDockEdge.Right,
    anchoredBounds);
Check(capturedAnchor.MonitorDevice == stationDisplay.Device && capturedAnchor.SavedWorkAreaHeightDip == stationDisplay.Work.Height &&
      Math.Abs(capturedAnchor.YDip - (anchoredBounds.Top + anchoredBounds.Height / 2d - stationDisplay.Work.Top)) <= .5,
    "中转站沿边位置没有保存到现有 WidgetPosition。");
Check(DisplayPlacementService.GetDisplay("missing-display").Device == DisplayPlacementService.GetDisplay().Device,
    "缺失显示器没有回退到主显示器。");

var gridDisplay = new DisplayInfo(
    "test-grid",
    new NativeMethods.RECT { Left = 0, Top = 0, Right = 960, Bottom = 960 },
    new NativeMethods.RECT { Left = 0, Top = 0, Right = 960, Bottom = 960 },
    1);
var gridSnapshot = new DesktopGridSnapshot(gridDisplay, 96, 96, [], true);
DesktopGridPlacement smallPlacement = DesktopGridService.Find(gridSnapshot, [], null, 1.2)!;
DesktopGridPlacement defaultPlacement = DesktopGridService.Find(gridSnapshot, [], null, 1.56)!;
DesktopGridPlacement maximumPlacement = DesktopGridService.Find(gridSnapshot, [], null, 1.8)!;
Check(smallPlacement.CompactScale == 1.2 && defaultPlacement.CompactScale == 1.56 && maximumPlacement.CompactScale == 1.8,
    "定位网格没有使用请求的入口比例。");
Check(smallPlacement.Bounds.Width < defaultPlacement.Bounds.Width &&
      defaultPlacement.Bounds.Width < maximumPlacement.Bounds.Width &&
      smallPlacement.Bounds.Height < defaultPlacement.Bounds.Height &&
      defaultPlacement.Bounds.Height < maximumPlacement.Bounds.Height,
    "定位入口尺寸没有随比例递增。");
Check(maximumPlacement.Bounds.Width <= gridSnapshot.CellWidthPx &&
      maximumPlacement.Bounds.Height <= gridSnapshot.CellHeightPx,
    "定位入口超过了一个桌面网格。");
var tightGridSnapshot = new DesktopGridSnapshot(gridDisplay, 64, 64, [], true);
DesktopGridPlacement tightPlacement = DesktopGridService.Find(tightGridSnapshot, [], null, 1.2)!;
Check(tightPlacement.CompactScale < 1.2 &&
      tightPlacement.Bounds.Width <= tightGridSnapshot.CellWidthPx &&
      tightPlacement.Bounds.Height <= tightGridSnapshot.CellHeightPx,
    "极端桌面网格没有优先保持单格占用。");

Directory.Delete(logicRoot, recursive: true);
Console.WriteLine("TuckPane logic checks: PASS");
