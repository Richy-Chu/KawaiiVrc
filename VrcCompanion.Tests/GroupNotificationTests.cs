using System;
using System.Linq;
using System.Text.Json;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class GroupNotificationTests
{
    [Fact]
    public void V2GroupPayloadUsesItsGroupIdNameAndAnnouncementTitle()
    {
        using var data = JsonDocument.Parse(
            """
            {
              "groupId": "grp_11111111-1111-1111-1111-111111111111",
              "groupName": "Lastation",
              "announcementTitle": "Needy Puppy's Birthday Game Night!!!"
            }
            """);
        var notification = new VrcNotification
        {
            Id = "not_group",
            Type = "group.announcement",
            Title = "Lastation: Needy Puppy's Birthday Game Night!!!",
            Message = "This event has started!",
            ImageUrl = "https://example.test/lastation.png",
            Data = data.RootElement.Clone()
        };

        var item = new NotificationItem(notification);

        Assert.True(item.IsGroup);
        Assert.Equal("GROUP", item.TypeLabel);
        Assert.Equal("group", item.Category);
        Assert.Equal("grp_11111111-1111-1111-1111-111111111111", item.GroupId);
        Assert.Equal("Lastation", item.GroupName);
        Assert.Equal("Needy Puppy's Birthday Game Night!!!", item.GroupTitle);
        Assert.Equal("This event has started!", item.MessageText);
        Assert.Equal("https://example.test/lastation.png", item.GroupImageUrl);
        Assert.True(item.HasGroupImage);
    }

    [Fact]
    public void GroupIdFallsBackToTheEventLinkAndGroupNameToTheTitlePrefix()
    {
        using var data = JsonDocument.Parse("{}");
        var notification = new VrcNotification
        {
            Id = "not_event",
            Type = "group.event.started",
            Title = "HIGANBANA//ETHOS: Cuddle me instance",
            Link = "event:grp_22222222-2222-2222-2222-222222222222,cal_123",
            Data = data.RootElement.Clone()
        };

        var item = new NotificationItem(notification);

        Assert.Equal("grp_22222222-2222-2222-2222-222222222222", item.GroupId);
        Assert.Equal("HIGANBANA//ETHOS", item.GroupName);
        Assert.Equal("Cuddle me instance", item.GroupTitle);
    }

    [Fact]
    public void GroupIdFallsBackToAGroupSenderId()
    {
        using var data = JsonDocument.Parse("{}");
        var notification = new VrcNotification
        {
            Id = "not_invite",
            Type = "group.invite",
            SenderUserId = "grp_33333333-3333-3333-3333-333333333333",
            Data = data.RootElement.Clone()
        };

        var item = new NotificationItem(notification);

        Assert.True(item.IsGroup);
        Assert.Equal("grp_33333333-3333-3333-3333-333333333333", item.GroupId);
        Assert.Equal("Group", item.GroupName);
    }

    [Fact]
    public void GroupInviteExposesAcceptAndDenyActionsAndItsSender()
    {
        using var data = JsonDocument.Parse(
            """
            {
              "groupId": "grp_55555555-5555-5555-5555-555555555555",
              "groupName": "HeartBendersMC"
            }
            """);
        var notification = new VrcNotification
        {
            Id = "not_group_invite",
            Type = "group.invite",
            SenderUserId = "usr_inviter",
            SenderDisplayName = "Willow0rBunny",
            Message = "Willow0rBunny has shared a group with you",
            Data = data.RootElement.Clone()
        };
        var item = new NotificationItem(notification);

        Assert.True(item.IsGroup);
        Assert.True(item.IsGroupInvite);
        Assert.True(item.CanAcceptGroupInvite);
        Assert.True(item.CanDeclineGroupInvite);
        Assert.False(item.CanDecline);
        Assert.Equal("INVITE", item.TypeLabel);
        Assert.Equal("HeartBendersMC", item.GroupName);
        Assert.Equal("Willow0rBunny has shared a group with you", item.MessageText);

        using var api = new VrcApiClient();
        var detail = new NotificationDetailViewModel(item, api);
        Assert.True(detail.HasSender);
        Assert.Equal("Willow0rBunny", detail.SenderName);
        Assert.Equal("Group invite", detail.NotificationKindText);
        Assert.Equal("invited you to a group", detail.ActionLine);
        Assert.True(detail.CanAcceptGroupInvite);
        Assert.True(detail.CanDeclineGroupInvite);
    }

    [Fact]
    public void GroupInviteSurvivesAnEmptyNotificationRefresh()
    {
        using var data = JsonDocument.Parse(
            """
            {
              "groupId": "grp_66666666-6666-6666-6666-666666666666",
              "groupName": "Keep Me"
            }
            """);
        var notification = new VrcNotification
        {
            Id = "not_group_persist",
            Type = "group.invite",
            Data = data.RootElement.Clone()
        };
        using var shell = new MainViewModel();

        TestHooks.UpdateNotifications(shell, new[] { notification });
        TestHooks.UpdateNotifications(shell, Array.Empty<VrcNotification>());

        var item = Assert.Single(shell.Notifications);
        Assert.True(item.IsGroupInvite);
        Assert.Equal("Keep Me", item.GroupName);
    }

    [Fact]
    public void ResolvedGroupMetadataWinsOverTheRawAnnouncementImage()
    {
        using var data = JsonDocument.Parse(
            """
            {
              "groupId": "grp_44444444-4444-4444-4444-444444444444",
              "groupName": "Raw Name"
            }
            """);
        var notification = new VrcNotification
        {
            Id = "not_resolved",
            Type = "group.announcement",
            Message = "Hello",
            ImageUrl = "https://example.test/announcement.webp",
            Data = data.RootElement.Clone()
        };
        var item = new NotificationItem(notification);

        item.ApplyGroupMetadata("Canonical Name", "https://example.test/group-icon.png");

        Assert.Equal("Canonical Name", item.GroupName);
        Assert.Equal("https://example.test/group-icon.png", item.GroupImageUrl);
    }
}
