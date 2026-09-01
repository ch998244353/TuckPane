using System.Text.Json;
using TuckPane.Core;
using TuckPane.Models;

namespace TuckPane.Services;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _statePath;
    private readonly string _backupPath;

    public StateStore(string? statePath = null)
    {
        _statePath = Path.GetFullPath(statePath ?? AppPaths.StatePath);
        _backupPath = _statePath + ".bak";
    }

    public async Task<AppStateV2> LoadAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        LoadResult? loaded = await TryLoadAsync(_statePath) ?? await TryLoadAsync(_backupPath);
        AppStateV2 state = Normalize(loaded?.State ?? new AppStateV2());
        if (loaded is { RequiresMigration: true }) await PersistMigrationAsync(state, loaded.SourcePath);
        return state;
    }

    public async Task SaveAsync(AppStateV2 state)
    {
        await _saveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            string temporary = _statePath + ".tmp";
            string json = JsonSerializer.Serialize(Normalize(state), JsonOptions);
            await File.WriteAllTextAsync(temporary, json);
            if (File.Exists(_statePath)) File.Copy(_statePath, _backupPath, overwrite: true);
            await MoveIntoPlaceAsync(temporary, _statePath);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task PersistMigrationAsync(AppStateV2 state, string legacyPath)
    {
        await _saveGate.WaitAsync();
        try
        {
            string temporary = _statePath + ".tmp";
            if (!legacyPath.Equals(_backupPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(legacyPath, _backupPath, overwrite: true);
            }
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions));
            await MoveIntoPlaceAsync(temporary, _statePath);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static async Task MoveIntoPlaceAsync(string temporary, string destination)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 9 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(50);
            }
        }
    }

    private static async Task<LoadResult?> TryLoadAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            string json = await File.ReadAllTextAsync(path);
            using JsonDocument document = JsonDocument.Parse(json);
            int schemaVersion = document.RootElement.TryGetProperty("SchemaVersion", out JsonElement schema)
                ? schema.GetInt32()
                : 1;
            if (schemaVersion >= 2)
            {
                AppStateV2? current = JsonSerializer.Deserialize<AppStateV2>(json, JsonOptions);
                if (current is null) return null;
                current.GlobalSettings ??= new GlobalSettings();
                if (schemaVersion < 3) current.GlobalSettings.Language = AppLanguage.ChineseSimplified;
                if (schemaVersion < 6) current.GlobalSettings.NoteTheme = NoteTheme.SunYellow;
                if (schemaVersion < 7)
                {
                    current.GlobalSettings.ThemeColorArgb = GlobalSettings.DefaultThemeColorArgb;
                    current.GlobalSettings.Material = ThemeMaterial.Acrylic;
                    current.GlobalSettings.ThemeTransparency = GlobalSettings.DefaultThemeTransparency;
                }
                if (schemaVersion < 8)
                {
                    ThemeValues organizerTheme = current.GlobalSettings.GetTheme(ThemeTarget.Organizer);
                    current.GlobalSettings.SetTheme(ThemeTarget.Settings, organizerTheme);
                }
                if (schemaVersion < 9)
                {
                    foreach (OrganizerDefinition organizer in current.Organizers ?? [])
                        organizer.StorageOwnedByApp = string.IsNullOrWhiteSpace(organizer.StorageAbsolutePath);
                }
                return new(current, schemaVersion < 9, path);
            }

            AppStateV1 legacy = JsonSerializer.Deserialize<AppStateV1>(json, JsonOptions) ?? new AppStateV1();
            return new(Migrate(legacy, File.GetCreationTimeUtc(path)), true, path);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法读取状态文件：{path}", ex);
            return null;
        }
    }

    private sealed record LoadResult(AppStateV2 State, bool RequiresMigration, string SourcePath);

    internal static AppStateV2 Migrate(AppStateV1 legacy, DateTime createdAtUtc)
    {
        var organizer = new OrganizerDefinition
        {
            Name = string.IsNullOrWhiteSpace(legacy.WidgetName) ? "文件夹" : legacy.WidgetName.Trim(),
            CreatedAtUtc = createdAtUtc == default ? DateTimeOffset.UtcNow : createdAtUtc,
            StorageRelativePath = "Items",
            StorageOwnedByApp = true,
            Position = legacy.Position,
            ItemOrder = legacy.ItemOrder.ToList(),
            Layout = new OrganizerLayout { Mode = OrganizerLayoutMode.Grid, Rows = 3, Columns = 3 }
        };
        return new AppStateV2
        {
            GlobalSettings = new GlobalSettings
            {
                NoteTheme = NoteTheme.SunYellow,
                StartWithWindows = legacy.StartWithWindows
            },
            Organizers = [organizer]
        };
    }

    internal static AppStateV2 Normalize(AppStateV2 state)
    {
        state.SchemaVersion = 9;
        state.GlobalSettings ??= new GlobalSettings();
        if (string.IsNullOrWhiteSpace(state.GlobalSettings.DefaultStorageDirectory))
        {
            state.GlobalSettings.DefaultStorageDirectory = null;
        }
        else
        {
            try
            {
                string path = state.GlobalSettings.DefaultStorageDirectory.Trim();
                state.GlobalSettings.DefaultStorageDirectory = Path.IsPathFullyQualified(path) &&
                    !path.StartsWith(@"\\", StringComparison.Ordinal)
                    ? Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : null;
            }
            catch
            {
                state.GlobalSettings.DefaultStorageDirectory = null;
            }
        }
        state.GlobalSettings.SetTheme(
            ThemeTarget.Organizer,
            GlobalSettings.NormalizeTheme(state.GlobalSettings.GetTheme(ThemeTarget.Organizer)));
        state.GlobalSettings.SetTheme(
            ThemeTarget.Settings,
            GlobalSettings.NormalizeTheme(state.GlobalSettings.GetTheme(ThemeTarget.Settings)));
        if (!Enum.IsDefined(state.GlobalSettings.NoteTheme)) state.GlobalSettings.NoteTheme = NoteTheme.SunYellow;
        if (!Enum.IsDefined(state.GlobalSettings.Language)) state.GlobalSettings.Language = AppLanguage.ChineseSimplified;
        if (!Enum.IsDefined(state.GlobalSettings.PerformanceProfile))
            state.GlobalSettings.PerformanceProfile = PerformanceProfile.Balanced;
        state.GlobalSettings.HoverExpandDelayMs = GlobalSettings.NormalizeHoverDelayMs(state.GlobalSettings.HoverExpandDelayMs);
        state.GlobalSettings.PointerLeaveCollapseDelayMs = GlobalSettings.NormalizeHoverDelayMs(state.GlobalSettings.PointerLeaveCollapseDelayMs);
        state.GlobalSettings.StationPointerLeaveCollapseDelayMs = GlobalSettings.NormalizeHoverDelayMs(
            state.GlobalSettings.StationPointerLeaveCollapseDelayMs);
        state.GlobalSettings.StationActivationDistanceDip = GlobalSettings.NormalizeStationActivationDistanceDip(
            state.GlobalSettings.StationActivationDistanceDip);
        state.GlobalSettings.StationHoverExpandDelayMs = GlobalSettings.NormalizeStationHoverExpandDelayMs(
            state.GlobalSettings.StationHoverExpandDelayMs);
        state.GlobalSettings.UniformFloatingCompactScale = GlobalSettings.NormalizeCompactScale(
            OrganizerPlacementMode.Floating,
            state.GlobalSettings.UniformFloatingCompactScale);
        state.GlobalSettings.UniformPositionedCompactScale = GlobalSettings.NormalizeCompactScale(
            OrganizerPlacementMode.Positioned,
            state.GlobalSettings.UniformPositionedCompactScale);
        state.GlobalSettings.UniformFloatingCompactNameScale = GlobalSettings.NormalizeCompactNameScale(
            state.GlobalSettings.UniformFloatingCompactNameScale);
        state.GlobalSettings.UniformPositionedCompactNameScale = GlobalSettings.NormalizeCompactNameScale(
            state.GlobalSettings.UniformPositionedCompactNameScale);
        state.GlobalSettings.ExpandedNameScale = GlobalSettings.NormalizeCompactNameScale(
            state.GlobalSettings.ExpandedNameScale);
        state.Organizers ??= [];
        var normalizedOrganizers = new List<OrganizerDefinition>();
        var stationEdges = new HashSet<OrganizerDockEdge>();
        int regularCount = 0;
        foreach (OrganizerDefinition organizer in state.Organizers)
        {
            if (!Enum.IsDefined(organizer.PlacementMode)) organizer.PlacementMode = OrganizerPlacementMode.Floating;
            if (!Enum.IsDefined(organizer.DockEdge)) organizer.DockEdge = OrganizerDockEdge.Right;
            if (organizer.PlacementMode == OrganizerPlacementMode.Station && !stationEdges.Add(organizer.DockEdge))
                organizer.PlacementMode = OrganizerPlacementMode.Floating;
            if (organizer.PlacementMode == OrganizerPlacementMode.Station || regularCount++ < OrganizerLimits.MaximumOrganizers)
                normalizedOrganizers.Add(organizer);
        }
        state.Organizers = normalizedOrganizers;

        var ids = new HashSet<Guid>();
        var noteIds = new HashSet<Guid>();
        foreach (OrganizerDefinition organizer in state.Organizers)
        {
            if (organizer.Id == Guid.Empty || !ids.Add(organizer.Id))
            {
                organizer.Id = Guid.NewGuid();
                ids.Add(organizer.Id);
            }
            organizer.Name = string.IsNullOrWhiteSpace(organizer.Name) ? "收纳窗" : organizer.Name.Trim();
            if (organizer.CreatedAtUtc == default) organizer.CreatedAtUtc = DateTimeOffset.UtcNow;
            if (!Enum.IsDefined(organizer.PlacementMode)) organizer.PlacementMode = OrganizerPlacementMode.Floating;
            if (!Enum.IsDefined(organizer.DockEdge)) organizer.DockEdge = OrganizerDockEdge.Right;
            organizer.Layout ??= new OrganizerLayout();
            bool station = organizer.PlacementMode == OrganizerPlacementMode.Station;
            if (!Enum.IsDefined(organizer.ExpandedContentMode) || station)
                organizer.ExpandedContentMode = OrganizerExpandedContentMode.Icon;
            organizer.CompactListCanvasWidthDip = double.IsFinite(organizer.CompactListCanvasWidthDip)
                ? Math.Clamp(
                    organizer.CompactListCanvasWidthDip,
                    OrganizerLimits.MinimumCompactListCanvasWidthDip,
                    OrganizerLimits.MaximumCompactListCanvasSizeDip)
                : OrganizerLimits.DefaultCompactListCanvasWidthDip;
            organizer.CompactListCanvasHeightDip = double.IsFinite(organizer.CompactListCanvasHeightDip)
                ? Math.Clamp(
                    organizer.CompactListCanvasHeightDip,
                    OrganizerLimits.MinimumCompactListCanvasHeightDip,
                    OrganizerLimits.MaximumCompactListCanvasSizeDip)
                : OrganizerLimits.DefaultCompactListCanvasHeightDip;
            if (organizer.Layout.Mode != OrganizerLayoutMode.Grid)
            {
                organizer.Layout.Mode = OrganizerLayoutMode.Grid;
                organizer.Layout.Rows = 3;
                organizer.Layout.Columns = 3;
            }
            else
            {
                organizer.Layout.Rows = Math.Clamp(
                    organizer.Layout.Rows,
                    station ? OrganizerLimits.MinimumStationRows : OrganizerLimits.MinimumGridDimension,
                    station ? OrganizerLimits.MaximumStationRows : OrganizerLimits.MaximumLayoutDimension);
                organizer.Layout.Columns = Math.Clamp(
                    organizer.Layout.Columns,
                    station ? OrganizerLimits.MinimumStationColumns : OrganizerLimits.MinimumGridDimension,
                    station ? OrganizerLimits.MaximumStationColumns : OrganizerLimits.MaximumLayoutDimension);
            }
            organizer.CompactScale = state.GlobalSettings.ResolveCompactScale(
                organizer.PlacementMode,
                organizer.CompactScale);
            organizer.CanvasScale = Math.Clamp(organizer.CanvasScale, .1, 1.2);
            organizer.ItemScale = Math.Clamp(organizer.ItemScale, .5, 1.65);
            organizer.NameScale = GlobalSettings.NormalizeCompactNameScale(organizer.NameScale);
            organizer.CompactListItemScale = double.IsFinite(organizer.CompactListItemScale)
                ? Math.Clamp(organizer.CompactListItemScale, .5, 1.65)
                : 1;
            if (station)
            {
                organizer.ManualCanvasBaseWidthDip = null;
                organizer.ManualCanvasBaseHeightDip = null;
            }
            else if (organizer.ManualCanvasBaseWidthDip is not double baseWidth ||
                organizer.ManualCanvasBaseHeightDip is not double baseHeight ||
                !double.IsFinite(baseWidth) || !double.IsFinite(baseHeight) ||
                baseWidth <= 0 || baseHeight <= 0)
            {
                organizer.ManualCanvasBaseWidthDip = null;
                organizer.ManualCanvasBaseHeightDip = null;
            }
            else
            {
                organizer.ManualCanvasBaseWidthDip = Math.Clamp(baseWidth, 1, 10000);
                organizer.ManualCanvasBaseHeightDip = Math.Clamp(baseHeight, 1, 10000);
            }
            if (!string.IsNullOrWhiteSpace(organizer.StorageAbsolutePath))
            {
                string absolute = organizer.StorageAbsolutePath.Trim();
                organizer.StorageAbsolutePath = Path.IsPathFullyQualified(absolute) ? Path.GetFullPath(absolute) : absolute;
                organizer.StorageRelativePath = string.Empty;
            }
            else
            {
                organizer.StorageAbsolutePath = null;
                organizer.StorageOwnedByApp = true;
                organizer.StorageRelativePath = string.IsNullOrWhiteSpace(organizer.StorageRelativePath)
                    ? AppPaths.CreateStorageRelativePath(organizer.Name, organizer.Id)
                    : organizer.StorageRelativePath;
                try
                {
                    _ = AppPaths.ResolveStoragePath(organizer.StorageRelativePath);
                }
                catch
                {
                    organizer.StorageRelativePath = AppPaths.CreateStorageRelativePath(organizer.Name, organizer.Id);
                }
            }
            organizer.Notes ??= [];
            var noteNames = new List<string>();
            foreach (NoteDefinition note in organizer.Notes)
            {
                if (note.Id == Guid.Empty || !noteIds.Add(note.Id))
                {
                    note.Id = Guid.NewGuid();
                    noteIds.Add(note.Id);
                }
                string name = note.Name?.Trim() ?? string.Empty;
                note.Name = string.IsNullOrWhiteSpace(name) ||
                    noteNames.Contains(name, StringComparer.CurrentCultureIgnoreCase)
                    ? OrganizerNoteRules.CreateDefaultName(noteNames)
                    : name;
                noteNames.Add(note.Name);
                note.Theme = state.GlobalSettings.NoteTheme;
                note.FontSize = double.IsFinite(note.FontSize)
                    ? Math.Clamp(note.FontSize, OrganizerNoteRules.MinimumFontSize, OrganizerNoteRules.MaximumFontSize)
                    : 14;
                if (note.Placement is { } placement)
                {
                    if (!double.IsFinite(placement.XDip) || !double.IsFinite(placement.YDip) ||
                        !double.IsFinite(placement.WidthDip) || !double.IsFinite(placement.HeightDip))
                    {
                        note.Placement = null;
                    }
                    else
                    {
                        placement.WidthDip = Math.Clamp(placement.WidthDip, 280, 1600);
                        placement.HeightDip = Math.Clamp(placement.HeightDip, 220, 1200);
                    }
                }
            }
            organizer.ItemOrder = (organizer.ItemOrder ?? [])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.StartsWith("note:", StringComparison.OrdinalIgnoreCase) ||
                    OrganizerInteractionMath.TryParseOrganizerItemKey(name, out _)
                    ? name
                    : Path.GetFileName(name)!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var organizersById = state.Organizers.ToDictionary(organizer => organizer.Id);
        foreach (OrganizerDefinition organizer in state.Organizers)
        {
            if (organizer.ContainerStationId is not Guid stationId ||
                !organizersById.TryGetValue(stationId, out OrganizerDefinition? station) ||
                !OrganizerInteractionMath.CanContainOrganizer(
                    organizer.PlacementMode,
                    station.PlacementMode,
                    organizer.Id,
                    station.Id))
            {
                organizer.ContainerStationId = null;
            }
        }

        foreach (OrganizerDefinition organizer in state.Organizers)
        {
            HashSet<string> containedKeys = organizer.PlacementMode == OrganizerPlacementMode.Station
                ? state.Organizers
                    .Where(candidate => candidate.ContainerStationId == organizer.Id)
                    .Select(candidate => OrganizerInteractionMath.OrganizerItemKey(candidate.Id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            organizer.ItemOrder = organizer.ItemOrder
                .Where(key => !OrganizerInteractionMath.TryParseOrganizerItemKey(key, out _) || containedKeys.Contains(key))
                .ToList();
            foreach (string key in containedKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
            {
                if (!organizer.ItemOrder.Contains(key, StringComparer.OrdinalIgnoreCase)) organizer.ItemOrder.Add(key);
            }
        }

        if (state.ConsolePlacement is not null)
        {
            state.ConsolePlacement.WidthDip = Math.Max(860, state.ConsolePlacement.WidthDip);
            state.ConsolePlacement.HeightDip = Math.Max(600, state.ConsolePlacement.HeightDip);
        }
        return state;
    }
}
