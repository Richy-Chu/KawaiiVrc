using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>
/// Legacy VRChat notification returned by /auth/user/notifications.
/// </summary>
public sealed class VrcNotification
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("details")]
    public JsonElement Details { get; set; }

    [JsonPropertyName("seen")]
    public bool Seen { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("senderUserId")]
    public string? SenderUserId { get; set; }

    [JsonPropertyName("senderUsername")]
    public string? SenderUsername { get; set; }

    [JsonPropertyName("displayCategory")]
    public string? DisplayCategory { get; set; }
}
