namespace TuckPane.Models;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

public enum GlassTheme
{
    Light = 0,
    Gray = 1,
    SolidLight = 2,
    SolidDark = 3,
    FrostedLight = 4,
    FrostedDark = 5
}

public enum OrganizerLayoutMode
{
    Grid,
    Row,
    Column
}

public enum OrganizerPlacementMode
{
    Floating = 0,
    Positioned = 1,
    Station = 2
}

public enum OrganizerDockEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3
}

public enum AppLanguage
{
    ChineseSimplified = 0,
    English = 1,
    Japanese = 2
}

[Flags]
internal enum OrganizerVisualChange
{
    None = 0,
    Name = 1 << 0,
    Theme = 1 << 1,
    Layout = 1 << 2,
    CompactScale = 1 << 3,
    CanvasScale = 1 << 4,
    ItemScale = 1 << 5,
    NameScale = 1 << 6,
    PlacementMode = 1 << 7,
    PositionLock = 1 << 8,
    Docking = 1 << 9,
    All = Name | Theme | Layout | CompactScale | CanvasScale | ItemScale | NameScale | PlacementMode | PositionLock | Docking
}

public enum NoteTheme
{
    RainBlue,
    Graphite,
    SunYellow,
    InkBlack,
    TransparentGlass,
    CloudPaper,
    WheatPaper
}

public sealed class AppStateV2
{
    public int SchemaVersion { get; set; } = 5;
    public GlobalSettings GlobalSettings { get; set; } = new();
    public ConsolePlacement? ConsolePlacement { get; set; }
    public List<OrganizerDefinition> Organizers { get; set; } = [];
}

public sealed class GlobalSettings
{
    public GlassTheme Theme { get; set; } = GlassTheme.Light;
    public bool StartWithWindows { get; set; }
    public bool CollapseOnOutsideClick { get; set; } = true;
    public bool ShowConsoleOnLaunch { get; set; }
    public double OrganizerSurfaceOpacity { get; set; }
    public AppLanguage Language { get; set; } = AppLanguage.ChineseSimplified;
    public bool ExclusiveExpansion { get; set; } = true;
    public bool ExpandOnHover { get; set; }
    public bool CollapseOnPointerLeave { get; set; }
    public int ExpandOnHoverMs { get; set; } = 350;
    public int CollapseOnPointerLeaveMs { get; set; } = 400;
}

public sealed class ConsolePlacement
{
    public double XDip { get; set; }
    public double YDip { get; set; }
    public double WidthDip { get; set; } = 960;
    public double HeightDip { get; set; } = 680;
    public string MonitorDevice { get; set; } = string.Empty;
}

public sealed class OrganizerDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "收纳窗";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public GlassTheme? ThemeOverride { get; set; }
    public OrganizerPlacementMode PlacementMode { get; set; } = OrganizerPlacementMode.Floating;
    public bool PositionLocked { get; set; }
    public OrganizerDockEdge DockEdge { get; set; } = OrganizerDockEdge.Right;
    public OrganizerLayout Layout { get; set; } = new();
    public double CompactScale { get; set; } = OrganizerLimits.DefaultCompactScale;
    public double CanvasScale { get; set; } = 1;
    public double ItemScale { get; set; } = 1;
    public double NameScale { get; set; } = 1;
    public double? ManualCanvasBaseWidthDip { get; set; }
    public double? ManualCanvasBaseHeightDip { get; set; }
    public WidgetPosition? Position { get; set; }
    public string StorageRelativePath { get; set; } = string.Empty;
    public string? StorageAbsolutePath { get; set; }
    public List<string> ItemOrder { get; set; } = [];
    public List<NoteDefinition> Notes { get; set; } = [];
}

public sealed class NoteDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public NoteTheme Theme { get; set; } = NoteTheme.RainBlue;
    public double FontSize { get; set; } = 14;
    public bool ShowRuledLines { get; set; }
    public NoteWindowPlacement? Placement { get; set; }
}

public sealed class NoteWindowPlacement
{
    public string MonitorDevice { get; set; } = string.Empty;
    public double XDip { get; set; }
    public double YDip { get; set; }
    public double WidthDip { get; set; } = 360;
    public double HeightDip { get; set; } = 300;
}

public sealed class NoteDocument
{
    public int Version { get; set; } = 1;
    public string Html { get; set; } = string.Empty;
}

public sealed class OrganizerLayout
{
    public OrganizerLayoutMode Mode { get; set; } = OrganizerLayoutMode.Grid;
    public int Rows { get; set; } = 3;
    public int Columns { get; set; } = 3;

    [JsonIgnore]
    public int VisibleItemCount => Rows * Columns;
}

// Kept only as the on-disk migration input for 0.1.x installations.
public sealed class AppStateV1
{
    public int SchemaVersion { get; set; } = 1;
    public string WidgetName { get; set; } = "文件夹";
    public bool StartWithWindows { get; set; }
    public WidgetPosition? Position { get; set; }
    public List<string> ItemOrder { get; set; } = [];
}

public sealed class WidgetPosition
{
    public string MonitorDevice { get; set; } = string.Empty;
    public double XDip { get; set; }
    public double YDip { get; set; }
    public double SavedWorkAreaWidthDip { get; set; }
    public double SavedWorkAreaHeightDip { get; set; }
}

public enum WidgetItemKind
{
    Folder,
    Shortcut,
    InternetShortcut,
    File,
    PortableNote,
    Note
}

public sealed record WidgetItem : INotifyPropertyChanged
{
    private string _name;
    private string _fullPath;
    private string _relativeName;
    private WidgetItemKind _kind;
    private Guid? _noteId;

    public WidgetItem(string name, string fullPath, string relativeName, WidgetItemKind kind, Guid? noteId = null)
    {
        _name = name;
        _fullPath = fullPath;
        _relativeName = relativeName;
        _kind = kind;
        _noteId = noteId;
    }

    public string Name
    {
        get => _name;
        private set => SetField(ref _name, value);
    }

    public string FullPath
    {
        get => _fullPath;
        private set => SetField(ref _fullPath, value);
    }

    public string RelativeName
    {
        get => _relativeName;
        private set => SetField(ref _relativeName, value);
    }

    public WidgetItemKind Kind
    {
        get => _kind;
        private set => SetField(ref _kind, value);
    }

    public Guid? NoteId
    {
        get => _noteId;
        private set => SetField(ref _noteId, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool HasSameValue(WidgetItem other) =>
        Name.Equals(other.Name, StringComparison.Ordinal) &&
        FullPath.Equals(other.FullPath, StringComparison.Ordinal) &&
        RelativeName.Equals(other.RelativeName, StringComparison.Ordinal) &&
        Kind == other.Kind &&
        NoteId == other.NoteId;

    internal void ApplyValue(WidgetItem other)
    {
        Name = other.Name;
        FullPath = other.FullPath;
        RelativeName = other.RelativeName;
        Kind = other.Kind;
        NoteId = other.NoteId;
    }

    internal WidgetItem CopyValue() => new(Name, FullPath, RelativeName, Kind, NoteId);

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum TransferStatus
{
    Moved,
    Copied,
    ShortcutCreated,
    CopiedSourceRetained,
    Cancelled,
    Failed
}

public sealed record TransferOutcome(
    string SourcePath,
    string? DestinationPath,
    TransferStatus Status,
    string Message);

public sealed record TransferProgress(string ItemName, long BytesCopied, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesCopied / TotalBytes, 0, 1);
}
