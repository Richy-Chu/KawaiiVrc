using System;
using System.IO;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// A log import and a burst of presence events add rows one at a time. The store
/// folds them into one trailing write instead of rewriting the feed per row.
/// </summary>
public sealed class ActivityStoreWriteTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public ActivityStoreWriteTests()
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
    public async Task AFeedBurstIsDeferredIntoOneWrite()
    {
        var store = new ActivityStore(_path, TimeSpan.FromMilliseconds(400));
        var first = new ActivityEntry
        {
            Id = "join-usr_1",
            Type = ActivityType.FriendJoinedWorld,
            UserId = "usr_1",
            Details = "joined a world"
        };
        var second = new ActivityEntry
        {
            Id = "join-usr_2",
            Type = ActivityType.FriendJoinedWorld,
            UserId = "usr_2",
            Details = "joined a world"
        };

        store.Save("usr_owner", new[] { first });
        store.Save("usr_owner", new[] { first, second });

        Assert.False(
            File.Exists(_path),
            "The feed was written for every row instead of once for the burst.");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(_path) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        await store.FlushAsync();

        var reopened = new ActivityStore(_path);
        Assert.Equal(2, reopened.Load("usr_owner").Count);
    }

    [Fact]
    public async Task FlushWritesThePendingFeedImmediately()
    {
        var store = new ActivityStore(_path, TimeSpan.FromMinutes(5));
        store.Save(
            "usr_owner",
            new[]
            {
                new ActivityEntry
                {
                    Id = "join-usr_1",
                    Type = ActivityType.FriendJoinedWorld,
                    UserId = "usr_1",
                    Details = "joined a world"
                }
            });

        await store.FlushAsync();

        Assert.True(File.Exists(_path), "FlushAsync left the feed on its timer.");
        Assert.Single(new ActivityStore(_path).Load("usr_owner"));
    }
}
