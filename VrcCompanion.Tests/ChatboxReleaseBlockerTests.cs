using System;
using System.IO;
using System.Linq;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class ChatboxReleaseBlockerTests : IDisposable
{
    private readonly string _settingsPath =
        Path.Combine(Path.GetTempPath(), $"VrcCompanion-secrets-{Guid.NewGuid():N}.json");

    [Fact]
    public void ProviderTokensAreEncryptedAtRestAndRoundTrip()
    {
        using var first = new ChatboxViewModel(new SettingsStore(_settingsPath), new FakeSender(), startTimers: false);
        first.PulsoidToken = "pulsoid-secret-value";
        first.TwitchAccessToken = "twitch-secret-value";

        var json = File.ReadAllText(_settingsPath);
        Assert.DoesNotContain("pulsoid-secret-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("twitch-secret-value", json, StringComparison.Ordinal);
        Assert.Contains("dpapi:v1:", json, StringComparison.Ordinal);

        using var second = new ChatboxViewModel(new SettingsStore(_settingsPath), new FakeSender(), startTimers: false);
        Assert.Equal("pulsoid-secret-value", second.PulsoidToken);
        Assert.Equal("twitch-secret-value", second.TwitchAccessToken);
    }

    [Fact]
    public void LegacyPlaintextTokensAreRewrittenEncryptedOnLoad()
    {
        File.WriteAllText(_settingsPath, """
            {
              "chatboxPulsoidToken": "legacy-pulsoid-value",
              "chatboxTwitchAccessToken": "legacy-twitch-value"
            }
            """);

        var store = new SettingsStore(_settingsPath);

        Assert.Equal("legacy-pulsoid-value", store.Settings.ChatboxPulsoidToken);
        Assert.Equal("legacy-twitch-value", store.Settings.ChatboxTwitchAccessToken);
        var rewritten = File.ReadAllText(_settingsPath);
        Assert.DoesNotContain("legacy-pulsoid-value", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-twitch-value", rewritten, StringComparison.Ordinal);
        Assert.Contains("dpapi:v1:", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyRotationStatusAndLyricsSettingsAreMigrated()
    {
        File.WriteAllText(_settingsPath, """
            {
              "chatboxRotationSeconds": 42,
              "chatboxRotateEnabled": true,
              "chatboxMessages": ["back soon", "{music}", "{lyrics}", "listening"],
              "chatboxIncludeNowPlaying": true,
              "chatboxShowLyrics": true,
              "chatboxLyricsOnVrchat": true,
              "chatboxPauseLyricsAfterMessage": true,
              "chatboxLyricsPauseSeconds": 90,
              "chatboxHistory": ["old history message"],
              "chatboxMusicFormat": "CUSTOM {display}"
            }
            """);

        var store = new SettingsStore(_settingsPath);
        using var chatbox = new ChatboxViewModel(store, new FakeSender(), startTimers: false);

        Assert.True(chatbox.CycleStatus);
        Assert.Equal(42, chatbox.CycleSeconds);
        Assert.True(chatbox.IncludeMusic);
        Assert.True(chatbox.IncludeLyrics);
        Assert.Equal(90, chatbox.ChatVisibleSeconds);
        Assert.Equal("CUSTOM {display}", chatbox.MusicFormat);
        Assert.Equal(
            new[] { "back soon", "listening" },
            chatbox.StatusItems.Select(item => item.Message));
        Assert.All(chatbox.StatusItems, item => Assert.True(item.UseInCycle));
        Assert.Contains("old history message", chatbox.ChatHistory.Select(item => item.Message));

        var rewritten = File.ReadAllText(_settingsPath);
        Assert.DoesNotContain("chatboxMessages", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("chatboxRotateEnabled", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("chatboxShowLyrics", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("chatboxLyricsPauseSeconds", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledLegacyLyricsPauseUsesTheShortestChatWindow()
    {
        File.WriteAllText(_settingsPath, """
            {
              "chatboxPauseLyricsAfterMessage": false,
              "chatboxLyricsPauseSeconds": 120
            }
            """);

        var store = new SettingsStore(_settingsPath);
        using var chatbox = new ChatboxViewModel(store, new FakeSender(), startTimers: false);

        Assert.Equal(1, chatbox.ChatVisibleSeconds);
    }

    public void Dispose()
    {
        if (File.Exists(_settingsPath))
        {
            File.Delete(_settingsPath);
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