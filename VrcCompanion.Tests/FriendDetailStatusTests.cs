using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class FriendDetailStatusTests
{
    private const string UserId = "usr_status_test";

    [Fact]
    public void ProfileStatusIsStableWhenTheFriendListRefreshes()
    {
        var friend = new FriendItem(new VrcUser
        {
            Id = UserId,
            DisplayName = "Status Test",
            Status = "active",
            Location = "wrld_11111111-1111-1111-1111-111111111111:12345"
        });
        var profile = new FriendDetailViewModel(friend, new VrcApiClient(), ImageLoader.Shared)
        {
            Profile = new VrcProfile { Id = UserId, DisplayName = "Status Test", Status = "ask me" }
        };

        Assert.True(profile.IsOnline);
        Assert.Equal("Ask Me", profile.StatusLabel);
        Assert.Same(FriendItem.AskMeBrush, profile.StatusBrush);

        // A background friend-list rebuild must not overwrite the profile's
        // presence while the card is open.
        profile.UpdateFriend(new FriendItem(new VrcUser
        {
            Id = UserId,
            DisplayName = "Status Test",
            Status = "active",
            Location = "wrld_22222222-2222-2222-2222-222222222222:67890"
        }));

        Assert.Equal("Ask Me", profile.StatusLabel);
        Assert.Same(FriendItem.AskMeBrush, profile.StatusBrush);

        profile.ApplyUser(new VrcUser
        {
            Id = UserId,
            DisplayName = "Status Test",
            Status = "active",
            Location = "wrld_22222222-2222-2222-2222-222222222222:67890"
        });
        Assert.Equal("Online", profile.StatusLabel);
        Assert.Same(FriendItem.OnlineBrush, profile.StatusBrush);

        // A new API profile is also an explicit status-change path.
        profile.Profile = new VrcProfile { Id = UserId, DisplayName = "Status Test", Status = "busy" };
        Assert.Equal("Busy", profile.StatusLabel);
        Assert.Same(FriendItem.BusyBrush, profile.StatusBrush);
    }

    [Fact]
    public void OnlineFriendRowWinsOverAStaleOfflineProfile()
    {
        var friend = new FriendItem(new VrcUser
        {
            Id = UserId,
            DisplayName = "Status Test",
            Status = "active",
            Location = "wrld_11111111-1111-1111-1111-111111111111:12345"
        });
        var profile = new FriendDetailViewModel(friend, new VrcApiClient(), ImageLoader.Shared)
        {
            Profile = new VrcProfile { Id = UserId, DisplayName = "Status Test", Status = "offline" }
        };

        // The public profile can lag behind the live friend record. The popup
        // follows the friend row's live presence instead of flipping Offline.
        Assert.True(profile.IsOnline);
        Assert.Equal("Online", profile.StatusLabel);
        Assert.Same(FriendItem.OnlineBrush, profile.StatusBrush);
    }

    [Fact]
    public void ActiveProfileDoesNotMakeAnOfflineFriendOnline()
    {
        var friend = new FriendItem(new VrcUser
        {
            Id = UserId,
            DisplayName = "Status Test",
            Status = "offline",
            Location = "offline"
        });
        var profile = new FriendDetailViewModel(friend, new VrcApiClient(), ImageLoader.Shared)
        {
            Profile = new VrcProfile { Id = UserId, DisplayName = "Status Test", Status = "active" }
        };

        Assert.False(profile.IsOnline);
        Assert.Equal("Offline", profile.StatusLabel);
        Assert.Same(FriendItem.OfflineBrush, profile.StatusBrush);
    }

    [Fact]
    public void PopupNeverSwitchesToAnotherUserFromABackgroundUpdate()
    {
        const string otherId = "usr_other";
        var profile = new FriendDetailViewModel(
            new FriendItem(new VrcUser
            {
                Id = UserId,
                DisplayName = "Maya",
                Status = "join me",
                Location = "wrld_11111111-1111-1111-1111-111111111111:12345"
            }),
            new VrcApiClient(),
            ImageLoader.Shared);

        profile.UpdateFriend(new FriendItem(new VrcUser
        {
            Id = otherId,
            DisplayName = "Someone Else",
            Status = "active",
            Location = "wrld_22222222-2222-2222-2222-222222222222:67890"
        }));
        profile.ApplyUser(new VrcUser
        {
            Id = otherId,
            DisplayName = "Someone Else",
            Status = "offline",
            Location = "offline"
        });
        profile.Profile = new VrcProfile { Id = otherId, DisplayName = "Someone Else" };

        Assert.Equal(UserId, profile.Id);
        Assert.Equal("Maya", profile.DisplayName);
        Assert.Null(profile.Profile);
    }
}