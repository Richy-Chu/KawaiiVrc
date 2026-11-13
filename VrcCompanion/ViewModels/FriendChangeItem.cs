using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>A friend-list addition or removal detected between refreshes.</summary>
public sealed class FriendChangeItem : ViewModelBase
{
    public FriendChangeItem(FriendItem friend, bool isAdded, DateTimeOffset timestamp)
    {
        Friend = friend;
        IsAdded = isAdded;
        Timestamp = timestamp;
    }

    public FriendItem Friend { get; }

    public bool IsAdded { get; }

    public bool IsRemoved => !IsAdded;

    public string DisplayName => Friend.DisplayName;

    public string Username => Friend.Username;

    public string AvatarUrl => Friend.AvatarUrl;

    public DateTimeOffset Timestamp { get; }

    public string ChangeLabel => IsAdded ? "Friended you" : "Unfriended you";

    public string DetailText => IsAdded
        ? "is now on your friends list"
        : "is no longer on your friends list";

    public string RelativeTime => TimeText.Relative(Timestamp);
}
