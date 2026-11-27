using TuckPane.Core;

namespace TuckPane.Services;

internal readonly record struct FileMenuSelection(uint CommandId, string? CanonicalVerb, bool IsFallback = false);

internal interface IFileContextMenuSession : IDisposable
{
    // Keep old non-GUI session doubles compatible; production overrides ShowAsync.
    FileMenuSelection? Show() => throw new NotSupportedException("This menu is asynchronous.");
    Task<FileMenuSelection?> ShowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Show());
    }
    void Invoke(FileMenuSelection selection) => throw new NotSupportedException("Unknown file command.");
    Task InvokeAsync(FileMenuSelection selection, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Invoke(selection);
        return Task.CompletedTask;
    }
}

// This is also the non-GUI test seam. Protection spans preparation, the native
// menu and asynchronous document operations; all exits release it exactly once.
internal static class FileContextMenuOperation
{
    internal static async Task RunAsync(
        Action enter,
        Func<Task<bool>> prepare,
        Func<IFileContextMenuSession> create,
        Func<string?, Task<bool>> handle,
        Func<Task> refresh,
        Action leave,
        CancellationToken cancellationToken = default,
        FileContextMenuGate? gate = null,
        Func<FileMenuSelection, Task<bool>>? handleSelection = null)
    {
        using IDisposable? lease = gate?.TryEnter();
        if (gate is not null && lease is null) return;
        try
        {
            enter();
            cancellationToken.ThrowIfCancellationRequested();
            if (!await prepare()) return;
            cancellationToken.ThrowIfCancellationRequested();
            using (IFileContextMenuSession session = create())
            {
                if (await session.ShowAsync(cancellationToken).WaitAsync(cancellationToken) is not { } selection) return;
                cancellationToken.ThrowIfCancellationRequested();
                bool handled = handleSelection is null
                    ? await handle(selection.CanonicalVerb) : await handleSelection(selection);
                if (!handled) await session.InvokeAsync(selection, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await refresh();
        }
        finally
        {
            leave();
        }
    }
}
