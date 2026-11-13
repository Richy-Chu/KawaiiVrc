using System.Globalization;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

public sealed class FriendItem : ViewModelBase
{
    private static readonly Brush OnlineBrush = MakeBrush("#3DDC97");
    private static readonly Brush JoinMeBrush = MakeBrush("#FFC857");
    private static readonly Brush AskMeBrush = MakeBrush("#FF9F43");
    private static readonly Brush BusyBrush = MakeBrush("#FF5C7A");
    private static readonly Brush OfflineBrush = MakeBrush("#6B7488");

    private VrcUser _user;
    private bool _isFavorite;
    private string _worldName = string.Empty;

    public FriendItem(VrcUser user)
    {
        _user = user;
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteGlyph));
            }
        }
    }

    public string FavoriteGlyph => IsFavorite ? "\uE735" : "\uE734";

    public string Id => _user.Id;

    public string DisplayName => string.IsNullOrWhiteSpace(_user.DisplayName) ? "Unknown user" : _user.DisplayName;

    public string Username => _user.Username;

    public string Location => _user.Location ?? string.Empty;

    public string WorldName => _worldName;

    public bool HasWorldName => !string.IsNullOrWhiteSpace(_worldName);

    public string AvatarUrl
        => ImageUrlHelper.PickAvatarThumbnailUrl(
            _user.UserIcon,
            _user.ProfilePicOverrideThumbnail,
            _user.CurrentAvatarThumbnailImageUrl,
            _user.CurrentAvatarImageUrl,
            256) ?? string.Empty;

    public string BannerUrl => _user.BannerUrl ?? string.Empty;

    public string DateJoined => _user.DateJoined ?? string.Empty;

    public string Initial
    {
        get
        {
            var text = DisplayName.Trim();
            if (text.Length == 0)
            {
                return "?";
            }

            var info = new StringInfo(text);
            return info.SubstringByTextElements(0, 1).ToUpperInvariant();
        }
    }

    public bool IsOnline
    {
        get
        {
            var location = _user.Location ?? string.Empty;
            var status = _user.Status ?? string.Empty;

            // VRChat returns location: null or "offline" when the user is offline.
            // A user is only online if they have a real world/instance location.
            if (string.IsNullOrWhiteSpace(location))
            {
                return false;
            }

            if (string.Equals(location, "offline", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // traveling means they're between worlds but still online.
            if (location.StartsWith("traveling", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // status: "offline" is the definitive offline signal.
            if (string.Equals(status, "offline", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
    }

    public string StatusLabel
    {
        get
        {
            if (!IsOnline)
            {
                return "Offline";
            }

            return (_user.Status ?? string.Empty).ToLowerInvariant() switch
            {
                "active" => "Online",
                "join me" => "Join Me",
                "ask me" => "Ask Me",
                "busy" => "Busy",
                _ => "Online"
            };
        }
    }

    public Brush StatusBrush
    {
        get
        {
            if (!IsOnline)
            {
                return OfflineBrush;
            }

            return (_user.Status ?? string.Empty).ToLowerInvariant() switch
            {
                "active" => OnlineBrush,
                "join me" => JoinMeBrush,
                "ask me" => AskMeBrush,
                "busy" => BusyBrush,
                _ => OnlineBrush
            };
        }
    }

    public int StatusRank => (_user.Status ?? string.Empty).ToLowerInvariant() switch
    {
        "active" => 0,
        "join me" => 1,
        "ask me" => 2,
        "busy" => 3,
        _ => 4
    };

    public string SubText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_user.StatusDescription))
            {
                return _user.StatusDescription.Trim();
            }
            return string.IsNullOrWhiteSpace(_user.Username) ? string.Empty : $"@{_user.Username}";
        }
    }

    public IReadOnlyList<string> Tags => _user.Tags;

    public bool IsInPrivateOrInviteWorld
    {
        get
        {
            var location = _user.Location ?? string.Empty;
            return IsOnline &&
                   (string.Equals(location, "private", StringComparison.OrdinalIgnoreCase) ||
                    location.Contains("~private(", StringComparison.OrdinalIgnoreCase));
        }
    }

    public string LocationAccessLabel
    {
        get
        {
            var location = _user.Location ?? string.Empty;
            if (!IsOnline || string.Equals(location, "offline", StringComparison.OrdinalIgnoreCase))
            {
                return "Offline";
            }

            if (string.Equals(location, "private", StringComparison.OrdinalIgnoreCase))
            {
                return "Invite-only";
            }

            if (location.StartsWith("traveling", StringComparison.OrdinalIgnoreCase))
            {
                return "Traveling";
            }

            if (location.StartsWith("wrld_", StringComparison.OrdinalIgnoreCase))
            {
                return location.Contains("~group(", StringComparison.OrdinalIgnoreCase) ? "Group"
                    : location.Contains("~private(", StringComparison.OrdinalIgnoreCase) && location.Contains("canRequestInvite", StringComparison.OrdinalIgnoreCase) ? "Invite+"
                    : location.Contains("~private(", StringComparison.OrdinalIgnoreCase) ? "Invite"
                    : location.Contains("~friends(", StringComparison.OrdinalIgnoreCase) && location.Contains("canRequestInvite", StringComparison.OrdinalIgnoreCase) ? "Friends+"
                    : location.Contains("~friends(", StringComparison.OrdinalIgnoreCase) ? "Friends"
                    : location.Contains("~hidden(", StringComparison.OrdinalIgnoreCase) ? "Friends+"
                    : "Public";
            }

            return "Online";
        }
    }

    public string LocationText
    {
        get
        {
            var location = _user.Location ?? string.Empty;
            if (!IsOnline || string.Equals(location, "offline", StringComparison.OrdinalIgnoreCase))
            {
                return "Offline";
            }

            if (string.Equals(location, "private", StringComparison.OrdinalIgnoreCase))
            {
                return "In a private world";
            }

            if (location.StartsWith("traveling", StringComparison.OrdinalIgnoreCase))
            {
                return "Traveling";
            }

            if (location.StartsWith("wrld_", StringComparison.OrdinalIgnoreCase))
            {
                return $"In a world · {LocationAccessLabel}";
            }

            return "Online";
        }
    }

    public bool IsJoinable
        => IsOnline && LaunchService.IsLocation(_user.Location);

    public string LocationTitle => HasWorldName ? WorldName : LocationAccessLabel;

    public string LocationDetail
        => IsOnline ? $"{LocationAccessLabel} · {PlatformText}".TrimEnd(' ', '·') : ActivityText;

    public void SetWorldName(string? worldName, string? location = null)
    {
        _worldName = worldName?.Trim() ?? string.Empty;
        OnPropertyChanged(nameof(WorldName));
        OnPropertyChanged(nameof(HasWorldName));
        OnPropertyChanged(nameof(LocationTitle));
        OnPropertyChanged(nameof(LocationDetail));
    }

    public string JoinLocation => _user.Location ?? string.Empty;

    public string PlatformText
    {
        get
        {
            var platform = (_user.LastPlatform ?? _user.Platform ?? string.Empty).ToLowerInvariant();
            return platform switch
            {
                "standalonewindows" or "standalonewindows64" => "PC",
                "android" => "Quest",
                "ios" => "iOS",
                "web" => "Web",
                "" => string.Empty,
                _ => char.ToUpperInvariant(platform[0]) + platform[1..]
            };
        }
    }

    public string ActivityText
    {
        get
        {
            if (IsOnline)
            {
                return string.IsNullOrWhiteSpace(PlatformText) ? "Online now" : PlatformText;
            }

            var lastSeen = TimeText.Relative(_user.LastActivity ?? _user.LastLogin);
            return string.IsNullOrWhiteSpace(lastSeen) ? "Offline" : $"Last seen {lastSeen}";
        }
    }

    public void Update(VrcUser user)
    {
        var locationChanged = !string.Equals(_user.Location, user.Location, StringComparison.Ordinal);
        _user = user;
        if (locationChanged)
        {
            _worldName = string.Empty;
        }

        OnAllPropertiesChanged();
    }

    /// <summary>
    /// Demotes the friend to offline when a fast refresh proves they are no
    /// longer in the online friend set, without downloading their offline data.
    /// </summary>
    public void MarkOffline()
    {
        if (!IsOnline)
        {
            return;
        }

        _user.Location = "offline";
        _user.Status = "offline";
        OnAllPropertiesChanged();
    }

    private static Brush MakeBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
