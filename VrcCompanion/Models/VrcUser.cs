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

    [JsonPropertyName("currentAvatarName")]
    public string? CurrentAvatarName { get; set; }

    [JsonPropertyName("currentAvatarImageUrl")]
    public string? CurrentAvatarImageUrl { get; set; }

    [JsonPropertyName("currentAvatarThumbnailImageUrl")]
    public string? CurrentAvatarThumbnailImageUrl { get; set; }

    // VRChat's user record knows who made the avatar they wear, even when its log
    // only printed the avatar's name. The name search needs it to refuse another
    // author's avatar that shares the name.
    [JsonPropertyName("currentAvatarAuthorName")]
    public string? CurrentAvatarAuthorName { get; set; }

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

    [JsonPropertyName("bannerType")]
    public string? BannerType { get; set; }

    [JsonPropertyName("bannerColor")]
    public string? BannerColor { get; set; }

    // Profile decorations are cosmetic ids. Their artwork is published by the
    // /cosmetics/index/{type} endpoints, not embedded in the user record.
    [JsonPropertyName("iconFrame")]
    public string? IconFrame { get; set; }

    [JsonPropertyName("profileEffect")]
    public string? ProfileEffect { get; set; }

    [JsonPropertyName("nameplateEffect")]
    public string? NameplateEffect { get; set; }

    [JsonPropertyName("themeId")]
    public string? ThemeId { get; set; }

    [JsonPropertyName("themeIconColor")]
    public string? ThemeIconColor { get; set; }

    [JsonPropertyName("themeButtonColor")]
    public string? ThemeButtonColor { get; set; }

    [JsonPropertyName("themeSubtextColor")]
    public string? ThemeSubtextColor { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("statusDescription")]
    public string? StatusDescription { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    // Only current-user responses populate these friend-id buckets. VRCX uses
    // them to resolve friends whose location is the stale "private" sentinel
    // and to repair friends that the paginated friend endpoint missed.
    [JsonPropertyName("friends")]
    public List<string> Friends { get; set; } = new();

    [JsonPropertyName("onlineFriends")]
    public List<string> OnlineFriends { get; set; } = new();

    [JsonPropertyName("activeFriends")]
    public List<string> ActiveFriends { get; set; } = new();

    [JsonPropertyName("offlineFriends")]
    public List<string> OfflineFriends { get; set; } = new();

    [JsonPropertyName("last_platform")]
    public string? LastPlatform { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("last_activity")]
    [JsonConverter(typeof(NullableDateTimeOffsetConverter))]
    public DateTimeOffset? LastActivity { get; set; }

    [JsonPropertyName("last_login")]
    [JsonConverter(typeof(NullableDateTimeOffsetConverter))]
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

    [JsonPropertyName("bioLinks")]
    public List<string> BioLinks { get; set; } = new();

    [JsonPropertyName("currentAvatarTags")]
    public List<string> CurrentAvatarTags { get; set; } = new();

    [JsonPropertyName("lastSeen")]
    public string? LastSeen { get; set; }

    [JsonPropertyName("developerType")]
    public string? DeveloperType { get; set; }

    [JsonPropertyName("allowAvatarCopying")]
    public bool AllowAvatarCopying { get; set; } = true;

    [JsonPropertyName("isFriend")]
    public bool IsFriend { get; set; }

    /// <summary>True when the signed-in account has blocked this user.</summary>
    [JsonPropertyName("isBlocked")]
    public bool IsBlocked { get; set; }

    // VRChat exposes this on the signed-in user. A missing field means the
    // account has not disabled boops, matching VRCX-0's `!== false` check.
    [JsonPropertyName("isBoopingEnabled")]
    public bool IsBoopingEnabled { get; set; } = true;
}
