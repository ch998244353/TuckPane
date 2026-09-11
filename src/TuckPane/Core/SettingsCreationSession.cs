using System.Text.Json;
using TuckPane.Models;

namespace TuckPane.Core;

internal sealed record SettingsCreationDraft(OrganizerDefinition Definition, string? StoragePath, bool NameWasEdited = false)
{
    public SettingsCreationDraft Snapshot() => this with
    {
        Definition = JsonSerializer.Deserialize<OrganizerDefinition>(JsonSerializer.Serialize(Definition))!
    };
}

internal sealed record SettingsCreationResult(OrganizerDefinition Created, bool SameSession);

// Owned by the settings window's UI thread. Drafts never enter AppState until submission.
internal sealed class SettingsCreationSession
{
    private readonly Dictionary<ThemeTarget, SettingsCreationDraft> _drafts = new();
    private int _generation;
    public ThemeTarget? SubmittingCategory { get; private set; }
    public bool IsSubmitting => SubmittingCategory is not null;
    public int Generation => _generation;

    public SettingsCreationDraft GetOrCreate(ThemeTarget category, Func<SettingsCreationDraft> factory)
    {
        if (!_drafts.TryGetValue(category, out SettingsCreationDraft? draft))
        {
            draft = factory();
            ValidateCategory(category, draft);
            _drafts.Add(category, draft.Snapshot());
        }
        return _drafts[category].Snapshot();
    }

    public void Save(ThemeTarget category, SettingsCreationDraft draft)
    {
        if (SubmittingCategory == category) return;
        ValidateCategory(category, draft);
        _drafts[category] = draft.Snapshot();
    }

    public void Close()
    {
        _generation++;
        _drafts.Clear();
    }

    public async Task<SettingsCreationResult?> SubmitAsync(ThemeTarget category,
        Func<OrganizerDefinition, string?, Task<OrganizerDefinition>> create)
    {
        if (IsSubmitting) return null;
        SettingsCreationDraft snapshot = _drafts[category].Snapshot();
        int generation = _generation;
        SubmittingCategory = category;
        try
        {
            OrganizerDefinition created = await create(snapshot.Definition, snapshot.StoragePath);
            bool sameSession = generation == _generation;
            if (sameSession) _drafts.Remove(category);
            return new SettingsCreationResult(created, sameSession);
        }
        finally { SubmittingCategory = null; }
    }

    private static void ValidateCategory(ThemeTarget category, SettingsCreationDraft draft)
    {
        if (category is not (ThemeTarget.Organizer or ThemeTarget.Station or ThemeTarget.Dock) ||
            OrganizerKinds.ThemeFor(draft.Definition.PlacementMode) != category)
            throw new ArgumentException("The creation draft must belong to its management category.");
    }
}
