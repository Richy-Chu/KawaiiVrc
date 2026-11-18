using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>Public profile returned by /profile/{userId}.</summary>
public sealed class VrcProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("bio")]
    public string? Bio { get; set; }

    [JsonPropertyName("bioLinks")]
    public List<string> BioLinks { get; set; } = new();

    [JsonPropertyName("pronouns")]
    public string? Pronouns { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("statusDescription")]
    public string? StatusDescription { get; set; }

    [JsonPropertyName("bannerUrl")]
    public string? BannerUrl { get; set; }

    [JsonPropertyName("bannerColor")]
    public string? BannerColor { get; set; }

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

    [JsonPropertyName("currentAvatarImageUrl")]
    public string? CurrentAvatarImageUrl { get; set; }

    [JsonPropertyName("currentAvatar")]
    public string? CurrentAvatar { get; set; }

    [JsonPropertyName("currentAvatarThumbnailImageUrl")]
    public string? CurrentAvatarThumbnailImageUrl { get; set; }

    [JsonPropertyName("currentAvatarName")]
    public string? CurrentAvatarName { get; set; }

    [JsonPropertyName("currentAvatarAuthorName")]
    public string? CurrentAvatarAuthorName { get; set; }

    [JsonPropertyName("userIcon")]
    public string? UserIcon { get; set; }

    // Newer VRChat profiles expose the chosen profile icon separately from
    // userIcon. VRC+ accounts often have only this field populated.
    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("ageVerified")]
    public bool AgeVerified { get; set; }

    [JsonPropertyName("ageVerificationStatus")]
    public string? AgeVerificationStatus { get; set; }

    [JsonPropertyName("hasVrcPlus")]
    public bool HasVrcPlus { get; set; }

    [JsonPropertyName("trustTags")]
    public List<string> TrustTags { get; set; } = new();

    [JsonPropertyName("badges")]
    public List<VrcBadge> Badges { get; set; } = new();

    [JsonPropertyName("groups")]
    public VrcProfileGroups? Groups { get; set; }

    [JsonPropertyName("representedGroup")]
    public VrcProfileGroup? RepresentedGroup { get; set; }
}

public sealed class VrcProfileGroups
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("list")]
    public List<VrcProfileGroup> List { get; set; } = new();
}

public sealed class VrcProfileGroup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    /// <summary>True when the signed-in account is also in this group.</summary>
    [JsonIgnore]
    public bool IsMine { get; set; }

    /// <summary>True when this is a group both the profile and the account are in.</summary>
    [JsonIgnore]
    public bool IsMutual { get; set; }

    [JsonIgnore]
    public string MembershipLabel => IsMutual ? "Mutual" : IsMine ? "Joined" : string.Empty;

    [JsonIgnore]
    public bool HasMembershipLabel => MembershipLabel.Length > 0;
}

public sealed class VrcBadge
{
    [JsonPropertyName("badgeId")]
    public string BadgeId { get; set; } = string.Empty;

    [JsonPropertyName("badgeName")]
    public string BadgeName { get; set; } = string.Empty;

    [JsonPropertyName("badgeDescription")]
    public string BadgeDescription { get; set; } = string.Empty;

    [JsonPropertyName("badgeImageUrl")]
    public string? BadgeImageUrl { get; set; }

    [JsonPropertyName("showcased")]
    public bool Showcased { get; set; }
}
