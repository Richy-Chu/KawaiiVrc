using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>
/// A named set of extra VRChat command-line arguments, similar to Steam's
/// per-game launch options. The selected profile applies to every launch.
/// </summary>
public sealed class LaunchProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}
