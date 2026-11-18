using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>
/// One player-moderation row from the signed-in account's moderation list. The
/// source is this account and the target is the other user. VRChat's separate
/// against-me list is disabled, so the reverse direction is not available here.
/// </summary>
public sealed class VrcPlayerModeration
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("sourceUserId")]
    public string? SourceUserId { get; set; }

    [JsonPropertyName("sourceDisplayName")]
    public string? SourceDisplayName { get; set; }

    [JsonPropertyName("targetUserId")]
    public string? TargetUserId { get; set; }

    [JsonPropertyName("targetDisplayName")]
    public string? TargetDisplayName { get; set; }

    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; set; }
}
