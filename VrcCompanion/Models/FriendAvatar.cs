using System.ComponentModel;
using System.Runtime.CompilerServices;
using VrcCompanion.Services;

namespace VrcCompanion.Models;

/// <summary>
/// One avatar associated with a VRChat user: the one they are wearing now or
/// one this app has seen them wear before. VRChat does not expose another
/// user's avatar history, so historical rows come from the local account store.
/// </summary>
public sealed class FriendAvatar : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _imageUrl = string.Empty;
    private string _thumbnailUrl = string.Empty;
    private string _releaseStatus = string.Empty;
    private DateTimeOffset _lastSeen;
    private bool _isCurrent;
    private bool _isPrivacyUnverified;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string AvatarId { get; set; } = string.Empty;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value ?? string.Empty);
    }

    public string ImageUrl
    {
        get => _imageUrl;
        set => SetField(ref _imageUrl, value ?? string.Empty);
    }

    public string ThumbnailUrl
    {
        get => _thumbnailUrl;
        set
        {
            if (SetField(ref _thumbnailUrl, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailUrl);

    /// <summary>VRChat's releaseStatus once the avatar record has been resolved.</summary>
    public string ReleaseStatus
    {
        get => _releaseStatus;
        set
        {
            if (SetField(ref _releaseStatus, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsPrivate));
                OnPropertyChanged(nameof(IsPublic));
                OnPropertyChanged(nameof(PrivacyLabel));
                OnPropertyChanged(nameof(PrivacyGlyph));
                OnPropertyChanged(nameof(HasResolvedPrivacy));
                OnPropertyChanged(nameof(CanOpen));
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public DateTimeOffset LastSeen
    {
        get => _lastSeen;
        set
        {
            if (SetField(ref _lastSeen, value))
            {
                OnPropertyChanged(nameof(SeenLabel));
            }
        }
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetField(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(SeenLabel));
            }
        }
    }

    /// <summary>
    /// A public avatar found by author id in VRChat or a community index. These
    /// are not the person's currently worn avatar; they are public work that can
    /// still be inspected and worn through the normal avatar popup.
    /// </summary>
    public bool IsIndexed { get; set; }

    /// <summary>
    /// The profile picture shown when VRChat exposes no wearable avatar data.
    /// It is not an avatar record and has no open/wear actions.
    /// </summary>
    public bool IsProfilePictureFallback { get; set; }

    /// <summary>The community index that supplied an author lookup result.</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>
    /// A picture-only current-avatar candidate from the profile. It is not
    /// displayed as a locked avatar if the indexes cannot identify it; it is
    /// only a bridge to an indexed public avatar.
    /// </summary>
    public bool IsProfileImageCandidate { get; set; }

    /// <summary>The resolved VRChat avatar, when it is publicly accessible.</summary>
    public Avatar? Details { get; private set; }

    private IReadOnlyList<string> _storedTagLabels = Array.Empty<string>();

    /// <summary>
    /// Human-readable content tags. Uses the full avatar record when it has
    /// been resolved, otherwise the labels remembered from a previous lookup.
    /// </summary>
    public IReadOnlyList<string> TagLabels
    {
        get => Details?.TagLabels is { Count: > 0 } labels ? labels : _storedTagLabels;
        set
        {
            var next = value ?? Array.Empty<string>();
            if (ReferenceEquals(_storedTagLabels, next))
            {
                return;
            }

            _storedTagLabels = next;
            OnPropertyChanged(nameof(TagLabels));
            OnPropertyChanged(nameof(HasTags));
        }
    }

    public bool HasTags => TagLabels.Count > 0;

    public string DisplayName => !string.IsNullOrWhiteSpace(Name)
        ? Name.Trim()
        : IsUnavailable
            ? "Avatar unavailable"
            : IsPrivate
                ? "Private avatar"
                : "Avatar";

    public bool IsPrivate => string.Equals(ReleaseStatus, "private", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when "private" was inferred from a missing image-index match or an
    /// old stored row, not confirmed by the avatar record. An unverified label
    /// must not stop a later lookup from discovering a public avatar.
    /// </summary>
    public bool IsPrivacyUnverified
    {
        get => _isPrivacyUnverified;
        set
        {
            if (SetField(ref _isPrivacyUnverified, value))
            {
                OnPropertyChanged(nameof(IsUnavailable));
                OnPropertyChanged(nameof(PrivacyLabel));
                OnPropertyChanged(nameof(PrivacyGlyph));
                OnPropertyChanged(nameof(HasResolvedPrivacy));
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>
    /// VRChat did not expose the record after we tried its avatar id. This may
    /// mean private, deleted, or otherwise unavailable; it is not safe to call
    /// it private, and the avatar remains non-wearable.
    /// </summary>
    public bool IsUnavailable =>
        string.Equals(ReleaseStatus, "unavailable", StringComparison.OrdinalIgnoreCase) ||
        (IsPrivate && IsPrivacyUnverified);

    public bool IsPublic => string.Equals(ReleaseStatus, "public", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(ReleaseStatus, "hidden", StringComparison.OrdinalIgnoreCase);

    public bool HasResolvedPrivacy => IsPrivate || IsPublic;

    public string PrivacyLabel => IsProfilePictureFallback
        ? "Profile picture"
        : IsUnavailable
            ? "Unavailable"
            : IsPrivate
                ? "Private"
                : IsPublic
                    ? "Public"
                    : "Checking…";

    public string PrivacyGlyph => IsUnavailable
        ? "\uE946"
        : IsPrivate
            ? "\uE72E"
            : IsPublic ? "\uE785" : "\uE946";

    public string SeenLabel => IsProfilePictureFallback
        ? "Profile picture"
        : IsCurrent
            ? "Current"
            : IsIndexed
                ? "Public index"
                : LastSeen == default
                    ? "Seen before"
                    : $"Seen {LastSeen.ToLocalTime():d MMM}";

    public bool CanOpen => !IsProfilePictureFallback &&
                           (!string.IsNullOrWhiteSpace(AvatarId) ||
                            HasThumbnail ||
                            !string.IsNullOrWhiteSpace(Name));

    /// <summary>Merges fresher data without losing a previously resolved privacy state.</summary>
    public void Merge(
        string? name,
        string? imageUrl,
        string? thumbnailUrl,
        string? releaseStatus,
        DateTimeOffset lastSeen,
        bool isCurrent)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            Name = name;
        }

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            ImageUrl = imageUrl;
        }

        if (!string.IsNullOrWhiteSpace(thumbnailUrl))
        {
            ThumbnailUrl = thumbnailUrl;
        }
        else if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            // The friends payload can omit the thumbnail while still carrying
            // the full current-avatar image. VRChat serves that file through the
            // resized image endpoint, so derive the card thumbnail from it.
            ThumbnailUrl = ImageUrlHelper.ToThumbnailUrl(imageUrl, 256) ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(releaseStatus))
        {
            ReleaseStatus = releaseStatus;
            // User/profile payloads do not carry an authoritative private
            // status. It normally comes from an older image lookup, so let the
            // next avatar-id lookup check it instead of treating it as fact.
            if (IsPrivate)
            {
                IsPrivacyUnverified = true;
            }
        }

        if (lastSeen != default && (LastSeen == default || lastSeen < LastSeen))
        {
            LastSeen = lastSeen;
        }

        IsCurrent = isCurrent;
    }

    /// <summary>Applies a full avatar record loaded from VRChat.</summary>
    public void ApplyDetails(Avatar avatar)
    {
        Details = avatar;
        OnPropertyChanged(nameof(TagLabels));
        OnPropertyChanged(nameof(HasTags));
        if (!string.IsNullOrWhiteSpace(avatar.Name))
        {
            Name = avatar.Name;
        }

        if (!string.IsNullOrWhiteSpace(avatar.ImageUrl))
        {
            ImageUrl = avatar.ImageUrl;
        }

        if (!string.IsNullOrWhiteSpace(avatar.ThumbnailImageUrl))
        {
            ThumbnailUrl = ImageUrlHelper.ToThumbnailUrl(avatar.ThumbnailImageUrl, 256) ?? ThumbnailUrl;
        }
        else if (!string.IsNullOrWhiteSpace(avatar.ImageUrl))
        {
            ThumbnailUrl = ImageUrlHelper.ToThumbnailUrl(avatar.ImageUrl, 256) ?? ThumbnailUrl;
        }

        ReleaseStatus = avatar.ReleaseStatus;
        IsPrivacyUnverified = false;
        IsProfileImageCandidate = false;
    }

    /// <summary>
    /// VRChat returns 403/404 for an avatar record that is private or no longer
    /// available. The thumbnail reported on the user can still be shown, but the
    /// avatar has no public details to wear or favourite.
    /// </summary>
    public void MarkUnavailable()
    {
        ReleaseStatus = "private";
        IsPrivacyUnverified = true;
    }

    /// <summary>Builds the record consumed by the shared avatar detail popup.</summary>
    public Avatar ToAvatar(bool isWearing = false)
    {
        if (Details is not null)
        {
            if (IsUnavailable)
            {
                Details.ReleaseStatus = "unavailable";
                if (string.IsNullOrWhiteSpace(Details.Description))
                {
                    Details.Description =
                        "VRChat did not expose this avatar's record. It may be private, deleted, or otherwise unavailable; its details cannot be verified.";
                }
            }

            Details.IsWearing = isWearing;
            return Details;
        }

        return new Avatar
        {
            Id = AvatarId,
            Name = DisplayName,
            ImageUrl = ImageUrl,
            ThumbnailImageUrl = ThumbnailUrl,
            SourceName = SourceName,
            ReleaseStatus = IsUnavailable ? "unavailable" : IsPrivate ? "private" : ReleaseStatus,
            Description = IsUnavailable
                ? "VRChat did not expose this avatar's record. It may be private, deleted, or otherwise unavailable; its details cannot be verified."
                : IsPrivate
                    ? "This avatar is private. Its picture comes from the user's VRChat profile; only the owner can wear it."
                    : string.Empty,
            IsWearing = isWearing
        };
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
