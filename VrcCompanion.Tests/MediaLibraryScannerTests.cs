using System;
using System.IO;
using System.Linq;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class MediaLibraryScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "vrccompanion-media-" + Guid.NewGuid().ToString("N"));

    public MediaLibraryScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Picture(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Array.Empty<byte>());
        File.SetLastWriteTime(full, new DateTime(2025, 6, 15, 12, 0, 0));
    }

    [Fact]
    public void Scan_GroupsByWorldFolderAndMonthNewestFirst()
    {
        Picture("Japan/VRChat_2025-06-01_10-00-00.000_1920x1080.png");
        Picture("Japan/VRChat_2025-06-20_11-00-00.000_1920x1080.png");
        Picture("Japan/VRChat_2025-05-02_09-00-00.000_1920x1080.png");
        Picture("VRChat_2025-06-03_08-00-00.000_1920x1080.png");

        var groups = MediaLibraryScanner.Scan(_root);

        Assert.Equal(3, groups.Count);
        Assert.Equal("Japan", groups[0].WorldName);
        Assert.Equal(new DateTime(2025, 6, 1), groups[0].Month);
        Assert.Equal(2, groups[0].Items.Count);
        // Newest picture first inside the group.
        Assert.Contains("2025-06-20", groups[0].Items[0].FileName, StringComparison.Ordinal);

        Assert.Equal("VRChat", groups[1].WorldName); // flat files group under VRChat
        Assert.Equal("Japan", groups[2].WorldName);
        Assert.Equal(new DateTime(2025, 5, 1), groups[2].Month);
    }

    [Fact]
    public void Scan_ReadsTheDateFromVrchatFileNames()
    {
        Picture("VRChat_2024-12-31_23-59-58.123_1920x1080.png");

        var item = Assert.Single(Assert.Single(MediaLibraryScanner.Scan(_root)).Items);

        Assert.Equal(new DateTime(2024, 12, 31, 23, 59, 58), item.TakenAt);
    }

    [Fact]
    public void Scan_FallsBackToLastWriteTimeAndIgnoresOtherFiles()
    {
        Picture("odd-name.png");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a picture");
        File.WriteAllText(Path.Combine(_root, "wallpaper.bmp"), "not a supported picture");

        var group = Assert.Single(MediaLibraryScanner.Scan(_root));
        var item = Assert.Single(group.Items);

        Assert.Equal("odd-name.png", item.FileName);
        Assert.Equal(new DateTime(2025, 6, 15, 12, 0, 0), item.TakenAt);
        Assert.Equal("VRChat", group.WorldName);
        Assert.Equal("VRChat · June 2025", group.Title);
    }

    [Fact]
    public void Scan_MissingFolderReturnsNothing()
    {
        Assert.Empty(MediaLibraryScanner.Scan(Path.Combine(_root, "nope")));
    }
}
