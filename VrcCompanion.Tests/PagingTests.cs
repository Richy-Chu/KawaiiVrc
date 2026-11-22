using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

[Collection(Seams.Collection)]
[ResetSeams]
public class PagingTests
{
    [Fact]
    public void PagedListSplitsSixtyRowsIntoThreePages()
    {
        var page = new PagedList<int>();
        page.SetSource(Enumerable.Range(0, 60));

        Assert.Equal(24, page.Items.Count);
        Assert.Equal(0, page.Items[0]);
        Assert.Equal("1 of 3", page.PageText);
        Assert.True(page.HasPrevious == false);
        Assert.True(page.HasNext);

        page.NextCommand.Execute(null);
        Assert.Equal(24, page.Items[0]);
        Assert.Equal(24, page.Items.Count);
        Assert.Equal("2 of 3", page.PageText);
        Assert.True(page.HasPrevious);

        page.NextCommand.Execute(null);
        Assert.Equal(48, page.Items[0]);
        Assert.Equal(12, page.Items.Count);
        Assert.Equal("3 of 3", page.PageText);
        Assert.False(page.HasNext);

        page.PreviousCommand.Execute(null);
        Assert.Equal(24, page.Items[0]);
    }

    [Fact]
    public void PagedListStartsOverOnANewSourceAndClampsWhenItShrinks()
    {
        var page = new PagedList<int>();
        page.SetSource(Enumerable.Range(0, 60));
        page.NextCommand.Execute(null);
        page.NextCommand.Execute(null);
        Assert.Equal(48, page.Items[0]);

        // A presence refresh keeps the page being read.
        page.SetSource(Enumerable.Range(0, 60));
        Assert.Equal(48, page.Items[0]);

        // Filtering starts over.
        page.SetSource(Enumerable.Range(0, 60), resetPage: true);
        Assert.Equal(0, page.Items[0]);

        // A shorter list cannot leave the user on a page that no longer exists.
        page.SetSource(Enumerable.Range(0, 10));
        Assert.Equal(10, page.Items.Count);
        Assert.Equal("1 of 1", page.PageText);
        Assert.False(page.HasPages);
    }

    [Fact]
    public void FriendsTabHandsTheListOnlyOnePageAtATime()
    {
        RunOnSta(() =>
        {
            using var shell = new MainViewModel();
            for (var index = 0; index < 30; index++)
            {
                shell.Friends.Add(new FriendItem(new VrcUser
                {
                    Id = $"usr_page{index}",
                    DisplayName = $"Page Friend {index}",
                    State = "offline",
                    Status = "offline",
                    Location = "offline"
                }));
            }

            // Any friend filter re-publishes the view.
            shell.ShowOnlineOnly = true;
            shell.ShowOnlineOnly = false;

            Assert.Equal(24, shell.FriendsPage.Items.Count);
            Assert.True(shell.FriendsPage.HasNext);

            shell.FriendsPage.NextCommand.Execute(null);
            Assert.Equal(6, shell.FriendsPage.Items.Count);
            Assert.False(shell.FriendsPage.HasNext);
        });
    }

    [Fact]
    public void NotificationTabHandsTheListOnlyOnePageAtATime()
    {
        using var shell = new MainViewModel();
        var notifications = Enumerable.Range(0, 30)
            .Select(index => new VrcNotification
            {
                Id = $"not_page{index}",
                Type = "friendRequest",
                SenderUserId = $"usr_page{index}",
                SenderUsername = $"page{index}",
                SenderDisplayName = $"Page Person {index}",
                Seen = false,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-index)
            })
            .ToList();

        TestHooks.UpdateNotifications(shell, notifications);

        Assert.Equal(24, shell.NotificationsPage.Items.Count);
        Assert.True(shell.NotificationsPage.HasNext);

        shell.NotificationsPage.NextCommand.Execute(null);
        Assert.Equal(6, shell.NotificationsPage.Items.Count);
        Assert.Equal("2 of 2", shell.NotificationsPage.PageText);
    }

    [Fact]
    public void EveryLongLocalListIsPaged()
    {
        using var api = new VrcApiClient();
        var continuity = new ContinuityViewModel();
        var context = new ContextViewModel();
        var instance = new InstanceViewModel(api, ImageLoader.Shared);
        var recent = new RecentPlayersViewModel(api, new InstanceHistoryStore());

        Assert.Equal(24, continuity.SessionsPage.PageSize);
        Assert.Equal(24, continuity.PeoplePage.PageSize);
        Assert.Equal(24, continuity.WorldsPage.PageSize);
        Assert.Equal(24, context.PlayersPage.PageSize);
        Assert.Equal(24, context.FriendsHerePage.PageSize);
        Assert.Equal(24, context.RecentPeoplePage.PageSize);
        Assert.Equal(24, instance.PlayersPage.PageSize);
        Assert.Equal(24, recent.PlayersPage.PageSize);
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("Paging test did not finish.");
        }

        failure?.Throw();
    }
}
