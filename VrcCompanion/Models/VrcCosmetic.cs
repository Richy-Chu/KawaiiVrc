using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>
/// A VRChat profile decoration. Users store only the cosmetic id; the published
/// artwork (base/main/intro assets and nameplate gradient) lives here.
/// </summary>
public sealed class VrcCosmetic
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("itemType")]
    public string ItemType { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("metadata")]
    public VrcCosmeticMetadata? Metadata { get; set; }
}

public sealed class VrcCosmeticMetadata
{
    [JsonPropertyName("assets")]
    public List<VrcCosmeticAsset> Assets { get; set; } = new();

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("gradientStart")]
    public string? GradientStart { get; set; }

    [JsonPropertyName("gradientEnd")]
    public string? GradientEnd { get; set; }
}

public sealed class VrcCosmeticAsset
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("totalDurationMs")]
    public int TotalDurationMs { get; set; }
}
