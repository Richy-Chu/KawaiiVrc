using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    public async Task WearingAnAvatarPastTheFirstChunkSelectsThatAvatar()
    {
        var handler = new AvatarSelectHandler();
        using var api = new VrcApiClient(null, handler);
        var viewModel = new AvatarViewModel(api);
        var rows = Library(200);
        viewModel.SeedLibraryForTests(rows);

        // The card is not built, so only the library kept behind the chunk can
        // still answer for this avatar id.
        Assert.Equal(48, viewModel.Avatars.Count);
        Assert.DoesNotContain(viewModel.Avatars, avatar => avatar.Id == "avtr_199");

        var worn = new TaskCompletionSource<Avatar>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.AvatarWorn += avatar => worn.TrySetResult(avatar);

        ((AsyncRelayCommand<string>)viewModel.WearAvatarCommand).Execute("avtr_199");

        Assert.Equal(
            "avtr_199",
            await handler.Selected.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            "avtr_199",
            (await worn.Task.WaitAsync(TimeSpan.FromSeconds(5))).Id);

        // The library row itself carries the worn state, not a copy of the chunk.
        Assert.True(rows[199].IsWearing);
    }

    [Fact]
    public void FavouritingAnAvatarPastTheFirstChunkFindsItInTheLibrary()
    {
        using var api = new VrcApiClient();
        var viewModel = new AvatarViewModel(api);
        viewModel.SeedLibraryForTests(Library(200));

        Assert.Equal(48, viewModel.Avatars.Count);

        // The star button passes the card's id, so the slot picker has to
        // resolve an avatar whose card was never built.
        viewModel.OpenSlotPickerForTests("avtr_199");

        Assert.True(viewModel.IsSlotPickerOpen);
        Assert.Equal("Avatar 199", viewModel.PendingFavoriteAvatarName);
    }

    [Fact]
    public void SearchStillReachesAvatarsWhoseCardsAreNotBuilt()
    {
        using var api = new VrcApiClient();
        var viewModel = new AvatarViewModel(api);
        viewModel.SeedLibraryForTests(Library(200));

        Assert.Equal(48, viewModel.Avatars.Count);

        // Search filters the library, so a match past the first chunk must
        // still become a card.
        viewModel.SearchTerm = "Avatar 199";

        var match = Assert.Single(viewModel.Avatars);
        Assert.Equal("avtr_199", match.Id);
    }

    [Fact]
    public void TheSummaryCountsTheWholeFilteredLibraryNotJustTheBuiltChunk()
    {
        using var api = new VrcApiClient();
        var viewModel = new AvatarViewModel(api);
        var rows = Library(200);
        viewModel.SeedLibraryForTests(rows);

        Assert.Equal("200 uploaded avatars", viewModel.SummaryText);

        viewModel.SearchTerm = "Avatar";

        // Every avatar matches, but only the first chunk is a card.
        Assert.Equal(48, viewModel.Avatars.Count);
        Assert.Equal("200 of 200 uploaded avatars", viewModel.SummaryText);

        viewModel.SearchTerm = string.Empty;
        viewModel.SeedFavoritesForTests(rows);
        viewModel.FavoritesOnly = true;

        Assert.Equal(48, viewModel.Avatars.Count);
        Assert.Equal("200 favourite avatars", viewModel.SummaryText);
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

    private static Avatar[] Library(int count)
        => Enumerable.Range(0, count)
            .Select(index => new Avatar { Id = $"avtr_{index}", Name = $"Avatar {index}" })
            .ToArray();

    /// <summary>Answers every request, and reports the avatar id the app selected.</summary>
    private sealed class AvatarSelectHandler : HttpMessageHandler
    {
        public TaskCompletionSource<string> Selected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/select", StringComparison.Ordinal))
            {
                Selected.TrySetResult(path.Split('/')[^2]);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
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
