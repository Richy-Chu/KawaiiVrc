using System.IO;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class ChatboxComposerTests
{
    [Fact]
    public void StatusLibraryIsSeparateFromChatAndKeepsOnlyNewestFiveMessages()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();

        vm.NewStatusText = "back in 5 minutes";
        vm.AddStatusCommand.Execute(null);

        Assert.Single(vm.StatusItems);
        Assert.Equal("back in 5 minutes", vm.ActiveStatusText);
        Assert.Empty(vm.ChatHistory);

        for (var i = 1; i <= 7; i++)
        {
            vm.ChatText = $"message {i}";
            vm.SendChatCommand.Execute(null);
        }

        Assert.Equal(5, vm.ChatHistory.Count);
        Assert.Equal("message 7", vm.ChatHistory[^1].Message);
        Assert.Equal("message 3", vm.ChatHistory[0].Message);
        Assert.DoesNotContain(vm.ChatHistory, item => item.Message.Contains("back in 5", StringComparison.Ordinal));
        Assert.Single(vm.StatusItems);
    }

    [Fact]
    public void OnlyCyclingStatusesAreUsedByTheCycle()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.ChatHistory.Clear();

        vm.NewStatusText = "first";
        vm.AddStatusCommand.Execute(null);
        var first = vm.StatusItems[0];
        vm.NewStatusText = "manual only";
        vm.AddStatusCommand.Execute(null);
        var second = vm.StatusItems[1];
        vm.NewStatusText = "third";
        vm.AddStatusCommand.Execute(null);

        second.UseInCycle = false;
        vm.UseStatusCommand.Execute(first);
        vm.CycleStatusForTest();

        Assert.Same(vm.StatusItems[2], vm.ActiveStatus);
        Assert.Equal("third", vm.ActiveStatusText);

        vm.CycleStatusForTest();
        Assert.Same(first, vm.ActiveStatus);
        Assert.DoesNotContain(vm.StatusItems.Where(item => item.IsActive), item => ReferenceEquals(item, second));
    }

    [Fact]
    public void ChatOverridesStatusAndKeepsTheTypedLine()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.NewStatusText = "automatic status";
        vm.AddStatusCommand.Execute(null);

        vm.ChatText = "hello";
        vm.SendChatCommand.Execute(null);

        Assert.True(vm.IsChatShowing);
        Assert.Equal("hello", vm.PreviewLine);
        var sent = fixture.Sender.Sent.Last();
        Assert.Equal("hello", sent.Text);
        Assert.True(sent.SendImmediately);
        Assert.Contains("Chat visible", vm.ChatCountdownText);

        vm.StopChatCommand.Execute(null);

        Assert.False(vm.IsChatShowing);
        Assert.Equal(string.Empty, fixture.Sender.Sent.Last().Text);
    }

    [Fact]
    public void KawaiiEmojiChangesTheTitleAndInsertsIntoChat()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();

        Assert.Equal("KawaiiChatbox 🌸", vm.KawaiiChatboxTitle);

        vm.KawaiiEmoji = "✨";
        Assert.Equal("KawaiiChatbox ✨", vm.KawaiiChatboxTitle);

        vm.ChatText = "hello";
        vm.InsertChatEmojiCommand.Execute("💜");
        Assert.Equal("hello💜", vm.ChatText);

        vm.SetKawaiiEmojiCommand.Execute("🎀");
        Assert.Equal("KawaiiChatbox 🎀", vm.KawaiiChatboxTitle);
    }

    [Fact]
    public void ChatCountdownClearsTheMessageAndAutomaticStatusReturns()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.ChatVisibleSeconds = 1;
        vm.NewStatusText = "automatic status";
        vm.AddStatusCommand.Execute(null);
        vm.ChatText = "temporary";
        vm.SendChatCommand.Execute(null);

        Thread.Sleep(1100);
        vm.RefreshForTest();

        Assert.False(vm.IsChatShowing);
        Assert.Equal(string.Empty, fixture.Sender.Sent.Last().Text);

        vm.RefreshForTest();

        Assert.Equal("💭 automatic status", fixture.Sender.Sent.Last().Text);
    }

    [Fact]
    public void LiveTypingSendsDraftsAndEnterFinalizesTheMessage()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.LiveTyping = true;
        vm.ChatText = "draft";

        vm.FlushLiveTypingForTest();

        var draft = Assert.Single(fixture.Sender.Sent);
        Assert.Equal("draft", draft.Text);
        Assert.True(draft.SendImmediately);
        Assert.Empty(fixture.Sender.Typing);

        vm.SendChatCommand.Execute(null);

        Assert.Equal(2, fixture.Sender.Sent.Count);
        Assert.True(fixture.Sender.Sent[^1].SendImmediately);
        Assert.Empty(fixture.Sender.Typing);
        Assert.Single(vm.ChatHistory);
    }

    [Fact]
    public void AutomaticStatusNeverOverwritesALiveTypingDraft()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.NewStatusText = "automatic status";
        vm.AddStatusCommand.Execute(null);
        vm.LiveTyping = true;
        vm.ChatText = "draft that must stay";

        vm.FlushLiveTypingForTest();
        var sentBeforeTick = fixture.Sender.Sent.Count;

        vm.RefreshForTest();

        Assert.Equal(sentBeforeTick, fixture.Sender.Sent.Count);
        Assert.Equal("draft that must stay", fixture.Sender.Sent[^1].Text);
        Assert.Equal("draft that must stay", vm.PreviewLine);
        Assert.True(vm.IsLiveTypingShowing);
    }

    [Fact]
    public void ComposingAndUnchangedIdleStatusDoNotSendOsc()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.NewStatusText = "automatic status";
        vm.AddStatusCommand.Execute(null);

        vm.RefreshForTest();

        Assert.Single(fixture.Sender.Sent);
        Assert.True(fixture.Sender.Sent[0].SendImmediately);
        Assert.Empty(fixture.Sender.Typing);

        vm.RefreshForTest();

        Assert.Single(fixture.Sender.Sent);
        Assert.Empty(fixture.Sender.Typing);

        vm.ChatText = "local draft";
        var beforeTyping = fixture.Sender.Sent.Count;
        vm.RefreshForTest();
        vm.RefreshForTest();

        Assert.Equal(beforeTyping, fixture.Sender.Sent.Count);
        Assert.Empty(fixture.Sender.Typing);
        Assert.Contains("Draft is local", vm.PreviewStatusText, StringComparison.Ordinal);

        vm.ChatText = string.Empty;
        vm.RefreshForTest();

        Assert.Equal(beforeTyping, fixture.Sender.Sent.Count);
        Assert.Empty(fixture.Sender.Typing);
    }

    [Fact]
    public void OscMasterSwitchStopsSendingAndRestoresTheStatusWhenTurnedBackOn()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.NewStatusText = "automatic status";
        vm.AddStatusCommand.Execute(null);
        vm.RefreshForTest();

        Assert.Single(fixture.Sender.Sent);
        Assert.True(fixture.Sender.Sent[^1].SendImmediately);

        vm.OscEnabled = false;

        Assert.Equal(string.Empty, fixture.Sender.Sent[^1].Text);
        Assert.True(fixture.Sender.Sent[^1].SendImmediately);
        Assert.Equal("OFF", vm.ChatboxDisplayTag);

        vm.ChatText = "should not send";
        vm.SendChatCommand.Execute(null);
        vm.RefreshForTest();

        Assert.DoesNotContain(fixture.Sender.Sent, sent => sent.Text == "should not send");

        vm.ChatText = string.Empty;
        vm.OscEnabled = true;

        Assert.Equal("💭 automatic status", fixture.Sender.Sent[^1].Text);
        Assert.True(fixture.Sender.Sent[^1].SendImmediately);
    }

    [Fact]
    public void DisablingLiveTypingClearsTheDraftAndAutomaticStatusResumes()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.NewStatusText = "automatic status";
        vm.AddStatusCommand.Execute(null);
        vm.LiveTyping = true;
        vm.ChatText = "draft";
        vm.FlushLiveTypingForTest();

        vm.LiveTyping = false;

        Assert.False(vm.IsLiveTypingShowing);
        Assert.Equal(string.Empty, fixture.Sender.Sent[^1].Text);

        vm.ChatText = string.Empty;
        vm.RefreshForTest();

        Assert.Equal("💭 automatic status", fixture.Sender.Sent[^1].Text);
    }

    [Fact]
    public void SendingALiveDraftDoesNotSendAnExtraEmptyMessageAfterIt()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.LiveTyping = true;
        vm.ChatText = "draft";
        vm.FlushLiveTypingForTest();

        vm.SendChatCommand.Execute(null);

        Assert.Equal(2, fixture.Sender.Sent.Count);
        Assert.Equal("draft", fixture.Sender.Sent[^1].Text);
        Assert.True(fixture.Sender.Sent[^1].SendImmediately);
        Assert.Empty(fixture.Sender.Typing);
        Assert.False(vm.IsLiveTypingShowing);
        Assert.Equal(string.Empty, vm.ChatText);
    }

    [Fact]
    public void AutocompleteCompletesFromHistoryWithATabCommand()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.AutocompleteEnabled = true;
        vm.AutocompleteMinChars = 3;
        vm.ChatText = "hello from the composer";
        vm.SendChatCommand.Execute(null);

        vm.ChatText = "hel";

        Assert.True(vm.HasAutocompleteSuffix);
        Assert.Equal("lo from", vm.AutocompleteSuffix);

        vm.AcceptAutocompleteCommand.Execute(null);

        Assert.Equal("hello from", vm.ChatText);
        Assert.False(vm.HasAutocompleteSuffix);
    }

    [Fact]
    public void MusicAndForegroundAppAreComposedAfterTheStatus()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.SetTrackForTest(new NowPlayingInfo(
            "Song",
            "Artist",
            "Spotify",
            Duration: TimeSpan.FromMinutes(3)));
        vm.SetWindowForTest(new WindowActivityInfo("Code", "File.cs"));
        vm.IncludeMusic = true;
        vm.IncludeWindow = true;
        vm.SeparateWithEnters = false;
        vm.Separator = " | ";
        vm.NewStatusText = "here";
        vm.AddStatusCommand.Execute(null);

        Assert.Equal(
            "💭 here | ▶ Artist — Song · Spotify | 🪟 Code",
            vm.PreviewLine);
    }

    [Fact]
    public void EditingTheRunningChatUpdatesIt()
    {
        using var fixture = new Fixture();
        var vm = fixture.CreateViewModel();
        vm.ChatText = "original";
        vm.SendChatCommand.Execute(null);
        var item = vm.ChatHistory.Single();

        vm.BeginChatEditCommand.Execute(item);
        item.EditText = "edited";
        vm.SaveChatEditCommand.Execute(item);

        Assert.Equal("edited", item.Message);
        Assert.False(item.IsEditing);
        Assert.Equal("edited", vm.PreviewLine);
        Assert.Equal("edited", fixture.Sender.Sent.Last().Text);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _settingsPath =
            Path.Combine(Path.GetTempPath(), $"VrcCompanion-chatbox-{Guid.NewGuid():N}.json");

        public FakeChatboxSender Sender { get; } = new();

        public FakeWindowActivityService WindowActivity { get; } =
            new(new WindowActivityInfo("Code", "File.cs"));

        public ChatboxViewModel CreateViewModel()
        {
            var store = new SettingsStore(_settingsPath);
            return new ChatboxViewModel(
                store,
                Sender,
                windowActivity: WindowActivity,
                startTimers: false);
        }

        public void Dispose()
        {
            if (File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }
        }
    }

    private sealed class FakeWindowActivityService : IWindowActivityService
    {
        private readonly WindowActivityInfo? _current;

        public FakeWindowActivityService(WindowActivityInfo? current)
        {
            _current = current;
        }

        public WindowActivityInfo? GetCurrent() => _current;
    }

    private sealed class FakeChatboxSender : IChatboxSender
    {
        public List<(string Text, bool SendImmediately, bool PlaySound)> Sent { get; } = new();

        public List<bool> Typing { get; } = new();

        public string Host { get; set; } = "127.0.0.1";

        public int Port { get; set; } = 9000;

        public void SendChatbox(string text, bool sendImmediately = true, bool playSound = false)
            => Sent.Add((text, sendImmediately, playSound));

        public void SendTyping(bool typing) => Typing.Add(typing);
    }

}
