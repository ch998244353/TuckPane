using TuckPane.Models;

namespace TuckPane.Core;

// A draft belongs to the window, never to the portable task collection.
internal sealed class TodoComposer
{
    internal bool IsEditing { get; private set; }
    internal string Text { get; set; } = string.Empty;

    internal void Begin() => IsEditing = true;

    internal void Cancel()
    {
        Text = string.Empty;
        IsEditing = false;
    }

    internal void Blur()
    {
        if (string.IsNullOrWhiteSpace(Text)) Cancel();
    }

    internal PortableTodoTask? Submit(PortableTodoDocument document)
    {
        if (!IsEditing || string.IsNullOrWhiteSpace(Text)) return null;
        PortableTodoTask task = TodoRules.Add(document, Text);
        Cancel();
        return task;
    }
}
