using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Controls;
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
    public void TheAvatarGridOnlyKeepsTheCardsNearTheViewportResident()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            SeedLibrary(shell, 200);
            var window = CreateWindow(shell);
            window.Show();

            Select(shell, window, "avatars");

            var viewModel = shell.AvatarViewModel!;
            var panel = AvatarPanel(window);
            Assert.NotNull(panel);

            Assert.Equal(48, viewModel.Avatars.Count);
            var resident = panel!.Children.Count;
            Assert.True(
                resident > 0 && resident < viewModel.Avatars.Count,
                $"the grid kept {resident} of {viewModel.Avatars.Count} published cards resident");

            // Revealing the rest of the library must add scrollable rows, not
            // resident cards: that is the whole point of the panel.
            while (viewModel.HasMoreAvatars)
            {
                viewModel.ShowMoreAvatars();
            }

            window.UpdateLayout();
            var revealed = AvatarPanel(window)!;
            var grid = FindDescendant<ItemsControl>(window, control =>
                string.Equals(control.Name, "AvatarGrid", StringComparison.Ordinal))!;

            Assert.Equal(200, viewModel.Avatars.Count);
            Assert.True(
                revealed.Children.Count < 200,
                $"all {revealed.Children.Count} revealed cards stayed resident");

            // The whole list is scrollable height, but the last card is not
            // built until a scroll asks for it...
            Assert.Null(grid.ItemContainerGenerator.ContainerFromIndex(199));

            var scroll = FindAncestor<ScrollViewer>(revealed)!;
            Assert.True(
                scroll.ExtentHeight > scroll.ViewportHeight,
                "the grid reported no scrollable height for the cards it did not build");

            scroll.ScrollToEnd();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            Assert.NotNull(grid.ItemContainerGenerator.ContainerFromIndex(199));

            // ...and the cards at the top are handed back once they are out of
            // reach, instead of staying resident for the rest of the visit.
            Assert.Null(grid.ItemContainerGenerator.ContainerFromIndex(0));

            window.Close();
        });
    }

    [Fact]
    public void ScrollingThroughTheWholeLibraryNeverLeavesTheGridBlank()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            SeedLibrary(shell, 200);
            var window = CreateWindow(shell);
            window.Show();
            Select(shell, window, "avatars");

            var viewModel = shell.AvatarViewModel!;
            while (viewModel.HasMoreAvatars)
            {
                viewModel.ShowMoreAvatars();
            }

            window.UpdateLayout();
            var panel = AvatarPanel(window)!;
            var scroll = FindAncestor<ScrollViewer>(panel)!;
            var step = Math.Max(120, scroll.ViewportHeight * 0.8);

            var lastTop = -1;
            for (var y = 0d; y <= scroll.ScrollableHeight; y += step)
            {
                scroll.ScrollToVerticalOffset(Math.Min(y, scroll.ScrollableHeight));
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var cards = AvatarPanel(window)!;
                Assert.True(cards.Children.Count > 0, $"the grid was blank at scroll offset {y:0}");

                // Every card on screen draws a distinct avatar from the list,
                // and scrolling down never shows an earlier row than before.
                var indices = cards.Children
                    .Cast<FrameworkElement>()
                    .Select(card => card.DataContext as Avatar)
                    .Select(avatar => avatar is null ? -1 : viewModel.Avatars.IndexOf(avatar))
                    .ToList();

                var foreign = cards.Children
                    .Cast<FrameworkElement>()
                    .Where(card => card.DataContext is not Avatar avatar || viewModel.Avatars.IndexOf(avatar) < 0)
                    .Select(card => (card.DataContext as Avatar)?.DisplayName ?? "<null>")
                    .ToList();
                Assert.True(
                    foreign.Count == 0,
                    $"at {y:0}: {foreign.Count} of {cards.Children.Count} cards drew avatars outside the grid ({string.Join(", ", foreign)})");
                Assert.Equal(indices.Count, indices.Distinct().Count());

                var top = indices.Min();
                Assert.True(top >= lastTop, $"the grid went back to row {top} after {lastTop} at {y:0}");
                lastTop = top;
            }

            window.Close();
        });
    }

    [Fact]
    public void SearchSortAndFavouritesRebuildTheGridWithNoStaleCards()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            // Names alternate so sorting by name cannot accidentally match the
            // order the library arrived in.
            var added = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var rows = Enumerable.Range(0, 60)
                .Select(index => new Avatar
                {
                    Id = $"avtr_{index}",
                    Name = index % 2 == 0 ? $"Bravo {index}" : $"Alpha {index}",
                    // Date-added is newest first, so descending CreatedAt is
                    // the order the rows were seeded in.
                    CreatedAt = added.AddMinutes(-index)
                })
                .ToArray();
            var viewModel = new AvatarViewModel(new VrcApiClient());
            TestHooks.AttachAvatarViewModel(shell, viewModel);
            viewModel.SeedLibraryForTests(rows);
            // The sort is persisted between runs, so pin the starting order.
            viewModel.SelectedAvatarSort = viewModel.AvatarSortOptions.Single(
                option => string.Equals(option.Key, "date-added", StringComparison.Ordinal));
            TestHooks.MarkAvatarsLoaded(viewModel);

            var window = CreateWindow(shell);
            window.Show();
            Select(shell, window, "avatars");

            var grid = AvatarGrid(window)!;
            var panel = AvatarPanel(window)!;

            Assert.Equal(48, viewModel.Avatars.Count);
            Assert.Equal("Bravo 0", FirstCard(panel).DisplayName);
            Assert.True(panel.Children.Count < viewModel.Avatars.Count);

            // A search publishes a brand new collection, which resets the panel.
            viewModel.SearchTerm = "Alpha 47";
            window.UpdateLayout();

            Assert.Single(viewModel.Avatars);
            Assert.Single(panel.Children);
            Assert.Equal("Alpha 47", FirstCard(panel).DisplayName);
            Assert.Null(grid.ItemContainerGenerator.ContainerFromIndex(1));

            viewModel.SearchTerm = string.Empty;
            window.UpdateLayout();

            Assert.Equal(48, viewModel.Avatars.Count);
            Assert.Equal("Bravo 0", FirstCard(panel).DisplayName);

            // Sorting reorders the same library, so the first card must change
            // even though the panel was already showing those avatars.
            viewModel.SelectedAvatarSort = viewModel.AvatarSortOptions.Single(
                option => string.Equals(option.Key, "name", StringComparison.Ordinal));
            window.UpdateLayout();

            Assert.Equal("Alpha 1", FirstCard(panel).DisplayName);
            Assert.True(panel.Children.Count < viewModel.Avatars.Count);

            // Favourites is another filter over the same library.
            viewModel.SeedFavoritesForTests(rows.Take(3));
            viewModel.FavoritesOnly = true;
            window.UpdateLayout();

            Assert.Equal(3, viewModel.Avatars.Count);
            Assert.Equal(3, panel.Children.Count);
            foreach (FrameworkElement card in panel.Children)
            {
                Assert.True(((Avatar)card.DataContext).IsFavorite);
            }

            viewModel.FavoritesOnly = false;
            window.UpdateLayout();

            Assert.Equal(48, viewModel.Avatars.Count);
            Assert.True(panel.Children.Count < 48);
            foreach (FrameworkElement card in panel.Children)
            {
                var index = grid.ItemContainerGenerator.IndexFromContainer(card);
                Assert.InRange(index, 0, viewModel.Avatars.Count - 1);
                Assert.Same(viewModel.Avatars[index], card.DataContext);
            }

            window.Close();
        });
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

    private static ItemsControl? AvatarGrid(DependencyObject root)
        => FindDescendant<ItemsControl>(root, control =>
            string.Equals(control.Name, "AvatarGrid", StringComparison.Ordinal));

    private static VirtualizingWrapPanel? AvatarPanel(DependencyObject root)
    {
        var grid = AvatarGrid(root);
        return grid is null ? null : FindDescendant<VirtualizingWrapPanel>(grid);
    }

    private static Avatar FirstCard(VirtualizingWrapPanel panel)
        => Assert.IsType<Avatar>(((FrameworkElement)panel.Children[0]).DataContext);

    private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool>? match = null)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed && (match is null || match(typed)))
            {
                return typed;
            }

            if (FindDescendant(child, match) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static void SeedLibrary(MainViewModel shell, int count = 3)
    {
        var avatars = Enumerable.Range(0, count)
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
