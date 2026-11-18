using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>A friend-list addition or removal detected between refreshes.</summary>
public sealed class FriendChangeItem : ViewModelBase
{
    public FriendChangeItem(
        FriendItem friend,
        bool isAdded,
        DateTimeOffset timestamp,
        bool detectedAfterRelaunch = false)
    {
        Friend = friend;
        IsAdded = isAdded;
        Timestamp = timestamp;
        DetectedAfterRelaunch = detectedAfterRelaunch;
    }

    /// <summary>Rebuilds a change row from its persisted activity event.</summary>
    public FriendChangeItem(ActivityEntry entry)
        : this(
            new FriendItem(new VrcUser
            {
                Id = entry.UserId,
                DisplayName = entry.DisplayName,
                Username = entry.UserName,
                Tags = entry.Tags ?? new List<string>(),
                DeveloperType = entry.DeveloperType,
                UserIcon = entry.AvatarUrl
            }),
            entry.Type == ActivityType.FriendAdded,
            entry.Timestamp,
            entry.DetectedAfterRelaunch)
    {
    }

    public FriendItem Friend { get; }

    public bool IsAdded { get; }

    public bool IsRemoved => !IsAdded;

    public string DisplayName => Friend.DisplayName;

    public string Username => Friend.Username;

    public string AvatarUrl => Friend.AvatarUrl;

    public string IconFrameUrl => Friend.IconFrameUrl;

    public string IconFrameIntroUrl => Friend.IconFrameIntroUrl;

    public bool HasIconFrame => Friend.HasIconFrame;

    public string TrustLabel => Friend.TrustLabel;

    public System.Windows.Media.Brush TrustBrush => Friend.TrustBrush;

    public DateTimeOffset Timestamp { get; }

    public bool DetectedAfterRelaunch { get; }

    public string ChangeLabel => IsAdded ? "Friended you" : "Unfriended you";

    public string DetailText => IsAdded
        ? "is now on your friends list"
        : "is no longer on your friends list";

    public string RelativeTime => TimeText.Relative(Timestamp);

    public string TimestampText
    {
        get
        {
            if (Timestamp == default)
            {
                return string.Empty;
            }

            var text = Timestamp.ToLocalTime().ToString("MMM d, yyyy h:mm tt");
            return DetectedAfterRelaunch
                ? $"{text} · detected on next launch"
                : text;
        }
    }
}
