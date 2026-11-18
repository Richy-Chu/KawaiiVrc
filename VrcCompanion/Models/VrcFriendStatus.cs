using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>Relationship flags returned by VRChat's friendStatus endpoint.</summary>
public sealed class VrcFriendStatus
{
    [JsonPropertyName("isFriend")]
    public bool IsFriend { get; set; }

    [JsonPropertyName("outgoingRequest")]
    public bool OutgoingRequest { get; set; }

    [JsonPropertyName("incomingRequest")]
    public bool IncomingRequest { get; set; }
}