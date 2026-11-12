using System;
using System.Collections.Generic;
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

        gate.SetResult(Array.Empty<MediaGroup>());
        await load;

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.IsEmpty);
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
