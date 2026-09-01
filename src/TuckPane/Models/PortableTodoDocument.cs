namespace TuckPane.Models;

using System.Text.Json.Serialization;

internal sealed class PortableTodoDocument
{
    [JsonRequired]
    [JsonPropertyName("format")]
    public string Format { get; set; } = "TuckPane.Todo";

    [JsonRequired]
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonRequired]
    [JsonPropertyName("theme")]
    public NoteTheme Theme { get; set; } = NoteTheme.RainBlue;

    [JsonRequired]
    [JsonPropertyName("fontSize")]
    public double FontSize { get; set; } = 14;

    [JsonRequired]
    [JsonPropertyName("placement")]
    public PortableNotePlacement? Placement { get; set; }

    [JsonRequired]
    [JsonPropertyName("tasks")]
    public List<PortableTodoTask> Tasks { get; set; } = [];
}

internal sealed class PortableTodoTask
{
    [JsonRequired]
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonRequired]
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonRequired]
    [JsonPropertyName("completedAtUtc")]
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
