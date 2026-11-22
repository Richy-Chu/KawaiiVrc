using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Durable local store for the Continuity feature. It is deliberately separate
/// from VRChat and from the source applications: imports are read-only, the
/// normalised rows live in this database, and notes written here survive every
/// restart. No sample rows are inserted.
/// </summary>
public sealed class ContinuityStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private bool _initialized;

    public ContinuityStore()
        : this(DefaultPath)
    {
    }

    internal ContinuityStore(string path)
    {
        _path = path;
    }

    public string FilePath => _path;

    public static string DefaultPath => UserDataPaths.Combine("continuity.db");

    /// <summary>Reads all rows from a source and merges them without duplicates.</summary>
    public int MergeSessions(IEnumerable<ContinuitySession> sessions)
    {
        var rows = sessions
            .Where(session => !string.IsNullOrWhiteSpace(session.Id) &&
                              !string.IsNullOrWhiteSpace(session.AccountUserId) &&
                              !string.IsNullOrWhiteSpace(session.SourceKey))
            .ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var session in rows)
            {
                UpsertWorld(connection, transaction, new ContinuityWorld
                {
                    WorldId = session.WorldId ?? string.Empty,
                    Name = session.WorldName ?? string.Empty,
                    Source = session.Source ?? string.Empty
                });

                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO sessions (
                            id, account_user_id, source, source_key, location, world_id,
                            world_name, joined_at, left_at, duration_seconds, player_count)
                        VALUES (
                            $id, $account, $source, $sourceKey, $location, $worldId,
                            $worldName, $joinedAt, $leftAt, $duration, $players)
                        ON CONFLICT(source, source_key) DO UPDATE SET
                            account_user_id = excluded.account_user_id,
                            location = excluded.location,
                            world_id = CASE WHEN excluded.world_id <> '' THEN excluded.world_id ELSE sessions.world_id END,
                            world_name = CASE WHEN excluded.world_name <> '' THEN excluded.world_name ELSE sessions.world_name END,
                            joined_at = excluded.joined_at,
                            left_at = excluded.left_at,
                            duration_seconds = excluded.duration_seconds,
                            player_count = excluded.player_count
                        """;
                    command.Parameters.AddWithValue("$id", session.Id);
                    command.Parameters.AddWithValue("$account", session.AccountUserId);
                    command.Parameters.AddWithValue("$source", session.Source);
                    command.Parameters.AddWithValue("$sourceKey", session.SourceKey);
                    command.Parameters.AddWithValue("$location", session.Location ?? string.Empty);
                    command.Parameters.AddWithValue("$worldId", session.WorldId ?? string.Empty);
                    command.Parameters.AddWithValue("$worldName", session.WorldName ?? string.Empty);
                    command.Parameters.AddWithValue("$joinedAt", ToStorage(session.JoinedAt));
                    command.Parameters.AddWithValue("$leftAt", session.LeftAt is null
                        ? DBNull.Value
                        : ToStorage(session.LeftAt.Value));
                    command.Parameters.AddWithValue("$duration", Math.Max(0, session.DurationSeconds));
                    command.Parameters.AddWithValue("$players", Math.Max(0, session.PlayerCount));
                    command.ExecuteNonQuery();
                }

                using (var clear = connection.CreateCommand())
                {
                    clear.Transaction = transaction;
                    clear.CommandText = "DELETE FROM session_people WHERE session_id = $id";
                    clear.Parameters.AddWithValue("$id", session.Id);
                    clear.ExecuteNonQuery();
                }

                foreach (var person in session.People)
                {
                    if (string.IsNullOrWhiteSpace(person.PersonKey))
                    {
                        continue;
                    }

                    UpsertPerson(connection, transaction, new ContinuityPerson
                    {
                        PersonKey = person.PersonKey ?? string.Empty,
                        UserId = person.UserId ?? string.Empty,
                        DisplayName = person.DisplayName ?? string.Empty,
                        AvatarUrl = person.AvatarUrl ?? string.Empty,
                        Source = session.Source ?? string.Empty
                    });

                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT OR REPLACE INTO session_people (
                            session_id, person_key, user_id, display_name, avatar_url,
                            joined_at, left_at, duration_seconds)
                        VALUES (
                            $session, $key, $userId, $displayName, $avatar,
                            $joinedAt, $leftAt, $duration)
                        """;
                    insert.Parameters.AddWithValue("$session", session.Id);
                    insert.Parameters.AddWithValue("$key", person.PersonKey);
                    insert.Parameters.AddWithValue("$userId", person.UserId ?? string.Empty);
                    insert.Parameters.AddWithValue("$displayName", person.DisplayName ?? string.Empty);
                    insert.Parameters.AddWithValue("$avatar", person.AvatarUrl ?? string.Empty);
                    insert.Parameters.AddWithValue("$joinedAt", ToStorage(person.JoinedAt));
                    insert.Parameters.AddWithValue("$leftAt", person.LeftAt is null
                        ? DBNull.Value
                        : ToStorage(person.LeftAt.Value));
                    insert.Parameters.AddWithValue("$duration", Math.Max(0, person.DurationSeconds));
                    insert.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return rows.Count;
        }
    }

    public void UpsertPersonMetadata(IEnumerable<ContinuityPerson> people)
    {
        var rows = people
            .Where(person => !string.IsNullOrWhiteSpace(person.PersonKey))
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var person in rows)
            {
                UpsertPerson(connection, transaction, person);
            }

            transaction.Commit();
        }
    }

    public void UpsertWorldMetadata(IEnumerable<ContinuityWorld> worlds)
    {
        var rows = worlds
            .Where(world => !string.IsNullOrWhiteSpace(world.WorldId))
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var world in rows)
            {
                UpsertWorld(connection, transaction, world);

                // Keep the timeline in step with the world card. Existing
                // source rows often have a real world id but an empty name.
                if (!string.IsNullOrWhiteSpace(world.Name))
                {
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE sessions
                        SET world_name = $name
                        WHERE world_id = $id
                          AND (world_name = '' OR world_name IS NULL)
                        """;
                    update.Parameters.AddWithValue("$name", world.Name);
                    update.Parameters.AddWithValue("$id", world.WorldId);
                    update.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Fills session names from world metadata already imported from another
    /// source. This is a local-only repair; it never invents a name or calls
    /// VRChat.
    /// </summary>
    public int BackfillSessionWorldNames()
    {
        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE sessions
                SET world_name = (
                    SELECT w.name FROM worlds w
                    WHERE w.world_id = sessions.world_id
                      AND w.name <> ''
                )
                WHERE (world_name = '' OR world_name IS NULL)
                  AND EXISTS (
                      SELECT 1 FROM worlds w
                      WHERE w.world_id = sessions.world_id
                        AND w.name <> ''
                  )
                """;
            return command.ExecuteNonQuery();
        }
    }

    public void SaveNote(string personKey, string? note)
    {
        if (string.IsNullOrWhiteSpace(personKey))
        {
            return;
        }

        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO people (person_key, local_note)
                VALUES ($key, $note)
                ON CONFLICT(person_key) DO UPDATE SET local_note = excluded.local_note
                """;
            command.Parameters.AddWithValue("$key", personKey);
            command.Parameters.AddWithValue("$note", note?.Trim() ?? string.Empty);
            command.ExecuteNonQuery();
        }
    }

    public ContinuitySummary GetSummary(string accountUserId)
    {
        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    COUNT(*),
                    COALESCE(SUM(duration_seconds), 0),
                    MIN(joined_at),
                    MAX(COALESCE(left_at, joined_at)),
                    COUNT(DISTINCT world_id)
                FROM sessions
                WHERE ($account = '' OR account_user_id = $account)
                """;
            command.Parameters.AddWithValue("$account", accountUserId ?? string.Empty);
            using var reader = command.ExecuteReader();

            var summary = new ContinuitySummary();
            if (reader.Read())
            {
                summary.SessionCount = reader.GetInt32(0);
                summary.TotalSeconds = reader.GetInt64(1);
                summary.FirstSeenAt = reader.IsDBNull(2) ? null : ParseStorage(reader.GetString(2));
                summary.LastSeenAt = reader.IsDBNull(3) ? null : ParseStorage(reader.GetString(3));
                summary.WorldCount = reader.GetInt32(4);
            }

            reader.Close();

            using var peopleCommand = connection.CreateCommand();
            peopleCommand.CommandText = """
                SELECT COUNT(DISTINCT sp.person_key)
                FROM session_people sp
                JOIN sessions s ON s.id = sp.session_id
                WHERE ($account = '' OR s.account_user_id = $account)
                """;
            peopleCommand.Parameters.AddWithValue("$account", accountUserId ?? string.Empty);
            summary.PeopleCount = Convert.ToInt32(peopleCommand.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
            return summary;
        }
    }

    public IReadOnlyList<ContinuitySession> GetSessions(
        string accountUserId,
        string? search = null,
        int limit = 500)
    {
        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.id, s.account_user_id, s.source, s.source_key, s.location, s.world_id,
                       COALESCE(NULLIF(s.world_name, ''), w.name, '') AS display_world_name,
                       s.joined_at, s.left_at, s.duration_seconds, s.player_count,
                       COALESCE(w.thumbnail_url, '') AS thumbnail_url,
                       COALESCE(w.author_name, '') AS author_name
                FROM sessions s
                LEFT JOIN worlds w ON w.world_id = s.world_id
                WHERE ($account = '' OR s.account_user_id = $account)
                  AND ($search = '' OR s.world_name LIKE $like OR s.world_id LIKE $like OR s.location LIKE $like)
                ORDER BY s.joined_at DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$account", accountUserId ?? string.Empty);
            command.Parameters.AddWithValue("$search", search?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$like", "%" + (search?.Trim() ?? string.Empty) + "%");
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));

            var rows = new List<ContinuitySession>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(ReadSession(reader));
            }

            return rows;
        }
    }

    public IReadOnlyList<ContinuityPerson> GetPeople(
        string accountUserId,
        string? search = null,
        int limit = 500)
    {
        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p.person_key, p.user_id, p.display_name, p.avatar_url, p.bio,
                       p.local_note, p.imported_note, p.trust_level, p.is_friend,
                       MIN(s.joined_at) AS first_seen,
                       MAX(COALESCE(s.left_at, s.joined_at)) AS last_seen,
                       COALESCE(SUM(sp.duration_seconds), 0) AS total_seconds,
                       COUNT(DISTINCT s.id) AS session_count
                FROM people p
                JOIN session_people sp ON sp.person_key = p.person_key
                JOIN sessions s ON s.id = sp.session_id
                WHERE ($account = '' OR s.account_user_id = $account)
                  AND ($search = '' OR p.display_name LIKE $like OR p.user_id LIKE $like)
                GROUP BY p.person_key
                ORDER BY last_seen DESC, p.display_name COLLATE NOCASE
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$account", accountUserId ?? string.Empty);
            command.Parameters.AddWithValue("$search", search?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$like", "%" + (search?.Trim() ?? string.Empty) + "%");
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));

            var rows = new List<ContinuityPerson>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var person = ReadPerson(reader);
                person.FirstSeenAt = reader.IsDBNull(9) ? null : ParseStorage(reader.GetString(9));
                person.LastSeenAt = reader.IsDBNull(10) ? null : ParseStorage(reader.GetString(10));
                person.TotalSeconds = reader.GetInt64(11);
                person.SessionCount = reader.GetInt32(12);
                rows.Add(person);
            }

            return rows;
        }
    }

    public ContinuityPerson? GetPerson(string personKey, string accountUserId)
    {
        if (string.IsNullOrWhiteSpace(personKey))
        {
            return null;
        }

        return GetPeople(accountUserId, null, 5000)
            .FirstOrDefault(person => string.Equals(person.PersonKey, personKey, StringComparison.Ordinal));
    }

    public IReadOnlyList<ContinuitySession> GetPersonSessions(
        string accountUserId,
        string personKey,
        int limit = 250)
    {
        if (string.IsNullOrWhiteSpace(personKey))
        {
            return Array.Empty<ContinuitySession>();
        }

        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.id, s.account_user_id, s.source, s.source_key, s.location,
                       s.world_id,
                       COALESCE(NULLIF(s.world_name, ''), w.name, '') AS display_world_name,
                       s.joined_at, s.left_at, s.duration_seconds, s.player_count,
                       COALESCE(w.thumbnail_url, '') AS thumbnail_url,
                       COALESCE(w.author_name, '') AS author_name
                FROM sessions s
                JOIN session_people sp ON sp.session_id = s.id
                LEFT JOIN worlds w ON w.world_id = s.world_id
                WHERE ($account = '' OR s.account_user_id = $account)
                  AND sp.person_key = $key
                ORDER BY s.joined_at DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$account", accountUserId ?? string.Empty);
            command.Parameters.AddWithValue("$key", personKey);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));

            var rows = new List<ContinuitySession>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(ReadSession(reader));
            }

            return rows;
        }
    }

    public IReadOnlyList<ContinuitySessionPerson> GetSessionPeople(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<ContinuitySessionPerson>();
        }

        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT person_key, user_id, display_name, avatar_url,
                       joined_at, left_at, duration_seconds
                FROM session_people
                WHERE session_id = $session
                ORDER BY joined_at ASC, display_name COLLATE NOCASE
                """;
            command.Parameters.AddWithValue("$session", sessionId);

            var rows = new List<ContinuitySessionPerson>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ContinuitySessionPerson
                {
                    PersonKey = reader.GetString(0),
                    UserId = reader.GetString(1),
                    DisplayName = reader.GetString(2),
                    AvatarUrl = reader.GetString(3),
                    JoinedAt = ParseStorage(reader.GetString(4)),
                    LeftAt = reader.IsDBNull(5) ? null : ParseStorage(reader.GetString(5)),
                    DurationSeconds = reader.GetInt64(6)
                });
            }

            return rows;
        }
    }

    public IReadOnlyList<ContinuityWorld> GetWorlds(
        string accountUserId,
        string? search = null,
        int limit = 500)
    {
        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT w.world_id, w.name, w.author_name, w.thumbnail_url,
                       MIN(s.joined_at) AS first_seen,
                       MAX(COALESCE(s.left_at, s.joined_at)) AS last_seen,
                       COALESCE(SUM(s.duration_seconds), 0) AS total_seconds,
                       COUNT(s.id) AS visit_count
                FROM worlds w
                JOIN sessions s ON s.world_id = w.world_id
                WHERE ($account = '' OR s.account_user_id = $account)
                  AND ($search = '' OR w.name LIKE $like OR w.world_id LIKE $like)
                GROUP BY w.world_id
                ORDER BY total_seconds DESC, last_seen DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$account", accountUserId ?? string.Empty);
            command.Parameters.AddWithValue("$search", search?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$like", "%" + (search?.Trim() ?? string.Empty) + "%");
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));

            var rows = new List<ContinuityWorld>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var world = new ContinuityWorld
                {
                    WorldId = reader.GetString(0),
                    Name = reader.GetString(1),
                    AuthorName = reader.GetString(2),
                    ThumbnailUrl = reader.GetString(3),
                    FirstSeenAt = reader.IsDBNull(4) ? null : ParseStorage(reader.GetString(4)),
                    LastSeenAt = reader.IsDBNull(5) ? null : ParseStorage(reader.GetString(5)),
                    TotalSeconds = reader.GetInt64(6),
                    VisitCount = reader.GetInt32(7)
                };
                rows.Add(world);
            }

            return rows;
        }
    }

    public IReadOnlyList<ContinuitySession> GetWorldSessions(
        string accountUserId,
        string worldId,
        int limit = 250)
    {
        if (string.IsNullOrWhiteSpace(worldId))
        {
            return Array.Empty<ContinuitySession>();
        }

        return GetSessions(accountUserId, worldId, limit)
            .Where(session => string.Equals(session.WorldId, worldId, StringComparison.Ordinal))
            .ToList();
    }

    public void RecordImport(ContinuityImportResult result)
    {
        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO imports (
                    source, path, imported_at, sessions, people, worlds, message)
                VALUES (
                    $source, $path, $importedAt, $sessions, $people, $worlds, $message)
                ON CONFLICT(source) DO UPDATE SET
                    path = excluded.path,
                    imported_at = excluded.imported_at,
                    sessions = excluded.sessions,
                    people = excluded.people,
                    worlds = excluded.worlds,
                    message = excluded.message
                """;
            command.Parameters.AddWithValue("$source", result.Source);
            command.Parameters.AddWithValue("$path", result.Path ?? string.Empty);
            command.Parameters.AddWithValue("$importedAt", ToStorage(result.ImportedAt));
            command.Parameters.AddWithValue("$sessions", result.Sessions);
            command.Parameters.AddWithValue("$people", result.People);
            command.Parameters.AddWithValue("$worlds", result.Worlds);
            command.Parameters.AddWithValue("$message", result.Message ?? string.Empty);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ContinuityImportSource> GetImports()
    {
        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT source, path, imported_at, sessions, people, worlds, message
                FROM imports
                ORDER BY imported_at DESC
                """;
            var rows = new List<ContinuityImportSource>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ContinuityImportSource
                {
                    Source = reader.GetString(0),
                    Label = reader.GetString(0),
                    Path = reader.GetString(1),
                    IsAvailable = true,
                    ImportedAt = reader.IsDBNull(2) ? null : ParseStorage(reader.GetString(2)),
                    Sessions = reader.GetInt32(3),
                    People = reader.GetInt32(4),
                    Worlds = reader.GetInt32(5),
                    Message = reader.GetString(6)
                });
            }

            return rows;
        }
    }

    public int DeleteAll()
    {
        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var table in new[] { "session_people", "sessions", "people", "worlds", "imports" })
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {table}";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return 0;
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS people (
                person_key TEXT PRIMARY KEY,
                user_id TEXT NOT NULL DEFAULT '',
                display_name TEXT NOT NULL DEFAULT '',
                avatar_url TEXT NOT NULL DEFAULT '',
                bio TEXT NOT NULL DEFAULT '',
                local_note TEXT NOT NULL DEFAULT '',
                imported_note TEXT NOT NULL DEFAULT '',
                trust_level TEXT NOT NULL DEFAULT '',
                is_friend INTEGER NOT NULL DEFAULT 0,
                source TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS worlds (
                world_id TEXT PRIMARY KEY,
                name TEXT NOT NULL DEFAULT '',
                author_name TEXT NOT NULL DEFAULT '',
                thumbnail_url TEXT NOT NULL DEFAULT '',
                source TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                account_user_id TEXT NOT NULL,
                source TEXT NOT NULL,
                source_key TEXT NOT NULL,
                location TEXT NOT NULL,
                world_id TEXT NOT NULL DEFAULT '',
                world_name TEXT NOT NULL DEFAULT '',
                joined_at TEXT NOT NULL,
                left_at TEXT,
                duration_seconds INTEGER NOT NULL DEFAULT 0,
                player_count INTEGER NOT NULL DEFAULT 0,
                UNIQUE(source, source_key)
            );

            CREATE TABLE IF NOT EXISTS session_people (
                session_id TEXT NOT NULL,
                person_key TEXT NOT NULL,
                user_id TEXT NOT NULL DEFAULT '',
                display_name TEXT NOT NULL DEFAULT '',
                avatar_url TEXT NOT NULL DEFAULT '',
                joined_at TEXT NOT NULL,
                left_at TEXT,
                duration_seconds INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(session_id, person_key),
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS imports (
                source TEXT PRIMARY KEY,
                path TEXT NOT NULL DEFAULT '',
                imported_at TEXT NOT NULL,
                sessions INTEGER NOT NULL DEFAULT 0,
                people INTEGER NOT NULL DEFAULT 0,
                worlds INTEGER NOT NULL DEFAULT 0,
                message TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS ix_sessions_account_time
                ON sessions(account_user_id, joined_at DESC);
            CREATE INDEX IF NOT EXISTS ix_sessions_world_time
                ON sessions(world_id, joined_at DESC);
            CREATE INDEX IF NOT EXISTS ix_session_people_person
                ON session_people(person_key, session_id);
            """;
        command.ExecuteNonQuery();
        _initialized = true;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void UpsertPerson(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ContinuityPerson person)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO people (
                person_key, user_id, display_name, avatar_url, bio,
                imported_note, trust_level, is_friend, source)
            VALUES (
                $key, $userId, $displayName, $avatar, $bio,
                $importedNote, $trust, $friend, $source)
            ON CONFLICT(person_key) DO UPDATE SET
                user_id = CASE WHEN excluded.user_id <> '' THEN excluded.user_id ELSE people.user_id END,
                display_name = CASE WHEN excluded.display_name <> '' THEN excluded.display_name ELSE people.display_name END,
                avatar_url = CASE WHEN excluded.avatar_url <> '' THEN excluded.avatar_url ELSE people.avatar_url END,
                bio = CASE WHEN excluded.bio <> '' THEN excluded.bio ELSE people.bio END,
                imported_note = CASE WHEN excluded.imported_note <> '' THEN excluded.imported_note ELSE people.imported_note END,
                trust_level = CASE WHEN excluded.trust_level <> '' THEN excluded.trust_level ELSE people.trust_level END,
                is_friend = CASE WHEN excluded.is_friend <> 0 THEN 1 ELSE people.is_friend END,
                source = CASE WHEN excluded.source <> '' THEN excluded.source ELSE people.source END
            """;
        command.Parameters.AddWithValue("$key", person.PersonKey);
        command.Parameters.AddWithValue("$userId", person.UserId ?? string.Empty);
        command.Parameters.AddWithValue("$displayName", person.DisplayName ?? string.Empty);
        command.Parameters.AddWithValue("$avatar", person.AvatarUrl ?? string.Empty);
        command.Parameters.AddWithValue("$bio", person.Bio ?? string.Empty);
        command.Parameters.AddWithValue("$importedNote", person.ImportedNote ?? string.Empty);
        command.Parameters.AddWithValue("$trust", person.TrustLevel ?? string.Empty);
        command.Parameters.AddWithValue("$friend", person.IsFriend ? 1 : 0);
        command.Parameters.AddWithValue("$source", person.Source ?? string.Empty);
        command.ExecuteNonQuery();
    }

    private static void UpsertWorld(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ContinuityWorld world)
    {
        if (string.IsNullOrWhiteSpace(world.WorldId))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO worlds (world_id, name, author_name, thumbnail_url, source)
            VALUES ($id, $name, $author, $thumbnail, $source)
            ON CONFLICT(world_id) DO UPDATE SET
                name = CASE WHEN excluded.name <> '' THEN excluded.name ELSE worlds.name END,
                author_name = CASE WHEN excluded.author_name <> '' THEN excluded.author_name ELSE worlds.author_name END,
                thumbnail_url = CASE WHEN excluded.thumbnail_url <> '' THEN excluded.thumbnail_url ELSE worlds.thumbnail_url END,
                source = CASE WHEN excluded.source <> '' THEN excluded.source ELSE worlds.source END
            """;
        command.Parameters.AddWithValue("$id", world.WorldId);
        command.Parameters.AddWithValue("$name", world.Name ?? string.Empty);
        command.Parameters.AddWithValue("$author", world.AuthorName ?? string.Empty);
        command.Parameters.AddWithValue("$thumbnail", world.ThumbnailUrl ?? string.Empty);
        command.Parameters.AddWithValue("$source", world.Source ?? string.Empty);
        command.ExecuteNonQuery();
    }

    private static ContinuitySession ReadSession(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0) ?? string.Empty,
        AccountUserId = reader.GetString(1) ?? string.Empty,
        Source = reader.GetString(2) ?? string.Empty,
        SourceKey = reader.GetString(3) ?? string.Empty,
        Location = reader.GetString(4) ?? string.Empty,
        WorldId = reader.GetString(5) ?? string.Empty,
        WorldName = reader.GetString(6) ?? string.Empty,
        JoinedAt = ParseStorage(reader.GetString(7)),
        LeftAt = reader.IsDBNull(8) ? null : ParseStorage(reader.GetString(8)),
        DurationSeconds = reader.GetInt64(9),
        PlayerCount = reader.GetInt32(10),
        WorldThumbnailUrl = reader.GetString(11) ?? string.Empty,
        WorldAuthorName = reader.GetString(12) ?? string.Empty
    };

    private static ContinuityPerson ReadPerson(SqliteDataReader reader) => new()
    {
        PersonKey = reader.GetString(0) ?? string.Empty,
        UserId = reader.GetString(1) ?? string.Empty,
        DisplayName = reader.GetString(2) ?? string.Empty,
        AvatarUrl = reader.GetString(3) ?? string.Empty,
        Bio = reader.GetString(4) ?? string.Empty,
        LocalNote = reader.GetString(5) ?? string.Empty,
        ImportedNote = reader.GetString(6) ?? string.Empty,
        TrustLevel = reader.GetString(7) ?? string.Empty,
        IsFriend = reader.GetInt32(8) != 0
    };

    private static string ToStorage(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStorage(string value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
