using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane.Core;

internal readonly record struct UnifiedExpandedValues(bool Enabled, double Width, double Height, double Cell, double Content);
internal readonly record struct UnifiedExpandedTarget(OrganizerDefinition Organizer, double PanelWidth, double PanelHeight);
internal readonly record struct UnifiedExpandedLimits(double Width, double Height, double Cell, double Content);
internal sealed record UnifiedExpandedResult(UnifiedExpandedValues Values, UnifiedExpandedLimits Limits, bool Limited, Guid[] Changed);

// Pure DIP geometry. The half-DIP allowance is the inverse of CalculateGridCellExtent.
internal static class UnifiedExpandedSize
{
    internal static bool Participates(OrganizerDefinition item, OrganizerExpandedContentMode mode) =>
        OrganizerKinds.IsRegular(item.PlacementMode) && item.ExpandedContentMode == mode;

    internal static UnifiedExpandedValues Get(GlobalSettings settings, OrganizerExpandedContentMode mode) =>
        mode == OrganizerExpandedContentMode.CompactList
            ? new(settings.UseUniformCompactExpandedSize, settings.UniformCompactExpandedWidthDip,
                settings.UniformCompactExpandedHeightDip, 128, settings.UniformCompactExpandedContentScale)
            : new(settings.UseUniformIconExpandedSize, 280, 360,
                settings.UniformIconExpandedCellDip, settings.UniformIconExpandedContentScale);

    internal static void Set(GlobalSettings settings, OrganizerExpandedContentMode mode, UnifiedExpandedValues value)
    {
        value = Normalize(value);
        if (mode == OrganizerExpandedContentMode.CompactList)
        {
            settings.UseUniformCompactExpandedSize = value.Enabled;
            settings.UniformCompactExpandedWidthDip = value.Width;
            settings.UniformCompactExpandedHeightDip = value.Height;
            settings.UniformCompactExpandedContentScale = value.Content;
        }
        else
        {
            settings.UseUniformIconExpandedSize = value.Enabled;
            settings.UniformIconExpandedCellDip = value.Cell;
            settings.UniformIconExpandedContentScale = value.Content;
        }
    }

    internal static UnifiedExpandedValues Normalize(UnifiedExpandedValues value) => value with
    {
        Width = Finite(value.Width, 280, OrganizerLimits.MinimumCompactListCanvasWidthDip, 10000),
        Height = Finite(value.Height, 360, OrganizerLimits.MinimumCompactListCanvasHeightDip, 10000),
        Cell = Finite(value.Cell, 128, 1, 10000),
        Content = Finite(value.Content, 1, .5, 1.65)
    };

    private static double Finite(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    internal static (double Width, double Height) IconPanel(OrganizerLayout layout, double cell) =>
        ((cell + .5) * layout.Columns + DisplayPlacementService.ItemGapDip * (layout.Columns - 1) +
            2 * DisplayPlacementService.ExpandedSideInsetDip,
         (cell + .5) * layout.Rows + DisplayPlacementService.ItemGapDip * (layout.Rows - 1) +
            DisplayPlacementService.ExpandedTopInsetDip + DisplayPlacementService.ExpandedBottomInsetDip);

    internal static double CellFromPanel(OrganizerLayout layout, double width, double height, double scale = 1) =>
        Math.Max(1, Math.Min(
            (width - 2 * DisplayPlacementService.ExpandedSideInsetDip * scale - DisplayPlacementService.ItemGapDip * scale * (layout.Columns - 1)) / layout.Columns,
            (height - (DisplayPlacementService.ExpandedTopInsetDip + DisplayPlacementService.ExpandedBottomInsetDip) * scale - DisplayPlacementService.ItemGapDip * scale * (layout.Rows - 1)) / layout.Rows) - .5 * scale);

    internal static UnifiedExpandedValues FromOrganizer(GlobalSettings settings, OrganizerDefinition item)
    {
        var value = Get(settings, item.ExpandedContentMode);
        return item.ExpandedContentMode == OrganizerExpandedContentMode.CompactList
            ? value with { Width = item.CompactListCanvasWidthDip, Height = item.CompactListCanvasHeightDip,
                Content = item.CompactListItemScale * item.CompactListContentScale }
            : value with { Cell = CellFromPanel(item.Layout,
                    (item.ManualCanvasBaseWidthDip ?? IconPanel(item.Layout, value.Cell).Width) * item.CanvasScale,
                    (item.ManualCanvasBaseHeightDip ?? IconPanel(item.Layout, value.Cell).Height) * item.CanvasScale,
                    item.IconContentScale), Content = item.ItemScale * item.IconContentScale };
    }

    internal static UnifiedExpandedResult Apply(GlobalSettings settings, OrganizerExpandedContentMode mode,
        IEnumerable<UnifiedExpandedTarget> allTargets)
    {
        var requested = Normalize(Get(settings, mode));
        UnifiedExpandedTarget[] targets = allTargets.Where(t => Participates(t.Organizer, mode)).ToArray();
        double maxWidth = targets.Length == 0 ? 10000 : targets.Min(t => t.PanelWidth);
        double maxHeight = targets.Length == 0 ? 10000 : targets.Min(t => t.PanelHeight);
        double maxCell = targets.Length == 0 ? 10000 : targets.Min(t => CellFromPanel(t.Organizer.Layout, t.PanelWidth, t.PanelHeight));
        var value = requested;
        double maxContent = 1.65;
        if (mode == OrganizerExpandedContentMode.CompactList)
        {
            value = value with { Width = Math.Min(value.Width, maxWidth), Height = Math.Min(value.Height, maxHeight) };
            // At least one full compact row and its vertical insets must fit.
            maxContent = Math.Clamp((value.Height - DisplayPlacementService.ExpandedTopInsetDip - DisplayPlacementService.ExpandedBottomInsetDip) / 36, .5, 1.65);
        }
        else
        {
            value = value with { Cell = Math.Min(maxCell, Math.Max(value.Cell, DisplayPlacementService.CalculateRequiredCellDip(.5))) };
            double low = .5, high = 1.65;
            for (int i = 0; i < 24; i++)
            {
                double mid = (low + high) / 2;
                if (DisplayPlacementService.CalculateRequiredCellDip(mid) <= value.Cell) low = mid;
                else high = mid;
            }
            maxContent = DisplayPlacementService.CalculateRequiredCellDip(1.65) <= value.Cell
                ? 1.65 : Math.Round(low, 6, MidpointRounding.ToZero);
        }
        value = value with { Content = Math.Min(value.Content, maxContent) };
        var limits = new UnifiedExpandedLimits(maxWidth, maxHeight, maxCell, maxContent);
        if (!requested.Enabled) return new(requested, limits, false, []);
        Set(settings, mode, value);
        List<Guid> changed = [];
        foreach (var target in targets)
        {
            var before = UnifiedExpandedSnapshot.Geometry.Capture(target.Organizer);
            Write(target.Organizer, value);
            if (before != UnifiedExpandedSnapshot.Geometry.Capture(target.Organizer)) changed.Add(target.Organizer.Id);
        }
        return new(value, limits, value != requested, changed.ToArray());
    }

    internal static void Write(OrganizerDefinition item, UnifiedExpandedValues value)
    {
        if (item.ExpandedContentMode == OrganizerExpandedContentMode.CompactList)
        {
            item.CompactListCanvasWidthDip = value.Width;
            item.CompactListCanvasHeightDip = value.Height;
            item.CompactListContentScale = 1;
            item.CompactListItemScale = value.Content;
        }
        else
        {
            var panel = IconPanel(item.Layout, value.Cell);
            item.ManualCanvasBaseWidthDip = panel.Width;
            item.ManualCanvasBaseHeightDip = panel.Height;
            item.CanvasScale = item.IconContentScale = 1;
            item.ItemScale = value.Content;
        }
    }
}

// Capture only fields owned by this feature; unrelated edits and organizer membership survive rollback.
internal sealed record UnifiedExpandedSnapshot(UnifiedExpandedValues Compact, UnifiedExpandedValues Icon,
    Dictionary<Guid, UnifiedExpandedSnapshot.Geometry> Items)
{
    internal readonly record struct Geometry(double Width, double Height, double CompactContent, double CompactItem,
        double? ManualWidth, double? ManualHeight, double Canvas, double IconContent, double Item,
        OrganizerExpandedContentMode Mode, int Rows, int Columns, OrganizerLayoutMode LayoutMode)
    {
        internal static Geometry Capture(OrganizerDefinition item) => new(item.CompactListCanvasWidthDip,
            item.CompactListCanvasHeightDip, item.CompactListContentScale, item.CompactListItemScale,
            item.ManualCanvasBaseWidthDip, item.ManualCanvasBaseHeightDip, item.CanvasScale, item.IconContentScale, item.ItemScale,
            item.ExpandedContentMode, item.Layout.Rows, item.Layout.Columns, item.Layout.Mode);
        internal void Restore(OrganizerDefinition item, int modes = 3)
        {
            if ((modes & (1 << (int)Mode | 1 << (int)item.ExpandedContentMode)) != 0) item.ExpandedContentMode = Mode;
            if ((modes & 2) != 0)
            {
                item.CompactListCanvasWidthDip = Width; item.CompactListCanvasHeightDip = Height;
                item.CompactListContentScale = CompactContent; item.CompactListItemScale = CompactItem;
            }
            if ((modes & 1) != 0)
            {
                item.ManualCanvasBaseWidthDip = ManualWidth; item.ManualCanvasBaseHeightDip = ManualHeight;
                item.CanvasScale = Canvas; item.IconContentScale = IconContent; item.ItemScale = Item;
                item.Layout.Rows = Rows; item.Layout.Columns = Columns; item.Layout.Mode = LayoutMode;
            }
        }
    }

    internal static UnifiedExpandedSnapshot Capture(AppStateV2 state) => new(
        UnifiedExpandedSize.Get(state.GlobalSettings, OrganizerExpandedContentMode.CompactList),
        UnifiedExpandedSize.Get(state.GlobalSettings, OrganizerExpandedContentMode.Icon),
        state.Organizers.Where(o => OrganizerKinds.IsRegular(o.PlacementMode)).ToDictionary(o => o.Id, Geometry.Capture));

    internal void Restore(AppStateV2 state, int modes = 3)
    {
        if ((modes & 2) != 0) UnifiedExpandedSize.Set(state.GlobalSettings, OrganizerExpandedContentMode.CompactList, Compact);
        if ((modes & 1) != 0) UnifiedExpandedSize.Set(state.GlobalSettings, OrganizerExpandedContentMode.Icon, Icon);
        foreach (var item in state.Organizers)
            if (OrganizerKinds.IsRegular(item.PlacementMode) && Items.TryGetValue(item.Id, out var geometry)) geometry.Restore(item, modes);
    }
}
