namespace TuckPane.Core;

// UI-thread session ownership: a delayed input callback cannot finish a later gesture.
internal sealed class InteractionSession
{
    internal long Version { get; private set; }
    internal bool IsCompleting { get; private set; }

    internal bool TryStart()
    {
        if (IsCompleting) return false;
        Version++;
        return true;
    }

    internal async Task CompleteAsync(long version, Func<Task> complete)
    {
        if (version != Version || IsCompleting) return;
        IsCompleting = true;
        try { await complete(); }
        finally { if (version == Version) IsCompleting = false; }
    }

    internal void Invalidate()
    {
        Version++;
        IsCompleting = false;
    }
}
