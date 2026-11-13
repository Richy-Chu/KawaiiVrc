using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
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

    [Fact]
    public async Task RetryLyricsCommandReloadsAfterATemporaryFailure()
    {
        using var fixture = new ViewModelFixture();
        var handler = new SequenceHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://lrclib.net")
        };
        var lyrics = new LyricsService(http);
        var vm = fixture.CreateViewModel(lyrics);
        vm.SetTrackForTest(new NowPlayingInfo(
            "Ain't My Fault",
            "Zara Larsson",
            "YouTube",
            Duration: TimeSpan.FromSeconds(223)));

        await vm.LyricsLoadTaskForTest!;
        Assert.Contains("busy", vm.LyricsStatusText);
        Assert.False(vm.HasLyrics);
        Assert.True(vm.RetryLyricsCommand.CanExecute(null));

        vm.RetryLyricsCommand.Execute(null);
        await vm.LyricsLoadTaskForTest!;

        Assert.True(vm.HasLyrics);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("Synced lyrics", vm.LyricsStatusText);
        Assert.Contains("Zara Larsson", vm.LyricsMatchText);
        Assert.Contains("Ain't My Fault", vm.LyricsMatchText);
    }

    private sealed class ViewModelFixture : IDisposable
    {
        private readonly string _settingsPath =
            Path.Combine(Path.GetTempPath(), $"VrcCompanion-test-{Guid.NewGuid():N}.json");

        public FakeChatboxSender Sender { get; } = new();

        public ChatboxViewModel CreateViewModel(LyricsService? lyrics = null)
        {
            var store = new SettingsStore(_settingsPath);
            store.Settings.ChatboxLyricsOnVrchat = true;
            store.Settings.ChatboxPauseLyricsAfterMessage = true;
            store.Settings.ChatboxLyricsPauseSeconds = 30;
            store.Settings.ChatboxRotateEnabled = false;
            return new ChatboxViewModel(store, Sender, lyrics: lyrics, startTimers: false);
        }

        public void Dispose()
        {
            if (File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }
        }
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            const string json = """
                {
                  "trackName": "Ain't My Fault",
                  "artistName": "Zara Larsson",
                  "duration": 223.0,
                  "instrumental": false,
                  "plainLyrics": null,
                  "syncedLyrics": "[00:01.00]Line one\n[00:05.00]Line two"
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
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
