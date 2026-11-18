using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class ContinuityImportTests
{
    [Fact]
    public void CurrentAccountIsYouAndNeverAFriendOrFriendListRow()
    {
        var history = new ContinuityPersonItem(
            new ContinuityPerson
            {
                PersonKey = "usr_me",
                UserId = "usr_me",
                DisplayName = "My account",
                IsFriend = true
            },
            "usr_me");

        Assert.True(history.IsCurrentUser);
        Assert.False(history.IsFriend);
        Assert.Equal("You", history.FriendText);

        var live = new InstancePlayerItem(
            "usr_me",
            "My account",
            isFriend: true,
            isSelf: true);

        Assert.False(live.IsFriend);
        Assert.Equal("You", live.BadgeText);
    }

    [Fact]
    public void LiveFriendDirectoryOverridesAStaleHistoryRow()
    {
        var person = new ContinuityPersonItem(new ContinuityPerson
        {
            PersonKey = "usr_friend",
            UserId = "usr_friend",
            DisplayName = "Sykari",
            IsFriend = false
        });

        Assert.False(person.IsFriend);
        Assert.Equal("Not on friend list", person.FriendText);

        person.ApplyFriendState(true);
        Assert.True(person.IsFriend);
        Assert.Equal("Friend", person.FriendText);

        var player = new InstancePlayerItem("usr_friend", "Sykari", isFriend: false, isSelf: false);
        Assert.False(player.IsFriend);
        player.ApplyFriendState(true);
        Assert.True(player.IsFriend);
        Assert.Equal("Friend", player.BadgeText);

        player.ApplyFriendState(false);
        Assert.False(player.IsFriend);
    }

    [Fact]
    public void SessionPeopleUseTheLiveFriendDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "vrccompanion-session-people-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = new ContinuityStore(path);
            var session = new ContinuitySession
            {
                Id = ContinuitySession.MakeId("test", "session-people"),
                AccountUserId = "usr_me",
                Source = "test",
                SourceKey = "session-people",
                Location = "wrld_test:123",
                WorldId = "wrld_test",
                JoinedAt = DateTimeOffset.UtcNow.AddHours(-1),
                LeftAt = DateTimeOffset.UtcNow,
                People =
                {
                    new ContinuitySessionPerson
                    {
                        PersonKey = "usr_friend",
                        UserId = "usr_friend",
                        DisplayName = "Sykari"
                    },
                    new ContinuitySessionPerson
                    {
                        PersonKey = "usr_other",
                        UserId = "usr_other",
                        DisplayName = "Someone else"
                    }
                }
            };
            store.MergeSessions(new[] { session });

            var viewModel = new ContinuityViewModel(store);
            viewModel.SetAccount(new VrcUser { Id = "usr_me", DisplayName = "Me" });
            viewModel.SetFriendIds(new[] { "usr_friend" });
            viewModel.LoadSessionPeople(session.Id);

            Assert.Equal(
                "Friend",
                viewModel.SessionPeople.Single(person => person.UserId == "usr_friend").FriendText);
            Assert.Equal(
                "Not on friend list",
                viewModel.SessionPeople.Single(person => person.UserId == "usr_other").FriendText);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public void CompanionHistoryDoesNotDuplicateExternalVisits()
    {
        if (!File.Exists(Vrcx0ContinuityImporter.DefaultPath) ||
            !File.Exists(VrcCompanionContinuityImporter.DefaultPath))
        {
            return;
        }

        var path = Path.Combine(
            Path.GetTempPath(),
            "vrccompanion-companion-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = new ContinuityStore(path);
            var account = "usr_61cc28f3-76ea-41b4-a9da-ede327ef3703";
            var external = new Vrcx0ContinuityImporter().Import(store, account, "Real account");
            Assert.True(external.Error is null, external.Error?.ToString() ?? external.Message);
            var before = store.GetSummary(account).SessionCount;

            var companion = new VrcCompanionContinuityImporter().Import(store, account, "Real account");
            Assert.True(companion.Error is null, companion.Error?.ToString() ?? companion.Message);

            var sessions = store.GetSessions(account, null, 5000);
            var duplicates = sessions
                .GroupBy(session => new
                {
                    session.AccountUserId,
                    session.Location,
                    Bucket = session.JoinedAt.ToUnixTimeSeconds() / 300
                })
                .Where(group => group.Select(session => session.Source).Distinct().Count() > 1)
                .ToList();
            Assert.Empty(duplicates);
            Assert.True(store.GetSummary(account).SessionCount >= before);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public void WorldMetadataRepairsAnEmptySessionWorldName()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "vrccompanion-world-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = new ContinuityStore(path);
            store.MergeSessions(new[]
            {
                new ContinuitySession
                {
                    Id = ContinuitySession.MakeId("test", "visit-1"),
                    AccountUserId = "usr_test",
                    Source = "test",
                    SourceKey = "visit-1",
                    Location = "wrld_test:123~region(us)",
                    WorldId = "wrld_test",
                    JoinedAt = DateTimeOffset.UtcNow.AddHours(-1),
                    LeftAt = DateTimeOffset.UtcNow,
                    DurationSeconds = 3600
                }
            });

            store.UpsertWorldMetadata(new[]
            {
                new ContinuityWorld
                {
                    WorldId = "wrld_test",
                    Name = "A real world name",
                    AuthorName = "A real creator",
                    Source = "vrchat"
                }
            });

            var session = Assert.Single(store.GetSessions("usr_test"));
            Assert.Equal("A real world name", session.WorldName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public void RealVrcx0ImportProducesDurableRows()
    {
        var source = Vrcx0ContinuityImporter.DefaultPath;
        if (!File.Exists(source))
        {
            return;
        }

        var path = Path.Combine(
            Path.GetTempPath(),
            "vrccompanion-continuity-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = new ContinuityStore(path);
            var account = "usr_61cc28f3-76ea-41b4-a9da-ede327ef3703";
            var result = new Vrcx0ContinuityImporter().Import(store, account, "Real account");
            Assert.True(result.Error is null, result.Error?.ToString() ?? result.Message);
            Assert.True(result.Sessions > 0, result.Message);

            var summary = store.GetSummary(account);
            Assert.True(summary.SessionCount > 0, "The summary did not persist any sessions.");
            Assert.True(summary.PeopleCount > 0, "The summary did not persist any people.");
            Assert.True(summary.WorldCount > 0, "The summary did not persist any worlds.");

            var sessions = store.GetSessions(account, null, 10);
            Assert.True(sessions.Count > 0);
            Assert.True(sessions.All(session => session.JoinedAt > DateTimeOffset.MinValue));

            var people = store.GetPeople(account, null, 50);
            Assert.True(people.Count > 0);
            var person = people[0];
            store.SaveNote(person.PersonKey, "durable local note");

            var reopened = new ContinuityStore(path);
            var reloaded = reopened.GetPerson(person.PersonKey, account);
            Assert.NotNull(reloaded);
            Assert.Equal("durable local note", reloaded!.LocalNote);
            Assert.Contains(reopened.GetImports(), source => source.Source == "vrcx-0");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }
}
