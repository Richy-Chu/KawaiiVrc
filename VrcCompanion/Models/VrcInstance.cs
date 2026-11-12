using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>
/// A VRChat instance. The same shape covers both full instances returned by
/// /instances and the slim summaries inside a world response.
/// </summary>
public sealed class VrcInstance
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = string.Empty;

    [JsonPropertyName("worldId")]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("n_users")]
    public int UserCount { get; set; }

    [JsonPropertyName("userCount")]
    public int UserCountAlt { get; set; }

    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    [JsonPropertyName("recommendedCapacity")]
    public int RecommendedCapacity { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; set; }

    [JsonPropertyName("shortName")]
    public string? ShortName { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("secureName")]
    public string? SecureName { get; set; }

    [JsonPropertyName("canRequestInvite")]
    public bool CanRequestInvite { get; set; }

    [JsonPropertyName("hasCapacityForYou")]
    public bool HasCapacityForYou { get; set; }

    [JsonPropertyName("full")]
    public bool Full { get; set; }

    public int Occupants => UserCountAlt > 0 ? UserCountAlt : UserCount;
}
