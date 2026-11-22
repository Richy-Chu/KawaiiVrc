using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The Worlds tab pages the public catalog: one page of cards is published at a
/// time, Next fetches the next offset, and Previous answers from the page cache.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class WorldsPagingTests
{
    [Fact]
    public async Task NextFetchesTheNextOffsetAndPreviousReusesTheCachedPage()
    {
        using var handler = new WorldsHandler(total: 60);
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.SetLoggedIn(shell);

        await shell.SearchWorldsAsync(string.Empty);

        Assert.Equal(24, shell.Worlds.Count);
        Assert.Equal("World 0", shell.Worlds[0].Name);
        Assert.Equal("Page 1", shell.WorldsPageText);
        Assert.True(shell.HasNextWorldsPage);
        Assert.False(shell.HasPreviousWorldsPage);
        Assert.Contains("offset=0", handler.Requests[^1]);

        await shell.GoToWorldsPageAsync(2);

        Assert.Equal(24, shell.Worlds.Count);
        Assert.Equal("World 24", shell.Worlds[0].Name);
        Assert.Equal("Page 2", shell.WorldsPageText);
        Assert.True(shell.HasPreviousWorldsPage);
        Assert.Contains("offset=24", handler.Requests[^1]);

        // Page 1 was already fetched, so stepping back must not hit the API.
        var requests = handler.Requests.Count;
        await shell.GoToWorldsPageAsync(1);

        Assert.Equal("World 0", shell.Worlds[0].Name);
        Assert.Equal(requests, handler.Requests.Count);
    }

    [Fact]
    public async Task AShortPageEndsPaging()
    {
        using var handler = new WorldsHandler(total: 30);
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.SetLoggedIn(shell);

        await shell.SearchWorldsAsync(string.Empty);
        await shell.GoToWorldsPageAsync(2);

        Assert.Equal(6, shell.Worlds.Count);
        Assert.False(shell.HasNextWorldsPage);
        Assert.False(shell.NextWorldsPageCommand.CanExecute(null));
    }

    [Fact]
    public void TheWorldsGridOnlyKeepsTheCardsNearTheViewportResident()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            TestHooks.SeedWorlds(shell, Enumerable.Range(0, 120).Select(index => new VrcWorld
            {
                Id = $"wrld_{index}",
                Name = $"World {index}",
                AuthorName = "AB"
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

            window.Show();
            shell.SelectedSection = shell.Sections.Single(section => section.Key == "worlds");
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            var list = FindDescendant<ListBox>(window, box =>
                ReferenceEquals(box.ItemsSource, shell.Worlds));
            Assert.NotNull(list);

            Assert.Equal(120, list!.Items.Count);

            // The pager under the list is wired to the view model's commands and
            // follows its state: a single seeded page has no Next or Previous.
            var nextButton = FindDescendant<Button>(window, button =>
                ReferenceEquals(button.Command, shell.NextWorldsPageCommand));
            Assert.NotNull(nextButton);
            Assert.False(nextButton!.IsEnabled);

            var previousButton = FindDescendant<Button>(window, button =>
                ReferenceEquals(button.Command, shell.PreviousWorldsPageCommand));
            Assert.NotNull(previousButton);
            Assert.False(previousButton!.IsEnabled);

            var resident = RealizedCount(list);
            Assert.True(
                resident > 0 && resident < 120,
                $"the worlds grid kept {resident} of 120 cards resident");

            // The list is scrollable height, but the last card is not built
            // until a scroll asks for it...
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(119));

            var scroll = FindDescendant<ScrollViewer>(list);
            Assert.NotNull(scroll);
            Assert.True(
                scroll!.ExtentHeight > scroll.ViewportHeight,
                "the worlds grid reported no scrollable height for the cards it did not build");

            scroll.ScrollToEnd();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(119));

            // ...and the cards at the top are handed back once they are out of
            // reach, instead of staying resident for the rest of the visit.
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(0));

            window.Close();
        });
    }

    private static int RealizedCount(ListBox list)
        => Enumerable.Range(0, list.Items.Count)
            .Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null);

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

    /// <summary>Answers worlds pages by offset, with a fixed number of worlds in total.</summary>
    private sealed class WorldsHandler : HttpMessageHandler
    {
        private readonly int _total;

        public WorldsHandler(int total) => _total = total;

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.PathAndQuery);

            var offset = 0;
            var match = Regex.Match(uri.Query, "offset=(\\d+)");
            if (match.Success)
            {
                offset = int.Parse(match.Groups[1].Value);
            }

            var count = Math.Max(0, Math.Min(VrcApiClient.WorldsPageSize, _total - offset));
            var rows = Enumerable.Range(offset, count).Select(index =>
                $"{{\"id\":\"wrld_{index}\",\"name\":\"World {index}\",\"authorName\":\"AB\"}}");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[" + string.Join(",", rows) + "]",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
