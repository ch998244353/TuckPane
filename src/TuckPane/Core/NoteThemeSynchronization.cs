using TuckPane.Models;

namespace TuckPane.Core;

internal sealed record NoteThemeTarget(string Name, Func<Task> Apply);
internal sealed record NoteThemeFailure(string Target, Exception Error);

// One application instance owns this queue. Registration order is the selection order;
// continuations retain the caller's UI context. File stores keep their existing locks.
internal sealed class NoteThemeSynchronization
{
    private readonly object _queueLock = new();
    private Task _tail = Task.CompletedTask;

    internal async Task<IReadOnlyList<NoteThemeFailure>> SetAsync(
        GlobalSettings settings, Func<IReadOnlyList<NoteDefinition>> notes, NoteTheme theme,
        Func<Task> saveSettings, Func<IReadOnlyList<NoteThemeTarget>> targets)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_queueLock)
        {
            previous = _tail;
            _tail = completion.Task;
        }
        try
        {
            await previous;
            NoteTheme oldTheme = settings.NoteTheme;
            var oldNotes = notes().Select(note => (Note: note, Theme: note.Theme)).ToArray();
            settings.NoteTheme = theme;
            foreach (var entry in oldNotes) entry.Note.Theme = theme;
            try { await saveSettings(); }
            catch
            {
                settings.NoteTheme = oldTheme;
                foreach (var entry in oldNotes) entry.Note.Theme = entry.Theme;
                throw;
            }

            var failures = new List<NoteThemeFailure>();
            foreach (NoteThemeTarget target in targets())
            {
                try { await target.Apply(); }
                catch (Exception ex) { failures.Add(new(target.Name, ex)); }
            }
            return failures;
        }
        finally { completion.SetResult(); }
    }
}
