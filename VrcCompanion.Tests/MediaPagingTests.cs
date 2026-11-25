using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class MediaPagingTests
{
    private static MediaGroup Group(string world, int count)
    {
        var date = new DateTime(2025, 6, 1);
        var items = Enumerable.Range(0, count)
            .Select(index => new MediaItem(
                Path.Combine(Path.GetTempPath(), "VrcMediaTests", world, $"{index}.png"),
                world,
                date.AddMinutes(index)))
            .ToList();

        return new MediaGroup($"{world} · June 2025", world, date, items);
    }

    private static async Task<MediaLibraryViewModel> LoadAsync(params MediaGroup[] groups)
    {
        var cachePath = Path.Combine(
            Path.GetTempPath(),
            "VrcMediaTests",
            $"paging-{Guid.NewGuid():N}.json");
        var viewModel = new MediaLibraryViewModel(
            "C:\\pictures",
            (_, _) => groups,
            cachePath: cachePath);
        await viewModel.LoadAsync(force: true);
        // The background metadata pass decides the filters and can start another
        // apply; paging is only stable once it has finished.
        await viewModel.PhotoContextsTask;
        return viewModel;
    }

    [Fact]
    public async Task PagingMovesTheTokenSoTheGalleryStartsAtTheTop()
    {
        var viewModel = await LoadAsync(Group("Japan", 60), Group("VRChat", 20));
        var first = viewModel.PageToken;

        viewModel.NextPageCommand.Execute(null);

        Assert.True(viewModel.PageToken > first, "Next should move the token the view scrolls on");

        var second = viewModel.PageToken;
        viewModel.PreviousPageCommand.Execute(null);

        Assert.True(viewModel.PageToken > second, "Previous should move it too");
    }

    [Fact]
    public async Task PageCards_AreCappedAndPageTwoCarriesRemainingGroups()
    {
        var viewModel = await LoadAsync(Group("Japan", 60), Group("VRChat", 20));

        Assert.Equal(80, viewModel.PictureCount);
        Assert.Equal(2, viewModel.PageCount);
        Assert.Equal("Page 1 of 2", viewModel.PageText);
        Assert.True(viewModel.HasNextPage);
        Assert.False(viewModel.HasPreviousPage);

        var firstPage = viewModel.PageCards.Where(card => card.HasItem).ToList();
        Assert.Equal(48, firstPage.Count);
        Assert.Equal("Japan · June 2025", firstPage[0].GroupTitle);
        Assert.All(firstPage.Skip(1), card => Assert.Null(card.GroupTitle));

        viewModel.NextPageCommand.Execute(null);

        Assert.Equal("Page 2 of 2", viewModel.PageText);
        Assert.True(viewModel.HasPreviousPage);
        Assert.False(viewModel.HasNextPage);
        var secondPage = viewModel.PageCards.Where(card => card.HasItem).ToList();
        Assert.Equal(32, secondPage.Count);
        Assert.Equal("Japan · June 2025", secondPage[0].GroupTitle);
        Assert.Equal("VRChat · June 2025", secondPage[12].GroupTitle);

        viewModel.NextPageCommand.Execute(null);
        Assert.Equal(2, viewModel.CurrentPage);
    }

    [Fact]
    public async Task PreviousAndReload_ReturnToTheFirstPage()
    {
        var viewModel = await LoadAsync(Group("Japan", 60), Group("VRChat", 20));

        viewModel.NextPageCommand.Execute(null);
        Assert.Equal(2, viewModel.CurrentPage);

        viewModel.PreviousPageCommand.Execute(null);
        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal(48, viewModel.PageCards.Count(card => card.HasItem));

        viewModel.NextPageCommand.Execute(null);
        await viewModel.LoadAsync(force: true);
        await viewModel.PhotoContextsTask;

        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal(48, viewModel.PageCards.Count(card => card.HasItem));
    }

    [Fact]
    public async Task LastPage_HasTheRemainingPicturesAndNoNextPage()
    {
        var viewModel = await LoadAsync(Group("VRChat", 937));

        Assert.Equal(20, viewModel.PageCount);
        Assert.Equal(1, viewModel.CurrentPage);

        for (var page = 1; page < viewModel.PageCount; page++)
        {
            viewModel.NextPageCommand.Execute(null);
        }

        Assert.Equal(20, viewModel.CurrentPage);
        Assert.Equal("Page 20 of 20", viewModel.PageText);
        Assert.False(viewModel.HasNextPage);
        Assert.False(viewModel.NextPageCommand.CanExecute(null));
        Assert.Equal(25, viewModel.PageCards.Count(card => card.HasItem));
        Assert.All(viewModel.PageCards.Where(card => !card.HasItem), card => Assert.Null(card.Path));
    }

    [Fact]
    public async Task EmptyLibrary_HasNoPagesAndNoCards()
    {
        var viewModel = await LoadAsync(Array.Empty<MediaGroup>());

        Assert.Equal(0, viewModel.PageCount);
        Assert.Equal(0, viewModel.CurrentPage);
        Assert.Equal(string.Empty, viewModel.PageText);
        Assert.False(viewModel.HasPreviousPage);
        Assert.False(viewModel.HasNextPage);
        Assert.All(viewModel.PageCards, card => Assert.False(card.HasItem));
    }
}
