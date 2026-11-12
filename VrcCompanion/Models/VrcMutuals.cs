using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>Mutual friend/group counts returned by /users/{userId}/mutuals.</summary>
public sealed class VrcMutuals
{
    [JsonPropertyName("friends")]
    public int Friends { get; set; }

    [JsonPropertyName("groups")]
    public int Groups { get; set; }
}
