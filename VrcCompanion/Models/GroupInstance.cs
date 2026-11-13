using System.Text.Json.Serialization;
using VrcCompanion.Services;

namespace VrcCompanion.Models;

/// <summary>An active instance hosted by a group (GET groups/{id}/instances).</summary>
public sealed class GroupInstance
{
    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("memberCount")]
    public int MemberCount { get; set; }

    [JsonPropertyName("world")]
    public GroupInstanceWorld? World { get; set; }

    [JsonIgnore]
    public string WorldName => string.IsNullOrWhiteSpace(World?.Name) ? "Unknown world" : World!.Name;

    [JsonIgnore]
    public string ThumbnailUrl => ImageUrlHelper.ToThumbnailUrl(World?.ThumbnailImageUrl, 256) ?? string.Empty;

    [JsonIgnore]
    public string UsersText => MemberCount == 1 ? "1 person here" : $"{MemberCount} people here";

    [JsonIgnore]
    public string DetailsText => $"{UsersText} · {AccessText}";

    /// <summary>Turns VRChat's location tags into a short access label.</summary>
    [JsonIgnore]
    public string AccessText
    {
        get
        {
            var location = Location ?? string.Empty;
            if (location.Contains("~group(", StringComparison.OrdinalIgnoreCase))
            {
                return "Group";
            }
            if (location.Contains("~friends(", StringComparison.OrdinalIgnoreCase))
            {
                return "Friends";
            }
            if (location.Contains("~hidden(", StringComparison.OrdinalIgnoreCase))
            {
                return "Invite";
            }
            if (location.Contains("~private(", StringComparison.OrdinalIgnoreCase))
            {
                return "Private";
            }
            return "Public";
        }
    }

    [JsonIgnore]
    public bool CanJoin => !string.IsNullOrWhiteSpace(Location) && LaunchService.IsLocation(Location);
}

public sealed class GroupInstanceWorld
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("thumbnailImageUrl")]
    public string? ThumbnailImageUrl { get; set; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }
}
