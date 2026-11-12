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
        : char.ToUpperInvariant(ReleaseStatus[0]) + ReleaseStatus[1..];

    /// <summary>Which community index supplied this row (empty for VRChat itself).</summary>
    public string SourceName { get; set; } = string.Empty;

    public bool HasSourceName => !string.IsNullOrWhiteSpace(SourceName);

    /// <summary>Card caption naming the index the result came from.</summary>
    public string SourceLabel => HasSourceName ? $"via {SourceName}" : string.Empty;

    /// <summary>Private avatars can be viewed but only the owner can wear them.</summary>
    public bool IsPrivate => string.Equals(ReleaseStatus, "private", StringComparison.OrdinalIgnoreCase);

    public string PrivacyHint => IsPrivate
        ? "Private — only the owner can wear it"
        : string.Empty;

    /// <summary>Short chip text shown on search cards: PUBLIC / PRIVATE / etc.</summary>
    public string ReleaseStatusBadge => string.IsNullOrWhiteSpace(ReleaseStatus)
        ? "UNKNOWN"
        : ReleaseStatus.ToUpperInvariant();

    private IEnumerable<string> PlatformNames
        => UnityPackages.Count > 0 ? UnityPackages.Select(package => package.Platform) : Platform;

    public string PlatformsText => !PlatformNames.Any()
        ? "Not specified"
        : string.Join(" · ", PlatformNames.Distinct().Select(platform => platform.ToLowerInvariant() switch
        {
            "standalonewindows" or "standalonewindows64" => "PC",
            "android" => "Quest",
            "ios" => "iOS",
            var value when !string.IsNullOrWhiteSpace(value) => value,
            _ => "Unknown"
        }));

    public string CreatedText => CreatedAt == default
        ? "Unknown"
        : CreatedAt.LocalDateTime.ToString("d MMM yyyy");

    public string UpdatedText => UpdatedAt == default
        ? "Unknown"
        : UpdatedAt.LocalDateTime.ToString("d MMM yyyy");

    public string TagsText => Tags.Count == 0 ? "No tags" : string.Join(", ", Tags);

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>Resized thumbnail URL for the avatar grid, loaded through the image cache.</summary>
    public string ThumbnailUrl
        => ImageUrlHelper.ToThumbnailUrl(
               string.IsNullOrWhiteSpace(ThumbnailImageUrl) ? ImageUrl : ThumbnailImageUrl,
               256) ?? string.Empty;
}

/// <summary>One exported platform bundle of an avatar.</summary>
public sealed class AvatarUnityPackage
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("performanceRating")]
    public string PerformanceRating { get; set; } = string.Empty;
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