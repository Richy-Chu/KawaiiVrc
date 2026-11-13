using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class ActivityFeedServiceTests
{
    [Fact]
    public void BioAndAvatarChangesAreReportedWithTheNewAvatar()
    {
        var before = new VrcUser
        {
            Id = "usr_1",
            DisplayName = "Friend",
            Bio = "Old bio",
            CurrentAvatar = "avtr_old"
        };
        var after = new VrcUser
        {
            Id = "usr_1",
            DisplayName = "Friend",
            Bio = "New bio",
            CurrentAvatar = "avtr_new",
            CurrentAvatarThumbnailImageUrl = "https://example.test/new.png"
        };

        var entries = new ActivityFeedService().BuildFeedFromFriends(
            new[] { after },
            new[] { before });

        Assert.Contains(entries, entry => entry.Type == ActivityType.BioChanged);
        var avatar = Assert.Single(entries, entry => entry.Type == ActivityType.AvatarChanged);
        Assert.Equal("avtr_new", avatar.AvatarId);
        Assert.Equal("https://example.test/new.png", avatar.AvatarUrl);
    }

    [Fact]
    public void FriendListDifferencesReportFriendedAndUnfriended()
    {
        var removed = new VrcUser { Id = "usr_removed", DisplayName = "Gone" };
        var added = new VrcUser { Id = "usr_added", DisplayName = "New" };

        var entries = new ActivityFeedService().BuildFeedFromFriends(
            new[] { added },
            new[] { removed });

        Assert.Contains(entries, entry =>
            entry.Type == ActivityType.FriendAdded && entry.UserId == added.Id);
        Assert.Contains(entries, entry =>
            entry.Type == ActivityType.FriendRemoved && entry.UserId == removed.Id);
    }

    [Fact]
    public void FirstLoadDoesNotReportEveryFriendAsAdded()
    {
        var friend = new VrcUser { Id = "usr_1", DisplayName = "Friend" };

        var entries = new ActivityFeedService().BuildFeedFromFriends(new[] { friend });

        Assert.DoesNotContain(entries, entry => entry.Type == ActivityType.FriendAdded);
    }
}
