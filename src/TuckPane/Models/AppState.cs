namespace TuckPane.Models;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

public enum OrganizerTextColor
{
    White = 0,
    Black = 1,
    Auto = 2
}

internal enum ThemeTarget
{
    Settings,
    Organizer
}

internal readonly record struct ThemeValues(
    uint ColorArgb,
    double Transparency,
    double BlurStrength = GlobalSettings.DefaultThemeBlurStrength,
    bool SolidColorMode = false,
    double SolidOpacity = 1);

public enum PerformanceProfile
{
    PowerSaver = 0,
    Balanced = 1,
    HighPerformance = 2
}

internal readonly record struct PerformanceTuning(
    int PointerPollMilliseconds,
    int DesktopRepairMilliseconds,
    bool CustomAnimationsEnabled);

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

public enum OrganizerExpandedContentMode
{
    Icon = 0,
    CompactList = 1
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
    Layout = 1 << 1,
    CompactScale = 1 << 2,
    CanvasScale = 1 << 3,
    ItemScale = 1 << 4,
    NameScale = 1 << 5,
    PlacementMode = 1 << 6,
    Docking = 1 << 7,
    ExpandedContentMode = 1 << 8,
    CompactListItemScale = 1 << 9,
    All = Name | Layout | CompactScale | CanvasScale | ItemScale | NameScale | PlacementMode | Docking | ExpandedContentMode | CompactListItemScale
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
    public int SchemaVersion { get; set; } = 15;
    public GlobalSettings GlobalSettings { get; set; } = new();
    public ConsolePlacement? ConsolePlacement { get; set; }
    public List<OrganizerDefinition> Organizers { get; set; } = [];
}

public sealed class GlobalSettings
{
    public const uint DefaultThemeColorArgb = 0xFFE2E5E9;
    public const double DefaultThemeTransparency = .35;
    // Glass opacity is persisted as a normalized 0..1 value and capped just
    // below fully opaque so the Glass pipeline remains distinguishable.
    public const double MaximumThemeTransparency = .99;
    // Glass blur intentionally has a non-zero floor so selecting Glass always
    // produces a perceptible material treatment (5% is the minimum setting).
    public const double MinimumThemeBlurStrength = .05;
    public const double DefaultThemeBlurStrength = 1;
    public const double MaximumThemeBlurStrength = 2;
    public const OrganizerTextColor DefaultOrganizerTextColor = OrganizerTextColor.Auto;
    public const int MinimumHoverDelayMs = 100;
    public const int MaximumHoverDelayMs = 2000;
    public const int HoverDelayStepMs = 50;
    public const int DefaultStationActivationDistanceDip = 16;
    public const int MinimumStationActivationDistanceDip = 4;
    public const int MaximumStationActivationDistanceDip = 48;
    public const int StationActivationDistanceStepDip = 4;
    public const int DefaultStationHoverExpandDelayMs = 120;
    public const int MinimumStationHoverExpandDelayMs = 0;
    public const int MaximumStationHoverExpandDelayMs = 500;
    public const int StationHoverExpandDelayStepMs = 20;
    public const double MinimumCompactNameScale = .6;
    public const double MaximumCompactNameScale = 1;

    public uint ThemeColorArgb { get; set; } = DefaultThemeColorArgb;
    public double ThemeTransparency { get; set; } = DefaultThemeTransparency;
    public double SolidThemeOpacity { get; set; } = 1;
    public double ThemeBlurStrength { get; set; } = DefaultThemeBlurStrength;
    public bool SolidColorMode { get; set; }
    public bool EdgeGlowEnabled { get; set; } = true;
    public uint SettingsThemeColorArgb { get; set; } = DefaultThemeColorArgb;
    public double SettingsThemeTransparency { get; set; } = DefaultThemeTransparency;
    public double SettingsSolidThemeOpacity { get; set; } = 1;
    public double SettingsThemeBlurStrength { get; set; } = DefaultThemeBlurStrength;
    public bool SettingsSolidColorMode { get; set; }
    public OrganizerTextColor OrganizerTextColor { get; set; } = DefaultOrganizerTextColor;
    public NoteTheme NoteTheme { get; set; } = NoteTheme.RainBlue;
    public bool StartWithWindows { get; set; }
    public AppLanguage Language { get; set; } = AppLanguage.ChineseSimplified;
    public string? DefaultStorageDirectory { get; set; }
    public PerformanceProfile PerformanceProfile { get; set; } = global::TuckPane.Models.PerformanceProfile.Balanced;
    public bool ExclusiveExpansion { get; set; } = true;
    public bool CollapseOnOutsideClick { get; set; }
    public bool NoteAlwaysOnTop { get; set; }
    public bool ExpandOnHover { get; set; }
    public bool CollapseOnPointerLeave { get; set; }
    public bool WindowAlignmentEnabled { get; set; }
    public bool RememberExpandedOrganizerPosition { get; set; }
    public bool MoveOrganizerFilesToDesktopOnDelete { get; set; } = true;
    public bool UseUniformFloatingCompactScale { get; set; }
    public double UniformFloatingCompactScale { get; set; } = OrganizerLimits.DefaultCompactScale;
    public bool UseUniformPositionedCompactScale { get; set; }
    public double UniformPositionedCompactScale { get; set; } = OrganizerLimits.DefaultCompactScale;
    public bool UseUniformFloatingCompactNameScale { get; set; }
    public double UniformFloatingCompactNameScale { get; set; } = MaximumCompactNameScale;
    public bool UseUniformPositionedCompactNameScale { get; set; }
    public double UniformPositionedCompactNameScale { get; set; } = MaximumCompactNameScale;
    public double ExpandedNameScale { get; set; } = MaximumCompactNameScale;
    public int HoverExpandDelayMs { get; set; } = 350;
    public int PointerLeaveCollapseDelayMs { get; set; } = 400;
    public int StationPointerLeaveCollapseDelayMs { get; set; } = 400;
    public int StationActivationDistanceDip { get; set; } = DefaultStationActivationDistanceDip;
    public int StationHoverExpandDelayMs { get; set; } = DefaultStationHoverExpandDelayMs;

    internal PerformanceTuning PerformanceTuning => PerformanceProfile switch
    {
        global::TuckPane.Models.PerformanceProfile.PowerSaver => new(100, 8000, false),
        global::TuckPane.Models.PerformanceProfile.HighPerformance => new(25, 2000, true),
        _ => new(50, 4000, true)
    };

    internal bool ShouldUseCustomAnimations(bool systemAnimationsEnabled) =>
        systemAnimationsEnabled && PerformanceTuning.CustomAnimationsEnabled;

    internal bool UsesUniformCompactScale(OrganizerPlacementMode mode) => mode switch
    {
        OrganizerPlacementMode.Floating => UseUniformFloatingCompactScale,
        OrganizerPlacementMode.Positioned => UseUniformPositionedCompactScale,
        _ => false
    };

    internal double ResolveCompactScale(OrganizerPlacementMode mode, double requestedScale)
    {
        double scale = mode switch
        {
            OrganizerPlacementMode.Floating when UseUniformFloatingCompactScale => UniformFloatingCompactScale,
            OrganizerPlacementMode.Positioned when UseUniformPositionedCompactScale => UniformPositionedCompactScale,
            _ => requestedScale
        };
        return NormalizeCompactScale(mode, scale);
    }

    internal static double NormalizeCompactScale(OrganizerPlacementMode mode, double scale) => Math.Clamp(
        scale,
        OrganizerLimits.MinimumCompactScale,
        mode == OrganizerPlacementMode.Positioned
            ? OrganizerLimits.MaximumPositionedCompactScale
            : OrganizerLimits.MaximumCompactScale);

    internal double ResolveCompactNameScale(OrganizerPlacementMode mode) =>
        mode is OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned
            ? NormalizeCompactNameScale(UniformFloatingCompactNameScale)
            : MaximumCompactNameScale;

    internal double ResolveExpandedNameScale(OrganizerPlacementMode mode) =>
        mode is OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned
            ? NormalizeCompactNameScale(ExpandedNameScale)
            : MaximumCompactNameScale;

    internal static double NormalizeCompactNameScale(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(scale, MinimumCompactNameScale, MaximumCompactNameScale) : MaximumCompactNameScale;

    public static int NormalizeHoverDelayMs(int value)
    {
        int clamped = Math.Clamp(value, MinimumHoverDelayMs, MaximumHoverDelayMs);
        return (int)Math.Round(clamped / (double)HoverDelayStepMs, MidpointRounding.AwayFromZero) * HoverDelayStepMs;
    }

    public static int NormalizeStationActivationDistanceDip(int value)
    {
        int clamped = Math.Clamp(value, MinimumStationActivationDistanceDip, MaximumStationActivationDistanceDip);
        return (int)Math.Round(clamped / (double)StationActivationDistanceStepDip, MidpointRounding.AwayFromZero) *
            StationActivationDistanceStepDip;
    }

    public static int NormalizeStationHoverExpandDelayMs(int value)
    {
        int clamped = Math.Clamp(value, MinimumStationHoverExpandDelayMs, MaximumStationHoverExpandDelayMs);
        return (int)Math.Round(clamped / (double)StationHoverExpandDelayStepMs, MidpointRounding.AwayFromZero) *
            StationHoverExpandDelayStepMs;
    }

    public static double NormalizeThemeTransparency(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, .99) : DefaultThemeTransparency;

    public static double NormalizeSolidThemeOpacity(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;

    public static double NormalizeThemeBlurStrength(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinimumThemeBlurStrength, MaximumThemeBlurStrength)
            : DefaultThemeBlurStrength;

    internal ThemeValues GetTheme(ThemeTarget target)
    {
        bool solid = target == ThemeTarget.Settings ? SettingsSolidColorMode : SolidColorMode;
        return target == ThemeTarget.Settings
            ? new(SettingsThemeColorArgb, SettingsThemeTransparency, SettingsThemeBlurStrength, solid, SettingsSolidThemeOpacity)
            : new(ThemeColorArgb, ThemeTransparency, ThemeBlurStrength, solid, SolidThemeOpacity);
    }

    internal void SetTheme(ThemeTarget target, ThemeValues theme)
    {
        if (target == ThemeTarget.Settings)
        {
            SettingsThemeColorArgb = theme.ColorArgb;
            SettingsThemeTransparency = theme.Transparency;
            SettingsSolidThemeOpacity = NormalizeSolidThemeOpacity(theme.SolidOpacity);
            SettingsThemeBlurStrength = theme.BlurStrength;
            SettingsSolidColorMode = theme.SolidColorMode;
            return;
        }

        ThemeColorArgb = theme.ColorArgb;
        ThemeTransparency = theme.Transparency;
        SolidThemeOpacity = NormalizeSolidThemeOpacity(theme.SolidOpacity);
        ThemeBlurStrength = theme.BlurStrength;
        SolidColorMode = theme.SolidColorMode;
    }

    internal static ThemeValues NormalizeTheme(ThemeValues theme) => new(
        theme.ColorArgb | 0xFF000000,
        NormalizeThemeTransparency(theme.Transparency),
        NormalizeThemeBlurStrength(theme.BlurStrength),
        theme.SolidColorMode,
        NormalizeSolidThemeOpacity(theme.SolidOpacity));

    internal static OrganizerTextColor NormalizeOrganizerTextColor(OrganizerTextColor color) =>
        Enum.IsDefined(color) ? color : DefaultOrganizerTextColor;
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
    public OrganizerPlacementMode PlacementMode { get; set; } = OrganizerPlacementMode.Floating;
    public OrganizerDockEdge DockEdge { get; set; } = OrganizerDockEdge.Right;
    public OrganizerLayout Layout { get; set; } = new();
    public double CompactScale { get; set; } = OrganizerLimits.DefaultCompactScale;
    public double CanvasScale { get; set; } = 1;
    public double ItemScale { get; set; } = 1;
    public double NameScale { get; set; } = 1;
    public double CompactListItemScale { get; set; } = 1;
    public OrganizerExpandedContentMode ExpandedContentMode { get; set; } = OrganizerExpandedContentMode.Icon;
    public double CompactListCanvasWidthDip { get; set; } = OrganizerLimits.DefaultCompactListCanvasWidthDip;
    public double CompactListCanvasHeightDip { get; set; } = OrganizerLimits.DefaultCompactListCanvasHeightDip;
    public double? ManualCanvasBaseWidthDip { get; set; }
    public double? ManualCanvasBaseHeightDip { get; set; }
    public WidgetPosition? Position { get; set; }
    public WidgetPosition? ExpandedPosition { get; set; }
    [JsonPropertyName("ContainerStationId")]
    public Guid? ContainerOrganizerId { get; set; }
    public string StorageRelativePath { get; set; } = string.Empty;
    public string? StorageAbsolutePath { get; set; }
    public bool StorageOwnedByApp { get; set; }
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
    PortableTodo,
    Note,
    Organizer
}

public sealed record WidgetItem : INotifyPropertyChanged
{
    private string _name;
    private string _fullPath;
    private string _relativeName;
    private WidgetItemKind _kind;
    private Guid? _noteId;
    private Guid? _organizerId;

    public WidgetItem(
        string name,
        string fullPath,
        string relativeName,
        WidgetItemKind kind,
        Guid? noteId = null,
        Guid? organizerId = null)
    {
        _name = name;
        _fullPath = fullPath;
        _relativeName = relativeName;
        _kind = kind;
        _noteId = noteId;
        _organizerId = organizerId;
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

    public Guid? OrganizerId
    {
        get => _organizerId;
        private set => SetField(ref _organizerId, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool HasSameValue(WidgetItem other) =>
        Name.Equals(other.Name, StringComparison.Ordinal) &&
        FullPath.Equals(other.FullPath, StringComparison.Ordinal) &&
        RelativeName.Equals(other.RelativeName, StringComparison.Ordinal) &&
        Kind == other.Kind &&
        NoteId == other.NoteId &&
        OrganizerId == other.OrganizerId;

    internal void ApplyValue(WidgetItem other)
    {
        Name = other.Name;
        FullPath = other.FullPath;
        RelativeName = other.RelativeName;
        Kind = other.Kind;
        NoteId = other.NoteId;
        OrganizerId = other.OrganizerId;
    }

    internal WidgetItem CopyValue() => new(Name, FullPath, RelativeName, Kind, NoteId, OrganizerId);

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
    Retained,
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
