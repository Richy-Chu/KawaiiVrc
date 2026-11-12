using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>
/// A VRChat user. The same shape is returned for the current user, friends,
/// and public profiles, so many fields are optional in practice.
/// </summary>
public sealed class VrcUser
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("bio")]
    public string? Bio { get; set; }

    [JsonPropertyName("currentAvatar")]
    public string? CurrentAvatar { get; set; }

    [JsonPropertyName("currentAvatarImageUrl")]
    public string? CurrentAvatarImageUrl { get; set; }

    [JsonPropertyName("currentAvatarThumbnailImageUrl")]
    public string? CurrentAvatarThumbnailImageUrl { get; set; }

    [JsonPropertyName("profilePicOverride")]
    public string? ProfilePicOverride { get; set; }

    [JsonPropertyName("profilePicOverrideThumbnail")]
    public string? ProfilePicOverrideThumbnail { get; set; }

    [JsonPropertyName("userIcon")]
    public string? UserIcon { get; set; }

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("bannerUrl")]
    public string? BannerUrl { get; set; }

    [JsonPropertyName("bannerColor")]
    public string? BannerColor { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("statusDescription")]
    public string? StatusDescription { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("last_platform")]
    public string? LastPlatform { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("last_activity")]
    public DateTimeOffset? LastActivity { get; set; }

    [JsonPropertyName("last_login")]
    public DateTimeOffset? LastLogin { get; set; }

    [JsonPropertyName("date_joined")]
    public string? DateJoined { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("ageVerified")]
    public bool AgeVerified { get; set; }

    [JsonPropertyName("ageVerificationStatus")]
    public string? AgeVerificationStatus { get; set; }

    [JsonPropertyName("twoFactorAuthEnabled")]
    public bool TwoFactorAuthEnabled { get; set; }

    [JsonPropertyName("emailVerified")]
    public bool EmailVerified { get; set; }

    [JsonPropertyName("accountStanding")]
    public string? AccountStanding { get; set; }

    [JsonPropertyName("pronouns")]
    public string? Pronouns { get; set; }

    [JsonPropertyName("developerType")]
    public string? DeveloperType { get; set; }

    [JsonPropertyName("allowAvatarCopying")]
    public bool AllowAvatarCopying { get; set; } = true;

    [JsonPropertyName("isFriend")]
    public bool IsFriend { get; set; }
}
