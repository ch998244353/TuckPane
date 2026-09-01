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
    private readonly Dictionary<string, TodoWindow> _externalTodoWindows = new(StringComparer.OrdinalIgnoreCase);
    // ponytail: portable document opens are rare; use per-path tasks only if parallel opens become measurable.
    private readonly SemaphoreSlim _externalNoteOpenGate = new(1, 1);
    private readonly HashSet<Guid> _trayHiddenNotes = [];
    private readonly HashSet<string> _trayHiddenExternalNotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _trayHiddenExternalTodos = new(StringComparer.OrdinalIgnoreCase);
    private readonly NoteStore _noteStore = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private TrayIconService? _tray;
    private MainWindow? _expandedWindow;
    private bool _transparencyNoticeShown;
    private bool _gridFallbackNoticeShown;
    private bool _exiting;

    public AppStateV2 State { get; private set; } = new();
    public TransferQueue TransferQueue { get; } = new();
    public ConsoleWindow Console { get; private set; } = null!;
    public IReadOnlyCollection<MainWindow> Windows => _windows.Values;
    internal event EventHandler? ThemeChanged;

    internal IReadOnlyList<WindowAlignmentTarget> GetWindowAlignmentTargets(MainWindow source, DisplayInfo display)
    {
        var targets = new List<WindowAlignmentTarget>(_windows.Count - 1);
        foreach (MainWindow window in _windows.Values.OrderBy(window => window.OrganizerId))
        {
            if (ReferenceEquals(window, source) || !window.TryGetCollapsedFloatingAlignmentFrame(out NativeMethods.RECT bounds)) continue;
            if (!string.Equals(DisplayPlacementService.ForBounds(bounds).Device, display.Device, StringComparison.OrdinalIgnoreCase)) continue;
            targets.Add(new(window.OrganizerId, bounds));
        }
        return targets;
    }

    internal IReadOnlyList<WidgetItem> GetContainedOrganizerItems(Guid stationId) =>
        State.Organizers
            .Where(organizer => organizer.ContainerStationId == stationId)
            .Select(organizer => new WidgetItem(
                organizer.Name,
                AppPaths.ResolveStoragePath(organizer),
                OrganizerInteractionMath.OrganizerItemKey(organizer.Id),
                WidgetItemKind.Organizer,
                organizerId: organizer.Id))
            .ToArray();

    internal IReadOnlyList<WidgetItem> GetOrganizerPreviewItems(Guid organizerId) =>
        _windows.TryGetValue(organizerId, out MainWindow? window)
            ? window.ItemSnapshot.Take(4).Select(item => item.CopyValue()).ToArray()
            : [];

    internal void NotifyOrganizerPreviewChanged(Guid organizerId)
    {
        OrganizerDefinition? organizer = State.Organizers.FirstOrDefault(candidate => candidate.Id == organizerId);
        if (organizer?.ContainerStationId is Guid stationId && _windows.TryGetValue(stationId, out MainWindow? station))
            station.RefreshOrganizerPreview(organizerId);
    }

    public async Task InitializeAsync()
    {
        AppPaths.EnsureCreated();
        State = await _stateStore.LoadAsync();
        AppStrings.SetLanguage(State.GlobalSettings.Language);
        await MigrateLegacyOrganizerNotesAsync();
        StartupService.Apply(State.GlobalSettings.StartWithWindows);

        Console = new ConsoleWindow(this);
        Console.InitializeHostWindow();
        _tray = new TrayIconService(Console.Hwnd, () => State.GlobalSettings.StartWithWindows, () => TransferQueue.IsActive, HandleTrayCommand);
        TransferQueue.StateChanged += (_, _) => Console.UpdateTransferState();

        if (NormalizePositionedPlacementsOnStartup() | NormalizeStationPlacementsOnStartup()) await SaveStateAsync();
        foreach (OrganizerDefinition organizer in State.Organizers) CreateWindow(organizer);
        Console.RefreshAll();
    }

    public async Task<OrganizerDefinition> CreateOrganizerAsync(OrganizerDefinition draft, string? storagePath = null)
    {
        draft.ContainerStationId = null;
        if (draft.PlacementMode == OrganizerPlacementMode.Station)
            draft.ExpandedContentMode = OrganizerExpandedContentMode.Icon;
        if (draft.PlacementMode == OrganizerPlacementMode.Station)
        {
            if (State.Organizers.Any(item => item.PlacementMode == OrganizerPlacementMode.Station && item.DockEdge == draft.DockEdge))
                throw new InvalidOperationException(AppStrings.Get("StationEdgeOccupiedError"));
        }
        else if (State.Organizers.Count(item => item.PlacementMode != OrganizerPlacementMode.Station) >= OrganizerLimits.MaximumOrganizers)
        {
            throw new InvalidOperationException(AppStrings.Get("MaximumOrganizersError"));
        }
        Guid id = draft.Id;
        if (id == Guid.Empty || State.Organizers.Any(item => item.Id == id))
            throw new InvalidOperationException(AppStrings.Get("OrganizerIdOccupied"));
        draft.Name = string.IsNullOrWhiteSpace(draft.Name) ? AppStrings.DefaultOrganizerName : draft.Name.Trim();
        draft.CreatedAtUtc = DateTimeOffset.UtcNow;
        string itemsPath;
        string? defaultStorageDirectory = null;
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            defaultStorageDirectory = AppPaths.ResolveDefaultStorageDirectory(State.GlobalSettings);
            itemsPath = AppPaths.CreateDefaultOrganizerStoragePath(defaultStorageDirectory, id);
            draft.StorageRelativePath = string.Empty;
            draft.StorageAbsolutePath = itemsPath;
            draft.StorageOwnedByApp = true;
        }
        else
        {
            draft.StorageRelativePath = string.Empty;
            draft.StorageAbsolutePath = ValidateStoragePath(storagePath);
            draft.StorageOwnedByApp = false;
            itemsPath = draft.StorageAbsolutePath;
        }

        draft.CompactScale = State.GlobalSettings.ResolveCompactScale(draft.PlacementMode, draft.CompactScale);
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
            int width = Math.Max(1, (int)Math.Round(OrganizerLimits.CompactWindowWidthDip * draft.CompactScale * selectedDisplay.Scale));
            int height = Math.Max(1, (int)Math.Round(OrganizerLimits.CompactWindowHeightDip * draft.CompactScale * selectedDisplay.Scale));
            bounds = DisplayPlacementService.FindAvailable(
                selectedDisplay,
                _windows.Values.Select(window => window.CompactBounds).ToArray(),
                width,
                height);
        }
        draft.Position = DisplayPlacementService.Capture(bounds);

        bool createdStorage = false;
        bool stateAdded = false;
        try
        {
            if (defaultStorageDirectory is not null)
            {
                itemsPath = AppPaths.CreateDefaultOrganizerStorageDirectory(
                    defaultStorageDirectory,
                    id,
                    State.Organizers.Select(AppPaths.ResolveStoragePath));
                createdStorage = true;
            }
            State.Organizers.Add(draft);
            stateAdded = true;
            await SaveStateAsync();
            CreateWindow(draft);
            Console.RefreshAll();
            return draft;
        }
        catch
        {
            if (stateAdded)
            {
                State.Organizers.RemoveAll(item => item.Id == draft.Id);
                try { await SaveStateAsync(); }
                catch (Exception rollbackError) { AppLogger.Error("无法回滚创建收纳窗的状态。", rollbackError); }
            }
            if (createdStorage) TryDeleteEmptyCreatedStorage(itemsPath);
            throw;
        }
    }

    public Task CreateDesktopOrganizerAsync()
        => CreateShellOrganizerAsync(storagePath: null);

    public Task CreateFolderOrganizerAsync(string storagePath)
        => CreateShellOrganizerAsync(storagePath);

    internal async Task CreateExternalNoteAsync(string directory)
    {
        try
        {
            string targetDirectory = NoteStore.ValidatePortableDirectory(directory);
            NativeMethods.POINT anchor;
            if (!NativeMethods.GetCursorPos(out anchor))
            {
                DisplayInfo display = DisplayPlacementService.GetDisplay();
                anchor = new NativeMethods.POINT { X = display.Work.Left + 16, Y = display.Work.Top + 16 };
            }
            var definition = new NoteDefinition
            {
                Name = "新建便签",
                Theme = State.GlobalSettings.NoteTheme,
                FontSize = 14,
                ShowRuledLines = false,
                Placement = CreateNotePlacement(anchor)
            };
            string path = await _noteStore.CreatePortableAsync(
                targetDirectory,
                definition.Name,
                ToPortableDocument(definition, new NoteDocument()));
            await OpenExternalNoteAsync(path);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法在目标目录创建便签：{directory}", ex);
            Notify(AppStrings.Get("PortableNoteCreateErrorTitle"), ex.Message, warning: true);
        }
    }

    private async Task CreateShellOrganizerAsync(string? storagePath)
    {
        try
        {
            string name = AppStrings.DefaultOrganizerName;
            if (storagePath is not null)
            {
                if (string.IsNullOrWhiteSpace(storagePath) || !Path.IsPathFullyQualified(storagePath))
                    throw new InvalidOperationException(AppStrings.Get("StorageAbsoluteRequired"));
                name = Path.GetFileName(Path.TrimEndingDirectorySeparator(storagePath));
            }
            DisplayInfo display = NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)
                ? DisplayPlacementService.ForBounds(new NativeMethods.RECT
                {
                    Left = cursor.X,
                    Top = cursor.Y,
                    Right = cursor.X + 1,
                    Bottom = cursor.Y + 1
                })
                : DisplayPlacementService.GetDisplay();
            await CreateOrganizerAsync(new OrganizerDefinition
            {
                Name = name,
                PlacementMode = OrganizerPlacementMode.Floating,
                Position = new WidgetPosition { MonitorDevice = display.Device }
            }, storagePath);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法从 Shell 命令创建收纳窗。", ex);
            Notify(AppStrings.Get("CreateErrorTitle"), ex.Message, warning: true);
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

    internal async Task<string> CreateNoteAsync(Guid organizerId, string? text, NativeMethods.POINT anchor)
    {
        OrganizerDefinition organizer = State.Organizers.First(item => item.Id == organizerId);
        var note = new NoteDefinition
        {
            Name = OrganizerNoteRules.CreateDefaultName(organizer.Notes.Select(item => item.Name)),
            Theme = State.GlobalSettings.NoteTheme,
            Placement = CreateNotePlacement(anchor)
        };
        string path = await CreateOrganizerPortableNoteAsync(
            organizer,
            _noteStore,
            note.Name,
            ToPortableDocument(note, new NoteDocument { Html = OrganizerNoteRules.PlainTextToHtml(text) }),
            SaveStateAsync);
        if (_windows.TryGetValue(organizerId, out MainWindow? window))
        {
            try { await window.RefreshNotesAsync(); }
            catch (Exception ex) { AppLogger.Error($"新建便签后刷新失败：{path}", ex); }
        }
        return path;
    }

    internal async Task<string> CreateTodoAsync(Guid organizerId, NativeMethods.POINT anchor)
    {
        OrganizerDefinition organizer = State.Organizers.First(item => item.Id == organizerId);
        string storagePath = AppPaths.ResolveStoragePath(organizer);
        Directory.CreateDirectory(storagePath);
        var document = new PortableTodoDocument
        {
            Theme = State.GlobalSettings.NoteTheme,
            FontSize = 14,
            Placement = CreateTodoPlacement(anchor)
        };
        string path = await _noteStore.CreateTodoAsync(storagePath, "新建待办", document);
        string relativeName = Path.GetFileName(path);
        organizer.ItemOrder.Add(relativeName);
        try { await SaveStateAsync(); }
        catch
        {
            organizer.ItemOrder.RemoveAll(key => key.Equals(relativeName, StringComparison.OrdinalIgnoreCase));
            try { File.Delete(path); }
            catch (Exception cleanupError) { AppLogger.Error($"无法回滚新建待办：{path}", cleanupError); }
            throw;
        }
        if (_windows.TryGetValue(organizerId, out MainWindow? window))
        {
            try { await window.RefreshNotesAsync(); }
            catch (Exception ex) { AppLogger.Error($"新建待办后刷新失败：{path}", ex); }
        }
        await OpenExternalTodoAsync(path);
        return path;
    }

    internal static async Task<string> CreateOrganizerPortableNoteAsync(
        OrganizerDefinition organizer,
        NoteStore noteStore,
        string name,
        PortableNoteDocument document,
        Func<Task> saveStateAsync)
    {
        string storagePath = AppPaths.ResolveStoragePath(organizer);
        Directory.CreateDirectory(storagePath);
        string path = await noteStore.CreatePortableAsync(storagePath, name, document);
        string relativeName = Path.GetFileName(path);
        organizer.ItemOrder.Add(relativeName);
        try { await saveStateAsync(); }
        catch
        {
            organizer.ItemOrder.RemoveAll(key => key.Equals(relativeName, StringComparison.OrdinalIgnoreCase));
            try { File.Delete(path); }
            catch (Exception cleanupError) { AppLogger.Error($"无法回滚新建便签：{path}", cleanupError); }
            throw;
        }
        return path;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> MigrateLegacyOrganizerNotesAsync(OrganizerDefinition? only = null)
    {
        var migrated = new Dictionary<Guid, string>();
        IEnumerable<OrganizerDefinition> organizers = only is null ? State.Organizers : [only!];
        foreach (OrganizerDefinition organizer in organizers)
            foreach ((Guid id, string path) in await MigrateLegacyOrganizerNotesAsync(organizer, _noteStore, SaveStateAsync))
                migrated[id] = path;
        return migrated;
    }

    internal static async Task<IReadOnlyDictionary<Guid, string>> MigrateLegacyOrganizerNotesAsync(
        OrganizerDefinition organizer,
        NoteStore noteStore,
        Func<Task> saveStateAsync)
    {
        var migrated = new Dictionary<Guid, string>();
        if (organizer.Notes.Count == 0) return migrated;
        string storagePath;
        try { storagePath = AppPaths.ResolveStoragePath(organizer); }
        catch (Exception ex)
        {
            AppLogger.Error($"无法解析旧便签迁移目录：{organizer.Id}", ex);
            return migrated;
        }
        if (!Directory.Exists(storagePath)) return migrated;

        foreach (NoteDefinition note in organizer.Notes.ToArray())
        {
            string? createdPath = null;
            int noteIndex = organizer.Notes.IndexOf(note);
            string legacyKey = OrganizerNoteRules.ItemKey(note.Id);
            int orderIndex = organizer.ItemOrder.FindIndex(key => key.Equals(legacyKey, StringComparison.OrdinalIgnoreCase));
            try
            {
                NoteDocument document = await noteStore.LoadAsync(note.Id);
                createdPath = await noteStore.CreatePortableAsync(storagePath, note.Name, ToPortableDocument(note, document));
                _ = await noteStore.LoadPortableAsync(createdPath);
                string relativeName = Path.GetFileName(createdPath);
                organizer.Notes.Remove(note);
                organizer.ItemOrder.RemoveAll(key => key.Equals(legacyKey, StringComparison.OrdinalIgnoreCase));
                if (orderIndex >= 0) organizer.ItemOrder.Insert(Math.Min(orderIndex, organizer.ItemOrder.Count), relativeName);
                else organizer.ItemOrder.Add(relativeName);
                try
                {
                    await saveStateAsync();
                }
                catch
                {
                    organizer.Notes.Insert(Math.Min(noteIndex, organizer.Notes.Count), note);
                    organizer.ItemOrder.RemoveAll(key => key.Equals(relativeName, StringComparison.OrdinalIgnoreCase));
                    if (orderIndex >= 0) organizer.ItemOrder.Insert(Math.Min(orderIndex, organizer.ItemOrder.Count), legacyKey);
                    throw;
                }
                migrated[note.Id] = createdPath;
                try { await noteStore.DeleteAsync(note.Id); }
                catch (Exception cleanupError) { AppLogger.Error($"无法清理已迁移旧便签：{note.Id}", cleanupError); }
            }
            catch (Exception ex)
            {
                if (createdPath is not null)
                {
                    try { File.Delete(createdPath); }
                    catch (Exception cleanupError) { AppLogger.Error($"无法回滚旧便签迁移文件：{createdPath}", cleanupError); }
                }
                AppLogger.Error($"旧便签迁移失败，将在下次启动重试：{note.Id}", ex);
            }
        }
        return migrated;
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
            if (portable.Theme != State.GlobalSettings.NoteTheme)
            {
                portable.Theme = State.GlobalSettings.NoteTheme;
                await _noteStore.SavePortableAsync(fullPath, portable);
            }
            var definition = new NoteDefinition
            {
                Name = Path.GetFileNameWithoutExtension(fullPath),
                Theme = State.GlobalSettings.NoteTheme,
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

    internal async Task OpenExternalTodoAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".tucktodo", StringComparison.OrdinalIgnoreCase))
        {
            Notify("TuckPane", AppStrings.Format("PortableTodoOpenErrorFormat", Path.GetFileName(fullPath), AppStrings.Get("PortableTodoExtensionError")), warning: true);
            return;
        }
        await _externalNoteOpenGate.WaitAsync();
        try
        {
            if (_externalTodoWindows.TryGetValue(fullPath, out TodoWindow? existing))
            {
                _trayHiddenExternalTodos.Remove(fullPath);
                existing.ShowAndActivate();
                return;
            }
            PortableTodoDocument document = await _noteStore.LoadTodoAsync(fullPath);
            if (document.Theme != State.GlobalSettings.NoteTheme)
            {
                document.Theme = State.GlobalSettings.NoteTheme;
                await _noteStore.SaveTodoAsync(fullPath, document);
            }
            var window = new TodoWindow(this, _noteStore, fullPath, document);
            window.InitializeHostWindow();
            _externalTodoWindows[fullPath] = window;
            window.ShowAndActivate();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法打开便携待办：{fullPath}", ex);
            Notify("TuckPane", AppStrings.Format("PortableTodoOpenErrorFormat", Path.GetFileName(fullPath), ex.Message), warning: true);
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

    internal async Task<string> RenameExternalTodoAsync(string path, string name)
    {
        string fullPath = Path.GetFullPath(path);
        string candidate = name.Trim();
        if (candidate.Length == 0) throw new InvalidOperationException(AppStrings.Get("TodoNameRequired"));
        if (candidate.Length > 120 || candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || candidate.EndsWith('.'))
            throw new InvalidOperationException(AppStrings.Get("FolderNameInvalidError"));
        if (NoteStore.IsReservedDeviceName(candidate.Split('.', 2)[0]))
            throw new InvalidOperationException(AppStrings.Get("FolderNameReservedError"));

        string directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException(AppStrings.Get("FolderNameInvalidError"));
        string targetPath = Path.Combine(directory, candidate + ".tucktodo");
        if (targetPath.Equals(fullPath, StringComparison.Ordinal)) return fullPath;
        if (!targetPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(targetPath) || Directory.Exists(targetPath)))
            throw new InvalidOperationException(AppStrings.Get("TodoNameDuplicate"));

        OrganizerDefinition? organizer = State.Organizers.FirstOrDefault(item =>
            Path.GetFullPath(AppPaths.ResolveStoragePath(item)).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        string oldFileName = Path.GetFileName(fullPath);
        string newFileName = Path.GetFileName(targetPath);
        int orderIndex = organizer?.ItemOrder.FindIndex(item =>
            item.Equals(oldFileName, StringComparison.OrdinalIgnoreCase)) ?? -1;
        bool wasHidden = _trayHiddenExternalTodos.Contains(fullPath);
        _externalTodoWindows.TryGetValue(fullPath, out TodoWindow? window);

        File.Move(fullPath, targetPath);
        _externalTodoWindows.Remove(fullPath);
        _trayHiddenExternalTodos.Remove(fullPath);
        if (window is not null) _externalTodoWindows[targetPath] = window;
        if (wasHidden) _trayHiddenExternalTodos.Add(targetPath);
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
                AppLogger.Error($"便携待办改名状态保存失败：{targetPath}", saveError);
                AppLogger.Error($"无法回滚便携待办改名：{targetPath}", rollbackError);
                window?.RebindExternalPath(targetPath);
                return targetPath;
            }
            if (orderIndex >= 0) organizer!.ItemOrder[orderIndex] = oldFileName;
            _externalTodoWindows.Remove(targetPath);
            if (window is not null) _externalTodoWindows[fullPath] = window;
            _trayHiddenExternalTodos.Remove(targetPath);
            if (wasHidden) _trayHiddenExternalTodos.Add(fullPath);
            throw;
        }

        window?.RebindExternalPath(targetPath);
        if (organizer is not null && _windows.TryGetValue(organizer.Id, out MainWindow? owner))
        {
            try { await owner.RefreshNotesAsync(); }
            catch (Exception ex) { AppLogger.Error($"便携待办改名后刷新失败：{targetPath}", ex); }
        }
        return targetPath;
    }

    internal void RebindPortableWindowAfterMove(string oldPath, string newPath)
    {
        string source = Path.GetFullPath(oldPath);
        string destination = Path.GetFullPath(newPath);
        if (_externalNoteWindows.Remove(source, out NoteWindow? noteWindow))
        {
            bool hidden = _trayHiddenExternalNotes.Remove(source);
            _externalNoteWindows[destination] = noteWindow;
            if (hidden) _trayHiddenExternalNotes.Add(destination);
            noteWindow.RebindExternalPath(destination);
        }
        if (_externalTodoWindows.Remove(source, out TodoWindow? todoWindow))
        {
            bool hidden = _trayHiddenExternalTodos.Remove(source);
            _externalTodoWindows[destination] = todoWindow;
            if (hidden) _trayHiddenExternalTodos.Add(destination);
            todoWindow.RebindExternalPath(destination);
        }
    }

    internal async Task<bool> FlushPortableWindowAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (_externalNoteWindows.TryGetValue(fullPath, out NoteWindow? noteWindow))
            return await noteWindow.FlushForExitAsync();
        if (_externalTodoWindows.TryGetValue(fullPath, out TodoWindow? todoWindow))
            return await todoWindow.FlushForExitAsync();
        return true;
    }

    internal void ClosePortableWindowWithoutSave(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (_externalNoteWindows.Remove(fullPath, out NoteWindow? noteWindow))
        {
            _trayHiddenExternalNotes.Remove(fullPath);
            noteWindow.ClosePermanentlyWithoutSave();
        }
        if (_externalTodoWindows.Remove(fullPath, out TodoWindow? todoWindow))
        {
            _trayHiddenExternalTodos.Remove(fullPath);
            todoWindow.ClosePermanentlyWithoutSave();
        }
    }

    internal async Task SetNoteThemeAsync(NoteTheme theme)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        NoteTheme previous = State.GlobalSettings.NoteTheme;
        State.GlobalSettings.NoteTheme = theme;
        foreach (NoteDefinition note in State.Organizers.SelectMany(organizer => organizer.Notes)) note.Theme = theme;
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            State.GlobalSettings.NoteTheme = previous;
            foreach (NoteDefinition note in State.Organizers.SelectMany(organizer => organizer.Notes)) note.Theme = previous;
            throw;
        }

        foreach (NoteWindow window in _noteWindows.Values.Concat(_externalNoteWindows.Values))
            await window.ApplyGlobalThemeAsync(theme);
        foreach (TodoWindow window in _externalTodoWindows.Values)
            await window.ApplyGlobalThemeAsync(theme);

        var openNotePaths = _externalNoteWindows.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var openTodoPaths = _externalTodoWindows.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visitedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OrganizerDefinition organizer in State.Organizers)
        {
            string root;
            try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.ResolveStoragePath(organizer))); }
            catch (Exception ex)
            {
                AppLogger.Error($"无法解析便签主题同步目录：{organizer.Id}", ex);
                continue;
            }
            if (!visitedRoots.Add(root)) continue;
            _ = await _noteStore.ApplyThemeToTopLevelPortableFilesAsync(root, theme, openNotePaths);
            _ = await _noteStore.ApplyThemeToTopLevelTodoFilesAsync(root, theme, openTodoPaths);
        }
    }

    internal async Task DeleteNoteAsync(Guid organizerId, Guid noteId)
    {
        OrganizerDefinition organizer = State.Organizers.First(item => item.Id == organizerId);
        NoteDefinition note = organizer.Notes.First(item => item.Id == noteId);
        int index = organizer.Notes.IndexOf(note);
        int orderIndex = organizer.ItemOrder.FindIndex(key =>
            key.Equals(OrganizerNoteRules.ItemKey(noteId), StringComparison.OrdinalIgnoreCase));
        if (_noteWindows.Remove(noteId, out NoteWindow? noteWindow)) await noteWindow.ClosePermanentlyAsync();
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

    private static PortableNotePlacement CreateTodoPlacement(NativeMethods.POINT anchor)
    {
        DisplayInfo display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
        {
            Left = anchor.X,
            Top = anchor.Y,
            Right = anchor.X + 1,
            Bottom = anchor.Y + 1
        });
        int width = (int)Math.Round(360 * display.Scale);
        int height = (int)Math.Round(480 * display.Scale);
        int offset = (int)Math.Round(16 * display.Scale);
        NativeMethods.RECT bounds = DisplayPlacementService.Clamp(new NativeMethods.RECT
        {
            Left = anchor.X + offset,
            Top = anchor.Y + offset,
            Right = anchor.X + offset + width,
            Bottom = anchor.Y + offset + height
        }, display.Work);
        return new PortableNotePlacement
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

    internal async Task<string?> ToggleOrganizerExpandedContentModeAsync(Guid id)
    {
        OrganizerDefinition current = State.Organizers.First(item => item.Id == id);
        OrganizerDefinition edited = OrganizerInteractionMath.CopySettings(current, current.Name);
        edited.Id = current.Id;
        if (!OrganizerInteractionMath.TryToggleExpandedContentMode(edited))
            return AppStrings.Get("StationManageModeError");

        string? error = ApplyOrganizerRuntime(edited, OrganizerVisualChange.ExpandedContentMode);
        if (error is not null) return error;
        await SaveStateAsync();
        Console.RefreshAll(id);
        return null;
    }

    internal string? ApplyOrganizerRuntime(OrganizerDefinition edited, OrganizerVisualChange changes)
    {
        OrganizerDefinition current = State.Organizers.First(item => item.Id == edited.Id);
        if (!OrganizerInteractionMath.CanChangePlacementMode(current.PlacementMode, edited.PlacementMode))
            return AppStrings.Get("StationManageModeError");
        if (edited.PlacementMode == OrganizerPlacementMode.Station)
            edited.ExpandedContentMode = OrganizerExpandedContentMode.Icon;
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
        current.PlacementMode = edited.PlacementMode;
        current.DockEdge = edited.DockEdge;
        if (edited.PlacementMode == OrganizerPlacementMode.Station && edited.Position is not null)
            current.Position = edited.Position;
        current.Layout = new OrganizerLayout { Mode = edited.Layout.Mode, Rows = edited.Layout.Rows, Columns = edited.Layout.Columns };
        current.CompactScale = edited.CompactScale;
        current.CanvasScale = edited.CanvasScale;
        current.ItemScale = edited.ItemScale;
        current.NameScale = edited.NameScale;
        current.CompactListItemScale = edited.CompactListItemScale;
        current.ExpandedContentMode = edited.ExpandedContentMode;
        current.CompactListCanvasWidthDip = edited.CompactListCanvasWidthDip;
        current.CompactListCanvasHeightDip = edited.CompactListCanvasHeightDip;
        if (layoutChanged)
        {
            current.ManualCanvasBaseWidthDip = null;
            current.ManualCanvasBaseHeightDip = null;
        }
        StateStore.Normalize(State);
        if (current.ContainerStationId is Guid parentStationId &&
            _windows.TryGetValue(parentStationId, out MainWindow? parentStation))
        {
            _ = parentStation.RefreshContainedOrganizerItemsAsync();
        }
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
                if (current.ContainerStationId is not null) window.SetContained(true);
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
                if (current.ContainerStationId is not null) window.SetContained(true);
                return null;
            }

            window.ApplyDefinition(changes);
            if (current.ContainerStationId is not null) window.SetContained(true);
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

    public async Task<TransferOutcome> DeleteOrganizerAsync(Guid id)
    {
        if (TransferQueue.IsActive) return new(string.Empty, null, TransferStatus.Failed, AppStrings.Get("TransferBeforeDelete"));
        OrganizerDefinition definition = State.Organizers.First(item => item.Id == id);
        if (definition.PlacementMode == OrganizerPlacementMode.Station &&
            State.Organizers.Any(item => item.ContainerStationId == definition.Id))
        {
            return new(
                AppPaths.ResolveStoragePath(definition),
                null,
                TransferStatus.Failed,
                AppStrings.Get("StationContainsOrganizersError"));
        }
        var legacyWindows = new List<(Guid Id, NoteWindow Window, bool WasVisible, bool WasTrayHidden)>();
        try
        {
            foreach (NoteDefinition note in definition.Notes)
            {
                if (!_noteWindows.TryGetValue(note.Id, out NoteWindow? noteWindow)) continue;
                bool wasVisible = await noteWindow.FlushAndHideForDragAsync();
                legacyWindows.Add((note.Id, noteWindow, wasVisible, _trayHiddenNotes.Contains(note.Id)));
            }
        }
        catch (Exception ex)
        {
            foreach (var item in legacyWindows.Where(item => item.WasVisible)) item.Window.RestoreAfterDrag();
            return new(AppPaths.ResolveStoragePath(definition), null, TransferStatus.Failed, ex.Message);
        }

        IReadOnlyDictionary<Guid, string> migratedPaths = await MigrateLegacyOrganizerNotesAsync(definition);
        foreach (var item in legacyWindows)
        {
            if (!migratedPaths.TryGetValue(item.Id, out string? path))
            {
                if (item.WasVisible) item.Window.RestoreAfterDrag();
                continue;
            }
            _noteWindows.Remove(item.Id);
            _trayHiddenNotes.Remove(item.Id);
            item.Window.ClosePermanentlyWithoutSave();
            await OpenExternalNoteAsync(path);
            if (!item.WasVisible && _externalNoteWindows.TryGetValue(path, out NoteWindow? migratedWindow))
            {
                migratedWindow.HideForTray();
                if (item.WasTrayHidden) _trayHiddenExternalNotes.Add(path);
            }
        }
        if (definition.Notes.Count > 0)
            return new(AppPaths.ResolveStoragePath(definition), null, TransferStatus.Failed, AppStrings.Get("DeleteLegacyNoteMigrationFailed"));

        string sourceRoot = Path.GetFullPath(AppPaths.ResolveStoragePath(definition));
        TransferOutcome outcome;
        if (State.GlobalSettings.MoveOrganizerFilesToDesktopOnDelete)
        {
            await _externalNoteOpenGate.WaitAsync();
            try
            {
            var movedNotes = new List<(string OldPath, NoteWindow Window, bool WasVisible, bool WasTrayHidden)>();
            var movedTodos = new List<(string OldPath, TodoWindow Window, bool WasVisible, bool WasTrayHidden)>();
            try
            {
                foreach ((string path, NoteWindow noteWindow) in _externalNoteWindows.Where(pair =>
                             OrganizerNoteRules.RebaseTopLevelPortablePath(sourceRoot, sourceRoot, pair.Key) is not null).ToArray())
                {
                    bool wasVisible = await noteWindow.FlushAndHideForDragAsync();
                    movedNotes.Add((path, noteWindow, wasVisible, _trayHiddenExternalNotes.Contains(path)));
                }
                foreach ((string path, TodoWindow todoWindow) in _externalTodoWindows.Where(pair =>
                             OrganizerNoteRules.RebaseTopLevelPortablePath(sourceRoot, sourceRoot, pair.Key) is not null).ToArray())
                {
                    bool wasVisible = await todoWindow.FlushAndHideForDragAsync();
                    movedTodos.Add((path, todoWindow, wasVisible, _trayHiddenExternalTodos.Contains(path)));
                }
            }
            catch (Exception ex)
            {
                foreach (var item in movedNotes.Where(item => item.WasVisible)) item.Window.RestoreAfterDrag();
                foreach (var item in movedTodos.Where(item => item.WasVisible)) item.Window.RestoreAfterDrag();
                return new(sourceRoot, null, TransferStatus.Failed, ex.Message);
            }

            var storage = new StorageService(
                sourceRoot,
                createIfMissing: false,
                ownedContainerPath: AppPaths.GetOwnedStorageContainer(definition),
                exportEmptyDirectory: !definition.StorageOwnedByApp);
            outcome = await TransferQueue.RunAsync(token => storage.ExportToDesktopAsync(definition.Name, null, token));
            if (outcome.Status != TransferStatus.Moved)
            {
                foreach (var item in movedNotes.Where(item => item.WasVisible)) item.Window.RestoreAfterDrag();
                foreach (var item in movedTodos.Where(item => item.WasVisible)) item.Window.RestoreAfterDrag();
                return outcome;
            }
            if (outcome.DestinationPath is not null)
            {
                string destinationRoot = Path.GetFullPath(outcome.DestinationPath);
                foreach (var item in movedNotes)
                {
                    string newPath = OrganizerNoteRules.ResolvePortablePathAfterMove(
                        sourceRoot,
                        destinationRoot,
                        item.OldPath,
                        moveSucceeded: true);
                    _externalNoteWindows.Remove(item.OldPath);
                    _externalNoteWindows[newPath] = item.Window;
                    _trayHiddenExternalNotes.Remove(item.OldPath);
                    if (item.WasTrayHidden) _trayHiddenExternalNotes.Add(newPath);
                    item.Window.RebindExternalPath(newPath);
                    if (item.WasVisible) item.Window.RestoreAfterDrag();
                }
                foreach (var item in movedTodos)
                {
                    string newPath = OrganizerNoteRules.ResolvePortablePathAfterMove(
                        sourceRoot,
                        destinationRoot,
                        item.OldPath,
                        moveSucceeded: true);
                    _externalTodoWindows.Remove(item.OldPath);
                    _externalTodoWindows[newPath] = item.Window;
                    _trayHiddenExternalTodos.Remove(item.OldPath);
                    if (item.WasTrayHidden) _trayHiddenExternalTodos.Add(newPath);
                    item.Window.RebindExternalPath(newPath);
                    if (item.WasVisible) item.Window.RestoreAfterDrag();
                }

                definition.StorageRelativePath = string.Empty;
                definition.StorageAbsolutePath = destinationRoot;
                definition.StorageOwnedByApp = false;
                try
                {
                    await SaveStateAsync();
                }
                catch (Exception ex)
                {
                    if (_windows.Remove(id, out MainWindow? staleWindow)) staleWindow.ClosePermanently();
                    CreateWindow(definition);
                    return new(sourceRoot, destinationRoot, TransferStatus.Failed, ex.Message);
                }
            }
            }
            finally
            {
                _externalNoteOpenGate.Release();
            }
        }
        else
        {
            outcome = new(sourceRoot, sourceRoot, TransferStatus.Retained, AppStrings.Format("OrganizerDeletedFilesRetainedFormat", sourceRoot));
        }

        int stateIndex = State.Organizers.IndexOf(definition);
        Guid? previousContainerId = definition.ContainerStationId;
        List<string>? previousContainerOrder = previousContainerId is Guid containerId
            ? State.Organizers.FirstOrDefault(item => item.Id == containerId)?.ItemOrder.ToList()
            : null;
        if (previousContainerId is Guid parentId && State.Organizers.FirstOrDefault(item => item.Id == parentId) is { } parent)
        {
            parent.ItemOrder.RemoveAll(key =>
                key.Equals(OrganizerInteractionMath.OrganizerItemKey(definition.Id), StringComparison.OrdinalIgnoreCase));
            definition.ContainerStationId = null;
        }
        State.Organizers.Remove(definition);
        try
        {
            await SaveStateAsync();
        }
        catch (Exception ex)
        {
            State.Organizers.Insert(Math.Max(0, stateIndex), definition);
            definition.ContainerStationId = previousContainerId;
            if (previousContainerId is Guid rollbackParentId && previousContainerOrder is not null &&
                State.Organizers.FirstOrDefault(item => item.Id == rollbackParentId) is { } rollbackParent)
            {
                rollbackParent.ItemOrder = previousContainerOrder;
            }
            if (outcome.Status == TransferStatus.Moved && outcome.DestinationPath is not null)
            {
                if (_windows.Remove(id, out MainWindow? staleWindow)) staleWindow.ClosePermanently();
                CreateWindow(definition);
            }
            return new(sourceRoot, outcome.DestinationPath, TransferStatus.Failed, ex.Message);
        }
        if (_windows.Remove(id, out MainWindow? window))
        {
            if (ReferenceEquals(_expandedWindow, window)) _expandedWindow = null;
            window.ClosePermanently();
        }
        if (previousContainerId is Guid previousStationId && _windows.TryGetValue(previousStationId, out MainWindow? previousStation))
            await previousStation.RefreshContainedOrganizerItemsAsync();
        Console.RefreshAll();
        return outcome;
    }

    public void RecreateStorage(Guid id)
    {
        if (_windows.TryGetValue(id, out MainWindow? window)) window.RecreateStorage();
        Console.RefreshAll(id);
    }

    internal void UpdateGlobalTheme(ThemeTarget target, uint colorArgb, ThemeMaterial material, double transparency)
    {
        ThemeValues theme = GlobalSettings.NormalizeTheme(new(colorArgb, material, transparency));
        GlobalSettings settings = State.GlobalSettings;
        if (settings.GetTheme(target) == theme) return;
        settings.SetTheme(target, theme);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
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

    public async Task SetDefaultStorageDirectoryAsync(string? path)
    {
        string? normalized = string.IsNullOrWhiteSpace(path) ? null : AppPaths.ValidateCustomStoragePath(path);
        string? previous = State.GlobalSettings.DefaultStorageDirectory;
        if (string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase)) return;
        State.GlobalSettings.DefaultStorageDirectory = normalized;
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            State.GlobalSettings.DefaultStorageDirectory = previous;
            throw;
        }
        Console.RefreshAll();
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
        foreach (TodoWindow window in _externalTodoWindows.Values) window.ApplyLanguage();
        _tray?.ApplyLanguage();
        Console.RefreshAll();
    }

    public async Task PrepareToExpandAsync(MainWindow source)
    {
        if (State.GlobalSettings.ExclusiveExpansion)
        {
            MainWindow[] unrelated = _windows.Values
                .Where(window => !ReferenceEquals(window, source) && window.IsExpanded &&
                    !IsContainedParentChildPair(window, source) && !window.IsShellDragActive)
                .OrderBy(window => window.ContainerStationId is null ? 1 : 0)
                .ToArray();
            foreach (MainWindow window in unrelated) await window.CollapseForPeerAsync();
        }
        _expandedWindow = source;
    }

    internal void RaiseActiveCompactOrganizerDrags(MainWindow station)
    {
        foreach (MainWindow source in _windows.Values.Where(window =>
                     !ReferenceEquals(window, station) && window.IsCompactOrganizerDragActive))
            source.RaiseActiveCompactOrganizerDrag();
    }

    internal async Task CollapseContainedChildrenAsync(Guid stationId)
    {
        foreach (MainWindow child in _windows.Values.Where(window =>
                     window.ContainerStationId == stationId && window.IsExpanded).ToArray())
        {
            await child.CollapseForPeerAsync();
        }
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
            if (!ReferenceEquals(window, keep) && window.IsExpanded && !window.IsShellDragActive &&
                (keep is null || !IsContainedParentChildPair(window, keep)))
                await window.CollapseForPeerAsync();
        }
    }

    public void NotifyCollapsed(MainWindow source)
    {
        if (!ReferenceEquals(_expandedWindow, source)) return;
        _expandedWindow = source.ContainerStationId is Guid stationId &&
            _windows.TryGetValue(stationId, out MainWindow? station) && station.IsExpanded
                ? station
                : null;
    }

    internal bool HasExpandedContainedChild(Guid stationId) =>
        _windows.Values.Any(window => window.ContainerStationId == stationId && window.IsExpanded);

    private static bool IsContainedParentChildPair(MainWindow first, MainWindow second) =>
        first.ContainerStationId == second.OrganizerId || second.ContainerStationId == first.OrganizerId;

    internal bool IsOrganizerStationDropTarget(MainWindow source, NativeMethods.POINT dropPoint) =>
        _windows.Values.Any(target =>
            !ReferenceEquals(target, source) &&
            target.TryGetStationDropIndex(dropPoint, out _));

    internal async Task<bool> TryContainDraggedOrganizerAsync(MainWindow source, NativeMethods.POINT dropPoint)
    {
        OrganizerDefinition organizer = State.Organizers.First(item => item.Id == source.OrganizerId);
        if (source.IsExpanded || organizer.ContainerStationId is not null ||
            organizer.PlacementMode is not (OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned)) return false;
        foreach (MainWindow target in _windows.Values)
        {
            if (!target.TryGetStationDropIndex(dropPoint, out int insertionIndex)) continue;
            await MoveOrganizerToStationAsync(organizer, target.OrganizerId, insertionIndex);
            return true;
        }
        return false;
    }

    internal async Task<string?> FinishContainedOrganizerDragAsync(Guid organizerId, NativeMethods.POINT dropPoint)
    {
        OrganizerDefinition organizer = State.Organizers.First(item => item.Id == organizerId);
        _windows.TryGetValue(organizer.Id, out MainWindow? window);
        foreach (MainWindow target in _windows.Values)
        {
            if (!target.TryGetStationDropIndex(dropPoint, out int insertionIndex)) continue;
            await MoveOrganizerToStationAsync(organizer, target.OrganizerId, insertionIndex);
            return null;
        }

        Guid? previousContainerId = organizer.ContainerStationId;
        if (previousContainerId is null) return null;
        DisplayInfo display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
        {
            Left = dropPoint.X,
            Top = dropPoint.Y,
            Right = dropPoint.X + 1,
            Bottom = dropPoint.Y + 1
        });
        NativeMethods.RECT bounds;
        DesktopGridPlacement? positionedPlacement = null;
        if (organizer.PlacementMode == OrganizerPlacementMode.Positioned)
        {
            positionedPlacement = FindPositionedPlacement(display, dropPoint, organizer.Id, organizer.CompactScale);
            if (positionedPlacement is null) return AppStrings.Get("ContainedOrganizerNoGridError");
            bounds = positionedPlacement.Bounds;
        }
        else
        {
            double nameScale = State.GlobalSettings.ResolveCompactNameScale(organizer.PlacementMode);
            int width = Math.Max(1, (int)Math.Round(
                OrganizerLimits.CalculateCompactWindowWidthDip(organizer.CompactScale, nameScale) * display.Scale));
            int height = Math.Max(1, (int)Math.Round(
                OrganizerLimits.CalculateCompactWindowHeightDip(organizer.CompactScale, nameScale) * display.Scale));
            var desired = new NativeMethods.RECT
            {
                Left = dropPoint.X - width / 2,
                Top = dropPoint.Y - height / 2,
                Right = dropPoint.X - width / 2 + width,
                Bottom = dropPoint.Y - height / 2 + height
            };
            bounds = DisplayPlacementService.CalculateDraggedBounds(desired, dropPoint, dropPoint, display.Work);
        }

        OrganizerDefinition parent = State.Organizers.First(item => item.Id == previousContainerId);
        List<string> previousOrder = parent.ItemOrder.ToList();
        WidgetPosition? previousPosition = organizer.Position;
        OrganizerInteractionMath.DetachOrganizerFromStation(State.Organizers, organizer.Id);
        organizer.Position = DisplayPlacementService.Capture(bounds);
        try
        {
            await SaveStateAsync();
        }
        catch (Exception ex)
        {
            organizer.ContainerStationId = previousContainerId;
            organizer.Position = previousPosition;
            parent.ItemOrder = previousOrder;
            return AppStrings.Format("OrganizerStationDropError", ex.Message);
        }

        if (window is not null)
        {
            try
            {
                if (positionedPlacement is not null) window.MoveToPositionedPlacement(bounds, positionedPlacement.CompactScale);
                else window.MoveToFloatingPlacement(bounds);
            }
            catch (Exception ex)
            {
                AppLogger.Error("收纳窗已拖回桌面，但窗口位置刷新失败。", ex);
            }
            finally
            {
                window.SetContained(false);
            }
        }
        try
        {
            if (_windows.TryGetValue(parent.Id, out MainWindow? parentWindow)) await parentWindow.RefreshContainedOrganizerItemsAsync();
            Console.RefreshAll(organizer.Id);
        }
        catch (Exception ex)
        {
            AppLogger.Error("收纳窗已拖回桌面，但界面刷新失败。", ex);
        }
        return null;
    }

    internal Task OpenContainedOrganizerAsync(Guid organizerId, NativeMethods.RECT anchor) =>
        _windows.TryGetValue(organizerId, out MainWindow? window)
            ? window.ExpandContainedAsync(anchor)
            : Task.CompletedTask;

    internal void UpdateContainedOrganizerDragPreview(Guid organizerId, NativeMethods.POINT cursor)
    {
        OrganizerDefinition? organizer = State.Organizers.FirstOrDefault(item => item.Id == organizerId);
        if (organizer?.ContainerStationId is not Guid stationId ||
            !_windows.TryGetValue(stationId, out MainWindow? station) ||
            !_windows.TryGetValue(organizerId, out MainWindow? window)) return;
        window.MoveContainedDragPreview(cursor, station);
    }

    internal void ReconcileContainedOrganizer(Guid organizerId)
    {
        OrganizerDefinition? organizer = State.Organizers.FirstOrDefault(item => item.Id == organizerId);
        if (organizer is not null && _windows.TryGetValue(organizerId, out MainWindow? window))
            window.SetContained(organizer.ContainerStationId is not null);
    }

    private async Task MoveOrganizerToStationAsync(
        OrganizerDefinition organizer,
        Guid stationId,
        int insertionIndex)
    {
        OrganizerDefinition station = State.Organizers.First(item => item.Id == stationId);
        if (!OrganizerInteractionMath.CanContainOrganizer(
                organizer.PlacementMode,
                station.PlacementMode,
                organizer.Id,
                station.Id))
            throw new InvalidOperationException("Invalid Station organizer reference.");

        Guid? previousContainerId = organizer.ContainerStationId;
        var orderSnapshots = State.Organizers
            .Where(item => item.PlacementMode == OrganizerPlacementMode.Station)
            .ToDictionary(item => item.Id, item => item.ItemOrder.ToList());
        if (!OrganizerInteractionMath.PlaceOrganizerInStation(State.Organizers, organizer.Id, station.Id, insertionIndex))
            throw new InvalidOperationException("Invalid Station organizer reference.");
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            organizer.ContainerStationId = previousContainerId;
            foreach ((Guid id, List<string> order) in orderSnapshots)
                State.Organizers.First(item => item.Id == id).ItemOrder = order;
            throw;
        }

        if (_windows.TryGetValue(organizer.Id, out MainWindow? sourceWindow)) sourceWindow.SetContained(true);
        try
        {
            foreach (Guid affectedId in new[] { previousContainerId, (Guid?)station.Id }.Where(id => id is not null).Select(id => id!.Value).Distinct())
            {
                if (_windows.TryGetValue(affectedId, out MainWindow? affectedWindow))
                    await affectedWindow.RefreshContainedOrganizerItemsAsync();
            }
            Console.RefreshAll(organizer.Id);
        }
        catch (Exception ex)
        {
            AppLogger.Error("收纳窗已移入中转站，但界面刷新失败。", ex);
        }
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
            .Where(window => State.Organizers.First(item => item.Id == window.OrganizerId) is
                { PlacementMode: OrganizerPlacementMode.Positioned, ContainerStationId: null })
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
        foreach (OrganizerDefinition organizer in State.Organizers.Where(item =>
                     item.PlacementMode == OrganizerPlacementMode.Positioned && item.ContainerStationId is null))
        {
            DisplayInfo display = displays.FirstOrDefault(item => string.Equals(item.Device, organizer.Position?.MonitorDevice, StringComparison.OrdinalIgnoreCase)) ?? primary;
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
            Notify("TuckPane", AppStrings.Get("GridFallbackMessage"));
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

    public async Task SetUniformCompactScaleEnabledAsync(OrganizerPlacementMode mode, bool enabled)
    {
        (bool previousEnabled, double previousScale) = GetUniformCompactScaleSetting(mode);
        if (previousEnabled == enabled) return;
        List<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> snapshots =
            CaptureCompactScaleSnapshots(mode);
        string? error = ApplyUniformCompactScaleSetting(mode, enabled, previousScale, snapshots);
        if (error is not null) throw new InvalidOperationException(error);
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            RestoreUniformCompactScaleSetting(mode, previousEnabled, previousScale, snapshots);
            throw;
        }
    }

    internal string? ApplyUniformCompactScale(OrganizerPlacementMode mode, double scale)
    {
        (bool enabled, _) = GetUniformCompactScaleSetting(mode);
        return ApplyUniformCompactScaleSetting(mode, enabled, scale, CaptureCompactScaleSnapshots(mode));
    }

    internal List<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> CaptureUniformCompactScaleSnapshots(
        OrganizerPlacementMode mode) =>
        CaptureCompactScaleSnapshots(mode);

    internal void RestoreUniformCompactScale(
        OrganizerPlacementMode mode,
        double scale,
        IReadOnlyList<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> snapshots)
    {
        (bool enabled, _) = GetUniformCompactScaleSetting(mode);
        RestoreUniformCompactScaleSetting(mode, enabled, scale, snapshots);
    }

    private string? ApplyUniformCompactScaleSetting(
        OrganizerPlacementMode mode,
        bool enabled,
        double scale,
        IReadOnlyList<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> snapshots)
    {
        (bool previousEnabled, double previousScale) = GetUniformCompactScaleSetting(mode);
        SetUniformCompactScaleSetting(mode, enabled, scale);
        if (!enabled) return null;

        try
        {
            foreach (var snapshot in snapshots)
            {
                OrganizerDefinition current = State.Organizers.First(item => item.Id == snapshot.Definition.Id);
                OrganizerDefinition edited = OrganizerInteractionMath.CopySettings(current, current.Name);
                edited.Id = current.Id;
                edited.CompactScale = State.GlobalSettings.ResolveCompactScale(mode, current.CompactScale);
                string? error = ApplyOrganizerRuntime(edited, OrganizerVisualChange.CompactScale);
                if (error is null) continue;
                RestoreUniformCompactScaleSetting(mode, previousEnabled, previousScale, snapshots);
                return error;
            }
        }
        catch (Exception ex)
        {
            RestoreUniformCompactScaleSetting(mode, previousEnabled, previousScale, snapshots);
            AppLogger.Error("无法批量应用统一入口大小。", ex);
            return ex.Message;
        }
        return null;
    }

    private void RestoreUniformCompactScaleSetting(
        OrganizerPlacementMode mode,
        bool enabled,
        double scale,
        IReadOnlyList<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> snapshots)
    {
        SetUniformCompactScaleSetting(mode, enabled, scale);
        foreach (var snapshot in snapshots)
        {
            OrganizerDefinition current = State.Organizers.First(item => item.Id == snapshot.Definition.Id);
            current.CompactScale = snapshot.Definition.CompactScale;
            current.Position = snapshot.Definition.Position;
            if (!_windows.TryGetValue(current.Id, out MainWindow? window)) continue;
            try
            {
                if (mode == OrganizerPlacementMode.Positioned)
                {
                    window.MoveToPositionedPlacement(snapshot.Bounds, snapshot.RuntimeScale);
                }
                else
                {
                    window.MoveToFloatingPlacement(snapshot.Bounds);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("无法回滚统一入口大小。", ex);
            }
        }
    }

    private (bool Enabled, double Scale) GetUniformCompactScaleSetting(OrganizerPlacementMode mode) => mode switch
    {
        OrganizerPlacementMode.Floating => (
            State.GlobalSettings.UseUniformFloatingCompactScale,
            State.GlobalSettings.UniformFloatingCompactScale),
        OrganizerPlacementMode.Positioned => (
            State.GlobalSettings.UseUniformPositionedCompactScale,
            State.GlobalSettings.UniformPositionedCompactScale),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private void SetUniformCompactScaleSetting(OrganizerPlacementMode mode, bool enabled, double scale)
    {
        scale = GlobalSettings.NormalizeCompactScale(mode, scale);
        switch (mode)
        {
            case OrganizerPlacementMode.Floating:
                State.GlobalSettings.UseUniformFloatingCompactScale = enabled;
                State.GlobalSettings.UniformFloatingCompactScale = scale;
                break;
            case OrganizerPlacementMode.Positioned:
                State.GlobalSettings.UseUniformPositionedCompactScale = enabled;
                State.GlobalSettings.UniformPositionedCompactScale = scale;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    internal void ApplyNameScales(double compactScale, double expandedScale)
    {
        compactScale = GlobalSettings.NormalizeCompactNameScale(compactScale);
        expandedScale = GlobalSettings.NormalizeCompactNameScale(expandedScale);
        State.GlobalSettings.UniformFloatingCompactNameScale = compactScale;
        State.GlobalSettings.ExpandedNameScale = expandedScale;
        foreach (OrganizerDefinition organizer in State.Organizers.Where(item =>
                     item.PlacementMode is OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned))
            if (_windows.TryGetValue(organizer.Id, out MainWindow? window)) window.ApplyDefinition(OrganizerVisualChange.NameScale);
    }

    private List<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> CaptureCompactScaleSnapshots(
        OrganizerPlacementMode mode) => State.Organizers
        .Where(item => item.PlacementMode == mode)
        .Select(item =>
        {
            OrganizerDefinition snapshot = OrganizerInteractionMath.CopySettings(item, item.Name);
            snapshot.Id = item.Id;
            snapshot.Position = item.Position is null ? null : new WidgetPosition
            {
                MonitorDevice = item.Position.MonitorDevice,
                XDip = item.Position.XDip,
                YDip = item.Position.YDip,
                SavedWorkAreaWidthDip = item.Position.SavedWorkAreaWidthDip,
                SavedWorkAreaHeightDip = item.Position.SavedWorkAreaHeightDip
            };
            NativeMethods.RECT bounds = _windows.TryGetValue(item.Id, out MainWindow? window)
                ? window.CompactBounds
                : default;
            return (snapshot, bounds, window?.AppliedCompactScale ?? item.CompactScale);
        })
        .ToList();

    public async Task SetCollapseOnOutsideClickAsync(bool enabled)
    {
        if (State.GlobalSettings.CollapseOnOutsideClick == enabled) return;
        State.GlobalSettings.CollapseOnOutsideClick = enabled;
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            State.GlobalSettings.CollapseOnOutsideClick = !enabled;
            throw;
        }
        foreach (MainWindow window in _windows.Values) window.ApplyOutsideClickSetting();
    }

    public async Task SetNoteAlwaysOnTopAsync(bool enabled)
    {
        if (State.GlobalSettings.NoteAlwaysOnTop == enabled) return;
        State.GlobalSettings.NoteAlwaysOnTop = enabled;
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            State.GlobalSettings.NoteAlwaysOnTop = !enabled;
            throw;
        }
        foreach (NoteWindow window in _noteWindows.Values.Concat(_externalNoteWindows.Values))
            window.ApplyAlwaysOnTopSetting();
        foreach (TodoWindow window in _externalTodoWindows.Values)
            window.ApplyAlwaysOnTopSetting();
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
        foreach (MainWindow window in _windows.Values) window.RefreshPerformanceSettings();
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
        foreach (MainWindow window in _windows.Values) window.RefreshPerformanceSettings();
    }

    public async Task SetPerformanceProfileAsync(PerformanceProfile profile)
    {
        if (!Enum.IsDefined(profile)) profile = PerformanceProfile.Balanced;
        if (State.GlobalSettings.PerformanceProfile == profile) return;
        PerformanceProfile previous = State.GlobalSettings.PerformanceProfile;
        State.GlobalSettings.PerformanceProfile = profile;
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            State.GlobalSettings.PerformanceProfile = previous;
            throw;
        }
        foreach (MainWindow window in _windows.Values) window.RefreshPerformanceSettings();
    }

    public async Task SetWindowAlignmentEnabledAsync(bool enabled)
    {
        if (State.GlobalSettings.WindowAlignmentEnabled == enabled) return;
        State.GlobalSettings.WindowAlignmentEnabled = enabled;
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            State.GlobalSettings.WindowAlignmentEnabled = !enabled;
            throw;
        }
        foreach (MainWindow window in _windows.Values) window.RefreshWindowAlignmentSetting();
    }

    public void SetHoverDelays(
        int hoverExpandDelayMs,
        int pointerLeaveCollapseDelayMs,
        int stationPointerLeaveCollapseDelayMs)
    {
        State.GlobalSettings.HoverExpandDelayMs = GlobalSettings.NormalizeHoverDelayMs(hoverExpandDelayMs);
        State.GlobalSettings.PointerLeaveCollapseDelayMs = GlobalSettings.NormalizeHoverDelayMs(pointerLeaveCollapseDelayMs);
        State.GlobalSettings.StationPointerLeaveCollapseDelayMs = GlobalSettings.NormalizeHoverDelayMs(
            stationPointerLeaveCollapseDelayMs);
        foreach (MainWindow window in _windows.Values) window.RefreshHoverDelays();
    }

    public void SetStationActivation(int distanceDip, int hoverExpandDelayMs)
    {
        State.GlobalSettings.StationActivationDistanceDip =
            GlobalSettings.NormalizeStationActivationDistanceDip(distanceDip);
        State.GlobalSettings.StationHoverExpandDelayMs =
            GlobalSettings.NormalizeStationHoverExpandDelayMs(hoverExpandDelayMs);
        foreach (MainWindow window in _windows.Values) window.RefreshHoverDelays();
    }

    public Task SaveStateAsync() => _stateStore.SaveAsync(State);

    public async Task ExitAsync()
    {
        if (_exiting) return;
        if (TransferQueue.IsActive && !await Console.ConfirmCancelTransferAndExitAsync()) return;
        if (!await Console.FlushPendingThemeSaveAsync()) return;
        if (!await Console.FlushPendingManageChangesAsync())
        {
            Console.ShowAndActivate();
            return;
        }

        NoteWindow[] noteWindows = _noteWindows.Values
            .Concat(_externalNoteWindows.Values)
            .Distinct()
            .ToArray();
        foreach (NoteWindow window in noteWindows)
        {
            if (await window.FlushForExitAsync()) continue;
            window.ShowAndActivate();
            return;
        }
        TodoWindow[] todoWindows = _externalTodoWindows.Values.Distinct().ToArray();
        foreach (TodoWindow window in todoWindows)
        {
            if (await window.FlushForExitAsync()) continue;
            window.ShowAndActivate();
            return;
        }

        _exiting = true;
        TransferQueue.CancelAll();
        await TransferQueue.WaitForIdleAsync();
        foreach (NoteWindow window in noteWindows) window.ClosePermanentlyWithoutSave();
        foreach (TodoWindow window in todoWindows) window.ClosePermanentlyWithoutSave();
        _noteWindows.Clear();
        _externalNoteWindows.Clear();
        _externalTodoWindows.Clear();
        foreach (MainWindow window in _windows.Values.ToArray()) window.ClosePermanently();
        _windows.Clear();
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
        if (organizer.ContainerStationId is not null) window.SetContained(true);
        else if (organizer.PlacementMode == OrganizerPlacementMode.Station) window.SetVisible(true);
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
                    foreach (string path in _trayHiddenExternalTodos.ToArray())
                    {
                        if (_externalTodoWindows.TryGetValue(path, out TodoWindow? todoWindow)) todoWindow.RestoreFromTray();
                    }
                    _trayHiddenExternalTodos.Clear();
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
                    _trayHiddenExternalTodos.Clear();
                    foreach ((string path, TodoWindow todoWindow) in _externalTodoWindows)
                    {
                        if (!todoWindow.IsVisible) continue;
                        _trayHiddenExternalTodos.Add(path);
                        todoWindow.HideForTray();
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

    public void Dispose()
    {
        _tray?.Dispose();
        foreach (NoteWindow window in _noteWindows.Values) _ = window.ClosePermanentlyAsync();
        foreach (NoteWindow window in _externalNoteWindows.Values) _ = window.ClosePermanentlyAsync();
        foreach (TodoWindow window in _externalTodoWindows.Values) _ = window.ClosePermanentlyAsync();
        foreach (MainWindow window in _windows.Values) window.ClosePermanently();
    }
}
