using System;
using System.Collections.Generic;
using System.Text.Json;
using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class VrChatRealtimeRouterTests
{
    private const string FriendId = "usr_bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string OldLocation = "wrld_old:1~region(us)";
    private const string NewLocation = "wrld_new:2~region(jp)";

    [Fact]
    public void CurrentUserLocationUpdatesEvenWhenTheUserIsNotAFriend()
    {
        const string currentUserId = "usr_aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string location = "wrld_current:9~region(eu)";
        var result = VrChatRealtimeRouter.Route(
            Event(
                "user-location",
                $$"""
                {
                  "userId": "{{currentUserId}}",
                  "location": "{{location}}",
                  "travelingToLocation": ""
                }
                """),
            Friends());

        Assert.Equal(VrChatRealtimeRouteAction.UpdateCurrentUserLocation, result.Action);
        Assert.Equal(currentUserId, result.UserId);
        Assert.Equal(location, result.Location);
    }

    [Fact]
    public void FriendLocationUsesTopLevelPresenceInsteadOfStaleEmbeddedOffline()
    {
        var current = Friend();
        var result = VrChatRealtimeRouter.Route(
            Event(
                "friend-location",
                $$"""
                {
                  "userId": "{{FriendId}}",
                  "location": "{{NewLocation}}",
                  "status": "join me",
                  "platform": "standalonewindows",
                  "user": {
                    "id": "{{FriendId}}",
                    "displayName": "New Name",
                    "location": "{{OldLocation}}",
                    "status": "offline",
                    "state": "offline",
                    "userIcon": "https://example.test/new.png"
                  }
                }
                """),
            Friends(current));

        Assert.Equal(VrChatRealtimeRouteAction.UpdateFriend, result.Action);
        Assert.Equal(FriendId, result.UserId);
        Assert.Equal(NewLocation, result.Friend!.Location);
        Assert.Equal("join me", result.Friend.Status);
        Assert.Equal("online", result.Friend.State);
        Assert.Equal("New Name", result.Friend.DisplayName);
        Assert.Equal("https://example.test/new.png", result.Friend.UserIcon);
        Assert.True(result.Friend.IsFriend);
        Assert.Equal(OldLocation, current.Location);
        Assert.Equal("active", current.Status);
    }

    [Fact]
    public void FriendOnlineUsesEmbeddedPresenceWhenThereIsNoTopLevelLocation()
    {
        var result = VrChatRealtimeRouter.Route(
            Event(
                "friend-online",
                $$"""
                {
                  "userId": "{{FriendId}}",
                  "user": {
                    "id": "{{FriendId}}",
                    "displayName": "Online Friend",
                    "location": "wrld_online:3~region(us)",
                    "status": "active",
                    "state": "online"
                  }
                }
                """),
            Friends(Friend()));

        Assert.Equal(VrChatRealtimeRouteAction.UpdateFriend, result.Action);
        Assert.Equal("wrld_online:3~region(us)", result.Friend!.Location);
        Assert.Equal("active", result.Friend.Status);
        Assert.Equal("online", result.Friend.State);
    }

    [Fact]
    public void FriendOnlineAfterOfflineClearsTheStaleOfflineStatus()
    {
        var current = Friend();
        current.Location = "offline";
        current.Status = "offline";
        current.State = "offline";

        var result = VrChatRealtimeRouter.Route(
            Event(
                "friend-online",
                $$"""
                {
                  "userId": "{{FriendId}}",
                  "location": "{{NewLocation}}",
                  "platform": "standalonewindows"
                }
                """),
            Friends(current));

        Assert.Equal(VrChatRealtimeRouteAction.UpdateFriend, result.Action);
        Assert.Equal(NewLocation, result.Friend!.Location);
        Assert.Equal("online", result.Friend.State);
        Assert.Equal("active", result.Friend.Status);
    }

    [Fact]
    public void FriendOfflineForcesOfflineEvenWhenEmbeddedPresenceIsStale()
    {
        var result = VrChatRealtimeRouter.Route(
            Event(
                "friend-offline",
                $$"""
                {
                  "userId": "{{FriendId}}",
                  "user": {
                    "id": "{{FriendId}}",
                    "location": "{{OldLocation}}",
                    "status": "join me",
                    "state": "online"
                  }
                }
                """),
            Friends(Friend()));

        Assert.Equal(VrChatRealtimeRouteAction.UpdateFriend, result.Action);
        Assert.Equal("offline", result.Friend!.Location);
        Assert.Equal("offline", result.Friend.Status);
        Assert.Equal("offline", result.Friend.State);
    }

    [Fact]
    public void TravelingLocationStaysTravelingInsteadOfUsingTheStaleEmbeddedWorld()
    {
        var result = VrChatRealtimeRouter.Route(
            Event(
                "friend-location",
                $$"""
                {
                  "userId": "{{FriendId}}",
                  "location": "traveling",
                  "travelingToLocation": "wrld_destination:7~region(eu)",
                  "user": {
                    "id": "{{FriendId}}",
                    "location": "{{OldLocation}}",
                    "status": "offline",
                    "state": "offline"
                  }
                }
                """),
            Friends(Friend()));

        Assert.Equal(VrChatRealtimeRouteAction.UpdateFriend, result.Action);
        Assert.Equal("traveling", result.Friend!.Location);
        Assert.Equal("active", result.Friend.Status);
        Assert.Equal("online", result.Friend.State);
    }

    [Fact]
    public void FriendUpdateAppliesEmbeddedPresenceAndProfileFields()
    {
        var result = VrChatRealtimeRouter.Route(
            Event(
                "friend-update",
                $$"""
                {
                  "userId": "{{FriendId}}",
                  "user": {
                    "id": "{{FriendId}}",
                    "displayName": "Renamed Friend",
                    "location": "{{NewLocation}}",
                    "status": "ask me",
                    "state": "online"
                  }
                }
                """),
            Friends(Friend()));

        Assert.Equal(VrChatRealtimeRouteAction.UpdateFriend, result.Action);
        Assert.Equal(NewLocation, result.Friend!.Location);
        Assert.Equal("ask me", result.Friend.Status);
        Assert.Equal("Renamed Friend", result.Friend.DisplayName);
    }

    [Fact]
    public void FriendUpdateAcceptsFlatAvatarFieldsWhenTheUserObjectIsAbsent()
    {
        var result = VrChatRealtimeRouter.Route(
            Event(
                "friend-update",
                $$"""
                {
                  "userId": "{{FriendId}}",
                  "currentAvatar": "avtr_flat_new",
                  "currentAvatarThumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_flat/1/256"
                }
                """),
            Friends(Friend()));

        Assert.Equal(VrChatRealtimeRouteAction.UpdateFriend, result.Action);
        Assert.Equal("avtr_flat_new", result.Friend!.CurrentAvatar);
        Assert.Contains("file_flat", result.Friend.CurrentAvatarThumbnailImageUrl);
    }

    [Fact]
    public void FriendUpdateAcceptsFlatProfileAvatarImageFields()
    {
        var result = VrChatRealtimeRouter.Route(
            Event(
                "friend-update",
                $$"""
                {
                  "userId": "{{FriendId}}",
                  "iconUrl": "https://example.test/avatar-new.png",
                  "bannerType": "avatarBanner",
                  "bannerUrl": "https://example.test/avatar-banner.png"
                }
                """),
            Friends(Friend()));

        Assert.Equal(VrChatRealtimeRouteAction.UpdateFriend, result.Action);
        Assert.Equal("https://example.test/avatar-new.png", result.Friend!.IconUrl);
        Assert.Equal("avatarBanner", result.Friend.BannerType);
        Assert.Equal("https://example.test/avatar-banner.png", result.Friend.BannerUrl);
    }

    [Fact]
    public void UnknownFriendOnlineCreatesTheFriendImmediately()
    {
        var result = VrChatRealtimeRouter.Route(
            Event(
                "friend-online",
                $$"""
                {
                  "userId": "{{FriendId}}",
                  "user": {
                    "id": "{{FriendId}}",
                    "displayName": "Just Came Online",
                    "location": "{{NewLocation}}",
                    "status": "active",
                    "state": "online"
                  }
                }
                """),
            Friends());

        Assert.Equal(VrChatRealtimeRouteAction.UpdateFriend, result.Action);
        Assert.Equal(FriendId, result.Friend!.Id);
        Assert.Equal("Just Came Online", result.Friend.DisplayName);
        Assert.Equal(NewLocation, result.Friend.Location);
        Assert.True(result.Friend.IsFriend);
    }

    [Fact]
    public void FriendAddCreatesTheFriendWithoutWaitingForTheList()
    {
        var result = VrChatRealtimeRouter.Route(
            Event(
                "friend-add",
                $$"""
                {
                  "userId": "{{FriendId}}",
                  "user": {
                    "id": "{{FriendId}}",
                    "displayName": "New Friend",
                    "location": "{{OldLocation}}",
                    "status": "active",
                    "state": "online"
                  }
                }
                """),
            Friends());

        Assert.Equal(VrChatRealtimeRouteAction.AddFriend, result.Action);
        Assert.Equal(FriendId, result.UserId);
        Assert.Equal("New Friend", result.Friend!.DisplayName);
        Assert.Equal(OldLocation, result.Friend.Location);
        Assert.True(result.Friend.IsFriend);
    }

    [Fact]
    public void UnknownFriendOfflineDoesNothing()
    {
        var result = VrChatRealtimeRouter.Route(
            Event("friend-offline", $$"""{"userId":"{{FriendId}}"}"""),
            Friends());

        Assert.Equal(VrChatRealtimeRouteAction.None, result.Action);
    }

    [Fact]
    public void KnownFriendDeleteRemovesAndUnknownDeleteDoesNothing()
    {
        var known = VrChatRealtimeRouter.Route(
            Event("friend-delete", $$"""{"userId":"{{FriendId}}"}"""),
            Friends(Friend()));
        var unknown = VrChatRealtimeRouter.Route(
            Event("friend-delete", "{\"userId\":\"usr_missing\"}"),
            Friends());

        Assert.Equal(VrChatRealtimeRouteAction.RemoveFriend, known.Action);
        Assert.Equal(FriendId, known.UserId);
        Assert.Equal(VrChatRealtimeRouteAction.None, unknown.Action);
    }

    [Fact]
    public void NotificationEventsRequestANotificationRefresh()
    {
        var result = VrChatRealtimeRouter.Route(
            Event("notification-v2", "{\"id\":\"not_1\",\"type\":\"friendRequest\"}"),
            Friends());

        Assert.Equal(VrChatRealtimeRouteAction.RefreshNotifications, result.Action);
    }

    [Fact]
    public void UnhandledEventsAreIgnored()
    {
        var result = VrChatRealtimeRouter.Route(
            Event("instance-queue-position", "{\"position\":3}"),
            Friends(Friend()));

        Assert.Equal(VrChatRealtimeRouteAction.None, result.Action);
    }

    private static VrChatRealtimeEvent Event(string type, string content)
    {
        var raw = $$"""{"type":"{{type}}","content":{{content}}}""";
        Assert.True(VrChatRealtimeMessage.TryParse(raw, DateTimeOffset.Now, out var parsed));
        return parsed!;
    }

    private static VrcUser Friend()
        => new()
        {
            Id = FriendId,
            DisplayName = "Friend",
            Location = OldLocation,
            Status = "active",
            State = "online",
            IsFriend = true
        };

    private static Dictionary<string, VrcUser> Friends(params VrcUser[] users)
    {
        var result = new Dictionary<string, VrcUser>(StringComparer.Ordinal);
        foreach (var user in users)
        {
            result[user.Id] = user;
        }
        return result;
    }
}
