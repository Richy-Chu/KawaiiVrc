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
    private const string ThirdWorldId = "wrld_00000000-0000-0000-0000-000000000003";
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
    public void WorldsTabFollowsJoinsLeavesAndSwitchesWithoutSearch()
    {
        var logPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "vrccompanion-worlds-live-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            System.IO.File.WriteAllText(logPath, string.Join('\n', new[]
            {
                $"2026.11.14 12:00:00 Log - [Behaviour] Joining {WorldId}:{InstanceId}",
                string.Empty
            }));

            WpfTestHost.RunAsync(async () =>
            {
                using var handler = new LiveHandler();
                using var api = new VrcApiClient(null, handler);
                using var shell = new MainViewModel(api);
                TestHooks.SetCurrentUser(shell, new VrcUser
                {
                    Id = "usr_00000000-0000-0000-0000-000000000001",
                    DisplayName = "Me",
                    Location = "offline"
                });
                TestHooks.SetLoggedIn(shell);
                TestHooks.UseLogPlayer(
                    shell,
                    new VrChatLogPlayerService(() => logPath, () => true));

                shell.SelectedSection = shell.Sections.Single(section => section.Key == "worlds");
                Settle.Until(
                    () => shell.Worlds.Count >= 3 && shell.IsWorldsLiveRefreshRunning,
                    Settle.PumpBackground,
                    "the Worlds tab never loaded with its live timer running");
                Assert.Equal(WorldId, shell.Worlds[0].Id);
                Assert.True(shell.Worlds[0].IsCurrentWorld);

                // A switch is caught without another search.
                System.IO.File.AppendAllText(
                    logPath,
                    $"\n2026.11.14 12:05:00 Log - [Behaviour] Joining {OtherWorldId}:99999~region(eu)\n");
                await shell.RefreshWorldsLiveAsync();

                Assert.Equal(OtherWorldId, shell.Worlds[0].Id);
                Assert.True(shell.Worlds[0].IsCurrentWorld);
                Assert.False(shell.Worlds.Single(world => world.Id == WorldId).IsCurrentWorld);

                // Leaving clears the badge and leaves the cards where they are.
                System.IO.File.AppendAllText(
                    logPath,
                    "\n2026.11.14 12:10:00 Log - [Behaviour] OnLeftRoom\n");
                await shell.RefreshWorldsLiveAsync();

                Assert.DoesNotContain(shell.Worlds, world => world.IsCurrentWorld);

                shell.SelectedSection = shell.Sections.Single(section => section.Key == "friends");
                Assert.False(shell.IsWorldsLiveRefreshRunning);
            });
        }
        finally
        {
            System.IO.File.Delete(logPath);
        }
    }

    [Fact]
    public void CurrentWorldStaysFirstThroughPagingAndTabSwitch()
    {
        var logPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "vrccompanion-worlds-pages-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            System.IO.File.WriteAllText(logPath, string.Join('\n', new[]
            {
                $"2026.11.14 12:00:00 Log - [Behaviour] Joining {OtherWorldId}:99999~region(eu)",
                string.Empty
            }));

            WpfTestHost.RunAsync(async () =>
            {
                using var handler = new LiveHandler { ManyWorlds = true };
                using var api = new VrcApiClient(null, handler);
                using var shell = new MainViewModel(api);
                TestHooks.SetCurrentUser(shell, new VrcUser
                {
                    Id = "usr_00000000-0000-0000-0000-000000000001",
                    DisplayName = "Me",
                    Location = "offline"
                });
                TestHooks.SetLoggedIn(shell);
                TestHooks.UseLogPlayer(
                    shell,
                    new VrChatLogPlayerService(() => logPath, () => true));

                shell.SelectedSection = shell.Sections.Single(section => section.Key == "worlds");
                Settle.Until(
                    () => shell.Worlds.Count >= 24,
                    Settle.PumpBackground,
                    "the Worlds tab never loaded its first page");
                await shell.RefreshWorldsLiveAsync();
                Assert.Equal(OtherWorldId, shell.Worlds[0].Id);
                Assert.True(shell.Worlds[0].IsCurrentWorld);

                // Switch to a world that was not on the popularity page at all.
                System.IO.File.AppendAllText(
                    logPath,
                    $"\n2026.11.14 12:05:00 Log - [Behaviour] Joining {ThirdWorldId}:55555~region(us)\n");
                await shell.RefreshWorldsLiveAsync();
                Assert.Equal(ThirdWorldId, shell.Worlds[0].Id);
                Assert.True(shell.Worlds[0].IsCurrentWorld);

                // Page away and back: the cached first page must keep the live world first.
                await shell.GoToWorldsPageAsync(2);
                Assert.Equal(2, shell.WorldsPageNumber);
                Assert.DoesNotContain(shell.Worlds, world => world.IsCurrentWorld);
                await shell.GoToWorldsPageAsync(1);
                Assert.Equal(ThirdWorldId, shell.Worlds[0].Id);
                Assert.True(shell.Worlds[0].IsCurrentWorld);

                // Leave the tab and come back: the immediate live refresh re-applies it.
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "friends");
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "worlds");
                Settle.Until(
                    () => shell.Worlds.Count > 0 &&
                          shell.Worlds[0].Id == ThirdWorldId &&
                          shell.Worlds[0].IsCurrentWorld,
                    Settle.PumpBackground,
                    "returning to Worlds lost the current world's first card");
                Assert.True(shell.IsWorldsLiveRefreshRunning);
            });
        }
        finally
        {
            System.IO.File.Delete(logPath);
        }
    }

    [Fact]
    public void RejoinTargetsTheInstanceInTheLogAndFollowsAWorldSwitch()
    {
        var logPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "vrccompanion-rejoin-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            System.IO.File.WriteAllText(logPath, string.Join('\n', new[]
            {
                $"2026.11.14 12:00:00 Log - [Behaviour] Joining {WorldId}:{InstanceId}",
                string.Empty
            }));

            WpfTestHost.RunAsync(async () =>
            {
                using var handler = new LiveHandler();
                using var api = new VrcApiClient(null, handler);
                using var shell = new MainViewModel(api);
                TestHooks.SetCurrentUser(shell, new VrcUser
                {
                    Id = "usr_00000000-0000-0000-0000-000000000001",
                    DisplayName = "Me",
                    Location = "offline"
                });
                TestHooks.SetLoggedIn(shell);
                TestHooks.UseLogPlayer(
                    shell,
                    new VrChatLogPlayerService(() => logPath, () => true));
                TestHooks.UseGameRunningProbe(() => false);
                // Every launch path goes through the seams: no real VRChat, no registry handler.
                TestHooks.UseHandlerPathProvider(() => @"C:\fake\VRChat.exe");
                var launched = new List<string>();
                TestHooks.UseUriLauncher(uri => launched.Add(uri));
                TestHooks.UseHandlerLauncher((handler, arguments) => launched.Add(arguments));

                // Choosing a mode alone does not turn the pill on: it reports what is running.
                shell.IsVrDefault = true;
                TestHooks.UseVrModeProbe(() => true);
                await shell.RefreshRunningLaunchModeAsync();
                Assert.False(shell.HasRunningLaunchMode);

                await shell.RejoinWorldAsync();
                Assert.True(
                    launched.Count == 1,
                    $"rejoin launched nothing; info={shell.InfoMessage} error={shell.ErrorMessage}");
                Assert.Contains(WorldId, launched[0]);
                Assert.Contains("12345", launched[0]);

                // The game is now up in VR; the light follows the running mode, not the radio.
                TestHooks.UseGameRunningProbe(() => true);
                TestHooks.UseVrModeProbe(() => true);
                await shell.RefreshRunningLaunchModeAsync();
                Assert.True(shell.HasRunningLaunchMode);
                Assert.Equal("VR", shell.RunningLaunchModeLabel);
                Assert.True(shell.IsRunningVr);
                shell.IsDesktopDefault = true;
                Assert.Equal("VR", shell.RunningLaunchModeLabel);

                // A switch while the app is open is what the next rejoin uses.
                TestHooks.UseGameRunningProbe(() => false);
                shell.IsVrDefault = true;
                System.IO.File.AppendAllText(
                    logPath,
                    $"\n2026.11.14 12:05:00 Log - [Behaviour] Joining {OtherWorldId}:99999~region(eu)\n");
                await shell.RejoinWorldAsync();
                Assert.Equal(2, launched.Count);

                Assert.Contains(OtherWorldId, launched[1]);
                Assert.Contains("99999", launched[1]);
                Assert.DoesNotContain("12345", launched[1]);

                // A desktop run (no VR runtime) moves the label even though the radio says VR.
                TestHooks.UseGameRunningProbe(() => false);
                shell.IsDesktopDefault = true;
                await shell.RejoinWorldAsync();
                TestHooks.UseGameRunningProbe(() => true);
                TestHooks.UseVrModeProbe(() => false);
                await shell.RefreshRunningLaunchModeAsync();
                Assert.Equal("Desktop", shell.RunningLaunchModeLabel);
                Assert.True(shell.IsRunningDesktop);
                Assert.True(shell.HasRunningLaunchMode);

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
                    shell.SelectedSection = shell.Sections.Single(section => section.Key == "friends");
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                    var indicator = FindVisualChildren<Border>(window)
                        .First(border =>
                            Equals(border.ToolTip, "VRChat is running in Desktop mode") &&
                            border.IsVisible);
                    var light = FindVisualChildren<System.Windows.Shapes.Ellipse>(indicator).First();
                    Assert.Equal(
                        ((SolidColorBrush)window.FindResource("GreenBrush")).Color,
                        ((SolidColorBrush)light.Fill).Color);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
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
        TestHooks.UseLogPlayer(
            shell,
            new VrChatLogPlayerService(() => string.Empty, () => false));

        // Launch condition: no Instance page was ever opened, so nothing has loaded the
        // live instance. The world still has to be marked and come first.
        Assert.Null(shell.InstanceViewModel?.Instance);

        await shell.SearchWorldsAsync(string.Empty);

        Assert.Equal(WorldId, shell.Worlds[0].Id);
        Assert.True(shell.Worlds[0].IsCurrentWorld);
        Assert.All(
            shell.Worlds.Skip(1),
            world => Assert.False(world.IsCurrentWorld));
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
            TestHooks.UseLogPlayer(
                shell,
                new VrChatLogPlayerService(() => string.Empty, () => false));

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

                Assert.Equal(WorldId, shell.Worlds[0].Id);
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

        /// <summary>Answers the popularity page with 30 rows, current world not among them.</summary>
        public bool ManyWorlds { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/worlds", StringComparison.Ordinal))
            {
                if (ManyWorlds)
                {
                    var rows = Enumerable.Range(1, 30)
                        .Select(index => new
                        {
                            id = $"wrld_10000000-0000-0000-0000-{index:D12}",
                            name = $"World {index}",
                            authorName = "spacebaar"
                        })
                        .ToList();
                    rows.Insert(5, new
                    {
                        id = OtherWorldId,
                        name = "Moonlit Meadow",
                        authorName = "spacebaar"
                    });
                    return Task.FromResult(Json(System.Text.Json.JsonSerializer.Serialize(rows)));
                }

                return Task.FromResult(Json($$"""
                    [
                      { "id": "{{OtherWorldId}}", "name": "Moonlit Meadow", "authorName": "spacebaar" },
                      { "id": "{{ThirdWorldId}}", "name": "Third Star", "authorName": "spacebaar" }
                    ]
                    """));
            }

            if (path.EndsWith($"/worlds/{WorldId}", StringComparison.Ordinal) ||
                path.EndsWith($"/worlds/{OtherWorldId}", StringComparison.Ordinal) ||
                path.EndsWith($"/worlds/{ThirdWorldId}", StringComparison.Ordinal))
            {
                var isMain = path.EndsWith($"/worlds/{WorldId}", StringComparison.Ordinal);
                var worldId = isMain
                    ? WorldId
                    : path.EndsWith($"/worlds/{OtherWorldId}", StringComparison.Ordinal)
                        ? OtherWorldId
                        : ThirdWorldId;
                var name = isMain
                    ? "Popcorn Palace"
                    : worldId == OtherWorldId
                        ? "Moonlit Meadow"
                        : "Third Star";
                var slim = isMain
                    ? SlimInstances ?? new() { (InstanceId, 2) }
                    : new() { ("99999~region(eu)", 4) };
                var rows = string.Join(
                    ",",
                    slim.Select(instance => $$"""
                        {
                          "instanceId": "{{instance.InstanceId}}",
                          "location": "{{worldId}}:{{instance.InstanceId}}",
                          "userCount": {{instance.Occupants}},
                          "capacity": 50,
                          "region": "us",
                          "type": "public"
                        }
                        """));
                return Task.FromResult(Json($$"""
                    {
                      "id": "{{worldId}}",
                      "name": "{{name}}",
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
