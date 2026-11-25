using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class InstanceHistoryTests : IDisposable
{
    private const string WorldA = "wrld_11111111-1111-1111-1111-111111111111";
    private const string WorldB = "wrld_22222222-2222-2222-2222-222222222222";
    private const string LocationA = WorldA + ":12345~region(eu)";
    private const string LocationB = WorldB + ":67890~region(us)";
    private const string LocationC = WorldB + ":99999~region(eu)";
    private const string Me = "usr_aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Friend = "usr_bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string Other = "usr_cccccccc-cccc-cccc-cccc-cccccccccccc";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "vrccompanion-history-" + Guid.NewGuid().ToString("N"));

    public InstanceHistoryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ImporterBuildsRealSessionsPlayersAndTimelineDurations()
    {
        var log = Path.Combine(_directory, "output_log_2026-11-14.txt");
        File.WriteAllText(log, string.Join('\n', new[]
        {
            $"2026.11.13 10:52:00 Log - [Behaviour] Joining {LocationA}",
            $"2026.11.13 10:52:01 Log - [Behaviour] OnPlayerJoined Me ({Me})",
            $"2026.11.13 10:52:02 Log - [Behaviour] OnPlayerJoined Juicy_Drink ({Friend})",
            "2026.11.13 10:52:03 Log - [Behaviour] OnPlayerJoined NameOnly",
            $"2026.11.13 10:59:00 Log - [Behaviour] OnPlayerLeft Juicy_Drink ({Friend})",
            "2026.11.13 11:00:00 Log - [Behaviour] OnLeftRoom",
            $"2026.11.14 08:00:00 Log - [Behaviour] Joining {LocationB}",
            $"2026.11.14 08:00:01 Log - [Behaviour] OnPlayerJoined Me ({Me})",
            $"2026.11.14 08:10:00 Log - [Behaviour] OnPlayerLeft Me ({Me})",
            "2026.11.14 08:11:00 Log - [Behaviour] OnLeftRoom",
            string.Empty
        }));

        var imported = new InstanceHistoryLogImporter().Import(Me, "Me", _directory);

        Assert.Equal(2, imported.Count);
        var first = imported[0];
        Assert.Equal(LocationA, first.Location);
        Assert.Equal(WorldA, first.WorldId);
        Assert.Equal("12345~region(eu)", first.InstanceId);
        Assert.Equal(8, first.Duration.TotalMinutes);
        Assert.Equal(3, first.Players.Count);

        var juicy = Assert.Single(first.Players, player => player.UserId == Friend);
        Assert.Equal("Juicy_Drink", juicy.DisplayName);
        Assert.Equal(7, Math.Round(juicy.TotalTime.TotalMinutes));
        Assert.Single(juicy.Visits);
        Assert.NotNull(juicy.Visits[0].LeftAt);

        var nameOnly = Assert.Single(first.Players, player => player.UserId.Length == 0);
        Assert.Equal("NameOnly", nameOnly.DisplayName);
        Assert.False(nameOnly.HasOpenVisit);
        Assert.Equal(8, Math.Round(nameOnly.TotalTime.TotalMinutes));

        Assert.Equal(LocationB, imported[1].Location);
        Assert.Equal(11, imported[1].Duration.TotalMinutes);
    }

    [Fact]
    public void RecorderTracksJoinsLeavesAndWorldChanges()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "history.json"));
        var recorder = new InstanceHistoryRecorder(store);
        var user = new VrcUser { Id = Me, DisplayName = "Me" };
        recorder.SetAccount(user);
        var start = DateTimeOffset.Now.AddMinutes(-10);

        Assert.True(recorder.Observe(
            new VrChatLogSnapshot(LocationA, new[]
            {
                new VrChatLogPlayer(Me, "Me"),
                new VrChatLogPlayer(Friend, "Juicy_Drink")
            }),
            gameRunning: true,
            start));

        var first = Assert.Single(store.ForAccount(Me));
        Assert.Equal(LocationA, first.Location);
        Assert.Equal(2, first.Players.Count);
        Assert.All(first.Players, player => Assert.True(player.HasOpenVisit));

        var playerLeftAt = start.AddMinutes(5);
        recorder.Observe(
            new VrChatLogSnapshot(LocationA, new[]
            {
                new VrChatLogPlayer(Me, "Me")
            }),
            gameRunning: true,
            playerLeftAt);

        // The SQLite store rebuilds rows from events, so re-query after a leave.
        var afterLeave = Assert.Single(store.ForAccount(Me));
        var friend = Assert.Single(afterLeave.Players, player => player.UserId == Friend);
        Assert.False(friend.HasOpenVisit);
        Assert.Equal(5, friend.TotalTime.TotalMinutes);

        var changedAt = start.AddMinutes(8);
        Assert.True(recorder.Observe(
            new VrChatLogSnapshot(LocationB, new[]
            {
                new VrChatLogPlayer(Me, "Me"),
                new VrChatLogPlayer(Other, "Other")
            }),
            gameRunning: true,
            changedAt));

        var rows = store.ForAccount(Me).OrderBy(entry => entry.JoinedAt).ToList();
        Assert.True(
            rows.Count == 2,
            string.Join(" | ", rows.Select(row =>
                $"{row.Location} {row.JoinedAt:O} -> {row.LeftAt:O}")));
        Assert.NotNull(rows[0].LeftAt);
        Assert.Equal(LocationB, rows[1].Location);
        Assert.Contains(rows[1].Players, player => player.UserId == Other);

        var detail = new InstanceHistoryItem(
            rows[1],
            Me,
            null,
            null,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            null);
        Assert.Single(detail.Timeline, item => item.Text.Contains("You joined", StringComparison.Ordinal));
        Assert.Contains(detail.Timeline, item => item.Text.Contains("Other joined", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelfInviteUsesVrchatsMyselfEndpointWithTheExactLocation()
    {
        var handler = new InviteHandler();
        using var api = new VrcApiClient(null, handler);

        await api.InviteSelfAsync(LocationA);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(
            "/api/1/invite/myself/to/" + LocationA,
            handler.Path);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal(string.Empty, body.RootElement.GetProperty("shortName").GetString());
    }

    [Fact]
    public async Task SelfInviteRejectsAnythingThatIsNotAnInstanceLocation()
    {
        var handler = new InviteHandler();
        using var api = new VrcApiClient(null, handler);

        await Assert.ThrowsAsync<ArgumentException>(() => api.InviteSelfAsync("private"));

        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task SelfInviteCarriesTheInstanceShortNameWhenVrchatProvidesIt()
    {
        var handler = new InviteHandler();
        using var api = new VrcApiClient(null, handler);

        await api.InviteSelfAsync(LocationA, "abc123");

        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("abc123", body.RootElement.GetProperty("shortName").GetString());
    }

    [Fact]
    public async Task ResolvesAnInstanceShortNameFromTheInstanceEndpoint()
    {
        var handler = new ShortNameHandler("{\"shortName\":\"abc123\"}");
        using var api = new VrcApiClient(null, handler);

        var shortName = await api.GetInstanceShortNameAsync(LocationA);

        Assert.Equal("abc123", shortName);
        Assert.Contains("/instances/", handler.Path, StringComparison.Ordinal);
        Assert.EndsWith("/shortName", handler.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvesTheSecureNameWhenVrchatOmitsTheShortName()
    {
        var handler = new ShortNameHandler("{\"secureName\":\"secure456\"}");
        using var api = new VrcApiClient(null, handler);

        var shortName = await api.GetInstanceShortNameAsync(LocationA);

        Assert.Equal("secure456", shortName);
    }

    [Fact]
    public void NonFriendPlayerUsesThePublicProfileIcon()
    {
        var player = new InstanceHistoryPlayer
        {
            UserId = Other,
            DisplayName = "Other"
        };
        var item = new InstanceHistoryPlayerItem(player, isSelf: false, Me);

        Assert.Equal(string.Empty, item.AvatarUrl);
        Assert.False(item.IsSelf);

        item.ApplyProfile(new VrcProfile
        {
            Id = Other,
            DisplayName = "Other Name",
            UserIcon = "https://api.vrchat.cloud/api/1/image/file_icon/1/256",
            TrustTags = new List<string> { "system_trust_known" }
        });

        Assert.Equal("Other Name", item.DisplayName);
        Assert.Equal("https://api.vrchat.cloud/api/1/image/file_icon/1/256", item.AvatarUrl);
        Assert.Same(UserPresentation.BrushFor("known"), item.NameBrush);
    }

    [Fact]
    public void StorePersistsHistoryAcrossReloadsAndDeletesOnlyTheChosenRow()
    {
        var path = Path.Combine(_directory, "history.json");
        var store = new InstanceHistoryStore(path);
        var entry = new InstanceHistoryEntry
        {
            Id = "abc",
            AccountUserId = Me,
            Location = LocationA,
            WorldId = WorldA,
            InstanceId = "12345~region(eu)",
            JoinedAt = DateTimeOffset.Now.AddHours(-1),
            LastSeenAt = DateTimeOffset.Now,
            LeftAt = DateTimeOffset.Now
        };
        store.Add(entry);
        store.Save();

        var reloaded = new InstanceHistoryStore(path);
        reloaded.Load();
        var persisted = Assert.Single(reloaded.ForAccount(Me));

        Assert.True(reloaded.Delete(persisted.Id));
        reloaded.Save();

        var empty = new InstanceHistoryStore(path);
        empty.Load();
        Assert.Empty(empty.ForAccount(Me));
    }

    [Fact]
    public void SqliteStoreKeepsRealLocationAndJoinLeaveEvents()
    {
        var path = Path.Combine(_directory, "history.db");
        var start = DateTimeOffset.Now.AddHours(-2);
        var entry = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
        entry.GetOrAddPlayer(Me, "Me").StartVisit(start);
        entry.GetOrAddPlayer(Friend, "Juicy_Drink").StartVisit(start);
        entry.Close(start.AddMinutes(30));
        entry.WorldName = "The Great Pug";

        var store = new InstanceHistoryStore(path);
        store.Add(entry);
        store.Save();

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM gamelog_location),
                    (SELECT COUNT(*) FROM gamelog_join_leave),
                    (SELECT event_type FROM gamelog_join_leave ORDER BY id LIMIT 1)
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(4, reader.GetInt64(1));
            Assert.Equal("OnPlayerJoined", reader.GetString(2));
        }

        var reloaded = new InstanceHistoryStore(path);
        reloaded.Load();
        var row = Assert.Single(reloaded.ForAccount(Me));
        Assert.Equal("The Great Pug", row.WorldName);
        Assert.Equal(2, row.Players.Count);
        Assert.Equal(30, row.Duration.TotalMinutes);
    }

    [Fact]
    public void AppendingOneEventDoesNotRebuildEveryStoredRow()
    {
        var path = Path.Combine(_directory, "incremental.db");
        var store = new InstanceHistoryStore(path);
        var start = DateTimeOffset.Now.AddHours(-8);
        for (var index = 0; index < 300; index++)
        {
            var joined = start.AddMinutes(index);
            var entry = InstanceHistoryStore.NewFromLog(
                Me,
                index % 2 == 0 ? LocationA : LocationB,
                joined)!;
            entry.GetOrAddPlayer(Me, "Me").StartVisit(joined);
            entry.GetOrAddPlayer(Friend, "Maya").StartVisit(joined);
            entry.Close(joined.AddSeconds(30));
            store.Add(entry);
        }

        // Warm the store the way the app does: one screen reads the history.
        store.ForAccount(Me);

        // The log appends one event while the app is open. The next refresh must
        // read that row instead of parsing every stored row again.
        store.RecordPlayerEvent(Me, LocationA, Other, "Someone", string.Empty, joined: true, DateTimeOffset.Now);
        var incrementalStart = GC.GetAllocatedBytesForCurrentThread();
        var incremental = store.ForAccount(Me);
        var incrementalCost = GC.GetAllocatedBytesForCurrentThread() - incrementalStart;

        // A store that has never read this file pays for the full read.
        var cold = new InstanceHistoryStore(path);
        var coldStart = GC.GetAllocatedBytesForCurrentThread();
        var coldRows = cold.ForAccount(Me);
        var coldCost = GC.GetAllocatedBytesForCurrentThread() - coldStart;

        Assert.Equal(coldRows.Count, incremental.Count);
        Assert.Contains(incremental, entry => entry.Players.Any(player => player.UserId == Other));
        // Measured here: 20,192 B against 2,135,392 B for the same 1,200 events.
        // The append reuses every finished group, so it pays for the open
        // session, the location rows and the result list only.
        Assert.True(
            incrementalCost * 20 < coldCost,
            $"Appending should not re-read every row: appended={incrementalCost:N0} B, full read={coldCost:N0} B.");
    }

    [Fact]
    public void AppendedEventsProduceTheSameHistoryAsAFreshRead()
    {
        var path = Path.Combine(_directory, "incremental-accuracy.db");
        var store = new InstanceHistoryStore(path);
        var start = DateTimeOffset.Now.AddHours(-4);
        var entry = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
        entry.GetOrAddPlayer(Me, "Me").StartVisit(start);
        entry.GetOrAddPlayer(Friend, "Maya").StartVisit(start);
        entry.Close(start.AddMinutes(15));
        store.Add(entry);
        store.ForAccount(Me);

        var joined = DateTimeOffset.Now.AddMinutes(-5);
        store.RecordPlayerEvent(Me, LocationA, Me, "Me", string.Empty, joined: true, joined);
        store.ForAccount(Me);
        store.RecordPlayerEvent(Me, LocationA, Friend, "Maya", string.Empty, joined: true, joined.AddMinutes(1));
        var incremental = store.ForAccount(Me);

        var fresh = new InstanceHistoryStore(path);
        var expected = fresh.ForAccount(Me);

        Assert.Equal(expected.Count, incremental.Count);
        Assert.Equal(
            expected.Select(row => (row.Id, row.JoinedAt, row.LeftAt, row.Players.Count)),
            incremental.Select(row => (row.Id, row.JoinedAt, row.LeftAt, row.Players.Count)));
        Assert.Equal(
            expected.SelectMany(row => row.Players.Select(player => (row.Id, player.UserId, player.Visits.Count))),
            incremental.SelectMany(row => row.Players.Select(player => (row.Id, player.UserId, player.Visits.Count))));
    }

    [Fact]
    public void AnOlderImportedSessionForcesAFullReadInsteadOfAnAppend()
    {
        var path = Path.Combine(_directory, "incremental-import.db");
        var store = new InstanceHistoryStore(path);
        var recent = DateTimeOffset.Now.AddHours(-2);
        var entry = InstanceHistoryStore.NewFromLog(Me, LocationA, recent)!;
        entry.GetOrAddPlayer(Me, "Me").StartVisit(recent);
        entry.Close(recent.AddMinutes(10));
        store.Add(entry);
        store.ForAccount(Me);

        // An imported session older than everything already read sorts before the
        // cached rows, so it must not be appended to the end of them.
        var imported = InstanceHistoryStore.NewFromLog(Me, LocationB, recent.AddHours(-6))!;
        imported.GetOrAddPlayer(Me, "Me").StartVisit(imported.JoinedAt);
        imported.Close(imported.JoinedAt.AddMinutes(12));
        store.Add(imported);

        var rows = store.ForAccount(Me);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Location == LocationB && row.Duration.TotalMinutes == 12);
        Assert.Equal(
            rows.OrderByDescending(row => row.JoinedAt).Select(row => row.Id),
            rows.Select(row => row.Id));
    }

    [Fact]
    public void RefreshingHistoryReusesFinishedGroupsAndRebuildsOnlyTheOpenOne()
    {
        var path = Path.Combine(_directory, "group-reuse.db");
        var store = new InstanceHistoryStore(path);
        var start = DateTimeOffset.Now.AddHours(-8);
        for (var index = 0; index < 39; index++)
        {
            var joined = start.AddMinutes(index * 10);
            var entry = InstanceHistoryStore.NewFromLog(
                Me,
                index % 2 == 0 ? LocationA : LocationB,
                joined)!;
            entry.GetOrAddPlayer(Me, "Me").StartVisit(joined);
            entry.GetOrAddPlayer(Friend, "Maya").StartVisit(joined);
            entry.Close(joined.AddMinutes(5));
            store.Add(entry);
        }

        var openStart = start.AddHours(7);
        var open = InstanceHistoryStore.NewFromLog(Me, LocationC, openStart)!;
        open.GetOrAddPlayer(Me, "Me").StartVisit(openStart);
        store.Add(open);

        var before = store.ForAccount(Me);
        Assert.Equal(40, before.Count);

        // A live event lands in the open session at the end. Every earlier group
        // is finished and cannot gain a row, so it must come back untouched.
        store.RecordPlayerEvent(Me, LocationC, Friend, "Maya", string.Empty, joined: true, openStart.AddMinutes(1));
        var after = store.ForAccount(Me);

        Assert.Equal(40, after.Count);
        foreach (var finished in before.Where(row => row.LeftAt is not null))
        {
            Assert.Same(finished, after.Single(row => row.Id == finished.Id));
        }

        var openBefore = before.Single(row => row.LeftAt is null);
        var openAfter = after.Single(row => row.LeftAt is null);
        Assert.NotSame(openBefore, openAfter);
        Assert.Equal(2, openAfter.Players.Count);
        Assert.All(openAfter.Players, player => Assert.True(player.HasOpenVisit));
    }

    [Fact]
    public void AppendingAcrossGroupBoundariesProducesTheSameHistoryAsAFreshRead()
    {
        var path = Path.Combine(_directory, "append-boundaries.db");
        var store = new InstanceHistoryStore(path);
        var start = DateTimeOffset.Now.AddHours(-6);
        var events = new (string Location, string User, string Name, bool Joined, DateTimeOffset At)[]
        {
            (LocationA, Me, "Me", true, start),
            (LocationA, Friend, "Maya", true, start.AddMinutes(1)),
            (LocationA, Friend, "Maya", false, start.AddMinutes(10)),
            (LocationA, Me, "Me", false, start.AddMinutes(11)),
            (LocationB, Me, "Me", true, start.AddHours(1)),
            (LocationB, Other, "Someone", true, start.AddHours(1).AddMinutes(2)),
            (LocationC, Me, "Me", true, start.AddHours(3)),
            (LocationC, Friend, "Maya", true, start.AddHours(3).AddMinutes(1)),
            (LocationC, Friend, "Maya", false, start.AddHours(3).AddMinutes(4))
        };
        foreach (var item in events)
        {
            store.RecordPlayerEvent(Me, item.Location, item.User, item.Name, string.Empty, item.Joined, item.At);
            // The app reads history after every live event, so every one of these
            // refreshes exercises the appended-group path.
            store.ForAccount(Me);
        }

        var appended = store.ForAccount(Me);
        var fresh = new InstanceHistoryStore(path).ForAccount(Me);

        static IEnumerable<string> Shape(IEnumerable<InstanceHistoryEntry> rows)
            => rows.Select(row =>
                $"{row.Id}|{row.Location}|{row.JoinedAt:O}|{row.LeftAt:O}|{row.LastEventAt:O}|" +
                string.Join(";", row.Players.Select(player =>
                    $"{player.UserId}:" + string.Join(",", player.Visits.Select(visit =>
                        $"{visit.JoinedAt:O}-{visit.LeftAt:O}")))));

        Assert.Equal(3, appended.Count);
        Assert.Equal(Shape(fresh), Shape(appended));
    }

    [Fact]
    public void ARefreshDoesNotReReadEveryStoredLocation()
    {
        var path = Path.Combine(_directory, "locations.db");
        var store = new InstanceHistoryStore(path);
        var start = DateTimeOffset.Now.AddHours(-10);
        for (var index = 0; index < 300; index++)
        {
            var joined = start.AddMinutes(index * 2);
            var world = $"wrld_{index:00000000}-1111-1111-1111-111111111111";
            var entry = InstanceHistoryStore.NewFromLog(Me, world + ":12345~region(eu)", joined)!;
            entry.WorldName = $"World {index}";
            entry.WorldThumbnailUrl = $"https://example.test/thumb-{index}.png";
            entry.WorldAuthorName = $"Author {index}";
            entry.GetOrAddPlayer(Me, "Me").StartVisit(joined);
            entry.Close(joined.AddMinutes(1));
            store.Add(entry);
        }

        store.ForAccount(Me);
        store.RecordPlayerEvent(Me, LocationA, Other, "Someone", string.Empty, joined: true, DateTimeOffset.Now);
        var measureStart = GC.GetAllocatedBytesForCurrentThread();
        var rows = store.ForAccount(Me);
        var cost = GC.GetAllocatedBytesForCurrentThread() - measureStart;

        Assert.Equal(301, rows.Count);
        // Measured here: 18,824 B against 249,768 B when every stored location
        // row was read again on each refresh.
        Assert.True(
            cost * 4 < 249_768,
            $"Refresh with 300 stored locations allocated {cost:N0} B.");

        // Metadata enriched after the cache is warm must still show up: the
        // stored timestamp does not move here, so only the write path's version
        // bump can invalidate the cached rows.
        var target = rows.Single(row => row.WorldName == "World 5");
        var update = InstanceHistoryStore.NewFromLog(Me, target.Location, target.JoinedAt)!;
        update.WorldName = "Renamed World";
        store.UpsertMetadata(update);

        var refreshed = store.ForAccount(Me).Single(row => row.Id == target.Id);
        Assert.Equal("Renamed World", refreshed.WorldName);
        Assert.Equal("Author 5", refreshed.WorldAuthorName);
    }

    [Fact]
    public void AHistoryPollWithNothingNewKeepsItsRows()
    {
        // Close() clears the collection, so everything stays on this thread.
        var store = new InstanceHistoryStore(Path.Combine(_directory, "poll-reuse.db"));
        var start = DateTimeOffset.Now.AddHours(-12);
        for (var index = 0; index < 200; index++)
        {
            var joined = start.AddMinutes(index * 2);
            var location = $"wrld_{index:00000000}-1111-1111-1111-111111111111:12345~region(eu)";
            var entry = InstanceHistoryStore.NewFromLog(Me, location, joined)!;
            entry.WorldName = $"World {index}";
            entry.WorldThumbnailUrl = $"https://example.test/{index}.png";
            entry.WorldAuthorName = "Author";
            entry.GetOrAddPlayer(Me, "Me").StartVisit(joined);
            for (var player = 0; player < 4; player++)
            {
                entry.GetOrAddPlayer(
                    $"usr_{player}{index:00000000}-1111-1111-1111-111111111111",
                    $"Player {player}").StartVisit(joined);
            }

            entry.Close(joined.AddMinutes(1));
            store.Add(entry);
        }

        using var api = new VrcApiClient();
        using var viewModel = new InstanceHistoryViewModel(api, store);
        viewModel.SetAccount(new VrcUser { Id = Me, DisplayName = "Me" });
        viewModel.RefreshAsync().GetAwaiter().GetResult();
        Assert.Equal(200, viewModel.Entries.Count);
        var rows = viewModel.Entries.ToArray();

        // Polling the same store must not rebuild the rows: that was ~6 KB of
        // garbage per row, every poll, for a history nobody changed.
        var before = GC.GetAllocatedBytesForCurrentThread();
        viewModel.RefreshAsync().GetAwaiter().GetResult();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(rows.Length, viewModel.Entries.Count);
        for (var index = 0; index < rows.Length; index++)
        {
            Assert.Same(rows[index], viewModel.Entries[index]);
        }

        Assert.True(
            allocated < 64 * 1024,
            $"An unchanged poll allocated {allocated:N0} B; it should not rebuild its rows.");
    }

    [Fact]
    public void ANewHistoryEventStillRebuildsTheRows()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "poll-change.db"));
        var joined = DateTimeOffset.Now.AddHours(-1);
        var entry = InstanceHistoryStore.NewFromLog(Me, LocationA, joined)!;
        entry.WorldName = "Thunder Tavern";
        entry.WorldThumbnailUrl = "https://example.test/world.png";
        entry.WorldAuthorName = "Author";
        entry.GetOrAddPlayer(Me, "Me").StartVisit(joined);
        store.Add(entry);

        using var api = new VrcApiClient();
        using var viewModel = new InstanceHistoryViewModel(api, store);
        viewModel.SetAccount(new VrcUser { Id = Me, DisplayName = "Me" });
        viewModel.RefreshAsync().GetAwaiter().GetResult();
        var first = Assert.Single(viewModel.Entries);

        var revisionBefore = store.Revision;
        store.RecordPlayerEvent(
            Me, LocationA, Other, "Other", string.Empty, true, joined.AddMinutes(5));
        Assert.NotEqual(revisionBefore, store.Revision);

        viewModel.RefreshAsync().GetAwaiter().GetResult();
        var updated = Assert.Single(viewModel.Entries);
        Assert.Contains(updated.Players, player => player.UserId == Other);
        Assert.NotSame(first, updated);
    }

    [Fact]
    public void ClosingTheHistoryPopupReleasesItsRowsAndReopeningRebuildsThem()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "close-releases.db"));
        var joined = DateTimeOffset.Now.AddHours(-2);
        var entry = InstanceHistoryStore.NewFromLog(Me, LocationA, joined)!;
        entry.WorldName = "Thunder Tavern";
        entry.WorldThumbnailUrl = "https://example.test/world.png";
        entry.WorldAuthorName = "Author";
        entry.GetOrAddPlayer(Me, "Me").StartVisit(joined);
        store.Add(entry);

        using var api = new VrcApiClient();
        using var viewModel = new InstanceHistoryViewModel(api, store);
        viewModel.SetAccount(new VrcUser { Id = Me, DisplayName = "Me" });
        viewModel.RefreshAsync().GetAwaiter().GetResult();
        Assert.Single(viewModel.Entries);

        viewModel.Close();
        Assert.Empty(viewModel.Entries);
        Assert.Null(viewModel.SelectedEntry);

        // Reopening must not depend on the rows that were released.
        viewModel.RefreshAsync().GetAwaiter().GetResult();
        Assert.Single(viewModel.Entries);
    }

    [Fact]
    public void SameLocationHoursApartIsStoredAsTwoVisits()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "grouping.db"));
        var first = DateTimeOffset.Now.AddHours(-4);
        var second = first.AddHours(2);

        var firstEntry = InstanceHistoryStore.NewFromLog(Me, LocationA, first)!;
        firstEntry.GetOrAddPlayer(Me, "Me").StartVisit(first);
        firstEntry.Close(first.AddMinutes(10));
        store.Add(firstEntry);

        var secondEntry = InstanceHistoryStore.NewFromLog(Me, LocationA, second)!;
        secondEntry.GetOrAddPlayer(Me, "Me").StartVisit(second);
        secondEntry.Close(second.AddMinutes(20));
        store.Add(secondEntry);

        var rows = store.ForAccount(Me).OrderBy(row => row.JoinedAt).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(10, rows[0].Duration.TotalMinutes);
        Assert.Equal(20, rows[1].Duration.TotalMinutes);
    }

    [Fact]
    public async Task ViewModelCollapsesRepeatedVisitsToTheSameInstanceButKeepsOtherInstances()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "same-instance.db"));
        var first = DateTimeOffset.Now.AddHours(-2);
        var second = first.AddMinutes(8);

        var firstEntry = InstanceHistoryStore.NewFromLog(Me, LocationA, first)!;
        firstEntry.WorldName = "Thunder Tavern";
        firstEntry.WorldThumbnailUrl = "https://example.test/world.png";
        firstEntry.WorldAuthorName = "Creator";
        firstEntry.GetOrAddPlayer(Me, "Me").StartVisit(first);
        firstEntry.GetOrAddPlayer(Friend, "Juicy_Drink").StartVisit(first);
        firstEntry.Close(first.AddMinutes(5));
        store.Add(firstEntry);

        var secondEntry = InstanceHistoryStore.NewFromLog(Me, LocationA, second)!;
        secondEntry.WorldName = "Thunder Tavern";
        secondEntry.WorldThumbnailUrl = "https://example.test/world.png";
        secondEntry.WorldAuthorName = "Creator";
        secondEntry.GetOrAddPlayer(Me, "Me").StartVisit(second);
        secondEntry.GetOrAddPlayer(Other, "Other").StartVisit(second);
        secondEntry.Close(second.AddMinutes(15));
        store.Add(secondEntry);

        var otherInstance = WorldA + ":99999~region(eu)";
        var otherEntry = InstanceHistoryStore.NewFromLog(Me, otherInstance, second)!;
        otherEntry.WorldName = "Thunder Tavern";
        otherEntry.WorldThumbnailUrl = "https://example.test/world.png";
        otherEntry.WorldAuthorName = "Creator";
        otherEntry.GetOrAddPlayer(Me, "Me").StartVisit(second);
        otherEntry.Close(second.AddMinutes(4));
        store.Add(otherEntry);

        using var api = new VrcApiClient();
        using var viewModel = new InstanceHistoryViewModel(api, store);
        viewModel.SetAccount(new VrcUser { Id = Me, DisplayName = "Me" });
        await viewModel.RefreshAsync();

        Assert.Equal(2, viewModel.Entries.Count);
        var merged = Assert.Single(viewModel.Entries, item => item.Entry.Location == LocationA);
        Assert.Equal(3, merged.Entry.SourceEntryIds.Count);
        Assert.Equal(3, merged.PlayerCount);
        Assert.Contains(merged.Players, player => player.UserId == Friend);
        Assert.Contains(merged.Players, player => player.UserId == Other);
        Assert.Contains(viewModel.Entries, item => item.Entry.Location == otherInstance);
    }

    [Fact]
    public void KnownUserIdIsReusedOnlyWhenTheNameIsUnambiguous()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "known-id.db"));
        var start = DateTimeOffset.Now.AddHours(-3);

        var nameOnly = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
        nameOnly.GetOrAddPlayer(string.Empty, "xoestasy").StartVisit(start);
        nameOnly.Close(start.AddMinutes(5));
        store.Add(nameOnly);

        // The same name was never seen with an id, so nothing is invented.
        Assert.Null(store.FindKnownUserId(Me, "xoestasy"));

        var withId = InstanceHistoryStore.NewFromLog(Me, LocationB, start.AddMinutes(10))!;
        withId.GetOrAddPlayer(Friend, "xoestasy").StartVisit(start.AddMinutes(10));
        withId.Close(start.AddMinutes(20));
        store.Add(withId);

        // One known id for the name is safe to borrow for the name-only row.
        Assert.Equal(Friend, store.FindKnownUserId(Me, "xoestasy"));

        var impostor = InstanceHistoryStore.NewFromLog(Me, LocationC, start.AddMinutes(30))!;
        impostor.GetOrAddPlayer(Other, "xoestasy").StartVisit(start.AddMinutes(30));
        impostor.Close(start.AddMinutes(40));
        store.Add(impostor);

        // Two different people share the display name: keep the row untouched.
        Assert.Null(store.FindKnownUserId(Me, "xoestasy"));
    }

    [Fact]
    public void DeletingAMergedHistoryRowRemovesEverySourceSession()
    {
        RunOnSta(() =>
        {
            var store = new InstanceHistoryStore(Path.Combine(_directory, "delete-merged.db"));
            var first = DateTimeOffset.Now.AddHours(-2);
            var second = first.AddMinutes(8);

            var firstEntry = InstanceHistoryStore.NewFromLog(Me, LocationA, first)!;
            SetWorldMetadata(firstEntry);
            firstEntry.GetOrAddPlayer(Me, "Me").StartVisit(first);
            firstEntry.GetOrAddPlayer(Friend, "Friend").StartVisit(first);
            firstEntry.Close(first.AddMinutes(5));
            store.Add(firstEntry);

            var secondEntry = InstanceHistoryStore.NewFromLog(Me, LocationA, second)!;
            SetWorldMetadata(secondEntry);
            secondEntry.GetOrAddPlayer(Me, "Me").StartVisit(second);
            secondEntry.GetOrAddPlayer(Other, "Other").StartVisit(second);
            secondEntry.Close(second.AddMinutes(15));
            store.Add(secondEntry);

            using var api = new VrcApiClient();
            using var viewModel = new InstanceHistoryViewModel(api, store);
            viewModel.SetAccount(new VrcUser { Id = Me, DisplayName = "Me" });
            viewModel.RefreshAsync().GetAwaiter().GetResult();

            var merged = Assert.Single(viewModel.Entries);
            Assert.True(merged.Entry.SourceEntryIds.Count > 1);

            merged.DeleteCommand.Execute(null);

            Assert.Empty(viewModel.Entries);
            Assert.Empty(store.ForAccount(Me));
        });
    }

    [Fact]
    public async Task ClosingAndReopeningHistoryKeepsRowsVisible()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "reopen.db"));
        var start = DateTimeOffset.Now.AddHours(-1);
        var entry = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
        entry.WorldName = "Reopen World";
        entry.WorldThumbnailUrl = "https://example.test/world.png";
        entry.WorldAuthorName = "Creator";
        entry.GetOrAddPlayer(Me, "Me").StartVisit(start);
        entry.Close(start.AddMinutes(10));
        store.Add(entry);

        using var api = new VrcApiClient();
        using var viewModel = new InstanceHistoryViewModel(api, store);
        viewModel.SetAccount(new VrcUser { Id = Me, DisplayName = "Me" });
        await viewModel.RefreshAsync();
        Assert.Single(viewModel.EntriesView.Cast<InstanceHistoryItem>());

        viewModel.SearchText = "does-not-match";
        Assert.Empty(viewModel.EntriesView.Cast<InstanceHistoryItem>());

        viewModel.Close();
        viewModel.Show();
        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsOpen);
        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.HasNoEntries);
        Assert.Single(viewModel.EntriesView.Cast<InstanceHistoryItem>());
    }

    [Fact]
    public void RecentPlayersAggregateSessionsAndSurviveAStoreReload()
    {
        RunOnSta(() =>
        {
            var path = Path.Combine(_directory, "recent-players.db");
            var store = new InstanceHistoryStore(path);
            var start = DateTimeOffset.Now.AddHours(-3);

            var first = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
            first.WorldName = "The Great Pug";
            first.GetOrAddPlayer(Me, "Me").StartVisit(start);
            first.GetOrAddPlayer(Friend, "Juicy_Drink").StartVisit(start);
            first.GetOrAddPlayer(Other, "Other").StartVisit(start);
            first.Close(start.AddMinutes(20));
            store.Add(first);

            var second = InstanceHistoryStore.NewFromLog(Me, LocationB, start.AddHours(1))!;
            second.WorldName = "Thunder Tavern";
            second.GetOrAddPlayer(Me, "Me").StartVisit(start.AddHours(1));
            second.GetOrAddPlayer(Friend, "Juicy_Drink").StartVisit(start.AddHours(1));
            second.Close(start.AddMinutes(70));
            store.Add(second);
            store.Save();

            using var api = new VrcApiClient();
            var viewModel = new RecentPlayersViewModel(api, store);
            RecentPlayerItem? openedProfile = null;
            RecentPlayerSessionItem? openedWorld = null;
            viewModel.PlayerProfileRequested = player => openedProfile = player;
            viewModel.WorldRequested = session => openedWorld = session;
            viewModel.Show(Me, new[] { Friend });
            viewModel.RefreshAsync().GetAwaiter().GetResult();

            Assert.Equal(2, viewModel.Players.Count);
            var friend = viewModel.Players[0];
            Assert.Equal(Friend, friend.UserId);
            Assert.True(friend.IsFriend);
            Assert.False(viewModel.Players[1].IsFriend);
            Assert.Equal(2, friend.Sessions.Count);
            Assert.Equal(TimeSpan.FromMinutes(30), friend.TotalTime);
            Assert.Equal("Thunder Tavern", friend.Sessions[0].WorldName);
            Assert.Equal("The Great Pug", friend.Sessions[1].WorldName);

            friend.OpenProfileCommand.Execute(null);
            Assert.Same(friend, openedProfile);
            friend.Sessions[0].OpenWorldCommand.Execute(null);
            Assert.Same(friend.Sessions[0], openedWorld);

            viewModel.SearchText = "Other";
            Assert.Single(viewModel.PlayersView.Cast<RecentPlayerItem>());

            var reloadedStore = new InstanceHistoryStore(path);
            reloadedStore.Load();
            var reloaded = new RecentPlayersViewModel(api, reloadedStore);
            reloaded.Show(Me, new[] { Friend });
            reloaded.RefreshAsync().GetAwaiter().GetResult();

            Assert.Equal(2, reloaded.Players.Count);
            Assert.Equal(2, reloaded.Players[0].Sessions.Count);
            Assert.Equal(TimeSpan.FromMinutes(30), reloaded.Players[0].TotalTime);
        });
    }

    [Fact]
    public void RecentPlayerSessionsExpandAndCollapse()
    {
        var end = DateTimeOffset.Now;
        var sessions = Enumerable.Range(0, 12)
            .Select(index => new RecentPlayerSessionItem(
                "Test World",
                "wrld_test",
                "wrld_test:1",
                string.Empty,
                end.AddDays(-index).AddHours(-1),
                end.AddDays(-index),
                end.AddDays(-index),
                TimeSpan.FromHours(1),
                null))
            .ToList();
        var player = new RecentPlayerItem("usr_test", "Test Player", string.Empty, false, sessions, null);

        Assert.Equal(5, player.VisibleSessions.Count);
        Assert.True(player.HasMoreSessions);
        Assert.Equal("+7 earlier sessions", player.MoreSessionsText);

        player.ToggleSessionsCommand.Execute(null);
        Assert.Equal(12, player.VisibleSessions.Count);
        Assert.Equal("Show fewer sessions", player.MoreSessionsText);

        player.ToggleSessionsCommand.Execute(null);
        Assert.Equal(5, player.VisibleSessions.Count);
        Assert.Equal("+7 earlier sessions", player.MoreSessionsText);
    }

    private static void SetWorldMetadata(InstanceHistoryEntry entry)
    {
        entry.WorldName = "Thunder Tavern";
        entry.WorldThumbnailUrl = "https://example.test/world.png";
        entry.WorldAuthorName = "Creator";
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("STA test failed.", failure);
        }
    }

    [Fact]
    public void SelfLeaveEndsTheRowEvenWhenPlayersLeaveLater()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "trailing.db"));
        var start = DateTimeOffset.Now.AddHours(-2);
        var entry = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
        var self = entry.GetOrAddPlayer(Me, "Me");
        self.StartVisit(start).LeftAt = start.AddMinutes(10);
        var friend = entry.GetOrAddPlayer(Friend, "Juicy_Drink");
        friend.StartVisit(start.AddMinutes(1)).LeftAt = start.AddMinutes(20);
        entry.LeftAt = start.AddMinutes(10);
        entry.LastSeenAt = start.AddMinutes(20);

        store.Add(entry);

        var row = Assert.Single(store.ForAccount(Me));
        Assert.Equal(10, row.Duration.TotalMinutes);
        Assert.Equal(start.AddMinutes(20), row.LastEventAt);
    }

    [Fact]
    public void GlobalLoadKeepsAccountsAtTheSameLocationSeparate()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "accounts.db"));
        var start = DateTimeOffset.Now.AddHours(-1);
        var accountB = "usr_dddddddd-dddd-dddd-dddd-dddddddddddd";

        var first = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
        first.GetOrAddPlayer(Me, "Me").StartVisit(start);
        var second = InstanceHistoryStore.NewFromLog(accountB, LocationA, start)!;
        second.GetOrAddPlayer(accountB, "Other me").StartVisit(start);

        store.Add(first);
        store.Add(second);

        Assert.Single(store.ForAccount(Me));
        Assert.Single(store.ForAccount(accountB));
        var loadedFirst = store.Find(first.Id)!;
        var loadedSecond = store.Find(second.Id)!;
        Assert.Equal(Me, loadedFirst.AccountUserId);
        Assert.Equal(accountB, loadedSecond.AccountUserId);
        Assert.True(loadedFirst.IsOpen);
        Assert.True(loadedSecond.IsOpen);
    }

    [Fact]
    public void EventsAtTheSameTimestampAreReadJoinBeforeLeave()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "same-time.db"));
        var start = DateTimeOffset.Now.AddHours(-1);
        var entry = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
        entry.GetOrAddPlayer(Me, "Me").StartVisit(start);
        var pairedAt = start.AddMinutes(5);
        var friend = entry.GetOrAddPlayer(Friend, "Friend");
        friend.StartVisit(pairedAt).LeftAt = pairedAt;
        entry.LastSeenAt = pairedAt;
        store.Add(entry);

        var row = Assert.Single(store.ForAccount(Me));
        var readFriend = Assert.Single(row.Players, player => player.UserId == Friend);
        Assert.False(readFriend.HasOpenVisit);
    }

    [Fact]
    public void RecorderStartsANewSessionWhenAStaleSameLocationRowWasClosed()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "reopen.db"));
        var oldStart = DateTimeOffset.Now.AddHours(-2);
        var old = InstanceHistoryStore.NewFromLog(Me, LocationA, oldStart)!;
        old.GetOrAddPlayer(Me, "Me").StartVisit(oldStart);
        store.Add(old);

        var recorder = new InstanceHistoryRecorder(store);
        var user = new VrcUser { Id = Me, DisplayName = "Me" };
        recorder.SetAccount(user);
        Assert.All(store.ForAccount(Me), row => Assert.NotNull(row.LeftAt));

        var now = DateTimeOffset.Now;
        Assert.True(recorder.Observe(
            new VrChatLogSnapshot(LocationA, new[] { new VrChatLogPlayer(Me, "Me") }),
            gameRunning: true,
            now));

        var rows = store.ForAccount(Me).OrderBy(row => row.JoinedAt).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, row => row.LeftAt is null);
        Assert.Equal(LocationA, rows[^1].Location);
    }

    [Fact]
    public void RecorderClosesAtTheLastEventWhenVrchatIsNoLongerRunning()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "game-stop.db"));
        var recorder = new InstanceHistoryRecorder(store);
        recorder.SetAccount(new VrcUser { Id = Me, DisplayName = "Me" });
        var start = DateTimeOffset.Now.AddMinutes(-10);
        var snapshot = new VrChatLogSnapshot(LocationA, new[]
        {
            new VrChatLogPlayer(Me, "Me"),
            new VrChatLogPlayer(Friend, "Friend")
        });

        recorder.Observe(snapshot, gameRunning: true, start);
        var later = start.AddHours(1);
        Assert.True(recorder.Observe(snapshot, gameRunning: false, later));

        var row = Assert.Single(store.ForAccount(Me));
        Assert.NotNull(row.LeftAt);
        Assert.Equal(start, row.LeftAt);
        Assert.True(row.Duration < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void DeletingOneSessionDoesNotRemoveTheNextAtTheSameLocation()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "delete-boundary.db"));
        var start = DateTimeOffset.Now.AddHours(-1);

        var first = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
        first.GetOrAddPlayer(Me, "Me").StartVisit(start).LeftAt = start.AddMinutes(10);
        first.LeftAt = start.AddMinutes(10);
        first.LastSeenAt = start.AddMinutes(10);
        store.Add(first);

        var secondStart = start.AddMinutes(10);
        var second = InstanceHistoryStore.NewFromLog(Me, LocationA, secondStart)!;
        second.GetOrAddPlayer(Me, "Me").StartVisit(secondStart).LeftAt = secondStart.AddMinutes(10);
        second.LeftAt = secondStart.AddMinutes(10);
        second.LastSeenAt = secondStart.AddMinutes(10);
        store.Add(second);

        var rows = store.ForAccount(Me).OrderBy(row => row.JoinedAt).ToList();
        Assert.Equal(2, rows.Count);
        Assert.True(store.Delete(rows[0].Id));

        var remaining = Assert.Single(store.ForAccount(Me));
        Assert.Equal(secondStart, remaining.JoinedAt);
        Assert.Equal(10, remaining.Duration.TotalMinutes);
    }

    [Fact]
    public async Task PersonHistoryShowsOnlyInstancesSharedWithThatPerson()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "person-filter.db"));
        var start = DateTimeOffset.Now.AddHours(-2);

        var shared = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
        shared.WorldName = "Shared World";
        shared.WorldThumbnailUrl = "https://example.test/shared.png";
        shared.OwnerName = "Owner";
        shared.GetOrAddPlayer(Me, "Me").StartVisit(start);
        shared.GetOrAddPlayer(Friend, "Friend").StartVisit(start);
        shared.Close(start.AddMinutes(10));
        store.Add(shared);

        var other = InstanceHistoryStore.NewFromLog(Me, LocationB, start.AddHours(1))!;
        other.WorldName = "Other World";
        other.WorldThumbnailUrl = "https://example.test/other.png";
        other.OwnerName = "Owner";
        other.GetOrAddPlayer(Me, "Me").StartVisit(start.AddHours(1));
        other.GetOrAddPlayer(Other, "Other").StartVisit(start.AddHours(1));
        other.Close(start.AddMinutes(70));
        store.Add(other);

        using var api = new VrcApiClient();
        var viewModel = new InstanceHistoryViewModel(api, store);
        viewModel.SetAccount(new VrcUser { Id = Me, DisplayName = "Me" });
        viewModel.ShowForUser(Friend, "Friend");
        await viewModel.RefreshAsync();

        var visible = viewModel.EntriesView.Cast<InstanceHistoryItem>().ToList();
        var row = Assert.Single(visible);
        Assert.Equal(LocationA, row.Entry.Location);
        Assert.Contains(row.Players, player => player.UserId == Friend);
        Assert.True(viewModel.IsFiltered);

        viewModel.ClearFilterCommand.Execute(null);
        Assert.Equal(2, viewModel.EntriesView.Cast<InstanceHistoryItem>().Count());
        Assert.False(viewModel.IsFiltered);

    }

    [Fact]
    public void ReopeningPersonHistoryKeepsTheSharedRows()
    {
        RunOnSta(() =>
        {
            var store = new InstanceHistoryStore(Path.Combine(_directory, "person-reopen.db"));
            var start = DateTimeOffset.Now.AddHours(-2);
            var shared = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
            SetWorldMetadata(shared);
            shared.GetOrAddPlayer(Me, "Me").StartVisit(start);
            shared.GetOrAddPlayer(Friend, "Maya").StartVisit(start);
            shared.Close(start.AddMinutes(10));
            store.Add(shared);

            using var api = new VrcApiClient();
            using var viewModel = new InstanceHistoryViewModel(api, store);
            viewModel.SetAccount(new VrcUser { Id = Me, DisplayName = "Me" });

            viewModel.ShowForUser(Friend, "Maya");
            viewModel.RefreshAsync().GetAwaiter().GetResult();
            Assert.Single(viewModel.EntriesView.Cast<InstanceHistoryItem>());

            viewModel.Close();
            viewModel.ShowForUser(Friend, "Maya");
            viewModel.RefreshAsync().GetAwaiter().GetResult();
            var reopened = Assert.Single(viewModel.EntriesView.Cast<InstanceHistoryItem>());
            Assert.Contains(reopened.Players, player => player.UserId == Friend);
        });
    }

    [Fact]
    public void PersonHistoryResolvesNameOnlyPlayersFromTheProfileIdentity()
    {
        RunOnSta(() =>
        {
            var store = new InstanceHistoryStore(Path.Combine(_directory, "person-name-only.db"));
            var start = DateTimeOffset.Now.AddHours(-2);

            var known = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
            SetWorldMetadata(known);
            known.GetOrAddPlayer(Me, "Me").StartVisit(start);
            known.GetOrAddPlayer(Friend, "Maya").StartVisit(start);
            known.Close(start.AddMinutes(10));
            store.Add(known);

            var nameOnly = InstanceHistoryStore.NewFromLog(
                Me,
                LocationB,
                start.AddMinutes(20))!;
            SetWorldMetadata(nameOnly);
            nameOnly.GetOrAddPlayer(Me, "Me").StartVisit(start.AddMinutes(20));
            nameOnly.GetOrAddPlayer(string.Empty, "Maya").StartVisit(start.AddMinutes(20));
            nameOnly.Close(start.AddMinutes(30));
            store.Add(nameOnly);

            using var api = new VrcApiClient();
            using var viewModel = new InstanceHistoryViewModel(api, store);
            viewModel.SetAccount(new VrcUser { Id = Me, DisplayName = "Me" });

            viewModel.ShowForUser(Friend, "Maya");
            viewModel.RefreshAsync().GetAwaiter().GetResult();

            var rows = viewModel.EntriesView.Cast<InstanceHistoryItem>().ToList();
            Assert.Equal(2, rows.Count);
            var row = Assert.Single(rows, item => item.Entry.Location == LocationB);
            Assert.Contains(row.Players, player =>
                player.UserId == Friend && player.DisplayName == "Maya");
            Assert.All(
                store.ForAccount(Me).SelectMany(candidate => candidate.Players)
                    .Where(player => player.DisplayName == "Maya"),
                player => Assert.Equal(Friend, player.UserId));
        });
    }

    [Fact]
    public void IdentityRepairDoesNotOverwriteAConflictingDisplayName()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "person-conflict.db"));
        var start = DateTimeOffset.Now.AddHours(-2);

        var nameOnly = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
        SetWorldMetadata(nameOnly);
        nameOnly.GetOrAddPlayer(Me, "Me").StartVisit(start);
        nameOnly.GetOrAddPlayer(string.Empty, "Maya").StartVisit(start);
        nameOnly.Close(start.AddMinutes(5));
        store.Add(nameOnly);

        var otherMaya = InstanceHistoryStore.NewFromLog(Me, LocationB, start.AddMinutes(20))!;
        SetWorldMetadata(otherMaya);
        otherMaya.GetOrAddPlayer(Me, "Me").StartVisit(start.AddMinutes(20));
        otherMaya.GetOrAddPlayer(Other, "Maya").StartVisit(start.AddMinutes(20));
        otherMaya.Close(start.AddMinutes(25));
        store.Add(otherMaya);

        var knownMaya = InstanceHistoryStore.NewFromLog(Me, LocationA, start.AddHours(1))!;
        SetWorldMetadata(knownMaya);
        knownMaya.GetOrAddPlayer(Me, "Me").StartVisit(start.AddHours(1));
        knownMaya.GetOrAddPlayer(Friend, "Maya").StartVisit(start.AddHours(1));
        knownMaya.Close(start.AddHours(1).AddMinutes(5));
        store.Add(knownMaya);

        var repaired = store.AssignPlayerUserId(Me, "Maya", Friend);

        Assert.Equal(0, repaired);
        Assert.Contains(
            store.ForAccount(Me).SelectMany(entry => entry.Players),
            player => player.DisplayName == "Maya" && string.IsNullOrWhiteSpace(player.UserId));
    }

    [Fact]
    public void PersonHistoryFallsBackForEntirelyNameOnlyHistory()
    {
        RunOnSta(() =>
        {
            var store = new InstanceHistoryStore(Path.Combine(_directory, "person-name-only-fallback.db"));
            var start = DateTimeOffset.Now.AddHours(-2);
            var entry = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
            SetWorldMetadata(entry);
            entry.GetOrAddPlayer(Me, "Me").StartVisit(start);
            entry.GetOrAddPlayer(string.Empty, "Maya").StartVisit(start);
            entry.Close(start.AddMinutes(10));
            store.Add(entry);

            using var api = new VrcApiClient();
            using var viewModel = new InstanceHistoryViewModel(api, store);
            viewModel.SetAccount(new VrcUser { Id = Me, DisplayName = "Me" });

            viewModel.ShowForUser(Friend, "Maya");
            viewModel.RefreshAsync().GetAwaiter().GetResult();

            var row = Assert.Single(viewModel.EntriesView.Cast<InstanceHistoryItem>());
            Assert.Contains(row.Players, player =>
                string.IsNullOrWhiteSpace(player.UserId) && player.DisplayName == "Maya");
        });
    }

    [Fact]
    public void NameOnlyFallbackStaysOffWhenTheNameIsShared()
    {
        RunOnSta(() =>
        {
            var store = new InstanceHistoryStore(Path.Combine(_directory, "person-name-only-ambiguous.db"));
            var start = DateTimeOffset.Now.AddHours(-2);
            foreach (var location in new[] { LocationA, LocationB })
            {
                var entry = InstanceHistoryStore.NewFromLog(Me, location, start)!;
                SetWorldMetadata(entry);
                entry.GetOrAddPlayer(Me, "Me").StartVisit(start);
                entry.GetOrAddPlayer(string.Empty, "Maya").StartVisit(start);
                entry.Close(start.AddMinutes(5));
                store.Add(entry);
            }

            using var api = new VrcApiClient();
            using var viewModel = new InstanceHistoryViewModel(api, store);
            viewModel.SetAccount(new VrcUser { Id = Me, DisplayName = "Me" });

            viewModel.ShowForUser(Friend, "Maya");
            viewModel.RefreshAsync().GetAwaiter().GetResult();

            Assert.Empty(viewModel.EntriesView.Cast<InstanceHistoryItem>());
        });
    }

    [Fact]
    public void IdentityRepairStopsWhenAnotherNameOnlyPlayerSharesTheDisplayName()
    {
        var store = new InstanceHistoryStore(Path.Combine(_directory, "person-name-only-conflict.db"));
        var start = DateTimeOffset.Now.AddHours(-2);

        var knownMaya = InstanceHistoryStore.NewFromLog(Me, LocationA, start)!;
        SetWorldMetadata(knownMaya);
        knownMaya.GetOrAddPlayer(Me, "Me").StartVisit(start);
        knownMaya.GetOrAddPlayer(Friend, "Maya").StartVisit(start);
        knownMaya.Close(start.AddMinutes(5));
        store.Add(knownMaya);

        foreach (var location in new[] { LocationB, LocationC })
        {
            var nameOnly = InstanceHistoryStore.NewFromLog(
                Me,
                location,
                start.AddMinutes(20))!;
            SetWorldMetadata(nameOnly);
            nameOnly.GetOrAddPlayer(Me, "Me").StartVisit(start.AddMinutes(20));
            nameOnly.GetOrAddPlayer(string.Empty, "Maya").StartVisit(start.AddMinutes(20));
            nameOnly.Close(start.AddMinutes(25));
            store.Add(nameOnly);
        }

        var repaired = store.AssignPlayerUserId(Me, "Maya", Friend);

        Assert.Equal(0, repaired);
        var nameOnlyPlayers = store.ForAccount(Me)
            .SelectMany(entry => entry.Players)
            .Where(player => player.DisplayName == "Maya" && string.IsNullOrWhiteSpace(player.UserId))
            .ToList();
        Assert.NotEmpty(nameOnlyPlayers);
        Assert.All(nameOnlyPlayers, player => Assert.Equal(string.Empty, player.UserId));
    }

    private sealed class InviteHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string Path { get; private set; } = string.Empty;
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ShortNameHandler(string json) : HttpMessageHandler
    {
        public string Path { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
