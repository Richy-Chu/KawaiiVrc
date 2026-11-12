using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>A world returned by /worlds or /worlds/{worldId}.</summary>
public sealed class VrcWorld
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("authorName")]
    public string AuthorName { get; set; } = string.Empty;

    [JsonPropertyName("authorId")]
    public string? AuthorId { get; set; }

    [JsonPropertyName("authorIconUrl")]
    public string? AuthorIconUrl { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    [JsonPropertyName("recommendedCapacity")]
    public int RecommendedCapacity { get; set; }

    [JsonPropertyName("occupants")]
    public int Occupants { get; set; }

    [JsonPropertyName("publicOccupants")]
    public int PublicOccupants { get; set; }

    [JsonPropertyName("privateOccupants")]
    public int PrivateOccupants { get; set; }

    [JsonPropertyName("favorites")]
    public int Favorites { get; set; }

    [JsonPropertyName("visits")]
    public int Visits { get; set; }

    [JsonPropertyName("featured")]
    public bool Featured { get; set; }

    [JsonPropertyName("releaseStatus")]
    public string? ReleaseStatus { get; set; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("thumbnailImageUrl")]
    public string? ThumbnailImageUrl { get; set; }

    [JsonPropertyName("previewYoutubeId")]
    public string? PreviewYoutubeId { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("supportedPlatforms")]
    public List<string> SupportedPlatforms { get; set; } = new();

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("publicationDate")]
    public string? PublicationDate { get; set; }

    [JsonPropertyName("labsPublicationDate")]
    public string? LabsPublicationDate { get; set; }

    [JsonPropertyName("slimInstances")]
    public List<VrcInstance> SlimInstances { get; set; } = new();
}
