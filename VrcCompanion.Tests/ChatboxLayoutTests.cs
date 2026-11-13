using System.Collections.Generic;
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
    public void ComposerHistoryAndRotationControlsRender()
    {
        WpfTestHost.Run(() =>
        {
            var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var window = new MainWindow(shell, initializeOnLoad: false)
            {
                Width = 1100,
                Height = 720,
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

                var composer = FindById<TextBox>(window, "ChatboxMessageBox");
                Assert.True(composer.IsVisible, "The chatbox composer should be visible.");

                var preview = FindById<TextBlock>(window, "ChatboxPreviewText");
                Assert.True(preview.IsVisible, "The VRChat preview should be visible.");

                var musicFormat = FindById<TextBox>(window, "ChatboxMusicFormatBox");
                Assert.True(musicFormat.IsVisible, "The music format field should be visible.");

                var addToRotation = FindById<Button>(window, "ChatboxAddToRotationButton");
                var send = FindById<Button>(window, "ChatboxSendButton");
                Assert.True(addToRotation.IsVisible);
                Assert.True(send.IsVisible);

                var history = FindById<ItemsControl>(window, "ChatboxHistoryList");
                var rotation = FindById<ItemsControl>(window, "ChatboxRotationList");
                Assert.NotNull(FindById<Button>(window, "ChatboxClearHistoryButton"));
                Assert.NotNull(FindById<Button>(window, "ChatboxClearRotationButton"));

                shell.ChatboxViewModel.History.Add("hello");
                shell.ChatboxViewModel.RotationMessages.Add("rotation");
                window.UpdateLayout();

                Assert.Contains("hello", history.Items.Cast<string>());
                Assert.Contains("rotation", rotation.Items.Cast<string>());
            }
            finally
            {
                window.Close();
            }
        });
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
