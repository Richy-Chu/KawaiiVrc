using System.Diagnostics;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Sender/world context for the notification that is selected in the list.
/// The notification itself renders immediately from the list data; the sender
/// card fills in from /users/{id} and the world card from /worlds/{id}.
/// </summary>
public sealed class NotificationDetailViewModel : ViewModelBase
{
    private static readonly Brush OnlineBrush = MakeBrush("#34D399");
    private static readonly Brush OfflineBrush = MakeBrush("#67718C");

    private readonly VrcApiClient _api;
    private VrcUser? _user;
    private VrcWorld? _world;
    private bool _isLoading;
    private string? _error;

    public NotificationDetailViewModel(NotificationItem notification, VrcApiClient api)
    {
        Notification = notification;
        _api = api;
        notification.PropertyChanged += (_, _) => OnAllPropertiesChanged();
    }

    public NotificationItem Notification { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasSender =>
        !string.IsNullOrWhiteSpace(Notification.SenderUserId) &&
        !Notification.SenderUserId.StartsWith("grp_", StringComparison.OrdinalIgnoreCase) &&
        (!Notification.IsGroup || Notification.IsGroupInvite);

    public bool IsGroup => Notification.IsGroup;

    public bool IsGroupInvite => Notification.IsGroupInvite;

    public bool CanAcceptGroupInvite => Notification.CanAcceptGroupInvite;

    public bool CanDeclineGroupInvite => Notification.CanDeclineGroupInvite;

    public bool HasGroup => Notification.IsGroup;

    public string GroupId => Notification.GroupId;

    public bool CanOpenGroup => Notification.HasGroupId;

    public string GroupName => Notification.GroupName;

    public string GroupTitle => Notification.GroupTitle;

    public bool HasGroupTitle => Notification.HasGroupTitle;

    public string GroupImageUrl => Notification.GroupImageUrl;

    public bool HasGroupImage => Notification.HasGroupImage;

    public string SenderName => string.IsNullOrWhiteSpace(_user?.DisplayName)
        ? Notification.SenderName
        : _user!.DisplayName;

    public string SenderHandle => string.IsNullOrWhiteSpace(_user?.Username)
        ? Notification.SenderHandle
        : $"@{_user!.Username}";

    public string SenderInitial => Notification.SenderInitial;

    public string SenderProfileUrl => HasSender
        ? $"https://vrchat.com/home/user/{Notification.SenderUserId}"
        : "https://vrchat.com/home";

    public string SenderAvatarUrl
    {
        get
        {
            if (_user is null)
            {
                return string.Empty;
            }

            return ImageUrlHelper.PickAvatarThumbnailUrl(
                _user.UserIcon,
                _user.ProfilePicOverrideThumbnail ?? _user.IconUrl,
                _user.CurrentAvatarThumbnailImageUrl,
                _user.CurrentAvatarImageUrl,
                256) ?? string.Empty;
        }
    }

    public bool HasSenderAvatar => !string.IsNullOrWhiteSpace(SenderAvatarUrl);

    public string TrustLabel => UserPresentation.TrustLabel(_user?.Tags, _user?.DeveloperType);

    public Brush TrustBrush => UserPresentation.TrustBrush(_user?.Tags, _user?.DeveloperType);

    public string IconFrameUrl => UserPresentation.IconFrameUrl(_user?.IconFrame) ?? string.Empty;

    public string IconFrameIntroUrl => UserPresentation.IconFrameIntroUrl(_user?.IconFrame) ?? string.Empty;

    public bool HasIconFrame => IconFrameUrl.Length > 0;

    public string ProfileEffectUrl => UserPresentation.ProfileEffectUrl(_user?.ProfileEffect) ?? string.Empty;

    public string ProfileEffectIntroUrl => UserPresentation.ProfileEffectIntroUrl(_user?.ProfileEffect) ?? string.Empty;

    public bool HasProfileEffect => ProfileEffectUrl.Length > 0;

    public string SenderStatusLabel => _user is null
        ? "Unknown"
        : UserPresence.Label(_user.Status, SenderPresence);

    public Brush SenderStatusBrush => _user is null
        ? OfflineBrush
        : UserPresence.Brush(_user.Status, SenderPresence);

    private UserPresenceState SenderPresence => _user is null
        ? UserPresenceState.Offline
        : UserPresence.Classify(_user.Location, _user.Status, _user.State, _user.Platform ?? _user.LastPlatform);

    /// <summary>The sender's current world, when VRChat exposes it to you.</summary>
    public string UserLocation => _user?.Location ?? string.Empty;

    // For an invite, the invited instance is the destination the sender chose.
    // Do not offer their current location as a second, possibly different join.
    public bool CanJoinUser => !Notification.IsInvite && LaunchService.IsLocation(UserLocation);

    public string UserLocationLine => CanJoinUser
        ? "In a world you can join"
        : SenderPresence == UserPresenceState.Active
            ? "Active on web"
            : SenderPresence == UserPresenceState.Offline
                ? "Offline"
                : "Their world is hidden";

    public string SenderBio => string.IsNullOrWhiteSpace(_user?.Bio) ? string.Empty : _user!.Bio!.Trim();

    public bool HasSenderBio => !string.IsNullOrWhiteSpace(SenderBio);

    public string TypeLabel => Notification.TypeLabel;

    public Brush TypeBrush => Notification.TypeBrush;

    public string TypeGlyph => Notification.TypeGlyph;

    public string MessageText => Notification.MessageText;

    public bool IsBoop => Notification.IsBoop;

    public string BoopImageUrl => Notification.BoopImageUrl;

    public bool HasBoopImage => Notification.HasBoopImage;

    public string BoopAttachmentTitle => Notification.BoopAttachmentTitle;

    public string BoopKindText => Notification.BoopKindText;

    public string DetailsText => Notification.DetailsText;

    public bool HasDetailsText => !string.IsNullOrWhiteSpace(DetailsText);

    public string ReceivedText => Notification.ReceivedText;

    public bool IsUnseen => Notification.IsUnseen;

    public bool CanAcceptFriendRequest => Notification.CanAcceptFriendRequest;

    public bool CanAcceptInviteRequest => Notification.CanAcceptInviteRequest;

    public bool CanDeclineNotification => Notification.CanDecline;

    public bool CanJoinInvite => Notification.CanJoinInvite;

    public bool IsFriendRequest => Notification.IsFriendRequest;

    public string NotificationKindText => Notification.IsFriendRequest
        ? "Friend request"
        : Notification.IsInvite
            ? "World invite"
            : Notification.IsInviteRequest
                ? "Invite request"
                : Notification.IsGroupInvite
                    ? "Group invite"
                    : Notification.IsGroup
                        ? "Group update"
                        : "Notification";

    public string NotificationKindGlyph => Notification.IsFriendRequest
        ? "\uE8FA"
        : Notification.TypeGlyph;

    public string InviteLocation => Notification.InviteLocation ?? string.Empty;

    public bool HasWorld => _world is not null ||
                            (Notification.IsInvite &&
                             (!string.IsNullOrWhiteSpace(Notification.InviteLocation) ||
                              !string.IsNullOrWhiteSpace(Notification.InviteWorldName)));

    public string WorldName => !string.IsNullOrWhiteSpace(_world?.Name)
        ? _world!.Name
        : !string.IsNullOrWhiteSpace(Notification.InviteWorldName)
            ? Notification.InviteWorldName
            : "Invited world";

    public string WorldAuthorText => string.IsNullOrWhiteSpace(_world?.AuthorName)
        ? string.Empty
        : $"by {_world!.AuthorName}";

    public string WorldMetaText
    {
        get
        {
            if (_world is null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (_world.Occupants > 0)
            {
                parts.Add($"{_world.Occupants:N0} online");
            }
            if (_world.Capacity > 0)
            {
                parts.Add($"{_world.Capacity} capacity");
            }
            if (!string.IsNullOrWhiteSpace(_world.ReleaseStatus))
            {
                parts.Add(char.ToUpperInvariant(_world.ReleaseStatus![0]) + _world.ReleaseStatus[1..]);
            }

            return string.Join(" · ", parts);
        }
    }

    public string WorldImageUrl => _world is null
        ? string.Empty
        : !string.IsNullOrWhiteSpace(_world.ThumbnailImageUrl)
            ? _world.ThumbnailImageUrl!
            : _world.ImageUrl ?? string.Empty;

    public VrcWorld? World => _world;

    public string ActionLine => Notification.IsFriendRequest
        ? "wants to be friends"
        : Notification.IsInvite
            ? "invited you to a world"
            : Notification.IsGroupInvite
                ? "invited you to a group"
                : Notification.TypeLabel.ToLowerInvariant() switch
            {
                "boop" => "booped you",
                "requestinvite" => "asked you for an invite",
                "inviteresponse" => "replied to your invite",
                "requestinviteresponse" => "replied to your invite request",
                var type when type.Contains("group", StringComparison.Ordinal) => "sent a group update",
                _ => "sent you a notification"
            };

    public async Task LoadAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        IsLoading = true;
        ErrorMessage = null;
        _ = LoadCosmeticsAsync(cancellationToken);

        try
        {
            if (HasSender)
            {
                _user = await _api.GetUserAsync(Notification.SenderUserId, forceRefresh, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // Friend requests frequently arrive before the sender is a
                // friend. The user endpoint then omits the avatar; the public
                // profile still carries the newer iconUrl field.
                if (string.IsNullOrWhiteSpace(SenderAvatarUrl))
                {
                    var profile = await _api.GetProfileAsync(
                        Notification.SenderUserId,
                        cancellationToken: cancellationToken);
                    if (profile is not null && !cancellationToken.IsCancellationRequested)
                    {
                        _user ??= new VrcUser
                        {
                            Id = Notification.SenderUserId,
                            DisplayName = Notification.SenderName
                        };
                        if (string.IsNullOrWhiteSpace(_user.UserIcon))
                        {
                            _user.UserIcon = profile.UserIcon;
                        }
                        if (string.IsNullOrWhiteSpace(_user.IconUrl))
                        {
                            _user.IconUrl = profile.IconUrl;
                        }
                        if (string.IsNullOrWhiteSpace(_user.CurrentAvatarThumbnailImageUrl))
                        {
                            _user.CurrentAvatarThumbnailImageUrl = profile.CurrentAvatarThumbnailImageUrl;
                        }
                        if (string.IsNullOrWhiteSpace(_user.CurrentAvatarImageUrl))
                        {
                            _user.CurrentAvatarImageUrl = profile.CurrentAvatarImageUrl;
                        }
                        _user.IconFrame ??= profile.IconFrame;
                        _user.ProfileEffect ??= profile.ProfileEffect;
                        _user.NameplateEffect ??= profile.NameplateEffect;
                        _user.ThemeIconColor ??= profile.ThemeIconColor;
                        _user.ThemeButtonColor ??= profile.ThemeButtonColor;
                        _user.ThemeSubtextColor ??= profile.ThemeSubtextColor;
                    }
                }
            }

            var worldId = LaunchService.GetWorldIdFromLocation(Notification.InviteLocation);
            if (worldId is not null)
            {
                _world = await _api.GetWorldAsync(worldId, forceRefresh, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            OnAllPropertiesChanged();
        }
        catch (OperationCanceledException)
        {
            // Selection changed.
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat to load the sender details.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not load the full notification details.";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private async Task LoadCosmeticsAsync(CancellationToken cancellationToken)
    {
        if (CosmeticCatalog.IsLoaded)
        {
            return;
        }

        try
        {
            await CosmeticCatalog.EnsureLoadedAsync(_api, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                OnAllPropertiesChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // Selection changed.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static Brush MakeBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
