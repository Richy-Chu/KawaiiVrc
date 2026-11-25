using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class MediaLibraryViewModelTests
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "vrccompanion-media-vm-" + Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_root, "cache", "index.json");

    private static MediaGroup Group(string world) => new(
        $"{world} · June 2025",
        world,
        new DateTime(2025, 6, 1),
        new[] { new MediaItem($"C:\\pictures\\{world}.png", world, new DateTime(2025, 6, 2)) });

    [Fact]
    public async Task FirstScan_DoesNotShowTheEmptyMessageWhileRunning()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<MediaGroup>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new MediaLibraryViewModel(
            "C:\\pictures",
            (_, _) => gate.Task.GetAwaiter().GetResult(),
            cachePath: CachePath);

        var load = viewModel.LoadAsync(force: false);

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.IsEmpty);
        Assert.False(viewModel.HasPictures);
        Assert.Equal("Scanning pictures…", viewModel.SummaryText);

        gate.SetResult(Array.Empty<MediaGroup>());
        await load;

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.IsEmpty);
    }

    [Fact]
    public async Task FailedScan_ShowsAnErrorAndRetryRecovers()
    {
        var shouldFail = true;
        var viewModel = new MediaLibraryViewModel(
            "C:\\pictures",
            (_, _) => shouldFail
                ? throw new IOException("access denied")
                : new[] { Group("Japan") },
            cachePath: CachePath);

        await viewModel.LoadAsync(force: false);

        Assert.True(viewModel.HasError);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsEmpty);
        Assert.False(viewModel.HasPictures);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorMessage));
        Assert.Equal("Pictures could not be loaded", viewModel.SummaryText);

        shouldFail = false;
        await viewModel.LoadAsync(force: true);

        Assert.False(viewModel.HasError);
        Assert.False(viewModel.IsEmpty);
        Assert.Equal(1, viewModel.PictureCount);
    }

    [Fact]
    public async Task CompletedScan_WithPictures_HidesTheEmptyMessage()
    {
        var viewModel = new MediaLibraryViewModel(
            "C:\\pictures",
            (_, _) => new[] { Group("Japan") },
            cachePath: CachePath);

        await viewModel.LoadAsync(force: false);

        Assert.False(viewModel.IsEmpty);
        Assert.True(viewModel.HasPictures);
        Assert.Equal(1, viewModel.PictureCount);
    }

    [Fact]
    public async Task ReleasingTheLibraryDropsTheRowsTheCardsAndTheViewer()
    {
        var item = new MediaItem(Path.Combine(_root, "a.png"), "VRChat", new DateTime(2025, 6, 1));
        var viewModel = new MediaLibraryViewModel(
            _root,
            (_, _) => new[]
            {
                new MediaGroup(
                    "VRChat · June 2025",
                    "VRChat",
                    new DateTime(2025, 6, 1),
                    new[] { item })
            },
            _ => { },
            CachePath);

        await viewModel.LoadAsync(force: true);
        viewModel.OpenViewer(item);

        Assert.True(viewModel.HasLoadedPictures);
        Assert.Equal(1, viewModel.TotalPictureCount);
        Assert.NotNull(viewModel.PageCards[0].Item);

        Assert.True(viewModel.ReleaseData());

        // The reusable cards are what used to keep every row reachable after
        // the page was gone.
        Assert.False(viewModel.HasLoadedPictures);
        Assert.Equal(0, viewModel.TotalPictureCount);
        Assert.Equal(0, viewModel.PictureCount);
        Assert.All(viewModel.PageCards, card => Assert.Null(card.Item));
        Assert.False(viewModel.IsViewerOpen);
        Assert.Null(viewModel.ViewerImage);
    }

    [Fact]
    public void OpenFolder_CreatesAMissingFolderAndOpensIt()
    {
        var missing = Path.Combine(_root, "VRChat");
        string? opened = null;
        var viewModel = new MediaLibraryViewModel(
            missing,
            (_, _) => Array.Empty<MediaGroup>(),
            path => opened = path,
            CachePath);

        viewModel.OpenFolderCommand.Execute(null);

        Assert.True(Directory.Exists(missing));
        Assert.Equal(missing, opened);
    }

    [Fact]
    public void Viewer_OpensPicturesAndMovesBetweenThem()
    {
        var first = new MediaItem(Path.Combine(_root, "a.png"), "VRChat", new DateTime(2025, 6, 1));
        var second = new MediaItem(Path.Combine(_root, "b.png"), "VRChat", new DateTime(2025, 6, 2));
        var viewModel = new MediaLibraryViewModel(
            _root,
            (_, _) => Array.Empty<MediaGroup>(),
            _ => { },
            CachePath);
        viewModel.Groups.Add(new MediaGroup("VRChat · June 2025", "VRChat", new DateTime(2025, 6, 1),
            new[] { second, first }));

        viewModel.OpenViewer(second);

        Assert.True(viewModel.IsViewerOpen);
        Assert.Equal("b.png", viewModel.ViewerTitle);

        viewModel.ShowRelative(1);
        Assert.Equal("a.png", viewModel.ViewerTitle);

        viewModel.ShowRelative(1);
        Assert.Equal("b.png", viewModel.ViewerTitle); // wraps around

        viewModel.CloseViewer();
        Assert.False(viewModel.IsViewerOpen);
    }
}
