using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// Feeds written before the merge dedupe hold the same transition several times
/// under different row ids. Loading one drops the repeats so the next write is
/// smaller, without waiting for the feed to notice them again.
/// </summary>
public sealed class ActivityStoreLoadDedupeTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public ActivityStoreLoadDedupeTests()
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
    public async Task LoadingDropsRepeatedEventsAndShrinksTheFile()
    {
        // Seeded through the store so both files use the same layout and the
        // byte comparison measures the repeats, not formatting.
        var seed = new ActivityStore(_path, TimeSpan.FromMilliseconds(50));
        var offlineTime = DateTimeOffset.Parse("2026-11-21T23:44:44.217+00:00");
        seed.Save(
            "usr_owner",
            new[]
            {
                Entry("offline-1", ActivityType.FriendOffline, "usr_1", "went offline", offlineTime),
                Entry("offline-2", ActivityType.FriendOffline, "usr_1", "went offline", offlineTime),
                Entry("offline-3", ActivityType.FriendOffline, "usr_1", "went offline somewhere else", offlineTime),
                Entry("online-1", ActivityType.FriendOnline, "usr_1", "came online", offlineTime.AddMinutes(-4)),
                Entry("online-2", ActivityType.FriendOnline, "usr_2", "came online", offlineTime.AddMinutes(-4)),
            });
        await seed.FlushAsync();
        var before = new FileInfo(_path).Length;

        var store = new ActivityStore(_path, TimeSpan.FromMilliseconds(200));
        var loaded = store.Load("usr_owner");

        Assert.Equal(3, loaded.Count);
        // The repeat with the most detail is the copy that survives.
        var offline = Assert.Single(loaded, entry => entry.Id.StartsWith("offline", StringComparison.Ordinal));
        Assert.Equal("offline-3", offline.Id);

        await store.FlushAsync();

        var after = new FileInfo(_path).Length;
        Assert.True(after < before, $"The deduped feed is {after} bytes, was {before}.");
        Assert.Equal(3, new ActivityStore(_path).Load("usr_owner").Count);
        Assert.DoesNotContain("offline-2", File.ReadAllText(_path));
    }

    private static ActivityEntry Entry(string id, ActivityType type, string userId, string details, DateTimeOffset timestamp)
        => new()
        {
            Id = id,
            Type = type,
            UserId = userId,
            UserName = userId,
            DisplayName = userId,
            Details = details,
            Timestamp = timestamp,
        };

    [Fact]
    public void AFeedWithoutRepeatsIsLeftAlone()
    {
        File.WriteAllText(
            _path,
            """
            {"accounts":{"usr_owner":{"entries":[
            {"id":"online-1","type":0,"userId":"usr_1","details":"came online","timestamp":"2026-11-21T23:40:00+00:00"},
            {"id":"online-2","type":0,"userId":"usr_2","details":"came online","timestamp":"2026-11-21T23:40:00+00:00"}
            ]}}}
            """);
        var before = new FileInfo(_path).LastWriteTimeUtc;

        var store = new ActivityStore(_path, TimeSpan.FromMilliseconds(50));
        Assert.Equal(2, store.Load("usr_owner").Count);

        // Nothing to clean, so no rewrite is queued.
        Assert.Equal(before, new FileInfo(_path).LastWriteTimeUtc);
    }
}
