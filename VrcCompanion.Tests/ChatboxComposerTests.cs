using System.IO;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class ChatboxComposerTests
{
    [Fact]
    public void SentMessagesAppearInHistoryAndAutocomplete()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();

        vm.MessageText = "hello from the composer";
        vm.SendCommand.Execute(null);

        Assert.Equal(new[] { "hello from the composer" }, vm.History);
        Assert.True(vm.HasHistory);

        vm.MessageText = "hello";
        Assert.Contains("hello from the composer", vm.AutocompleteSuggestions);
        Assert.True(vm.HasAutocompleteSuggestions);

        vm.UseSuggestionCommand.Execute("hello from the composer");
        Assert.Equal("hello from the composer", vm.MessageText);

        vm.ClearHistoryCommand.Execute(null);
        Assert.Empty(vm.History);
        Assert.False(vm.HasHistory);
    }

    [Fact]
    public void PreviewExpandsTokensWithoutChangingTheComposerText()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.SetTrackForTest(new NowPlayingInfo(
            "Song",
            "Artist",
            "Spotify",
            Duration: TimeSpan.FromMinutes(3)));

        vm.MessageText = "{music} at {time}";

        Assert.Contains("♪ Artist — Song · Spotify", vm.PreviewText);
        Assert.DoesNotContain("{time}", vm.PreviewText, StringComparison.Ordinal);
        Assert.Equal("{music} at {time}", vm.MessageText);
        Assert.False(vm.IsPreviewOverLimit);
    }

    [Fact]
    public void RotationCanBeReorderedAndCleared()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.RotationMessages.Add("first");
        vm.RotationMessages.Add("second");
        vm.RotationMessages.Add("third");

        vm.MoveRotationUpCommand.Execute("third");
        Assert.Equal(new[] { "first", "third", "second" }, vm.RotationMessages);

        vm.MoveRotationDownCommand.Execute("first");
        Assert.Equal(new[] { "third", "first", "second" }, vm.RotationMessages);

        vm.ClearRotationCommand.Execute(null);
        Assert.Empty(vm.RotationMessages);
        Assert.False(vm.HasRotationMessages);
    }

    [Fact]
    public void LiveEditSendsDraftAndStopsTypingWhenTheMessageIsSent()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.LiveEditEnabled = true;
        vm.MessageText = "draft";

        vm.FlushLiveEditForTest();

        var draft = Assert.Single(fixture.Sender.Sent);
        Assert.Equal("draft", draft.Text);
        Assert.False(draft.SendImmediately);
        Assert.Equal(new[] { true }, fixture.Sender.Typing);

        vm.SendCommand.Execute(null);

        Assert.Equal(2, fixture.Sender.Sent.Count);
        Assert.True(fixture.Sender.Sent[^1].SendImmediately);
        Assert.Equal(new[] { true, false }, fixture.Sender.Typing);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _settingsPath =
            Path.Combine(Path.GetTempPath(), $"VrcCompanion-chatbox-{Guid.NewGuid():N}.json");

        public FakeChatboxSender Sender { get; } = new();

        public ChatboxViewModel CreateViewModel()
        {
            var store = new SettingsStore(_settingsPath);
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
        public List<(string Text, bool SendImmediately)> Sent { get; } = new();

        public List<bool> Typing { get; } = new();

        public string Host { get; set; } = "127.0.0.1";

        public int Port { get; set; } = 9000;

        public void SendChatbox(string text, bool sendImmediately = true)
            => Sent.Add((text, sendImmediately));

        public void SendTyping(bool typing) => Typing.Add(typing);
    }
}
