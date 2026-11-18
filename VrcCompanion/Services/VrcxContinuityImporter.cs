using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Imports the original VRCX game-log database when it exists. VRCX stores a
/// single-account log in <c>%APPDATA%\VRCX\VRCX.sqlite3</c>; the active account
/// comes from this app because that schema has no owner column.
/// </summary>
public sealed class VrcxContinuityImporter
{
    public const string SourceName = "vrcx";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCX",
        "VRCX.sqlite3");

    public ContinuityImportResult Import(
        ContinuityStore store,
        string accountUserId,
        string accountDisplayName)
    {
        var result = new ContinuityImportResult
        {
            Source = SourceName,
            Path = DefaultPath,
            ImportedAt = DateTimeOffset.UtcNow
        };

        if (!File.Exists(DefaultPath))
        {
            result.Message = "VRCX was not found at " + DefaultPath + ".";
            return result;
        }

        if (string.IsNullOrWhiteSpace(accountUserId))
        {
            result.Message = "Sign in to VRChat before importing VRCX.";
            return result;
        }

        try
        {
            using var connection = OpenReadOnly(DefaultPath);
            if (!TableExists(connection, "gamelog_location") ||
                !TableExists(connection, "gamelog_join_leave"))
            {
                result.Message = "VRCX was found, but its game-log tables are not present.";
                return result;
            }

            var locations = ReadLocations(connection);
            var events = ReadEvents(connection);
            var worlds = ReadWorlds(connection);
            var sessions = new List<ContinuitySession>();
            var people = new Dictionary<string, ContinuityPerson>(StringComparer.Ordinal);
            var taken = store.GetSessions(accountUserId, null, 5000).ToList();

            for (var index = 0; index < locations.Count; index++)
            {
                var location = locations[index];
                var next = index + 1 < locations.Count ? locations[index + 1] : null;
                var start = location.CreatedAt;
                if (taken.Any(candidate =>
                        string.Equals(candidate.AccountUserId, accountUserId, StringComparison.Ordinal) &&
                        string.Equals(candidate.Location, location.Location, StringComparison.Ordinal) &&
                        Math.Abs((candidate.JoinedAt - start).TotalMinutes) <= 5))
                {
                    continue;
                }
                var end = next?.CreatedAt ?? start.AddSeconds(1);
                if (end <= start)
                {
                    end = start.AddSeconds(1);
                }

                if (next is null)
                {
                    var lastEvent = events
                        .Where(row => string.Equals(row.Location, location.Location, StringComparison.Ordinal) &&
                                      row.CreatedAt >= start)
                        .Select(row => row.CreatedAt)
                        .DefaultIfEmpty(start)
                        .Max();
                    end = lastEvent > start ? lastEvent : start.AddSeconds(1);
                }

                var sessionPeople = new Dictionary<string, ContinuitySessionPerson>(StringComparer.Ordinal);
                AddPerson(sessionPeople, accountUserId, accountDisplayName, start, end);

                foreach (var observation in events.Where(row =>
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
                        joinedAt,
                        end,
                        observation.AvatarUrl);

                    if (!people.ContainsKey(key))
                    {
                        people[key] = new ContinuityPerson
                        {
                            PersonKey = key,
                            UserId = observation.UserId,
                            DisplayName = observation.DisplayName,
                            Source = SourceName
                        };
                    }
                }

                if (!sessionPeople.ContainsKey(accountUserId))
                {
                    AddPerson(sessionPeople, accountUserId, accountDisplayName, start, end);
                }

                var sourceKey = string.Join(
                    "|",
                    accountUserId,
                    location.Location,
                    start.UtcTicks.ToString(CultureInfo.InvariantCulture));
                var session = new ContinuitySession
                {
                    Id = ContinuitySession.MakeId(SourceName, sourceKey),
                    AccountUserId = accountUserId,
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
                sessions.Add(session);
                taken.Add(session);
            }

            store.MergeSessions(sessions);
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
            result.Message = result.Sessions == 0
                ? "VRCX was found, but it has no game-log sessions to import."
                : "Imported " + result.Sessions.ToString("N0", CultureInfo.InvariantCulture) +
                  " real session(s) from VRCX.";
            store.RecordImport(result);
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex;
            result.Message = "VRCX import failed: " + ex.Message;
            return result;
        }
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

    private static List<LocationRow> ReadLocations(SqliteConnection connection)
    {
        var rows = new List<LocationRow>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT created_at, location, world_id, world_name
            FROM gamelog_location
            ORDER BY created_at ASC, id ASC
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var createdAt = ParseTime(reader.GetString(0));
            if (createdAt is null)
            {
                continue;
            }

            rows.Add(new LocationRow(
                createdAt.Value,
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
        }

        return rows;
    }

    private static List<EventRow> ReadEvents(SqliteConnection connection)
    {
        var rows = new List<EventRow>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT created_at, type, display_name, location, user_id
            FROM gamelog_join_leave
            ORDER BY created_at ASC, id ASC
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var createdAt = ParseTime(reader.GetString(0));
            if (createdAt is null)
            {
                continue;
            }

            rows.Add(new EventRow(
                createdAt.Value,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                string.Empty));
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

    private static DateTimeOffset? ParseTime(string value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;

    private sealed record LocationRow(
        DateTimeOffset CreatedAt,
        string Location,
        string WorldId,
        string WorldName);

    private sealed record EventRow(
        DateTimeOffset CreatedAt,
        string Type,
        string DisplayName,
        string Location,
        string UserId,
        string AvatarUrl);
}
