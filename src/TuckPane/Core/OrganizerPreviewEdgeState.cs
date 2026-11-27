namespace TuckPane.Core;

// Pure presentation state shared by initial realization and recycled previews.
internal readonly record struct OrganizerPreviewEdgeState(
    bool Enabled, bool IsCompact, bool IsDock, bool IsIconPreview)
{
    internal bool IsVisible => Enabled && !IsCompact && !IsDock && IsIconPreview;
}
