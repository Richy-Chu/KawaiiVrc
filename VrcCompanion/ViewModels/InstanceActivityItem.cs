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
    /// The avatar's own picture, filled in once the shared lookup resolves the name
    /// VRChat's log wrote. It is what the Activity page draws on its switch rows.
    /// </summary>
    public string AvatarImageUrl => _avatarImageUrl;

    public bool HasAvatarImage => IsAvatarChange && _avatarImageUrl.Length > 0;

    /// <summary>Avatar rows draw the avatar in place of the AVATAR chip once it is known.</summary>
    public bool ShowAvatarImage => HasAvatarImage;

    /// <summary>
    /// Every other row, and an avatar row still waiting for its picture, keeps the
    /// kind chip so the badge column is never blank.
    /// </summary>
    public bool ShowKindPill => !HasAvatarImage;

    public string AvatarImageTooltip => AvatarName.Length == 0
        ? "The avatar this player switched to"
        : $"The avatar this player switched to: {AvatarName}";

    public void SetAvatarImage(string? url)
    {
        if (!IsAvatarChange)
        {
            return;
        }

        var next = url?.Trim() ?? string.Empty;
        if (string.Equals(_avatarImageUrl, next, StringComparison.Ordinal))
        {
            return;
        }

        _avatarImageUrl = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarImageUrl)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAvatarImage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAvatarImage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowKindPill)));
    }

    /// <summary>True when the row can open an avatar popup (the log gave a name or an id).</summary>
    public bool CanOpenAvatar => AvatarEntry?.CanOpenAvatar == true;

    /// <summary>True when VRChat's log told us who changed, so a profile can be opened.</summary>
    public bool CanOpenProfile => IsAvatarChange && !string.IsNullOrWhiteSpace(Activity.UserId);

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
        UserId = Activity.UserId,
        UserName = DisplayName,
        DisplayName = DisplayName,
        AvatarId = Activity.AvatarId,
        AvatarName = AvatarName,
        AvatarAuthor = string.IsNullOrWhiteSpace(AvatarAuthor) ? null : AvatarAuthor,
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
