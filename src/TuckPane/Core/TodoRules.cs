namespace TuckPane.Core;

using TuckPane.Models;

internal static class TodoRules
{
    internal static readonly TimeSpan CompletionDelay = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(260);

    internal static string NormalizeText(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static PortableTodoTask Add(PortableTodoDocument document, string? text)
    {
        string normalized = NormalizeText(text);
        if (normalized.Length == 0) throw new ArgumentException("Todo text is required.", nameof(text));
        var task = new PortableTodoTask { Text = normalized };
        document.Tasks.Add(task);
        return task;
    }

    internal static bool UpdateText(PortableTodoTask task, string? text)
    {
        string normalized = NormalizeText(text);
        if (normalized.Length == 0 || normalized == task.Text) return false;
        task.Text = normalized;
        return true;
    }

    internal static void SetDone(PortableTodoTask task, bool done, DateTimeOffset nowUtc)
    {
        task.Done = done;
        task.CompletedAtUtc = done ? nowUtc.ToUniversalTime() : null;
    }

    internal static bool Move(PortableTodoDocument document, Guid taskId, int targetIndex)
    {
        int sourceIndex = document.Tasks.FindIndex(task => task.Id == taskId);
        if (sourceIndex < 0 || document.Tasks.Count < 2) return false;
        targetIndex = Math.Clamp(targetIndex, 0, document.Tasks.Count - 1);
        if (sourceIndex == targetIndex) return false;
        PortableTodoTask task = document.Tasks[sourceIndex];
        document.Tasks.RemoveAt(sourceIndex);
        document.Tasks.Insert(targetIndex, task);
        return true;
    }

    internal static int RemoveExpired(PortableTodoDocument document, DateTimeOffset nowUtc)
    {
        DateTimeOffset now = nowUtc.ToUniversalTime();
        return document.Tasks.RemoveAll(task => task.Done &&
            task.CompletedAtUtc is DateTimeOffset completed &&
            now - completed >= CompletionDelay);
    }

    internal static double GetOpacity(PortableTodoTask task, DateTimeOffset nowUtc)
    {
        if (!task.Done || task.CompletedAtUtc is not DateTimeOffset completed) return 1;
        TimeSpan elapsed = nowUtc.ToUniversalTime() - completed;
        TimeSpan fadeStart = CompletionDelay - FadeDuration;
        if (elapsed <= fadeStart) return 1;
        if (elapsed >= CompletionDelay) return 0;
        return 1 - (elapsed - fadeStart).TotalMilliseconds / FadeDuration.TotalMilliseconds;
    }
}
