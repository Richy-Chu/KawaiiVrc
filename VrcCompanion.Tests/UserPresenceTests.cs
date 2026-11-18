using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using System.Text.Json;
using System.Windows.Media;
using Xunit;

namespace VrcCompanion.Tests;

public class UserPresenceTests
{
    [Fact]
    public void StatusColorsMatchVrcxOfficialPalette()
    {
        Assert.Equal(Color.FromRgb(0x2E, 0xD3, 0x19), ((SolidColorBrush)UserPresence.OnlineBrush).Color);
        Assert.Equal(Color.FromRgb(0x00, 0xB8, 0xFF), ((SolidColorBrush)UserPresence.JoinMeBrush).Color);
        Assert.Equal(Color.FromRgb(0xE9, 0x7C, 0x03), ((SolidColorBrush)UserPresence.AskMeBrush).Color);
        Assert.Equal(Color.FromRgb(0xC8, 0x09, 0x28), ((SolidColorBrush)UserPresence.BusyBrush).Color);
        Assert.Equal(Color.FromRgb(0x73, 0x7F, 0x8D), ((SolidColorBrush)UserPresence.OfflineBrush).Color);
    }

    [Fact]
    public void ActiveStatusWithoutALocationIsOffline()
    {
        Assert.False(UserPresence.IsOnline(null, "active"));
        Assert.False(UserPresence.IsOnline("offline", "active"));
        Assert.Equal("Offline", UserPresence.Label("active", isOnline: false));
        Assert.Same(UserPresence.OfflineBrush, UserPresence.Brush("active", isOnline: false));
    }

    [Fact]
    public void LocationDrivesOnlineAndStatusDrivesTheLabel()
    {
        Assert.True(UserPresence.IsOnline("wrld_test:12345", "busy"));
        Assert.Equal("Busy", UserPresence.Label("busy", isOnline: true));
        Assert.Same(UserPresence.BusyBrush, UserPresence.Brush("busy", isOnline: true));
    }

    [Fact]
    public void TravelingCountsAsOnlineAndOfflineStatusWins()
    {
        Assert.True(UserPresence.IsOnline("traveling", "active"));
        Assert.False(UserPresence.IsOnline("wrld_test:12345", "offline"));
    }

    [Fact]
    public void InstanceRowsUseTheSameStatusLabels()
    {
        var player = new InstancePlayerItem(new VrcUser
        {
            Id = "usr_presence",
            DisplayName = "Presence",
            Status = "ask me",
            Location = "wrld_test:12345"
        }, isFriend: false, isSelf: false);

        Assert.Equal("Ask Me", player.StatusText);
        Assert.Same(UserPresence.AskMeBrush, player.StatusBrush);
    }

    [Fact]
    public void ResolvedProfileUpdatesTheStatusUsedByTheInstanceRowAndPopup()
    {
        var player = new InstancePlayerItem("usr_log_only", "Log Only", isFriend: false, isSelf: false);
        Assert.Equal("In instance", player.StatusText);

        player.ApplyProfile(new VrcProfile
        {
            Id = "usr_log_only",
            DisplayName = "Log Only",
            Status = "join me"
        });

        Assert.Equal("Join Me", player.StatusText);
        Assert.Same(UserPresence.JoinMeBrush, player.StatusBrush);

        // The profile popup is built from the same row data and must receive
        // the resolved mode, not the row's initial default.
        var friend = new FriendItem(player.User!);
        friend.Update(new VrcUser
        {
            Id = player.UserId,
            DisplayName = player.DisplayName,
            Status = player.User!.Status,
            Location = "wrld_test:12345"
        });
        var popup = new FriendDetailViewModel(friend, new VrcApiClient(), ImageLoader.Shared);

        Assert.True(popup.IsOnline);
        Assert.Equal("Join Me", popup.StatusLabel);
        Assert.Same(UserPresence.JoinMeBrush, popup.StatusBrush);
    }

    [Fact]
    public void WebActiveIsActiveInsteadOfOffline()
    {
        Assert.False(UserPresence.IsOnline("offline", "active", "active", "web"));
        Assert.True(UserPresence.IsActive("offline", "active", "active", "web"));
        Assert.True(UserPresence.IsPresent("offline", "active", "active", "web"));
        Assert.Equal("Active", UserPresence.Label("active", UserPresenceState.Active));
    }

    [Fact]
    public void WebActiveKeepsTheModeInTheLabel()
    {
        Assert.Equal("Active (Join Me)", UserPresence.Label("join me", UserPresenceState.Active));
        Assert.Same(UserPresence.JoinMeBrush, UserPresence.Brush("join me", UserPresenceState.Active));
    }

    [Fact]
    public void PrivateLocationIsOnlyOnlineWithEvidence()
    {
        Assert.True(UserPresence.IsOnline("private", "join me", "online"));
        Assert.False(UserPresence.IsOnline("private", "join me", null, "standalonewindows"));
        Assert.True(UserPresence.IsActive("offline", "join me", null, "web"));
        Assert.True(UserPresence.IsOnline(
            "private",
            "join me",
            null,
            "standalonewindows",
            inOnlineFriends: true));
        Assert.True(UserPresence.IsActive(
            "private",
            "join me",
            null,
            "standalonewindows",
            inActiveFriends: true));
    }

    [Fact]
    public void PrivateFriendUsesTheCurrentUserOnlineBucketInThePopup()
    {
        var friend = new FriendItem(new VrcUser
        {
            Id = "usr_private_online",
            DisplayName = "Private Online",
            Status = "join me",
            Location = "private",
            State = string.Empty,
            Platform = "standalonewindows"
        });

        Assert.False(friend.IsPresent);
        friend.ApplyPresenceSets(inOnlineFriends: true, inActiveFriends: false);

        Assert.True(friend.IsOnline);
        Assert.Equal("Join Me", friend.StatusLabel);

        var popup = new FriendDetailViewModel(friend, new VrcApiClient(), ImageLoader.Shared);
        Assert.True(popup.IsOnline);
        Assert.Equal("Join Me", popup.StatusLabel);
    }

    [Fact]
    public void InstancePopupKeepsTheLiveWorldWhenAStaleFriendUpdateArrives()
    {
        const string instance = "wrld_00000000-0000-0000-0000-000000000001:123~group(grp_test)";
        var friend = new FriendItem(new VrcUser
        {
            Id = "usr_instance_maya",
            DisplayName = "Maya",
            Status = "join me",
            Location = instance,
            State = "online",
            Platform = "standalonewindows"
        });
        friend.SetWorldName("Popcorn Palace", instance);

        var popup = new FriendDetailViewModel(friend, new VrcApiClient(), ImageLoader.Shared);
        popup.PinToInstance(instance);

        popup.UpdateFriend(new FriendItem(new VrcUser
        {
            Id = friend.Id,
            DisplayName = friend.DisplayName,
            Status = "offline",
            Location = "private",
            State = "offline",
            Platform = "standalonewindows"
        }));

        Assert.True(popup.IsOnline);
        Assert.Equal("Join Me", popup.StatusLabel);
        Assert.Contains("Popcorn Palace", popup.WorldLine);
        Assert.Equal(instance, popup.JoinLocation);
    }

    [Fact]
    public void OnlineStateOverridesAStaleOfflineStatusOnlyWithARealLocation()
    {
        Assert.True(UserPresence.IsOnline("wrld_test:12345", "offline", "online"));
        Assert.False(UserPresence.IsOnline("offline", "offline", "online"));
    }

    [Fact]
    public void WebActiveFriendShowsActiveInThePopup()
    {
        var friend = new FriendItem(new VrcUser
        {
            Id = "usr_web_active",
            DisplayName = "Web Active",
            Status = "join me",
            Location = "offline",
            State = "active",
            Platform = "web"
        });
        var popup = new FriendDetailViewModel(friend, new VrcApiClient(), ImageLoader.Shared);

        Assert.False(popup.IsOnline);
        Assert.True(popup.IsActive);
        Assert.Equal("Active (Join Me)", popup.StatusLabel);
        Assert.Same(UserPresence.JoinMeBrush, popup.StatusBrush);
        Assert.NotSame(UserPresence.OnlineBrush, popup.StatusDotBrush);
    }

    [Fact]
    public void ProfileWithAnEmptyActivityTimestampStillExposesItsAvatar()
    {
        const string json = "{\"id\":\"usr_real\",\"displayName\":\"Real User\",\"last_activity\":\"\",\"last_login\":\"not-a-date\",\"userIcon\":\"https://api.vrchat.cloud/api/1/image/file_icon/1/256\",\"currentAvatarThumbnailImageUrl\":\"https://api.vrchat.cloud/api/1/image/file_avatar/1/256\"}";

        var user = JsonSerializer.Deserialize<VrcUser>(json);

        Assert.NotNull(user);
        Assert.Equal("Real User", user.DisplayName);
        Assert.Null(user.LastActivity);
        Assert.Null(user.LastLogin);
        Assert.Contains("file_icon", user.UserIcon);
        Assert.Contains("file_avatar", user.CurrentAvatarThumbnailImageUrl);
    }
}