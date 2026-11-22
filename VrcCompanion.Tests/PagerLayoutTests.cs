using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
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
/// The pagers are part of the layout, not just the view model: each test hosts
/// the real view, finds the pager bound to the page, clicks Next through the
/// button and checks the list the user sees. The empty cases check that a list
/// with no second page hides the pager.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class PagerLayoutTests
{
    [Fact]
    public void FriendsPagerFlipsTheVisibleList()
    {
        WpfTestHost.Run(() =>
        {
            using var api = IsolatedApi();
            using var shell = new MainViewModel(api);
            for (var index = 0; index < 30; index++)
            {
                shell.Friends.Add(new FriendItem(new VrcUser
                {
                    Id = $"usr_layout{index}",
                    DisplayName = $"Layout Friend {index}",
                    State = "offline",
                    Status = "offline",
                    Location = "offline"
                }));
            }

            shell.ShowOnlineOnly = true;
            shell.ShowOnlineOnly = false;

            var window = Host(new MainWindow(shell, initializeOnLoad: false));
            try
            {
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "friends");
                Pump(window);

                var pager = PagerFor(window, shell.FriendsPage);
                Assert.Equal(Visibility.Visible, pager.Visibility);

                var list = FindVisualChildren<ListBox>(window)
                    .Single(box => ReferenceEquals(box.ItemsSource, shell.FriendsPage.Items));
                PumpUntil(window, () => list.Items.Count == 24, "The friends list did not show its first page.");

                ClickNext(pager);
                PumpUntil(window, () => list.Items.Count == 6, "The friends list did not flip to page 2.");

                Assert.Equal(6, list.Items.Count);
                Assert.Equal(2, shell.FriendsPage.PageNumber);
                Assert.Equal("2 of 2", shell.FriendsPage.PageText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FriendsPagerIsHiddenAndTheEmptyStateShownWithoutFriends()
    {
        WpfTestHost.Run(() =>
        {
            using var api = IsolatedApi();
            using var shell = new MainViewModel(api);
            shell.ShowOnlineOnly = true;
            var window = Host(new MainWindow(shell, initializeOnLoad: false));
            try
            {
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "friends");
                Pump(window);

                var pager = PagerFor(window, shell.FriendsPage);
                PumpUntil(
                    window,
                    () => pager.Visibility == Visibility.Collapsed,
                    "The empty friends list showed a pager.");
                Assert.True(shell.HasNoVisibleFriends);
                Assert.False(shell.FriendsPage.HasPages);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ContinuityPeoplePagerFlipsTheList()
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = new ContinuityViewModel();
            viewModel.ShowPeopleCommand.Execute(null);
            for (var index = 0; index < 30; index++)
            {
                viewModel.People.Add(new ContinuityPersonItem(new ContinuityPerson
                {
                    PersonKey = $"person-{index}",
                    UserId = $"usr_person{index}",
                    DisplayName = $"Continuity Person {index}"
                }));
            }

            TestHooks.RefreshContinuity(viewModel);

            var view = new ContinuityView { DataContext = viewModel };
            var window = Host(view);
            try
            {
                var pager = PagerFor(window, viewModel.PeoplePage);
                PumpUntil(
                    window,
                    () => pager.Visibility == Visibility.Visible,
                    "The continuity pager stayed collapsed with 30 people.");

                var list = FindVisualChildren<ListBox>(window)
                    .Single(box => ReferenceEquals(box.ItemsSource, viewModel.PeoplePage.Items));
                PumpUntil(window, () => list.Items.Count == 24, "The people list did not show its first page.");

                ClickNext(pager);
                PumpUntil(window, () => list.Items.Count == 6, "The people list did not flip to page 2.");

                Assert.Equal(6, list.Items.Count);
                Assert.Equal("2 of 2", viewModel.PeoplePage.PageText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ContinuityPagerIsHiddenWhenThereIsNoSecondPage()
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = new ContinuityViewModel();
            viewModel.ShowPeopleCommand.Execute(null);

            var window = Host(new ContinuityView { DataContext = viewModel });
            try
            {
                var peoplePager = PagerFor(window, viewModel.PeoplePage);
                var worldsPager = PagerFor(window, viewModel.WorldsPage);
                PumpUntil(
                    window,
                    () => peoplePager.Visibility == Visibility.Collapsed &&
                          worldsPager.Visibility == Visibility.Collapsed,
                    "A continuity pager with no second page stayed visible.");
                Assert.True(viewModel.IsEmpty);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ContextPagerFlipsTheInstanceRoster()
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = new ContextViewModel();
            for (var index = 0; index < 30; index++)
            {
                viewModel.Players.Add(new InstancePlayerItem(
                    $"usr_context{index}",
                    $"Context Person {index}",
                    isFriend: false,
                    isSelf: false));
            }

            TestHooks.RefreshContext(viewModel);

            var window = Host(new ContextView { DataContext = viewModel });
            try
            {
                var pager = PagerFor(window, viewModel.PlayersPage);
                PumpUntil(
                    window,
                    () => pager.Visibility == Visibility.Visible,
                    "The context pager stayed collapsed with 30 players.");

                ClickNext(pager);
                PumpUntil(
                    window,
                    () => viewModel.PlayersPage.Items.Count == 6,
                    "The context roster did not flip to page 2.");

                Assert.Equal(6, viewModel.PlayersPage.Items.Count);
                Assert.Equal(2, viewModel.PlayersPage.PageNumber);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ContextPagerIsHiddenForAnEmptyInstance()
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = new ContextViewModel();
            var window = Host(new ContextView { DataContext = viewModel });
            try
            {
                var pager = PagerFor(window, viewModel.PlayersPage);
                PumpUntil(
                    window,
                    () => pager.Visibility == Visibility.Collapsed,
                    "The empty context roster showed a pager.");
                Assert.False(viewModel.HasPlayers);
                Assert.Empty(viewModel.PlayersPage.Items);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InstanceRosterPagerFlipsThePlayerList()
    {
        WpfTestHost.Run(() =>
        {
            using var api = IsolatedApi(new NeverCompletingHandler());
            using var shell = new MainViewModel(api);
            var instance = new InstanceViewModel(api, ImageLoader.Shared);
            TestHooks.SetInstance(instance, new VrcInstance
            {
                Id = "wrld_layout:12345",
                InstanceId = "12345",
                WorldId = "wrld_layout",
                Location = "wrld_layout:12345",
                UserCount = 30
            });
            TestHooks.AttachInstanceViewModel(shell, instance);
            TestHooks.SetLoggedIn(shell);

            var window = Host(new MainWindow(shell, initializeOnLoad: false));
            try
            {
                // Opening the popup starts a live refresh; seed after it so the
                // roster under test is the one the pager receives.
                shell.OpenInstanceCommand.Execute(null);
                Pump(window);
                TestHooks.ReplaceInstancePlayers(
                    instance,
                    Enumerable.Range(0, 30)
                        .Select(index => new InstancePlayerItem(
                            $"usr_roster{index}",
                            $"Roster Person {index}",
                            isFriend: false,
                            isSelf: false))
                        .ToList());
                Pump(window);

                Assert.Equal(24, instance.PlayersPage.Items.Count);
                var pager = PagerFor(window, instance.PlayersPage);
                PumpUntil(
                    window,
                    () => pager.Visibility == Visibility.Visible,
                    "The instance roster pager stayed collapsed with 30 players.");

                ClickNext(pager);
                PumpUntil(
                    window,
                    () => instance.PlayersPage.Items.Count == 6,
                    "The instance roster did not flip to page 2.");

                Assert.Equal(6, instance.PlayersPage.Items.Count);
                Assert.Equal("2 of 2", instance.PlayersPage.PageText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InstanceRosterPagerIsHiddenWithoutAnInstance()
    {
        WpfTestHost.Run(() =>
        {
            using var api = IsolatedApi(new NeverCompletingHandler());
            using var shell = new MainViewModel(api);
            var instance = new InstanceViewModel(api, ImageLoader.Shared);
            TestHooks.AttachInstanceViewModel(shell, instance);
            TestHooks.SetLoggedIn(shell);

            var window = Host(new MainWindow(shell, initializeOnLoad: false));
            try
            {
                shell.OpenInstanceCommand.Execute(null);
                Pump(window);

                var pager = PagerFor(window, instance.PlayersPage);
                PumpUntil(
                    window,
                    () => pager.Visibility == Visibility.Collapsed,
                    "The roster pager for an empty instance stayed visible.");
                Assert.False(instance.HasInstance);
                Assert.Empty(instance.PlayersPage.Items);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InstanceRosterNeedsALoggedInSession()
    {
        WpfTestHost.Run(() =>
        {
            using var api = IsolatedApi();
            using var shell = new MainViewModel(api);
            Assert.False(shell.HasLoggedInSession);
            Assert.False(shell.OpenInstanceCommand.CanExecute(null));

            // The account alone is not a session; the session alone has no user.
            TestHooks.SetCurrentUser(shell, new VrcUser { Id = "usr_session", DisplayName = "Session" });
            Assert.False(shell.HasLoggedInSession);
            TestHooks.SetLoggedIn(shell);
            Assert.True(shell.HasLoggedInSession);
        });
    }

    [Fact]
    public void InstanceRosterKeepsItsPageWhenLiveUpdatesRefreshIt()
    {
        using var api = IsolatedApi();
        var instance = new InstanceViewModel(api, ImageLoader.Shared);
        TestHooks.SetInstance(instance, new VrcInstance
        {
            Id = "wrld_page:1",
            InstanceId = "1",
            WorldId = "wrld_page",
            Location = "wrld_page:1",
            UserCount = 30
        });
        var players = Enumerable.Range(0, 30)
            .Select(index => new InstancePlayerItem(
                $"usr_page{index}",
                $"Page Person {index}",
                isFriend: false,
                isSelf: false))
            .ToList();
        TestHooks.ReplaceInstancePlayers(instance, players);

        instance.PlayersPage.NextCommand.Execute(null);
        Assert.Equal(2, instance.PlayersPage.PageNumber);

        // A live join rebuilds the roster; the reader stays on page 2.
        TestHooks.ReplaceInstancePlayers(
            instance,
            players.Append(new InstancePlayerItem("usr_joined", "Someone New", false, false)).ToList());
        Assert.Equal(2, instance.PlayersPage.PageNumber);

        // A new search starts at the first page.
        instance.PlayerSearch = "Page Person";
        Assert.Equal(1, instance.PlayersPage.PageNumber);

        // Another world starts at the first page too.
        instance.PlayerSearch = string.Empty;
        instance.PlayersPage.NextCommand.Execute(null);
        Assert.Equal(2, instance.PlayersPage.PageNumber);
        TestHooks.SetInstance(instance, new VrcInstance
        {
            Id = "wrld_other:2",
            InstanceId = "2",
            WorldId = "wrld_other",
            Location = "wrld_other:2"
        });
        Assert.Equal(1, instance.PlayersPage.PageNumber);
    }

    [Fact]
    public void GroupListsExposeOnePageEach()
    {
        var viewModel = new GroupViewModel(IsolatedApi());

        Assert.Equal(24, viewModel.VisibleGroupsPage.PageSize);
        Assert.Equal(24, viewModel.GroupSearchResultsPage.PageSize);
    }

    private sealed class NeverCompletingHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
            => new TaskCompletionSource<System.Net.Http.HttpResponseMessage>().Task;
    }

    /// <summary>
    /// One test's own session file: layout tests must never read or clear the
    /// signed-in session in the real profile.
    /// </summary>
    private static VrcApiClient IsolatedApi(System.Net.Http.HttpMessageHandler? handler = null)
        => new(
            new SessionStore(Path.Combine(
                Path.GetTempPath(),
                "vrc-pager-" + Guid.NewGuid().ToString("N"))),
            handler);

    private static Window Host(Window window)
    {
        window.Show();
        Pump(window);
        return window;
    }

    private static Window Host(FrameworkElement content)
    {
        var window = new Window
        {
            Content = content,
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
        Pump(window);
        return window;
    }

    /// <summary>
    /// Finds the pager the view built for a page. The section template is created
    /// on a dispatcher turn of its own, so a single layout pass is not enough on
    /// a loaded machine; pump until it appears instead of assuming it is there.
    /// </summary>
    private static ListPager PagerFor(Window window, object page)
    {
        ListPager? found = null;
        PumpUntil(
            window,
            () => (found = FindVisualChildren<ListPager>(window)
                .FirstOrDefault(pager => ReferenceEquals(pager.DataContext, page))) is not null,
            () => DescribeMissingPager(window, page));
        return found!;
    }

    private static string DescribeMissingPager(Window window, object page)
    {
        var host = window.FindName("SectionHost") as Panel;
        var sections = host is null
            ? "no SectionHost"
            : string.Join(
                ", ",
                host.Children.OfType<ContentControl>()
                    .Select(view => $"{view.Visibility}/{view.ContentTemplate?.GetHashCode()}"));
        var pagers = string.Join(
            ", ",
            FindVisualChildren<ListPager>(window)
                .Select(pager => DescribePager(pager)));
        var viewModel = window.DataContext;
        var friendsPage = viewModel?.GetType().GetProperty("FriendsPage")?.GetValue(viewModel);
        var selected = viewModel?.GetType().GetProperty("SelectedSection")?.GetValue(viewModel);
        var selectedKey = selected?.GetType().GetProperty("Key")?.GetValue(selected);
        return $"No pager was created for {page.GetType().Name}. " +
               $"loaded={window.IsLoaded} size={window.ActualWidth}x{window.ActualHeight} " +
               $"sections=[{sections}] pagers=[{pagers}] " +
               $"selected={selectedKey} pageHash={RuntimeHelpers.GetHashCode(page)} " +
               $"vmFriends==page={ReferenceEquals(friendsPage, page)}";
    }

    private static string DescribePager(ListPager pager)
    {
        var context = pager.DataContext;
        if (context is null)
        {
            return "null";
        }

        var items = context.GetType().GetProperty("Items")?.GetValue(context) as System.Collections.ICollection;
        return $"{context.GetType().Name} hash={RuntimeHelpers.GetHashCode(context)} " +
               $"items={items?.Count.ToString() ?? "?"}";
    }

    private static void ClickNext(ListPager pager)
    {
        var next = FindVisualChildren<Button>(pager)
            .Single(button => string.Equals(button.Content as string, "Next", StringComparison.Ordinal));
        Assert.True(next.IsEnabled);
        // Raising ClickEvent bypasses Button.OnClick, which is what runs the
        // command; execute the bound command exactly as a click would.
        next.Command.Execute(next.CommandParameter);
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        // Drain every priority for a bounded slice: section views are created on
        // background turns, so stopping at Loaded would skip them, while waiting
        // on ApplicationIdle can starve behind the app's timer work.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void PumpUntil(Window window, Func<bool> condition, string because)
        => PumpUntil(window, condition, () => because);

    private static void PumpUntil(Window window, Func<bool> condition, Func<string> because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, because());
            Pump(window);
            Thread.Sleep(5);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }
}
