using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>An owned VRChat inventory item; emoji items can be attached to a boop.</summary>
public sealed class VrcInventoryItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; set; }

    [JsonPropertyName("metadata")]
    public VrcInventoryMetadata? Metadata { get; set; }

    public string FriendlyName =>
        !string.IsNullOrWhiteSpace(Name) ? Name!.Trim() : "Inventory emoji";

    public string DisplayImageUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ImageUrl))
            {
                return ImageUrl!.Trim();
            }
            if (!string.IsNullOrWhiteSpace(ThumbnailUrl))
            {
                return ThumbnailUrl!.Trim();
            }
            if (!string.IsNullOrWhiteSpace(Metadata?.ImageUrl))
            {
                return Metadata!.ImageUrl!.Trim();
            }
            return Metadata?.Assets?
                .FirstOrDefault(asset => !string.IsNullOrWhiteSpace(asset.Url))
                ?.Url?.Trim() ?? string.Empty;
        }
    }
}

public sealed class VrcInventoryMetadata
{
    [JsonPropertyName("fileId")]
    public string? FileId { get; set; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("assets")]
    public List<VrcInventoryAsset> Assets { get; set; } = new();
}

public sealed class VrcInventoryAsset
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
