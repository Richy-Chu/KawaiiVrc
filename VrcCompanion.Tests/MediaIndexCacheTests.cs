using System;
using System.IO;
using System.Linq;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class MediaIndexCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "vrccompanion-media-cache-" + Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_directory, "media-index.json");

    public MediaIndexCacheTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static MediaGroup Group() => new(
        "VRChat · June 2025",
        "VRChat",
        new DateTime(2025, 6, 1),
        new[]
        {
            new MediaItem("C:\\pictures\\a.png", "VRChat", new DateTime(2025, 6, 2, 10, 0, 0)),
            new MediaItem("C:\\pictures\\b.png", "VRChat", new DateTime(2025, 6, 3, 10, 0, 0))
        });

    [Fact]
    public void SavedIndex_LoadsTheSameLibraryWithoutScanning()
    {
        MediaIndexCache.Save("C:\\pictures", new[] { Group() }, CachePath);

        var loaded = MediaIndexCache.Load("C:\\pictures", CachePath);

        var group = Assert.Single(loaded);
        Assert.Equal("VRChat · June 2025", group.Title);
        Assert.Equal(2, group.Items.Count);
        Assert.Equal("b.png", group.Items[0].FileName); // newest first
        Assert.Equal(new DateTime(2025, 6, 3, 10, 0, 0), group.Items[0].TakenAt);
    }

    [Fact]
    public void IndexForAnotherFolderIsIgnored()
    {
        MediaIndexCache.Save("C:\\other", new[] { Group() }, CachePath);

        Assert.Empty(MediaIndexCache.Load("C:\\pictures", CachePath));
    }

    [Fact]
    public void CorruptIndexLoadsEmptyAndCanBeReplaced()
    {
        File.WriteAllText(CachePath, "{ broken");

        Assert.Empty(MediaIndexCache.Load("C:\\pictures", CachePath));

        MediaIndexCache.Save("C:\\pictures", new[] { Group() }, CachePath);
        Assert.Single(MediaIndexCache.Load("C:\\pictures", CachePath));
    }
}
