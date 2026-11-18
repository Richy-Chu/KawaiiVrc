using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class LaunchModeFlowTests
{
    private const string Location =
        "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345~region(eu)";

    [Fact]
    public void FriendJoinUsesTheGlobalDefaultLaunchMode()
    {
        RunOnSta(() =>
        {
            using var api = new VrcApiClient();
            var shell = new MainViewModel(api);
            TestHooks.SetLoggedIn(shell);
            var originalMode = shell.DefaultLaunchMode;
            var launched = new ManualResetEventSlim();
            var arguments = string.Empty;
            const string handler = @"C:\VRChat\launch.exe";

            try
            {
                shell.DefaultLaunchMode = LaunchMode.Desktop;
                TestHooks.UseGameRunningProbe(() => false);
                TestHooks.UseHandlerPathProvider(() => handler);
                TestHooks.UseHandlerLauncher((_, args) =>
                {
                    arguments = args;
                    launched.Set();
                });

                var friend = new FriendItem(new VrcUser
                {
                    Id = "usr_launch_friend",
                    DisplayName = "Launch Friend",
                    Username = "launchfriend",
                    Status = "active",
                    Location = Location,
                    IsFriend = true
                });

                shell.JoinFriendCommand.Execute(friend);

                Assert.True(launched.Wait(TimeSpan.FromSeconds(5)), "The friend join never reached the launch handler.");
                Assert.Contains("--no-vr", arguments, StringComparison.Ordinal);
            }
            finally
            {
                TestHooks.UseGameRunningProbe(null);
                TestHooks.UseHandlerPathProvider(null);
                TestHooks.UseHandlerLauncher(null);
                shell.DefaultLaunchMode = originalMode;
            }
        });
    }

    [Fact]
    public void InstanceHistoryJoinUsesTheGlobalDefaultLaunchMode()
    {
        RunOnSta(() =>
        {
            using var api = new VrcApiClient();
            var shell = new MainViewModel(api);
            var originalMode = shell.DefaultLaunchMode;
            var launched = new ManualResetEventSlim();
            var arguments = string.Empty;
            const string handler = @"C:\VRChat\launch.exe";

            try
            {
                shell.DefaultLaunchMode = LaunchMode.Desktop;
                TestHooks.UseGameRunningProbe(() => false);
                TestHooks.UseHandlerPathProvider(() => handler);
                TestHooks.UseHandlerLauncher((_, args) =>
                {
                    arguments = args;
                    launched.Set();
                });

                var entry = new InstanceHistoryEntry
                {
                    Id = "history-1",
                    AccountUserId = "usr_launch_me",
                    Location = Location,
                    WorldId = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd",
                    InstanceId = "12345~region(eu)",
                    WorldName = "Launch World",
                    JoinedAt = DateTimeOffset.Now.AddMinutes(-15),
                    LeftAt = DateTimeOffset.Now.AddMinutes(-5),
                    LastSeenAt = DateTimeOffset.Now.AddMinutes(-5)
                };
                var item = new InstanceHistoryItem(
                    entry,
                    entry.AccountUserId,
                    null,
                    null,
                    _ => Task.CompletedTask,
                    _ => { },
                    _ => { },
                    _ => { },
                    null);

                shell.OpenHistoryLaunch(item);

                Assert.True(launched.Wait(TimeSpan.FromSeconds(5)), "The history join never reached the launch handler.");
                Assert.Contains("--no-vr", arguments, StringComparison.Ordinal);
            }
            finally
            {
                TestHooks.UseGameRunningProbe(null);
                TestHooks.UseHandlerPathProvider(null);
                TestHooks.UseHandlerLauncher(null);
                shell.DefaultLaunchMode = originalMode;
            }
        });
    }

    [Fact]
    public void ActiveLaunchProfileArgumentsApplyToFriendJoin()
    {
        RunOnSta(() =>
        {
            using var api = new VrcApiClient();
            var shell = new MainViewModel(api);
            TestHooks.SetLoggedIn(shell);
            var originalMode = shell.DefaultLaunchMode;
            var originalActive = shell.LaunchProfiles.FirstOrDefault(profile =>
                string.Equals(profile.DisplayName, shell.ActiveLaunchProfileName, StringComparison.Ordinal))
                ?? shell.LaunchProfiles[0];
            var launched = new ManualResetEventSlim();
            var arguments = string.Empty;
            const string handler = @"C:\VRChat\launch.exe";
            LaunchProfileItem? created = null;

            try
            {
                shell.OpenAdvancedLaunchCommand.Execute(null);
                shell.NewLaunchProfileCommand.Execute(null);
                created = shell.SelectedLaunchProfile;
                Assert.NotNull(created);
                shell.LaunchProfileName = "Test launch profile";
                shell.LaunchProfileArguments = "--fps=121 --profile=1";
                shell.UseLaunchProfileCommand.Execute(null);
                Assert.Equal("Test launch profile", shell.ActiveLaunchProfileName);

                shell.DefaultLaunchMode = LaunchMode.Desktop;
                TestHooks.UseGameRunningProbe(() => false);
                TestHooks.UseHandlerPathProvider(() => handler);
                TestHooks.UseHandlerLauncher((_, args) =>
                {
                    arguments = args;
                    launched.Set();
                });

                var friend = new FriendItem(new VrcUser
                {
                    Id = "usr_launch_friend",
                    DisplayName = "Launch Friend",
                    Username = "launchfriend",
                    Status = "active",
                    Location = Location,
                    IsFriend = true
                });

                shell.JoinFriendCommand.Execute(friend);

                Assert.True(launched.Wait(TimeSpan.FromSeconds(5)), "The friend join never reached the launch handler.");
                Assert.Contains("--no-vr", arguments, StringComparison.Ordinal);
                Assert.Contains("--fps=121", arguments, StringComparison.Ordinal);
                Assert.Contains("--profile=1", arguments, StringComparison.Ordinal);
            }
            finally
            {
                TestHooks.UseGameRunningProbe(null);
                TestHooks.UseHandlerPathProvider(null);
                TestHooks.UseHandlerLauncher(null);
                if (created is not null && shell.LaunchProfiles.Contains(created))
                {
                    shell.SelectedLaunchProfile = created;
                    shell.DeleteLaunchProfileCommand.Execute(null);
                }

                shell.SelectedLaunchProfile = originalActive;
                shell.UseLaunchProfileCommand.Execute(null);
                shell.DefaultLaunchMode = originalMode;
            }
        });
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
        thread.Join();
        failure?.Throw();
    }
}