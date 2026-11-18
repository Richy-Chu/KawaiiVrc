using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;
using VrcCompanion.Services;

namespace VrcCompanion.Models;

/// <summary>
/// Represents a VRChat avatar with metadata for avatar management.
/// </summary>
public sealed class Avatar : INotifyPropertyChanged
{
    private bool _isFavorite;
    private bool _isWearing;
    private bool _isLocalFavorite;
    private bool _isOwnedByCurrentUser;

    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("authorId")]
    public string AuthorId { get; set; } = string.Empty;

    [JsonPropertyName("authorName")]
    public string AuthorName { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string ImageUrl { get; set; } = string.Empty;

    [JsonPropertyName("thumbnailImageUrl")]
    public string ThumbnailImageUrl { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("releaseStatus")]
    public string ReleaseStatus { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("unityPackages")]
    public List<AvatarUnityPackage> UnityPackages { get; set; } = new();

    [JsonPropertyName("favorite")]
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }

            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAnyFavorite)));
        }
    }

    [JsonPropertyName("useCount")]
    public int UseCount { get; set; }

    [JsonPropertyName("assetUrl")]
    public string AssetUrl { get; set; } = string.Empty;

    [JsonPropertyName("assetUrlSecondary")]
    public string AssetUrlSecondary { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public List<string> Platform { get; set; } = new();

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("favorites")]
    public int FavoriteCount { get; set; }

    /// <summary>
    /// VRChat reports failed security checks on the uploaded bundle. Keep both
    /// the avatar-level alias some API responses use and the per-package value
    /// below, so a community row does not lose the state when its details merge.
    /// </summary>
    [JsonPropertyName("scanStatus")]
    public string ScanStatus { get; set; } = string.Empty;

    [JsonPropertyName("hasAvatarSecurityChecksFailed")]
    public bool HasAvatarSecurityChecksFailed { get; set; }

    [JsonPropertyName("hasSecurityChecksFailed")]
    public bool HasSecurityChecksFailedFromApi { get; set; }

    /// <summary>Saved by the app in its own unlimited Local slot, not by VRChat.</summary>
    [JsonIgnore]
    public bool IsLocalFavorite
    {
        get => _isLocalFavorite;
        set
        {
            if (_isLocalFavorite == value)
            {
                return;
            }

            _isLocalFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLocalFavorite)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAnyFavorite)));
        }
    }

    /// <summary>True for either a VRChat favourite or the app's Local slot.</summary>
    [JsonIgnore]
    public bool IsAnyFavorite => IsFavorite || IsLocalFavorite;

    /// <summary>
    /// Set by the view model when VRChat says the signed-in account created this
    /// avatar. Private avatars are wearable only by their owner, so this is what
    /// separates "my private avatar" from "someone else's private avatar".
    /// </summary>
    [JsonIgnore]
    public bool IsOwnedByCurrentUser
    {
        get => _isOwnedByCurrentUser;
        set
        {
            if (_isOwnedByCurrentUser == value)
            {
                return;
            }

            _isOwnedByCurrentUser = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOwnedByCurrentUser)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUse)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrivacyHint)));
        }
    }

    /// <summary>Set by the view model from the account's current avatar id.</summary>
    [JsonIgnore]
    public bool IsWearing
    {
        get => _isWearing;
        set
        {
            if (_isWearing == value)
            {
                return;
            }

            _isWearing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWearing)));
        }
    }

    public string DescriptionText => string.IsNullOrWhiteSpace(Description)
        ? "No description provided."
        : Description.Trim();

    public string ReleaseStatusLabel => string.IsNullOrWhiteSpace(ReleaseStatus)
        ? "Unknown"
        : IsUnavailable
            ? "Unavailable"
            : char.ToUpperInvariant(ReleaseStatus[0]) + ReleaseStatus[1..];

    /// <summary>Which community index supplied this row (empty for VRChat itself).</summary>
    public string SourceName { get; set; } = string.Empty;

    public bool HasSourceName => !string.IsNullOrWhiteSpace(SourceName);

    /// <summary>Card caption naming the index the result came from.</summary>
    public string SourceLabel => HasSourceName ? $"via {SourceName}" : string.Empty;

    /// <summary>Private avatars can be viewed but only the owner can wear them.</summary>
    public bool IsPrivate => string.Equals(ReleaseStatus, "private", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// VRChat refuses to load, wear, or favourite an avatar whose server-side
    /// security checks failed. Treat that exactly like a private avatar in the
    /// UI so the action buttons are visibly disabled instead of failing later.
    /// </summary>
    public bool HasSecurityCheckFailed
        => HasAvatarSecurityChecksFailed ||
           HasSecurityChecksFailedFromApi ||
           string.Equals(ScanStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
           UnityPackages.Any(package =>
               string.Equals(package.ScanStatus, "failed", StringComparison.OrdinalIgnoreCase));

    /// <summary>VRChat did not expose a record for this avatar, so its state is unverified.</summary>
    public bool IsUnavailable => string.Equals(ReleaseStatus, "unavailable", StringComparison.OrdinalIgnoreCase);

    /// <summary>Your own private avatars are usable; another user's private avatar is not.</summary>
    public bool CanUse => (!IsPrivate || IsOwnedByCurrentUser) &&
                          !IsUnavailable &&
                          !HasSecurityCheckFailed &&
                          !string.IsNullOrWhiteSpace(Id);

    /// <summary>Explains why the wear/favourite actions are disabled.</summary>
    public string UseHint => HasSecurityCheckFailed ? SecurityCheckFailedHint : PrivacyHint;

    public string PrivacyHint => IsUnavailable
        ? "This avatar's record is unavailable — it may be private, deleted, or blocked by VRChat"
        : IsPrivate
            ? IsOwnedByCurrentUser
                ? "Private — this avatar is yours, so you can wear it"
                : "Private — only the owner can wear it"
            : string.Empty;

    public bool ShowPrivacyHint => IsPrivate || IsUnavailable;

    public string SecurityCheckFailedHint => HasSecurityCheckFailed
        ? "Security checks failed — VRChat will not load, wear, or favourite this avatar"
        : string.Empty;

    /// <summary>Short chip text shown on search cards: PUBLIC / PRIVATE / etc.</summary>
    public string ReleaseStatusBadge => string.IsNullOrWhiteSpace(ReleaseStatus)
        ? "UNKNOWN"
        : ReleaseStatus.ToUpperInvariant();

    private IEnumerable<string> PlatformNames
    {
        get
        {
            if (UnityPackages.Count == 0)
            {
                return Platform;
            }

            // VRChat can list impostor bundles for platforms that have no real
            // avatar upload. VRCX and VRCNext ignore these when deciding which
            // platforms an avatar actually supports.
            return UnityPackages
                .Where(package => !package.IsImpostor)
                .Select(package => package.Platform);
        }
    }

    /// <summary>
    /// Platform labels from the actual uploaded bundles. Normalising before
    /// removing duplicates keeps "pc"/"PC" and "android"/"quest" from
    /// appearing twice on cards.
    /// </summary>
    public IReadOnlyList<string> PlatformLabels
        => PlatformNames
            .Select(PlatformLabel)
            .Where(label => label.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool HasPlatforms => PlatformLabels.Count > 0;

    public string PlatformsText => HasPlatforms
        ? string.Join(" · ", PlatformLabels)
        : "Not specified";

    private static string PlatformLabel(string? platform)
    {
        var value = platform?.Trim().ToLowerInvariant();
        return value switch
        {
            "standalonewindows" or "standalonewindows64" or "pc" or "windows" => "PC",
            "android" or "quest" or "oculus" => "Quest",
            "ios" => "iOS",
            null or "" => string.Empty,
            _ => platform!.Trim()
        };
    }

    public string CreatedText => CreatedAt == default
        ? "Unknown"
        : CreatedAt.LocalDateTime.ToString("d MMM yyyy");

    public string UpdatedText => UpdatedAt == default
        ? "Unknown"
        : UpdatedAt.LocalDateTime.ToString("d MMM yyyy");

    /// <summary>Readable content/author tags for cards and detail text.</summary>
    public IReadOnlyList<string> TagLabels => Tags
        .Select(TagLabel)
        .Where(label => label.Length > 0)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public bool HasTags => TagLabels.Count > 0;

    /// <summary>
    /// Adds tags learned from a filtered list or a full avatar record. Returns
    /// true when anything changed so callers can refresh the visible cards.
    /// </summary>
    public bool MergeTags(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return false;
        }

        var merged = Tags
            .Concat(tags)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (merged.Count == Tags.Count &&
            merged.All(tag => Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        Tags = merged;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        return true;
    }

    public string TagsText => TagLabels.Count == 0
        ? "No tags"
        : string.Join(", ", TagLabels);

    private static string TagLabel(string? tag)
    {
        var value = tag?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lower = value.ToLowerInvariant();
        if (lower.StartsWith("system_", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (lower.StartsWith("author_tag_", StringComparison.Ordinal))
        {
            value = value[11..];
            return value.Replace('_', ' ').Trim();
        }

        if (lower.StartsWith("content_", StringComparison.Ordinal))
        {
            return lower switch
            {
                "content_horror" => "Horror content",
                "content_gore" => "Gore content",
                "content_violence" => "Violence content",
                "content_adult" => "Adult content",
                "content_sex" => "Sexual content",
                _ => HumanizeTag(value[8..]) + " content"
            };
        }

        return value;
    }

    private static string HumanizeTag(string value)
    {
        var words = value.Replace('_', ' ').Trim();
        return words.Length == 0
            ? words
            : char.ToUpperInvariant(words[0]) + words[1..];
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>
    /// Full-size image for the detail panel. Community indexes that proxy
    /// VRChat files are rewritten to VRChat's own endpoint so the image loads.
    /// </summary>
    public string DisplayImageUrl
        => ImageUrlHelper.NormalizeCommunityImageUrl(
               string.IsNullOrWhiteSpace(ImageUrl) ? ThumbnailImageUrl : ImageUrl) ?? string.Empty;

    /// <summary>Resized thumbnail URL for the avatar grid, loaded through the image cache.</summary>
    public string ThumbnailUrl
        => ImageUrlHelper.ToThumbnailUrl(
               ImageUrlHelper.NormalizeCommunityImageUrl(
                   string.IsNullOrWhiteSpace(ThumbnailImageUrl) ? ImageUrl : ThumbnailImageUrl),
               256) ?? string.Empty;

    /// <summary>
    /// Completes a community index row with VRChat's authoritative avatar record.
    /// Index rows often omit creation time, platform bundles, or the full
    /// description; the API record fills those in without replacing artwork the
    /// index supplied.
    /// </summary>
    public void MergeDetailsFrom(Avatar details)
    {
        if (ReferenceEquals(this, details))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(details.Name)) Name = details.Name;
        if (!string.IsNullOrWhiteSpace(details.AuthorId)) AuthorId = details.AuthorId;
        if (!string.IsNullOrWhiteSpace(details.AuthorName)) AuthorName = details.AuthorName;
        if (!string.IsNullOrWhiteSpace(details.ImageUrl)) ImageUrl = details.ImageUrl;
        if (!string.IsNullOrWhiteSpace(details.ThumbnailImageUrl)) ThumbnailImageUrl = details.ThumbnailImageUrl;
        if (!string.IsNullOrWhiteSpace(details.ReleaseStatus)) ReleaseStatus = details.ReleaseStatus;
        if (!string.IsNullOrWhiteSpace(details.Description)) Description = details.Description;
        if (!string.IsNullOrWhiteSpace(details.AssetUrl)) AssetUrl = details.AssetUrl;
        if (!string.IsNullOrWhiteSpace(details.AssetUrlSecondary)) AssetUrlSecondary = details.AssetUrlSecondary;
        if (details.CreatedAt != default) CreatedAt = details.CreatedAt;
        if (details.UpdatedAt != default) UpdatedAt = details.UpdatedAt;
        if (details.Tags.Count > 0) Tags = details.Tags;

        // Platform data from the authoritative record replaces an index's
        // broader guess even when VRChat returns an empty bundle list. Keeping
        // the old list here was what made every platform look PC + Quest + iOS.
        UnityPackages = details.UnityPackages;
        Platform = details.Platform;

        if (details.Version > 0) Version = details.Version;
        if (details.FavoriteCount > 0) FavoriteCount = details.FavoriteCount;
        if (details.HasAvatarSecurityChecksFailed) HasAvatarSecurityChecksFailed = true;
        if (details.HasSecurityChecksFailedFromApi) HasSecurityChecksFailedFromApi = true;
        if (!string.IsNullOrWhiteSpace(details.ScanStatus)) ScanStatus = details.ScanStatus;

        // WPF treats an empty property name as "all properties changed", which
        // refreshes the name, author, description, dates, and platforms at once.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>One exported platform bundle of an avatar.</summary>
public sealed class AvatarUnityPackage
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("variant")]
    public string Variant { get; set; } = string.Empty;

    /// <summary>An impostor bundle does not mean the avatar was uploaded for that platform.</summary>
    [JsonIgnore]
    public bool IsImpostor => string.Equals(Variant, "impostor", StringComparison.OrdinalIgnoreCase);

    [JsonPropertyName("performanceRating")]
    public string PerformanceRating { get; set; } = string.Empty;

    /// <summary>"failed" is VRChat's marker for a bundle that cannot be loaded.</summary>
    [JsonPropertyName("scanStatus")]
    public string ScanStatus { get; set; } = string.Empty;
}

/// <summary>
/// Filter options for avatar search.
/// </summary>
public sealed class AvatarFilter
{
    public string SearchTerm { get; set; } = string.Empty;
    public string AuthorId { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public string ReleaseStatus { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// Result of an avatar search.
/// </summary>
public sealed class AvatarSearchResult
{
    public List<Avatar> Avatars { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public bool HasMore => Page * PageSize < TotalCount;
}