using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// Friend changes, friend locations and recent players fill the window with a
/// backdrop, but Escape used to do nothing to them. They are in the same chain as
/// the sign-out card now, in their z-order: recent players (25) sits over friend
/// changes (24), which sits over friend locations (23), so one press closes the
/// popup on top.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class PopupEscapeTests
{
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
    public void OnePressClosesThePopupOnTopOnly()
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
                recent.Show("usr_escape", Array.Empty<string>());
                shell.OpenFriendChangesCommand.Execute(null);
                shell.OpenFriendLocationsCommand.Execute(null);
                window.UpdateLayout();
                Assert.True(recent.IsOpen);
                Assert.True(shell.IsFriendChangesOpen);
                Assert.True(shell.IsFriendLocationsOpen);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(recent.IsOpen);
                Assert.True(shell.IsFriendChangesOpen, "One press closes only the popup on top.");
                Assert.True(shell.IsFriendLocationsOpen);

                RaiseEscape(window);
                window.UpdateLayout();

                Assert.False(shell.IsFriendChangesOpen);
                Assert.True(shell.IsFriendLocationsOpen);

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
