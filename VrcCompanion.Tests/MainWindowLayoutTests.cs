using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Controls;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class MainWindowLayoutTests
{
    [Fact]
    public void ContinuityAndContextViewsRenderFromTheSidebar()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
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
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "continuity");
                Pump(window);
                Assert.Single(FindVisualChildren<ContinuityView>(window));

                shell.SelectedSection = shell.Sections.Single(section => section.Key == "context");
                Pump(window);
                Assert.Single(FindVisualChildren<ContextView>(window));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void NotificationFiltersDriveTheVisibleList()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            using var document = JsonDocument.Parse("{}");
            shell.Notifications.Add(new NotificationItem(new VrcNotification
            {
                Id = "friend-request",
                Type = "friendRequest",
                SenderUsername = "Tester",
                Details = document.RootElement.Clone()
            }));
            shell.Notifications.Add(new NotificationItem(new VrcNotification
            {
                Id = "world-invite",
                Type = "invite",
                SenderUsername = "Tester",
                Details = document.RootElement.Clone()
            }));
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
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "notifications");
                Pump(window);

                var list = FindVisualChildren<ListBox>(window)
                    .Single(box => ReferenceEquals(box.ItemsSource, shell.NotificationsView));
                Assert.Equal(2, list.Items.Count);

                shell.SelectedNotificationFilter = shell.NotificationFilters
                    .Single(filter => filter.Key == "friend-requests");
                Pump(window);

                Assert.Single(list.Items);
                Assert.Equal("friend-request", ((NotificationItem)list.Items[0]).Id);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InviteDetailShowsTheInvitedWorldAndJoinChoice()
    {
        WpfTestHost.Run(() =>
        {
            var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            using var api = new VrcApiClient();
            using var document = JsonDocument.Parse(
                """{"worldId":"wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:96583~private(usr_9439f8cc-1c6b-4dca-9a07-d2eccb570701)~region(use)","worldName":"The Black Cat"}""");
            var item = new NotificationItem(new VrcNotification
            {
                Id = "not_invite_layout",
                Type = "invite",
                SenderUsername = "K7+",
                Details = document.RootElement.Clone()
            });
            TestHooks.AttachNotificationDetail(
                shell,
                new NotificationDetailViewModel(item, api));
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
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "notifications");
                Pump(window);

                Assert.True(shell.HasSelectedNotification, "The invite detail should be attached.");
                var worldName = FindVisualChildren<TextBlock>(window)
                    .Single(text => text.Text == "The Black Cat");
                var join = FindVisualChildren<Button>(window)
                    .Single(button => Equals(button.Content, "Join world"));
                var declines = FindVisualChildren<Button>(window)
                    .Where(button => Equals(button.Content, "Decline"))
                    .ToList();
                var decline = declines.First();
                var declineWithMessage = FindVisualChildren<Button>(window)
                    .Single(button => Equals(button.Content, "Decline with message"));

                Assert.True(worldName.IsVisible, "The invite should name the world without waiting for its lookup.");
                Assert.True(join.IsVisible, "The invite should offer Join world.");
                Assert.True(decline.IsVisible, "The invite should offer Decline.");
                Assert.True(declineWithMessage.IsVisible, "The invite should offer a custom decline message.");
                Assert.True(join.Command!.CanExecute(join.CommandParameter), "Join world should use the invite location.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InviteComposerExplainsThatAWorldIsRequired()
    {
        WpfTestHost.Run(() =>
        {
            using var api = new VrcApiClient();
            using var shell = new MainViewModel(api);
            TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
            TestHooks.SetLoggedIn(shell);
            TestHooks.SetCurrentUser(shell, new VrcUser
            {
                Id = "usr_me",
                DisplayName = "Me",
                Location = "private"
            });
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
                shell.InviteFriendCommand.Execute(
                    new FriendItem(new VrcUser { Id = "usr_friend", DisplayName = "Friend" }));
                Pump(window);

                Assert.True(shell.IsInviteComposerOpen);
                var hint = FindVisualChildren<TextBlock>(window)
                    .Single(text => text.Text.Contains("not in a world", StringComparison.Ordinal));
                var invite = FindVisualChildren<Button>(window)
                    .Single(button => Equals(button.Content, "Invite them to my world"));
                var request = FindVisualChildren<Button>(window)
                    .Single(button => Equals(button.Content, "Request their invite"));

                Assert.True(hint.IsVisible, "The composer should explain why inviting is unavailable.");
                Assert.True(invite.IsVisible);
                Assert.True(request.IsVisible);
                Assert.True(invite.IsEnabled, "The invite mode should stay selectable.");
                Assert.True(request.IsEnabled, "The request mode should stay selectable.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InviteComposerRendersPresetRows()
    {
        WpfTestHost.Run(() =>
        {
            using var api = new VrcApiClient(null, new PresetHandler());
            using var shell = new MainViewModel(api);
            TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
            TestHooks.SetLoggedIn(shell);
            TestHooks.SetCurrentUser(shell, new VrcUser
            {
                Id = "usr_me",
                DisplayName = "Me",
                Location = "private"
            });
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
                shell.InviteFriendCommand.Execute(
                    new FriendItem(new VrcUser { Id = "usr_friend", DisplayName = "Friend" }));
                for (var attempt = 0;
                     attempt < 100 && (shell.IsInviteMessagesLoading || shell.InviteMessages.Count == 0);
                     attempt++)
                {
                    Pump(window);
                    System.Threading.Thread.Sleep(10);
                }

                Pump(window);

                var preview = FindVisualChildren<TextBlock>(window)
                    .Where(text => string.Equals(text.Text, "preset text", StringComparison.Ordinal))
                    .ToList();
                Assert.Single(preview, text => text.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AdvancedLaunchProfilesDialogRendersAndCreatesAProfile()
    {
        WpfTestHost.Run(() =>
        {
            using var api = new VrcApiClient();
            using var shell = new MainViewModel(api);
            var window = new MainWindow(shell, initializeOnLoad: false)
            {
                Width = 1200,
                Height = 800,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 100,
                Top = 100
            };
            LaunchProfileItem? created = null;

            try
            {
                window.Show();
                Pump(window);
                shell.OpenAdvancedLaunchCommand.Execute(null);
                Pump(window);

                Assert.True(shell.IsAdvancedLaunchOpen);
                var header = FindVisualChildren<TextBlock>(window)
                    .Single(text => text.Text == "Advanced launch profiles");
                Assert.True(header.IsVisible);
                Assert.Contains(shell.LaunchProfiles, profile => profile.IsDefault);

                var before = shell.LaunchProfiles.Count;
                shell.NewLaunchProfileCommand.Execute(null);
                Pump(window);
                created = shell.SelectedLaunchProfile;
                Assert.Equal(before + 1, shell.LaunchProfiles.Count);
                Assert.NotNull(created);
                Assert.False(created!.IsDefault);

                shell.DeleteLaunchProfileCommand.Execute(null);
                shell.CancelAdvancedLaunchCommand.Execute(null);
                Pump(window);
                Assert.Equal(before, shell.LaunchProfiles.Count);
                Assert.False(shell.IsAdvancedLaunchOpen);
            }
            finally
            {
                if (created is not null && shell.LaunchProfiles.Contains(created))
                {
                    shell.SelectedLaunchProfile = created;
                    shell.DeleteLaunchProfileCommand.Execute(null);
                }

                window.Close();
            }
        });
    }

    [Fact]
    public void FriendProfileInstanceHistoryRowsRenderAgainAfterCloseAndReopen()
    {
        WpfTestHost.Run(() =>
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "vrccompanion-history-wpf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var store = new InstanceHistoryStore(Path.Combine(directory, "history.db"));
            var start = DateTimeOffset.Now.AddHours(-1);
            var entry = InstanceHistoryStore.NewFromLog(
                "usr_wpf_me",
                "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345~region(eu)",
                start)!;
            entry.WorldName = "WPF History World";
            entry.WorldThumbnailUrl = "https://example.test/world.png";
            entry.WorldAuthorName = "Creator";
            entry.GetOrAddPlayer("usr_wpf_me", "Me").StartVisit(start);
            entry.GetOrAddPlayer("usr_wpf_maya", "Maya").StartVisit(start);
            entry.Close(start.AddMinutes(10));
            store.Add(entry);

            var second = InstanceHistoryStore.NewFromLog(
                "usr_wpf_me",
                "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:67890~region(us)",
                start.AddMinutes(20))!;
            second.WorldName = "Second WPF World";
            second.WorldThumbnailUrl = "https://example.test/second.png";
            second.WorldAuthorName = "Creator";
            second.GetOrAddPlayer("usr_wpf_me", "Me").StartVisit(start.AddMinutes(20));
            second.GetOrAddPlayer(string.Empty, "Maya").StartVisit(start.AddMinutes(20));
            second.Close(start.AddMinutes(30));
            store.Add(second);

            using var api = new VrcApiClient();
            var shell = new MainViewModel(api);
            using var history = new InstanceHistoryViewModel(api, store);
            history.SetAccount(new VrcUser { Id = "usr_wpf_me", DisplayName = "Me" });
            history.PlayerProfileRequested = shell.OpenHistoryPlayer;
            history.RefreshAsync().GetAwaiter().GetResult();

            TestHooks.SetLoggedIn(shell);
            TestHooks.AttachInstanceHistoryViewModel(shell, history);
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = "usr_wpf_maya",
                    DisplayName = "Maya",
                    Username = "maya",
                    IsFriend = true
                }),
                api,
                ImageLoader.Shared);
            profile.ShowInstanceHistoryCommand = new RelayCommand(
                () => shell.OpenInstanceHistoryFor(profile));
            TestHooks.AttachFriendDetail(shell, profile);
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
                profile.ShowInstanceHistoryCommand.Execute(null);
                Pump(window);

                var firstRows = FindVisibleHistoryRows(window).ToList();
                Assert.Equal(2, firstRows.Count);
                Assert.All(firstRows, row => Assert.True(row.IsVisible));
                AssertTopmostOverlay(window, "InstanceHistoryOverlay");
                Assert.True(shell.HasSelectedFriend, "The profile should stay open behind history.");
                Assert.True(history.CanReturnToProfile);

                var backToProfile = FindVisualChildren<Button>(window)
                    .FirstOrDefault(button =>
                        button.IsVisible &&
                        Equals(button.ToolTip, "Back to profile"));
                Assert.NotNull(backToProfile);
                backToProfile!.Command.Execute(backToProfile.CommandParameter);
                Pump(window);

                Assert.False(history.IsOpen);
                Assert.True(shell.HasSelectedFriend, "Back to profile should leave the profile open.");

                profile.ShowInstanceHistoryCommand.Execute(null);
                Pump(window);

                var close = FindVisualChildren<Button>(window)
                    .FirstOrDefault(button =>
                        button.IsVisible &&
                        Equals(button.ToolTip, "Close instance history"));
                Assert.NotNull(close);
                close!.Command.Execute(close.CommandParameter);
                Pump(window);

                Assert.False(history.IsOpen);
                Assert.Empty(FindVisibleHistoryRows(window));

                profile.ShowInstanceHistoryCommand.Execute(null);
                Pump(window);

                var reopenedRows = FindVisibleHistoryRows(window).ToList();
                Assert.Equal(2, reopenedRows.Count);
                Assert.All(reopenedRows, row => Assert.True(row.IsVisible));

                reopenedRows[0].Command.Execute(reopenedRows[0].CommandParameter);
                Pump(window);
                Assert.True(history.IsDetailVisible, "Opening a history row should show instance details.");
                AssertTopmostOverlay(window, "InstanceHistoryDetailOverlay");

                var playerProfile = FindVisualChildren<Button>(window)
                    .FirstOrDefault(button =>
                        button.IsVisible &&
                        button.DataContext is InstanceHistoryPlayerItem player &&
                        player.UserId == "usr_wpf_maya" &&
                        Equals(button.ToolTip, "Open this player's profile"));
                Assert.NotNull(playerProfile);
                playerProfile!.Command.Execute(playerProfile.CommandParameter);
                Pump(window);

                Assert.True(shell.HasSelectedFriend, "The player profile should open over instance details.");
                Assert.True(history.IsDetailVisible, "Instance details should stay behind the player profile.");
                AssertTopmostOverlay(window, "FriendProfileOverlay");

                shell.CloseFriendDetailCommand.Execute(null);
                Pump(window);

                Assert.False(shell.HasSelectedFriend);
                Assert.True(history.IsDetailVisible, "Closing the profile should return to instance details.");
                AssertTopmostOverlay(window, "InstanceHistoryDetailOverlay");
                Assert.Contains(
                    FindVisualChildren<Button>(window),
                    button => button.IsVisible && Equals(button.ToolTip, "Back to instance history"));
            }
            finally
            {
                window.Close();
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        });
    }

    private static IEnumerable<Button> FindVisibleHistoryRows(DependencyObject root)
        => FindVisualChildren<Button>(root)
            .Where(button =>
                button.IsVisible &&
                AutomationProperties.GetAutomationId(button) == "InstanceHistoryEntryButton");

    private static void AssertTopmostOverlay(Window window, string expected)
    {
        var overlays = FindVisualChildren<Grid>(window)
            .Where(grid =>
                grid.IsVisible &&
                !string.IsNullOrEmpty(AutomationProperties.GetAutomationId(grid)) &&
                AutomationProperties.GetAutomationId(grid).EndsWith("Overlay", StringComparison.Ordinal))
            .OrderByDescending(Panel.GetZIndex)
            .ToList();

        Assert.NotEmpty(overlays);
        Assert.Equal(expected, AutomationProperties.GetAutomationId(overlays[0]));
        Assert.All(
            overlays,
            overlay => Assert.Same(VisualTreeHelper.GetParent(overlays[0]), VisualTreeHelper.GetParent(overlay)));
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    [Fact]
    public void BraveFocusToggleIsHiddenUntilTheBrowserHasAPage()
    {
        WpfTestHost.Run(() =>
        {
            var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var browserSection = shell.Sections.Single(section => section.Key == "brave");
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
                shell.SelectedSection = browserSection;
                window.UpdateLayout();

                var toggle = FindVisualChildren<ToggleButton>(window)
                    .Single(element => Equals(element.Tag, "BraveFocusToggle"));
                Assert.Equal(Visibility.Collapsed, toggle.Visibility);
                Assert.False(toggle.IsVisible, "Focus must not be offered while Brave is closed.");

                shell.BraveBrowserViewModel.ShowBrowserForLayoutTest();
                window.UpdateLayout();

                Assert.Equal(Visibility.Visible, toggle.Visibility);
                Assert.True(toggle.IsVisible, "Focus should be offered once a page is open.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SidebarNavScrollsSettingsIntoViewAtConstrainedHeight()
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
                window.UpdateLayout();

                var settings = shell.Sections.Single(section => section.Key == "settings");
                var nav = FindVisualChildren<ListBox>(window)
                    .Single(list => list.Items.Count == shell.Sections.Count &&
                                    list.Items.Contains(settings));

                nav.ScrollIntoView(settings);
                nav.UpdateLayout();

                var scroll = FindVisualChildren<ScrollViewer>(nav).First();
                Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                Assert.True(scroll.ScrollableHeight > 0, "The constrained sidebar should be scrollable.");
                Assert.True(scroll.VerticalOffset > 0, "Settings should be below the initial sidebar viewport.");

                var settingsItem = Assert.IsType<ListBoxItem>(
                    nav.ItemContainerGenerator.ContainerFromItem(settings));
                Assert.True(settingsItem.ActualHeight > 0, "The Settings row should have been laid out.");

                var bounds = settingsItem
                    .TransformToAncestor(scroll)
                    .TransformBounds(new Rect(0, 0, settingsItem.ActualWidth, settingsItem.ActualHeight));
                Assert.True(bounds.Top >= -1, "Settings should not be clipped above the sidebar viewport.");
                Assert.True(
                    bounds.Bottom <= scroll.ActualHeight + 1,
                    $"Settings should be fully visible after scrolling the sidebar. " +
                    $"Bounds={bounds}, actual={scroll.ActualHeight}, viewport={scroll.ViewportHeight}, " +
                    $"offset={scroll.VerticalOffset}, scrollable={scroll.ScrollableHeight}.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("brave", "BraveBrowserPane", "BraveFocusToggle")]
    [InlineData("discord", "DiscordBrowserPane", "DiscordFocusToggle")]
    [InlineData("resources", "CreatorBrowserPane", "CreatorFocusToggle")]
    public void FocusModeGivesTheBrowserTheWholePane(
        string sectionKey,
        string paneTag,
        string toggleTag)
    {
        WpfTestHost.Run(() =>
        {
            var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var browserSection = shell.Sections.Single(section => section.Key == sectionKey);
            var profileSection = shell.Sections.Single(section => section.Key == "profile");
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
                window.UpdateLayout();

                shell.SelectedSection = browserSection;
                window.UpdateLayout();

                if (sectionKey == "resources")
                {
                    shell.CreatorResourcesViewModel.ShowBrowserForLayoutTest();
                    window.UpdateLayout();
                }
                else if (sectionKey == "brave")
                {
                    shell.BraveBrowserViewModel.ShowBrowserForLayoutTest();
                    window.UpdateLayout();
                }

                var pane = FindVisualChildren<Border>(window)
                    .Single(element => Equals(element.Tag, paneTag));
                var toggle = FindVisualChildren<ToggleButton>(window)
                    .Single(element => Equals(element.Tag, toggleTag));
                var focusToolbar = FindVisualChildren<Border>(window)
                    .Single(element => Equals(element.Tag, "BrowserFocusToolbar"));
                var normalToolbar = FindVisualChildren<StackPanel>(window)
                    .Single(element => Equals(element.Tag, "BrowserNormalToolbar"));
                var expander = FindVisualChildren<Expander>(window).SingleOrDefault();
                var appHeader = (Border)window.FindName("AppHeader");
                var appFooter = (Border)window.FindName("AppFooter");

                void AssertChrome(Visibility expected)
                {
                    Assert.Equal(expected, appHeader.Visibility);
                    Assert.Equal(expected, appFooter.Visibility);
                }

                var normalHeight = pane.ActualHeight;
                toggle.IsChecked = true;
                window.UpdateLayout();

                Assert.True(
                    pane.ActualHeight > normalHeight + 20,
                    $"Focus mode should give {sectionKey} more height. " +
                    $"Before={normalHeight:F1}, after={pane.ActualHeight:F1}.");
                if (expander is not null)
                {
                    Assert.Equal(Visibility.Collapsed, expander.Visibility);
                }
                AssertChrome(Visibility.Collapsed);
                Assert.Equal(Visibility.Visible, focusToolbar.Visibility);
                Assert.Equal(Visibility.Collapsed, normalToolbar.Visibility);

                shell.SelectedSection = profileSection;
                window.UpdateLayout();
                AssertChrome(Visibility.Visible);

                shell.SelectedSection = browserSection;
                window.UpdateLayout();
                Assert.True(toggle.IsChecked == true, "Focus should remain checked when returning to the browser.");
                Assert.True(
                    pane.ActualHeight > normalHeight + 20,
                    $"Focus mode should reapply {sectionKey} height after returning. " +
                    $"Before={normalHeight:F1}, after={pane.ActualHeight:F1}.");
                AssertChrome(Visibility.Collapsed);

                var escape = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!,
                    0,
                    Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                window.RaiseEvent(escape);
                window.UpdateLayout();
                Assert.True(escape.Handled, "Escape should exit focus mode.");
                Assert.False(toggle.IsChecked == true, "Escape should clear the focus toggle.");
                AssertChrome(Visibility.Visible);
                Assert.Equal(Visibility.Collapsed, focusToolbar.Visibility);
                Assert.Equal(Visibility.Visible, normalToolbar.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FocusToolbarShowsPageContextAndNavigationAvailability()
    {
        WpfTestHost.Run(() =>
        {
            var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var browserSection = shell.Sections.Single(section => section.Key == "brave");
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
                shell.SelectedSection = browserSection;
                shell.BraveBrowserViewModel.ShowBrowserForLayoutTest();
                shell.BraveBrowserViewModel.SetBrowserLocation(
                    "https://example.com/focus",
                    "Focus page");
                shell.BraveBrowserViewModel.IsFocusMode = true;
                window.UpdateLayout();

                var focusToolbar = FindVisualChildren<Border>(window)
                    .Single(element => Equals(element.Tag, "BrowserFocusToolbar"));
                var labels = FindVisualChildren<TextBlock>(focusToolbar)
                    .Select(text => text.Text)
                    .ToArray();
                Assert.Contains("Focus page", labels);
                var address = FindVisualChildren<TextBox>(focusToolbar)
                    .Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "BrowserFocusAddressBox");
                Assert.Equal("https://example.com/focus", address.Text);

                var back = FindVisualChildren<Button>(focusToolbar)
                    .Single(button =>
                        AutomationProperties.GetAutomationId(button) == "BrowserFocusBackButton");
                var forward = FindVisualChildren<Button>(focusToolbar)
                    .Single(button =>
                        AutomationProperties.GetAutomationId(button) == "BrowserFocusForwardButton");
                Assert.False(back.IsEnabled);
                Assert.False(forward.IsEnabled);

                var browser = FindVisualChildren<EmbeddedBrowser>(window)
                    .Single(element => element.IsVisible);
                browser.SetValue(EmbeddedBrowser.CanGoBackProperty, true);
                browser.SetValue(EmbeddedBrowser.CanGoForwardProperty, true);
                window.UpdateLayout();

                Assert.True(back.IsEnabled);
                Assert.True(forward.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("brave", "BraveFocusToggle")]
    [InlineData("discord", "DiscordFocusToggle")]
    [InlineData("resources", "CreatorFocusToggle")]
    public void InvalidFocusAddressFeedbackFitsAtMinimumWindowWidth(
        string sectionKey,
        string toggleTag)
    {
        WpfTestHost.Run(() =>
        {
            const string message = "Enter a valid http or https address.";
            const string pageUrl = "https://example.com/focus";
            var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var browserSection = shell.Sections.Single(section => section.Key == sectionKey);
            IEmbeddedBrowserHost host = sectionKey switch
            {
                "brave" => shell.BraveBrowserViewModel,
                "discord" => shell.DiscordBrowserViewModel,
                "resources" => shell.CreatorResourcesViewModel,
                _ => throw new ArgumentOutOfRangeException(nameof(sectionKey), sectionKey, null)
            };
            string GetBrowserError() => sectionKey switch
            {
                "brave" => shell.BraveBrowserViewModel.BrowserError,
                "discord" => shell.DiscordBrowserViewModel.BrowserError,
                "resources" => shell.CreatorResourcesViewModel.BrowserError,
                _ => string.Empty
            };
            var window = new MainWindow(shell, initializeOnLoad: false)
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 100,
                Top = 100
            };
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;

            try
            {
                window.Show();
                shell.SelectedSection = browserSection;
                if (host is CreatorResourcesViewModel creatorResources)
                {
                    creatorResources.ShowBrowserForLayoutTest();
                }
                else if (host is BraveBrowserViewModel braveBrowser)
                {
                    braveBrowser.ShowBrowserForLayoutTest();
                }

                host.SetBrowserLocation(pageUrl, "Focus page");
                host.IsFocusMode = true;
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var toggle = FindVisualChildren<ToggleButton>(window)
                    .Single(element => Equals(element.Tag, toggleTag));
                var address = FindVisualChildren<TextBox>(window)
                    .Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "BrowserFocusAddressBox");
                var exit = FindVisualChildren<Button>(window)
                    .Single(button =>
                        AutomationProperties.GetAutomationId(button) == "BrowserFocusExitButton");

                Assert.True(toggle.IsChecked == true, $"{sectionKey} should be in focus mode.");
                Assert.True(host.IsFocusMode, $"{sectionKey} focus state should be active.");
                Assert.True(address.IsVisible, $"The {sectionKey} focus address box should be visible.");
                Assert.True(exit.IsVisible, $"The {sectionKey} Exit focus button should be visible.");

                address.Text = "ftp://example.com/";
                var enter = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!,
                    0,
                    Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                };
                address.RaiseEvent(enter);
                window.UpdateLayout();

                Assert.True(
                    enter.Handled,
                    $"Enter should submit the {sectionKey} focus address bar. Text='{address.Text}', " +
                    $"error='{GetBrowserError()}', browserUrl='{host.BrowserUrl}'.");
                Assert.Equal(message, GetBrowserError());
                Assert.Equal(pageUrl, host.BrowserUrl);

                var focusToolbar = FindVisualChildren<Border>(window)
                    .Single(element => Equals(element.Tag, "BrowserFocusToolbar"));
                var feedback = FindVisualChildren<TextBlock>(focusToolbar)
                    .Single(text =>
                        AutomationProperties.GetAutomationId(text) == "BrowserFocusAddressError");
                Assert.Equal(message, feedback.Text);
                Assert.True(
                    feedback.IsVisible,
                    $"Invalid-address feedback should be visible for {sectionKey} in focus mode.");
                Assert.Equal(
                    AutomationLiveSetting.Assertive,
                    AutomationProperties.GetLiveSetting(feedback));

                var otherFeedback = FindVisualChildren<TextBlock>(window)
                    .Where(text => text.Text == message && !ReferenceEquals(text, feedback));
                Assert.All(
                    otherFeedback,
                    text => Assert.False(
                        text.IsVisible,
                        $"The {sectionKey} focus mode should not duplicate feedback outside the toolbar."));

                var feedbackBounds = BoundsWithin(feedback, window);
                var addressBounds = BoundsWithin(address, window);
                var exitBounds = BoundsWithin(exit, window);
                Assert.True(
                    feedbackBounds.Width > 0 && feedbackBounds.Height > 0,
                    $"Invalid-address feedback should have size for {sectionKey}. Bounds={feedbackBounds}.");
                Assert.True(
                    feedbackBounds.Left >= 0 &&
                    feedbackBounds.Right <= window.ActualWidth + 1 &&
                    feedbackBounds.Top >= 0 &&
                    feedbackBounds.Bottom <= window.ActualHeight + 1,
                    $"Invalid-address feedback should remain inside the window for {sectionKey}. " +
                    $"Bounds={feedbackBounds}, window={window.ActualWidth}x{window.ActualHeight}.");
                Assert.True(
                    addressBounds.Width >= 200,
                    $"The {sectionKey} address box should remain usable at the minimum width. " +
                    $"Bounds={addressBounds}.");
                Assert.True(
                    addressBounds.Right <= exitBounds.Left + 1,
                    $"The {sectionKey} address box should not overlap Exit focus. " +
                    $"Address={addressBounds}, exit={exitBounds}.");
                Assert.True(
                    exitBounds.Left >= 0 &&
                    exitBounds.Right <= window.ActualWidth + 1 &&
                    exitBounds.Top >= 0 &&
                    exitBounds.Bottom <= window.ActualHeight + 1,
                    $"The {sectionKey} Exit focus button should remain inside the window. " +
                    $"Bounds={exitBounds}, window={window.ActualWidth}x{window.ActualHeight}.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InstancePopupTemplateLoadsAtMinimumWindowSize()
    {
        WpfTestHost.Run(() =>
        {
            var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            Assert.DoesNotContain(shell.Sections, section => section.Key == "instance");
            var window = new MainWindow(shell, initializeOnLoad: false)
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 100,
                Top = 100
            };
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;

            try
            {
                window.Show();
                shell.OpenInstanceCommand.Execute(null);
                window.UpdateLayout();

                var label = FindVisualChildren<TextBlock>(window)
                    .FirstOrDefault(text => text.Text == "INSTANCE");
                Assert.NotNull(label);
                Assert.True(label!.IsVisible, "The live instance popup should render at the minimum window size.");

                var close = FindVisualChildren<Button>(window)
                    .FirstOrDefault(button =>
                        AutomationProperties.GetName(button) == "Close instance popup" ||
                        Equals(button.ToolTip, "Close the instance popup"));
                Assert.NotNull(close);
                Assert.True(close!.IsVisible, "The instance popup close action should be visible.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ProfileScrollbarAndPrimaryActionUseTheProfileButtonColor()
    {
        WpfTestHost.Run(() =>
        {
            var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            using var api = new VrcApiClient();
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = "usr_theme",
                    DisplayName = "Theme",
                    Username = "theme",
                    ThemeButtonColor = "#1778FF"
                }),
                api,
                ImageLoader.Shared);
            TestHooks.AttachFriendDetail(shell, profile);
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
                Pump(window);

                var scroll = FindVisualChildren<ScrollViewer>(window)
                    .Single(view => view.Resources.Contains("ProfileVerticalScrollBarTemplate"));
                var verticalBars = FindVisualChildren<ScrollBar>(scroll)
                    .Where(bar => bar.Orientation == Orientation.Vertical)
                    .ToList();
                foreach (var bar in verticalBars)
                {
                    bar.ApplyTemplate();
                }
                Pump(window);
                var profileTemplate = scroll.TryFindResource("ProfileVerticalScrollBarTemplate");
                Assert.NotEmpty(verticalBars);
                Assert.All(verticalBars, bar => Assert.Same(profileTemplate, bar.Template));

                var scrollBar = verticalBars.Single(bar => ReferenceEquals(bar.TemplatedParent, scroll));
                var thumb = FindVisualChildren<Thumb>(scrollBar).Single();
                // The thumb template can still be pending when the full suite is
                // busy; force it so this color assertion does not depend on timing.
                thumb.ApplyTemplate();
                Pump(window);
                var thumbBorder = FindVisualChildren<Border>(thumb)
                    .Single(border => border.Name == "ThumbBorder");

                Assert.Equal(BrushColor(profile.ProfileButtonBrush), BrushColor(thumbBorder.Background));

                var primary = FindVisualChildren<Button>(window)
                    .Single(button => ReferenceEquals(
                        button.Style,
                        scroll.TryFindResource("ProfilePrimaryButton")));
                Assert.Equal(BrushColor(profile.ProfileButtonBrush), BrushColor(primary.Background));
                Assert.Equal(BrushColor(profile.ProfileButtonTextBrush), BrushColor(primary.Foreground));

                var viewAllGroups = FindVisualChildren<Button>(window)
                    .Single(button => button.IsVisible && Equals(button.Content, "View all groups"));
                viewAllGroups.Command.Execute(viewAllGroups.CommandParameter);
                Pump(window);

                Assert.True(profile.IsGroupsOpen);
                AssertTopmostOverlay(window, "ProfileGroupsOverlay");

                foreach (var key in new[] { "SidebarBrush", "PanelBrush", "InputBrush", "RowBrush" })
                {
                    var brush = Assert.IsType<SolidColorBrush>(window.FindResource(key));
                    Assert.InRange(brush.Color.A, 1, 254);
                }

                var profileScrim = FindVisualChildren<Border>(window)
                    .Single(border =>
                        AutomationProperties.GetAutomationId(border) == "ProfileBackdropScrim");
                Assert.InRange(
                    Assert.IsType<SolidColorBrush>(profileScrim.Background).Color.A,
                    1,
                    254);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Rect BoundsWithin(FrameworkElement element, Visual ancestor)
        => element
            .TransformToAncestor(ancestor)
            .TransformBounds(new Rect(new Point(0, 0), element.RenderSize));

    private static Color BrushColor(Brush brush)
        => Assert.IsType<SolidColorBrush>(brush).Color;

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

    private sealed class PresetHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
            => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(
                    """[{"id":"msg_3","message":"preset text","messageType":"message","slot":3,"canBeUpdated":true,"remainingCooldownMinutes":0}]""",
                    System.Text.Encoding.UTF8,
                    "application/json")
            });
    }
}
