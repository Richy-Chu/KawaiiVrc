using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class VrcNextImportTests
{
    [Fact]
    public void VrcNextTimelineImportsRealSchemaRows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vrccompanion-vrcnext-" + Guid.NewGuid().ToString("N"));
        var database = Path.Combine(directory, "VRCNData.db");
        var storePath = Path.Combine(Path.GetTempPath(), "vrccompanion-vrcnext-" + Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(directory);
        try
        {
            using (var connection = new SqliteConnection("Data Source=" + database))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE events (
                        id TEXT PRIMARY KEY, type TEXT, timestamp TEXT, world_id TEXT,
                        world_name TEXT, world_thumb TEXT, location TEXT, left_at TEXT);
                    CREATE TABLE event_players (
                        event_id TEXT, user_id TEXT, display_name TEXT, image TEXT,
                        joined_at TEXT, left_at TEXT, PRIMARY KEY(event_id, user_id));
                    CREATE TABLE user_tracking (
                        user_id TEXT PRIMARY KEY, display_name TEXT, image TEXT,
                        total_seconds INTEGER, last_seen TEXT);
                    CREATE TABLE world_tracking (
                        world_id TEXT PRIMARY KEY, world_name TEXT, world_thumb TEXT);
                    CREATE TABLE user_memos (user_id TEXT PRIMARY KEY, memo TEXT);

                    INSERT INTO events VALUES (
                        'evt_real', 'instance_join', '2026-11-16T10:00:00.000Z',
                        'wrld_real', 'Real VRCNext world', 'https://example.invalid/world.png',
                        'wrld_real:123~region(us)', '2026-11-16T11:00:00.000Z');
                    INSERT INTO event_players VALUES (
                        'evt_real', 'usr_friend', 'Real friend', 'https://example.invalid/avatar.png',
                        '["2026-11-16T10:05:00.000Z"]', '["2026-11-16T10:55:00.000Z"]');
                    INSERT INTO user_tracking VALUES (
                        'usr_friend', 'Real friend', 'https://example.invalid/avatar.png', 3000,
                        '2026-11-16T10:55:00.000Z');
                    INSERT INTO user_memos VALUES ('usr_friend', 'existing VRCNext memo');
                    INSERT INTO world_tracking VALUES (
                        'wrld_real', 'Real VRCNext world', 'https://example.invalid/world.png');
                    """;
                command.ExecuteNonQuery();
            }

            var store = new ContinuityStore(storePath);
            var importer = new VrcNextContinuityImporter { DirectoryPath = directory };
            var result = importer.Import(store, "usr_account", "Real account");

            Assert.True(result.Error is null, result.Error?.ToString() ?? result.Message);
            Assert.Equal(1, result.Sessions);
            Assert.Equal(1, result.Worlds);
            Assert.True(result.People >= 2);

            var summary = store.GetSummary("usr_account");
            Assert.Equal(1, summary.SessionCount);
            Assert.Equal(1, summary.WorldCount);

            var session = store.GetSessions("usr_account").Single();
            Assert.Equal("Real VRCNext world", session.WorldName);
            Assert.Equal("wrld_real", session.WorldId);
            Assert.Equal(3600, session.DurationSeconds);
            Assert.Equal(2, session.PlayerCount);

            var friend = store.GetPerson("usr_friend", "usr_account");
            Assert.NotNull(friend);
            Assert.Equal("existing VRCNext memo", friend!.ImportedNote);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(storePath);
            File.Delete(storePath + "-wal");
            File.Delete(storePath + "-shm");
            Directory.Delete(directory, recursive: true);
        }
    }
}
