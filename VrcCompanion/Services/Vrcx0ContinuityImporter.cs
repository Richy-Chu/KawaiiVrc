using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Reads the real VRCX-0 SQLite database without modifying it. VRCX-0 records
/// location changes and player observations; there is no VRChat endpoint for
/// this history, so only rows that actually exist in the database are imported.
/// </summary>
public sealed class Vrcx0ContinuityImporter
{
    public const string SourceName = "vrcx-0";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCX-0",
        "VRCX-0.sqlite3");

    public string? DatabasePath { get; set; } = DefaultPath;

    public ContinuityImportResult Import(
        ContinuityStore store,
        string? currentAccountId = null,
        string? currentAccountDisplayName = null,
        IProgress<string>? progress = null)
    {
        pendingSessions.Clear();
        var result = new ContinuityImportResult
        {
            Source = SourceName,
            Path = DatabasePath ?? string.Empty,
            ImportedAt = DateTimeOffset.UtcNow
        };

        if (string.IsNullOrWhiteSpace(DatabasePath) || !File.Exists(DatabasePath))
        {
            result.Message = "VRCX-0 was not found at " + DefaultPath + ".";
            return result;
        }

        try
        {
            using var connection = OpenReadOnly(DatabasePath);
            if (!TableExists(connection, "owners") ||
                !TableExists(connection, "gamelog_location") ||
                !TableExists(connection, "gamelog_join_leave"))
            {
                result.Message = "The VRCX-0 database does not contain the expected game-log tables.";
                return result;
            }

            var owners = ReadOwners(connection);
            var locations = ReadLocations(connection);
            var events = ReadEvents(connection);
            var worlds = ReadWorlds(connection);
            var people = new Dictionary<string, ContinuityPerson>(StringComparer.Ordinal);

            progress?.Report("Reading VRCX-0 game logs…");

            foreach (var owner in owners)
            {
                var accountId = owner.Value;
                var ownerLocations = locations
                    .Where(row => row.OwnerId == owner.Key)
                    .OrderBy(row => row.CreatedAt)
                    .ToList();
                if (ownerLocations.Count == 0)
                {
                    continue;
                }

                var ownerEvents = events
                    .Where(row => row.OwnerId == owner.Key)
                    .OrderBy(row => row.CreatedAt)
                    .ToList();
                var ownerPrefix = TablePrefix(accountId);
                var accountPeople = ReadAccountPeople(connection, ownerPrefix);
                var friendRows = ReadFriendRows(connection, ownerPrefix);
                var selfName = string.Equals(accountId, currentAccountId, StringComparison.Ordinal)
                    ? currentAccountDisplayName
                    : null;

                for (var index = 0; index < ownerLocations.Count; index++)
                {
                    var location = ownerLocations[index];
                    var next = index + 1 < ownerLocations.Count
                        ? ownerLocations[index + 1]
                        : null;
                    var start = location.CreatedAt;
                    var end = next?.CreatedAt ?? start.AddSeconds(1);
                    if (end <= start)
                    {
                        end = start.AddSeconds(1);
                    }

                    // The last location row is not necessarily a live session:
                    // import only the time actually covered by observations.
                    if (next is null)
                    {
                        var lastEvent = ownerEvents
                            .Where(row => string.Equals(row.Location, location.Location, StringComparison.Ordinal) &&
                                          row.CreatedAt >= start)
                            .Select(row => row.CreatedAt)
                            .DefaultIfEmpty(start)
                            .Max();
                        end = lastEvent > start ? lastEvent : start.AddSeconds(1);
                    }

                    var sessionPeople = new Dictionary<string, ContinuitySessionPerson>(StringComparer.Ordinal);
                    AddPerson(
                        sessionPeople,
                        accountId,
                        selfName ?? accountPeople.GetValueOrDefault(accountId)?.DisplayName ?? accountId,
                        string.Empty,
                        start,
                        end,
                        accountPeople.GetValueOrDefault(accountId)?.AvatarUrl);

                    foreach (var observation in ownerEvents.Where(row =>
                                 string.Equals(row.Location, location.Location, StringComparison.Ordinal) &&
                                 row.CreatedAt >= start.AddSeconds(-2) &&
                                 row.CreatedAt <= end))
                    {
                        var key = PersonKey(observation.UserId, observation.DisplayName);
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            continue;
                        }

                        var joinedAt = observation.CreatedAt < start ? start : observation.CreatedAt;
                        if (joinedAt > end)
                        {
                            joinedAt = end;
                        }

                        if (string.Equals(observation.Type, "OnPlayerLeft", StringComparison.OrdinalIgnoreCase))
                        {
                            if (sessionPeople.TryGetValue(key, out var existing))
                            {
                                existing.LeftAt = joinedAt;
                                existing.DurationSeconds = Math.Max(1, (long)(joinedAt - existing.JoinedAt).TotalSeconds);
                            }
                            continue;
                        }

                        AddPerson(
                            sessionPeople,
                            observation.UserId,
                            observation.DisplayName,
                            accountPeople.GetValueOrDefault(observation.UserId)?.AvatarUrl,
                            joinedAt,
                            end,
                            accountPeople.GetValueOrDefault(observation.UserId)?.AvatarUrl);

                        if (people.TryGetValue(key, out var known))
                        {
                            if (!string.IsNullOrWhiteSpace(observation.DisplayName))
                            {
                                known.DisplayName = observation.DisplayName;
                            }
                        }
                    }

                    var sourceKey = string.Join(
                        "|",
                        owner.Key.ToString(CultureInfo.InvariantCulture),
                        location.Location,
                        start.UtcTicks.ToString(CultureInfo.InvariantCulture));
                    var session = new ContinuitySession
                    {
                        Id = ContinuitySession.MakeId(SourceName, sourceKey),
                        AccountUserId = accountId,
                        Source = SourceName,
                        SourceKey = sourceKey,
                        Location = location.Location,
                        WorldId = location.WorldId,
                        WorldName = location.WorldName,
                        JoinedAt = start,
                        LeftAt = end,
                        DurationSeconds = Math.Max(1, (long)(end - start).TotalSeconds),
                        PlayerCount = sessionPeople.Count,
                        People = sessionPeople.Values
                            .OrderBy(person => person.JoinedAt)
                            .ThenBy(person => person.DisplayName, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    };

                    result.Sessions++;
                    pendingSessions.Add(session);
                }

                foreach (var extra in accountPeople.Values)
                {
                    people[extra.PersonKey] = extra;
                }

                foreach (var friend in friendRows)
                {
                    var key = PersonKey(friend.UserId, friend.DisplayName);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    if (!people.TryGetValue(key, out var person))
                    {
                        person = new ContinuityPerson
                        {
                            PersonKey = key,
                            UserId = friend.UserId,
                            DisplayName = friend.DisplayName,
                            Source = SourceName
                        };
                        people[key] = person;
                    }

                    person.TrustLevel = friend.TrustLevel;
                    person.IsFriend = true;
                }
            }

            progress?.Report("Writing durable Continuity rows…");
            store.MergeSessions(pendingSessions);
            store.UpsertPersonMetadata(people.Values);
            store.UpsertWorldMetadata(worlds.Values);

            result.People = pendingSessions
                .SelectMany(session => session.People)
                .Select(person => person.PersonKey)
                .Concat(people.Keys)
                .Distinct(StringComparer.Ordinal)
                .Count();
            result.Worlds = pendingSessions
                .Select(session => session.WorldId)
                .Where(worldId => !string.IsNullOrWhiteSpace(worldId))
                .Distinct(StringComparer.Ordinal)
                .Count();
            result.Message = result.Sessions == 0
                ? "VRCX-0 was found, but it has no game-log sessions to import."
                : "Imported " + result.Sessions.ToString("N0", CultureInfo.InvariantCulture) +
                  " real session(s) from VRCX-0.";
            store.RecordImport(result);
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex;
            result.Message = "VRCX-0 import failed: " + ex.Message;
            return result;
        }
    }

    private readonly List<ContinuitySession> pendingSessions = new();

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

    private static Dictionary<long, string> ReadOwners(SqliteConnection connection)
    {
        var owners = new Dictionary<long, string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, user_id FROM owners";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            owners[reader.GetInt64(0)] = reader.GetString(1);
        }

        return owners;
    }

    private static List<LocationRow> ReadLocations(SqliteConnection connection)
    {
        var rows = new List<LocationRow>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner_id, created_at, location, world_id, world_name
            FROM gamelog_location
            ORDER BY created_at ASC, id ASC
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var createdAt = ParseTime(reader.GetString(1));
            if (createdAt is null)
            {
                continue;
            }

            rows.Add(new LocationRow(
                reader.GetInt64(0),
                createdAt.Value,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }

    private static List<EventRow> ReadEvents(SqliteConnection connection)
    {
        var rows = new List<EventRow>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner_id, created_at, type, display_name, location, user_id
            FROM gamelog_join_leave
            ORDER BY created_at ASC, id ASC
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var createdAt = ParseTime(reader.GetString(1));
            if (createdAt is null)
            {
                continue;
            }

            rows.Add(new EventRow(
                reader.GetInt64(0),
                createdAt.Value,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return rows;
    }

    private static Dictionary<string, ContinuityWorld> ReadWorlds(SqliteConnection connection)
    {
        var worlds = new Dictionary<string, ContinuityWorld>(StringComparer.Ordinal);
        if (!TableExists(connection, "cache_world"))
        {
            return worlds;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, author_name, thumbnail_image_url, image_url
            FROM cache_world
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            worlds[reader.GetString(0)] = new ContinuityWorld
            {
                WorldId = reader.GetString(0),
                Name = reader.GetString(1),
                AuthorName = reader.GetString(2),
                ThumbnailUrl = !reader.IsDBNull(3) && reader.GetString(3).Length > 0
                    ? reader.GetString(3)
                    : !reader.IsDBNull(4) ? reader.GetString(4) : string.Empty,
                Source = SourceName
            };
        }

        return worlds;
    }

    private static Dictionary<string, ContinuityPerson> ReadAccountPeople(
        SqliteConnection connection,
        string? prefix)
    {
        var people = new Dictionary<string, ContinuityPerson>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return people;
        }

        var avatarTable = prefix + "_feed_avatar";
        if (TableExists(connection, avatarTable))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT user_id, display_name, current_avatar_thumbnail_image_url,
                       current_avatar_image_url, created_at
                FROM "{avatarTable}"
                ORDER BY created_at ASC
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

                var avatar = !reader.IsDBNull(2) && reader.GetString(2).Length > 0
                    ? reader.GetString(2)
                    : !reader.IsDBNull(3) ? reader.GetString(3) : string.Empty;
                people[key] = new ContinuityPerson
                {
                    PersonKey = key,
                    UserId = userId,
                    DisplayName = displayName,
                    AvatarUrl = avatar ?? string.Empty,
                    Source = SourceName
                };
            }
        }

        var bioTable = prefix + "_profile_bio";
        if (TableExists(connection, bioTable))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT user_id, bio
                FROM "{bioTable}"
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var userId = reader.GetString(0);
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

                person.Bio = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
        }

        var notesTable = prefix + "_notes";
        if (TableExists(connection, notesTable))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT user_id, display_name, note
                FROM "{notesTable}"
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var userId = reader.GetString(0);
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
                        DisplayName = displayName,
                        Source = SourceName
                    };
                    people[key] = person;
                }

                person.ImportedNote = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            }
        }

        return people;
    }

    private static List<FriendRow> ReadFriendRows(SqliteConnection connection, string? prefix)
    {
        var rows = new List<FriendRow>();
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return rows;
        }

        var table = prefix + "_friend_log_current";
        if (!TableExists(connection, table))
        {
            return rows;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT user_id, display_name, trust_level
            FROM "{table}"
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new FriendRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
        }

        return rows;
    }

    private static void AddPerson(
        IDictionary<string, ContinuitySessionPerson> people,
        string? userId,
        string? displayName,
        string? avatarUrl,
        DateTimeOffset joinedAt,
        DateTimeOffset end,
        string? knownAvatarUrl = null)
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
                AvatarUrl = avatarUrl ?? knownAvatarUrl ?? string.Empty,
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
            else if (string.IsNullOrWhiteSpace(person.AvatarUrl) &&
                     !string.IsNullOrWhiteSpace(knownAvatarUrl))
            {
                person.AvatarUrl = knownAvatarUrl;
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

    private static string? TablePrefix(string userId)
    {
        var compact = userId.Replace("-", string.Empty, StringComparison.Ordinal);
        return compact.All(ch => char.IsLetterOrDigit(ch) || ch == '_')
            ? compact
            : null;
    }

    private static DateTimeOffset? ParseTime(string value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;

    private sealed record LocationRow(
        long OwnerId,
        DateTimeOffset CreatedAt,
        string Location,
        string WorldId,
        string WorldName);

    private sealed record EventRow(
        long OwnerId,
        DateTimeOffset CreatedAt,
        string Type,
        string DisplayName,
        string Location,
        string UserId);

    private sealed record FriendRow(
        string UserId,
        string DisplayName,
        string TrustLevel);
}
