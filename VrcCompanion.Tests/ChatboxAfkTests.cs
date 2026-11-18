using System;
using System.IO;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class ChatboxAfkTests
{
    [Fact]
    public void AfkReplacesTheStatusAndTheSwitchTurnsItOff()
    {
        using var fixture = new Fixture();
        fixture.Idle = TimeSpan.FromMinutes(10);
        var vm = fixture.CreateViewModel();

        vm.NewStatusText = "back soon";
        vm.AddStatusCommand.Execute(null);
        vm.AfkEnabled = true;
        vm.AfkTimeoutSeconds = 60;
        vm.AfkPrefix = "💤";
        vm.AfkText = "AFK";

        vm.RefreshForTest();

        Assert.True(vm.IsAfk);
        Assert.Equal("💤 AFK", vm.AfkStatusMessage);
        Assert.Equal("💤 AFK", vm.PreviewLine);

        fixture.Idle = TimeSpan.FromSeconds(5);
        vm.RefreshForTest();

        Assert.False(vm.IsAfk);
        Assert.Contains("back soon", vm.PreviewLine);

        fixture.Idle = TimeSpan.FromMinutes(10);
        vm.AfkEnabled = false;
        vm.RefreshForTest();

        Assert.False(vm.IsAfk);
        Assert.Contains("back soon", vm.PreviewLine);
        Assert.Contains("off", vm.AfkStateText, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _settingsPath =
            Path.Combine(Path.GetTempPath(), $"VrcCompanion-afk-{Guid.NewGuid():N}.json");

        public TimeSpan Idle { get; set; }

        public ChatboxViewModel CreateViewModel()
            => new(
                new SettingsStore(_settingsPath),
                new FakeSender(),
                afk: new FakeAfkSensor(this),
                startTimers: false);

        public void Dispose()
        {
            if (File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }
        }
    }

    private sealed class FakeAfkSensor : IAfkSensor
    {
        private readonly Fixture _fixture;

        public FakeAfkSensor(Fixture fixture) => _fixture = fixture;

        public TimeSpan GetIdleTime() => _fixture.Idle;
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
