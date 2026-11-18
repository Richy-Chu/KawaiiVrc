using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.FlatBuffers;
using solarxr_protocol;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class ChatboxProviderPipelineTests
{
    [Fact]
    public void ProvidersComposeInSavedOrderAndPreserveThePrefixAndSuffix()
    {
        var track = new NowPlayingInfo(
            "Show",
            "Ado",
            "Spotify",
            Duration: TimeSpan.FromMinutes(4));

        var providers = new IChatboxProvider[]
        {
            new StatusChatboxProvider(),
            new MediaChatboxProvider(),
            new TimeChatboxProvider()
        };
        var context = new ChatboxProviderContext
        {
            IncludeStatus = true,
            IncludeMedia = true,
            IncludeTime = true,
            ActiveStatus = "back in 5",
            ShowStatusIcon = true,
            StatusIcon = "💭",
            Track = track,
            Now = new DateTime(2026, 11, 13, 21, 41, 0),
            TimeFormat = "{time}"
        };

        var result = new ChatboxOutputBuilder().Build(
            providers,
            context,
            " ┆ ",
            separateWithEnters: false,
            prefix: "[",
            suffix: "]");

        Assert.Equal("[💭 back in 5 ┆ ▶ Ado — Show · Spotify ┆ 9:41 PM]", result.Message);
        Assert.Equal(new[] { "Status", "Media", "Time" }, result.IncludedProviders);
        Assert.Empty(result.TrimmedProviders);
    }

    [Fact]
    public void OverflowDropsTheHighestPriorityNumberFirstThenClipsTheSurvivor()
    {
        var providers = new IChatboxProvider[]
        {
            new FixedProvider("Status", 10, "keep this"),
            new FixedProvider("Media", 20, new string('m', 100)),
            new FixedProvider("Time", 90, "late clock")
        };
        var result = new ChatboxOutputBuilder().Build(
            providers,
            new ChatboxProviderContext { Limit = 60 },
            " ┆ ",
            separateWithEnters: false,
            prefix: string.Empty,
            suffix: string.Empty);

        Assert.Contains("Time", result.TrimmedProviders);
        Assert.Contains("Media", result.TrimmedProviders);
        Assert.Equal("keep this", result.Message);
        Assert.True(result.ExceededLimit);

        var clipped = new ChatboxOutputBuilder().Build(
            new IChatboxProvider[] { new FixedProvider("Media", 20, new string('x', 200)) },
            new ChatboxProviderContext { Limit = 60 },
            " ┆ ",
            separateWithEnters: false,
            prefix: string.Empty,
            suffix: string.Empty);

        Assert.True(clipped.Message.Length <= 60);
        Assert.EndsWith("…", clipped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaProviderCarriesPlaybackStateSeekbarAndPausedState()
    {
        var provider = new MediaChatboxProvider();
        var context = new ChatboxProviderContext
        {
            IncludeMedia = true,
            MusicFormat = "{state} {display}{source} {progress}",
            Track = new NowPlayingInfo(
                "Show",
                "Ado",
                "Spotify",
                Position: TimeSpan.FromMinutes(1),
                Duration: TimeSpan.FromMinutes(4),
                IsPlaying: false)
        };

        var text = provider.Build(context);

        Assert.NotNull(text);
        Assert.StartsWith("⏸ Ado — Show · Spotify ▰", text, StringComparison.Ordinal);
        Assert.Contains("▱", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeProviderSupportsTwelveHourClockAndZoneTokens()
    {
        var provider = new TimeChatboxProvider();
        var context = new ChatboxProviderContext
        {
            IncludeTime = true,
            TimeFormat = "{time12} {zone}",
            Now = new DateTime(2026, 11, 13, 21, 41, 0)
        };

        var text = provider.Build(context);

        Assert.NotNull(text);
        Assert.Contains("9:41", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogCoversEveryMagicChatboxProviderInPriorityOrder()
    {
        var keys = ChatboxIntegrationCatalog.Keys;

        Assert.Equal(18, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("Status", keys[0]);
        Assert.Equal("Time", keys[^1]);
        Assert.Equal(
            keys.OrderBy(key => ChatboxIntegrationCatalog.Get(key).Priority),
            keys);
    }

    [Fact]
    public void SnapshotProvidersContributeOnlyWhenEnabledAndPopulated()
    {
        var provider = new SnapshotChatboxProvider("Weather", "Weather", 85);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Weather"] = "☀️ 21°C"
        };
        var context = new ChatboxProviderContext
        {
            IntegrationTexts = texts,
            EnabledIntegrations = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["Weather"] = true
            }
        };

        Assert.Equal("☀️ 21°C", provider.Build(context));

        context = new ChatboxProviderContext
        {
            IntegrationTexts = texts,
            EnabledIntegrations = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["Weather"] = false
            }
        };
        Assert.False(provider.IsEnabled(context));
        Assert.Null(provider.Build(context));
    }

    [Fact]
    public void SpotifyProviderSuppressesTheGenericMediaLine()
    {
        var providers = new IChatboxProvider[]
        {
            new MediaChatboxProvider(),
            new SnapshotChatboxProvider("Spotify", "Spotify", 25)
        };
        var context = new ChatboxProviderContext
        {
            IncludeMedia = true,
            SuppressSpotifyInMedia = true,
            Track = new NowPlayingInfo("Show", "Ado", "Spotify", Duration: TimeSpan.FromMinutes(4)),
            IntegrationTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Spotify"] = "▶ Ado — Show"
            },
            EnabledIntegrations = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["Spotify"] = true
            }
        };

        var result = new ChatboxOutputBuilder().Build(
            providers,
            context,
            " ┆ ",
            separateWithEnters: false,
            prefix: string.Empty,
            suffix: string.Empty);

        Assert.Equal("▶ Ado — Show", result.Message);
        Assert.Equal(new[] { "Spotify" }, result.IncludedProviders);
    }

    [Fact]
    public void DisablingAnIntegrationClearsItsStaleCardPreview()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.NewStatusText = "here";
        vm.AddStatusCommand.Execute(null);

        var time = vm.IntegrationCards.Single(card => card.Key == "Time");
        time.IsEnabled = true;
        Assert.True(time.HasPreview);

        time.IsEnabled = false;

        Assert.False(time.HasPreview);
        Assert.Equal(string.Empty, time.Preview);
        Assert.Equal("Off", time.StateText);
    }

    [Fact]
    public void VoicemodIsTheOnlyRemainingUnavailableIntegration()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();

        var voicemod = vm.IntegrationCards.Single(candidate => candidate.Key == "Voicemod");
        Assert.False(voicemod.IsAvailable);
        Assert.Equal("Unavailable", voicemod.StateText);
        voicemod.IsEnabled = true;
        Assert.False(voicemod.IsEnabled);

        foreach (var key in new[] { "Discord", "TrackerBattery", "VrPerformance" })
        {
            var card = vm.IntegrationCards.Single(candidate => candidate.Key == key);
            Assert.True(card.IsAvailable);
            card.IsEnabled = true;
            Assert.True(card.IsEnabled);
        }

        Assert.True(vm.IntegrationCards.Single(card => card.Key == "Soundpad").IsAvailable);
        Assert.True(vm.IntegrationCards.Single(card => card.Key == "TikTokLive").IsAvailable);
    }

    [Fact]
    public void DiscordVoiceSnapshotParsesWebViewJsonAndBuildsTheChatboxLine()
    {
        var snapshot = DiscordVoiceSnapshot.TryParse(
            "\"{\\\"channel\\\":\\\"Chill Lounge\\\",\\\"count\\\":3,\\\"speaking\\\":[\\\"Mika\\\",\\\"Ren\\\"]}\"");

        Assert.NotNull(snapshot);
        Assert.Equal("Chill Lounge", snapshot!.ChannelName);
        Assert.Equal(3, snapshot.MemberCount);
        Assert.Equal("🔊 Chill Lounge (3) | 🎙 Mika, Ren", snapshot.ToText());
        Assert.Equal("Chill Lounge 3/2 | Mika, Ren",
            snapshot.ToText("{channel} {count}/{speaking_count} | {speaking}"));
        Assert.Null(DiscordVoiceSnapshot.TryParse("null"));
    }

    [Fact]
    public void OpenVrFormatsUseTheSavedTemplates()
    {
        var snapshot = new OpenVrSnapshot(
            new[]
            {
                new OpenVrDeviceState("Valve Index", 82, false),
                new OpenVrDeviceState("Controller", 45, true, IsController: true),
                new OpenVrDeviceState("SlimeVR Tracker", null, false)
            },
            72.4,
            3,
            13.8);

        Assert.Equal(
            "🔋 Index 82% · ⚡ C1 45% · 🔋 T1 --%",
            snapshot.BuildTrackerText());
        Assert.Equal(
            "Index: 82 (battery) · C1: 45 (charging) · T1: -- (battery unknown)",
            snapshot.BuildTrackerText("{name}: {batt} ({status})"));
        Assert.Equal(
            "Valve Index: 82 · Controller: 45 · SlimeVR Tracker: --",
            snapshot.BuildTrackerText("{fullName}: {batt}"));
        Assert.Equal("🎮 72 FPS · 3 dropped · 13.8 ms",
            snapshot.BuildPerformanceText("🎮 {fps} FPS · {dropped} dropped · {frame_ms} ms"));
        Assert.Equal(string.Empty, new OpenVrSnapshot(Array.Empty<OpenVrDeviceState>(), 0, 0, 0)
            .BuildPerformanceText());
    }

    [Fact]
    public void ControllersWithoutBatteryAreOmittedFromTheChatboxLine()
    {
        var snapshot = new OpenVrSnapshot(
            new[]
            {
                new OpenVrDeviceState("HMD", 90, false),
                new OpenVrDeviceState("SlimeVR Tracker", 80, false, IsTracker: true),
                new OpenVrDeviceState("Controller", null, false, IsController: true),
                new OpenVrDeviceState("Controller", null, false, IsController: true)
            },
            72,
            0,
            13.8);

        Assert.Equal("🔋 HMD 90% · 🔋 T1 80%", snapshot.BuildTrackerText());
    }

    [Fact]
    public void FullVrRigBatteryLineFitsTheChatboxLimit()
    {
        var devices = new List<OpenVrDeviceState>
        {
            new("HMD", 90, false)
        };
        devices.AddRange(Enumerable.Range(1, 6)
            .Select(index => new OpenVrDeviceState(
                $"SlimeVR Virtual Tracker {index}", 80, false, IsTracker: true)));
        devices.Add(new OpenVrDeviceState("L", 75, false));
        devices.Add(new OpenVrDeviceState("R", 70, true));

        var text = new OpenVrSnapshot(devices, 90, 0, 11).BuildTrackerText();

        Assert.True(text.Length <= OscMessage.ChatboxLimit, $"Battery line was {text.Length} characters: {text}");
        Assert.Contains("HMD 90%", text, StringComparison.Ordinal);
        Assert.Contains("T6 80%", text, StringComparison.Ordinal);
        Assert.Contains("L 75%", text, StringComparison.Ordinal);
        Assert.Contains("R 70%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SlimeVrFeedProvidesPhysicalTrackerBattery()
    {
        var builder = new FlatBufferBuilder(512);
        Offset<TrackerInfo> CreateInfo(string? custom, string? display)
        {
            var customOffset = custom is null ? (StringOffset?)null : builder.CreateString(custom);
            var displayOffset = display is null ? (StringOffset?)null : builder.CreateString(display);
            TrackerInfo.StartTrackerInfo(builder);
            TrackerInfo.AddIsImu(builder, true);
            if (customOffset is { } customValue)
            {
                TrackerInfo.AddCustomName(builder, customValue);
            }

            if (displayOffset is { } displayValue)
            {
                TrackerInfo.AddDisplayName(builder, displayValue);
            }

            return TrackerInfo.EndTrackerInfo(builder);
        }

        var waistInfo = CreateInfo("Waist", null);
        var leftFootInfo = CreateInfo(null, "Left Foot");
        var trackers = DeviceData.CreateTrackersVector(
            builder,
            new[]
            {
                TrackerData.CreateTrackerData(builder, tracker_id: 1, infoOffset: waistInfo),
                TrackerData.CreateTrackerData(builder, tracker_id: 2, infoOffset: leftFootInfo)
            });
        var status = HardwareStatus.CreateHardwareStatus(
            builder,
            battery_pct_estimate: (byte)87);
        var device = DeviceData.CreateDeviceData(
            builder,
            id: 1,
            hardware_statusOffset: status,
            trackersOffset: trackers);
        var devices = DataFeedUpdate.CreateDevicesVector(builder, new[] { device });
        var update = DataFeedUpdate.CreateDataFeedUpdate(builder, devicesOffset: devices);
        var header = DataFeedMessageHeader.CreateDataFeedMessageHeader(
            builder,
            DataFeedMessage.DataFeedUpdate,
            update.Value);
        var headers = MessageBundle.CreateDataFeedMsgsVector(builder, new[] { header });
        var bundle = MessageBundle.CreateMessageBundle(builder, headers);
        builder.Finish(bundle.Value);

        var parsed = SlimeVrBatteryService.ParseBundle(builder.SizedByteArray());

        Assert.Equal("Waist", parsed[0].Name);
        Assert.Equal(87, parsed[0].Percent);
        Assert.Equal("Left Foot", parsed[1].Name);
        Assert.Equal(87, parsed[1].Percent);
        Assert.Equal(2, parsed.Count);
    }

    [Fact]
    public void SlimeVrRequestAsksForTrackerInfoAndStatus()
    {
        var request = SlimeVrBatteryService.BuildStartFeedRequest();
        var bundle = MessageBundle.GetRootAsMessageBundle(new ByteBuffer(request));
        Assert.Equal(1, bundle.DataFeedMsgsLength);
        var header = bundle.DataFeedMsgs(0);
        Assert.NotNull(header);
        Assert.Equal(DataFeedMessage.StartDataFeed, header!.Value.MessageType);
        var start = header.Value.MessageAsStartDataFeed();
        Assert.Equal(1, start.DataFeedsLength);
        var config = start.DataFeeds(0);
        Assert.NotNull(config);
        Assert.True(config!.Value.DataMask?.TrackerData?.Info);
        Assert.True(config.Value.DataMask?.TrackerData?.Status);
        Assert.True(config.Value.DataMask?.DeviceData);
    }

    [Fact]
    public void OpenVrDiagnosticsExplainMissingBatteryProperties()
    {
        var snapshot = new OpenVrSnapshot(
            Array.Empty<OpenVrDeviceState>(), 0, 0, 0,
            new[]
            {
                new OpenVrDeviceDiagnostic(
                    Index: 1,
                    DeviceClass: "Controller",
                    Name: "L",
                    ModelNumber: "Oculus Touch",
                    Connected: true,
                    ProvidesBatteryStatus: false,
                    BatteryPropertyStatus: "TrackedProp_UnknownProperty",
                    BatteryPercent: null,
                    Charging: null)
            });

        var text = snapshot.BuildBatteryDiagnosticText();

        Assert.Contains("L [Controller] connected", text, StringComparison.Ordinal);
        Assert.Contains("battery unavailable (TrackedProp_UnknownProperty)", text, StringComparison.Ordinal);
        Assert.Contains("advertises=false", text, StringComparison.Ordinal);
        Assert.Contains("Oculus Touch", text, StringComparison.Ordinal);
    }

    [Fact]
    public void KawaiiAccentAcceptsCustomHexAndRejectsInvalidInput()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();

        vm.KawaiiAccentInput = "e85d9e";
        vm.ApplyKawaiiAccentCommand.Execute(null);

        Assert.Equal("#E85D9E", vm.KawaiiAccentHex);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xE8, 0x5D, 0x9E), vm.KawaiiAccentColor);
        Assert.Contains(vm.KawaiiAccentPresets, preset => preset.Hex == "#E85D9E");

        vm.KawaiiAccentInput = "not-a-color";
        vm.ApplyKawaiiAccentCommand.Execute(null);

        Assert.Equal("#E85D9E", vm.KawaiiAccentHex);
        Assert.Contains("hex color", vm.KawaiiAccentFeedback, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IntegrationRouteSwitchesFollowTheRunningOutputMode()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.NewStatusText = "here";
        vm.AddStatusCommand.Execute(null);
        vm.SetVrModeForTest(false);

        var time = vm.IntegrationCards.Single(card => card.Key == "Time");
        time.IsEnabled = true;
        var withTime = vm.PreviewLine;

        time.UseInDesktop = false;

        Assert.False(time.UseInDesktop);
        Assert.Equal("VR only", time.ModeText);
        Assert.NotEqual(withTime, vm.PreviewLine);

        time.UseInDesktop = true;

        Assert.Equal(withTime, vm.PreviewLine);
    }

    [Fact]
    public void NetworkRateFormattingUsesReadableUnits()
    {
        Assert.Equal("↓ 12.5 Mbps ↑ 1.2 Mbps", new NetworkStatsSnapshot(12_500_000, 1_200_000).Text);
        Assert.Equal("↓ 2 Kbps ↑ 0 bps", new NetworkStatsSnapshot(2_000, 0).Text);
    }

    [Fact]
    public void ComponentStatsIncludeGpuAndVramWhenAvailable()
    {
        var snapshot = new SystemStatsSnapshot(
            CpuPercent: 34,
            MemoryPercent: 41,
            GpuPercent: 62,
            VramUsedGb: 4.2,
            MemoryUsedGb: 13.1,
            MemoryTotalGb: 31.9);

        Assert.Equal("CPU 34% ¦ GPU 62% ¦ VRAM 4.2 GB ¦ RAM 41%", snapshot.Text);

        var withoutGpu = new SystemStatsSnapshot(34, 41);
        Assert.Equal("CPU 34% ¦ RAM 41%", withoutGpu.Text);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _settingsPath =
            Path.Combine(Path.GetTempPath(), $"VrcCompanion-pipeline-{Guid.NewGuid():N}.json");

        public ChatboxViewModel CreateViewModel()
            => new(new SettingsStore(_settingsPath), new FakeSender(), startTimers: false);

        public void Dispose()
        {
            if (File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }
        }
    }

    private sealed class FakeSender : IChatboxSender
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 9000;

        public void SendChatbox(string text, bool sendImmediately = true, bool playSound = false)
        {
        }

        public void SendTyping(bool typing)
        {
        }
    }

    private sealed class FixedProvider : IChatboxProvider
    {
        private readonly string _text;

        public FixedProvider(string key, int priority, string text)
        {
            Key = key;
            DisplayName = key;
            Priority = priority;
            _text = text;
        }

        public string Key { get; }

        public string DisplayName { get; }

        public int Priority { get; }

        public bool IsEnabled(ChatboxProviderContext context) => true;

        public string? Build(ChatboxProviderContext context) => _text;
    }
}

public class ChatboxStatusGroupTests
{
    [Fact]
    public void FilteringAndCycleGroupsFollowMagicChatboxRules()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();

        vm.NewGroupName = "AFK";
        vm.AddStatusGroupCommand.Execute(null);
        var afk = Assert.Single(vm.StatusGroups);

        vm.NewStatusText = "back soon";
        vm.AddStatusCommand.Execute(null);
        var grouped = vm.StatusItems.Single();
        Assert.Equal(afk.Id, grouped.GroupId);

        vm.ShowAllStatusesCommand.Execute(null);
        vm.NewStatusText = "listening";
        vm.ChatText = string.Empty;
        vm.AddStatusCommand.Execute(null);
        var ungrouped = vm.StatusItems.Last();
        Assert.Null(ungrouped.GroupId);

        Assert.Equal(2, vm.FilteredStatusItems.Count);
        vm.SelectStatusGroupCommand.Execute(afk);
        Assert.Single(vm.FilteredStatusItems);
        Assert.Same(grouped, vm.FilteredStatusItems[0]);

        vm.ShowAllStatusesCommand.Execute(null);
        afk.IsActiveForCycle = false;
        vm.ToggleStatusGroupCycleCommand.Execute(afk);
        Assert.True(afk.IsActiveForCycle);

        afk.IsActiveForCycle = false;
        vm.UseStatusCommand.Execute(ungrouped);
        vm.CycleStatusForTest();

        Assert.Same(ungrouped, vm.ActiveStatus);
    }

    [Fact]
    public void IntegrationCardsControlOrderTogglesAndTrimReporting()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.NewStatusText = "here";
        vm.AddStatusCommand.Execute(null);

        Assert.Equal(ChatboxIntegrationCatalog.Keys, vm.IntegrationCards.Select(card => card.Key));
        Assert.Equal(18, vm.IntegrationCards.Count);

        var time = vm.IntegrationCards.Single(card => card.Key == "Time");
        vm.MoveIntegrationUpCommand.Execute(time);

        Assert.Equal("Time", vm.IntegrationCards[16].Key);
        Assert.Equal("17", time.OrderText);

        var media = vm.IntegrationCards.Single(card => card.Key == "Media");
        media.IsEnabled = true;
        Assert.True(vm.IncludeMusic);
        Assert.Equal("💭 here", vm.PreviewLine);

        media.IsEnabled = false;
        Assert.False(vm.IncludeMusic);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _settingsPath =
            Path.Combine(Path.GetTempPath(), $"VrcCompanion-pipeline-{Guid.NewGuid():N}.json");

        public ChatboxViewModel CreateViewModel()
            => new(new SettingsStore(_settingsPath), new FakeSender(), startTimers: false);

        public void Dispose()
        {
            if (File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }
        }
    }

    private sealed class FakeSender : IChatboxSender
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 9000;
        public void SendChatbox(string text, bool sendImmediately = true, bool playSound = false)
        {
        }

        public void SendTyping(bool typing)
        {
        }
    }
}
