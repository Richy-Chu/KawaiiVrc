using System.Collections.Generic;
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

    [JsonPropertyName("world")]
    public VrcWorld? World { get; set; }

    [JsonPropertyName("users")]
    public List<VrcUser> Users { get; set; } = new();

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("platforms")]
    public Dictionary<string, int>? Platforms { get; set; }

    [JsonPropertyName("ageGate")]
    public bool AgeGate { get; set; }

    [JsonPropertyName("queueEnabled")]
    public bool QueueEnabled { get; set; }

    [JsonPropertyName("queueSize")]
    public int QueueSize { get; set; }

    [JsonPropertyName("roleRestricted")]
    public bool RoleRestricted { get; set; }

    [JsonPropertyName("closedAt")]
    public System.DateTimeOffset? ClosedAt { get; set; }

    [JsonPropertyName("minimumAvatarPerformance")]
    public string? MinimumAvatarPerformance { get; set; }

    public int Occupants => UserCountAlt > 0 ? UserCountAlt : UserCount;
}
