using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>One of the account's reusable VRChat invite messages.</summary>
public sealed class InviteMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("messageType")]
    public string MessageType { get; set; } = string.Empty;

    [JsonPropertyName("slot")]
    public int Slot { get; set; }

    [JsonPropertyName("canBeUpdated")]
    public bool CanBeUpdated { get; set; }

    [JsonPropertyName("remainingCooldownMinutes")]
    public int RemainingCooldownMinutes { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }
}
