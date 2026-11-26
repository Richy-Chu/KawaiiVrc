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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Paths.Add(path);

            if (path.Contains("/instances/", StringComparison.Ordinal))
            {
                return await Json($$"""
                    {
                      "instanceId": "{{InstanceId}}",
                      "worldId": "{{WorldId}}",
                      "location": "{{WorldId}}:{{InstanceId}}",
                      "n_users": 2,
                      "capacity": 32,
                      "type": "public",
                      "region": "us",
                      "users": [
                        {
                          "id": "usr_tree",
                          "displayName": "Tree Bear",
                          "userIcon": "{{AvatarA}}",
                          "isFriend": true,
                          "tags": ["trusted"]
                        },
                        { "id": "usr_owl", "displayName": "Night Owl" }
                      ]
                    }
                    """);
            }

            if (path.EndsWith($"/worlds/{WorldId}", StringComparison.Ordinal))
            {
                return await Json($$"""
                    {
                      "id": "{{WorldId}}",
                      "name": "Popcorn Palace",
                      "authorName": "spacebaar",
                      "slimInstances": [
                        {
                          "instanceId": "{{InstanceId}}",
                          "location": "{{WorldId}}:{{InstanceId}}",
                          "userCount": 2,
                          "capacity": 32,
                          "region": "us",
                          "type": "public"
                        }
                      ]
                    }
                    """);
            }

            return await Json("[]");
        }

        private static async Task<HttpResponseMessage> Json(string json)
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
