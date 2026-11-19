using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class FriendSnapshotStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;
    private readonly string _activityPath;

    public FriendSnapshotStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "VrcCompanion.Tests", Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_directory, "friend-snapshots.json");
        _activityPath = Path.Combine(_directory, "activity.json");
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
    public void SnapshotIsKeptPerAccountAndPreservesCaptureTime()
    {
        var store = new FriendSnapshotStore(_path);
        var capturedAt = new DateTimeOffset(2026, 11, 15, 9, 30, 0, TimeSpan.Zero);
        var friend = new VrcUser
        {
            Id = "usr_kept",
            DisplayName = "Kept Friend",
            Username = "kept",
            Tags = new() { "trusted" }
        };

        store.Save("usr_owner", new[] { friend }, capturedAt);

        var snapshot = store.Load("usr_owner");
        Assert.NotNull(snapshot);
        Assert.Equal(capturedAt, snapshot!.CapturedAt);
        Assert.Equal(friend.Id, Assert.Single(snapshot.Friends).Id);
        Assert.Equal(friend.Id, Assert.Single(snapshot.Users).Id);
        Assert.Null(store.Load("usr_other"));
    }

    [Fact]
    public async Task SeededBaselineDetectsChangesThatHappenedWhileClosed()
    {
        var removed = Friend("usr_removed", "Removed");
        var added = Friend("usr_added", "Added");
        var published = new TaskCompletionSource<FriendListDelta>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource<IReadOnlyList<VrcUser>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var coalescer = new FriendChangeCoalescer(
            (delta, _) => Task.FromResult(delta),
            delta =>
            {
                published.TrySetResult(delta);
                return Task.CompletedTask;
            },
            debounce: TimeSpan.Zero);
        coalescer.SetAccount("usr_owner");
        coalescer.SnapshotAccepted += (accountId, friends) =>
        {
            if (accountId == "usr_owner")
            {
                accepted.TrySetResult(friends);
            }
        };

        coalescer.SeedBaseline(new[] { removed });
        coalescer.Queue(new[] { added });

        var change = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(added.Id, Assert.Single(change.Added).Id);
        Assert.Equal(removed.Id, Assert.Single(change.Removed).Id);

        var snapshot = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(added.Id, Assert.Single(snapshot).Id);
        await coalescer.WaitForIdleAsync();
    }

    [Fact]
    public async Task AnUnverifiedCandidateDoesNotAdvanceThePersistedBaseline()
    {
        var oldFriend = Friend("usr_old", "Old");
        var newFriend = Friend("usr_new", "New");
        var accepted = false;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var coalescer = new FriendChangeCoalescer(
            (_, _) => Task.FromResult(new FriendListDelta(
                new[] { newFriend },
                Array.Empty<VrcUser>())
            {
                IsComplete = false
            }),
            _ =>
            {
                published.TrySetResult();
                return Task.CompletedTask;
            },
            debounce: TimeSpan.Zero);
        coalescer.SetAccount("usr_owner");
        coalescer.SnapshotAccepted += (_, _) => accepted = true;

        coalescer.SeedBaseline(new[] { oldFriend });
        coalescer.Queue(new[] { newFriend });

        await coalescer.WaitForIdleAsync();
        Assert.False(accepted);
        Assert.True(published.Task.IsCompleted);
    }

    [Fact]
    public async Task SavedSnapshotProducesLabeledFriendRowsOnRelaunch()
    {
        var shownBeforeClose = Friend("usr_removed", "Removed Friend");
        var kept = Friend("usr_kept", "Kept Friend");
        var addedWhileClosed = Friend("usr_added", "Added Friend");
        var snapshots = new FriendSnapshotStore(_path);
        snapshots.Save(
            "usr_owner",
            new[] { shownBeforeClose, kept },
            DateTimeOffset.Now.AddHours(-1));

        var saved = snapshots.Load("usr_owner");
        Assert.NotNull(saved);

        using var api = new VrcApiClient();
        var activityStore = new ActivityStore(_activityPath);
        var feed = new ActivityFeedViewModel(api, activityStore);
        feed.SetAccount("usr_owner");

        var beforeRefresh = DateTimeOffset.Now;
        using var coalescer = new FriendChangeCoalescer(
            (delta, _) => Task.FromResult(delta),
            delta =>
            {
                var detectedAt = DateTimeOffset.Now;
                foreach (var friend in delta.Added)
                {
                    feed.ReportFriendChange(
                        friend,
                        added: true,
                        detectedAt,
                        delta.DetectedAfterRelaunch);
                }

                foreach (var friend in delta.Removed)
                {
                    feed.ReportFriendChange(
                        friend,
                        added: false,
                        detectedAt,
                        delta.DetectedAfterRelaunch);
                }

                return Task.CompletedTask;
            },
            debounce: TimeSpan.Zero);
        coalescer.SetAccount("usr_owner");
        coalescer.SeedBaseline(saved!.Users);
        coalescer.Queue(new[] { kept, addedWhileClosed });
        await coalescer.WaitForIdleAsync();
        var afterRefresh = DateTimeOffset.Now;

        var added = Assert.Single(feed.Entries, entry => entry.Type == ActivityType.FriendAdded);
        var removed = Assert.Single(feed.Entries, entry => entry.Type == ActivityType.FriendRemoved);
        Assert.Equal(addedWhileClosed.Id, added.UserId);
        Assert.Equal(shownBeforeClose.Id, removed.UserId);
        Assert.True(added.DetectedAfterRelaunch);
        Assert.True(removed.DetectedAfterRelaunch);
        Assert.InRange(added.Timestamp, beforeRefresh, afterRefresh);
        Assert.InRange(removed.Timestamp, beforeRefresh, afterRefresh);
        Assert.Contains("detected on next launch", added.TimestampText);
        Assert.Contains("detected on next launch", removed.TimestampText);
        Assert.Contains(added.Timestamp.ToLocalTime().ToString("MMM d, yyyy"), added.TimestampText);

        var addedRow = new FriendChangeItem(added);
        var removedRow = new FriendChangeItem(removed);
        Assert.True(addedRow.IsAdded);
        Assert.True(removedRow.IsRemoved);
        Assert.Contains("detected on next launch", addedRow.TimestampText);
        Assert.Contains("detected on next launch", removedRow.TimestampText);

        // Feed writes now happen on a worker thread so the UI never serialises
        // the 14 MB file; wait for them before reloading from disk.
        await feed.FlushSaveAsync();

        var reloaded = new ActivityFeedViewModel(api, new ActivityStore(_activityPath));
        reloaded.SetAccount("usr_owner");
        Assert.True(Assert.Single(reloaded.Entries, entry =>
            entry.Type == ActivityType.FriendAdded).DetectedAfterRelaunch);
        Assert.True(Assert.Single(reloaded.Entries, entry =>
            entry.Type == ActivityType.FriendRemoved).DetectedAfterRelaunch);
    }

    private static VrcUser Friend(string id, string name)
        => new() { Id = id, DisplayName = name, Username = id, IsFriend = true };
}
