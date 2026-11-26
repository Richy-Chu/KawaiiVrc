using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// A profile can be opened for someone who is not in the friend list (a recent player, a
/// group member, a name from history). The friend poll runs every 10-30 seconds; it used
/// to look the open profile up in the friend rows, fail to find it and close the popup,
/// so a profile could vanish moments after opening.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class ProfilePopupRefreshTests
{
    [Fact]
    public void AFriendRefreshDoesNotCloseAProfileForSomeoneWhoIsNotAFriend()
    {
        WpfTestHost.Run(() =>
        {
            using var api = new VrcApiClient(null, new OfflineHandler());
            using var shell = new MainViewModel(api);
            TestHooks.SetCurrentUser(shell, new VrcUser { Id = "usr_me", DisplayName = "Me" });
            TestHooks.SetLoggedIn(shell);

            shell.OpenUserProfile("usr_stranger", "Stranger");

            Settle.Until(
                () => shell.SelectedFriendDetail is { } detail &&
                      string.Equals(detail.Id, "usr_stranger", StringComparison.Ordinal),
                "the stranger profile never opened");

            shell.RestoreFriendsSelectionForTests();

            Assert.True(shell.HasSelectedFriend, "A friend refresh closed a profile for a non-friend.");
            Assert.Equal("usr_stranger", shell.SelectedFriendDetail!.Id);
            Assert.Null(shell.SelectedFriend);
        });
    }

    [Fact]
    public void AFriendRefreshKeepsTheOpenProfileAndUpdatesItsFriendRow()
    {
        WpfTestHost.Run(() =>
        {
            using var api = new VrcApiClient(null, new OfflineHandler());
            using var shell = new MainViewModel(api);
            TestHooks.SetCurrentUser(shell, new VrcUser { Id = "usr_me", DisplayName = "Me" });
            TestHooks.SetLoggedIn(shell);

            var friend = new FriendItem(new VrcUser
            {
                Id = "usr_friend",
                DisplayName = "Tree Bear",
                Location = "offline"
            });
            shell.Friends.Add(friend);
            shell.SelectedFriend = friend;

            Settle.Until(
                () => shell.SelectedFriendDetail is { } detail &&
                      string.Equals(detail.Id, friend.Id, StringComparison.Ordinal),
                "the friend profile never opened");

            shell.RestoreFriendsSelectionForTests();

            Assert.True(shell.HasSelectedFriend, "A friend refresh closed the friend's open profile.");
            Assert.Same(friend, shell.SelectedFriend);
        });
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
