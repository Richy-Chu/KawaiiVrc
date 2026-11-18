using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class ContextSnapshotTests
{
    [Fact]
    public void SnapshotRoundTripsAndReplacesThePreviousFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "vrccompanion-context-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new ContextSnapshotStore(path);
            store.Save(new ContextSnapshot
            {
                AccountUserId = "usr_real",
                AccountDisplayName = "Real account",
                WorldId = "wrld_real",
                WorldName = "Real world",
                PlayerCount = 1,
                Players =
                {
                    new ContextSnapshotPerson { UserId = "usr_real", DisplayName = "Real account", IsFriend = true }
                }
            });

            var first = store.Read();
            Assert.NotNull(first);
            Assert.Equal("Real world", first!.WorldName);
            Assert.Single(first.Players);

            store.Save(new ContextSnapshot
            {
                AccountUserId = "usr_real",
                AccountDisplayName = "Real account",
                WorldId = "wrld_second",
                WorldName = "Second world",
                PlayerCount = 0
            });

            var second = store.Read();
            Assert.NotNull(second);
            Assert.Equal("Second world", second!.WorldName);
            Assert.Empty(second.Players);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ContextRestoresTheSavedSnapshotWhenSignedOut()
    {
        var database = Path.Combine(Path.GetTempPath(), "vrccompanion-context-" + Guid.NewGuid().ToString("N") + ".db");
        var snapshot = Path.Combine(Path.GetTempPath(), "vrccompanion-context-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var records = new ContextSnapshotStore(snapshot);
            records.Save(new ContextSnapshot
            {
                AccountUserId = "usr_saved",
                AccountDisplayName = "Saved account",
                WorldId = "wrld_saved",
                WorldName = "Saved world",
                Location = "wrld_saved:123",
                AccessText = "Public",
                RegionText = "US West",
                PlayerCount = 1,
                Players =
                {
                    new ContextSnapshotPerson
                    {
                        UserId = "usr_saved",
                        DisplayName = "Saved account",
                        IsFriend = true
                    }
                }
            });

            var viewModel = new ContextViewModel(new ContinuityStore(database), records);
            await viewModel.RefreshAsync(null, null, Array.Empty<FriendItem>());

            Assert.True(viewModel.IsStale);
            Assert.Equal("Saved account", viewModel.AccountName);
            Assert.Equal("Saved world", viewModel.WorldName);
            Assert.Equal("Saved world", viewModel.WorldTitle);
            Assert.True(viewModel.IsInWorld);
            Assert.Single(viewModel.Players);
            Assert.True(viewModel.Players[0].IsSelf);
            Assert.False(viewModel.Players[0].IsFriend);
            Assert.Empty(viewModel.FriendsHere);
            Assert.Equal("1 player", viewModel.PlayerCountText);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(database);
            File.Delete(database + "-wal");
            File.Delete(database + "-shm");
            File.Delete(snapshot);
        }
    }

    [Fact]
    public async Task ContextResolvesSnapshotAndHistoryRowsAgainstTheLiveFriendDirectory()
    {
        var database = Path.Combine(Path.GetTempPath(), "vrccompanion-context-" + Guid.NewGuid().ToString("N") + ".db");
        var snapshot = Path.Combine(Path.GetTempPath(), "vrccompanion-context-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var records = new ContextSnapshotStore(snapshot);
            records.Save(new ContextSnapshot
            {
                AccountUserId = "usr_me",
                AccountDisplayName = "Me",
                WorldId = "wrld_test",
                WorldName = "Test world",
                PlayerCount = 2,
                Players =
                {
                    new ContextSnapshotPerson
                    {
                        UserId = "usr_friend",
                        DisplayName = "Sykari",
                        IsFriend = false
                    },
                    new ContextSnapshotPerson
                    {
                        UserId = "usr_other",
                        DisplayName = "Someone else",
                        IsFriend = false,
                        IsBlocked = true
                    }
                }
            });

            var viewModel = new ContextViewModel(new ContinuityStore(database), records);
            await viewModel.RefreshAsync(null, null, Array.Empty<FriendItem>());

            Assert.False(viewModel.Players.Single(player => player.UserId == "usr_friend").IsFriend);
            Assert.Empty(viewModel.FriendsHere);

            viewModel.SetFriendIds(new[] { "usr_friend" });

            Assert.True(viewModel.Players.Single(player => player.UserId == "usr_friend").IsFriend);
            Assert.False(viewModel.Players.Single(player => player.UserId == "usr_other").IsFriend);
            Assert.True(viewModel.Players.Single(player => player.UserId == "usr_other").IsBlocked);
            Assert.Equal(
                "(B)",
                viewModel.Players.Single(player => player.UserId == "usr_other").BlockedMarker);
            Assert.Single(viewModel.FriendsHere);

            viewModel.SetFriendIds(Array.Empty<string>());

            Assert.False(viewModel.Players.Single(player => player.UserId == "usr_friend").IsFriend);
            Assert.Empty(viewModel.FriendsHere);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(database);
            File.Delete(database + "-wal");
            File.Delete(database + "-shm");
            File.Delete(snapshot);
        }
    }

    [Fact]
    public void ContinuityAndContextAreFirstClassSections()
    {
        using var shell = new MainViewModel();

        Assert.Contains(shell.Sections, section => section.Key == "continuity");
        Assert.Contains(shell.Sections, section => section.Key == "context");
        Assert.Equal("Continuity", shell.Sections.Single(section => section.Key == "continuity").Title);
        Assert.Equal("Context", shell.Sections.Single(section => section.Key == "context").Title);
    }
}
