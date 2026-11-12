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
            _ => gate.Task.GetAwaiter().GetResult());

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
            _ => shouldFail
                ? throw new IOException("access denied")
                : new[] { Group("Japan") });

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
            _ => new[] { Group("Japan") });

        await viewModel.LoadAsync(force: false);

        Assert.False(viewModel.IsEmpty);
        Assert.True(viewModel.HasPictures);
        Assert.Equal(1, viewModel.PictureCount);
    }
}
