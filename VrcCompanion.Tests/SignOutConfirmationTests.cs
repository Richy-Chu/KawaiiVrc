using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// Sign out sits next to Refresh in the top bar and inside the Settings account
/// card, so a stray click used to drop the saved session. Both buttons open a
/// confirmation now; only its Sign out runs the sign-out.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class SignOutConfirmationTests
{
    [Fact]
    public void SigningOutAsksBeforeItDropsTheSession()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);

            shell.LogoutCommand.Execute(null);

            Assert.True(shell.IsLogoutConfirmationOpen);
            Assert.True(shell.IsLoggedIn);
            Assert.True(shell.ConfirmLogoutCommand.CanExecute(null));
        });
    }

    [Fact]
    public void CancellingTheConfirmationKeepsTheSession()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            shell.LogoutCommand.Execute(null);

            shell.CancelLogoutCommand.Execute(null);

            Assert.False(shell.IsLogoutConfirmationOpen);
            Assert.True(shell.IsLoggedIn);
        });
    }

    [Fact]
    public void ConfirmingSignsOutAndClosesTheDialog()
    {
        WpfTestHost.RunAsync(async () =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            shell.LogoutCommand.Execute(null);

            await shell.LogoutAsync();

            Assert.False(shell.IsLoggedIn);
            Assert.False(shell.IsLogoutConfirmationOpen);
            Assert.False(shell.IsBusy);
        });
    }

    [Fact]
    public void TheWindowShowsTheDialogAndKeepsTheSessionUntilItIsConfirmed()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
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
            window.Show();
            try
            {
                var overlay = FindByName(window, "SignOutConfirmationOverlay");
                Assert.NotNull(overlay);
                Assert.Equal(Visibility.Collapsed, overlay!.Visibility);

                shell.LogoutCommand.Execute(null);
                window.UpdateLayout();

                Assert.Equal(Visibility.Visible, overlay.Visibility);
                Assert.True(shell.IsLoggedIn);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A session file this test owns; never the signed-in user's.</summary>
    private static VrcApiClient IsolatedApi()
        => new(new SessionStore(Path.Combine(
            Path.GetTempPath(),
            "vrc-signout-" + Guid.NewGuid().ToString("N"))));

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
