namespace TuckPane.Core;

// Shared by normal flush and directory rename: read and capture only after the previous write.
internal sealed class NoteSaveSequence(Func<bool, Task> prepare, Func<Func<Task>> captureWrite)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task SaveAsync(bool readEditor, bool holdForDirectoryRename = false)
    {
        await _gate.WaitAsync();
        bool keepHeld = false;
        try
        {
            await prepare(readEditor);
            Func<Task> write = captureWrite();
            await write();
            keepHeld = holdForDirectoryRename;
        }
        finally
        {
            if (!keepHeld) _gate.Release();
        }
    }

    internal void EndDirectoryRename() => _gate.Release();
}
