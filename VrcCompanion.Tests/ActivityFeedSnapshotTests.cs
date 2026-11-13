using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class ActivityFeedSnapshotTests
{
    [Fact]
    public async Task LoadUsesTheSharedFriendLoader()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);
        var calls = 0;
        feed.RefreshRequested = () =>
        {
            calls++;
            feed.ApplyFriends(new[]
            {
                new VrcUser
                {
                    Id = "usr_1",
                    DisplayName = "One",
                    Username = "one",
                    Location = "wrld_test:12345",
                    Status = "active"
                }
            });
            return Task.CompletedTask;
        };

        await feed.LoadAsync();

        Assert.Equal(1, calls);
        Assert.True(feed.HasLoaded);
        Assert.Single(feed.Entries);
    }

    [Fact]
    public void SharedFriendSnapshotRaisesOneDeltaForTheChange()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);
        var deltas = new List<FriendListDelta>();
        feed.FriendListChanged += deltas.Add;

        var kept = new VrcUser { Id = "usr_kept", DisplayName = "Kept", Username = "kept" };
        var removed = new VrcUser { Id = "usr_removed", DisplayName = "Removed", Username = "removed" };
        var added = new VrcUser { Id = "usr_added", DisplayName = "Added", Username = "added" };

        feed.ApplyFriends(new[] { kept, removed });
        Assert.Empty(deltas);

        // The online-only refresh renders immediately but must not become the
        // snapshot, or the offline friend would look unfriended.
        feed.ShowOnlineFriends(new[] { kept });
        Assert.Empty(deltas);

        feed.ApplyFriends(new[] { kept, added });

        var delta = Assert.Single(deltas);
        Assert.Equal(added.Id, Assert.Single(delta.Added).Id);
        Assert.Equal(removed.Id, Assert.Single(delta.Removed).Id);
        Assert.Contains(feed.Entries, entry =>
            entry.Type == ActivityType.FriendAdded && entry.UserId == added.Id);
        Assert.Contains(feed.Entries, entry =>
            entry.Type == ActivityType.FriendRemoved && entry.UserId == removed.Id);

        // Applying the same complete list again must not repeat the delta.
        feed.ApplyFriends(new[] { kept, added });
        Assert.Single(deltas);
    }
}
