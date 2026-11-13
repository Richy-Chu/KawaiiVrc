using System.IO;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class ChatboxLyricsPauseTests
{
    [Fact]
    public void ManualMessageThroughSendCommandPausesLyrics()
    {
        using var fixture = new ViewModelFixture();
        var vm = fixture.CreateViewModel();

        vm.MessageText = "manual message";
        vm.SendCommand.Execute(null);

        Assert.True(vm.IsLyricsPaused);
        Assert.Equal(new[] { "manual message" }, fixture.Sender.Sent);
    }

    [Fact]
    public void RotationMessageThroughRotationSendPathDoesNotPauseLyrics()
    {
        using var fixture = new ViewModelFixture();
        var vm = fixture.CreateViewModel();

        vm.RotationMessages.Add("rotation message");
        vm.RotateNextForTest();

        Assert.False(vm.IsLyricsPaused);
        Assert.Equal(new[] { "rotation message" }, fixture.Sender.Sent);
    }

    private sealed class ViewModelFixture : IDisposable
    {
        private readonly string _settingsPath =
            Path.Combine(Path.GetTempPath(), $"VrcCompanion-test-{Guid.NewGuid():N}.json");

        public FakeChatboxSender Sender { get; } = new();

        public ChatboxViewModel CreateViewModel()
        {
            var store = new SettingsStore(_settingsPath);
            store.Settings.ChatboxLyricsOnVrchat = true;
            store.Settings.ChatboxPauseLyricsAfterMessage = true;
            store.Settings.ChatboxLyricsPauseSeconds = 30;
            store.Settings.ChatboxRotateEnabled = false;
            return new ChatboxViewModel(store, Sender, startTimers: false);
        }

        public void Dispose()
        {
            if (File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }
        }
    }

    private sealed class FakeChatboxSender : IChatboxSender
    {
        public List<string> Sent { get; } = new();

        public string Host { get; set; } = "127.0.0.1";

        public int Port { get; set; } = 9000;

        public void SendChatbox(string text, bool sendImmediately = true) => Sent.Add(text);

        public void SendTyping(bool typing)
        {
        }
    }
}
