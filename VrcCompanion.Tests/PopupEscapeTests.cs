using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The window's popups share a dim backdrop, and Escape used to do nothing to
/// several of them. Every overlay in MainWindow.xaml is in the Escape chain now,
/// in its z-order, so one press closes the popup (or inner layer) on top:
/// update (400), advanced launch (62), composers (61/60), favorite slot picker
/// (50), avatar details (40), launch chooser (35), profile groups and group info
/// (ProfileZIndex + 1), friend profile (30/34) with instance history (32),
/// recent players (25), friend changes (24), friend locations (23), boop preview
/// (20), world dialog (20).
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class PopupEscapeTests
{
    [Fact]
    public void EscapeClosesTheAvatarDetailsPopup()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            var avatars = new AvatarViewModel(IsolatedApi());
            TestHooks.AttachAvatarViewModel(shell, avatars);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                var overlay = FindByName(window, "AvatarDetailsOverlay");
                Assert.NotNull(overlay);

                avatars.ShowAvatarDetails(new Avatar
                {
                    Id = "avtr_escape",
                    Name = "Escape Test"
                });
                window.UpdateLayout();

                Assert.True(avatars.IsAvatarDetailsOpen);
                Assert.Equal(Visibility.Visible, overlay!.Visibility);

                var escape = RaiseEscape(window);
                window.UpdateLayout();

                Assert.True(escape.Handled, "Escape should close the avatar details popup.");
                Assert.False(avatars.IsAvatarDetailsOpen);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeClosesTheInstanceHistoryPopup()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            using var history = new InstanceHistoryViewModel(IsolatedApi(), IsolatedHistory());
            TestHooks.AttachInstanceHistoryViewModel(shell, history);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                var overlay = FindByName(window, "InstanceHistoryOverlay");
                Assert.NotNull(overlay);

                history.Show();
                window.UpdateLayout();

                Assert.True(history.IsOpen);
                Assert.Equal(Visibility.Visible, overlay!.Visibility);

                var escape = RaiseEscape(window);
                window.UpdateLayout();

                Assert.True(escape.Handled, "Escape should close the instance history popup.");
                Assert.False(history.IsOpen);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeLeavesTheSessionDetailBeforeItClosesInstanceHistory()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            using var history = new InstanceHistoryViewModel(IsolatedApi(), HistoryWithOneSession());
            history.SetAccount(new VrcUser { Id = "usr_escape", DisplayName = "Me" });
            history.RefreshAsync().GetAwaiter().GetResult();
            TestHooks.AttachInstanceHistoryViewModel(shell, history);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                var overlay = FindByName(window, "InstanceHistoryOverlay");
                var detail = FindByName(window, "InstanceHistoryDetailOverlay");
                Assert.NotNull(overlay);
                Assert.NotNull(detail);

                history.Show();
                window.UpdateLayout();
                Assert.True(history.IsOpen);
                Assert.Equal(Visibility.Visible, overlay!.Visibility);

                var entry = Assert.Single(history.Entries);
                entry.OpenCommand.Execute(null);
                window.UpdateLayout();

                Assert.True(history.IsDetailVisible);
                Assert.Equal(Visibility.Visible, detail!.Visibility);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.True(history.IsOpen, "The first Escape goes back to the list.");
                Assert.False(history.IsDetailVisible);
                Assert.Null(history.SelectedEntry);
                Assert.Equal(Visibility.Collapsed, detail.Visibility);
                Assert.Equal(Visibility.Visible, overlay.Visibility);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(history.IsOpen, "The next Escape closes the popup.");
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeClosesTheFriendLocationsPopup()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                var overlay = FindByName(window, "FriendLocationsOverlay");
                Assert.NotNull(overlay);

                shell.OpenFriendLocationsCommand.Execute(null);
                window.UpdateLayout();

                Assert.True(shell.IsFriendLocationsOpen);
                Assert.Equal(Visibility.Visible, overlay!.Visibility);

                var escape = RaiseEscape(window);
                window.UpdateLayout();

                Assert.True(escape.Handled, "Escape should close the friend locations popup.");
                Assert.False(shell.IsFriendLocationsOpen);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeClosesTheRecentPlayersPopup()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            var recent = new RecentPlayersViewModel(IsolatedApi(), IsolatedHistory());
            TestHooks.AttachRecentPlayersViewModel(shell, recent);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                var overlay = FindByName(window, "RecentPlayersOverlay");
                Assert.NotNull(overlay);

                recent.Show("usr_escape", Array.Empty<string>());
                window.UpdateLayout();

                Assert.True(recent.IsOpen);
                Assert.Equal(Visibility.Visible, overlay!.Visibility);

                var escape = RaiseEscape(window);
                window.UpdateLayout();

                Assert.True(escape.Handled, "Escape should close the recent players popup.");
                Assert.False(recent.IsOpen);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeClosesTheFriendChangesPopup()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                var overlay = FindByName(window, "FriendChangesOverlay");
                Assert.NotNull(overlay);

                shell.OpenFriendChangesCommand.Execute(null);
                window.UpdateLayout();

                Assert.True(shell.IsFriendChangesOpen);
                Assert.Equal(Visibility.Visible, overlay!.Visibility);

                var escape = RaiseEscape(window);
                window.UpdateLayout();

                Assert.True(escape.Handled, "Escape should close the friend changes popup.");
                Assert.False(shell.IsFriendChangesOpen);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeClosesTheUpdatePrompt()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                var overlay = FindByName(window, "UpdateOverlay");
                Assert.NotNull(overlay);

                shell.UpdateViewModel.ShowForTests("9.9.9");
                window.UpdateLayout();

                Assert.True(shell.UpdateViewModel.IsOpen);
                Assert.Equal(Visibility.Visible, overlay!.Visibility);

                var escape = RaiseEscape(window);
                window.UpdateLayout();

                Assert.True(escape.Handled, "Escape should close the update prompt.");
                Assert.False(shell.UpdateViewModel.IsOpen);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeClosesTheAdvancedLaunchPanel()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                var overlay = FindByName(window, "AdvancedLaunchOverlay");
                Assert.NotNull(overlay);

                shell.OpenAdvancedLaunchCommand.Execute(null);
                window.UpdateLayout();

                Assert.True(shell.IsAdvancedLaunchOpen);
                Assert.Equal(Visibility.Visible, overlay!.Visibility);

                var escape = RaiseEscape(window);
                window.UpdateLayout();

                Assert.True(escape.Handled, "Escape should close the advanced launch panel.");
                Assert.False(shell.IsAdvancedLaunchOpen);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeClosesTheFavoriteSlotPickerBeforeTheAvatarDetails()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            var avatars = new AvatarViewModel(IsolatedApi());
            TestHooks.AttachAvatarViewModel(shell, avatars);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                avatars.ShowAvatarDetails(new Avatar { Id = "avtr_escape", Name = "Escape Test" });
                avatars.OpenSlotPickerForTests("avtr_escape");
                window.UpdateLayout();

                Assert.True(avatars.IsSlotPickerOpen);
                Assert.True(avatars.IsAvatarDetailsOpen);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(avatars.IsSlotPickerOpen, "The slot picker (50) is on top.");
                Assert.True(avatars.IsAvatarDetailsOpen);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(avatars.IsAvatarDetailsOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeClosesTheProfileGroupsBeforeTheProfile()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            var profile = Profile();
            TestHooks.AttachFriendDetail(shell, profile);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                var overlay = FindByName(window, "ProfileGroupsOverlay");
                Assert.NotNull(overlay);

                profile.OpenGroupsCommand.Execute(null);
                window.UpdateLayout();

                Assert.True(profile.IsGroupsOpen);
                Assert.True(shell.HasSelectedFriend);
                Assert.Equal(Visibility.Visible, overlay!.Visibility);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(profile.IsGroupsOpen, "The group list sits above the profile.");
                Assert.True(shell.HasSelectedFriend);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(shell.HasSelectedFriend);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeClosesTheGroupInfoPopup()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            var groups = new GroupViewModel(IsolatedApi());
            TestHooks.AttachGroupViewModel(shell, groups);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                var overlay = FindByName(window, "GroupInfoOverlay");
                Assert.NotNull(overlay);

                groups.OpenGroupInfoForTests(new Group { Id = "grp_escape", Name = "Escape Group" });
                window.UpdateLayout();

                Assert.True(groups.IsGroupInfoOpen);
                Assert.Equal(Visibility.Visible, overlay!.Visibility);

                var escape = RaiseEscape(window);
                window.UpdateLayout();

                Assert.True(escape.Handled, "Escape should close the group info popup.");
                Assert.False(groups.IsGroupInfoOpen);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeClosesTheBoopPreview()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                // The preview lives inside the notifications page, so the page has
                // to be the shown section before its overlay is in the tree.
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "notifications");
                window.UpdateLayout();

                var overlay = FindByName(window, "BoopPreviewOverlay");
                Assert.NotNull(overlay);

                var notification = new NotificationItem(new VrcNotification
                {
                    Id = "not_escape",
                    Type = "boop",
                    SenderUserId = "usr_escape"
                });
                notification.ApplyBoopAttachment("Happy", "https://example.test/boop.png", "emoji");
                shell.OpenBoopPreviewCommand.Execute(notification);
                window.UpdateLayout();

                Assert.True(shell.IsBoopPreviewOpen);
                Assert.Equal(Visibility.Visible, overlay!.Visibility);

                var escape = RaiseEscape(window);
                window.UpdateLayout();

                Assert.True(escape.Handled, "Escape should close the boop preview.");
                Assert.False(shell.IsBoopPreviewOpen);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeClosesHistoryBeforeTheProfileThatOpenedIt()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            var profile = Profile();
            TestHooks.AttachFriendDetail(shell, profile);
            using var history = new InstanceHistoryViewModel(IsolatedApi(), HistoryWithOneSession());
            TestHooks.AttachInstanceHistoryViewModel(shell, history);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                history.Show();
                history.CanReturnToProfile = true;
                window.UpdateLayout();

                Assert.True(history.IsOpen);
                Assert.True(shell.HasSelectedFriend);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(history.IsOpen, "History (32) sits above the profile (30) that opened it.");
                Assert.True(shell.HasSelectedFriend);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(shell.HasSelectedFriend);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void OnePressClosesThePopupOnTopOnly()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            var avatars = new AvatarViewModel(IsolatedApi());
            TestHooks.AttachAvatarViewModel(shell, avatars);
            using var history = new InstanceHistoryViewModel(IsolatedApi(), IsolatedHistory());
            TestHooks.AttachInstanceHistoryViewModel(shell, history);
            var recent = new RecentPlayersViewModel(IsolatedApi(), IsolatedHistory());
            TestHooks.AttachRecentPlayersViewModel(shell, recent);
            var window = CreateWindow(shell);
            window.Show();
            try
            {
                avatars.ShowAvatarDetails(new Avatar { Id = "avtr_escape", Name = "Escape Test" });
                history.Show();
                recent.Show("usr_escape", Array.Empty<string>());
                shell.OpenFriendChangesCommand.Execute(null);
                shell.OpenFriendLocationsCommand.Execute(null);
                window.UpdateLayout();
                Assert.True(avatars.IsAvatarDetailsOpen);
                Assert.True(history.IsOpen);
                Assert.True(recent.IsOpen);
                Assert.True(shell.IsFriendChangesOpen);
                Assert.True(shell.IsFriendLocationsOpen);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(avatars.IsAvatarDetailsOpen, "Avatar details (40) is on top.");
                Assert.True(history.IsOpen);
                Assert.True(recent.IsOpen);
                Assert.True(shell.IsFriendChangesOpen);
                Assert.True(shell.IsFriendLocationsOpen);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(history.IsOpen, "Instance history (32) is next.");
                Assert.True(recent.IsOpen);
                Assert.True(shell.IsFriendChangesOpen);
                Assert.True(shell.IsFriendLocationsOpen);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(recent.IsOpen, "Recent players (25) is next.");
                Assert.True(shell.IsFriendChangesOpen);
                Assert.True(shell.IsFriendLocationsOpen);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(shell.IsFriendChangesOpen, "Friend changes (24) is next.");
                Assert.True(shell.IsFriendLocationsOpen, "One press closes only the popup on top.");

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(shell.IsFriendLocationsOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static KeyEventArgs RaiseEscape(Window window)
    {
        var escape = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window)!,
            0,
            Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        window.RaiseEvent(escape);
        return escape;
    }

    private static FriendDetailViewModel Profile()
        => new(
            new FriendItem(new VrcUser
            {
                Id = "usr_escape",
                DisplayName = "Escape Test",
                Username = "escape",
                IsFriend = true
            }),
            IsolatedApi(),
            ImageLoader.Shared);

    private static MainWindow CreateWindow(MainViewModel shell)
        => new(shell, initializeOnLoad: false)
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

    /// <summary>A session file this test owns; never the signed-in user's.</summary>
    private static VrcApiClient IsolatedApi()
        => new(new SessionStore(Path.Combine(
            Path.GetTempPath(),
            "vrc-popup-escape-" + Guid.NewGuid().ToString("N"))));

    /// <summary>A history database this test owns; never the signed-in user's.</summary>
    private static InstanceHistoryStore IsolatedHistory()
        => new(Path.Combine(
            Path.GetTempPath(),
            "vrc-popup-escape-" + Guid.NewGuid().ToString("N") + ".db"));

    /// <summary>One finished session, so a detail row can be opened.</summary>
    private static InstanceHistoryStore HistoryWithOneSession()
    {
        var store = IsolatedHistory();
        var start = DateTimeOffset.Now.AddHours(-1);
        var entry = InstanceHistoryStore.NewFromLog(
            "usr_escape",
            "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345~region(eu)",
            start)!;
        // Every metadata field is set so the refresh has no world to fetch; a
        // fetch would await on this dispatcher and never come back.
        entry.WorldName = "Escape Test World";
        entry.WorldThumbnailUrl = "https://example.test/world.png";
        entry.WorldAuthorName = "Creator";
        entry.GetOrAddPlayer("usr_escape", "Me").StartVisit(start);
        entry.Close(start.AddMinutes(10));
        store.Add(entry);
        return store;
    }

    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name)
        {
            return element;
        }

        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
        {
            var found = FindByName(VisualTreeHelper.GetChild(root, i), name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
