using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VrcCompanion.Controls;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// An active instance in world details can be opened to see who is in it: the names and
/// pictures VRChat reports, a Profile button per person and Join. These read the loaded
/// view model and then the realised window.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class WorldInstancePlayersTests
{
    private const string WorldId = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    private const string InstanceId = "12345~region(us)";
    private const string AvatarA = "https://example.test/tree.png";

    [Fact]
    public async Task AnInstanceListsItsPlayersAndOpensTheirProfiles()
    {
        using var handler = new InstanceHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });

        (string Id, string Name, string Avatar)? opened = null;
        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared,
            openUserProfile: (id, name, avatar) => opened = (id, name, avatar));

        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);
        var instance = Assert.Single(dialog.Instances);
        Assert.True(instance.ViewPlayersCommand.CanExecute(null));

        var loading = dialog.ShowInstancePlayersAsync(instance);
        Assert.True(instance.IsLoadingPlayers, "Starting a load must mark the instance as loading.");
        await loading;

        Assert.True(dialog.HasSelectedInstance);
        Assert.Same(instance, dialog.SelectedInstance);
        Assert.False(instance.IsLoadingPlayers);
        Assert.Equal(2, instance.Players.Count);

        var tree = instance.Players.Single(player => player.DisplayName == "Tree Bear");
        Assert.Equal("usr_tree", tree.UserId);
        Assert.Equal(AvatarA, tree.AvatarUrl);
        Assert.True(tree.HasAvatar);
        Assert.True(tree.IsFriend);
        Assert.True(tree.OpenProfileCommand.CanExecute(null));

        tree.OpenProfileCommand.Execute(null);
        Assert.Equal(("usr_tree", "Tree Bear", AvatarA), opened);

        var other = instance.Players.Single(player => player.DisplayName == "Night Owl");
        Assert.False(other.HasAvatar);
        Assert.False(other.IsFriend);

        dialog.CloseInstancePlayersCommand.Execute(null);
        Assert.False(dialog.HasSelectedInstance);
    }

    [Fact]
    public async Task ThePlayersCommandLoadsTheInstance()
    {
        using var handler = new InstanceHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });

        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared);
        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);
        var instance = Assert.Single(dialog.Instances);

        dialog.ShowInstancePlayersCommand.Execute(instance);
        await Settle.UntilAsync(() => instance.PlayersLoaded || instance.HasPlayersError);

        Assert.False(instance.HasPlayersError, instance.PlayersError);
        Assert.Equal(2, instance.Players.Count);
        Assert.Same(instance, dialog.SelectedInstance);
    }

    [Fact]
    public void TheWindowDrawsThePlayersButtonAndThePlayerRows()
    {
        WpfTestHost.Run(() =>
        {
            using var handler = new InstanceHandler();
            using var api = new VrcApiClient(null, handler);
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });
            using var shell = new MainViewModel(api);
            TestHooks.SetLoggedIn(shell);

            var window = CreateWindow(shell);
            window.Show();
            try
            {
                shell.OpenWorldCommand.Execute(new WorldItem(new VrcWorld
                {
                    Id = WorldId,
                    Name = "Popcorn Palace"
                }));
                Settle.Until(
                    () => shell.WorldDialog is { } dialog && dialog.Instances.Count == 1,
                    Settle.PumpBackground,
                    "the world dialog never listed its instance");
                var worldDialog = shell.WorldDialog!;
                Pump(window);

                var overlay = window.FindName("WorldDialogOverlay") as DependencyObject;
                Assert.True(overlay is not null, "The world dialog overlay was not in the window.");

                var players = FindVisualChildren<Button>(overlay!)
                    .Single(button => Equals(button.Content, "Players"));
                Assert.True(players.IsVisible, "The instance row has no visible Players button.");
                Assert.True(players.Command is not null, "The Players button lost its command binding.");
                var rowItem = Assert.IsType<WorldInstanceItem>(players.DataContext);
                Assert.Same(worldDialog.Instances[0], rowItem);
                Assert.Same(rowItem.ViewPlayersCommand, players.Command);
                Assert.False(rowItem.PlayersLoaded, "The instance was already marked loaded before anyone asked.");
                Assert.True(players.Command.CanExecute(players.CommandParameter));
                players.Command.Execute(players.CommandParameter);

                // The row's Players button opens the instance and reads its people.
                Assert.Same(rowItem, worldDialog.SelectedInstance);
                Settle.Until(
                    () => rowItem.PlayersLoaded || rowItem.HasPlayersError,
                    Settle.PumpBackground,
                    $"the instance players never arrived (paths: {string.Join(", ", handler.Paths)})");
                Pump(window);

                Assert.False(rowItem.HasPlayersError, rowItem.PlayersError);
                Assert.Equal(2, rowItem.Players.Count);

                var row = FindVisualChildren<Button>(overlay!)
                    .Single(button =>
                        Equals(button.Content, "Profile") &&
                        button.DataContext is WorldInstancePlayerItem player &&
                        player.UserId == "usr_tree");
                Assert.True(row.IsVisible, "The player row's Profile button is not on screen.");
                Assert.True(row.Command.CanExecute(row.CommandParameter));

                Assert.Contains(
                    FindVisualChildren<TextBlock>(overlay!),
                    text => text.Text == "Tree Bear" && text.IsVisible);
                Assert.Contains(
                    FindVisualChildren<AsyncImage>(overlay!),
                    image => image.SourceUrl == AvatarA && image.IsVisible);
            }
            finally
            {
                shell.CloseWorldDialogCommand.Execute(null);
                window.Close();
            }
        });
    }

    [Fact]
    public async Task AQuietRefreshShowsJoinsAndKeepsTheRosterWhenAReadFails()
    {
        using var handler = new InstanceHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });
        handler.InstanceResponse = call => call switch
        {
            1 => InstanceHandler.JsonNow(InstancePayload("usr_tree", "Tree Bear", AvatarA, trusted: true)),
            2 => InstanceHandler.JsonNow(InstancePayload(
                "usr_tree", "Tree Bear", AvatarA, trusted: true,
                "usr_owl", "Late Owl", string.Empty)),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        };

        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared);
        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);
        var instance = Assert.Single(dialog.Instances);

        await dialog.ShowInstancePlayersAsync(instance);
        Assert.Single(instance.Players);

        // Someone joins while the panel is open: no close, no reopen.
        await dialog.RefreshOpenPlayersAsync();
        Assert.Equal(2, instance.Players.Count);
        Assert.Contains(instance.Players, player => player.DisplayName == "Late Owl");
        Assert.Same(instance, dialog.SelectedInstance);
        Assert.NotNull(instance.PlayersUpdatedAt);

        // A failed read leaves the last good list alone instead of blanking it.
        await dialog.RefreshOpenPlayersAsync();
        Assert.Equal(2, instance.Players.Count);
        Assert.False(instance.HasPlayersError, instance.PlayersError);
        dialog.StopPlayerRefresh();
    }

    [Fact]
    public async Task YourOwnInstanceIsNamedFromTheGameLogInsteadOfTheApi()
    {
        using var handler = new InstanceHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });

        (string Id, string Name, string Avatar)? opened = null;
        var roster = new[]
        {
            new InstanceRosterPlayer("usr_log", "Log Only", string.Empty, IsFriend: false, IsSelf: false),
            new InstanceRosterPlayer("usr_me", "Me", string.Empty, IsFriend: false, IsSelf: true),
            new InstanceRosterPlayer(
                "usr_friend", "Friend", "https://example.test/friend.png", IsFriend: true, IsSelf: false)
        };
        var location = $"{WorldId}:{InstanceId}";

        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared,
            openUserProfile: (id, name, avatar) => opened = (id, name, avatar),
            liveRoster: (requested, _) => Task.FromResult(
                string.Equals(requested, location, StringComparison.Ordinal)
                    ? roster
                    : (IReadOnlyList<InstanceRosterPlayer>?)null));
        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);
        var instance = Assert.Single(dialog.Instances);

        await dialog.ShowInstancePlayersAsync(instance);

        Assert.DoesNotContain(handler.Paths, path => path.Contains("/instances/", StringComparison.Ordinal));
        Assert.True(instance.PlayersFromLiveRoster);
        Assert.Equal(3, instance.Players.Count);
        Assert.Equal("usr_me", instance.Players[0].UserId);
        Assert.True(instance.Players[0].IsSelf);
        var friend = instance.Players.Single(player => player.DisplayName == "Friend");
        Assert.True(friend.IsFriend);
        Assert.True(friend.HasAvatar);

        instance.Players.Single(player => player.DisplayName == "Log Only")
            .OpenProfileCommand.Execute(null);
        Assert.Equal(("usr_log", "Log Only", string.Empty), opened);
        dialog.StopPlayerRefresh();
    }

    [Fact]
    public async Task VrchatOnlySharesSomeInstancesAndThePanelSaysSo()
    {
        using var handler = new InstanceHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });
        handler.InstanceResponse = _ => InstanceHandler.JsonNow($$"""
            {
              "instanceId": "{{InstanceId}}",
              "worldId": "{{WorldId}}",
              "location": "{{WorldId}}:{{InstanceId}}",
              "n_users": 15,
              "capacity": 32,
              "type": "public",
              "region": "jp",
              "users": []
            }
            """);

        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared);
        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);
        var instance = Assert.Single(dialog.Instances);

        await dialog.ShowInstancePlayersAsync(instance);

        Assert.Empty(instance.Players);
        Assert.False(instance.HasPlayersError);
        Assert.True(instance.HasPlayersNote);
        Assert.Contains("only shares the player list", instance.PlayersNote);
        Assert.Equal(15, instance.Occupants);
        Assert.Equal("15 / 32 people", instance.OccupancyText);
        Assert.False(instance.ShowNoPlayers);
        dialog.StopPlayerRefresh();
    }

    [Fact]
    public async Task ThePanelRefreshesOnATimerAndStopsWhenItCloses()
    {
        Assert.True(
            WorldDialogViewModel.PlayersRefreshInterval >= TimeSpan.FromSeconds(15),
            "Polling faster than every 15 s would hammer VRChat.");

        using var handler = new InstanceHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });
        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared);
        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);
        var instance = Assert.Single(dialog.Instances);

        await dialog.ShowInstancePlayersAsync(instance);
        Assert.True(dialog.IsPlayerRefreshRunning);

        dialog.CloseInstancePlayersCommand.Execute(null);
        Assert.Null(dialog.SelectedInstance);
        Assert.False(dialog.IsPlayerRefreshRunning);
    }

    [Fact]
    public void AQuietRefreshUpdatesTheOpenPanelInAWindow()
    {
        WpfTestHost.Run(() =>
        {
            using var handler = new InstanceHandler();
            using var api = new VrcApiClient(null, handler);
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });
            using var shell = new MainViewModel(api);
            TestHooks.SetLoggedIn(shell);

            var window = CreateWindow(shell);
            window.Show();
            try
            {
                shell.OpenWorldCommand.Execute(new WorldItem(new VrcWorld
                {
                    Id = WorldId,
                    Name = "Popcorn Palace"
                }));
                Settle.Until(
                    () => shell.WorldDialog is { } dialog && dialog.Instances.Count == 1,
                    Settle.PumpBackground,
                    "the world dialog never listed its instance");
                var worldDialog = shell.WorldDialog!;
                Pump(window);

                var overlay = window.FindName("WorldDialogOverlay") as DependencyObject;
                var players = FindVisualChildren<Button>(overlay!)
                    .Single(button => Equals(button.Content, "Players"));
                var rowItem = Assert.IsType<WorldInstanceItem>(players.DataContext);
                players.Command.Execute(players.CommandParameter);
                Settle.Until(
                    () => rowItem.PlayersLoaded || rowItem.HasPlayersError,
                    Settle.PumpBackground,
                    "the instance players never arrived");
                Assert.Equal(2, rowItem.Players.Count);

                // Someone new joins while the panel stays open; the next quiet read draws them.
                handler.InstanceResponse = _ => InstanceHandler.JsonNow(InstancePayload(
                    "usr_tree", "Tree Bear", AvatarA, trusted: true,
                    "usr_late", "Late Owl", string.Empty));
                _ = worldDialog.RefreshOpenPlayersAsync();
                Settle.Until(
                    () => rowItem.Players.Any(player => player.DisplayName == "Late Owl"),
                    Settle.PumpBackground,
                    "the quiet refresh never reached the open panel");
                Pump(window);

                Assert.Contains(
                    FindVisualChildren<TextBlock>(overlay!),
                    text => text.Text == "Late Owl" && text.IsVisible);
                Assert.Same(rowItem, worldDialog.SelectedInstance);
            }
            finally
            {
                shell.CloseWorldDialogCommand.Execute(null);
                window.Close();
            }
        });
    }

    [Fact]
    public void TheShellPollsTheLogForTheRosterEvenOnAnotherPage()
    {
        WpfTestHost.RunAsync(async () =>
        {
        var logPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "vrccompanion-world-players-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var location = $"{WorldId}:{InstanceId}";
            System.IO.File.WriteAllText(logPath, string.Join('\n', new[]
            {
                $"2026.11.14 12:00:00 Log - [Behaviour] Joining {location}",
                "2026.11.14 12:00:01 Log - [Behaviour] OnPlayerJoined Me (usr_00000000-0000-0000-0000-000000000001)",
                "2026.11.14 12:00:02 Log - [Behaviour] OnPlayerJoined Log Only (usr_00000000-0000-0000-0000-000000000002)",
                string.Empty
            }));

            using var handler = new InstanceHandler();
            using var api = new VrcApiClient(null, handler);
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });
            var instanceViewModel = new InstanceViewModel(
                api,
                ImageLoader.Shared,
                new VrChatLogPlayerService(() => logPath, () => true));

            var currentUser = new VrcUser
            {
                Id = "usr_00000000-0000-0000-0000-000000000001",
                DisplayName = "Me",
                Location = "offline"
            };
            await instanceViewModel.LoadAsync(currentUser, Array.Empty<FriendItem>());

            // The Instance page is not the open section: its own log timer is stopped.
            instanceViewModel.SetActive(false);

            using var shell = new MainViewModel(api);
            TestHooks.SetCurrentUser(shell, currentUser);
            TestHooks.SetLoggedIn(shell);
            TestHooks.AttachInstanceViewModel(shell, instanceViewModel);

            Assert.NotNull(instanceViewModel.Instance);
            Assert.True(
                instanceViewModel.Players.Count > 0,
                "The instance page never built a roster from the game log.");

            var roster = await shell.LiveInstanceRosterAsync(location);
            Assert.NotNull(roster);
            Assert.Contains(roster!, player => player.DisplayName == "Me" && player.IsSelf);
            Assert.Contains(
                roster!,
                player => player.DisplayName == "Log Only" && player.UserId.EndsWith("0002"));

            // Someone joins while the panel is open on another page: the panel's own
            // poll is what finds them, because the Instance page timer is still off.
            System.IO.File.AppendAllText(
                logPath,
                "2026.11.14 12:03:00 Log - [Behaviour] OnPlayerJoined Late Owl (usr_00000000-0000-0000-0000-000000000003)\n");
            roster = await shell.LiveInstanceRosterAsync(location);
            Assert.NotNull(roster);
            Assert.Contains(roster!, player => player.DisplayName == "Late Owl");
            Assert.True(instanceViewModel.Players.Any(player => player.DisplayName == "Late Owl"));

            Assert.Null(await shell.LiveInstanceRosterAsync($"{WorldId}:99999~region(eu)"));
        }
        finally
        {
            System.IO.File.Delete(logPath);
        }
        });
    }

    [Fact]
    public async Task TheWorldDialogPinsTheInstanceYouAreInAndDropsItWhenYouLeave()
    {
        using var handler = new InstanceHandler();
        handler.SlimInstances = new()
        {
            ($"11111~region(us)", 4),
            ($"22222~region(us)", 9)
        };
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });

        VrcInstance? live = new()
        {
            WorldId = WorldId,
            InstanceId = "33333~region(us)",
            Location = $"{WorldId}:33333~region(us)",
            UserCount = 3,
            Capacity = 50,
            Type = "groupPublic"
        };

        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared,
            currentInstance: () => live);
        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);

        // VRChat's world payload did not list the instance the reader is in, so the
        // dialog adds it, pins it above the fuller rooms and labels it.
        Assert.Equal($"{WorldId}:33333~region(us)", dialog.Instances[0].Location);
        Assert.True(dialog.Instances[0].IsCurrent);
        Assert.False(dialog.Instances[1].IsCurrent);

        await dialog.ShowInstancePlayersAsync(dialog.Instances[1]);

        // Leaving the instance clears the label and removes the row it added.
        live = null;
        await dialog.RefreshOpenPlayersAsync();
        Assert.DoesNotContain(dialog.Instances, item => item.IsCurrent);
        Assert.DoesNotContain(dialog.Instances, item => item.Location.EndsWith("33333~region(us)"));
        dialog.StopPlayerRefresh();
    }

    [Fact]
    public void OpeningAWorldMarksTheLiveInstanceEvenWhenTheInstancePageWasNeverOpened()
    {
        WpfTestHost.RunAsync(async () =>
        {
            var logPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "vrccompanion-world-current-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                var location = $"{WorldId}:{InstanceId}";
                System.IO.File.WriteAllText(logPath, string.Join('\n', new[]
                {
                    $"2026.11.14 12:00:00 Log - [Behaviour] Joining {location}",
                    "2026.11.14 12:00:01 Log - [Behaviour] OnPlayerJoined Me (usr_00000000-0000-0000-0000-000000000001)",
                    string.Empty
                }));

                using var handler = new InstanceHandler();
                using var api = new VrcApiClient(null, handler);
                using var shell = new MainViewModel(api);
                var me = new VrcUser
                {
                    Id = "usr_00000000-0000-0000-0000-000000000001",
                    DisplayName = "Me",
                    Location = "offline"
                };
                TestHooks.SetCurrentUser(shell, me);
                TestHooks.SetLoggedIn(shell);

                var instanceViewModel = new InstanceViewModel(
                    api,
                    ImageLoader.Shared,
                    new VrChatLogPlayerService(() => logPath, () => true));
                TestHooks.AttachInstanceViewModel(shell, instanceViewModel);

                shell.OpenWorldCommand.Execute(new WorldItem(new VrcWorld
                {
                    Id = WorldId,
                    Name = "Popcorn Palace"
                }));

                Settle.Until(
                    () => shell.WorldDialog is { } dialog && dialog.Instances.Any(item => item.IsCurrent),
                    Settle.PumpBackground,
                    "the world dialog never marked the instance the account is in");

                var worldDialog = shell.WorldDialog!;
                Assert.True(worldDialog.Instances[0].IsCurrent);
                Assert.Equal(location, worldDialog.Instances[0].Location);
                shell.CloseWorldDialogCommand.Execute(null);
                await Task.CompletedTask;
            }
            finally
            {
                System.IO.File.Delete(logPath);
            }
        });
    }

    [Fact]
    public void TheWindowLabelsYouAreHereAndPagesLongInstanceLists()
    {
        WpfTestHost.Run(() =>
        {
            using var handler = new InstanceHandler();
            handler.SlimInstances = Enumerable.Range(1, 19)
                .Select(index => ($"{index}0000~region(us)", 20 - index))
                .ToList();
            using var api = new VrcApiClient(null, handler);
            using var shell = new MainViewModel(api);
            var me = new VrcUser { Id = "usr_me", DisplayName = "Me" };
            TestHooks.SetCurrentUser(shell, me);
            TestHooks.SetLoggedIn(shell);

            var live = new VrcInstance
            {
                WorldId = WorldId,
                InstanceId = "150000~region(us)",
                Location = $"{WorldId}:150000~region(us)",
                UserCount = 5,
                Capacity = 50,
                Type = "public"
            };
            var instanceViewModel = new InstanceViewModel(api, ImageLoader.Shared);
            TestHooks.SetInstance(instanceViewModel, live);
            TestHooks.AttachInstanceViewModel(shell, instanceViewModel);

            var window = CreateWindow(shell);
            window.Show();
            try
            {
                shell.OpenWorldCommand.Execute(new WorldItem(new VrcWorld
                {
                    Id = WorldId,
                    Name = "Popcorn Palace"
                }));
                Settle.Until(
                    () => shell.WorldDialog is { InstancesPage.Items.Count: 8 },
                    Settle.PumpBackground,
                    "the world dialog never listed its first page");
                var worldDialog = shell.WorldDialog!;
                Pump(window);

                var overlay = window.FindName("WorldDialogOverlay") as DependencyObject;
                Assert.True(overlay is not null);

                Assert.Contains(
                    FindVisualChildren<TextBlock>(overlay!),
                    text => text.Text == "YOU ARE HERE" && text.IsVisible);

                var next = FindVisualChildren<Button>(overlay!)
                    .Single(button =>
                        Equals(button.Content, "Next") &&
                        ReferenceEquals(button.DataContext, worldDialog.InstancesPage));
                Assert.True(next.IsVisible, "A long instance list has no visible Next button.");
                next.Command.Execute(next.CommandParameter);
                Pump(window);

                Assert.Equal("2 of 3", worldDialog.InstancesPage.PageText);
                Assert.Equal(8, worldDialog.InstancesPage.Items.Count);
                Assert.DoesNotContain(worldDialog.InstancesPage.Items, item => item.IsCurrent);
            }
            finally
            {
                shell.CloseWorldDialogCommand.Execute(null);
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ManyActiveInstancesPageInsteadOfAllDrawing()
    {
        using var handler = new InstanceHandler();
        handler.SlimInstances = Enumerable.Range(1, 19)
            .Select(index => ($"{index}0000~region(us)", 20 - index))
            .ToList();
        using var api = new VrcApiClient(null, handler);
        TestVrcUser(api);

        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared);
        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);

        Assert.Equal(19, dialog.Instances.Count);
        Assert.True(dialog.InstancesPage.HasPages);
        Assert.Equal(8, dialog.InstancesPage.Items.Count);
        Assert.Equal("1 of 3", dialog.InstancesPage.PageText);

        dialog.InstancesPage.NextCommand.Execute(null);
        Assert.Equal("2 of 3", dialog.InstancesPage.PageText);
        Assert.Equal(8, dialog.InstancesPage.Items.Count);

        dialog.InstancesPage.NextCommand.Execute(null);
        Assert.Equal("3 of 3", dialog.InstancesPage.PageText);
        Assert.Equal(3, dialog.InstancesPage.Items.Count);
        Assert.False(dialog.InstancesPage.NextCommand.CanExecute(null));

        dialog.InstancesPage.PreviousCommand.Execute(null);
        Assert.Equal("2 of 3", dialog.InstancesPage.PageText);
    }

    private static void TestVrcUser(VrcApiClient api)
        => TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });

    private static MainWindow CreateWindow(MainViewModel shell)
        => new(shell, initializeOnLoad: false)
        {
            Width = 1400,
            Height = 900,
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

    private sealed class InstanceHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();

        public int InstanceCalls { get; private set; }

        /// <summary>Answers the n-th instance read (1-based); null uses the default payload.</summary>
        public Func<int, HttpResponseMessage>? InstanceResponse { get; set; }

        /// <summary>The world payload's visible instances; null is one row matching the focus.</summary>
        public List<(string InstanceId, int Occupants)>? SlimInstances { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Paths.Add(path);

            if (path.Contains("/instances/", StringComparison.Ordinal))
            {
                InstanceCalls++;
                if (InstanceResponse is { } response)
                {
                    await Task.Yield();
                    return response(InstanceCalls);
                }

                return await Json(InstancePayload(
                    "usr_tree", "Tree Bear", AvatarA, trusted: true,
                    "usr_owl", "Night Owl", string.Empty));
            }

            if (path.EndsWith($"/worlds/{WorldId}", StringComparison.Ordinal))
            {
                var slim = SlimInstances ?? new() { (InstanceId, 2) };
                var rows = string.Join(
                    ",",
                    slim.Select(instance => $$"""
                        {
                          "instanceId": "{{instance.InstanceId}}",
                          "location": "{{WorldId}}:{{instance.InstanceId}}",
                          "userCount": {{instance.Occupants}},
                          "capacity": 50,
                          "region": "us",
                          "type": "public"
                        }
                        """));
                return await Json($$"""
                    {
                      "id": "{{WorldId}}",
                      "name": "Popcorn Palace",
                      "authorName": "spacebaar",
                      "slimInstances": [ {{rows}} ]
                    }
                    """);
            }

            return await Json("[]");
        }

        internal static HttpResponseMessage JsonNow(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

        internal static async Task<HttpResponseMessage> Json(string json)
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    internal static string InstancePayload(
        string firstId,
        string firstName,
        string firstAvatar,
        bool trusted = false,
        string secondId = "",
        string secondName = "",
        string secondAvatar = "")
    {
        var users = new List<string>
        {
            UserPayload(firstId, firstName, firstAvatar, trusted)
        };

        if (!string.IsNullOrWhiteSpace(secondId))
        {
            users.Add(UserPayload(secondId, secondName, secondAvatar, false));
        }

        return $$"""
            {
              "instanceId": "{{InstanceId}}",
              "worldId": "{{WorldId}}",
              "location": "{{WorldId}}:{{InstanceId}}",
              "n_users": {{users.Count}},
              "capacity": 32,
              "type": "public",
              "region": "us",
              "users": [ {{string.Join(",", users)}} ]
            }
            """;
    }

    private static string UserPayload(string id, string name, string avatar, bool trusted)
    {
        var icon = string.IsNullOrWhiteSpace(avatar) ? string.Empty : $", \"userIcon\": \"{avatar}\"";
        var tag = trusted ? ", \"tags\": [\"trusted\"]" : string.Empty;
        return $"{{ \"id\": \"{id}\", \"displayName\": \"{name}\"{icon}, \"isFriend\": {(trusted ? "true" : "false")}{tag} }}";
    }
}
