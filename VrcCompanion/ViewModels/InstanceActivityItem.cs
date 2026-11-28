using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// One row of the instance activity box. It wraps the line VRChat's log wrote, so the
/// row shows the log's own time and never invents an event.
/// </summary>
public sealed class InstanceActivityItem : INotifyPropertyChanged
{
    private static readonly Brush JoinBrush = MakeBrush("#3DDC97");
    private static readonly Brush LeaveBrush = MakeBrush("#8B95AA");
    private static readonly Brush UdonBrush = MakeBrush("#A78BFA");
    private static readonly Brush AvatarBrush = MakeBrush("#F472B6");

    private ActivityEntry? _avatarEntry;
    private string _avatarImageUrl = string.Empty;
    private string _resolvedUserId = string.Empty;
    private string _personAvatarId = string.Empty;
    private string _personAvatarAuthor = string.Empty;
    private string _personAvatarName = string.Empty;
    private int _avatarImagePriority;
    private bool _showsPersonPicture;

    public event PropertyChangedEventHandler? PropertyChanged;

    public InstanceActivityItem(VrChatLogActivity activity, bool isSelf = false)
    {
        Activity = activity;
        IsSelf = isSelf;
    }

    public VrChatLogActivity Activity { get; }

    /// <summary>
    /// True when the log line was the signed-in account's own action. The Activity page
    /// already tags its own rows, and this box is read the same way.
    /// </summary>
    public bool IsSelf { get; }

    public VrChatLogActivityKind Kind => Activity.Kind;

    /// <summary>Filter key used by <see cref="InstanceActivityFeed.SelectedActivityFilter"/>.</summary>
    public string KindKey => Activity.Kind switch
    {
        VrChatLogActivityKind.Joined => "joined",
        VrChatLogActivityKind.Left => "left",
        VrChatLogActivityKind.AvatarChanged => "avatar",
        _ => "udon"
    };

    public string KindLabel => Activity.Kind switch
    {
        VrChatLogActivityKind.Joined => "JOINED",
        VrChatLogActivityKind.Left => "LEFT",
        VrChatLogActivityKind.AvatarChanged => "AVATAR",
        _ => "UDON"
    };

    public Brush KindBrush => Activity.Kind switch
    {
        VrChatLogActivityKind.Joined => JoinBrush,
        VrChatLogActivityKind.Left => LeaveBrush,
        VrChatLogActivityKind.AvatarChanged => AvatarBrush,
        _ => UdonBrush
    };

    public bool IsUdon => Activity.Kind == VrChatLogActivityKind.Udon;

    public bool IsAvatarChange => Activity.Kind == VrChatLogActivityKind.AvatarChanged;

    public string DisplayName => string.IsNullOrWhiteSpace(Activity.DisplayName)
        ? (IsUdon ? "Udon" : "Someone")
        : Activity.DisplayName;

    /// <summary>The name for a person, the event for Udon.</summary>
    public string Title => IsUdon ? "Udon event" : DisplayName;

    public string DetailText
    {
        get
        {
            if (IsAvatarChange)
            {
                return AvatarName.Length == 0
                    ? "changed avatar"
                    : AvatarAuthor.Length == 0
                        ? $"now wearing {AvatarName}"
                        : $"now wearing {AvatarName} by {AvatarAuthor}";
            }

            return string.IsNullOrWhiteSpace(Activity.Detail) ? KindLabel : Activity.Detail;
        }
    }

    /// <summary>The avatar VRChat's log named for this switch; empty for every other row.</summary>
    public string AvatarName => string.IsNullOrWhiteSpace(Activity.AvatarName)
        ? (IsAvatarChange ? Activity.Detail : string.Empty)
        : Activity.AvatarName;

    public string AvatarAuthor => Activity.AvatarAuthor;

    /// <summary>
    /// The Activity page's own row model for this switch, so a click runs the same commands
    /// that page uses: the avatar popup resolves a name-only switch to a public avatar, and
    /// the profile action opens whoever changed on VRChat.
    /// </summary>
    public ActivityEntry? AvatarEntry => IsAvatarChange
        ? _avatarEntry ??= BuildAvatarEntry()
        : null;

    /// <summary>
    /// The round picture the row draws: the avatar the switch named, and only when that
    /// avatar has no public picture, the person's own picture. The person icon must never
    /// stand in for the avatar - that is what made a row that says "now wearing X" show
    /// a face that is not X.
    /// </summary>
    public string AvatarImageUrl => _avatarImageUrl;

    public bool HasAvatarImage => IsAvatarChange && _avatarImageUrl.Length > 0;

    /// <summary>
    /// Every avatar row draws the same round avatar slot the Activity page draws, even
    /// while its picture is unknown, so one private avatar does not leave a text chip in
    /// a list of pictures.
    /// </summary>
    public bool ShowAvatarIcon => IsAvatarChange;

    /// <summary>Joins, leaves and Udon keep their kind chip; avatar rows use the slot.</summary>
    public bool ShowKindPill => !IsAvatarChange;

    public string AvatarImageTooltip
    {
        get
        {
            var avatar = AvatarName.Length == 0 ? "their new avatar" : AvatarName;
            return _showsPersonPicture
                ? $"This player's VRChat profile picture · now wearing {avatar}"
                : $"The avatar this player switched to · {avatar}";
        }
    }

    /// <summary>
    /// The person's own picture, used only to fill a slot the avatar's picture could not.
    /// It never replaces an avatar picture, so a row can correct a fallback when the real
    /// avatar image arrives.
    /// </summary>
    public void SetPersonImage(string? url)
    {
        if (!IsAvatarChange)
        {
            return;
        }

        var next = url?.Trim() ?? string.Empty;
        if (next.Length == 0 || _avatarImageUrl.Length > 0)
        {
            return;
        }

        _showsPersonPicture = true;
        ApplyImage(next);
    }

    /// <summary>
    /// The switched avatar's picture, found from the name the log wrote. It always wins
    /// over a person picture, so the row shows an avatar rather than a face.
    /// </summary>
    public void SetAvatarImage(string? url) => ApplyAvatarImage(url, priority: 1);

    /// <summary>
    /// The avatar on the player's own VRChat profile, which is exactly the picture the
    /// Activity page draws on an "Avatar changed" row. A name search can return a
    /// different avatar that happens to share the name, so the player's own record wins
    /// over it whenever VRChat gives its picture. Its id and name go to the popup too, so
    /// View avatar opens that same avatar instead of searching the name.
    /// </summary>
    public void SetPersonAvatarImage(
        string? url,
        string? avatarId = null,
        string? avatarName = null,
        string? avatarAuthor = null)
    {
        if (!IsAvatarChange)
        {
            return;
        }

        var id = avatarId?.Trim() ?? string.Empty;
        var name = avatarName?.Trim() ?? string.Empty;
        var author = avatarAuthor?.Trim() ?? string.Empty;
        // The profile's current avatar is this row's avatar only when the profile names the
        // same avatar or does not name one. A profile naming a different avatar is about a
        // later switch, and stamping its id here would open the wrong record. The author the
        // log named is the second check: a different creator means a different avatar even
        // when the names are identical.
        var authorConflict = AvatarAuthor.Length > 0 &&
                             author.Length > 0 &&
                             !ImageUrlHelper.AuthorFits(author, AvatarAuthor);
        var sameAvatar = !authorConflict &&
                         (name.Length == 0 ||
                          AvatarName.Length == 0 ||
                          string.Equals(name, AvatarName.Trim(), StringComparison.OrdinalIgnoreCase));
        // The id is the player's own record, so it identifies the avatar even when VRChat
        // does not expose that avatar's picture (a private avatar usually has none). The
        // popup then reports that exact record instead of searching the name and opening
        // a different avatar that happens to share it.
        if (id.Length > 0 && sameAvatar)
        {
            _personAvatarId = id;
            if (name.Length > 0)
            {
                _personAvatarName = name;
            }

            if (_avatarEntry is { } entry)
            {
                entry.AvatarId = id;
                entry.AvatarIdIsAuthoritative = true;
                // The person lookup read the player's current VRChat record, so only this
                // id may be reported as private when VRChat refuses the avatar.
                entry.AvatarIdIsCurrentAvatar = true;
                if (name.Length > 0)
                {
                    entry.SetAvatarName(name);
                }
            }
        }

        // The profile knows the avatar's author even when the log only printed its
        // name. That name-plus-author pair is what lets View avatar open the right
        // public record instead of the locked "no author" card. It is only the same
        // avatar when the profile names the avatar this row is about.
        if (author.Length > 0 && name.Length > 0 && sameAvatar)
        {
            _personAvatarAuthor = author;
            if (_avatarEntry is { } entry)
            {
                entry.AvatarAuthor = author;
            }
        }

        ApplyAvatarImage(sameAvatar ? url : null, priority: 2);
    }

    private void ApplyAvatarImage(string? url, int priority)
    {
        if (!IsAvatarChange)
        {
            return;
        }

        var next = url?.Trim() ?? string.Empty;
        if (next.Length == 0 || (_avatarImageUrl.Length > 0 && priority < _avatarImagePriority))
        {
            return;
        }

        _avatarImagePriority = priority;
        _showsPersonPicture = false;
        ApplyImage(next);
        if (_avatarEntry is { } entry)
        {
            // The popup draws this picture while the record itself loads.
            entry.AvatarUrl = next;
        }
    }

    private void ApplyImage(string next)
    {
        if (string.Equals(_avatarImageUrl, next, StringComparison.Ordinal))
        {
            return;
        }

        _avatarImageUrl = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarImageUrl)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAvatarImage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarImageTooltip)));
    }

    /// <summary>True when the row can open an avatar popup (the log gave a name or an id).</summary>
    public bool CanOpenAvatar => AvatarEntry?.CanOpenAvatar == true;

    /// <summary>
    /// The player's id: the log's own, or the one VRChat's user lookup found for a switch
    /// line that carried none.
    /// </summary>
    public string UserId => !string.IsNullOrWhiteSpace(Activity.UserId)
        ? Activity.UserId
        : _resolvedUserId;

    /// <summary>
    /// True when the row knows who changed, so the row can open their VRChat profile - the
    /// same action Activity rows carry.
    /// </summary>
    public bool CanOpenProfile => IsAvatarChange && !string.IsNullOrWhiteSpace(UserId);

    /// <summary>
    /// Fills the id a name-only switch line was missing. It only ever fills a blank: the
    /// log's own id stays authoritative.
    /// </summary>
    public void SetUserId(string? userId)
    {
        if (!IsAvatarChange ||
            string.IsNullOrWhiteSpace(userId) ||
            !string.IsNullOrWhiteSpace(Activity.UserId) ||
            _resolvedUserId.Length > 0)
        {
            return;
        }

        _resolvedUserId = userId.Trim();
        if (_avatarEntry is { } entry)
        {
            entry.UserId = _resolvedUserId;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UserId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanOpenProfile)));
    }

    public string AvatarTooltip => CanOpenAvatar
        ? "Open this avatar's details — wear it if it is public"
        : "VRChat's log did not name this avatar.";

    /// <summary>How many identical lines this row stands for; 1 for everything but folded Udon noise.</summary>
    public int RepeatCount => Activity.RepeatCount;

    public bool HasRepeat => Activity.RepeatCount > 1;

    public string RepeatText => Activity.RepeatCount > 1
        ? $"× {Activity.RepeatCount.ToString("N0", CultureInfo.CurrentCulture)}"
        : string.Empty;

    /// <summary>The log line's own time, in the app's 12-hour style.</summary>
    public string TimeText => Activity.At.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture);

    public string ToolTip => IsUdon
        ? $"Udon · {TimeText}" +
          (HasRepeat ? $" · seen {RepeatText} times" : string.Empty) +
          $"\n{DetailText}"
        : $"{DisplayName}{(IsSelf ? " (you)" : string.Empty)} · {KindLabel} · {TimeText}" +
          (IsAvatarChange ? $"\n{DetailText}" : string.Empty);

    public bool HasUserId => !string.IsNullOrWhiteSpace(Activity.UserId);

    private ActivityEntry BuildAvatarEntry() => new()
    {
        Type = ActivityType.AvatarChanged,
        UserId = UserId,
        UserName = DisplayName,
        DisplayName = DisplayName,
        // The player's own avatar outranks the log's name-only guess: its id opens the
        // exact record the row picture came from.
        AvatarId = _personAvatarId.Length > 0 ? _personAvatarId : Activity.AvatarId,
        AvatarName = _personAvatarName.Length > 0 ? _personAvatarName : AvatarName,
        AvatarIdIsAuthoritative = _personAvatarId.Length > 0,
        AvatarIdIsCurrentAvatar = _personAvatarId.Length > 0,
        AvatarAuthor = _personAvatarAuthor.Length > 0
            ? _personAvatarAuthor
            : string.IsNullOrWhiteSpace(AvatarAuthor) ? null : AvatarAuthor,
        AvatarUrl = _showsPersonPicture ? string.Empty : _avatarImageUrl,
        Details = AvatarName.Length == 0 ? "changed avatar" : "changed avatar · " + AvatarName,
        Timestamp = Activity.At
    };

    private static Brush MakeBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
