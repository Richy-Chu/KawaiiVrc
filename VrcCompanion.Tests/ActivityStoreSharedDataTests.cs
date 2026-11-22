using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// Trust tags belong to a friend and avatar urls to an avatar, but both used to
/// be written on every feed row. They are now stored once per account, with the
/// rows in memory still carrying them so the feed looks the same.
/// </summary>
public sealed class ActivityStoreSharedDataTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public ActivityStoreSharedDataTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "VrcCompanion.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "activity.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task TagsAndAvatarUrlsAreWrittenOncePerPersonAndAvatar()
    {
        var tags = new[] { "system_trust_trusted", "system_trust_veteran" }.ToList();
        var url = "https://api.vrchat.cloud/api/1/file/file_11111111-2222-3333-4444-555555555555/1/file";
        var store = new ActivityStore(_path, TimeSpan.FromMilliseconds(50));

        store.Save("usr_owner", new[]
        {
            Entry("a", ActivityType.AvatarChanged, "usr_1", "changed avatar", DateTimeOffset.UtcNow, tags: tags, avatarId: "avtr_1", avatarUrl: url),
            Entry("b", ActivityType.FriendOnline, "usr_1", "came online", DateTimeOffset.UtcNow.AddMinutes(-5), tags: tags, avatarId: "avtr_1", avatarUrl: url),
            Entry("c", ActivityType.FriendOffline, "usr_1", "went offline", DateTimeOffset.UtcNow.AddMinutes(-9), tags: tags, avatarId: "avtr_1", avatarUrl: url),
        });
        await store.FlushAsync();

        var json = File.ReadAllText(_path);
        Assert.Contains("\"userTags\":{\"usr_1\":[\"system_trust_trusted\",\"system_trust_veteran\"]}", json);
        Assert.Contains("\"avatarUrls\":{\"avtr_1\":\"https://api.vrchat.cloud/api/1/file/", json);
        Assert.DoesNotContain("\"tags\":", json);
        Assert.DoesNotContain("\"avatarUrl\":", json);
        // One copy of each, not one per row.
        Assert.Equal(1, CountOccurrences(json, "system_trust_veteran"));
        Assert.Equal(1, CountOccurrences(json, "file_11111111"));
    }

    [Fact]
    public async Task LoadingPutsTheSharedDataBackOnTheRows()
    {
        var tags = new[] { "system_trust_trusted" }.ToList();
        var url = "https://example.test/avatar.png";
        var store = new ActivityStore(_path, TimeSpan.FromMilliseconds(50));
        store.Save("usr_owner", new[]
        {
            Entry("a", ActivityType.FriendOnline, "usr_1", "came online", DateTimeOffset.UtcNow, tags: tags, avatarId: "avtr_1", avatarUrl: url),
            Entry("b", ActivityType.FriendOffline, "usr_1", "went offline", DateTimeOffset.UtcNow.AddMinutes(-3), tags: tags, avatarId: "avtr_1", avatarUrl: url),
        });
        await store.FlushAsync();

        var reloaded = new ActivityStore(_path).Load("usr_owner");

        Assert.Equal(2, reloaded.Count);
        Assert.All(reloaded, row => Assert.Equal(tags, row.Tags));
        Assert.All(reloaded, row => Assert.Equal(url, row.AvatarUrl));
        Assert.All(reloaded, row => Assert.True(row.HasAvatarImage));
    }

    [Fact]
    public async Task AFeedWithPerRowCopiesIsRewrittenIntoTheSharedMaps()
    {
        // The layout written before this change: tags and the url on every row.
        File.WriteAllText(
            _path,
            """
            {"accounts":{"usr_owner":{"entries":[
            {"id":"a","type":13,"userId":"usr_1","details":"changed avatar","avatarId":"avtr_1","avatarUrl":"https://example.test/a.png","tags":["system_trust_trusted"],"timestamp":"2026-11-22T01:00:00+00:00"},
            {"id":"b","type":13,"userId":"usr_1","details":"changed avatar again","avatarId":"avtr_1","avatarUrl":"https://example.test/a.png","tags":["system_trust_trusted"],"timestamp":"2026-11-22T00:50:00+00:00"}
            ]}}}
            """);
        var store = new ActivityStore(_path, TimeSpan.FromMilliseconds(200));
        var loaded = store.Load("usr_owner");

        // The rows on screen keep what they had.
        Assert.All(loaded, row => Assert.Equal("https://example.test/a.png", row.AvatarUrl));
        Assert.All(loaded, row => Assert.Single(row.Tags!));

        await store.FlushAsync();

        var json = File.ReadAllText(_path);
        Assert.Contains("\"userTags\"", json);
        Assert.Contains("\"avatarUrls\"", json);
        Assert.DoesNotContain("\"tags\":", json);
        Assert.Equal(1, CountOccurrences(json, "system_trust_trusted"));
        Assert.Equal(1, CountOccurrences(json, "https://example.test/a.png"));

        // And the rewritten file still reads back with the same values.
        var reread = new ActivityStore(_path).Load("usr_owner");
        Assert.All(reread, row => Assert.Equal("https://example.test/a.png", row.AvatarUrl));
        Assert.All(reread, row => Assert.Equal("system_trust_trusted", Assert.Single(row.Tags!)));
    }

    [Fact]
    public async Task AUrlWithoutAnAvatarIdIsKeptPerFriend()
    {
        // Nearly every presence event carries the friend's picture but no avatar
        // id; keying only by avatar id used to drop those urls on migration.
        var url = "https://example.test/friend.png";
        var store = new ActivityStore(_path, TimeSpan.FromMilliseconds(50));
        store.Save("usr_owner", new[]
        {
            Entry("a", ActivityType.FriendOnline, "usr_1", "came online", DateTimeOffset.UtcNow, tags: new() { "trusted" }, avatarUrl: url),
            Entry("b", ActivityType.FriendOffline, "usr_1", "went offline", DateTimeOffset.UtcNow.AddMinutes(-2), tags: new() { "trusted" }, avatarUrl: url),
        });
        await store.FlushAsync();

        var json = File.ReadAllText(_path);
        Assert.DoesNotContain("\"avatarUrl\":", json);
        Assert.Contains("\"avatarUrls\":{\"usr_1\":\"https://example.test/friend.png\"", json);

        var reloaded = new ActivityStore(_path).Load("usr_owner");
        Assert.All(reloaded, row => Assert.Equal(url, row.AvatarUrl));
    }

    [Fact]
    public async Task ANewerTagWinsForTheFriendNotTheOldestRow()
    {
        var store = new ActivityStore(_path, TimeSpan.FromMilliseconds(50));
        store.Save("usr_owner", new[]
        {
            Entry("new", ActivityType.FriendOnline, "usr_1", "came online", DateTimeOffset.UtcNow, tags: new() { "trusted", "veteran" }),
            Entry("old", ActivityType.FriendOffline, "usr_1", "went offline", DateTimeOffset.UtcNow.AddDays(-1), tags: new() { "basic" }),
        });
        await store.FlushAsync();

        Assert.Contains("\"usr_1\":[\"trusted\",\"veteran\"]", File.ReadAllText(_path));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static ActivityEntry Entry(
        string id,
        ActivityType type,
        string userId,
        string details,
        DateTimeOffset timestamp,
        System.Collections.Generic.List<string>? tags = null,
        string avatarId = "",
        string avatarUrl = "")
        => new()
        {
            Id = id,
            Type = type,
            UserId = userId,
            UserName = userId,
            DisplayName = userId,
            Details = details,
            Timestamp = timestamp,
            Tags = tags,
            AvatarId = avatarId,
            AvatarUrl = avatarUrl,
        };
}
