namespace TuckPane.Core;

// One sequence per slider settings group. UI-thread callers may keep previewing
// while an older disk write is pending; only the current edit can be rolled back.
internal sealed class SliderSaveSequence
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _revision;
    private long _completedRevision;

    internal long Revision => _revision;
    internal void Changed() => ++_revision;

    internal async Task<bool> SaveAsync<T>(Func<T> capture, Func<Action, Task> save,
        Action<T> commit, Action rollback, Action<Exception> report)
    {
        await _gate.WaitAsync();
        try
        {
            while (_completedRevision < _revision)
            {
                long saving = _revision;
                T snapshot = default!;
                try
                {
                    await save(() =>
                    {
                        // The shared StateStore lock may have delayed this write.
                        // Match the values/revision actually serialized inside that lock.
                        snapshot = capture();
                        saving = _revision;
                    });
                    commit(snapshot);
                    _completedRevision = saving;
                }
                catch (Exception ex)
                {
                    // A later value still needs saving, even when its debounce
                    // fired during this await. Never overwrite it with an old value.
                    if (saving != _revision) continue;
                    rollback();
                    _completedRevision = saving;
                    report(ex);
                    return false;
                }
            }
            return true;
        }
        finally { _gate.Release(); }
    }
}
