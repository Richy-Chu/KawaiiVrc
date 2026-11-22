using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public void ARebuiltSnapshotDoesNotAddTheSameTransitionTwice()
    {
        // A friend whose record flaps between online and offline is rebuilt from
        // the same stored transition, but with a fresh row id. Adding it again
        // grew the timeline and rewrote the whole feed file every few seconds.
        var timestamp = new DateTimeOffset(2026, 11, 21, 23, 44, 44, TimeSpan.Zero);
        var original = new ActivityEntry
        {
            Id = "offline-usr_1-aaa",
            Type = ActivityType.FriendOffline,
            UserId = "usr_1",
            Details = "went offline",
            Timestamp = timestamp
        };
        var rebuilt = new ActivityEntry
        {
            Id = "offline-usr_1-bbb",
            Type = ActivityType.FriendOffline,
            UserId = "usr_1",
            Details = "went offline",
            Timestamp = timestamp
        };

        Assert.Single(ActivityFeedViewModel.MergeEntries(new[] { original }, new[] { rebuilt }));

        // A real second transition carries its own time and is kept.
        var later = new ActivityEntry
        {
            Id = "offline-usr_1-ccc",
            Type = ActivityType.FriendOffline,
            UserId = "usr_1",
            Details = "went offline",
            Timestamp = timestamp.AddSeconds(45)
        };
        Assert.Equal(2, ActivityFeedViewModel.MergeEntries(new[] { original }, new[] { rebuilt, later }).Count);
    }

    [Fact]
    public void SelfLogSwitchIsRecordedOnceWhenTheApiCatchesUp()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "VrcCompanionTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var api = new VrcApiClient();
            var feed = new ActivityFeedViewModel(
                api,
                new ActivityStore(Path.Combine(directory, "activity.json")),
                new FriendAvatarStore(Path.Combine(directory, "avatars.json")));
            feed.SetAccount("usr_self");
            var me = new VrcUser
            {
                Id = "usr_self",
                DisplayName = "Me",
                Username = "me",
                CurrentAvatar = "avtr_old",
                CurrentAvatarName = "Old Look",
                CurrentAvatarThumbnailImageUrl = "https://example.test/old.png",
                Location = "wrld_test:1"
            };
            feed.ApplyCurrentUser(me);

            me.CurrentAvatar = "avtr_new";
            me.CurrentAvatarName = "New Look";
            me.CurrentAvatarThumbnailImageUrl = "https://example.test/new.png";
            feed.ApplySelfLoggedAvatarChange(
                me,
                "New Look",
                "avtr_new",
                "https://example.test/new.png",
                "Me");

            // The account endpoint catching up must not duplicate the row.
            feed.ApplyCurrentUser(me);

            var avatarRows = feed.Entries
                .Where(entry =>
                    entry.Type == ActivityType.AvatarChanged &&
                    entry.UserId == me.Id)
                .ToList();
            Assert.Single(avatarRows);
            Assert.Equal("avtr_new", avatarRows[0].AvatarId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SnapshotDiffsDoNotInventFriendChanges()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);

        var kept = new VrcUser { Id = "usr_kept", DisplayName = "Kept", Username = "kept" };
        var removed = new VrcUser { Id = "usr_removed", DisplayName = "Removed", Username = "removed" };
        var added = new VrcUser { Id = "usr_added", DisplayName = "Added", Username = "added" };

        feed.ApplyFriends(new[] { kept, removed });
        Assert.DoesNotContain(feed.Entries, entry => entry.Type == ActivityType.FriendAdded);

        // The online-only refresh renders immediately but must not become the
        // snapshot, or the offline friend would look unfriended.
        feed.ShowOnlineFriends(new[] { kept });
        Assert.DoesNotContain(feed.Entries, entry => entry.Type == ActivityType.FriendRemoved);

        feed.ApplyFriends(new[] { kept, added });

        Assert.DoesNotContain(feed.Entries, entry => entry.Type == ActivityType.FriendAdded);
        Assert.DoesNotContain(feed.Entries, entry => entry.Type == ActivityType.FriendRemoved);

        // Only a verified relationship change may add these activity rows.
        feed.ReportFriendChange(added, added: true);
        feed.ReportFriendChange(removed, added: false);
        Assert.Contains(feed.Entries, entry =>
            entry.Type == ActivityType.FriendAdded && entry.UserId == added.Id);
        Assert.Contains(feed.Entries, entry =>
            entry.Type == ActivityType.FriendRemoved && entry.UserId == removed.Id);
    }

    [Fact]
    public void ActivityFiltersIncludeAllAndApplyTheOfflineSetting()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);
        var friend = new VrcUser
        {
            Id = "usr_1",
            DisplayName = "Friend",
            Username = "friend",
            Location = "wrld_test:12345",
            Status = "active"
        };

        feed.ApplyFriends(new[] { friend });
        feed.ApplyFriends(new[]
        {
            new VrcUser
            {
                Id = friend.Id,
                DisplayName = friend.DisplayName,
                Username = friend.Username,
                Location = "offline",
                Status = "offline"
            }
        });

        var offline = feed.Filters.Single(option => option.Type == ActivityType.FriendOffline);
        feed.SelectedFilterOption = offline;
        Assert.All(feed.Entries, entry => Assert.Equal(ActivityType.FriendOffline, entry.Type));

        feed.SelectedFilterOption = feed.Filters[0];
        Assert.NotEmpty(feed.Entries);

        feed.ShowOfflineFriends = false;
        Assert.DoesNotContain(feed.Entries, entry => entry.Type == ActivityType.FriendOffline);
    }

    [Fact]
    public void AvatarChangeIsShownAndRetainedAcrossRefreshes()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);
        var friend = new VrcUser
        {
            Id = "usr_avatar",
            DisplayName = "Avatar Friend",
            Username = "avatarfriend",
            CurrentAvatar = "avtr_old",
            Location = "wrld_test:12345",
            Status = "active"
        };

        feed.ApplyFriends(new[] { friend });
        feed.ApplyFriends(new[]
        {
            new VrcUser
            {
                Id = friend.Id,
                DisplayName = friend.DisplayName,
                Username = friend.Username,
                CurrentAvatar = "avtr_new",
                Location = friend.Location,
                Status = friend.Status
            }
        });

        var avatar = Assert.Single(feed.Entries, entry => entry.Type == ActivityType.AvatarChanged);
        Assert.Equal("avtr_new", avatar.AvatarId);

        // The fast online refresh must also see the change without treating
        // offline friends as removed from the complete snapshot.
        feed.ApplyFriends(new[]
        {
            new VrcUser
            {
                Id = friend.Id,
                DisplayName = friend.DisplayName,
                Username = friend.Username,
                CurrentAvatar = "avtr_new",
                Location = friend.Location,
                Status = friend.Status
            }
        });
        feed.ApplyOnlineFriends(new[]
        {
            new VrcUser
            {
                Id = friend.Id,
                DisplayName = friend.DisplayName,
                Username = friend.Username,
                CurrentAvatar = "avtr_newer",
                Location = friend.Location,
                Status = friend.Status
            }
        });
        Assert.Contains(feed.Entries, entry =>
            entry.Type == ActivityType.AvatarChanged && entry.AvatarId == "avtr_newer");

        // A later snapshot with the same avatar must not make the event vanish.
        feed.ApplyFriends(new[]
        {
            new VrcUser
            {
                Id = friend.Id,
                DisplayName = friend.DisplayName,
                Username = friend.Username,
                CurrentAvatar = "avtr_newer",
                Location = friend.Location,
                Status = friend.Status
            }
        });
        Assert.Contains(feed.Entries, entry =>
            entry.Type == ActivityType.AvatarChanged && entry.AvatarId == "avtr_newer");

        feed.SelectedFilterOption = feed.Filters.Single(option => option.Type == ActivityType.AvatarChanged);
        Assert.All(feed.Entries, entry => Assert.Equal(ActivityType.AvatarChanged, entry.Type));
    }

    [Fact]
    public void ProfileAvatarImageChangeIsDetectedWhenCurrentAvatarDidNotChange()
    {
        var before = new VrcUser
        {
            Id = "usr_one",
            IconUrl = "https://api.vrchat.cloud/api/1/image/file_old/1/256",
            CurrentAvatar = "avtr_old"
        };
        var after = new VrcUser
        {
            Id = "usr_one",
            IconUrl = "https://api.vrchat.cloud/api/1/image/file_new/1/256",
            CurrentAvatar = "avtr_old"
        };

        var image = ActivityFeedViewModel.DetectProfileAvatarChange(after, before);

        Assert.Equal(after.IconUrl, image);
    }

    [Fact]
    public void ProfileAvatarImageChangeIsIgnoredWhenCurrentAvatarChanged()
    {
        var before = new VrcUser
        {
            Id = "usr_one",
            IconUrl = "https://api.vrchat.cloud/api/1/image/file_old/1/256",
            CurrentAvatar = "avtr_old"
        };
        var after = new VrcUser
        {
            Id = "usr_one",
            IconUrl = "https://api.vrchat.cloud/api/1/image/file_new/1/256",
            CurrentAvatar = "avtr_new"
        };

        Assert.Null(ActivityFeedViewModel.DetectProfileAvatarChange(after, before));
    }

    [Fact]
    public void RepeatedWorldAndPresenceChangesAreNotSwallowedByStableIds()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);

        static VrcUser Friend(string location) => new()
        {
            Id = "usr_repeat",
            DisplayName = "Repeat Friend",
            Username = "repeat",
            Location = location,
            Status = "active"
        };

        feed.ApplyFriends(new[] { Friend("wrld_one:1") });
        feed.ApplyFriends(new[] { Friend("wrld_two:2") });
        feed.ApplyFriends(new[] { Friend("wrld_three:3") });

        Assert.Equal(2, feed.Entries.Count(entry => entry.Type == ActivityType.FriendJoinedWorld));

        feed.ApplyFriends(new[] { Friend("offline") });
        feed.ApplyFriends(new[] { Friend("wrld_four:4") });

        Assert.Single(feed.Entries, entry => entry.Type == ActivityType.FriendOffline);
        Assert.Equal(2, feed.Entries.Count(entry => entry.Type == ActivityType.FriendOnline));
    }

    [Fact]
    public void ChangingBackToThePreviousAvatarCreatesAnotherEvent()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);

        static VrcUser Friend(string avatar) => new()
        {
            Id = "usr_avatar_cycle",
            DisplayName = "Avatar Cycle",
            Username = "avatarcycle",
            CurrentAvatar = avatar,
            Location = "wrld_test:12345",
            Status = "active"
        };

        feed.ApplyFriends(new[] { Friend("avtr_a") });
        feed.ApplyFriends(new[] { Friend("avtr_b") });
        feed.ApplyFriends(new[] { Friend("avtr_a") });

        Assert.Equal(2, feed.Entries.Count(entry => entry.Type == ActivityType.AvatarChanged));
    }

    [Fact]
    public void TravelingIsNotReportedAsGoingOffline()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);

        static VrcUser Friend(string location) => new()
        {
            Id = "usr_travel",
            DisplayName = "Traveler",
            Username = "traveler",
            Location = location,
            Status = "active"
        };

        feed.ApplyFriends(new[] { Friend("wrld_one:1") });
        feed.ApplyFriends(new[] { Friend("traveling") });

        Assert.DoesNotContain(feed.Entries, entry => entry.Type == ActivityType.FriendOffline);

        feed.ApplyFriends(new[] { Friend("wrld_two:2") });
        Assert.Contains(feed.Entries, entry => entry.Type == ActivityType.FriendJoinedWorld);
    }

    [Fact]
    public void JoinMeAndBusyFriendsStillCountAsOnline()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);
        feed.ApplyFriends(new[]
        {
            new VrcUser
            {
                Id = "usr_joinme",
                DisplayName = "Join Me",
                Username = "joinme",
                Location = "wrld_test:12345",
                Status = "join me"
            },
            new VrcUser
            {
                Id = "usr_busy",
                DisplayName = "Busy",
                Username = "busy",
                Location = "wrld_test:12345",
                Status = "busy"
            },
            new VrcUser
            {
                Id = "usr_stale",
                DisplayName = "Stale Location",
                Username = "stale",
                Location = "wrld_test:12345",
                Status = "offline"
            }
        });

        Assert.Equal(2, feed.Entries.Count(entry => entry.Type == ActivityType.FriendOnline));
    }

    [Fact]
    public void InitialOnlineLoadSeedsTheBaselineForTheFullLoad()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);
        var friend = new VrcUser
        {
            Id = "usr_baseline",
            DisplayName = "Baseline",
            Username = "baseline",
            Location = "wrld_test:12345",
            Status = "active"
        };

        feed.ApplyOnlineFriends(new[] { friend });
        feed.ApplyFriends(new[] { friend });

        Assert.Single(feed.Entries, entry => entry.Type == ActivityType.FriendOnline);
    }

    [Fact]
    public void FullFriendSnapshotReportsEveryFriendChecked()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);
        feed.ApplyFriends(new[]
        {
            new VrcUser { Id = "usr_one", DisplayName = "One", Username = "one" },
            new VrcUser { Id = "usr_two", DisplayName = "Two", Username = "two" }
        });

        Assert.Equal(2, feed.CheckedFriendCount);
        Assert.Contains("2 friends", feed.CheckedFriendsText);
    }

    [Fact]
    public void LoggedAvatarSwitchAddsAnAvatarChangeWithoutAnApiUpdate()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);
        var friend = new VrcUser
        {
            Id = "usr_one",
            DisplayName = "One",
            Username = "one",
            Location = "wrld_test:12345~region(us)"
        };

        feed.ApplyLoggedAvatarChange(friend, "AC-Moe-Green");

        var entry = Assert.Single(feed.Entries);
        Assert.Equal(ActivityType.AvatarChanged, entry.Type);
        Assert.Equal("usr_one", entry.UserId);
        Assert.Contains("AC-Moe-Green", entry.Details);
    }

    [Fact]
    public void RealtimeFriendUpdateAddsAnAvatarChangeImmediately()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);
        var friend = new VrcUser
        {
            Id = "usr_realtime",
            DisplayName = "Realtime Friend",
            Username = "realtime",
            CurrentAvatar = "avtr_before",
            Location = "wrld_test:12345",
            Status = "active"
        };
        feed.ApplyFriends(new[] { friend });

        feed.ApplyFriendUpdate(new VrcUser
        {
            Id = friend.Id,
            DisplayName = friend.DisplayName,
            Username = friend.Username,
            CurrentAvatar = "avtr_after",
            Location = friend.Location,
            Status = friend.Status
        });

        Assert.Contains(feed.Entries, entry =>
            entry.Type == ActivityType.AvatarChanged &&
            entry.UserId == friend.Id &&
            entry.AvatarId == "avtr_after");
    }

    [Fact]
    public void RealtimeUpdateCanDiffAgainstTheLiveFriendBeforeItIsUpdated()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);
        var before = new VrcUser
        {
            Id = "usr_realtime_first",
            DisplayName = "First Realtime",
            Username = "firstrealtime",
            CurrentAvatar = "avtr_before",
            Location = "wrld_test:12345",
            Status = "active"
        };
        var after = new VrcUser
        {
            Id = before.Id,
            DisplayName = before.DisplayName,
            Username = before.Username,
            CurrentAvatar = "avtr_after",
            Location = before.Location,
            Status = before.Status
        };

        feed.ApplyFriendUpdate(after, before);

        Assert.Contains(feed.Entries, entry =>
            entry.Type == ActivityType.AvatarChanged &&
            entry.UserId == before.Id &&
            entry.AvatarId == "avtr_after");
    }

    [Fact]
    public void EveryActivityFilterReturnsOnlyItsActivityType()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);

        static VrcUser Friend(
            string id,
            string location,
            string status,
            string avatar = "avtr_old",
            string bio = "old bio") => new()
        {
            Id = id,
            DisplayName = id,
            Username = id,
            Location = location,
            Status = status,
            CurrentAvatar = avatar,
            Bio = bio
        };

        feed.ApplyFriends(new[]
        {
            Friend("usr_a", "wrld_one:1", "active"),
            Friend("usr_b", "wrld_one:1", "active"),
            Friend("usr_d", "wrld_one:1", "active"),
            Friend("usr_e", "offline", "offline")
        });
        feed.ApplyFriends(new[]
        {
            Friend("usr_a", "wrld_two:2", "active", "avtr_new", "new bio"),
            Friend("usr_c", "wrld_one:1", "active"),
            Friend("usr_d", "offline", "offline"),
            Friend("usr_e", "wrld_one:1", "active")
        });
        feed.ReportFriendChange(Friend("usr_c", "wrld_one:1", "active"), added: true);
        feed.ReportFriendChange(Friend("usr_b", "wrld_one:1", "active"), added: false);

        var expected = new[]
        {
            ActivityType.FriendOnline,
            ActivityType.FriendJoinedWorld,
            ActivityType.FriendOffline,
            ActivityType.BioChanged,
            ActivityType.AvatarChanged,
            ActivityType.FriendAdded,
            ActivityType.FriendRemoved
        };

        feed.SelectedFilterOption = feed.Filters[0];
        var available = string.Join(", ", feed.Entries.Select(entry => entry.Type).Distinct());
        foreach (var type in expected)
        {
            feed.SelectedFilterOption = feed.Filters.Single(option => option.Type == type);
            Assert.True(feed.Entries.Count > 0, $"{type} returned no entries. Available: {available}");
            Assert.All(feed.Entries, entry => Assert.Equal(type, entry.Type));
        }

        feed.SelectedFilterOption = feed.Filters[0];
        Assert.True(feed.Entries.Count > expected.Length);
    }

    [Fact]
    public void SignedInAccountAppearsInActivityAndItsBioChangesAreRecorded()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);
        var self = new VrcUser
        {
            Id = "usr_self",
            DisplayName = "Me",
            Username = "me",
            Location = "wrld_self:1",
            Status = "active",
            State = "online",
            Bio = "old bio"
        };

        feed.ApplyCurrentUser(self);
        Assert.Contains(feed.Entries, entry =>
            entry.Type == ActivityType.FriendOnline && entry.UserId == self.Id);

        feed.ApplyCurrentUser(new VrcUser
        {
            Id = self.Id,
            DisplayName = self.DisplayName,
            Username = self.Username,
            Location = self.Location,
            Status = self.Status,
            State = self.State,
            Bio = "new bio"
        });

        Assert.Contains(feed.Entries, entry =>
            entry.Type == ActivityType.BioChanged && entry.UserId == self.Id);
    }

    [Fact]
    public void OnlinePollMarksAFriendOfflineWhenTheBucketConfirmsIt()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);
        var friend = new VrcUser
        {
            Id = "usr_leave",
            DisplayName = "Leaver",
            Username = "leaver",
            Location = "wrld_test:12345",
            Status = "active",
            State = "online"
        };

        feed.ApplyOnlineFriends(new[] { friend });
        feed.ApplyOnlineFriends(
            Array.Empty<VrcUser>(),
            new HashSet<string>(StringComparer.Ordinal) { friend.Id });

        Assert.Single(feed.Entries, entry => entry.Type == ActivityType.FriendOffline);

        feed.ApplyOnlineFriends(new[] { friend });
        Assert.Equal(2, feed.Entries.Count(entry => entry.Type == ActivityType.FriendOnline));
    }

    [Fact]
    public void OwnBioChangeIsMarkedAsSelfAndSaysYourBio()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "VrcCompanionTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var api = new VrcApiClient();
            var feed = new ActivityFeedViewModel(
                api,
                new ActivityStore(Path.Combine(directory, "activity.json")),
                new FriendAvatarStore(Path.Combine(directory, "avatars.json")));
            feed.SetAccount("usr_self_bio");
            var me = new VrcUser
            {
                Id = "usr_self_bio",
                DisplayName = "Me",
                Username = "me",
                Bio = "old bio",
                Location = "wrld_test:1",
                Status = "active"
            };
            feed.ApplyCurrentUser(me);

            me.Bio = "new bio";
            feed.ApplyCurrentUser(me);

            var change = Assert.Single(
                feed.Entries,
                entry => entry.Type == ActivityType.BioChanged);
            Assert.True(change.IsSelf);
            Assert.Equal("changed your bio · new bio", change.Details);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FriendBioChangeIsNotMarkedAsSelf()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "VrcCompanionTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var api = new VrcApiClient();
            var feed = new ActivityFeedViewModel(
                api,
                new ActivityStore(Path.Combine(directory, "activity.json")),
                new FriendAvatarStore(Path.Combine(directory, "avatars.json")));
            feed.SetAccount("usr_self_bio");
            var friend = new VrcUser
            {
                Id = "usr_friend_bio",
                DisplayName = "Friend",
                Username = "friend",
                Bio = "old bio",
                Location = "wrld_test:2",
                Status = "active"
            };
            feed.ApplyFriends(new[] { friend });

            friend.Bio = "new bio";
            feed.ApplyFriends(new[] { friend });

            var change = Assert.Single(
                feed.Entries,
                entry => entry.Type == ActivityType.BioChanged);
            Assert.False(change.IsSelf);
            Assert.Equal("changed their bio · new bio", change.Details);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BioChecksBecomeDueForSelfAndFriendsOnTheProfileScanWindow()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);
        var now = DateTimeOffset.UtcNow;
        var self = new VrcUser
        {
            Id = "usr_self_bio",
            DisplayName = "Me",
            Username = "me",
            Bio = "mine",
            Location = "wrld_test:1",
            Status = "active"
        };
        var friend = new VrcUser
        {
            Id = "usr_friend_bio",
            DisplayName = "Friend",
            Username = "friend",
            Bio = "theirs",
            Location = "wrld_test:2",
            Status = "active"
        };

        feed.ApplyCurrentUser(self);
        feed.ApplyFriends(new[] { friend });

        Assert.False(feed.IsBioCheckDue(self.Id, now, TimeSpan.FromMinutes(1)));
        Assert.True(feed.IsBioCheckDue(self.Id, now.AddMinutes(2), TimeSpan.FromMinutes(1)));
        Assert.False(feed.IsBioCheckDue(friend.Id, now, TimeSpan.FromMinutes(5)));
        Assert.True(feed.IsBioCheckDue(friend.Id, now.AddMinutes(6), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void OmittedBioIsUnknownButAnExplicitEmptyBioIsAClear()
    {
        using var api = new VrcApiClient();
        var feed = new ActivityFeedViewModel(api);

        static VrcUser Friend(string? bio) => new()
        {
            Id = "usr_bio",
            DisplayName = "Bio Friend",
            Username = "bio",
            Location = "wrld_test:12345",
            Status = "active",
            Bio = bio
        };

        feed.ApplyFriends(new[] { Friend("old bio") });
        feed.ApplyFriends(new[] { Friend(null) });
        Assert.DoesNotContain(feed.Entries, entry => entry.Type == ActivityType.BioChanged);

        feed.ApplyFriends(new[] { Friend(string.Empty) });
        var change = Assert.Single(feed.Entries, entry => entry.Type == ActivityType.BioChanged);
        Assert.Contains("cleared", change.Details);
    }

    [Fact]
    public void SearchFindsAPersonByIdAndByTheirNewName()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "VrcCompanionTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var api = new VrcApiClient();
            var feed = new ActivityFeedViewModel(
                api,
                new ActivityStore(Path.Combine(directory, "activity.json")),
                new FriendAvatarStore(Path.Combine(directory, "avatars.json")));
            feed.SetAccount("usr_self_search");
            var friend = new VrcUser
            {
                Id = "usr_search_friend",
                DisplayName = "Old Name",
                Username = "oldname",
                Location = "wrld_test:7",
                Status = "active"
            };
            feed.ApplyFriends(new[] { friend });
            Assert.NotEmpty(feed.Entries);

            feed.SearchQuery = "usr_search_friend";
            Assert.NotEmpty(feed.Entries);
            Assert.All(feed.Entries, entry => Assert.Equal("usr_search_friend", entry.UserId));
            Assert.NotNull(feed.SearchPerson);
            Assert.Equal("Old Name", feed.SearchPerson!.DisplayName);

            // The same account under a new display name keeps the old rows
            // reachable from the new name, because the id is the identity.
            var renamed = new VrcUser
            {
                Id = "usr_search_friend",
                DisplayName = "New Name",
                Username = "newname",
                Location = "wrld_test:7",
                Status = "active"
            };
            feed.ApplyFriendUpdate(renamed, friend);

            feed.SearchQuery = "new name";
            Assert.NotEmpty(feed.Entries);
            Assert.All(feed.Entries, entry => Assert.Equal("usr_search_friend", entry.UserId));
            Assert.Equal("New Name", feed.SearchPerson!.DisplayName);

            feed.SearchQuery = "old name";
            Assert.NotEmpty(feed.Entries);

            feed.SearchQuery = "nobody here";
            Assert.Empty(feed.Entries);
            Assert.Null(feed.SearchPerson);
            Assert.Contains("No activity matches", feed.NoEntriesText);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ActivityIsPagedInsteadOfOneEndlessScroll()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "VrcCompanionTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var api = new VrcApiClient();
            var feed = new ActivityFeedViewModel(
                api,
                new ActivityStore(Path.Combine(directory, "activity.json")),
                new FriendAvatarStore(Path.Combine(directory, "avatars.json")));
            feed.SetAccount("usr_paging");
            var friends = Enumerable.Range(0, 60)
                .Select(index => new VrcUser
                {
                    Id = $"usr_page_{index}",
                    DisplayName = $"Friend {index}",
                    Username = $"friend{index}",
                    Location = "wrld_test:1",
                    Status = "active"
                })
                .ToList();
            feed.ApplyFriends(friends);

            Assert.Equal(3, feed.PageCount);
            Assert.Equal(1, feed.CurrentPage);
            Assert.Equal(ActivityFeedViewModel.PageSize, feed.Entries.Count);
            Assert.False(feed.HasPreviousPage);
            Assert.True(feed.HasNextPage);
            Assert.True(feed.HasPages);

            var firstPageToken = feed.PageToken;
            feed.NextPageCommand.Execute(null);
            Assert.Equal(2, feed.CurrentPage);
            Assert.Equal(25, feed.Entries.Count);
            Assert.True(feed.HasPreviousPage);
            Assert.True(feed.HasNextPage);
            Assert.NotEqual(firstPageToken, feed.PageToken);

            feed.NextPageCommand.Execute(null);
            Assert.Equal(3, feed.CurrentPage);
            Assert.Equal(10, feed.Entries.Count);
            Assert.False(feed.HasNextPage);

            feed.PreviousPageCommand.Execute(null);
            Assert.Equal(2, feed.CurrentPage);
            Assert.Equal(25, feed.Entries.Count);

            // A new search always starts from its own first page.
            feed.SearchQuery = "Friend 1";
            Assert.Equal(1, feed.CurrentPage);

            feed.SearchQuery = string.Empty;
            Assert.Equal(1, feed.CurrentPage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
