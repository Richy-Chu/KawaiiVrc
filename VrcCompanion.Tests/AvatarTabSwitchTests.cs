using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// Covers the two 1.0.64 behaviours: leaving the Avatars tab hands the library
/// back at once, and switching accounts no longer moves the user to Friends.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class AvatarTabSwitchTests
{
    [Fact]
    public void LeavingTheAvatarsTabReleasesTheLibraryStraightAway()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            SeedLibrary(shell);
            var window = CreateWindow(shell);
            window.Show();

            Select(shell, window, "avatars");
            Assert.NotEmpty(shell.AvatarViewModel!.Avatars);

            Select(shell, window, "friends");

            // The 90 second grace period must not apply to the avatar library.
            Assert.Empty(shell.AvatarViewModel!.Avatars);
            Assert.False(shell.AvatarViewModel.HasLoaded);

            window.Close();
        });
    }

    [Fact]
    public void AReleaseRefusedWhileTheLibraryIsStillLoadingIsRetried()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            SeedLibrary(shell);
            var window = CreateWindow(shell);
            window.AvatarReleaseRetryDelay = TimeSpan.FromMilliseconds(80);
            window.Show();

            Select(shell, window, "avatars");
            TestHooks.SetAvatarBusy(shell.AvatarViewModel!, true);
            Select(shell, window, "friends");

            // Still downloading, so this pass has to leave the rows alone.
            Assert.NotEmpty(shell.AvatarViewModel!.Avatars);

            TestHooks.SetAvatarBusy(shell.AvatarViewModel!, false);

            Assert.True(
                WaitUntil(() => shell.AvatarViewModel!.Avatars.Count == 0),
                "the deferred release never retried, so a library that was still loading stayed resident");

            window.Close();
        });
    }

    [Fact]
    public void ALargeLibraryOnlyBuildsItsFirstChunk()
    {
        using var api = new VrcApiClient();
        var viewModel = new AvatarViewModel(api);
        var rows = Enumerable.Range(0, 200)
            .Select(index => new Avatar { Id = $"avtr_{index}", Name = $"Avatar {index}" })
            .ToArray();

        viewModel.SeedLibraryForTests(rows);

        // A 200-avatar account used to realise 200 cards (and 200 decoded
        // images) the moment the tab opened.
        Assert.Equal(48, viewModel.Avatars.Count);
        Assert.True(viewModel.HasMoreAvatars);
        Assert.Equal(152, viewModel.HiddenAvatarCount);

        viewModel.ShowMoreAvatars();

        Assert.Equal(144, viewModel.Avatars.Count);
        Assert.Equal(56, viewModel.HiddenAvatarCount);

        viewModel.ReleaseLibraryData(keepFavoriteRows: false);

        Assert.False(viewModel.HasMoreAvatars);
        Assert.Equal(0, viewModel.HiddenAvatarCount);
    }

    [Fact]
    public void SwitchingAccountsKeepsTheSectionThatIsOpen()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            var user = new VrcUser { Id = "usr_switched", DisplayName = "Switched" };
            TestHooks.SetLoggedIn(shell);
            TestHooks.SetCurrentUser(shell, user);
            shell.SelectedSection = Section(shell, "accounts");

            shell.ApplySwitchedAccount(user);

            Assert.Equal("accounts", shell.SelectedSection.Key);
        });
    }

    private static void SeedLibrary(MainViewModel shell)
    {
        var avatars = Enumerable.Range(0, 3)
            .Select(index => new Avatar
            {
                Id = $"avtr_{index}",
                Name = $"Avatar {index}"
            })
            .ToArray();

        // A shell without a signed-in session builds no avatar view model, so the
        // test supplies one rather than making the real page load anything.
        var viewModel = new AvatarViewModel(new VrcApiClient());
        TestHooks.AttachAvatarViewModel(shell, viewModel);
        viewModel.SeedLibraryForTests(avatars);
        TestHooks.MarkAvatarsLoaded(viewModel);
    }

    private static NavItem Section(MainViewModel shell, string key)
        => shell.Sections.Single(section => section.Key == key);

    private static void Select(MainViewModel shell, MainWindow window, string key)
    {
        shell.SelectedSection = Section(shell, key);
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
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
}
