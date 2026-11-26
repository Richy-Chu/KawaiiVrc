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
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// "You are here" must stay true while the world dialog is open without depending on the
/// Players panel, and a world search marks the world the account is standing in.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class WorldCurrentInstanceTests
{
    private const string WorldId = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    private const string OtherWorldId = "wrld_00000000-0000-0000-0000-000000000002";
    private const string InstanceId = "12345~region(us)";

    [Fact]
    public void LeavingClearsYouAreHereWithThePlayersPanelClosed()
    {
        var logPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "vrccompanion-world-leave-" + Guid.NewGuid().ToString("N") + ".log");
        var interval = WorldDialogViewModel.LiveStateRefreshInterval;
        try
        {
            var location = $"{WorldId}:{InstanceId}";
            System.IO.File.WriteAllText(logPath, string.Join('\n', new[]
            {
                $"2026.11.14 12:00:00 Log - [Behaviour] Joining {location}",
                "2026.11.14 12:00:01 Log - [Behaviour] OnPlayerJoined Me (usr_00000000-0000-0000-0000-000000000001)",
                string.Empty
            }));

            WpfTestHost.RunAsync(async () =>
            {
                using var handler = new LiveHandler();
                handler.SlimInstances = new() { ("99999~region(eu)", 12) };
                using var api = new VrcApiClient(null, handler);
                using var shell = new MainViewModel(api);
                TestHooks.SetCurrentUser(shell, new VrcUser
                {
                    Id = "usr_00000000-0000-0000-0000-000000000001",
                    DisplayName = "Me",
                    Location = "offline"
                });
                TestHooks.SetLoggedIn(shell);

                var instanceViewModel = new InstanceViewModel(
                    api,
                    ImageLoader.Shared,
                    new VrChatLogPlayerService(() => logPath, () => true));
                TestHooks.AttachInstanceViewModel(shell, instanceViewModel);

                WorldDialogViewModel.LiveStateRefreshInterval = TimeSpan.FromMilliseconds(100);
                shell.OpenWorldCommand.Execute(new WorldItem(new VrcWorld
                {
                    Id = WorldId,
                    Name = "Popcorn Palace"
                }));

                Settle.Until(
                    () => shell.WorldDialog is { } dialog &&
                          dialog.Instances.Any(item => item.IsCurrent),
                    Settle.PumpBackground,
                    "the world dialog never marked the instance the account is in");

                var worldDialog = shell.WorldDialog!;
                Assert.Null(worldDialog.SelectedInstance);
                Assert.True(
                    worldDialog.IsLiveRefreshRunning,
                    "The current-instance timer must run with the Players panel closed.");

                System.IO.File.AppendAllText(
                    logPath,
                    "\n2026.11.14 12:05:00 Log - [Behaviour] OnLeftRoom\n");

                Settle.Until(
                    () => !worldDialog.Instances.Any(item => item.IsCurrent) &&
                          worldDialog.Instances.All(item => item.Location != location),
                    Settle.PumpBackground,
                    "leaving did not clear the YOU ARE HERE row with the panel closed");

                Assert.Null(worldDialog.SelectedInstance);
                shell.CloseWorldDialogCommand.Execute(null);
                Assert.False(worldDialog.IsLiveRefreshRunning);
                await Task.CompletedTask;
            });
        }
        finally
        {
            WorldDialogViewModel.LiveStateRefreshInterval = interval;
            System.IO.File.Delete(logPath);
        }
    }

    [Fact]
    public async Task SearchResultsMarkTheWorldYouAreIn()
    {
        using var handler = new LiveHandler();
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_00000000-0000-0000-0000-000000000001",
            DisplayName = "Me",
            Location = $"{WorldId}:{InstanceId}"
        });
        TestHooks.SetLoggedIn(shell);
        TestHooks.AttachInstanceViewModel(shell, LiveViewModel(api));

        await shell.SearchWorldsAsync(string.Empty);

        Assert.True(shell.Worlds.Single(world => world.Id == WorldId).IsCurrentWorld);
        Assert.False(shell.Worlds.Single(world => world.Id != WorldId).IsCurrentWorld);
    }

    [Fact]
    public void TheWorldCardShowsYouAreHere()
    {
        WpfTestHost.Run(() =>
        {
            using var handler = new LiveHandler();
            using var api = new VrcApiClient(null, handler);
            using var shell = new MainViewModel(api);
            TestHooks.SetCurrentUser(shell, new VrcUser
            {
                Id = "usr_00000000-0000-0000-0000-000000000001",
                DisplayName = "Me",
                Location = $"{WorldId}:{InstanceId}"
            });
            TestHooks.SetLoggedIn(shell);
            TestHooks.AttachInstanceViewModel(shell, LiveViewModel(api));

            var window = new MainWindow(shell, initializeOnLoad: false)
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
            window.Show();
            try
            {
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "worlds");
                Settle.Until(
                    () => shell.Worlds.Count >= 2,
                    Settle.PumpBackground,
                    "the world search never produced its results");
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                var badges = FindVisualChildren<TextBlock>(window)
                    .Where(text => text.Text == "YOU'RE HERE" && text.IsVisible)
                    .ToList();
                Assert.Single(badges);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static InstanceViewModel LiveViewModel(VrcApiClient api)
    {
        var viewModel = new InstanceViewModel(api, ImageLoader.Shared);
        TestHooks.SetInstance(viewModel, new VrcInstance
        {
            WorldId = WorldId,
            InstanceId = InstanceId,
            Location = $"{WorldId}:{InstanceId}",
            UserCount = 9,
            Capacity = 50,
            Type = "public"
        });
        return viewModel;
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

    private sealed class LiveHandler : HttpMessageHandler
    {
        public List<(string InstanceId, int Occupants)>? SlimInstances { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/worlds", StringComparison.Ordinal))
            {
                return Task.FromResult(Json($$"""
                    [
                      { "id": "{{WorldId}}", "name": "Popcorn Palace", "authorName": "spacebaar" },
                      { "id": "{{OtherWorldId}}", "name": "Moonlit Meadow", "authorName": "spacebaar" }
                    ]
                    """));
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
                return Task.FromResult(Json($$"""
                    {
                      "id": "{{WorldId}}",
                      "name": "Popcorn Palace",
                      "authorName": "spacebaar",
                      "slimInstances": [ {{rows}} ]
                    }
                    """));
            }

            if (path.Contains("/instances/", StringComparison.Ordinal))
            {
                return Task.FromResult(Json($$"""
                    {
                      "instanceId": "{{InstanceId}}",
                      "worldId": "{{WorldId}}",
                      "location": "{{WorldId}}:{{InstanceId}}",
                      "n_users": 1,
                      "capacity": 50,
                      "type": "public",
                      "region": "us",
                      "users": []
                    }
                    """));
            }

            return Task.FromResult(Json("[]"));
        }

        private static HttpResponseMessage Json(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }
}
