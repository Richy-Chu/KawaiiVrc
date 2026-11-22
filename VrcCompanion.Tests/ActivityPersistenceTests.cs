using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class ActivityPersistenceTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public ActivityPersistenceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "VrcCompanion.Tests", Guid.NewGuid().ToString("N"));
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
    public void FriendChangesSurviveRestartAndKeepTheirTypeAndDate()
    {
        using var api = new VrcApiClient();
        var store = new ActivityStore(_path);
        var first = new ActivityFeedViewModel(api, store);
        first.SetAccount("usr_owner");

        var added = new VrcUser
        {
            Id = "usr_added",
            DisplayName = "Added Friend",
            Username = "added",
            Tags = new() { "trusted" }
        };
        var removed = new VrcUser
        {
            Id = "usr_removed",
            DisplayName = "Removed Friend",
            Username = "removed"
        };

        var detectedAt = DateTimeOffset.Now.AddMinutes(-5);
        first.ReportFriendChange(added, added: true, detectedAt);
        first.ReportFriendChange(removed, added: false, detectedAt);

        var second = new ActivityFeedViewModel(api, store);
        second.SetAccount("usr_owner");

        var addedEntry = Assert.Single(second.Entries, entry => entry.Type == ActivityType.FriendAdded);
        var removedEntry = Assert.Single(second.Entries, entry => entry.Type == ActivityType.FriendRemoved);
        Assert.Equal(added.Id, addedEntry.UserId);
        Assert.Equal(removed.Id, removedEntry.UserId);
        Assert.Equal(detectedAt, removedEntry.Timestamp);
        Assert.NotEmpty(addedEntry.TimestampText);
        Assert.NotEmpty(removedEntry.TimestampText);
        Assert.Contains(DateTime.Now.Year.ToString(), addedEntry.TimestampText);

        // Re-detecting the same relationship state after a relaunch must not
        // create a second event.
        second.ReportFriendChange(removed, added: false, DateTimeOffset.Now);
        Assert.Single(second.Entries, entry =>
            entry.Type == ActivityType.FriendRemoved && entry.UserId == removed.Id);

        var friended = second.Filters.Single(option => option.Type == ActivityType.FriendAdded);
        second.SelectedFilterOption = friended;
        Assert.All(second.Entries, entry => Assert.Equal(ActivityType.FriendAdded, entry.Type));

        var unfriended = second.Filters.Single(option => option.Type == ActivityType.FriendRemoved);
        string? rememberedFilter = null;
        second.FilterChanged += filter => rememberedFilter = filter;
        second.SelectedFilterOption = unfriended;
        Assert.All(second.Entries, entry => Assert.Equal(ActivityType.FriendRemoved, entry.Type));
        Assert.Equal(nameof(ActivityType.FriendRemoved), rememberedFilter);

        var change = new FriendChangeItem(removedEntry);
        Assert.True(change.IsRemoved);
        Assert.False(change.IsAdded);
        Assert.Equal("Unfriended you", change.ChangeLabel);
        Assert.Equal(removedEntry.Timestamp, change.Timestamp);
    }

    [Fact]
    public void HistoryIsKeptSeparatePerAccount()
    {
        using var api = new VrcApiClient();
        var store = new ActivityStore(_path);
        var feed = new ActivityFeedViewModel(api, store);

        feed.SetAccount("usr_one");
        feed.ReportFriendChange(
            new VrcUser { Id = "usr_friend", DisplayName = "One", Username = "one" },
            added: true);

        feed.SetAccount("usr_two");
        Assert.Empty(feed.Entries);

        feed.SetAccount("usr_one");
        Assert.Single(feed.Entries);
    }

    [Fact]
    public void BioBaselineSurvivesRestartAndStillDetectsTheNextChange()
    {
        using var api = new VrcApiClient();
        var store = new ActivityStore(_path);
        var first = new ActivityFeedViewModel(api, store);
        first.SetAccount("usr_owner");
        first.ApplyFriends(new[]
        {
            new VrcUser
            {
                Id = "usr_bio",
                DisplayName = "Bio Friend",
                Username = "bio",
                Location = "wrld_test:12345",
                Status = "active",
                Bio = "before restart"
            }
        });

        var second = new ActivityFeedViewModel(api, store);
        second.SetAccount("usr_owner");
        second.ApplyFriends(new[]
        {
            new VrcUser
            {
                Id = "usr_bio",
                DisplayName = "Bio Friend",
                Username = "bio",
                Location = "wrld_test:12345",
                Status = "active",
                Bio = "after restart"
            }
        });

        var change = Assert.Single(second.Entries, entry => entry.Type == ActivityType.BioChanged);
        Assert.Equal("usr_bio", change.UserId);
        Assert.Contains("after restart", change.Details);
    }

    [Fact]
    public void StoreDropsDuplicateIdsAndLoadsNewestFirst()
    {
        var store = new ActivityStore(_path);
        var older = new ActivityEntry
        {
            Id = "friend-added-usr_a-1",
            Type = ActivityType.FriendAdded,
            UserId = "usr_a",
            Timestamp = DateTimeOffset.UtcNow.AddHours(-2)
        };
        var newer = new ActivityEntry
        {
            Id = "friend-removed-usr_a-2",
            Type = ActivityType.FriendRemoved,
            UserId = "usr_a",
            Timestamp = DateTimeOffset.UtcNow
        };

        store.Save("usr_owner", new[] { older, newer, older });

        var loaded = store.Load("usr_owner");
        Assert.Equal(2, loaded.Count);
        Assert.Equal(newer.Id, loaded[0].Id);
        Assert.Empty(store.Load("usr_other"));
    }

    [Fact]
    public void ClearingTheDataDirectoryWhileAQueuedWriteRunsDoesNotFailTheDelete()
    {
        // The store serialises its snapshot on a worker thread, so a caller can
        // remove the data directory while the .tmp snapshot is still open. The
        // temp file must be opened with FileShare.Delete, otherwise every one of
        // these deletes fails with "activity.json.tmp is being used by another
        // process" after the test's own assertions have already passed.
        var observedWriteInFlight = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var directory = Path.Combine(_directory, "queued-write-" + attempt);
            var path = Path.Combine(directory, "activity.json");
            Directory.CreateDirectory(directory);

            // No write delay: this test needs the snapshot write to start while
            // the delete runs.
            var store = new ActivityStore(path, TimeSpan.Zero);
            store.Save(
                "usr_owner",
                Enumerable.Range(0, 20_000)
                    .Select(index => new ActivityEntry
                    {
                        Id = "friend-added-usr_" + index,
                        Type = ActivityType.FriendAdded,
                        UserId = "usr_" + index,
                        Details = new string('x', 200)
                    })
                    .ToList());

            var temp = path + ".tmp";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(temp) && DateTime.UtcNow < deadline)
            {
                Thread.SpinWait(100);
            }

            if (File.Exists(temp))
            {
                observedWriteInFlight++;
            }

            // Throws IOException while the fix is reverted.
            Directory.Delete(directory, recursive: true);
        }

        Assert.True(
            observedWriteInFlight > 0,
            "Every queued write finished before the directory was cleared, so " +
            "the delete never overlapped an open snapshot.");
    }
}
