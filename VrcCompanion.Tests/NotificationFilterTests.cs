using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

[Collection(Seams.Collection)]
[ResetSeams]
public class NotificationFilterTests
{
    [Fact]
    public void FiltersSplitFriendRequestsWorldInvitesAndInviteRequests()
    {
        using var shell = new MainViewModel();
        var friendRequest = Item("friend-request", "friendRequest");
        var worldInvite = Item("world-invite", "invite");
        var inviteRequest = Item("invite-request", "requestInvite");
        var group = Item("group", "group.announcement");
        var boop = Item("boop", "boop");
        shell.Notifications.Add(friendRequest);
        shell.Notifications.Add(worldInvite);
        shell.Notifications.Add(inviteRequest);
        shell.Notifications.Add(group);
        shell.Notifications.Add(boop);

        Assert.Equal(5, Visible(shell).Count());

        shell.SelectedNotificationFilter = Filter(shell, "friend-requests");
        Assert.Equal(new[] { friendRequest }, Visible(shell));

        shell.SelectedNotificationFilter = Filter(shell, "world-invites");
        Assert.Equal(new[] { worldInvite }, Visible(shell));

        shell.SelectedNotificationFilter = Filter(shell, "invite-requests");
        Assert.Equal(new[] { inviteRequest }, Visible(shell));

        shell.SelectedNotificationFilter = Filter(shell, "groups");
        Assert.Equal(new[] { group }, Visible(shell));

        shell.SelectedNotificationFilter = Filter(shell, "boops");
        Assert.Equal(new[] { boop }, Visible(shell));

        shell.SelectedNotificationFilter = Filter(shell, "other");
        Assert.Empty(Visible(shell));

        worldInvite.MarkSeen();
        shell.SelectedNotificationFilter = Filter(shell, "unread");
        Assert.DoesNotContain(worldInvite, Visible(shell));
        Assert.Equal(4, Visible(shell).Count());
    }

    [Fact]
    public void InvitesRequestsAndBoopsSurviveAnEmptyRefresh()
    {
        using var shell = new MainViewModel();
        var friendRequest = Model("friend-request", "friendRequest");
        var worldInvite = Model("world-invite", "invite");
        var inviteRequest = Model("invite-request", "requestInvite");
        var boop = Model("boop", "boop");
        TestHooks.UpdateNotifications(shell, new[] { friendRequest, worldInvite, inviteRequest, boop });

        TestHooks.UpdateNotifications(shell, Array.Empty<VrcNotification>());

        Assert.Equal(4, shell.Notifications.Count);
        Assert.Contains(shell.Notifications, notification => notification.Id == "friend-request");
        Assert.Contains(shell.Notifications, notification => notification.Id == "world-invite");
        Assert.Contains(shell.Notifications, notification => notification.Id == "invite-request");
        Assert.Contains(shell.Notifications, notification => notification.Id == "boop");
    }

    [Fact]
    public void AcceptedFriendRequestStaysGone()
    {
        using var shell = new MainViewModel();
        var model = new VrcNotification
        {
            Id = "friend-request",
            Type = "friendRequest",
            SenderUserId = "usr_test",
            SenderUsername = "Tester",
            Details = EmptyDetails()
        };
        TestHooks.UpdateNotifications(shell, new[] { model });
        var item = shell.Notifications.Single();

        TestHooks.ResolveFriendRequest(shell, item);
        TestHooks.UpdateNotifications(shell, new[] { model });

        Assert.Empty(shell.Notifications);
    }

    private static IEnumerable<NotificationItem> Visible(MainViewModel shell)
        => shell.NotificationsView.Cast<NotificationItem>();

    private static FilterOption Filter(MainViewModel shell, string key)
        => shell.NotificationFilters.Single(filter => filter.Key == key);

    private static NotificationItem Item(string id, string type)
        => new(Model(id, type));

    private static VrcNotification Model(string id, string type)
        => new()
        {
            Id = id,
            Type = type,
            SenderUsername = "Tester",
            Details = EmptyDetails()
        };

    private static JsonElement EmptyDetails()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}