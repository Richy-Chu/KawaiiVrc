using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class LaunchServiceTests
{
    private const string WorldId = "wrld_4432ea9b-729c-46e3-8eaf-846f7706d3d5";

    [Theory]
    [InlineData(WorldId + ":12345")]
    [InlineData(WorldId + ":12345~private(usr_61cc28f3-76ea-41b4-a9da-ede327ef3703)~region(us)")]
    [InlineData(WorldId + ":12345~group(grp_1a2b3c4d-5e6f-7890-abcd-ef1234567890)~groupAccessType(public)")]
    [InlineData(WorldId + ":12345~hidden(usr_61cc28f3-76ea-41b4-a9da-ede327ef3703)")]
    public void IsLocation_AcceptsVrchatInstances(string location)
    {
        Assert.True(LaunchService.IsLocation(location));
        Assert.Equal(WorldId, LaunchService.GetWorldIdFromLocation(location));
        Assert.NotNull(LaunchService.GetInstanceIdFromLocation(location));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("wrld_:12345")]
    [InlineData("wrld_not-a-uuid:12345")]
    [InlineData(WorldId)]
    [InlineData(WorldId + ":")]
    [InlineData(WorldId + ":12345&ref=evil")]
    [InlineData(WorldId + ":12345?x=1")]
    [InlineData(WorldId + ":12345 #frag")]
    [InlineData(WorldId + ":12\n345")]
    [InlineData(WorldId + ":12345\" --no-vr")]
    [InlineData("wrld_4432ea9b-729c-46e3-8eaf-846f7706d3d5x:12345")]
    public void IsLocation_RejectsMalformedOrInjectedLocations(string? location)
    {
        Assert.False(LaunchService.IsLocation(location));
        Assert.Null(LaunchService.GetWorldIdFromLocation(location));
        Assert.Null(LaunchService.GetInstanceIdFromLocation(location));
    }

    [Fact]
    public void BuildLaunchUri_KeepsTheLocationReadable()
    {
        var location = WorldId + ":12345~region(us)~private(usr_61cc28f3-76ea-41b4-a9da-ede327ef3703)";

        var uri = LaunchService.BuildLaunchUri(location);

        Assert.Equal($"vrchat://launch?ref=vrchat.com&id={location}", uri, StringComparer.Ordinal);
        // Readable separators, no percent-encoding of the id.
        Assert.Contains("wrld_4432ea9b", uri, StringComparison.Ordinal);
        Assert.Contains("~region(us)", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("%", uri, StringComparison.Ordinal);
        // Still exactly one '&': the ref/id separator the launcher expects.
        Assert.Equal(1, uri.Count(c => c == '&'));
    }

    [Fact]
    public void PrivateInstancesCarryTheirShortNameIntoTheLaunchUri()
    {
        var location = WorldId + ":12345~private(usr_61cc28f3-76ea-41b4-a9da-ede327ef3703)~region(us)";

        var uri = LaunchService.BuildLaunchUri(location, "abc123");

        Assert.Equal(
            $"vrchat://launch?ref=vrchat.com&id={location}&shortName=abc123",
            uri,
            StringComparer.Ordinal);
        Assert.True(LaunchService.NeedsShortName(location));
        Assert.False(LaunchService.NeedsShortName(WorldId + ":12345~region(us)"));
        Assert.False(LaunchService.NeedsShortName(
            WorldId + ":12345~group(grp_1)~groupAccessType(public)"));
    }

    [Theory]
    [InlineData("https://vrchat.com/home/avatar/avtr_1")]
    [InlineData("http://example.test/path?x=1")]
    [InlineData("HTTPS://VRChat.com/home/user/usr_1")]
    public void IsWebUrl_AcceptsHttpAndHttps(string url)
    {
        Assert.True(LaunchService.IsWebUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vrchat://launch?id=wrld_x:1")]
    [InlineData("ftp://example.test/file")]
    [InlineData("C:\\Windows\\notepad.exe")]
    public void IsWebUrl_RejectsOtherSchemes(string? url)
    {
        Assert.False(LaunchService.IsWebUrl(url));
    }

    [Fact]
    public void LaunchProfilesRoundTripThroughSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VrcCompanion.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var profile = new LaunchProfile
            {
                Name = "Desktop 1440p",
                Arguments = "--fps=120 -screen-width 2560 -screen-height 1440"
            };
            store.Settings.LaunchProfiles.Add(profile);
            store.Settings.ActiveLaunchProfileId = profile.Id;
            store.Save();

            var reopened = new SettingsStore(path);
            var saved = Assert.Single(reopened.Settings.LaunchProfiles);
            Assert.Equal("Desktop 1440p", saved.Name);
            Assert.Equal("--fps=120 -screen-width 2560 -screen-height 1440", saved.Arguments);
            Assert.Equal(saved.Id, reopened.Settings.ActiveLaunchProfileId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--profile=1 --fps=120")]
    [InlineData("--no-vr -screen-fullscreen 1 -screen-width 1920 -screen-height 1080 -monitor 1")]
    [InlineData("--midi=\"My Controller\"")]
    public void ExtraArguments_AcceptValidLaunchOptions(string? arguments)
    {
        Assert.Null(LaunchService.ValidateExtraArguments(arguments));
    }

    [Theory]
    [InlineData("--fps=120\n--profile=1")]
    [InlineData("vrchat://launch?id=wrld_test")]
    [InlineData("--midi=\"unclosed")]
    public void ExtraArguments_RejectCommandBreakingValues(string arguments)
    {
        Assert.NotNull(LaunchService.ValidateExtraArguments(arguments));
    }

    [Fact]
    public void LaunchClient_AppendsASelectedProfileAfterTheModeArguments()
    {
        const string handler = @"C:\VRChat\launch.exe";
        var launched = new List<string>();
        TestHooks.UseHandlerPathProvider(() => handler);
        TestHooks.UseHandlerLauncher((_, arguments) => launched.Add(arguments));
        try
        {
            LaunchService.LaunchClient(LaunchMode.Desktop, "--fps=120 --profile=1");
            LaunchService.LaunchClient(LaunchMode.Vr, "--fps=90");

            Assert.Equal(2, launched.Count);
            Assert.Equal(
                "\"vrchat://launch/\" --no-vr --fps=120 --profile=1",
                launched[0],
                StringComparer.Ordinal);
            Assert.Equal(
                "\"vrchat://launch/\" --fps=90",
                launched[1],
                StringComparer.Ordinal);
        }
        finally
        {
            TestHooks.UseHandlerPathProvider(null);
            TestHooks.UseHandlerLauncher(null);
        }
    }

    [Fact]
    public void Launch_RejectsInvalidLocationBeforeStartingAnything()
    {
        var started = new List<string>();
        TestHooks.UseUriLauncher(started.Add);
        TestHooks.UseHandlerPathProvider(() => null);
        try
        {
            Assert.Throws<ArgumentException>(() => LaunchService.Launch(WorldId + ":12345&ref=evil"));
            Assert.Throws<ArgumentException>(() => LaunchService.Launch("not-a-location"));
            Assert.Throws<ArgumentException>(() => LaunchService.Launch(string.Empty));
            Assert.Empty(started);

            // A valid location reaches the launcher exactly once, unescaped.
            var location = WorldId + ":12345~region(us)";
            LaunchService.Launch(location);

            var uri = Assert.Single(started);
            Assert.Equal($"vrchat://launch?ref=vrchat.com&id={location}", uri, StringComparer.Ordinal);
        }
        finally
        {
            TestHooks.UseUriLauncher(null);
            TestHooks.UseHandlerPathProvider(null);
        }
    }

    [Fact]
    public void VrLaunch_BypassesAHandlerRegisteredWithNoVr()
    {
        const string handler = @"C:\VRChat\launch.exe";
        var launched = new List<(string Handler, string Arguments)>();
        var uriLaunches = new List<string>();
        TestHooks.UseHandlerPathProvider(() => handler);
        TestHooks.UseHandlerLauncher((path, arguments) => launched.Add((path, arguments)));
        TestHooks.UseUriLauncher(uriLaunches.Add);
        try
        {
            LaunchService.LaunchClient(LaunchMode.Vr);

            var call = Assert.Single(launched);
            Assert.Equal(handler, call.Handler, StringComparer.Ordinal);
            Assert.Equal("\"vrchat://launch/\"", call.Arguments, StringComparer.Ordinal);
            Assert.DoesNotContain("--no-vr", call.Arguments, StringComparison.Ordinal);
            Assert.Empty(uriLaunches);
        }
        finally
        {
            TestHooks.UseHandlerPathProvider(null);
            TestHooks.UseHandlerLauncher(null);
            TestHooks.UseUriLauncher(null);
        }
    }

    [Fact]
    public void DesktopLaunch_AddsNoVrToTheDirectHandlerCall()
    {
        const string handler = @"C:\VRChat\launch.exe";
        var launched = new List<(string Handler, string Arguments)>();
        TestHooks.UseHandlerPathProvider(() => handler);
        TestHooks.UseHandlerLauncher((path, arguments) => launched.Add((path, arguments)));
        try
        {
            var location = WorldId + ":12345~region(us)";
            LaunchService.Launch(location, LaunchMode.Desktop);

            var call = Assert.Single(launched);
            Assert.Equal(handler, call.Handler, StringComparer.Ordinal);
            Assert.Equal(
                $"\"vrchat://launch?ref=vrchat.com&id={location}\" --no-vr",
                call.Arguments,
                StringComparer.Ordinal);
        }
        finally
        {
            TestHooks.UseHandlerPathProvider(null);
            TestHooks.UseHandlerLauncher(null);
        }
    }

    [Fact]
    public async Task OpenLocation_AttachesToRunningGameInsteadOfStartingAnotherProcess()
    {
        const string handler = @"C:\VRChat\launch.exe";
        var attached = new List<string>();
        var launched = new List<(string Handler, string Arguments)>();
        var uriLaunches = new List<string>();
        TestHooks.UseGameRunningProbe(() => true);
        TestHooks.UseRunningGameLauncher((uri, _) =>
        {
            attached.Add(uri);
            return Task.FromResult(true);
        });
        TestHooks.UseHandlerPathProvider(() => handler);
        TestHooks.UseHandlerLauncher((path, arguments) => launched.Add((path, arguments)));
        TestHooks.UseUriLauncher(uriLaunches.Add);
        try
        {
            var location = WorldId + ":12345~private(usr_61cc28f3-76ea-41b4-a9da-ede327ef3703)";
            var outcome = await LaunchService.OpenLocationAsync(location, LaunchMode.Desktop, "abc123");

            Assert.Equal(LaunchOutcome.AttachedToRunningGame, outcome);
            var uri = Assert.Single(attached);
            Assert.Contains($"id={location}", uri, StringComparison.Ordinal);
            Assert.Contains("shortName=abc123", uri, StringComparison.Ordinal);
            Assert.DoesNotContain("attach=1", uri, StringComparison.Ordinal);
            Assert.Empty(launched);
            Assert.Empty(uriLaunches);
        }
        finally
        {
            TestHooks.UseGameRunningProbe(null);
            TestHooks.UseRunningGameLauncher(null);
            TestHooks.UseHandlerPathProvider(null);
            TestHooks.UseHandlerLauncher(null);
            TestHooks.UseUriLauncher(null);
        }
    }

    [Theory]
    [InlineData(LaunchMode.Desktop, true)]
    [InlineData(LaunchMode.Vr, false)]
    public async Task OpenLocation_StartsTheGameWithTheGlobalLaunchModeWhenItIsNotRunning(
        LaunchMode mode,
        bool expectDesktopFlag)
    {
        const string handler = @"C:\VRChat\launch.exe";
        var launched = new List<(string Handler, string Arguments)>();
        var attached = new List<string>();
        TestHooks.UseGameRunningProbe(() => false);
        TestHooks.UseRunningGameLauncher((uri, _) =>
        {
            attached.Add(uri);
            return Task.FromResult(true);
        });
        TestHooks.UseHandlerPathProvider(() => handler);
        TestHooks.UseHandlerLauncher((path, arguments) => launched.Add((path, arguments)));
        try
        {
            var location = WorldId + ":12345~region(us)";
            var outcome = await LaunchService.OpenLocationAsync(location, mode);

            Assert.Equal(LaunchOutcome.GameStarted, outcome);
            var call = Assert.Single(launched);
            Assert.Equal(handler, call.Handler, StringComparer.Ordinal);
            Assert.Contains($"id={location}", call.Arguments, StringComparison.Ordinal);
            Assert.Equal(expectDesktopFlag, call.Arguments.Contains("--no-vr", StringComparison.Ordinal));
            Assert.DoesNotContain("attach=1", call.Arguments, StringComparison.Ordinal);
            Assert.Empty(attached);
        }
        finally
        {
            TestHooks.UseGameRunningProbe(null);
            TestHooks.UseRunningGameLauncher(null);
            TestHooks.UseHandlerPathProvider(null);
            TestHooks.UseHandlerLauncher(null);
        }
    }

    [Fact]
    public async Task OpenLocation_DoesNotColdStartWhenTheRunningGameRejectsTheAttach()
    {
        const string handler = @"C:\VRChat\launch.exe";
        var launched = new List<(string Handler, string Arguments)>();
        TestHooks.UseGameRunningProbe(() => true);
        TestHooks.UseRunningGameLauncher((_, _) => Task.FromResult(false));
        TestHooks.UseHandlerPathProvider(() => handler);
        TestHooks.UseHandlerLauncher((path, arguments) => launched.Add((path, arguments)));
        try
        {
            var outcome = await LaunchService.OpenLocationAsync(WorldId + ":12345~region(us)");

            Assert.Equal(LaunchOutcome.AttachFailed, outcome);
            Assert.Empty(launched);
        }
        finally
        {
            TestHooks.UseGameRunningProbe(null);
            TestHooks.UseRunningGameLauncher(null);
            TestHooks.UseHandlerPathProvider(null);
            TestHooks.UseHandlerLauncher(null);
        }
    }

    [Fact]
    public void OpenFolder_SelectsTheFileOrFallsBackToItsFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "VrcCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "avatar.zip");
        var started = new List<string>();
        TestHooks.UseFolderLauncher(started.Add);
        try
        {
            Assert.False(LaunchService.OpenFolder(null));
            Assert.False(LaunchService.OpenFolder(Path.Combine(folder, "missing", "avatar.zip")));
            Assert.Empty(started);

            Assert.True(LaunchService.OpenFolder(file));
            Assert.Equal($"\"{folder}\"", Assert.Single(started));

            started.Clear();
            File.WriteAllText(file, "avatar");
            Assert.True(LaunchService.OpenFolder(file));
            Assert.Equal($"/select,\"{file}\"", Assert.Single(started));
        }
        finally
        {
            TestHooks.UseFolderLauncher(null);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void OpenUrl_DoesNotLaunchNonWebSchemes()
    {
        Assert.False(LaunchService.OpenUrl("file:///C:/Windows/System32/calc.exe"));
        Assert.False(LaunchService.OpenUrl("ms-msdt:/id PCWDiagnostic"));
        Assert.False(LaunchService.OpenUrl(null));
    }
}
