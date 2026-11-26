using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Controls;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The sign-in screen offers the accounts saved on this PC, and the Accounts page
/// leads back to it without ending their stored sessions. The window cases read
/// the live visual tree, so a binding that stops finding the list fails here.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class SavedAccountSignInTests
{
    private const string RichyId = "usr_saved_richy";
    private const string ChebeId = "usr_saved_chebe";

    [Fact]
    public void SavedAccountsSurviveARestartAndAreListedForTheSignInScreen()
    {
        var directory = NewAccountsDirectory();
        try
        {
            var first = new MultiAccountService(directory);
            first.RegisterAccount(RichyId, "cookie-richy", "RichyTru");
            first.RegisterAccount(ChebeId, "cookie-chebe", "chebe");
            first.SwitchToAccount(ChebeId);

            // What the next launch sees: the account list and its encrypted sessions
            // come back from disk, not from the instance that saved them.
            var reloaded = new MultiAccountService(directory);
            using var api = new VrcApiClient();
            var accounts = new MultiAccountViewModel(api, reloaded);

            Assert.True(accounts.HasAccounts, "The sign-in screen needs to see the saved accounts.");
            Assert.Equal(new[] { RichyId, ChebeId }, accounts.Accounts.Select(entry => entry.Id).ToArray());
            Assert.Equal(new[] { "RichyTru", "chebe" }, accounts.Accounts.Select(entry => entry.DisplayName).ToArray());
            Assert.Equal("R", accounts.Accounts[0].Initial);
            Assert.Equal("C", accounts.Accounts[1].Initial);
            Assert.Equal("cookie-richy", reloaded.GetAuthCookie(RichyId));
            Assert.Equal("cookie-chebe", reloaded.GetAuthCookie(ChebeId));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ReturningToSignInKeepsEverySavedSession()
    {
        var directory = NewAccountsDirectory();
        try
        {
            var store = new MultiAccountService(directory);
            store.RegisterAccount(RichyId, "cookie-richy", "RichyTru");
            store.RegisterAccount(ChebeId, "cookie-chebe", "chebe");
            store.SwitchToAccount(ChebeId);

            using var shell = new MainViewModel();
            TestHooks.AttachMultiAccountViewModel(
                shell,
                new MultiAccountViewModel(new VrcApiClient(), new MultiAccountService(directory)));
            TestHooks.SetLoggedIn(shell);
            TestHooks.SetCurrentUser(shell, new VrcUser
            {
                Id = ChebeId,
                DisplayName = "chebe",
                Username = "chebe"
            });

            shell.ReturnToSignIn();

            Assert.False(shell.IsLoggedIn);
            Assert.True(shell.MultiAccountViewModel!.HasAccounts, "The sign-in screen must keep the saved list.");
            Assert.Equal(2, shell.MultiAccountViewModel.Accounts.Count);

            // The parked session is not a sign out: both cookies are still usable.
            Assert.Equal("cookie-richy", store.GetAuthCookie(RichyId));
            Assert.Equal("cookie-chebe", store.GetAuthCookie(ChebeId));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void TheAccountsPageLeadsToTheSignInScreenWithTheSavedAccountsOnIt()
    {
        WpfTestHost.Run(() =>
        {
            var directory = NewAccountsDirectory();
            try
            {
                var store = new MultiAccountService(directory);
                store.RegisterAccount(RichyId, "cookie-richy", "RichyTru");
                store.RegisterAccount(ChebeId, "cookie-chebe", "chebe");
                store.SwitchToAccount(ChebeId);

                using var shell = new MainViewModel();
                TestHooks.AttachMultiAccountViewModel(
                    shell,
                    new MultiAccountViewModel(new VrcApiClient(), new MultiAccountService(directory)));
                TestHooks.SetLoggedIn(shell);
                TestHooks.SetCurrentUser(shell, new VrcUser
                {
                    Id = ChebeId,
                    DisplayName = "chebe",
                    Username = "chebe"
                });

                var window = CreateWindow(shell);

                try
                {
                    window.Show();
                    shell.SelectedSection = shell.Sections.Single(section => section.Key == "accounts");
                    Pump(window);

                    var back = FindVisualChildren<Button>(window)
                        .Single(button => (button.Content as string) == "Back to sign-in");
                    Assert.True(back.IsVisible, "The Accounts page needs a visible way back to sign-in.");
                    Assert.Same(shell.ReturnToSignInCommand, back.Command);

                    // Raising ClickEvent bypasses Button.OnClick, which is what runs the command.
                    back.Command.Execute(back.CommandParameter);
                    Pump(window);

                    Assert.False(shell.IsLoggedIn);

                    var heading = FindVisualChildren<TextBlock>(window)
                        .Single(text => text.Text == "SAVED ACCOUNTS");
                    Assert.True(heading.IsVisible, "The sign-in screen should show the saved accounts.");

                    var listed = FindVisualChildren<TextBlock>(window)
                        .Where(text => text.IsVisible && (text.Text == "RichyTru" || text.Text == "chebe"))
                        .Select(text => text.Text)
                        .Distinct()
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray();
                    Assert.Equal(new[] { "RichyTru", "chebe" }, listed);

                    // One row button per saved account, each wired to the switch that
                    // signs in with that account's stored session.
                    var signIn = FindVisualChildren<Button>(window)
                        .Where(button => button.IsVisible &&
                                         ReferenceEquals(button.Command, shell.MultiAccountViewModel!.SwitchAccountCommand))
                        .ToArray();
                    Assert.Equal(2, signIn.Length);
                    Assert.Equal(
                        new[] { RichyId, ChebeId }.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                        signIn.Select(button => (string)button.CommandParameter).OrderBy(id => id, StringComparer.Ordinal).ToArray());

                    // Both stored sessions are still readable after leaving the page.
                    Assert.Equal("cookie-richy", store.GetAuthCookie(RichyId));
                    Assert.Equal("cookie-chebe", store.GetAuthCookie(ChebeId));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                DeleteDirectory(directory);
            }
        });
    }

    [Fact]
    public void SavedAccountPicturesSurviveARestartAndAreKeptOnResave()
    {
        var directory = NewAccountsDirectory();
        try
        {
            var first = new MultiAccountService(directory);
            first.RegisterAccount(RichyId, "cookie-richy", "RichyTru", "https://example.test/richy.png");
            first.SwitchToAccount(RichyId);

            // The picture comes back from disk with the rest of the profile.
            var reloaded = new MultiAccountService(directory);
            Assert.Equal("https://example.test/richy.png", reloaded.GetAccountImageUrl(RichyId));

            // Signing that session in again without artwork must not erase the icon.
            reloaded.RegisterAccount(RichyId, "cookie-richy-2", "RichyTru");
            Assert.Equal("https://example.test/richy.png", reloaded.GetAccountImageUrl(RichyId));
            Assert.Equal("RichyTru", reloaded.GetAccountDisplayName(RichyId));
            Assert.Equal("cookie-richy-2", reloaded.GetAuthCookie(RichyId));

            // A newer picture does replace the stored one.
            reloaded.RegisterAccount(RichyId, "cookie-richy-2", "RichyTru", "https://example.test/new.png");
            Assert.Equal("https://example.test/new.png", reloaded.GetAccountImageUrl(RichyId));

            using var api = new VrcApiClient();
            var accounts = new MultiAccountViewModel(api, new MultiAccountService(directory));
            Assert.Equal("https://example.test/new.png", accounts.Accounts.Single().ImageUrl);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void TheSignedInPictureFillsItsRowBeforeAnythingWasStored()
    {
        var directory = NewAccountsDirectory();
        try
        {
            var store = new MultiAccountService(directory);
            store.RegisterAccount(RichyId, "cookie-richy", "RichyTru");
            store.SwitchToAccount(RichyId);

            using var api = new VrcApiClient();
            TestHooks.SetCurrentUser(api, new VrcUser
            {
                Id = RichyId,
                DisplayName = "RichyTru",
                UserIcon = "https://example.test/richy.png"
            });

            var accounts = new MultiAccountViewModel(api, store);

            Assert.Equal("https://example.test/richy.png", accounts.ActiveAccountImageUrl);
            Assert.Equal("https://example.test/richy.png", accounts.Accounts.Single().ImageUrl);
            Assert.Equal("https://example.test/richy.png", store.GetAccountImageUrl(RichyId));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void EveryAccountRowShowsTheProfilesPicture()
    {
        WpfTestHost.Run(() =>
        {
            var directory = NewAccountsDirectory();
            try
            {
                var store = new MultiAccountService(directory);
                store.RegisterAccount(RichyId, "cookie-richy", "RichyTru", "https://example.test/richy.png");
                store.RegisterAccount(ChebeId, "cookie-chebe", "chebe", "https://example.test/chebe.png");
                store.SwitchToAccount(RichyId);

                using var api = new VrcApiClient();
                TestHooks.SetCurrentUser(api, new VrcUser
                {
                    Id = RichyId,
                    DisplayName = "RichyTru",
                    Username = "RichyTru",
                    UserIcon = "https://example.test/richy.png"
                });

                using var shell = new MainViewModel();
                TestHooks.AttachMultiAccountViewModel(
                    shell,
                    new MultiAccountViewModel(api, new MultiAccountService(directory)));
                TestHooks.SetLoggedIn(shell);

                var window = CreateWindow(shell);
                try
                {
                    window.Show();
                    shell.SelectedSection = shell.Sections.Single(section => section.Key == "accounts");
                    Pump(window);

                    // The header shows the signed-in picture and both saved rows
                    // carry their own, so the letters are only a fallback now.
                    var icons = FindVisualChildren<AsyncImage>(window)
                        .Where(image => image.IsVisible && !string.IsNullOrWhiteSpace(image.SourceUrl))
                        .Select(image => image.SourceUrl!)
                        .ToArray();
                    Assert.Equal(2, icons.Count(url => url == "https://example.test/richy.png"));
                    Assert.Equal(1, icons.Count(url => url == "https://example.test/chebe.png"));

                    // The sign-in screen lists the same accounts with the same pictures.
                    shell.ReturnToSignIn();
                    Pump(window);

                    var signInIcons = FindVisualChildren<AsyncImage>(window)
                        .Where(image => image.IsVisible && !string.IsNullOrWhiteSpace(image.SourceUrl))
                        .Select(image => image.SourceUrl!)
                        .ToArray();
                    Assert.Contains("https://example.test/richy.png", signInIcons);
                    Assert.Contains("https://example.test/chebe.png", signInIcons);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                DeleteDirectory(directory);
            }
        });
    }

    private static string NewAccountsDirectory()
        => Path.Combine(Path.GetTempPath(), "vrccompanion-accounts-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover scratch folder is not what these cases are about.
        }
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

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

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
