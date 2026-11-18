using System.Collections.Generic;
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
            CurrentAvatarThumbnailImageUrl = "https://example.test/new.png",
            Tags = new List<string> { "system_trust_trusted" },
            DeveloperType = "none"
        };

        var entries = new ActivityFeedService().BuildFeedFromFriends(
            new[] { after },
            new[] { before });

        Assert.Contains(entries, entry => entry.Type == ActivityType.BioChanged);
        var avatar = Assert.Single(entries, entry => entry.Type == ActivityType.AvatarChanged);
        Assert.Equal("avtr_new", avatar.AvatarId);
        Assert.Equal("https://example.test/new.png", avatar.AvatarUrl);
        Assert.Equal(new[] { "system_trust_trusted" }, avatar.Tags);
        Assert.Equal("none", avatar.DeveloperType);
    }

    [Fact]
    public void FriendListDifferencesDoNotCreateUnverifiedActivity()
    {
        var removed = new VrcUser { Id = "usr_removed", DisplayName = "Gone" };
        var added = new VrcUser { Id = "usr_added", DisplayName = "New" };

        var entries = new ActivityFeedService().BuildFeedFromFriends(
            new[] { added },
            new[] { removed });

        // A partial or stale friend-list page is not proof of a friendship
        // change. Only a verified friendStatus response may create these rows.
        Assert.DoesNotContain(entries, entry => entry.Type == ActivityType.FriendAdded);
        Assert.DoesNotContain(entries, entry => entry.Type == ActivityType.FriendRemoved);
    }

    [Fact]
    public void FirstLoadDoesNotReportEveryFriendAsAdded()
    {
        var friend = new VrcUser { Id = "usr_1", DisplayName = "Friend" };

        var entries = new ActivityFeedService().BuildFeedFromFriends(new[] { friend });

        Assert.DoesNotContain(entries, entry => entry.Type == ActivityType.FriendAdded);
    }

    [Fact]
    public void SearchMatchesTheUserIdWithOrWithoutItsPrefix()
    {
        var entry = new ActivityEntry
        {
            Id = "bio-1",
            Type = ActivityType.BioChanged,
            UserId = "usr_565208a5-c2fc-465f-abc1-a40daab336c0",
            DisplayName = "Someone",
            UserName = "someone",
            Details = "changed their bio · hello"
        };
        var entries = new List<ActivityEntry> { entry };
        var service = new ActivityFeedService();

        Assert.Single(service.Search(entries, "usr_565208a5-c2fc-465f-abc1-a40daab336c0"));
        Assert.Single(service.Search(entries, "565208a5-c2fc"));
        Assert.Single(service.Search(entries, "USR_565208A5"));
        Assert.Empty(service.Search(entries, "usr_unrelated"));
    }

    [Theory]
    [InlineData("usr_565208a5-c2fc-465f-abc1-a40daab336c0", "565208a5-c2fc-465f-abc1-a40daab336c0", true)]
    [InlineData("usr_565208a5", "usr_565208a5", true)]
    [InlineData("usr_565208a5", "1234", false)]
    [InlineData("", "565208a5", false)]
    public void UserIdMatchingToleratesACopiedPrefix(
        string userId,
        string query,
        bool expected)
    {
        Assert.Equal(expected, ActivityFeedService.MatchesUserId(userId, query));
    }
}
