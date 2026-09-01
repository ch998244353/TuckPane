using TuckPane.Models;
using TuckPane.Services;
using TuckPane.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace TuckPane;

public sealed class AppHost : IDisposable
{
    private readonly StateStore _stateStore = new();
    private readonly DesktopGridService _desktopGrid = new();
    private readonly Dictionary<Guid, MainWindow> _windows = [];
    private readonly Dictionary<Guid, NoteWindow> _noteWindows = [];
    private readonly Dictionary<string, NoteWindow> _externalNoteWindows = new(StringComparer.OrdinalIgnoreCase);
    // ponytail: external note opens are rare; use per-path tasks only if parallel opens become measurable.
    private readonly SemaphoreSlim _externalNoteOpenGate = new(1, 1);
    private readonly HashSet<Guid> _trayHiddenNotes = [];
    private readonly HashSet<string> _trayHiddenExternalNotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly NoteStore _noteStore = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private TrayIconService? _tray;
    private DesktopIconGuardService? _desktopIconGuard;
    private MainWindow? _expandedWindow;
    private bool _transparencyNoticeShown;
    private bool _gridFallbackNoticeShown;
    private bool _exiting;
    private int _suspendOrganizerRelocation;

    public AppStateV2 State { get; private set; } = new();
    public TransferQueue TransferQueue { get; } = new();
    public ConsoleWindow Console { get; private set; } = null!;
    public IReadOnlyCollection<MainWindow> Windows => _windows.Values;

    // 在"桌面图标避让"把文件挪出收纳盒的短暂窗口内，抑制收纳盒自动贴网格重定位，
    // 避免收纳盒先在 4 秒修复 tick 里被搬走、之后不会自己归位。
    public bool ShouldSuspendOrganizerRelocation => Volatile.Read(ref _suspendOrganizerRelocation) > 0;

    public IDisposable SuspendOrganizerRelocation()
    {
        _ = Interlocked.Increment(ref _suspendOrganizerRelocation);
        return new RelocationSuspendScope(() => _ = Interlocked.Decrement(ref _suspendOrganizerRelocation));
    }

    private sealed class RelocationSuspendScope(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public async Task InitializeAsync(bool startup)
    {
        long startupStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        AppPaths.EnsureCreated();
        State = await _stateStore.LoadAsync();
        AppStrings.SetLanguage(State.GlobalSettings.Language);
        StartupService.Apply(State.GlobalSettings.StartWithWindows);
        AppLogger.Info($"启动：状态加载完成 {System.Diagnostics.Stopwatch.GetElapsedTime(startupStartedAt).TotalMilliseconds:0}ms。");

        Console = new ConsoleWindow(this);
        Console.InitializeHostWindow();
        Console.Activate();
        if (Console.CurrentBounds is { } consoleBounds)
        {
            AppLogger.Info($"启动：控制台 bounds={consoleBounds.X},{consoleBounds.Y} {consoleBounds.Width}x{consoleBounds.Height}px。");
        }
        _tray = new TrayIconService(Console.Hwnd, () => State.GlobalSettings.StartWithWindows, () => TransferQueue.IsActive, HandleTrayCommand);
        TransferQueue.StateChanged += (_, _) => Console.UpdateTransferState();
        bool showConsole = !startup && (State.Organizers.Count == 0 || State.GlobalSettings.ShowConsoleOnLaunch);
        if (!showConsole) Console.HideToTray();
        _desktopIconGuard = new DesktopIconGuardService(CollectOrganizerBounds, _dispatcher, SuspendOrganizerRelocation);
        _desktopIconGuard.Start();

        if (showConsole) await Console.WaitFirstRenderAsync();

        bool normalized = await Task.Run(() => NormalizePositionedPlacementsOnStartup() | NormalizeStationPlacementsOnStartup());
        AppLogger.Info($"启动：网格归一化完成 {System.Diagnostics.Stopwatch.GetElapsedTime(startupStartedAt).TotalMilliseconds:0}ms（变更={normalized}）。");
        if (normalized) await SaveStateAsync();
        foreach (OrganizerDefinition organizer in State.Organizers)
        {
            CreateWindow(organizer);
            await Task.Yield();
        }
        Console.RefreshAll();
        Console.SetStartupLoading(false);
        AppLogger.Info($"启动：全部收纳窗已创建 {System.Diagnostics.Stopwatch.GetElapsedTime(startupStartedAt).TotalMilliseconds:0}ms。");
        AppLogger.Info($"启动：控制台 chrome 重设 {Console.ChromeApplyCount} 次。");

        bool normalizedStations = NormalizeStationPlacementsOnStartup();
        if (normalizedStations) await SaveStateAsync();
    }

    public GlassTheme GetTheme(OrganizerDefinition organizer) => organizer.ThemeOverride ?? State.GlobalSettings.Theme;

    public async Task<OrganizerDefinition> CreateOrganizerAsync(OrganizerDefinition draft, string? storagePath = null)
    {
        if (draft.PlacementMode == OrganizerPlacementMode.Station)
        {
            if (State.Organizers.Any(item => item.PlacementMode == OrganizerPlacementMode.Station && item.DockEdge == draft.DockEdge))
                throw new InvalidOperationException(AppStrings.Get("StationEdgeOccupiedError"));
        }
        else if (State.Organizers.Count(item => item.PlacementMode != OrganizerPlacementMode.Station) >= OrganizerLimits.MaximumOrganizers)
        {
            throw new InvalidOperationException(AppStrings.Get("MaximumOrganizersError"));
        }
        Guid id = Guid.NewGuid();
        draft.Id = id;
        draft.Name = string.IsNullOrWhiteSpace(draft.Name) ? AppStrings.DefaultOrganizerName : draft.Name.Trim();
        draft.CreatedAtUtc = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            draft.StorageRelativePath = AppPaths.CreateStorageRelativePath(draft.Name, id);
            draft.StorageAbsolutePath = null;
        }
        else
        {
            string validatedParent = storagePath;
            draft.StorageRelativePath = string.Empty;
            draft.StorageAbsolutePath = ValidateStoragePath(storagePath);
        }

        DisplayInfo primary = DisplayPlacementService.GetDisplay();
        DisplayInfo selectedDisplay = DisplayPlacementService.GetDisplay(draft.Position?.MonitorDevice);
        NativeMethods.RECT bounds;
        if (draft.PlacementMode == OrganizerPlacementMode.Station)
        {
            bounds = DisplayPlacementService.CalculateStationAnchor(selectedDisplay, draft.DockEdge);
        }
        else if (draft.PlacementMode == OrganizerPlacementMode.Positioned)
        {
            DesktopGridPlacement? placement = FindPositionedPlacement(
                primary,
                desiredCenter: null,
                excludeId: id,
                draft.CompactScale);
            if (placement is null) throw new InvalidOperationException(AppStrings.Get("NoPrimaryGridError"));
            bounds = placement.Bounds;
        }
        else
        {
            int width = Math.Max(1, (int)Math.Round(OrganizerLimits.CompactWindowWidthDip * draft.CompactScale * primary.Scale));
            int height = Math.Max(1, (int)Math.Round(OrganizerLimits.CompactWindowHeightDip * draft.CompactScale * primary.Scale));
            bounds = DisplayPlacementService.FindAvailableOnPrimary(_windows.Values.Select(window => window.CompactBounds).ToArray(), width, height);
        }
        draft.Position = DisplayPlacementService.Capture(bounds);

        string itemsPath = AppPaths.ResolveStoragePath(draft);
        bool createdStorage = !Directory.Exists(itemsPath);
        try
        {
            Directory.CreateDirectory(itemsPath);
            State.Organizers.Add(draft);
            await SaveStateAsync();
            CreateWindow(draft);
            Console.RefreshAll();
            return draft;
        }
        catch
        {
            State.Organizers.RemoveAll(item => item.Id == draft.Id);
            try { await SaveStateAsync(); }
            catch (Exception rollbackError) { AppLogger.Error("无法回滚创建收纳窗的状态。", rollbackError); }
            if (createdStorage) TryDeleteEmptyCreatedStorage(itemsPath);
            throw;
        }
    }

    public async Task<OrganizerDefinition> DuplicateOrganizerAsync(Guid id)
    {
        OrganizerDefinition source = State.Organizers.First(item => item.Id == id);
        if (source.PlacementMode == OrganizerPlacementMode.Station)
            throw new InvalidOperationException(AppStrings.Get("StationDuplicateError"));
        string name = OrganizerInteractionMath.CreateCopyName(
            source.Name,
            State.Organizers.Select(item => item.Name),
            AppStrings.Get("CopyNameSuffix"));
        var draft = OrganizerInteractionMath.CopySettings(source, name);
        var copiedNoteIds = new List<Guid>();
        try
        {
            var idMap = new Dictionary<Guid, Guid>();
            foreach (NoteDefinition note in source.Notes)
            {
                Guid copiedId = Guid.NewGuid();
                idMap[note.Id] = copiedId;
                copiedNoteIds.Add(copiedId);
                await _noteStore.CopyAsync(note.Id, copiedId);
                draft.Notes.Add(new NoteDefinition
                {
                    Id = copiedId,
                    Name = note.Name,
                    Theme = note.Theme,
                    FontSize = note.FontSize,
                    ShowRuledLines = note.ShowRuledLines,
                    Placement = note.Placement is null ? null : new NoteWindowPlacement
                    {
                        MonitorDevice = note.Placement.MonitorDevice,
                        XDip = note.Placement.XDip,
                        YDip = note.Placement.YDip,
                        WidthDip = note.Placement.WidthDip,
                        HeightDip = note.Placement.HeightDip
                    }
                });
            }
            draft.ItemOrder = source.ItemOrder
                .Where(key => key.StartsWith("note:", StringComparison.OrdinalIgnoreCase))
                .Select(key => source.Notes.FirstOrDefault(note => OrganizerNoteRules.ItemKey(note.Id)
                    .Equals(key, StringComparison.OrdinalIgnoreCase)) is { } note && idMap.TryGetValue(note.Id, out Guid copiedId)
                    ? OrganizerNoteRules.ItemKey(copiedId)
                    : string.Empty)
                .Where(key => key.Length > 0)
                .ToList();
            foreach (NoteDefinition note in draft.Notes)
            {
                string key = OrganizerNoteRules.ItemKey(note.Id);
                if (!draft.ItemOrder.Contains(key, StringComparer.OrdinalIgnoreCase)) draft.ItemOrder.Add(key);
            }
            return await CreateOrganizerAsync(draft);
        }
        catch
        {
            foreach (Guid noteId in copiedNoteIds)
            {
                try { await _noteStore.DeleteAsync(noteId); }
                catch (Exception cleanupError) { AppLogger.Error($"无法回滚复制便签：{noteId}", cleanupError); }
            }
            throw;
        }
    }

    internal async Task<NoteDefinition> CreateNoteAsync(Guid organizerId, string? text, NativeMethods.POINT anchor)
    {
        OrganizerDefinition organizer = State.Organizers.First(item => item.Id == organizerId);
        var note = new NoteDefinition
        {
            Name = OrganizerNoteRules.CreateDefaultName(organizer.Notes.Select(item => item.Name)),
            Placement = CreateNotePlacement(anchor)
        };
        await _noteStore.SaveAsync(note.Id, new NoteDocument { Html = OrganizerNoteRules.PlainTextToHtml(text) });
        organizer.Notes.Add(note);
        organizer.ItemOrder.Add(OrganizerNoteRules.ItemKey(note.Id));
        try
        {
            await SaveStateAsync();
            if (_windows.TryGetValue(organizerId, out MainWindow? window)) await window.RefreshNotesAsync();
            return note;
        }
        catch
        {
            organizer.Notes.Remove(note);
            organizer.ItemOrder.RemoveAll(key => key.Equals(OrganizerNoteRules.ItemKey(note.Id), StringComparison.OrdinalIgnoreCase));
            try { await _noteStore.DeleteAsync(note.Id); }
            catch (Exception cleanupError) { AppLogger.Error($"无法回滚新建便签：{note.Id}", cleanupError); }
            throw;
        }
    }

    internal void OpenNote(Guid organizerId, Guid noteId)
    {
        OrganizerDefinition organizer = State.Organizers.First(item => item.Id == organizerId);
        NoteDefinition note = organizer.Notes.First(item => item.Id == noteId);
        if (!_noteWindows.TryGetValue(noteId, out NoteWindow? window))
        {
            window = new NoteWindow(this, note, _noteStore, organizerId: organizerId);
            window.InitializeHostWindow();
            _noteWindows[noteId] = window;
        }
        window.ShowAndActivate();
    }

    internal async Task OpenExternalNoteAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".tucknote", StringComparison.OrdinalIgnoreCase))
        {
            Notify("TuckPane", AppStrings.Format("PortableNoteOpenErrorFormat", Path.GetFileName(fullPath), AppStrings.Get("PortableNoteExtensionError")), warning: true);
            return;
        }
        await _externalNoteOpenGate.WaitAsync();
        try
        {
            if (_externalNoteWindows.TryGetValue(fullPath, out NoteWindow? existing))
            {
                _trayHiddenExternalNotes.Remove(fullPath);
                existing.ShowAndActivate();
                return;
            }
            PortableNoteDocument portable = await _noteStore.LoadPortableAsync(fullPath);
            var definition = new NoteDefinition
            {
                Name = Path.GetFileNameWithoutExtension(fullPath),
                Theme = portable.Theme,
                FontSize = portable.FontSize,
                ShowRuledLines = portable.ShowRuledLines,
                Placement = FromPortablePlacement(portable.Placement)
            };
            var window = new NoteWindow(this, definition, _noteStore, fullPath, portable);
            window.InitializeHostWindow();
            _externalNoteWindows[fullPath] = window;
            window.ShowAndActivate();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法打开便携便签：{fullPath}", ex);
            Notify("TuckPane", AppStrings.Format("PortableNoteOpenErrorFormat", Path.GetFileName(fullPath), ex.Message), warning: true);
        }
        finally
        {
            _externalNoteOpenGate.Release();
        }
    }

    internal async Task<(string Path, bool RestoreWindow)> PrepareNoteDragAsync(Guid organizerId, Guid noteId)
    {
        OrganizerDefinition organizer = State.Organizers.First(item => item.Id == organizerId);
        NoteDefinition note = organizer.Notes.First(item => item.Id == noteId);
        bool restoreWindow = false;
        try
        {
            if (_noteWindows.TryGetValue(noteId, out NoteWindow? window))
                restoreWindow = await window.FlushAndHideForDragAsync();
            NoteDocument document = await _noteStore.LoadAsync(noteId);
            string path = await _noteStore.CreatePortableStagingAsync(note.Name, ToPortableDocument(note, document));
            return (path, restoreWindow);
        }
        catch
        {
            if (restoreWindow && _noteWindows.TryGetValue(noteId, out NoteWindow? window)) window.RestoreAfterDrag();
            throw;
        }
    }

    internal async Task CompleteNoteDragAsync(
        Guid organizerId,
        Guid noteId,
        string stagingPath,
        bool restoreWindow,
        bool moved)
    {
        try
        {
            if (moved)
            {
                try { await DeleteNoteAsync(organizerId, noteId); }
                catch
                {
                    if (State.Organizers.FirstOrDefault(item => item.Id == organizerId)?.Notes.Any(item => item.Id == noteId) == true)
                        OpenNote(organizerId, noteId);
                    throw;
                }
            }
            else if (restoreWindow && _noteWindows.TryGetValue(noteId, out NoteWindow? window))
            {
                window.RestoreAfterDrag();
            }
        }
        finally
        {
            CleanupNoteDragStaging(stagingPath);
        }
    }

    internal async Task RenameNoteAsync(Guid organizerId, Guid noteId, string name)
    {
        OrganizerDefinition organizer = State.Organizers.First(item => item.Id == organizerId);
        NoteDefinition note = organizer.Notes.First(item => item.Id == noteId);
        string candidate = name.Trim();
        if (candidate.Length == 0) throw new InvalidOperationException(AppStrings.Get("NoteNameRequired"));
        if (!OrganizerNoteRules.IsNameAvailable(
                organizer.Notes.Where(item => item.Id != noteId).Select(item => item.Name),
                candidate))
            throw new InvalidOperationException(AppStrings.Get("NoteNameDuplicate"));
        string previous = note.Name;
        note.Name = candidate;
        try { await SaveStateAsync(); }
        catch
        {
            note.Name = previous;
            throw;
        }
        if (_noteWindows.TryGetValue(noteId, out NoteWindow? noteWindow)) noteWindow.UpdateTitle();
        if (_windows.TryGetValue(organizerId, out MainWindow? window))
        {
            try { await window.RefreshNotesAsync(); }
            catch (Exception ex) { AppLogger.Error($"便签改名后刷新失败：{noteId}", ex); }
        }
    }

    internal async Task<string> RenameExternalNoteAsync(string path, string name)
    {
        string fullPath = Path.GetFullPath(path);
        string candidate = name.Trim();
        if (candidate.Length == 0) throw new InvalidOperationException(AppStrings.Get("NoteNameRequired"));
        if (candidate.Length > 120 || candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || candidate.EndsWith('.'))
            throw new InvalidOperationException(AppStrings.Get("FolderNameInvalidError"));
        if (NoteStore.IsReservedDeviceName(candidate.Split('.', 2)[0]))
            throw new InvalidOperationException(AppStrings.Get("FolderNameReservedError"));

        string directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException(AppStrings.Get("FolderNameInvalidError"));
        string targetPath = Path.Combine(directory, candidate + ".tucknote");
        if (targetPath.Equals(fullPath, StringComparison.Ordinal)) return fullPath;
        if (!targetPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(targetPath) || Directory.Exists(targetPath)))
            throw new InvalidOperationException(AppStrings.Get("NoteNameDuplicate"));

        OrganizerDefinition? organizer = State.Organizers.FirstOrDefault(item =>
            Path.GetFullPath(AppPaths.ResolveStoragePath(item)).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        string oldFileName = Path.GetFileName(fullPath);
        string newFileName = Path.GetFileName(targetPath);
        int orderIndex = organizer?.ItemOrder.FindIndex(item =>
            item.Equals(oldFileName, StringComparison.OrdinalIgnoreCase)) ?? -1;
        bool wasHidden = _trayHiddenExternalNotes.Contains(fullPath);
        _externalNoteWindows.TryGetValue(fullPath, out NoteWindow? window);

        File.Move(fullPath, targetPath);
        _externalNoteWindows.Remove(fullPath);
        _trayHiddenExternalNotes.Remove(fullPath);
        if (window is not null) _externalNoteWindows[targetPath] = window;
        if (wasHidden) _trayHiddenExternalNotes.Add(targetPath);
        if (orderIndex >= 0) organizer!.ItemOrder[orderIndex] = newFileName;
        try
        {
            if (orderIndex >= 0) await SaveStateAsync();
        }
        catch (Exception saveError)
        {
            try
            {
                File.Move(targetPath, fullPath);
            }
            catch (Exception rollbackError)
            {
                AppLogger.Error($"便携便签改名状态保存失败：{targetPath}", saveError);
                AppLogger.Error($"无法回滚便携便签改名：{targetPath}", rollbackError);
                return targetPath;
            }
            if (orderIndex >= 0) organizer!.ItemOrder[orderIndex] = oldFileName;
            _externalNoteWindows.Remove(targetPath);
            if (window is not null) _externalNoteWindows[fullPath] = window;
            _trayHiddenExternalNotes.Remove(targetPath);
            if (wasHidden) _trayHiddenExternalNotes.Add(fullPath);
            throw;
        }

        if (organizer is not null && _windows.TryGetValue(organizer.Id, out MainWindow? owner))
        {
            try { await owner.RefreshNotesAsync(); }
            catch (Exception ex) { AppLogger.Error($"便携便签改名后刷新失败：{targetPath}", ex); }
        }
        return targetPath;
    }

    internal async Task DeleteNoteAsync(Guid organizerId, Guid noteId)
    {
        AppLogger.Info($"便签删除：开始 organizer={organizerId} note={noteId}。");
        OrganizerDefinition organizer = State.Organizers.First(item => item.Id == organizerId);
        NoteDefinition? note = organizer.Notes.FirstOrDefault(item => item.Id == noteId);
        if (note is null)
        {
            AppLogger.Info($"便签删除：便签已不存在，跳过 note={noteId}。");
            return;
        }
        int index = organizer.Notes.IndexOf(note);
        int orderIndex = organizer.ItemOrder.FindIndex(key =>
            key.Equals(OrganizerNoteRules.ItemKey(noteId), StringComparison.OrdinalIgnoreCase));
        if (_noteWindows.Remove(noteId, out NoteWindow? noteWindow))
        {
            try
            {
                await noteWindow.ClosePermanentlyAsync();
                AppLogger.Info($"便签删除：窗口已关闭 note={noteId}。");
            }
            catch (Exception closeException)
            {
                // 窗口关闭/保存失败不阻断删除流程：内容文件稍后一并清理
                AppLogger.Error($"便签删除：窗口关闭失败，继续删除 note={noteId}", closeException);
            }
        }
        _trayHiddenNotes.Remove(noteId);
        organizer.Notes.RemoveAt(index);
        organizer.ItemOrder.RemoveAll(key => key.Equals(OrganizerNoteRules.ItemKey(noteId), StringComparison.OrdinalIgnoreCase));
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            organizer.Notes.Insert(index, note);
            organizer.ItemOrder.Insert(orderIndex < 0 ? organizer.ItemOrder.Count : orderIndex, OrganizerNoteRules.ItemKey(noteId));
            throw;
        }
        try { await _noteStore.DeleteAsync(noteId); }
        catch (Exception ex) { AppLogger.Error($"无法删除便签文件：{noteId}", ex); }
        if (_windows.TryGetValue(organizerId, out MainWindow? window)) await window.RefreshNotesAsync();
    }

    private static NoteWindowPlacement CreateNotePlacement(NativeMethods.POINT anchor)
    {
        DisplayInfo display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
        {
            Left = anchor.X,
            Top = anchor.Y,
            Right = anchor.X + 1,
            Bottom = anchor.Y + 1
        });
        int width = (int)Math.Round(360 * display.Scale);
        int height = (int)Math.Round(300 * display.Scale);
        int offset = (int)Math.Round(16 * display.Scale);
        NativeMethods.RECT bounds = DisplayPlacementService.Clamp(new NativeMethods.RECT
        {
            Left = anchor.X + offset,
            Top = anchor.Y + offset,
            Right = anchor.X + offset + width,
            Bottom = anchor.Y + offset + height
        }, display.Work);
        return new NoteWindowPlacement
        {
            MonitorDevice = display.Device,
            XDip = (bounds.Left - display.Work.Left) / display.Scale,
            YDip = (bounds.Top - display.Work.Top) / display.Scale,
            WidthDip = bounds.Width / display.Scale,
            HeightDip = bounds.Height / display.Scale
        };
    }

    private static PortableNoteDocument ToPortableDocument(NoteDefinition note, NoteDocument document) => new()
    {
        Theme = note.Theme,
        FontSize = note.FontSize,
        ShowRuledLines = note.ShowRuledLines,
        Placement = note.Placement is null ? null : new PortableNotePlacement
        {
            MonitorDevice = note.Placement.MonitorDevice,
            XDip = note.Placement.XDip,
            YDip = note.Placement.YDip,
            WidthDip = note.Placement.WidthDip,
            HeightDip = note.Placement.HeightDip
        },
        Html = document.Html
    };

    private static NoteWindowPlacement? FromPortablePlacement(PortableNotePlacement? placement) => placement is null
        ? null
        : new NoteWindowPlacement
        {
            MonitorDevice = placement.MonitorDevice,
            XDip = placement.XDip,
            YDip = placement.YDip,
            WidthDip = placement.WidthDip,
            HeightDip = placement.HeightDip
        };

    private static void CleanupNoteDragStaging(string path)
    {
        try
        {
            string root = Path.GetFullPath(AppPaths.NoteStagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(fullPath)) File.Delete(fullPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法清理便签拖放暂存文件：{path}", ex);
        }
    }

    public string ValidateStoragePath(string path)
    {
        string normalized = AppPaths.ValidateCustomStoragePath(path);
        foreach (OrganizerDefinition organizer in State.Organizers)
        {
            if (AppPaths.PathsOverlap(normalized, AppPaths.ResolveStoragePath(organizer)))
                throw new InvalidOperationException(AppStrings.Get("StoragePathOverlap"));
        }
        return normalized;
    }

    public async Task<string?> ToggleOrganizerModeAsync(Guid id)
    {
        OrganizerDefinition current = State.Organizers.First(item => item.Id == id);
        if (current.PlacementMode == OrganizerPlacementMode.Station) return AppStrings.Get("StationManageModeError");
        if (_windows.TryGetValue(id, out MainWindow? window) && window.IsExpanded)
        {
            await window.CollapseForPeerAsync();
        }

        OrganizerDefinition edited = OrganizerInteractionMath.CopySettings(current, current.Name);
        edited.Id = current.Id;
        edited.PlacementMode = current.PlacementMode == OrganizerPlacementMode.Floating
            ? OrganizerPlacementMode.Positioned
            : OrganizerPlacementMode.Floating;

        string? error = ApplyOrganizerRuntime(
            edited,
            OrganizerVisualChange.PlacementMode | OrganizerVisualChange.CompactScale);
        if (error is not null) return error;
        await SaveStateAsync();
        Console.RefreshAll(id);
        return null;
    }

    internal string? ApplyOrganizerRuntime(OrganizerDefinition edited, OrganizerVisualChange changes)
    {
        OrganizerDefinition current = State.Organizers.First(item => item.Id == edited.Id);
        if (edited.PlacementMode == OrganizerPlacementMode.Station &&
            State.Organizers.Any(item => item.Id != edited.Id &&
                item.PlacementMode == OrganizerPlacementMode.Station && item.DockEdge == edited.DockEdge))
        {
            return AppStrings.Get("StationEdgeOccupiedError");
        }
        if (current.PlacementMode == OrganizerPlacementMode.Station &&
            edited.PlacementMode != OrganizerPlacementMode.Station &&
            State.Organizers.Count(item => item.Id != edited.Id && item.PlacementMode != OrganizerPlacementMode.Station) >= OrganizerLimits.MaximumOrganizers)
        {
            return AppStrings.Get("MaximumOrganizersError");
        }
        bool layoutChanged = current.Layout.Mode != edited.Layout.Mode ||
            current.Layout.Rows != edited.Layout.Rows ||
            current.Layout.Columns != edited.Layout.Columns;
        OrganizerPlacementMode previousMode = current.PlacementMode;
        double previousCompactScale = current.CompactScale;
        WidgetPosition? previousPosition = current.Position;
        current.Name = string.IsNullOrWhiteSpace(edited.Name) ? current.Name : edited.Name.Trim();
        current.ThemeOverride = edited.ThemeOverride;
        current.PlacementMode = edited.PlacementMode;
        current.PositionLocked = edited.PositionLocked;
        current.DockEdge = edited.DockEdge;
        if (edited.PlacementMode == OrganizerPlacementMode.Station && edited.Position is not null)
            current.Position = edited.Position;
        current.Layout = new OrganizerLayout { Mode = edited.Layout.Mode, Rows = edited.Layout.Rows, Columns = edited.Layout.Columns };
        current.CompactScale = edited.CompactScale;
        current.CanvasScale = edited.CanvasScale;
        current.ItemScale = edited.ItemScale;
        current.NameScale = edited.NameScale;
        if (layoutChanged)
        {
            current.ManualCanvasBaseWidthDip = null;
            current.ManualCanvasBaseHeightDip = null;
        }
        StateStore.Normalize(State);
        if (_windows.TryGetValue(current.Id, out MainWindow? window))
        {
            bool enteringStation = previousMode != OrganizerPlacementMode.Station &&
                current.PlacementMode == OrganizerPlacementMode.Station;
            bool movingStation = current.PlacementMode == OrganizerPlacementMode.Station &&
                ((changes & (OrganizerVisualChange.Docking | OrganizerVisualChange.PlacementMode)) != 0);
            if (enteringStation || movingStation)
            {
                DisplayInfo stationDisplay = DisplayPlacementService.GetDisplay(current.Position?.MonitorDevice);
                NativeMethods.RECT anchor = DisplayPlacementService.CalculateStationAnchor(stationDisplay, current.DockEdge);
                current.Position = DisplayPlacementService.Capture(anchor);
                window.ApplyDefinition(changes & ~(OrganizerVisualChange.CompactScale | OrganizerVisualChange.NameScale | OrganizerVisualChange.PlacementMode | OrganizerVisualChange.Docking));
                window.MoveToStationPlacement(anchor);
                return null;
            }

            bool enteringPositioned = previousMode != OrganizerPlacementMode.Positioned &&
                current.PlacementMode == OrganizerPlacementMode.Positioned;
            bool resizingPositioned = current.PlacementMode == OrganizerPlacementMode.Positioned &&
                (changes & OrganizerVisualChange.CompactScale) != 0;
            if (enteringPositioned || resizingPositioned)
            {
                NativeMethods.RECT currentBounds = window.CompactBounds;
                DisplayInfo display = previousMode == OrganizerPlacementMode.Station
                    ? DisplayPlacementService.GetDisplay(previousPosition?.MonitorDevice)
                    : DisplayPlacementService.ForBounds(currentBounds);
                NativeMethods.RECT stationAnchor = DisplayPlacementService.CalculateStationAnchor(display, current.DockEdge, previousPosition);
                var center = new NativeMethods.POINT
                {
                    X = previousMode == OrganizerPlacementMode.Station ? stationAnchor.Left : currentBounds.Left + currentBounds.Width / 2,
                    Y = previousMode == OrganizerPlacementMode.Station ? stationAnchor.Top : currentBounds.Top + currentBounds.Height / 2
                };
                DesktopGridPlacement? placement = FindPositionedPlacement(
                    display,
                    center,
                    current.Id,
                    current.CompactScale);
                if (placement is null)
                {
                    current.PlacementMode = previousMode;
                    current.CompactScale = previousCompactScale;
                    current.Position = previousPosition;
                    return AppStrings.Get("PositionedRollbackError");
                }
                current.Position = DisplayPlacementService.Capture(placement.Bounds);
                window.ApplyDefinition(changes & ~(OrganizerVisualChange.CompactScale | OrganizerVisualChange.PlacementMode));
                window.MoveToPositionedPlacement(placement.Bounds, placement.CompactScale);
                return null;
            }

            if (previousMode == OrganizerPlacementMode.Station && current.PlacementMode == OrganizerPlacementMode.Floating)
            {
                DisplayInfo display = DisplayPlacementService.GetDisplay(previousPosition?.MonitorDevice);
                int width = Math.Max(1, (int)Math.Round(OrganizerLimits.CompactWindowWidthDip * current.CompactScale * display.Scale));
                int height = Math.Max(1, (int)Math.Round(OrganizerLimits.CompactWindowHeightDip * current.CompactScale * display.Scale));
                NativeMethods.RECT[] occupied = _windows.Values
                    .Where(candidate => candidate.OrganizerId != current.Id)
                    .Select(candidate => candidate.CompactBounds)
                    .ToArray();
                NativeMethods.RECT bounds = DisplayPlacementService.FindAvailable(display, occupied, width, height);
                current.Position = DisplayPlacementService.Capture(bounds);
                window.ApplyDefinition(changes & ~(OrganizerVisualChange.CompactScale | OrganizerVisualChange.NameScale | OrganizerVisualChange.PlacementMode));
                window.MoveToFloatingPlacement(bounds);
                return null;
            }

            window.ApplyDefinition(changes);
            if (previousMode == OrganizerPlacementMode.Positioned && current.PlacementMode == OrganizerPlacementMode.Floating)
            {
                current.Position = window.AdoptExpandedCenterForFloating() ?? current.Position;
            }
            else if (current.PlacementMode == OrganizerPlacementMode.Positioned &&
                (changes & OrganizerVisualChange.PositionLock) != 0 && !current.PositionLocked)
            {
                NativeMethods.RECT currentBounds = window.CompactBounds;
                var center = new NativeMethods.POINT
                {
                    X = currentBounds.Left + currentBounds.Width / 2,
                    Y = currentBounds.Top + currentBounds.Height / 2
                };
                DisplayInfo display = DisplayPlacementService.ForBounds(currentBounds);
                DesktopGridPlacement? placement = FindPositionedPlacement(display, center, current.Id, current.CompactScale);
                if (placement is not null)
                {
                    current.Position = DisplayPlacementService.Capture(placement.Bounds);
                    window.MoveToPositionedPlacement(placement.Bounds, placement.CompactScale);
                }
            }
        }
        return null;
    }

    internal DesktopGridPlacement? FindNearestPositionedPlacement(Guid organizerId, NativeMethods.RECT desiredBounds)
    {
        DisplayInfo display = DisplayPlacementService.ForBounds(desiredBounds);
        var center = new NativeMethods.POINT
        {
            X = desiredBounds.Left + desiredBounds.Width / 2,
            Y = desiredBounds.Top + desiredBounds.Height / 2
        };
        double compactScale = State.Organizers.First(item => item.Id == organizerId).CompactScale;
        return FindPositionedPlacement(display, center, organizerId, compactScale);
    }

    internal DesktopGridPlacement? FindCurrentPositionedPlacement(Guid organizerId, NativeMethods.RECT currentBounds)
    {
        DisplayInfo display = DisplayPlacementService.ForBounds(currentBounds);
        var center = new NativeMethods.POINT
        {
            X = currentBounds.Left + currentBounds.Width / 2,
            Y = currentBounds.Top + currentBounds.Height / 2
        };
        double compactScale = State.Organizers.First(item => item.Id == organizerId).CompactScale;
        return FindPositionedPlacement(display, center, organizerId, compactScale);
    }

    internal DesktopGridPlacement? RestoreLockedPositionedBounds(Guid organizerId)
    {
        OrganizerDefinition organizer = State.Organizers.First(item => item.Id == organizerId);
        DisplayInfo display = DisplayPlacementService.GetDisplays()
            .FirstOrDefault(item => string.Equals(item.Device, organizer.Position?.MonitorDevice, StringComparison.OrdinalIgnoreCase))
            ?? DisplayPlacementService.GetDisplays().FirstOrDefault(item => item.Monitor.Left == 0 && item.Monitor.Top == 0)
            ?? DisplayPlacementService.GetDisplays().First();
        DesktopGridSnapshot snapshot = ReadGridSnapshot(display);
        double scale = Math.Min(
            organizer.CompactScale,
            DesktopGridService.CalculatePositionedCompactScale(snapshot));
        (int width, int height, _) = DesktopGridService.CalculatePositionedWindowSize(snapshot, scale);
        NativeMethods.RECT bounds = DisplayPlacementService.RestoreToDisplay(organizer.Position, display, width, height);
        return new DesktopGridPlacement(bounds, scale, snapshot.ExplorerPositionsAvailable);
    }

    public async Task<TransferOutcome> DeleteOrganizerAsync(Guid id)
    {
        if (TransferQueue.IsActive) return new(string.Empty, null, TransferStatus.Failed, AppStrings.Get("TransferBeforeDelete"));
        OrganizerDefinition definition = State.Organizers.First(item => item.Id == id);
        var storage = new StorageService(
            AppPaths.ResolveStoragePath(definition),
            createIfMissing: false,
            ownedContainerPath: AppPaths.GetOwnedStorageContainer(definition),
            exportEmptyDirectory: !string.IsNullOrWhiteSpace(definition.StorageAbsolutePath));
        TransferOutcome outcome = await TransferQueue.RunAsync(token => storage.ExportToDesktopAsync(definition.Name, null, token));
        if (outcome.Status != TransferStatus.Moved) return outcome;

        foreach (NoteDefinition note in definition.Notes.ToArray())
        {
            if (_noteWindows.Remove(note.Id, out NoteWindow? noteWindow)) await noteWindow.ClosePermanentlyAsync();
        }

        if (_windows.Remove(id, out MainWindow? window))
        {
            if (ReferenceEquals(_expandedWindow, window)) _expandedWindow = null;
            window.ClosePermanently();
        }
        State.Organizers.RemoveAll(item => item.Id == id);
        await SaveStateAsync();
        foreach (NoteDefinition note in definition.Notes)
        {
            try { await _noteStore.DeleteAsync(note.Id); }
            catch (Exception ex) { AppLogger.Error($"无法删除收纳窗便签文件：{note.Id}", ex); }
        }
        Console.RefreshAll();
        return outcome;
    }

    public void RecreateStorage(Guid id)
    {
        if (_windows.TryGetValue(id, out MainWindow? window)) window.RecreateStorage();
        Console.RefreshAll(id);
    }

    public async Task SetGlobalThemeAsync(GlassTheme theme)
    {
        if (State.GlobalSettings.Theme == theme)
        {
            Console.RefreshAll();
            return;
        }
        State.GlobalSettings.Theme = theme;
        Console.ApplyTheme();
        await SaveStateAsync();
        foreach ((Guid id, MainWindow window) in _windows)
        {
            if (State.Organizers.First(item => item.Id == id).ThemeOverride is null) window.ApplyInheritedTheme();
        }
        Console.RefreshAll();
    }

    public async Task SetStartupAsync(bool enabled)
    {
        if (State.GlobalSettings.StartWithWindows == enabled) return;
        bool previous = State.GlobalSettings.StartWithWindows;
        StartupService.Apply(enabled);
        State.GlobalSettings.StartWithWindows = enabled;
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            State.GlobalSettings.StartWithWindows = previous;
            StartupService.Apply(previous);
            throw;
        }
    }

    public async Task SetCollapseOnOutsideClickAsync(bool enabled)
    {
        if (State.GlobalSettings.CollapseOnOutsideClick == enabled) return;
        bool previous = State.GlobalSettings.CollapseOnOutsideClick;
        State.GlobalSettings.CollapseOnOutsideClick = enabled;
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            State.GlobalSettings.CollapseOnOutsideClick = previous;
            throw;
        }
        foreach (MainWindow window in _windows.Values) window.ApplyOutsideClickSetting();
    }

    public async Task SetShowConsoleOnLaunchAsync(bool enabled)
    {
        if (State.GlobalSettings.ShowConsoleOnLaunch == enabled) return;
        bool previous = State.GlobalSettings.ShowConsoleOnLaunch;
        State.GlobalSettings.ShowConsoleOnLaunch = enabled;
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            State.GlobalSettings.ShowConsoleOnLaunch = previous;
            throw;
        }
    }

    public void ApplyOrganizerSurfaceOpacity(double opacity)
    {
        double clamped = Math.Clamp(opacity, 0d, 1d);
        if (Math.Abs(State.GlobalSettings.OrganizerSurfaceOpacity - clamped) < 0.0001) return;
        State.GlobalSettings.OrganizerSurfaceOpacity = clamped;
        foreach (MainWindow window in _windows.Values) window.ApplyInheritedTheme();
    }

    public void ApplyExpandOnHoverDelay(int milliseconds)
    {
        State.GlobalSettings.ExpandOnHoverMs = Math.Clamp(milliseconds, 100, 1500);
        foreach (MainWindow window in _windows.Values) window.ApplyHoverExpandDelay(State.GlobalSettings.ExpandOnHoverMs);
    }

    public void ApplyPointerLeaveDelay(int milliseconds)
    {
        State.GlobalSettings.CollapseOnPointerLeaveMs = Math.Clamp(milliseconds, 200, 2000);
        foreach (MainWindow window in _windows.Values) window.ApplyPointerLeaveDelay(State.GlobalSettings.CollapseOnPointerLeaveMs);
    }

    public async Task SetLanguageAsync(AppLanguage language)
    {
        if (!Enum.IsDefined(language)) language = AppLanguage.ChineseSimplified;
        if (State.GlobalSettings.Language == language)
        {
            Console.ApplyLanguage();
            return;
        }
        AppLanguage previous = State.GlobalSettings.Language;
        State.GlobalSettings.Language = language;
        AppStrings.SetLanguage(language);
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            State.GlobalSettings.Language = previous;
            AppStrings.SetLanguage(previous);
            throw;
        }
        Console.ApplyLanguage();
        foreach (MainWindow window in _windows.Values) window.ApplyLanguage();
        foreach (NoteWindow window in _noteWindows.Values) window.ApplyLanguage();
        foreach (NoteWindow window in _externalNoteWindows.Values) window.ApplyLanguage();
        _tray?.ApplyLanguage();
        Console.RefreshAll();
    }

    public async Task PrepareToExpandAsync(MainWindow source)
    {
        if (State.GlobalSettings.ExclusiveExpansion &&
            _expandedWindow is not null &&
            !ReferenceEquals(_expandedWindow, source) &&
            !_expandedWindow.IsShellDragActive)
        {
            await _expandedWindow.CollapseForPeerAsync();
        }
        _expandedWindow = source;
    }

    internal async Task ReconcileExclusiveExpansionAsync(MainWindow? preferred = null)
    {
        if (!State.GlobalSettings.ExclusiveExpansion) return;
        MainWindow? keep = preferred is { IsExpanded: true } ? preferred :
            _expandedWindow is { IsExpanded: true } ? _expandedWindow :
            _windows.Values.FirstOrDefault(window => window.IsExpanded);
        _expandedWindow = keep;
        foreach (MainWindow window in _windows.Values.ToArray())
        {
            if (!ReferenceEquals(window, keep) && window.IsExpanded && !window.IsShellDragActive)
                await window.CollapseForPeerAsync();
        }
    }

    public void NotifyCollapsed(MainWindow source)
    {
        if (ReferenceEquals(_expandedWindow, source)) _expandedWindow = null;
    }

    internal async Task<bool> TryMoveItemToPeerAsync(MainWindow source, string path, NativeMethods.POINT dropPoint)
    {
        MainWindow? target = _windows.Values.FirstOrDefault(window =>
            !ReferenceEquals(window, source) && window.ContainsScreenPoint(dropPoint));
        return target is not null && await target.ImportFromPeerAsync(path);
    }

    public void OpenConsole(Guid? organizerId = null)
    {
        _ = _dispatcher.TryEnqueue(() => Console.ShowAndActivate(organizerId));
    }

    public void Notify(string title, string message, bool warning = false)
    {
        AppLogger.Info($"{title}: {message}");
        _tray?.ShowNotification(title, message, warning);
    }

    public void NotifyTransparencyFallback()
    {
        if (_transparencyNoticeShown) return;
        _transparencyNoticeShown = true;
        Notify("TuckPane", AppStrings.Get("TransparencyNotification"));
        Console.ShowTransparencyNotice();
    }

    private DesktopGridPlacement? FindPositionedPlacement(
        DisplayInfo display,
        NativeMethods.POINT? desiredCenter,
        Guid excludeId,
        double compactScale)
    {
        DesktopGridSnapshot snapshot = ReadGridSnapshot(display);
        NativeMethods.RECT[] occupied = _windows.Values
            .Where(window => window.OrganizerId != excludeId)
            .Where(window => State.Organizers.First(item => item.Id == window.OrganizerId).PlacementMode == OrganizerPlacementMode.Positioned)
            .Select(window => window.CompactBounds)
            .ToArray();
        return DesktopGridService.Find(snapshot, occupied, desiredCenter, compactScale);
    }

    private bool NormalizePositionedPlacementsOnStartup()
    {
        IReadOnlyList<DisplayInfo> displays = DisplayPlacementService.GetDisplays();
        DisplayInfo primary = displays.FirstOrDefault(display => display.Monitor.Left == 0 && display.Monitor.Top == 0) ?? displays.First();
        var occupied = new List<NativeMethods.RECT>();
        bool changed = false;
        foreach (OrganizerDefinition organizer in State.Organizers.Where(item => item.PlacementMode == OrganizerPlacementMode.Positioned))
        {
            DisplayInfo display = displays.FirstOrDefault(item => string.Equals(item.Device, organizer.Position?.MonitorDevice, StringComparison.OrdinalIgnoreCase)) ?? primary;
            if (organizer.PositionLocked)
            {
                DesktopGridSnapshot lockedSnapshot = ReadGridSnapshot(display);
                double lockedScale = Math.Min(
                    organizer.CompactScale,
                    DesktopGridService.CalculatePositionedCompactScale(lockedSnapshot));
                (int lockedWidth, int lockedHeight, _) = DesktopGridService.CalculatePositionedWindowSize(lockedSnapshot, lockedScale);
                NativeMethods.RECT locked = DisplayPlacementService.RestoreToDisplay(organizer.Position, display, lockedWidth, lockedHeight);
                occupied.Add(locked);
                continue;
            }
            DesktopGridSnapshot snapshot = ReadGridSnapshot(display);
            double scale = Math.Min(
                organizer.CompactScale,
                DesktopGridService.CalculatePositionedCompactScale(snapshot));
            (int width, int height, _) = DesktopGridService.CalculatePositionedWindowSize(snapshot, scale);
            NativeMethods.RECT desired = DisplayPlacementService.RestoreToDisplay(organizer.Position, display, width, height);
            var center = new NativeMethods.POINT
            {
                X = desired.Left + desired.Width / 2,
                Y = desired.Top + desired.Height / 2
            };
            DesktopGridPlacement? placement = DesktopGridService.Find(
                snapshot,
                occupied,
                center,
                organizer.CompactScale);
            if (placement is null)
            {
                occupied.Add(desired);
                continue;
            }
            occupied.Add(placement.Bounds);
            if (!RectsEqual(desired, placement.Bounds)) changed = true;
            organizer.Position = DisplayPlacementService.Capture(placement.Bounds);
        }
        return changed;
    }

    private bool NormalizeStationPlacementsOnStartup()
    {
        bool changed = false;
        foreach (OrganizerDefinition organizer in State.Organizers.Where(item => item.PlacementMode == OrganizerPlacementMode.Station))
        {
            DisplayInfo display = DisplayPlacementService.GetDisplay(organizer.Position?.MonitorDevice);
            WidgetPosition normalized = DisplayPlacementService.Capture(
                DisplayPlacementService.CalculateStationAnchor(display, organizer.DockEdge, organizer.Position));
            if (!PositionsEqual(organizer.Position, normalized)) changed = true;
            organizer.Position = normalized;
        }
        return changed;
    }

    private static bool PositionsEqual(WidgetPosition? first, WidgetPosition second) =>
        first is not null && string.Equals(first.MonitorDevice, second.MonitorDevice, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs(first.XDip - second.XDip) < .01 && Math.Abs(first.YDip - second.YDip) < .01;

    private DesktopGridSnapshot ReadGridSnapshot(DisplayInfo display)
    {
        DesktopGridSnapshot snapshot = _desktopGrid.ReadSnapshot(display);
        if (!snapshot.ExplorerPositionsAvailable && !_gridFallbackNoticeShown)
        {
            _gridFallbackNoticeShown = true;
            _ = _dispatcher.TryEnqueue(() => Notify("TuckPane", AppStrings.Get("GridFallbackMessage")));
        }
        return snapshot;
    }

    private static bool RectsEqual(NativeMethods.RECT first, NativeMethods.RECT second) =>
        first.Left == second.Left && first.Top == second.Top && first.Right == second.Right && first.Bottom == second.Bottom;

    private static void TryDeleteEmptyCreatedStorage(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法回滚空的收纳目录：{path}", ex);
        }
    }

    
    public async Task SetExclusiveExpansionAsync(bool enabled)
    {
        if (State.GlobalSettings.ExclusiveExpansion == enabled)
        {
            if (enabled) await ReconcileExclusiveExpansionAsync();
            return;
        }
        State.GlobalSettings.ExclusiveExpansion = enabled;
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            State.GlobalSettings.ExclusiveExpansion = !enabled;
            throw;
        }
        if (enabled) await ReconcileExclusiveExpansionAsync();
    }

    public async Task SetExpandOnHoverAsync(bool enabled)
    {
        if (State.GlobalSettings.ExpandOnHover == enabled) return;
        State.GlobalSettings.ExpandOnHover = enabled;
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            State.GlobalSettings.ExpandOnHover = !enabled;
            throw;
        }
    }

    public async Task SetCollapseOnPointerLeaveAsync(bool enabled)
    {
        if (State.GlobalSettings.CollapseOnPointerLeave == enabled) return;
        State.GlobalSettings.CollapseOnPointerLeave = enabled;
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            State.GlobalSettings.CollapseOnPointerLeave = !enabled;
            throw;
        }
    }

    public Task SaveStateAsync() => _stateStore.SaveAsync(State);

    public async Task ExitAsync()
    {
        if (_exiting) return;
        if (TransferQueue.IsActive && !await Console.ConfirmCancelTransferAndExitAsync()) return;
        _exiting = true;
        TransferQueue.CancelAll();
        await TransferQueue.WaitForIdleAsync();
        foreach (NoteWindow window in _noteWindows.Values.ToArray()) await window.ClosePermanentlyAsync();
        _noteWindows.Clear();
        foreach (NoteWindow window in _externalNoteWindows.Values.ToArray()) await window.ClosePermanentlyAsync();
        _externalNoteWindows.Clear();
        foreach (MainWindow window in _windows.Values.ToArray()) window.ClosePermanently();
        _windows.Clear();
        _desktopIconGuard?.Dispose();
        _tray?.Dispose();
        Console.ClosePermanently();
        Application.Current.Exit();
    }

    private void CreateWindow(OrganizerDefinition organizer)
    {
        var window = new MainWindow(this, organizer);
        window.InitializeHostWindow();
        _windows.Add(organizer.Id, window);
        window.Activate();
        if (organizer.PlacementMode == OrganizerPlacementMode.Station) window.SetVisible(true);
    }

    private void HandleTrayCommand(TrayCommand command)
    {
        _ = _dispatcher.TryEnqueue(async () =>
        {
            switch (command)
            {
                case TrayCommand.OpenConsole:
                    OpenConsole();
                    break;
                case TrayCommand.ShowAll:
                    foreach (MainWindow window in _windows.Values) window.SetVisible(true);
                    foreach (Guid noteId in _trayHiddenNotes.ToArray())
                    {
                        if (_noteWindows.TryGetValue(noteId, out NoteWindow? noteWindow)) noteWindow.RestoreFromTray();
                    }
                    _trayHiddenNotes.Clear();
                    foreach (string path in _trayHiddenExternalNotes.ToArray())
                    {
                        if (_externalNoteWindows.TryGetValue(path, out NoteWindow? noteWindow)) noteWindow.RestoreFromTray();
                    }
                    _trayHiddenExternalNotes.Clear();
                    break;
                case TrayCommand.HideAll:
                    foreach (MainWindow window in _windows.Values) window.SetVisible(false);
                    _trayHiddenNotes.Clear();
                    foreach ((Guid noteId, NoteWindow noteWindow) in _noteWindows)
                    {
                        if (!noteWindow.IsVisible) continue;
                        _trayHiddenNotes.Add(noteId);
                        noteWindow.HideForTray();
                    }
                    _trayHiddenExternalNotes.Clear();
                    foreach ((string path, NoteWindow noteWindow) in _externalNoteWindows)
                    {
                        if (!noteWindow.IsVisible) continue;
                        _trayHiddenExternalNotes.Add(path);
                        noteWindow.HideForTray();
                    }
                    break;
                case TrayCommand.ToggleStartup:
                    await SetStartupAsync(!State.GlobalSettings.StartWithWindows);
                    Console.RefreshAll();
                    break;
                case TrayCommand.CancelTransfer:
                    TransferQueue.CancelCurrent();
                    break;
                case TrayCommand.Exit:
                    await ExitAsync();
                    break;
            }
        });
    }

    private IReadOnlyList<NativeMethods.RECT> CollectOrganizerBounds()
    {
        var bounds = new List<NativeMethods.RECT>(_windows.Count);
        foreach (MainWindow window in _windows.Values)
        {
            // 只在折叠态才把收纳盒当作桌面图标覆盖层；展开时窗口提到普通层，
            // 覆盖桌面图标是正常行为，不参与避让。
            if (window.IsExpanded) continue;
            if (window.TryGetWindowBounds(out NativeMethods.RECT rect)) bounds.Add(rect);
        }
        return bounds;
    }

    public void Dispose()
    {
        _desktopIconGuard?.Dispose();
        _tray?.Dispose();
        foreach (NoteWindow window in _noteWindows.Values) _ = window.ClosePermanentlyAsync();
        foreach (NoteWindow window in _externalNoteWindows.Values) _ = window.ClosePermanentlyAsync();
        foreach (MainWindow window in _windows.Values) window.ClosePermanently();
    }
}
