using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>One favourite entry from <c>GET favorites?type=avatar</c>.</summary>
public sealed class AvatarFavorite
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("favoriteId")]
    public string FavoriteId { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}
