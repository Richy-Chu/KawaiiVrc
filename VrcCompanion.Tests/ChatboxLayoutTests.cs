using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class ChatboxLayoutTests
{
    [Fact]
    public void MagicChatboxPagesRenderTheirOwnControls()
    {
        WpfTestHost.Run(() =>
        {
            using var fixture = new Fixture();
            var chatbox = new ChatboxViewModel(fixture.Store, startTimers: false);
            var shell = new MainViewModel(chatbox);
            TestHooks.SetLoggedIn(shell);
            var window = new MainWindow(shell, initializeOnLoad: false)
            {
                Width = 1200,
                Height = 820,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 100,
                Top = 100
            };

            try
            {
                window.Show();
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "chatbox");
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.True(chatbox.IsStatusPage);
                var preview = FindById<TextBlock>(window, "ChatboxPreviewText");
                Assert.True(preview.IsVisible, "The shared VRChat preview should be visible.");

                var newStatus = FindById<TextBox>(window, "ChatboxNewStatusBox");
                var addStatus = FindById<Button>(window, "ChatboxAddStatusButton");
                Assert.True(newStatus.IsVisible, "The status editor should be visible on the Status page.");
                Assert.True(addStatus.IsVisible, "The add-status button should be visible on the Status page.");

                var statusList = FindById<ItemsControl>(window, "ChatboxStatusList");
                Assert.Empty(statusList.Items);

                chatbox.NewStatusText = "back in 5";
                chatbox.AddStatusCommand.Execute(null);
                window.UpdateLayout();
                Assert.Single(statusList.Items);

                Assert.True(FindById<Button>(window, "ChatboxOptionsPageButton").IsVisible);
                chatbox.ShowChattingPageCommand.Execute(null);
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.True(FindById<TextBox>(window, "ChatboxMessageBox").IsVisible);
                Assert.True(FindById<Button>(window, "ChatboxSendButton").IsVisible);
                Assert.True(FindById<CheckBox>(window, "ChatboxLiveTypingCheckBox").IsVisible);
                Assert.True(FindById<Button>(window, "ChatboxStopChatButton").IsVisible);

                var chatList = FindById<ItemsControl>(window, "ChatboxChatList");
                Assert.Empty(chatList.Items);
                chatbox.ChatText = "hello";
                chatbox.SendChatCommand.Execute(null);
                window.UpdateLayout();
                Assert.Single(chatList.Items);

                chatbox.ShowIntegrationsPageCommand.Execute(null);
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.True(FindById<ItemsControl>(window, "ChatboxIntegrationList").IsVisible);

                chatbox.ShowOptionsPageCommand.Execute(null);
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                chatbox.PulsoidToken = "layout-pulsoid-secret";
                chatbox.TwitchAccessToken = "layout-twitch-secret";
                window.UpdateLayout();

                Assert.True(FindById<TextBox>(window, "ChatboxChatVisibleSecondsBox").IsVisible);
                Assert.True(FindById<TextBox>(window, "ChatboxKawaiiEmojiBox").IsVisible);
                Assert.True(FindById<TextBox>(window, "ChatboxMusicFormatBox").IsVisible);
                Assert.True(FindById<TextBox>(window, "ChatboxTimeFormatBox").IsVisible);
                Assert.True(FindById<Button>(window, "ChatboxIntegrationsPageButton").IsVisible);

                var pulsoid = FindById<PasswordBox>(window, "ChatboxPulsoidTokenBox");
                var twitch = FindById<PasswordBox>(window, "ChatboxTwitchAccessTokenBox");
                Assert.True(pulsoid.IsVisible, "The Pulsoid token must use the masked password control.");
                Assert.True(twitch.IsVisible, "The Twitch token must use the masked password control.");
                Assert.Equal("layout-pulsoid-secret", pulsoid.Password);
                Assert.Equal("layout-twitch-secret", twitch.Password);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _settingsPath =
            Path.Combine(Path.GetTempPath(), $"VrcCompanion-chatbox-layout-{Guid.NewGuid():N}.json");

        public SettingsStore Store { get; }

        public Fixture()
        {
            Store = new SettingsStore(_settingsPath);
        }

        public void Dispose()
        {
            if (File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }
        }
    }

    private static T FindById<T>(DependencyObject root, string automationId)
        where T : DependencyObject
        => FindVisualChildren<T>(root)
            .Single(element => AutomationProperties.GetAutomationId(element) == automationId);

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }
}
