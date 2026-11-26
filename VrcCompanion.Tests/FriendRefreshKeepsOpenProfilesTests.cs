using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The friend list is bound to FriendsPage.Items with a two-way SelectedItem. Refreshing
/// that page used to clear the rows, which cleared the selection, which cleared the open
/// profile - the popup closed itself while the user was reading it. These run the same
/// snapshot merge the online poll runs, and a filter change, with a profile open.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class FriendRefreshKeepsOpenProfilesTests
{
    private const string FriendId = "usr_tree";
    private const string RecentId = "usr_recent";

    [Fact]
    public void AFriendPollKeepsTheFriendsOpenProfileAndItsRowSelected()
    {
        WpfTestHost.Run(() =>
        {
            using var handler = new OfflineHandler();
            using var api = new VrcApiClient(null, handler);
            using var shell = new MainViewModel(api);
            TestHooks.SetCurrentUser(shell, new VrcUser { Id = "usr_me", DisplayName = "Me" });
            TestHooks.SetLoggedIn(shell);

            var friend = OnlineFriend("Tree Bear");
            shell.Friends.Add(friend);
            shell.FriendsPage.SetSource(new[] { friend }, resetPage: true);

            var window = CreateWindow(shell);
            window.Show();
            try
            {
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "friends");
                Pump(window);

                // Select the row the way a click does: through the real ListBox.
                var list = FindVisualChildren<ListBox>(window)
                    .Single(box => ReferenceEquals(box.ItemsSource, shell.FriendsPage.Items));
                list.SelectedItem = friend;
                Settle.Until(
                    () => shell.SelectedFriendDetail is { } detail && detail.Id == FriendId,
                    () => Pump(window),
                    "the friend profile never opened");

                // The same snapshot the poll sees every tick.
                shell.RunFriendPollForTests(new[] { User(FriendId, "Tree Bear", online: true) });
                Pump(window);
                Assert.True(shell.HasSelectedFriend, "A unchanged friend poll closed the open profile.");
                Assert.Equal(FriendId, shell.SelectedFriendDetail!.Id);
                Assert.Same(friend, shell.SelectedFriend);

                // And a snapshot that changes the row (the friend goes offline, so the
                // list re-sorts) must keep it open as well.
                shell.RunFriendPollForTests(new[] { User(FriendId, "Tree Bear", online: false) });
                Pump(window);
                Assert.True(shell.HasSelectedFriend, "A presence change closed the open profile.");
                Assert.Equal(FriendId, shell.SelectedFriendDetail!.Id);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ARecentPlayersProfileSurvivesAFriendPoll()
    {
        WpfTestHost.Run(() =>
        {
            using var handler = new OfflineHandler();
            using var api = new VrcApiClient(null, handler);
            using var shell = new MainViewModel(api);
            TestHooks.SetCurrentUser(shell, new VrcUser { Id = "usr_me", DisplayName = "Me" });
            TestHooks.SetLoggedIn(shell);

            var friend = OnlineFriend("Tree Bear");
            shell.Friends.Add(friend);
            shell.FriendsPage.SetSource(new[] { friend }, resetPage: true);

            var window = CreateWindow(shell);
            window.Show();
            try
            {
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "friends");
                Pump(window);

                shell.OpenUserProfile(RecentId, "Recent Player");
                Settle.Until(
                    () => shell.SelectedFriendDetail is { } detail && detail.Id == RecentId,
                    () => Pump(window),
                    "the recent player profile never opened");

                shell.RunFriendPollForTests(new[] { User(FriendId, "Tree Bear", online: true) });
                Pump(window);

                Assert.True(shell.HasSelectedFriend, "A friend poll closed a recent player's profile.");
                Assert.Equal(RecentId, shell.SelectedFriendDetail!.Id);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ChangingTheFriendFilterKeepsTheOpenProfile()
    {
        WpfTestHost.Run(() =>
        {
            using var handler = new OfflineHandler();
            using var api = new VrcApiClient(null, handler);
            using var shell = new MainViewModel(api);
            TestHooks.SetCurrentUser(shell, new VrcUser { Id = "usr_me", DisplayName = "Me" });
            TestHooks.SetLoggedIn(shell);

            var friend = OnlineFriend("Tree Bear");
            shell.Friends.Add(friend);
            shell.FriendsPage.SetSource(new[] { friend }, resetPage: true);

            var window = CreateWindow(shell);
            window.Show();
            try
            {
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "friends");
                Pump(window);

                shell.SelectedFriend = friend;
                Settle.Until(
                    () => shell.SelectedFriendDetail is { } detail && detail.Id == FriendId,
                    () => Pump(window),
                    "the friend profile never opened");

                var otherFilter = shell.FriendFilters.First(filter => !ReferenceEquals(filter, shell.SelectedFriendFilter));
                shell.SelectedFriendFilter = otherFilter;
                Pump(window);

                Assert.True(shell.HasSelectedFriend, "Changing the friend filter closed the open profile.");
                Assert.Equal(FriendId, shell.SelectedFriendDetail!.Id);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RepublishingTheSamePageDoesNotClearItsRows()
    {
        var page = new PagedList<string>();
        page.SetSource(new[] { "a", "b" });

        var changes = 0;
        page.Items.CollectionChanged += (_, _) => changes++;
        page.SetSource(new[] { "a", "b" });

        Assert.Equal(0, changes);
        Assert.Equal(new[] { "a", "b" }, page.Items);

        page.SetSource(new[] { "a", "c" });
        Assert.True(changes > 0, "A changed page must still publish its rows.");
        Assert.Equal(new[] { "a", "c" }, page.Items);
    }

    private static FriendItem OnlineFriend(string name)
        => new(User(FriendId, name, online: true))
        {
            IsFavorite = false
        };

    private static VrcUser User(string id, string name, bool online) => new()
    {
        Id = id,
        DisplayName = name,
        State = online ? "online" : "offline",
        Location = online ? "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345~region(us)" : null
    };

    private static MainWindow CreateWindow(MainViewModel shell)
        => new(shell, initializeOnLoad: false)
        {
            Width = 1100,
            Height = 720,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 100,
            Top = 100
        };

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"error":{"message":"offline"}}""")
            });
    }
}
