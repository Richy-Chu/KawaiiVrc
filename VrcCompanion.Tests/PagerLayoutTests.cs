using System;
using System.Collections.Generic;
using System.Linq;
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
            using var shell = new MainViewModel();
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
                Assert.Equal(24, list.Items.Count);

                ClickNext(pager);
                Pump(window);

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
            using var shell = new MainViewModel();
            shell.ShowOnlineOnly = true;
            var window = Host(new MainWindow(shell, initializeOnLoad: false));
            try
            {
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "friends");
                Pump(window);

                var pager = PagerFor(window, shell.FriendsPage);
                Assert.Equal(Visibility.Collapsed, pager.Visibility);
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
                Assert.Equal(Visibility.Visible, pager.Visibility);

                var list = FindVisualChildren<ListBox>(window)
                    .Single(box => ReferenceEquals(box.ItemsSource, viewModel.PeoplePage.Items));
                Assert.Equal(24, list.Items.Count);

                ClickNext(pager);
                Pump(window);

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
                Assert.Equal(Visibility.Collapsed, PagerFor(window, viewModel.PeoplePage).Visibility);
                Assert.Equal(Visibility.Collapsed, PagerFor(window, viewModel.WorldsPage).Visibility);
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
                Assert.Equal(Visibility.Visible, pager.Visibility);

                ClickNext(pager);
                Pump(window);

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
                Assert.Equal(Visibility.Collapsed, PagerFor(window, viewModel.PlayersPage).Visibility);
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
            using var api = new VrcApiClient(null, new NeverCompletingHandler());
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
                Assert.Equal(Visibility.Visible, pager.Visibility);

                ClickNext(pager);
                Pump(window);

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
            using var api = new VrcApiClient();
            using var shell = new MainViewModel();
            var instance = new InstanceViewModel(api, ImageLoader.Shared);
            TestHooks.AttachInstanceViewModel(shell, instance);
            TestHooks.SetLoggedIn(shell);

            var window = Host(new MainWindow(shell, initializeOnLoad: false));
            try
            {
                shell.OpenInstanceCommand.Execute(null);
                Pump(window);

                Assert.Equal(Visibility.Collapsed, PagerFor(window, instance.PlayersPage).Visibility);
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
    public void GroupListsExposeOnePageEach()
    {
        var viewModel = new GroupViewModel(new VrcApiClient());

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

    private static ListPager PagerFor(Window window, object page)
        => FindVisualChildren<ListPager>(window)
            .Single(pager => ReferenceEquals(pager.DataContext, page));

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
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
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
