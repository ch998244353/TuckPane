namespace TuckPane.Core;

// A queued completion belongs to one requested Dock layout, never a later resize.
internal sealed class DockLayoutCommit
{
    internal long Version { get; private set; }
    internal bool Pending { get; private set; }

    internal long Request()
    {
        Pending = true;
        return ++Version;
    }

    internal bool Complete(long version)
    {
        if (version != Version) return false;
        Pending = false;
        return true;
    }

    internal bool CanPresent(long version, bool dimensionsReady, HoverRect visibleBounds) =>
        !Pending && version == Version && dimensionsReady && !visibleBounds.IsEmpty;
}
