using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// A StaticResource that only exists inside another overlay's Resources parses fine
/// until the first real row is measured, and then the whole window dies with
/// XamlParseException. Empty lists never create a row, so these tests put one real
/// row into each popup list and make the window lay it out.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class PopupRowRenderTests
{
    private const string WorldId = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";

    [Fact]
    public void AFriendChangesRowRealisesItsProfileButton()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);

            var friend = new FriendItem(new VrcUser
            {
                Id = "usr_added",
                DisplayName = "New Friend",
                Username = "newfriend",
                State = "offline"
            });
            var change = new FriendChangeItem(friend, isAdded: true, DateTimeOffset.Now);
            shell.FriendChanges.Add(change);

            var window = CreateWindow(shell);
            window.Show();
            try
            {
                shell.OpenFriendChangesCommand.Execute(null);
                Pump(window);

                var row = FindVisualChildren<Button>(window)
                    .FirstOrDefault(button =>
                        ReferenceEquals(button.Command, shell.OpenFriendChangeCommand) &&
                        ReferenceEquals(button.CommandParameter, change));
                Assert.True(row is not null, "The friend change row never realised its whole-row button.");
                Assert.True(row!.IsVisible, "The friend change row is not on screen.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ARecentPlayersRowRealisesItsProfileButton()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);

            var history = new InstanceHistoryStore(
                Path.Combine(Path.GetTempPath(), "vrc-popup-rows-" + Guid.NewGuid().ToString("N") + ".db"));
            var recent = new RecentPlayersViewModel(IsolatedApi(), history);
            TestHooks.AttachRecentPlayersViewModel(shell, recent);

            var window = CreateWindow(shell);
            window.Show();
            try
            {
                recent.Show("usr_me", Array.Empty<string>());
                Settle.Until(() => !recent.IsLoading, Settle.PumpBackground, "recent players never finished loading");

                var now = DateTimeOffset.Now;
                var session = new RecentPlayerSessionItem(
                    "Popcorn Palace",
                    WorldId,
                    $"{WorldId}:12345~region(us)",
                    string.Empty,
                    now.AddHours(-1),
                    null,
                    now,
                    TimeSpan.FromHours(1),
                    _ => { });
                var player = new RecentPlayerItem(
                    "usr_recent",
                    "Tree Bear",
                    string.Empty,
                    isFriend: false,
                    new[] { session },
                    _ => { });
                recent.Players.Add(player);
                recent.PlayersPage.SetSource(recent.Players, resetPage: true);
                Pump(window);

                var row = FindVisualChildren<Button>(window)
                    .FirstOrDefault(button => ReferenceEquals(button.Command, player.OpenProfileCommand));
                Assert.True(row is not null, "The recent player row never realised its Profile button.");
                Assert.True(row!.IsVisible, "The recent player row is not on screen.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ANotificationRowRealisesItsClearButton()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);

            using var details = System.Text.Json.JsonDocument.Parse("""{"worldName":"The Black Cat"}""");
            var notification = new NotificationItem(new VrcNotification
            {
                Id = "not_row",
                Type = "invite",
                SenderUserId = "usr_sender",
                SenderUsername = "Sender",
                Details = details.RootElement.Clone()
            });
            shell.NotificationsPage.SetSource(new[] { notification }, resetPage: true);

            var window = CreateWindow(shell);
            window.Show();
            try
            {
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "notifications");
                Pump(window);

                var row = FindVisualChildren<Button>(window)
                    .FirstOrDefault(button =>
                        ReferenceEquals(button.Command, shell.ClearNotificationCommand) &&
                        ReferenceEquals(button.CommandParameter, notification));
                Assert.True(row is not null, "The notification row never realised its Clear button.");
                Assert.True(row!.IsVisible, "The notification row is not on screen.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AnInvitePastTheReplyWindowShowsItsMarkerInTheList()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);

            var expired = new NotificationItem(new VrcNotification
            {
                Id = "not_expired_row",
                Type = "invite",
                SenderUserId = "usr_sender",
                SenderUsername = "Sender"
            });
            expired.MarkPastReplyWindow(true);
            var fresh = new NotificationItem(new VrcNotification
            {
                Id = "not_fresh_row",
                Type = "invite",
                SenderUserId = "usr_sender",
                SenderUsername = "Sender"
            });
            shell.NotificationsPage.SetSource(new[] { expired, fresh }, resetPage: true);

            var window = CreateWindow(shell);
            window.Show();
            try
            {
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "notifications");
                Pump(window);

                // Exactly the expired row carries the marker; the newest invite does not.
                var markers = FindVisualChildren<TextBlock>(window)
                    .Where(text => text.Text == "REPLY WINDOW CLOSED" && text.IsVisible)
                    .ToList();
                Assert.Single(markers);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AGroupInstanceRowRealisesItsJoinButton()
    {
        WpfTestHost.Run(() =>
        {
            using var handler = new GroupHandler();
            using var api = new VrcApiClient(null, handler);
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            var groups = new GroupViewModel(api);
            TestHooks.AttachGroupViewModel(shell, groups);

            var window = CreateWindow(shell);
            window.Show();
            try
            {
                groups.OpenGroupCommand.Execute(new Group
                {
                    Id = "grp_row",
                    Name = "Test Group",
                    MembershipStatus = "member"
                });
                Settle.Until(
                    () => groups.SelectedGroup is not null,
                    Settle.PumpBackground,
                    "the group was never selected");

                groups.OpenSelectedGroupInfoCommand.Execute(null);
                Settle.Until(
                    () => groups.IsGroupInfoOpen && groups.GroupInfoInstances.Count > 0,
                    Settle.PumpBackground,
                    "the group info instances never arrived");
                Pump(window);

                var row = FindVisualChildren<Button>(window)
                    .FirstOrDefault(button =>
                        ReferenceEquals(button.Command, groups.JoinGroupInstanceCommand) &&
                        button.DataContext is GroupInstance);
                Assert.True(row is not null, "The group instance row never realised its Join button.");
                Assert.True(row!.IsVisible, "The group instance row is not on screen.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ASavedAccountRowRealisesItsSwitchButton()
    {
        WpfTestHost.Run(() =>
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "vrc-popup-rows-accounts-" + Guid.NewGuid().ToString("N"));
            try
            {
                var accounts = new MultiAccountService(directory);
                accounts.RegisterAccount("usr_saved", "cookie-saved", "Richy", "https://example.test/richy.png");
                accounts.RegisterAccount("usr_other", "cookie-other", "Other", string.Empty);
                accounts.SwitchToAccount("usr_other");

                using var api = new VrcApiClient();
                TestHooks.SetCurrentUser(api, new VrcUser
                {
                    Id = "usr_saved",
                    DisplayName = "Richy",
                    Username = "Richy",
                    UserIcon = "https://example.test/richy.png"
                });
                using var shell = new MainViewModel(api);
                TestHooks.SetCurrentUser(shell, new VrcUser
                {
                    Id = "usr_saved",
                    DisplayName = "Richy",
                    Username = "Richy",
                    UserIcon = "https://example.test/richy.png"
                });
                var vm = new MultiAccountViewModel(api, new MultiAccountService(directory));
                TestHooks.AttachMultiAccountViewModel(shell, vm);
                TestHooks.SetLoggedIn(shell);

                var window = CreateWindow(shell);
                window.Show();
                try
                {
                    shell.SelectedSection = shell.Sections.Single(section => section.Key == "accounts");
                    Pump(window);

                    // The signed-in account shows no Switch button, so the other saved
                    // row is the one that has to realise it.
                    var row = FindVisualChildren<Button>(window)
                        .FirstOrDefault(button =>
                            ReferenceEquals(button.Command, vm.SwitchAccountCommand) &&
                            Equals(button.CommandParameter, "usr_saved"));
                    Assert.True(row is not null, "The saved account row never realised its Switch button.");
                    Assert.True(row!.IsVisible, "The saved account row is not on screen.");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // A leftover scratch folder is not what this case is about.
                }
            }
        });
    }

    private static VrcApiClient IsolatedApi()
        => new(new SessionStore(Path.Combine(
            Path.GetTempPath(),
            "vrc-popup-rows-" + Guid.NewGuid().ToString("N"))));

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

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private sealed class GroupHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/groups/grp_row/instances", StringComparison.Ordinal))
            {
                return await Json("""
                    [
                      {
                        "instanceId": "77777~group(grp_row)~groupAccessType(public)~region(us)",
                        "location": "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:77777~group(grp_row)~groupAccessType(public)~region(us)",
                        "memberCount": 4,
                        "world": { "name": "Popcorn Palace", "thumbnailImageUrl": "" }
                      }
                    ]
                    """);
            }

            if (path.Contains("/groups/grp_row", StringComparison.Ordinal))
            {
                return await Json("""
                    {
                      "id": "grp_row",
                      "name": "Test Group",
                      "membershipStatus": "member",
                      "myMember": { "permissions": ["group-instance-public-create"] }
                    }
                    """);
            }

            return await Json("{}", HttpStatusCode.NotFound);
        }

        private static async Task<HttpResponseMessage> Json(
            string json,
            HttpStatusCode status = HttpStatusCode.OK)
        {
            await Task.Yield();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
