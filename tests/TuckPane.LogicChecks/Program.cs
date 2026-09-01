using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using TuckPane;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using System.Text.Json;
using System.Xml.Linq;

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
        ContainerStationId = stationId,
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
    Require(applySource.Contains("ContainerStationId", StringComparison.Ordinal) &&
            applySource.Contains("RefreshContainedOrganizerItemsAsync", StringComparison.Ordinal),
        "名称 runtime 更新没有复用父 Station 刷新链。");

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
        Require(reloaded.SchemaVersion == 9 &&
                reloaded.GlobalSettings.DefaultStorageDirectory == normalizedDefaultRoot,
            "默认存储根目录没有按 Schema 9 持久化。 ");

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
        Require(migrated.SchemaVersion == 9 &&
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
            overStationDropTarget: false),
        "普通 Floating 收起拖动没有保留窗口对齐。");
    Require(
        !OrganizerInteractionMath.ShouldUseWindowAlignment(
            enabled: true,
            draggingExpanded: false,
            OrganizerPlacementMode.Floating,
            overStationDropTarget: true),
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

if (args is ["--station-organizer-reference"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    string root = Path.Combine(Path.GetTempPath(), $"TuckPane-station-organizer-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var defaultNames = new GlobalSettings();
        Require(
            defaultNames.ResolveCompactNameScale(OrganizerPlacementMode.Floating) == 1 &&
            defaultNames.ResolveCompactNameScale(OrganizerPlacementMode.Positioned) == 1 &&
            defaultNames.ResolveExpandedNameScale(OrganizerPlacementMode.Floating) == 1 &&
            defaultNames.ResolveExpandedNameScale(OrganizerPlacementMode.Positioned) == 1,
            "全局收起/展开名称比例默认值不是 100%。");
        var nameSettings = StateStore.Normalize(new AppStateV2
        {
            GlobalSettings = new GlobalSettings
            {
                UniformFloatingCompactNameScale = .72,
                UniformPositionedCompactNameScale = .88,
                ExpandedNameScale = .2
            }
        }).GlobalSettings;
        Require(
            nameSettings.ResolveCompactNameScale(OrganizerPlacementMode.Floating) == .72 &&
            nameSettings.ResolveCompactNameScale(OrganizerPlacementMode.Positioned) == .72 &&
            nameSettings.ResolveCompactNameScale(OrganizerPlacementMode.Station) == 1 &&
            nameSettings.ResolveExpandedNameScale(OrganizerPlacementMode.Floating) == .6 &&
            nameSettings.ResolveExpandedNameScale(OrganizerPlacementMode.Positioned) == .6 &&
            nameSettings.ResolveExpandedNameScale(OrganizerPlacementMode.Station) == 1,
            "全局收起/展开名称比例没有统一作用于悬浮和定位，或错误影响了 Station。");
        string legacyNamesPath = Path.Combine(root, "legacy-names.json");
        await File.WriteAllTextAsync(
            legacyNamesPath,
            """{"SchemaVersion":8,"GlobalSettings":{"UniformFloatingCompactNameScale":0.73,"UniformPositionedCompactNameScale":0.91},"Organizers":[]}""");
        GlobalSettings migratedNames = (await new StateStore(legacyNamesPath).LoadAsync()).GlobalSettings;
        Require(
            migratedNames.ResolveCompactNameScale(OrganizerPlacementMode.Floating) == .73 &&
            migratedNames.ResolveCompactNameScale(OrganizerPlacementMode.Positioned) == .73 &&
            migratedNames.ExpandedNameScale == 1,
            "旧状态没有沿用悬浮名称比例，或缺失的展开名称比例没有回退 100%。");

        Guid childId = Guid.NewGuid();
        Guid positionedId = Guid.NewGuid();
        Guid stationAId = Guid.NewGuid();
        Guid stationBId = Guid.NewGuid();
        var originalPosition = new WidgetPosition { MonitorDevice = "display", XDip = 21, YDip = 34 };
        var child = new OrganizerDefinition
        {
            Id = childId,
            Name = "child",
            PlacementMode = OrganizerPlacementMode.Floating,
            Position = originalPosition,
            CompactScale = 2.25,
            NameScale = .65,
            StorageAbsolutePath = Path.Combine(root, "child")
        };
        var positioned = new OrganizerDefinition { Id = positionedId, PlacementMode = OrganizerPlacementMode.Positioned };
        var stationA = new OrganizerDefinition
        {
            Id = stationAId,
            Name = "A",
            PlacementMode = OrganizerPlacementMode.Station,
            ItemScale = .75,
            ItemOrder = ["alpha.txt"]
        };
        var stationB = new OrganizerDefinition
        {
            Id = stationBId,
            Name = "B",
            PlacementMode = OrganizerPlacementMode.Station,
            DockEdge = OrganizerDockEdge.Left,
            ItemScale = 1.4
        };
        List<OrganizerDefinition> organizers = [child, positioned, stationA, stationB];
        string key = OrganizerInteractionMath.OrganizerItemKey(childId);

        Require(
            OrganizerInteractionMath.CanContainOrganizer(OrganizerPlacementMode.Floating, OrganizerPlacementMode.Station, childId, stationAId) &&
            OrganizerInteractionMath.CanContainOrganizer(OrganizerPlacementMode.Positioned, OrganizerPlacementMode.Station, positionedId, stationAId) &&
            !OrganizerInteractionMath.CanContainOrganizer(OrganizerPlacementMode.Station, OrganizerPlacementMode.Station, stationAId, stationBId) &&
            !OrganizerInteractionMath.CanContainOrganizer(OrganizerPlacementMode.Floating, OrganizerPlacementMode.Floating, childId, positionedId) &&
            !OrganizerInteractionMath.CanContainOrganizer(OrganizerPlacementMode.Floating, OrganizerPlacementMode.Station, childId, childId),
            "收纳窗来源/目标接受矩阵或自引用拒绝错误。");

        Require(OrganizerInteractionMath.PlaceOrganizerInStation(organizers, childId, stationAId, 1) &&
                OrganizerInteractionMath.PlaceOrganizerInStation(organizers, childId, stationAId, 1) &&
                child.ContainerStationId == stationAId && stationA.ItemOrder.SequenceEqual(["alpha.txt", key]) &&
                child.PlacementMode == OrganizerPlacementMode.Floating && ReferenceEquals(child.Position, originalPosition) &&
                child.CompactScale == 2.25 && child.NameScale == .65,
            "首次/重复拖入不幂等，或改变了来源模式、位置和入口缩放。");

        var projected = new WidgetItem(child.Name, child.StorageAbsolutePath!, key, WidgetItemKind.Organizer, organizerId: child.Id);
        Require(projected.Kind == WidgetItemKind.Organizer && projected.OrganizerId == child.Id &&
                OrganizerInteractionMath.TryParseOrganizerItemKey(projected.RelativeName, out Guid parsedId) && parsedId == child.Id &&
                stationA.ItemScale == .75 && child.CompactScale == 2.25,
            "中转站收纳窗投影、稳定顺序键或父子缩放解耦错误。");

        Require(OrganizerInteractionMath.PlaceOrganizerInStation(organizers, childId, stationBId, 0) &&
                child.ContainerStationId == stationBId && !stationA.ItemOrder.Contains(key) &&
                stationB.ItemOrder.Count(item => item.Equals(key, StringComparison.OrdinalIgnoreCase)) == 1,
            "跨中转站移动没有保持唯一归属和唯一顺序键。");

        (int Left, int Top, int Width, int Height) centered = OrganizerInteractionMath.CreateCenteredBounds(500, 400, 100, 60);
        Require(OrganizerInteractionMath.DetachOrganizerFromStation(organizers, childId) == stationBId &&
                child.ContainerStationId is null && organizers.All(item => !item.ItemOrder.Contains(key)) &&
                centered == (450, 370, 100, 60),
            "拖出没有解除归属或按释放点生成桌面候选位置。");
        child.Position = new WidgetPosition { MonitorDevice = "desktop", XDip = centered.Left, YDip = centered.Top };
        string detachedPath = Path.Combine(root, "detached-state.json");
        var detachedStore = new StateStore(detachedPath);
        await detachedStore.SaveAsync(new AppStateV2 { GlobalSettings = nameSettings, Organizers = organizers });
        AppStateV2 detachedReloaded = await detachedStore.LoadAsync();
        OrganizerDefinition detachedChild = detachedReloaded.Organizers.Single(item => item.Id == childId);
        Require(detachedChild.ContainerStationId is null &&
                detachedChild.Position is { MonitorDevice: "desktop", XDip: 450, YDip: 370 } &&
                detachedReloaded.Organizers.Count == organizers.Count,
            "拖回桌面的解除归属、位置或收纳窗定义没有完整保存。");

        OrganizerInteractionMath.PlaceOrganizerInStation(organizers, childId, stationBId, 0);
        var store = new StateStore(Path.Combine(root, "state.json"));
        await store.SaveAsync(new AppStateV2 { GlobalSettings = nameSettings, Organizers = organizers });
        AppStateV2 reloaded = await store.LoadAsync();
        OrganizerDefinition reloadedChild = reloaded.Organizers.Single(item => item.Id == childId);
        OrganizerDefinition reloadedStation = reloaded.Organizers.Single(item => item.Id == stationBId);
        Guid missingId = Guid.NewGuid();
        var invalidChild = new OrganizerDefinition { Id = Guid.NewGuid(), ContainerStationId = missingId };
        var invalidStation = new OrganizerDefinition
        {
            Id = Guid.NewGuid(),
            PlacementMode = OrganizerPlacementMode.Station,
            ItemOrder = [OrganizerInteractionMath.OrganizerItemKey(invalidChild.Id), OrganizerInteractionMath.OrganizerItemKey(missingId)]
        };
        StateStore.Normalize(new AppStateV2 { Organizers = [invalidChild, invalidStation] });
        Require(reloaded.GlobalSettings.UniformFloatingCompactNameScale == .72 &&
                reloaded.GlobalSettings.ExpandedNameScale == .6 &&
                reloadedChild.ContainerStationId == stationBId && reloadedStation.ItemOrder.Contains(key) &&
                invalidChild.ContainerStationId is null && invalidStation.ItemOrder.All(item =>
                    !OrganizerInteractionMath.TryParseOrganizerItemKey(item, out _)),
            "保存重载、悬空归属或非法收纳窗顺序键归一化错误。");

        Console.WriteLine("PASS: station organizer reference");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
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

        IconCacheService.IconSnapshot expected = IconCacheService.ExtractShellIconPixels(sourceIconPath);
        IconCacheService.IconSnapshot actual = IconCacheService.ExtractShellIconPixels(outerShortcutPath);
        double fixtureSimilarity = Similarity(expected, actual);
        Require(fixtureSimilarity >= .95,
            $"二级快捷方式没有解析到最终图标，像素相似度仅 {fixtureSimilarity:F4}。");

        if (args.Length == 2)
        {
            string actualShortcutPath = Path.GetFullPath(args[1]);
            Require(File.Exists(actualShortcutPath), $"真实快捷方式不存在：{actualShortcutPath}");
            (string expectedPath, _) = ResolveExpectedShortcutIcon(actualShortcutPath);
            expected = IconCacheService.ExtractShellIconPixels(expectedPath);
            actual = IconCacheService.ExtractShellIconPixels(actualShortcutPath);
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

    Require(Enum.GetValues<ThemeMaterial>().SequenceEqual(
            [ThemeMaterial.Acrylic, ThemeMaterial.Glass, ThemeMaterial.Matte]),
        "可选材质不是 Acrylic、Glass、Matte 三种。");

    AppStateV2 legacyFrosted = StateStore.Normalize(new AppStateV2
    {
        GlobalSettings = new GlobalSettings { Material = (ThemeMaterial)2 }
    });
    Require(legacyFrosted.GlobalSettings.Material == ThemeMaterial.Matte,
        "旧 FrostedGlass 数值 2 没有迁移到 Matte。");

    Console.WriteLine("PASS: theme material removal");
    return;
}

if (args is ["--theme-material-depth"])
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    ThemeEffectParameters acrylic = ThemePalette.Effect(ThemeMaterial.Acrylic);
    ThemeEffectParameters glass = ThemePalette.Effect(ThemeMaterial.Glass);
    ThemeEffectParameters matte = ThemePalette.Effect(ThemeMaterial.Matte);
    Require(acrylic == new ThemeEffectParameters(30, 1f, .80f, 0, .90f),
        "亚克力没有使用高白色覆盖的无颗粒参数。");
    Require(glass == new ThemeEffectParameters(10, 2f, .90f, 0, .06f),
        "玻璃材质参数被意外改变。");
    Require(matte == new ThemeEffectParameters(18, .75f, .92f, .035f, .50f),
        "磨砂没有使用 3.5% 的有效颗粒参数。");

    var blueSettings = new GlobalSettings { ThemeColorArgb = 0xFF0055FF };
    ThemeValues blueTheme = blueSettings.GetTheme(ThemeTarget.Organizer);
    Windows.UI.Color acrylicTint = ThemePalette.MaterialTintColor(blueTheme, ThemeMaterial.Acrylic);
    Require(acrylicTint.R == 166 && acrylicTint.G == 196 && acrylicTint.B == 255,
        "亚克力没有按 65% 白色 + 35% 主题色混合。");
    Require(ThemePalette.MaterialTintColor(blueTheme, ThemeMaterial.Glass) == ThemePalette.SurfaceColor(blueTheme) &&
            ThemePalette.MaterialTintColor(blueTheme, ThemeMaterial.Matte) == ThemePalette.SurfaceColor(blueTheme),
        "玻璃或磨砂错误使用了亚克力白色混合。");

    Require(ThemePalette.OuterEdgeThickness == 1.25f &&
            ThemePalette.InnerEdgeThickness == .75f &&
            ThemePalette.InnerEdgeInset == 1.5f,
        "双层边缘厚度或内缩参数错误。");
    foreach (ThemeMaterial material in Enum.GetValues<ThemeMaterial>())
    {
        Require(ThemePalette.OuterEdgeStops(material).Count >= 3 &&
                ThemePalette.InnerEdgeStops(material).Count >= 3,
            $"{material} 没有外边界和内反光两层参数。");
    }
    Require(ThemePalette.HasPrismaticEdge(ThemeMaterial.Glass) &&
            !ThemePalette.HasPrismaticEdge(ThemeMaterial.Acrylic) &&
            !ThemePalette.HasPrismaticEdge(ThemeMaterial.Matte),
        "彩色棱镜边缘没有仅限玻璃材质。");
    foreach (ThemeMaterial material in Enum.GetValues<ThemeMaterial>())
    {
        float scale = ThemePalette.Effect(material).TintOpacityScale;
        float opaque = ThemePalette.EffectiveTintOpacity(new ThemeValues(0xFF112233, material, 0), material);
        float middle = ThemePalette.EffectiveTintOpacity(new ThemeValues(0xFF112233, material, .35), material);
        float maximum = ThemePalette.EffectiveTintOpacity(new ThemeValues(0xFF112233, material, .9), material);
        Require(Math.Abs(opaque - 1) < .0001f &&
                Math.Abs(middle - .65f * scale) < .0001f &&
                Math.Abs(maximum - .1f * scale) < .0001f &&
                opaque > middle && middle > maximum,
            $"{material} 没有在 0% 时完全不透明，或非零透明度没有保留材质缩放。");
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
        ThemeValues legacyTheme = new(0xFF123456, ThemeMaterial.Matte, .42);
        string legacyPath = Path.Combine(root, "schema-7.json");
        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 7,
            GlobalSettings = new
            {
                ThemeColorArgb = legacyTheme.ColorArgb,
                Material = legacyTheme.Material,
                ThemeTransparency = legacyTheme.Transparency
            },
            Organizers = Array.Empty<object>()
        }));
        AppStateV2 migrated = await new StateStore(legacyPath).LoadAsync();
        Require(migrated.SchemaVersion == 8 &&
                migrated.GlobalSettings.GetTheme(ThemeTarget.Organizer) == legacyTheme &&
                migrated.GlobalSettings.GetTheme(ThemeTarget.Settings) == legacyTheme,
            "Schema 7 主题没有同时迁移到设置界面和收纳窗。");

        var settings = new GlobalSettings();
        ThemeValues organizerTheme = new(0xFF203040, ThemeMaterial.Glass, .2);
        ThemeValues settingsTheme = new(0xFF506070, ThemeMaterial.Matte, .6);
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
                Material = (ThemeMaterial)99,
                ThemeTransparency = double.NaN,
                SettingsThemeColorArgb = 0x00445566,
                SettingsThemeMaterial = (ThemeMaterial)(-1),
                SettingsThemeTransparency = 2
            }
        });
        Require(normalized.GlobalSettings.GetTheme(ThemeTarget.Organizer) ==
                    new ThemeValues(0xFF112233, ThemeMaterial.Acrylic, GlobalSettings.DefaultThemeTransparency) &&
                normalized.GlobalSettings.GetTheme(ThemeTarget.Settings) ==
                    new ThemeValues(0xFF445566, ThemeMaterial.Acrylic, GlobalSettings.MaximumThemeTransparency),
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
        Require(defaults.ThemeColorArgb == 0xFFE2E5E9 && defaults.Material == ThemeMaterial.Acrylic &&
                defaults.ThemeTransparency == .35,
            "统一主题默认值错误。");

        string legacyPath = Path.Combine(root, "legacy.json");
        await File.WriteAllTextAsync(legacyPath,
            """{"SchemaVersion":6,"GlobalSettings":{"Theme":5,"NoteTheme":2},"Organizers":[{"Name":"A","ThemeOverride":3},{"Name":"B","ThemeOverride":4}]}""");
        AppStateV2 migrated = await new StateStore(legacyPath).LoadAsync();
        Require(migrated.SchemaVersion == 8 && migrated.GlobalSettings.ThemeColorArgb == 0xFFE2E5E9 &&
                migrated.GlobalSettings.Material == ThemeMaterial.Acrylic && migrated.GlobalSettings.ThemeTransparency == .35 &&
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
                ThemeColorArgb = 0xFF112233,
                Material = ThemeMaterial.Matte
            }
        };
        foreach (double transparency in new[] { 0d, .35d, .9d })
        {
            state.GlobalSettings.ThemeTransparency = transparency;
            await store.SaveAsync(state);
            AppStateV2 reloaded = await store.LoadAsync();
            Require(reloaded.SchemaVersion == 8 && reloaded.GlobalSettings.ThemeColorArgb == 0xFF112233 &&
                    reloaded.GlobalSettings.Material == ThemeMaterial.Matte && reloaded.GlobalSettings.ThemeTransparency == transparency,
                $"统一主题保存重载失败：{transparency}。");
        }

        AppStateV2 invalid = StateStore.Normalize(new AppStateV2
        {
            GlobalSettings = new GlobalSettings
            {
                ThemeColorArgb = 0x00112233,
                Material = (ThemeMaterial)99,
                ThemeTransparency = double.NaN
            }
        });
        Require(invalid.GlobalSettings.ThemeColorArgb == 0xFF112233 &&
                invalid.GlobalSettings.Material == ThemeMaterial.Acrylic && invalid.GlobalSettings.ThemeTransparency == .35,
            "统一主题非法值回退错误。");
        Require(StateStore.Normalize(new AppStateV2
            {
                GlobalSettings = new GlobalSettings { ThemeTransparency = -1 }
            }).GlobalSettings.ThemeTransparency == 0 &&
            StateStore.Normalize(new AppStateV2
            {
                GlobalSettings = new GlobalSettings { ThemeTransparency = 2 }
            }).GlobalSettings.ThemeTransparency == .9,
            "统一主题透明度边界没有限制在 0–90%。");

        Require(ThemePalette.TintOpacity(new ThemeValues(0, default, 0)) == 1 &&
                Math.Abs(ThemePalette.TintOpacity(new ThemeValues(0, default, .35)) - .65f) < .0001f &&
                Math.Abs(ThemePalette.TintOpacity(new ThemeValues(0, default, .9)) - .1f) < .0001f,
            "背景色层不透明度没有使用 1 - 主题透明度。");
        Require(ThemePalette.ForegroundColor(new ThemeValues(0xFFF5F6F8, default, 0)).R < 128 &&
                ThemePalette.ForegroundColor(new ThemeValues(0xFF2F2D2D, default, 0)).R > 128,
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
