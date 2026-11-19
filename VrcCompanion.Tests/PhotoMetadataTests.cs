using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class PhotoMetadataTests
{
    private const string JapanId = "wrld_71873c79-8c28-4e13-a311-8af1e16bf703";
    private const string GroupId = "grp_9f1c2b3d-4e5f-4a6b-8c7d-0e1f2a3b4c5d";

    private const string Xmp = """
<x:xmpmeta xmlns:x="adobe:ns:meta/">
  <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:xmp="http://ns.adobe.com/xap/1.0/">
    <rdf:Description>
      <xmp:CreatorTool>VRChat</xmp:CreatorTool>
      <xmp:Author>ЯichψTru</xmp:Author>
      <xmp:CreateDate>2026-11-06T23:23:27.3318236-06:00</xmp:CreateDate>
    </rdf:Description>
    <rdf:Description xmlns:vrc="http://ns.vrchat.com/vrc/1.0/">
      <vrc:WorldID>wrld_71873c79-8c28-4e13-a311-8af1e16bf703</vrc:WorldID>
      <vrc:WorldDisplayName>Anthro Heat Lockers and Sauna</vrc:WorldDisplayName>
      <vrc:AuthorID>usr_61cc28f3-76ea-41b4-a9da-ede327ef3703</vrc:AuthorID>
    </rdf:Description>
  </rdf:RDF>
</x:xmpmeta>
""";

    private static string Json(string instanceId) => $$"""
{"application":"VRCX-0","version":1,"author":{"id":"usr_61cc28f3-76ea-41b4-a9da-ede327ef3703","displayName":"ЯichψTru"},"world":{"name":"Anthro Heat Lockers and Sauna","id":"{{JapanId}}","instanceId":"{{instanceId}}"},"players":[{"id":"usr_a","displayName":"Alice"},{"id":"usr_b","displayName":"Bob"}]}
""";

    [Fact]
    public void ReadsTheMetadataVrchatWritesIntoThePicture()
    {
        var path = WritePng("xmp", "XML:com.adobe.xmp", Xmp);

        var metadata = PhotoMetadataReader.Read(path);

        Assert.NotNull(metadata);
        Assert.Equal(JapanId, metadata!.WorldId);
        Assert.Equal("Anthro Heat Lockers and Sauna", metadata.WorldName);
        Assert.Equal("ЯichψTru", metadata.AuthorName);
        Assert.Equal("usr_61cc28f3-76ea-41b4-a9da-ede327ef3703", metadata.AuthorId);
        Assert.Equal(
            DateTimeOffset.Parse("2026-11-06T23:23:27.3318236-06:00", System.Globalization.CultureInfo.InvariantCulture),
            metadata.TakenAt);
        Assert.Empty(metadata.Players);
    }

    [Fact]
    public void MergesVrchatAndVrcxChunksInTheSamePicture()
    {
        // This is what a real VRCX-0 photo looks like: VRChat's XMP chunk first,
        // then VRCX's own chunk with the instance and the players.
        var path = WritePng(
            "merged",
            new[]
            {
                ("XML:com.adobe.xmp", Xmp),
                ("Description", Json($"{JapanId}:12345~group({GroupId})~groupAccessType(plus)"))
            });

        var metadata = PhotoMetadataReader.Read(path);

        Assert.NotNull(metadata);
        Assert.Equal(JapanId, metadata!.WorldId);
        Assert.Equal("Anthro Heat Lockers and Sauna", metadata.WorldName);
        Assert.Equal("ЯichψTru", metadata.AuthorName);
        Assert.Contains("groupAccessType(plus)", metadata.InstanceId, StringComparison.Ordinal);
        Assert.Equal(new[] { "Alice", "Bob" }, metadata.Players.Select(player => player.DisplayName));
    }

    [Fact]
    public void ReadsTheMetadataVrcxWritesIncludingPlayers()
    {
        var path = WritePng(
            "vrcx",
            "Description",
            Json($"{JapanId}:12345~group({GroupId})~groupAccessType(public)"));

        var metadata = PhotoMetadataReader.Read(path);

        Assert.NotNull(metadata);
        Assert.Equal(JapanId, metadata!.WorldId);
        Assert.Contains("~group(", metadata.InstanceId, StringComparison.Ordinal);
        Assert.Equal(new[] { "Alice", "Bob" }, metadata.Players.Select(player => player.DisplayName));
        Assert.Equal("usr_a", metadata.Players[0].UserId);
    }

    [Fact]
    public void PicturesWithoutMetadataReadAsNull()
    {
        var path = WritePng("plain", "Comment", "just a picture");

        Assert.Null(PhotoMetadataReader.Read(path));
    }

    [Fact]
    public async Task ViewerFallsBackToThePicturesOwnMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "VrcMediaMeta", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var picturePath = Path.Combine(root, "VRChat_2026-11-06_23-23-27.108_1920x1080.png");
        File.WriteAllBytes(
            picturePath,
            BuildPng(("Description", Json($"{JapanId}:12345~group({GroupId})~groupAccessType(public)"))));

        var picture = new MediaItem(picturePath, "VRChat", new DateTime(2026, 11, 6, 23, 23, 27));
        var groups = new[]
        {
            new MediaGroup("VRChat · November 2026", "VRChat", new DateTime(2026, 11, 1), new[] { picture })
        };
        var viewModel = new MediaLibraryViewModel(
            root,
            (_, _) => groups,
            cachePath: Path.Combine(root, "index.json"))
        {
            HistoryProvider = () => Array.Empty<Models.InstanceHistoryEntry>(),
            WorldFetcher = _ => Task.FromResult<MediaWorldInfo?>(
                new MediaWorldInfo("Anthro Heat Lockers and Sauna", "https://example.test/world.png", "Sluggy Boi")),
            PersonFetcher = userId => Task.FromResult<MediaPersonInfo?>(
                new MediaPersonInfo(userId == "usr_a" ? "Alice" : "Bob", "https://example.test/" + userId + ".png"))
        };
        viewModel.GroupLookup = groupId => groupId == GroupId ? ("Kawaii Club", "https://example.test/group.png") : default;

        await viewModel.LoadAsync(force: true);
        viewModel.OpenViewer(viewModel.Groups[0].Items[0]);

        Assert.True(viewModel.HasViewerContext);
        Assert.Equal("Anthro Heat Lockers and Sauna", viewModel.ViewerWorldName);
        Assert.Equal("Group public", viewModel.ViewerAccessLabel);
        Assert.True(viewModel.ViewerHasGroup);
        Assert.Equal("Kawaii Club", viewModel.ViewerGroupName);
        Assert.StartsWith("Taken ", viewModel.ViewerSessionText);
        Assert.Equal("PHOTO", viewModel.ViewerSessionTitle);
        // The photo carries the world, so its session card can be opened.
        Assert.True(viewModel.CanOpenViewerSession);
        Assert.True(viewModel.ViewerShowsPhotoNote);
        Assert.Equal("Read from the picture itself: VRChat saved the world, VRCX saved the players.", viewModel.ViewerContextNote);
        Assert.True(viewModel.ViewerShowsPeopleCount);
        Assert.Equal(new[] { "Alice", "Bob" }, viewModel.ViewerPeople.Select(person => person.DisplayName));

        await viewModel.ViewerHydration;

        Assert.Equal("https://example.test/world.png", viewModel.ViewerWorldThumbnailUrl);
        Assert.Equal("by Sluggy Boi", viewModel.ViewerWorldAuthorText);
        Assert.All(viewModel.ViewerPeople, person => Assert.True(person.HasAvatar));
    }

    [Fact]
    public async Task PhotoWithWorldOnlySaysNoPlayerListWasSaved()
    {
        var path = WritePng("xmp-only", "XML:com.adobe.xmp", Xmp);
        var item = new MediaItem(path, "VRChat", new DateTime(2026, 11, 6, 23, 23, 27));
        var groups = new[]
        {
            new MediaGroup("VRChat", "VRChat", new DateTime(2026, 11, 1), new[] { item })
        };
        var viewModel = new MediaLibraryViewModel(
            Path.GetDirectoryName(path),
            (_, _) => groups,
            cachePath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));

        await viewModel.LoadAsync(force: true);
        viewModel.OpenViewer(item);

        Assert.Equal("PHOTO", viewModel.ViewerSessionTitle);
        Assert.StartsWith("Taken ", viewModel.ViewerSessionText);
        // Opening the session card shows the world and time from the file.
        Assert.True(viewModel.CanOpenViewerSession);
        Assert.True(viewModel.ViewerShowsNoPlayers);
        Assert.False(viewModel.ViewerShowsPeopleCount);
        Assert.Equal("VRCX didn't save who was there for this picture.", viewModel.ViewerNoPlayersText);
        Assert.Contains("no player list", viewModel.ViewerContextNote);
    }

    [Fact]
    public async Task ViewerBorrowsPlayersFromAnotherShotOfTheSameWorldLaterThatDay()
    {
        var root = Path.Combine(Path.GetTempPath(), "VrcMediaMeta", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // Three hours into the same PokoPoko session one shot got VRCX's chunk.
        var withPlayers = Path.Combine(root, "VRChat_2026-10-28_21-40-00.000_2560x1440.png");
        File.WriteAllBytes(
            withPlayers,
            BuildPng(("Description", Json($"{JapanId}:12345~groupAccessType(public)"))));
        var xmpOnly = Path.Combine(root, "VRChat_2026-10-28_18-47-46.273_2560x1440.png");
        File.WriteAllBytes(xmpOnly, BuildPng(("XML:com.adobe.xmp", Xmp)));

        var picture = new MediaItem(xmpOnly, "VRChat", new DateTime(2026, 10, 28, 18, 47, 46));
        var later = new MediaItem(withPlayers, "VRChat", new DateTime(2026, 10, 28, 21, 40, 0));
        var groups = new[]
        {
            new MediaGroup("VRChat · October 2026", "VRChat", new DateTime(2026, 10, 1), new[] { picture, later })
        };
        var viewModel = new MediaLibraryViewModel(
            root,
            (_, _) => groups,
            cachePath: Path.Combine(root, "index.json"));

        await viewModel.LoadAsync(force: true);
        viewModel.OpenViewer(picture);

        Assert.Equal(new[] { "Alice", "Bob" }, viewModel.ViewerPeople.Select(person => person.DisplayName));
        Assert.Equal("Anthro Heat Lockers and Sauna", viewModel.ViewerWorldName);
    }

    [Fact]
    public async Task PicturesWithoutSessionsStillFillTheFilters()
    {
        var root = Path.Combine(Path.GetTempPath(), "VrcMediaMeta", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        const string clubId = "wrld_11111111-2222-3333-4444-555555555555";
        var clubJson = """
{"application":"VRCX-0","version":1,"author":{"id":"usr_x","displayName":"Aya"},"world":{"name":"Late Night Sauna","id":"__WORLD__","instanceId":"__WORLD__:98765"},"players":[{"id":"usr_c","displayName":"Cleo"}]}
""".Replace("__WORLD__", clubId);

        var sauna = Path.Combine(root, "VRChat_2026-11-06_23-23-27.108_1920x1080.png");
        File.WriteAllBytes(sauna, BuildPng(("XML:com.adobe.xmp", Xmp)));
        var club = Path.Combine(root, "VRChat_2026-10-02_20-00-00.000_1920x1080.png");
        File.WriteAllBytes(club, BuildPng(("Description", clubJson)));

        var groups = new[]
        {
            new MediaGroup(
                "VRChat · November 2026",
                "VRChat",
                new DateTime(2026, 11, 1),
                new[]
                {
                    new MediaItem(sauna, "VRChat", new DateTime(2026, 11, 6, 23, 23, 27)),
                    new MediaItem(club, "VRChat", new DateTime(2026, 10, 2, 20, 0, 0))
                })
        };
        var viewModel = new MediaLibraryViewModel(
            root,
            (_, _) => groups,
            cachePath: Path.Combine(root, "index.json"));

        await viewModel.LoadAsync(force: true);
        await viewModel.PhotoContextsTask;

        Assert.Contains(viewModel.WorldFilterOptions, option => option.Label == "Anthro Heat Lockers and Sauna");
        Assert.Contains(viewModel.WorldFilterOptions, option => option.Label == "Late Night Sauna");
        Assert.Contains(viewModel.PeopleFilterOptions, option => option.Label == "Cleo");

        viewModel.SelectedWorldFilter = viewModel.WorldFilterOptions
            .Single(option => option.Label == "Anthro Heat Lockers and Sauna");
        Assert.Equal(1, viewModel.PictureCount);
    }

    [Fact]
    public async Task ViewerBorrowsPlayersFromAShotTakenSecondsEarlier()
    {
        var root = Path.Combine(Path.GetTempPath(), "VrcMediaMeta", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // One shot has VRChat's world only; the other, two seconds earlier, also
        // has VRCX's chunk with the instance and players.
        var xmpOnly = Path.Combine(root, "VRChat_2026-11-06_23-23-29.317_2048x1440.png");
        File.WriteAllBytes(xmpOnly, BuildPng(("XML:com.adobe.xmp", Xmp)));
        var withPlayers = Path.Combine(root, "VRChat_2026-11-06_23-23-27.108_1920x1080.png");
        File.WriteAllBytes(
            withPlayers,
            BuildPng(("Description", Json($"{JapanId}:12345~group({GroupId})~groupAccessType(plus)"))));

        var picture = new MediaItem(xmpOnly, "VRChat", new DateTime(2026, 11, 6, 23, 23, 29));
        var sibling = new MediaItem(withPlayers, "VRChat", new DateTime(2026, 11, 6, 23, 23, 27));
        var groups = new[]
        {
            new MediaGroup("VRChat · November 2026", "VRChat", new DateTime(2026, 11, 1), new[] { picture, sibling })
        };
        var viewModel = new MediaLibraryViewModel(
            root,
            (_, _) => groups,
            cachePath: Path.Combine(root, "index.json"))
        {
            HistoryProvider = () => Array.Empty<Models.InstanceHistoryEntry>(),
            WorldFetcher = _ => Task.FromResult<MediaWorldInfo?>(
                new MediaWorldInfo("Anthro Heat Lockers and Sauna", "https://example.test/world.png", "Sluggy Boi")),
            PersonFetcher = _ => Task.FromResult<MediaPersonInfo?>(null),
            GroupLookup = _ => ("Kawaii Club", "https://example.test/group.png")
        };

        await viewModel.LoadAsync(force: true);
        viewModel.OpenViewer(picture);

        Assert.True(viewModel.HasViewerContext);
        Assert.Equal("Anthro Heat Lockers and Sauna", viewModel.ViewerWorldName);
        Assert.Equal("Group+", viewModel.ViewerAccessLabel);
        Assert.Equal("Kawaii Club", viewModel.ViewerGroupName);
        Assert.Equal(new[] { "Alice", "Bob" }, viewModel.ViewerPeople.Select(person => person.DisplayName));
        Assert.StartsWith("Taken ", viewModel.ViewerSessionText);
        Assert.Equal("PHOTO", viewModel.ViewerSessionTitle);
    }

    [Fact]
    public async Task PhotoWithPlayersButNoWorldBorrowsTheNeighboursWorld()
    {
        // VRCX writes an empty world when it did not know the instance yet.
        var root = Path.Combine(Path.GetTempPath(), "VrcMediaMeta", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string emptyWorldJson = """
{"application":"VRCX","version":1,"author":{"id":"usr_a","displayName":"Aya"},"world":{"name":"","id":"","instanceId":""},"players":[{"id":"usr_a","displayName":"Aya"},{"id":"usr_b","displayName":"Bob"}]}
""";
        var noWorld = Path.Combine(root, "VRChat_2026-08-02_15-36-47.768_1920x1080.png");
        File.WriteAllBytes(noWorld, BuildPng(("Description", emptyWorldJson)));
        var withWorld = Path.Combine(root, "VRChat_2026-08-02_15-36-20.000_1920x1080.png");
        File.WriteAllBytes(withWorld, BuildPng(("XML:com.adobe.xmp", Xmp)));

        var picture = new MediaItem(noWorld, "VRChat", new DateTime(2026, 8, 2, 15, 36, 47));
        var neighbour = new MediaItem(withWorld, "VRChat", new DateTime(2026, 8, 2, 15, 36, 20));
        var groups = new[]
        {
            new MediaGroup("VRChat · August 2026", "VRChat", new DateTime(2026, 8, 1), new[] { picture, neighbour })
        };
        var viewModel = new MediaLibraryViewModel(
            root,
            (_, _) => groups,
            cachePath: Path.Combine(root, "index.json"));

        await viewModel.LoadAsync(force: true);
        viewModel.OpenViewer(picture);

        Assert.Equal("Anthro Heat Lockers and Sauna", viewModel.ViewerWorldName);
        Assert.Equal(new[] { "Aya", "Bob" }, viewModel.ViewerPeople.Select(person => person.DisplayName));
        Assert.True(viewModel.CanOpenViewerSession);
    }

    [Fact]
    public async Task WorldlessPhotoShowsUnknownWorldAndNoVRChatWorldOption()
    {
        var path = WritePng(
            "worldless",
            "Description",
            """
{"application":"VRCX","version":1,"author":{"id":"usr_a","displayName":"Aya"},"world":{"name":"","id":"","instanceId":""},"players":[{"id":"usr_a","displayName":"Aya"}]}
""");
        var item = new MediaItem(path, "VRChat", new DateTime(2026, 8, 2, 15, 36, 47));
        var groups = new[]
        {
            new MediaGroup("VRChat", "VRChat", new DateTime(2026, 8, 1), new[] { item })
        };
        var viewModel = new MediaLibraryViewModel(
            Path.GetDirectoryName(path),
            (_, _) => groups,
            cachePath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));

        await viewModel.LoadAsync(force: true);
        await viewModel.PhotoContextsTask;
        viewModel.OpenViewer(item);

        Assert.Equal("Unknown world", viewModel.ViewerWorldName);
        Assert.False(viewModel.CanOpenViewerWorld);
        Assert.DoesNotContain(viewModel.WorldFilterOptions, option => option.Label == "VRChat");
        Assert.True(viewModel.HasViewerPeople);
    }

    [Fact]
    public void ReadsLegacyMetadataStoredAfterThePixels()
    {
        // LFS and ScreenshotManager put their text chunk after the image data;
        // VRCX scans backwards for it, and so does the reader now.
        var path = WritePngAfterPixels(
            "lfs",
            ("Description", $"lfs|2|author:usr_61cc28f3-76ea-41b4-a9da-ede327ef3703,ЯichψTru|world:{JapanId},12345,Anthro Heat Lockers and Sauna"));

        var metadata = PhotoMetadataReader.Read(path);

        Assert.NotNull(metadata);
        Assert.Equal(JapanId, metadata!.WorldId);
        Assert.Equal("Anthro Heat Lockers and Sauna", metadata.WorldName);
        Assert.Equal($"{JapanId}:12345", metadata.InstanceId);
        Assert.Equal("ЯichψTru", metadata.AuthorName);
    }

    [Fact]
    public async Task FilterWorldsGetTheirThumbnailFromTheApi()
    {
        var root = Path.Combine(Path.GetTempPath(), "VrcMediaMeta", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "VRChat_2026-06-14_08-07-40.105_2048x1440.png");
        File.WriteAllBytes(path, BuildPng(("XML:com.adobe.xmp", Xmp)));

        var item = new MediaItem(path, "VRChat", new DateTime(2026, 6, 14, 8, 7, 40));
        var groups = new[]
        {
            new MediaGroup("VRChat · June 2026", "VRChat", new DateTime(2026, 6, 1), new[] { item })
        };
        var viewModel = new MediaLibraryViewModel(
            root,
            (_, _) => groups,
            cachePath: Path.Combine(root, "index.json"))
        {
            WorldFetcher = _ => Task.FromResult<MediaWorldInfo?>(
                new MediaWorldInfo("Palm Point - Night", "https://example.test/palm.png", "Sam"))
        };

        await viewModel.LoadAsync(force: true);
        await viewModel.PhotoContextsTask;

        var world = viewModel.WorldFilterOptions.Single(option => option.Key == JapanId);
        Assert.Equal("https://example.test/palm.png", world.ImageUrl);
        Assert.True(world.HasImage);
    }

    private static string WritePng(string name, string keyword, string text)
        => WritePng(name, new[] { (keyword, text) });

    /// <summary>A PNG with a large IDAT, then the metadata chunk after it.</summary>
    private static string WritePngAfterPixels(string name, params (string Keyword, string Text)[] chunks)
    {
        var root = Path.Combine(Path.GetTempPath(), "VrcPhotoMetadata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name + ".png");

        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        header[3] = 1;
        header[7] = 1;
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", new byte[300_000]);
        foreach (var (keyword, text) in chunks)
        {
            var payload = new List<byte>();
            payload.AddRange(Encoding.UTF8.GetBytes(keyword));
            payload.AddRange(new byte[] { 0, 0, 0, 0, 0 });
            payload.AddRange(Encoding.UTF8.GetBytes(text));
            WriteChunk(output, "iTXt", payload.ToArray());
        }

        WriteChunk(output, "IEND", Array.Empty<byte>());
        File.WriteAllBytes(path, output.ToArray());
        return path;
    }

    private static string WritePng(string name, (string Keyword, string Text)[] chunks)
    {
        var root = Path.Combine(Path.GetTempPath(), "VrcPhotoMetadata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name + ".png");
        File.WriteAllBytes(path, BuildPng(chunks));
        return path;
    }

    /// <summary>A minimal PNG whose text lives in one or more iTXt chunks.</summary>
    private static byte[] BuildPng(params (string Keyword, string Text)[] chunks)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        header[3] = 1;
        header[7] = 1;
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        foreach (var (keyword, text) in chunks)
        {
            var payload = new List<byte>();
            payload.AddRange(Encoding.UTF8.GetBytes(keyword));
            payload.AddRange(new byte[] { 0, 0, 0, 0, 0 });
            payload.AddRange(Encoding.UTF8.GetBytes(text));
            WriteChunk(output, "iTXt", payload.ToArray());
        }

        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        stream.Write(new[]
        {
            (byte)(data.Length >> 24),
            (byte)(data.Length >> 16),
            (byte)(data.Length >> 8),
            (byte)data.Length
        });
        stream.Write(Encoding.ASCII.GetBytes(type));
        stream.Write(data);
        stream.Write(new byte[4]);
    }
}
