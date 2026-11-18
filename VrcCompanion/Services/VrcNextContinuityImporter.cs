using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Reads VRCNext's local timeline database(s) read-only. VRCNext stores
/// instance visits in <c>events</c> and the observed roster in
/// <c>event_players</c>; its profile and memo caches supply names and notes.
/// </summary>
public sealed class VrcNextContinuityImporter
{
    public const string SourceName = "vrcnext";

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext");

    public string DirectoryPath { get; set; } = DefaultDirectory;

    public static IReadOnlyList<string> FindDatabases(string? directory = null)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory
                .EnumerateFiles(directory, "*VRCNData.db", SearchOption.TopDirectoryOnly)
                .Where(path => !path.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) &&
                               !path.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    public ContinuityImportResult Import(
        ContinuityStore store,
        string? currentAccountId,
        string? currentAccountDisplayName)
    {
        var databases = FindDatabases(DirectoryPath);
        var result = new ContinuityImportResult
        {
            Source = SourceName,
            Path = databases.Count == 0 ? DirectoryPath : string.Join("; ", databases),
            ImportedAt = DateTimeOffset.UtcNow
        };

        if (databases.Count == 0)
        {
            result.Message = "VRCNext was not found at " + DirectoryPath + ".";
            return result;
        }

        var sessions = new List<ContinuitySession>();
        var people = new Dictionary<string, ContinuityPerson>(StringComparer.Ordinal);
        var worlds = new Dictionary<string, ContinuityWorld>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var database in databases)
        {
            var accountId = AccountForDatabase(database, currentAccountId);
            if (string.IsNullOrWhiteSpace(accountId))
            {
                errors.Add("VRCNext's primary database has no signed-in account id; sign in and import again.");
                continue;
            }

            try
            {
                using var connection = OpenReadOnly(database);
                if (!TableExists(connection, "events"))
                {
                    continue;
                }

                var players = ReadPlayers(connection);
                var events = ReadInstanceEvents(connection);
                if (events.Count == 0)
                {
                    continue;
                }

                var taken = store.GetSessions(accountId, null, 5000).ToList();
                foreach (var item in events)
                {
                    if (taken.Any(candidate =>
                            string.Equals(candidate.AccountUserId, accountId, StringComparison.Ordinal) &&
                            string.Equals(candidate.Location, item.Location, StringComparison.Ordinal) &&
                            Math.Abs((candidate.JoinedAt - item.Timestamp).TotalMinutes) <= 5))
                    {
                        continue;
                    }

                    var start = item.Timestamp;
                    var end = item.LeftAt is not null && item.LeftAt > start
                        ? item.LeftAt.Value
                        : start.AddSeconds(1);
                    var sessionPeople = new Dictionary<string, ContinuitySessionPerson>(StringComparer.Ordinal);
                    AddPerson(
                        sessionPeople,
                        accountId,
                        string.Equals(accountId, currentAccountId, StringComparison.Ordinal)
                            ? currentAccountDisplayName
                            : null,
                        start,
                        end);

                    if (players.TryGetValue(item.Id, out var observed))
                    {
                        foreach (var player in observed)
                        {
                            var key = PersonKey(player.UserId, player.DisplayName);
                            if (string.IsNullOrWhiteSpace(key))
                            {
                                continue;
                            }

                            var joined = player.JoinedAt ?? start;
                            if (joined < start)
                            {
                                joined = start;
                            }

                            if (joined > end)
                            {
                                joined = end;
                            }

                            var playerEnd = player.LeftAt is { } left && left > joined && left < end
                                ? left
                                : end;
                            AddPerson(
                                sessionPeople,
                                player.UserId,
                                player.DisplayName,
                                joined,
                                playerEnd,
                                player.AvatarUrl);

                            if (!people.TryGetValue(key, out var known))
                            {
                                people[key] = new ContinuityPerson
                                {
                                    PersonKey = key,
                                    UserId = player.UserId,
                                    DisplayName = player.DisplayName,
                                    AvatarUrl = player.AvatarUrl,
                                    Source = SourceName
                                };
                            }
                            else
                            {
                                if (!string.IsNullOrWhiteSpace(player.DisplayName))
                                {
                                    known.DisplayName = player.DisplayName;
                                }

                                if (!string.IsNullOrWhiteSpace(player.AvatarUrl))
                                {
                                    known.AvatarUrl = player.AvatarUrl;
                                }
                            }
                        }
                    }

                    if (!sessionPeople.ContainsKey(accountId))
                    {
                        AddPerson(
                            sessionPeople,
                            accountId,
                            string.Equals(accountId, currentAccountId, StringComparison.Ordinal)
                                ? currentAccountDisplayName
                                : null,
                            start,
                            end);
                    }

                    var sourceKey = accountId + "|" + item.Id;
                    var session = new ContinuitySession
                    {
                        Id = ContinuitySession.MakeId(SourceName, sourceKey),
                        AccountUserId = accountId,
                        Source = SourceName,
                        SourceKey = sourceKey,
                        Location = item.Location,
                        WorldId = item.WorldId,
                        WorldName = item.WorldName,
                        JoinedAt = start,
                        LeftAt = end,
                        DurationSeconds = Math.Max(1, (long)(end - start).TotalSeconds),
                        PlayerCount = sessionPeople.Count,
                        People = sessionPeople.Values
                            .OrderBy(person => person.JoinedAt)
                            .ThenBy(person => person.DisplayName, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    };
                    sessions.Add(session);
                    taken.Add(session);

                    if (!string.IsNullOrWhiteSpace(item.WorldId))
                    {
                        worlds[item.WorldId] = new ContinuityWorld
                        {
                            WorldId = item.WorldId,
                            Name = item.WorldName,
                            ThumbnailUrl = item.WorldThumb,
                            Source = SourceName
                        };
                    }
                }

                ReadPeople(connection, people);
                ReadWorlds(connection, worlds);
            }
            catch (Exception ex)
            {
                errors.Add(Path.GetFileName(database) + ": " + ex.Message);
            }
        }

        if (sessions.Count > 0)
        {
            store.MergeSessions(sessions);
        }

        store.UpsertPersonMetadata(people.Values);
        store.UpsertWorldMetadata(worlds.Values);

        result.Sessions = sessions.Count;
        result.People = sessions
            .SelectMany(session => session.People)
            .Select(person => person.PersonKey)
            .Concat(people.Keys)
            .Distinct(StringComparer.Ordinal)
            .Count();
        result.Worlds = sessions
            .Select(session => session.WorldId)
            .Where(worldId => !string.IsNullOrWhiteSpace(worldId))
            .Distinct(StringComparer.Ordinal)
            .Count();
        result.Message = sessions.Count == 0
            ? errors.Count == 0
                ? "VRCNext was found, but its timeline has no instance visits to import."
                : "VRCNext import found no readable visits. " + string.Join(" ", errors)
            : "Imported " + sessions.Count.ToString("N0", CultureInfo.InvariantCulture) +
              " real session(s) from VRCNext." +
              (errors.Count > 0 ? " " + string.Join(" ", errors) : string.Empty);
        store.RecordImport(result);
        return result;
    }

    private static string? AccountForDatabase(string path, string? currentAccountId)
    {
        var name = Path.GetFileName(path);
        const string suffix = "_VRCNData.db";
        if (name.StartsWith("usr_", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return name[..^suffix.Length];
        }

        return currentAccountId;
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static List<InstanceEvent> ReadInstanceEvents(SqliteConnection connection)
    {
        var rows = new List<InstanceEvent>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp, location, world_id, world_name, world_thumb, left_at
            FROM events
            WHERE type = 'instance_join'
            ORDER BY timestamp ASC, id ASC
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var timestamp = ParseTime(reader.GetString(1));
            if (timestamp is null)
            {
                continue;
            }

            var leftAt = reader.IsDBNull(6) ? null : ParseTime(reader.GetString(6));
            rows.Add(new InstanceEvent(
                reader.GetString(0),
                timestamp.Value,
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                leftAt));
        }

        return rows;
    }

    private static Dictionary<string, List<ObservedPlayer>> ReadPlayers(SqliteConnection connection)
    {
        var rows = new Dictionary<string, List<ObservedPlayer>>(StringComparer.Ordinal);
        if (!TableExists(connection, "event_players"))
        {
            return rows;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, user_id, display_name, image, joined_at, left_at
            FROM event_players
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var eventId = reader.GetString(0);
            var joins = ParseSessions(reader.IsDBNull(4) ? null : reader.GetString(4));
            var lefts = ParseSessions(reader.IsDBNull(5) ? null : reader.GetString(5));
            var player = new ObservedPlayer(
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                joins.Count == 0 ? null : joins.Min(),
                lefts.Count == 0 ? null : lefts.Max());
            if (!rows.TryGetValue(eventId, out var list))
            {
                rows[eventId] = list = new List<ObservedPlayer>();
            }

            list.Add(player);
        }

        return rows;
    }

    private static void ReadPeople(
        SqliteConnection connection,
        IDictionary<string, ContinuityPerson> people)
    {
        if (!TableExists(connection, "user_tracking"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT user_id, display_name, image, total_seconds, last_seen
            FROM user_tracking
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var userId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var displayName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var key = PersonKey(userId, displayName);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!people.TryGetValue(key, out var person))
            {
                person = new ContinuityPerson
                {
                    PersonKey = key,
                    UserId = userId,
                    Source = SourceName
                };
                people[key] = person;
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                person.DisplayName = displayName;
            }

            if (!reader.IsDBNull(2) && !string.IsNullOrWhiteSpace(reader.GetString(2)))
            {
                person.AvatarUrl = reader.GetString(2);
            }

            if (!reader.IsDBNull(4) && ParseTime(reader.GetString(4)) is { } lastSeen)
            {
                person.LastSeenAt = lastSeen;
            }
        }

        if (!TableExists(connection, "user_memos"))
        {
            return;
        }

        using var memos = connection.CreateCommand();
        memos.CommandText = "SELECT user_id, memo FROM user_memos";
        using var memoReader = memos.ExecuteReader();
        while (memoReader.Read())
        {
            var userId = memoReader.IsDBNull(0) ? string.Empty : memoReader.GetString(0);
            var key = PersonKey(userId, string.Empty);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!people.TryGetValue(key, out var person))
            {
                person = new ContinuityPerson
                {
                    PersonKey = key,
                    UserId = userId,
                    Source = SourceName
                };
                people[key] = person;
            }

            if (!memoReader.IsDBNull(1))
            {
                person.ImportedNote = memoReader.GetString(1);
            }
        }
    }

    private static void ReadWorlds(
        SqliteConnection connection,
        IDictionary<string, ContinuityWorld> worlds)
    {
        if (!TableExists(connection, "world_tracking"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT world_id, world_name, world_thumb
            FROM world_tracking
            WHERE world_id LIKE 'wrld_%'
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var worldId = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(worldId))
            {
                continue;
            }

            if (!worlds.TryGetValue(worldId, out var world))
            {
                world = new ContinuityWorld
                {
                    WorldId = worldId,
                    Source = SourceName
                };
                worlds[worldId] = world;
            }

            if (!reader.IsDBNull(1) && !string.IsNullOrWhiteSpace(reader.GetString(1)))
            {
                world.Name = reader.GetString(1);
            }

            if (!reader.IsDBNull(2) && !string.IsNullOrWhiteSpace(reader.GetString(2)))
            {
                world.ThumbnailUrl = reader.GetString(2);
            }
        }
    }

    private static void AddPerson(
        IDictionary<string, ContinuitySessionPerson> people,
        string? userId,
        string? displayName,
        DateTimeOffset joinedAt,
        DateTimeOffset end,
        string? avatarUrl = null)
    {
        var key = PersonKey(userId, displayName);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!people.TryGetValue(key, out var person))
        {
            person = new ContinuitySessionPerson
            {
                PersonKey = key,
                UserId = userId ?? string.Empty,
                DisplayName = displayName ?? string.Empty,
                AvatarUrl = avatarUrl ?? string.Empty,
                JoinedAt = joinedAt
            };
            people[key] = person;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                person.DisplayName = displayName;
            }

            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                person.AvatarUrl = avatarUrl;
            }
        }

        var left = end < joinedAt ? joinedAt : end;
        person.LeftAt = left;
        person.DurationSeconds = Math.Max(1, (long)(left - person.JoinedAt).TotalSeconds);
    }

    private static string PersonKey(string? userId, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            return userId;
        }

        return string.IsNullOrWhiteSpace(displayName)
            ? string.Empty
            : "name:" + displayName.Trim();
    }

    private static IReadOnlyList<DateTimeOffset> ParseSessions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<DateTimeOffset>();
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                var values = JsonSerializer.Deserialize<List<string>>(trimmed);
                return values?
                    .Select(ParseTime)
                    .Where(time => time is not null)
                    .Select(time => time!.Value)
                    .ToList() ?? new List<DateTimeOffset>();
            }
            catch (JsonException)
            {
                return Array.Empty<DateTimeOffset>();
            }
        }

        return ParseTime(trimmed) is { } single
            ? new[] { single }
            : Array.Empty<DateTimeOffset>();
    }

    private static DateTimeOffset? ParseTime(string value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;

    private sealed record InstanceEvent(
        string Id,
        DateTimeOffset Timestamp,
        string Location,
        string WorldId,
        string WorldName,
        string WorldThumb,
        DateTimeOffset? LeftAt);

    private sealed record ObservedPlayer(
        string UserId,
        string DisplayName,
        string AvatarUrl,
        DateTimeOffset? JoinedAt,
        DateTimeOffset? LeftAt);
}
