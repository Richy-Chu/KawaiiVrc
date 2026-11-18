using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class InstanceTests
{
    private const string WorldId = "wrld_11111111-1111-1111-1111-111111111111";
    private const string Location = WorldId + ":12345~region(us)";
    private const string LoggedUserId = "usr_22222222-2222-2222-2222-222222222222";
    private const string ApiUserId = "usr_33333333-3333-3333-3333-333333333333";
    private const string FriendUserId = "usr_44444444-4444-4444-4444-444444444444";
    private const string OwnerUserId = "usr_55555555-5555-5555-5555-555555555555";
    private const string CurrentUserId = "usr_66666666-6666-6666-6666-666666666666";

        [Fact]
    public void LogSignatureChangesWhenPlayerJoinsTheSameInstance()
    {
        var before = new VrChatLogSnapshot(
            Location,
            new[] { new VrChatLogPlayer(LoggedUserId, "Me") });
        var after = new VrChatLogSnapshot(
            Location,
            new[]
            {
                new VrChatLogPlayer(LoggedUserId, "Me"),
                new VrChatLogPlayer(ApiUserId, "Someone")
            });

        Assert.NotEqual(
            InstanceViewModel.BuildLogSignature(before),
            InstanceViewModel.BuildLogSignature(after));
    }

    [Fact]

    public async Task InstanceEndpointParsesRealWorldAndPlayerData()
    {
        var handler = new InstanceHandler();
        using var api = new VrcApiClient(null, handler);

        var instance = await api.GetInstanceAsync(Location);

        Assert.NotNull(instance);
        Assert.Equal(Location, instance!.Location);
        Assert.Equal("12345~region(us)", instance.InstanceId);
        Assert.Equal(WorldId, instance.WorldId);
        Assert.Equal(2, instance.Occupants);
        Assert.Equal("Test World", instance.World!.Name);
        Assert.Equal("Test Author", instance.World.AuthorName);
        Assert.Equal("grp_owner", instance.OwnerId);
        Assert.True(instance.AgeGate);
        Assert.True(instance.QueueEnabled);
        Assert.Equal("us", instance.Region);
        Assert.Single(instance.Users);
        Assert.Equal(ApiUserId, instance.Users[0].Id);
        Assert.Equal("/api/1/instances/" + WorldId + ":12345~region(us)", handler.LastPath);
    }

    [Fact]
    public async Task InvalidInstanceLocationDoesNotCallTheApi()
    {
        var handler = new InstanceHandler();
        using var api = new VrcApiClient(null, handler);

        var instance = await api.GetInstanceAsync("private");

        Assert.Null(instance);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public void LogRosterTracksJoinsLeavesAndWorldChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), "vrccompanion-instance-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllText(path, string.Join('\n', new[]
            {
                $"2026.11.14 12:00:00 Log - [Behaviour] Joining {Location}",
                $"2026.11.14 12:00:01 Log - [Behaviour] OnPlayerJoined Logged Person ({LoggedUserId})",
                "2026.11.14 12:00:02 Log - [Behaviour] OnPlayerJoined Name Only",
                "2026.11.14 12:00:02 Log - [Behaviour] Switching Logged Person to avatar First Avatar",
                "2026.11.14 12:00:02 Log - [Behaviour] Switching Me to avatar My Avatar",
                "2026.11.14 12:00:02 Log - [AssetBundleDownloadManager] [12] Unpacking Avatar (First Avatar by Author One)",
                string.Empty
            }));

            var service = new VrChatLogPlayerService(() => path);
            var first = service.Poll();

            Assert.NotNull(first);
            Assert.Equal(Location, first!.Location);
            Assert.Equal(2, first.Players.Count);
            Assert.Contains(first.Players, player => player.UserId == LoggedUserId && player.DisplayName == "Logged Person");
            Assert.Contains(first.Players, player => player.UserId.Length == 0 && player.DisplayName == "Name Only");
            Assert.Equal("First Avatar", first.Avatars![LoggedUserId].Name);
            Assert.Equal("Author One", first.Avatars[LoggedUserId].AuthorName);
            // The signed-in player is not always in OnPlayerJoined, but their
            // own switch must still be observable by display name.
            Assert.Equal("My Avatar", first.AvatarSwitches!["Me"].Name);

            File.AppendAllText(path, string.Join('\n', new[]
            {
                $"2026.11.14 12:00:03 Log - [Behaviour] OnPlayerLeft Logged Person ({LoggedUserId})",
                $"2026.11.14 12:00:04 Log - [Behaviour] OnPlayerJoined Second Person ({ApiUserId})",
                "2026.11.14 12:00:04 Log - [Behaviour] Switching Second Person to avatar Second Avatar",
                "2026.11.14 12:00:04 Log - [AssetBundleDownloadManager] [13] Unpacking Avatar (Second Avatar by Author Two)",
                string.Empty
            }));

            var second = service.Poll();

            Assert.NotNull(second);
            Assert.DoesNotContain(second!.Players, player => player.UserId == LoggedUserId);
            Assert.Contains(second.Players, player => player.UserId == ApiUserId);
            Assert.Contains(second.Players, player => player.DisplayName == "Name Only");
            Assert.Equal("Second Avatar", second.Avatars![ApiUserId].Name);
            Assert.Equal("Author Two", second.Avatars[ApiUserId].AuthorName);

            File.AppendAllText(path, string.Join('\n', new[]
            {
                $"2026.11.14 12:00:05 Log - [Behaviour] Joining {WorldId}:67890~region(eu)",
                "2026.11.14 12:00:06 Log - [Behaviour] OnPlayerJoined Third Person",
                string.Empty
            }));

            var third = service.Poll();

            Assert.NotNull(third);
            Assert.Equal(WorldId + ":67890~region(eu)", third!.Location);
            Assert.Single(third.Players);
            Assert.Equal("Third Person", third.Players[0].DisplayName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LogRosterTracksLocalBlockAndUnblock()
    {
        var path = Path.Combine(Path.GetTempPath(), "vrccompanion-block-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllText(path, string.Join('\n', new[]
            {
                $"2026.11.14 12:00:00 Log - [Behaviour] Joining {Location}",
                $"2026.11.14 12:00:01 Log - [Behaviour] OnPlayerJoined Logged Person ({LoggedUserId})",
                "2026.11.14 12:00:02 Debug - [ModerationManager] Requesting block on Logged Person",
                "2026.11.14 12:00:03 Debug - [ModerationManager] Logged Person is now Blocked",
                string.Empty
            }));

            var service = new VrChatLogPlayerService(() => path);
            var blocked = service.Poll();

            Assert.NotNull(blocked);
            Assert.Contains("Logged Person", blocked!.BlockedPlayerNames!);
            Assert.DoesNotContain("Logged Person", blocked.UnblockedPlayerNames!);

            File.AppendAllText(path, string.Join('\n', new[]
            {
                "2026.11.14 12:00:04 Debug - [ModerationManager] Logged Person is no longer Blocked",
                string.Empty
            }));

            var unblocked = service.Poll();

            Assert.NotNull(unblocked);
            Assert.DoesNotContain("Logged Person", unblocked!.BlockedPlayerNames!);
            Assert.Contains("Logged Person", unblocked.UnblockedPlayerNames!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ViewModelShowsBlockedMarkerFromLog()
    {
        RunOnSta(() =>
        {
            var logPath = Path.Combine(Path.GetTempPath(), "vrccompanion-block-view-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                File.WriteAllText(logPath, string.Join('\n', new[]
                {
                    $"2026.11.14 12:00:00 Log - [Behaviour] Joining {Location}",
                    $"2026.11.14 12:00:01 Log - [Behaviour] OnPlayerJoined Logged Person ({LoggedUserId})",
                    "2026.11.14 12:00:02 Debug - [ModerationManager] Logged Person is now Blocked",
                    string.Empty
                }));

                using var api = new VrcApiClient(null, new InstanceHandler());
                var current = new VrcUser
                {
                    Id = CurrentUserId,
                    DisplayName = "Me",
                    Username = "me",
                    Location = "offline"
                };

                var viewModel = new InstanceViewModel(
                    api,
                    ImageLoader.Shared,
                    new VrChatLogPlayerService(() => logPath, () => true));

                viewModel.LoadAsync(current, Array.Empty<FriendItem>()).GetAwaiter().GetResult();

                var player = viewModel.Players.Single(row => row.UserId == LoggedUserId);
                Assert.True(player.IsBlocked);
                Assert.True(player.HasBlockedMarker);
                Assert.Equal("(B)", player.BlockedMarker);
                Assert.Contains("You have blocked", player.BlockedToolTip);
            }
            finally
            {
                File.Delete(logPath);
            }
        });
    }

    [Fact]
    public void ViewModelClearsBlockedMarkerAfterLogUnblock()
    {
        RunOnSta(() =>
        {
            var logPath = Path.Combine(Path.GetTempPath(), "vrccompanion-unblock-view-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                File.WriteAllText(logPath, string.Join('\n', new[]
                {
                    $"2026.11.14 12:00:00 Log - [Behaviour] Joining {Location}",
                    $"2026.11.14 12:00:01 Log - [Behaviour] OnPlayerJoined Logged Person ({LoggedUserId})",
                    "2026.11.14 12:00:02 Debug - [ModerationManager] Logged Person is now Blocked",
                    "2026.11.14 12:00:03 Debug - [ModerationManager] Logged Person is no longer Blocked",
                    string.Empty
                }));

                using var api = new VrcApiClient(null, new InstanceHandler());
                var current = new VrcUser
                {
                    Id = CurrentUserId,
                    DisplayName = "Me",
                    Username = "me",
                    Location = "offline"
                };

                var viewModel = new InstanceViewModel(
                    api,
                    ImageLoader.Shared,
                    new VrChatLogPlayerService(() => logPath, () => true));

                viewModel.LoadAsync(current, Array.Empty<FriendItem>()).GetAwaiter().GetResult();

                var player = viewModel.Players.Single(row => row.UserId == LoggedUserId);
                Assert.False(player.IsBlocked);
                Assert.False(player.HasBlockedMarker);
                Assert.Equal(string.Empty, player.BlockedMarker);
            }
            finally
            {
                File.Delete(logPath);
            }
        });
    }

    [Fact]
    public void ViewModelMergesLogApiFriendsAndCurrentUser()
    {
        RunOnSta(() =>
        {
            var logPath = Path.Combine(Path.GetTempPath(), "vrccompanion-view-" + Guid.NewGuid().ToString("N") + ".log");
            var handler = new InstanceHandler();
            try
            {
                File.WriteAllText(logPath, string.Join('\n', new[]
                {
                    $"2026.11.14 12:00:00 Log - [Behaviour] Joining {Location}",
                    $"2026.11.14 12:00:01 Log - [Behaviour] OnPlayerJoined Logged Person ({LoggedUserId})",
                    $"2026.11.14 12:00:02 Log - [Behaviour] OnPlayerJoined Friend Person ({FriendUserId})",
                    $"2026.11.14 12:00:03 Log - [Behaviour] OnPlayerJoined Me ({CurrentUserId})",
                    string.Empty
                }));

                using var api = new VrcApiClient(null, handler);
                var current = new VrcUser
                {
                    Id = CurrentUserId,
                    DisplayName = "Me",
                    Username = "me",
                    // The web profile can lag behind the game; the log is the
                    // live location source while VRChat is running.
                    Location = "offline"
                };
                var friends = new List<FriendItem>
                {
                    new(new VrcUser
                    {
                        Id = FriendUserId,
                        DisplayName = "Friend Person",
                        Username = "friend",
                        Location = Location,
                        IsFriend = true
                    })
                };

                var viewModel = new InstanceViewModel(
                    api,
                    ImageLoader.Shared,
                    new VrChatLogPlayerService(() => logPath, () => true));

                viewModel.LoadAsync(current, friends).GetAwaiter().GetResult();

                Assert.True(viewModel.HasInstance);
                Assert.Equal("Test World", viewModel.WorldName);
                Assert.Equal("VRChat log + API", viewModel.PlayerListSourceText);
                Assert.Contains(viewModel.Players, player => player.UserId == ApiUserId);
                Assert.Contains(viewModel.Players, player => player.UserId == FriendUserId && player.IsFriend);
                Assert.Contains(viewModel.Players, player => player.UserId == CurrentUserId && player.IsSelf);
                Assert.Contains(viewModel.Players, player => player.UserId == LoggedUserId);

                WaitUntil(() => viewModel.Players
                    .First(player => player.UserId == LoggedUserId)
                    .HasProfile);
                Assert.Equal("Logged Person", viewModel.Players
                    .First(player => player.UserId == LoggedUserId)
                    .DisplayName);
            }
            finally
            {
                File.Delete(logPath);
            }
        });
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }

        Assert.True(condition(), "Timed out waiting for the instance roster.");
    }

    /// <summary>The view model uses WPF collection views.</summary>
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15));

        if (failure is not null)
        {
            throw failure;
        }
    }

    private sealed class InstanceHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public string LastPath { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            LastPath = request.RequestUri?.AbsolutePath ?? string.Empty;
            var path = LastPath;

            if (path.Contains("/instances/", StringComparison.Ordinal))
            {
                return Json($$"""
                    {
                      "id": "{{WorldId}}:12345",
                      "instanceId": "12345~region(us)",
                      "worldId": "{{WorldId}}",
                      "location": "{{Location}}",
                      "userCount": 2,
                      "capacity": 32,
                      "recommendedCapacity": 16,
                      "type": "public",
                      "region": "us",
                      "ownerId": "grp_owner",
                      "ageGate": true,
                      "queueEnabled": true,
                      "queueSize": 3,
                      "tags": ["author_tag_chill"],
                      "platforms": { "standalonewindows": 1, "android": 1 },
                      "users": [
                        {
                          "id": "{{ApiUserId}}",
                          "displayName": "API Person",
                          "username": "api",
                          "status": "active",
                          "platform": "standalonewindows",
                          "tags": ["system_trust_trusted"]
                        }
                      ],
                      "world": {
                        "id": "{{WorldId}}",
                        "name": "Test World",
                        "authorName": "Test Author",
                        "description": "A real world description.",
                        "capacity": 32,
                        "recommendedCapacity": 16,
                        "visits": 1234,
                        "favorites": 56,
                        "publicationDate": "2024-01-02T00:00:00.000Z",
                        "updated_at": "2026-01-02T00:00:00.000Z",
                        "tags": ["author_tag_chill"]
                      }
                    }
                    """);
            }

            if (path.Contains("/users/" + LoggedUserId, StringComparison.Ordinal))
            {
                return Json($$"""
                    {
                      "id": "{{LoggedUserId}}",
                      "displayName": "Logged Person",
                      "username": "logged",
                      "status": "join me",
                      "platform": "android",
                      "tags": ["system_trust_known", "language_eng"]
                    }
                    """);
            }

            if (path.Contains("/users/" + OwnerUserId, StringComparison.Ordinal))
            {
                return Json($$"""
                    {
                      "id": "{{OwnerUserId}}",
                      "displayName": "Owner Person",
                      "username": "owner"
                    }
                    """);
            }

            return Json("{}");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
