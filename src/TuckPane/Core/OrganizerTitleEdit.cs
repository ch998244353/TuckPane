namespace TuckPane.Core;

// Take the draft once, before hiding the editor can raise a second LostFocus event.
internal sealed class OrganizerTitleEdit
{
    internal bool IsEditing { get; private set; }
    internal bool IsSaving { get; private set; }
    internal bool IsBusy => IsEditing || IsSaving;
    internal bool Begin()
    {
        if (IsBusy) return false;
        IsEditing = true;
        return true;
    }
    internal bool Finish(bool commit)
    {
        if (!IsEditing) return false;
        IsEditing = false;
        IsSaving = commit;
        return commit;
    }
    internal void Saved() => IsSaving = false;
}
