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

    public bool HasSender => !string.IsNullOrWhiteSpace(Notification.SenderUserId);

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
                _user.ProfilePicOverrideThumbnail,
                _user.CurrentAvatarThumbnailImageUrl,
                _user.CurrentAvatarImageUrl,
                256) ?? string.Empty;
        }
    }

    public string TrustLabel => UserPresentation.TrustLabel(_user?.Tags);

    public string SenderStatusLabel => (_user?.Status ?? string.Empty).ToLowerInvariant() switch
    {
        "active" => "Online",
        "join me" => "Join Me",
        "ask me" => "Ask Me",
        "busy" => "Busy",
        "offline" => "Offline",
        var status when !string.IsNullOrWhiteSpace(status) => char.ToUpperInvariant(status[0]) + status[1..],
        _ => "Unknown"
    };

    public Brush SenderStatusBrush => string.Equals(_user?.Status, "offline", StringComparison.OrdinalIgnoreCase)
        ? OfflineBrush
        : OnlineBrush;

    /// <summary>The sender's current world, when VRChat exposes it to you.</summary>
    public string UserLocation => _user?.Location ?? string.Empty;

    public bool CanJoinUser => LaunchService.IsLocation(UserLocation);

    public string UserLocationLine => CanJoinUser
        ? "In a world you can join"
        : string.IsNullOrWhiteSpace(_user?.Location) || string.Equals(_user!.Location, "offline", StringComparison.OrdinalIgnoreCase)
            ? "Offline"
            : "Their world is hidden";

    public string SenderBio => string.IsNullOrWhiteSpace(_user?.Bio) ? string.Empty : _user!.Bio!.Trim();

    public bool HasSenderBio => !string.IsNullOrWhiteSpace(SenderBio);

    public string TypeLabel => Notification.TypeLabel;

    public Brush TypeBrush => Notification.TypeBrush;

    public string TypeGlyph => Notification.TypeGlyph;

    public string MessageText => Notification.MessageText;

    public string DetailsText => Notification.DetailsText;

    public bool HasDetailsText => !string.IsNullOrWhiteSpace(DetailsText);

    public string ReceivedText => Notification.ReceivedText;

    public bool IsUnseen => Notification.IsUnseen;

    public bool CanAcceptFriendRequest => Notification.CanAcceptFriendRequest;

    public bool CanJoinInvite => Notification.CanJoinInvite;

    public string InviteLocation => Notification.InviteLocation ?? string.Empty;

    public bool HasWorld => _world is not null;

    public string WorldName => string.IsNullOrWhiteSpace(_world?.Name) ? "Invited world" : _world!.Name;

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

        try
        {
            if (HasSender)
            {
                _user = await _api.GetUserAsync(Notification.SenderUserId, forceRefresh, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
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

    private static Brush MakeBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
