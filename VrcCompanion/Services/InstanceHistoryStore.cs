using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Local, per-account instance history backed by SQLite. The source of truth is
/// the same shape VRCX-0 uses: world location metadata plus individual
/// OnPlayerJoined/OnPlayerLeft events. Visits are grouped from those events on
/// read, so player history survives restarts and can later be queried for
/// friends without changing the storage format.
///
/// This is an independent C# implementation of that model, not copied GPL
/// source. VRChat has no history endpoint; rows only exist after the signed-in
/// account actually entered an instance.
/// </summary>
public sealed class InstanceHistoryStore
{
    private const int MaximumEntries = 500;
    private const long GroupingToleranceMilliseconds = 60 * 60 * 1000;
    private const string JoinedEvent = "OnPlayerJoined";
    private const string LeftEvent = "OnPlayerLeft";

    private readonly object _gate = new();
    private readonly string _databasePath;
    private bool _initialized;
    private bool _isDefaultDatabase;

    public InstanceHistoryStore()
        : this(DefaultPath)
    {
    }

    internal InstanceHistoryStore(string path)
    {
        _databasePath = path;
        FilePath = path;
        _isDefaultDatabase = string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(DefaultPath),
            StringComparison.OrdinalIgnoreCase);
    }

    public string FilePath { get; }

    private long _revision;

    /// <summary>
    /// Moves whenever stored history changes. The history screens rebuild every
    /// row from this, so a poll that finds nothing new can skip the rebuild.
    /// </summary>
    public long Revision => Interlocked.Read(ref _revision);

    private void BumpRevision() => Interlocked.Increment(ref _revision);

    /// <summary>
    /// VRCX-0 style SQLite database. It lives beside the old JSON file so the
    /// first launch can migrate that file without deleting it.
    /// </summary>
    public static string DefaultPath => UserDataPaths.Combine("instance-history.db");

    private static string LegacyJsonPath => UserDataPaths.Combine("instance-history.json");

    public void Load()
    {
        lock (_gate)
        {
            EnsureInitialized();
            if (!_isDefaultDatabase || CountEvents() > 0 || !File.Exists(LegacyJsonPath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(LegacyJsonPath);
                var legacy = JsonSerializer.Deserialize<List<InstanceHistoryEntry>>(json);
                if (legacy is null)
                {
                    return;
                }

                var migrated = Merge(legacy);

                // Keep the old file for rollback/debugging; the SQLite database
                // becomes the source of truth from this point on.
                Debug.WriteLine($"Migrated {migrated} instance-history rows from JSON to SQLite.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"instance history migration: {ex}");
            }
        }
    }

    public IReadOnlyList<InstanceHistoryEntry> ForAccount(string accountUserId)
    {
        if (string.IsNullOrWhiteSpace(accountUserId))
        {
            return Array.Empty<InstanceHistoryEntry>();
        }

        lock (_gate)
        {
            EnsureInitialized();
            // Grouping 27k join/leave events takes ~460 ms. Every screen that
            // shows sessions asks for the same rows within seconds of the last
            // write, so keep the last result until the database files change.
            var signature = DatabaseSignature();
            if (_cachedEntries is not null &&
                string.Equals(_cachedAccount, accountUserId, StringComparison.Ordinal) &&
                string.Equals(_cachedSignature, signature, StringComparison.Ordinal))
            {
                return new List<InstanceHistoryEntry>(_cachedEntries);
            }

            using var connection = OpenConnection();
            var entries = BuildEntries(connection, accountUserId);
            _cachedEntries = entries;
            _cachedAccount = accountUserId;
            _cachedSignature = signature;
            return new List<InstanceHistoryEntry>(entries);
        }
    }

    private IReadOnlyList<InstanceHistoryEntry>? _cachedEntries;
    private string? _cachedAccount;
    private string? _cachedSignature;

    /// <summary>
    /// Parsed event rows, keyed by account. The live log appends a few events at
    /// a time and every screen re-reads history after each write, so keeping the
    /// rows turns a 27k-row re-read into an append of just the new rows. The row
    /// count and highest id prove the cache still matches the table; a delete or
    /// an out-of-order import falls back to a full read.
    /// </summary>
    private readonly Dictionary<string, EventRowCache> _eventCaches = new(StringComparer.Ordinal);
    private const int MaximumCachedEventAccounts = 2;

    /// <summary>
    /// The stored world metadata, read once per refresh and shared with every
    /// session at that location. Reading it was the largest allocation left once
    /// the grouping was cached, and the rows change only through this store's
    /// writes, so a version bumped by those plus a cheap (count, last-updated)
    /// check keep the cached dictionary honest.
    /// </summary>
    private readonly Dictionary<string, LocationCache> _locationCaches = new(StringComparer.Ordinal);
    private long _locationVersion;

    /// <summary>
    /// Drops parsed rows after a write that edits them in place. Appends do not
    /// need this: the row count and highest id tell ReadEvents what is new.
    /// </summary>
    private void InvalidateEventRows() => _eventCaches.Clear();

    private string DatabaseSignature()
    {
        try
        {
            var info = new FileInfo(_databasePath);
            var length = info.Exists ? info.Length : 0;
            var written = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
            var wal = new FileInfo(_databasePath + "-wal");
            var walLength = wal.Exists ? wal.Length : 0;
            var walWritten = wal.Exists ? wal.LastWriteTimeUtc.Ticks : 0;
            return $"{length}:{written}:{walLength}:{walWritten}";
        }
        catch
        {
            return string.Empty;
        }
    }

    public InstanceHistoryEntry? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            return BuildEntries(connection, accountUserId: null)
                .FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>Drops the read cache after a write from this instance.</summary>
    private void InvalidateCache()
    {
        _cachedEntries = null;
        _cachedAccount = null;
        _cachedSignature = null;
    }

    public InstanceHistoryEntry? FindOpen(string accountUserId, string location)
    {
        if (string.IsNullOrWhiteSpace(accountUserId) || string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            return BuildEntries(connection, accountUserId)
                .Where(entry =>
                    string.Equals(entry.Location, location, StringComparison.Ordinal) &&
                    entry.LeftAt is null)
                .OrderByDescending(entry => entry.JoinedAt)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Inserts the real visits represented by an imported/live entry. The same
    /// visit can be added repeatedly; the event unique index makes it idempotent.
    /// </summary>
    public InstanceHistoryEntry Add(InstanceHistoryEntry entry)
    {
        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            UpsertLocation(connection, transaction, entry);
            WriteEntryEvents(connection, transaction, entry);
            transaction.Commit();
            _locationVersion++;
            BumpRevision();
            InvalidateCache();

            return BuildEntries(connection, entry.AccountUserId)
                       .FirstOrDefault(candidate => string.Equals(candidate.Id, entry.Id, StringComparison.Ordinal))
                   ?? entry;
        }
    }

    /// <summary>Adds imported rows without overwriting live observations.</summary>
    public int Merge(IEnumerable<InstanceHistoryEntry> entries)
    {
        var usable = entries.Where(IsUsable).ToList();
        if (usable.Count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var entry in usable)
            {
                UpsertLocation(connection, transaction, entry);
                WriteEntryEvents(connection, transaction, entry);
            }

                            transaction.Commit();
            _locationVersion++;
            BumpRevision();
            InvalidateCache();
            return usable.Count;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            EnsureInitialized();
            InvalidateCache();
            InvalidateEventRows();
            using var connection = OpenConnection();
            var entry = BuildEntries(connection, accountUserId: null)
                .FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (entry is null)
            {
                return false;
            }

            var end = entry.LeftAt ?? DateTimeOffset.Now;
            using var transaction = connection.BeginTransaction();
            if (entry.EventIds.Count > 0)
            {
                DeleteEventRows(connection, transaction, entry.EventIds);
            }
            else
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM gamelog_join_leave
                    WHERE account_user_id = $account
                      AND location = $location
                      AND created_at_ts >= $start
                      AND created_at_ts <= $end
                    """;
                command.Parameters.AddWithValue("$account", entry.AccountUserId);
                command.Parameters.AddWithValue("$location", entry.Location);
                command.Parameters.AddWithValue("$start", entry.JoinedAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$end", end.ToUnixTimeMilliseconds());
                command.ExecuteNonQuery();
            }

            using (var cleanup = connection.CreateCommand())
            {
                cleanup.Transaction = transaction;
                cleanup.CommandText = """
                    DELETE FROM gamelog_location
                    WHERE account_user_id = $account
                      AND location = $location
                      AND NOT EXISTS (
                          SELECT 1 FROM gamelog_join_leave e
                          WHERE e.account_user_id = gamelog_location.account_user_id
                            AND e.location = gamelog_location.location)
                    """;
                cleanup.Parameters.AddWithValue("$account", entry.AccountUserId);
                cleanup.Parameters.AddWithValue("$location", entry.Location);
                cleanup.ExecuteNonQuery();
            }

            transaction.Commit();
            _locationVersion++;
            BumpRevision();
            return true;
        }
    }

    /// <summary>
    /// Older VRChat log lines can name a player without their usr_ id. When the
    /// profile supplies both, attach that id to the stored events so history
    /// filtering can stay id-based instead of matching display names. This is
    /// deliberately conservative: the profile must already have one known-id
    /// row under that name, and there must be exactly one name-only occurrence
    /// to repair. A second name-only occurrence (which could be another player
    /// sharing the display name) leaves every row untouched.
    /// </summary>
    /// <summary>
    /// Finds the one id ever recorded for a display name, so a name-only row can
    /// borrow it when the same person appears with an id elsewhere in the log or
    /// in an imported history. Returns null when the name is unknown or shared.
    /// </summary>
    public string? FindKnownUserId(string accountUserId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(accountUserId) || string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT user_id
                FROM gamelog_join_leave
                WHERE account_user_id = $account
                  AND display_name = $display COLLATE NOCASE
                  AND user_id <> ''
                GROUP BY user_id
                LIMIT 2
                """;
            command.Parameters.AddWithValue("$account", accountUserId);
            command.Parameters.AddWithValue("$display", displayName.Trim());

            var ids = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }

            return ids.Count == 1 ? ids[0] : null;
        }
    }

    public int AssignPlayerUserId(string accountUserId, string displayName, string userId)
    {
        InvalidateCache();
        // This edits stored rows in place, which neither the row count nor the
        // highest id can reveal, so the parsed rows have to go.
        InvalidateEventRows();
        if (string.IsNullOrWhiteSpace(accountUserId) ||
            string.IsNullOrWhiteSpace(displayName) ||
            string.IsNullOrWhiteSpace(userId))
        {
            return 0;
        }

        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();

            using (var evidence = connection.CreateCommand())
            {
                evidence.CommandText = """
                    SELECT
                        SUM(CASE WHEN user_id <> '' AND user_id <> $userId THEN 1 ELSE 0 END),
                        SUM(CASE WHEN user_id = $userId THEN 1 ELSE 0 END),
                        COUNT(CASE
                            WHEN user_id = '' AND event_type = 'OnPlayerJoined'
                            THEN 1 END),
                        COUNT(DISTINCT CASE
                            WHEN user_id = '' AND event_type = 'OnPlayerJoined'
                            THEN location END)
                    FROM gamelog_join_leave
                    WHERE account_user_id = $account
                      AND display_name = $display COLLATE NOCASE
                    """;
                evidence.Parameters.AddWithValue("$userId", userId);
                evidence.Parameters.AddWithValue("$account", accountUserId);
                evidence.Parameters.AddWithValue("$display", displayName);

                using var reader = evidence.ExecuteReader();
                if (!reader.Read())
                {
                    return 0;
                }

                var conflictingIds = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                var knownTargetRows = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                var nameOnlyOccurrences = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                var nameOnlyLocations = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                if (conflictingIds > 0 ||
                    knownTargetRows == 0 ||
                    nameOnlyOccurrences != 1 ||
                    nameOnlyLocations != 1)
                {
                    return 0;
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE OR IGNORE gamelog_join_leave
                SET user_id = $userId
                WHERE account_user_id = $account
                  AND user_id = ''
                  AND display_name = $display COLLATE NOCASE
                  AND location = (
                      SELECT location
                      FROM gamelog_join_leave
                      WHERE account_user_id = $account
                        AND display_name = $display COLLATE NOCASE
                        AND user_id = ''
                        AND event_type = 'OnPlayerJoined'
                      ORDER BY id
                      LIMIT 1)
                """;
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$account", accountUserId);
            command.Parameters.AddWithValue("$display", displayName);
            var repaired = command.ExecuteNonQuery();
            if (repaired > 0)
            {
                // Rows changed in place; a reader that skipped a rebuild would
                // still show the name-only row.
                BumpRevision();
            }

            return repaired;
        }
    }

    /// <summary>
    /// A profile can have no known-id row at all, which is common after a
    /// log-only import. Display-name matching is only safe for that fallback
    /// when the name still identifies one name-only join and no other known
    /// account has used it.
    /// </summary>
    public bool CanUseNameOnlyFallback(string accountUserId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(accountUserId) || string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    SUM(CASE WHEN user_id <> '' AND user_id <> $userId THEN 1 ELSE 0 END),
                    COUNT(CASE WHEN user_id = '' AND event_type = 'OnPlayerJoined' THEN 1 END),
                    COUNT(DISTINCT CASE WHEN user_id = '' AND event_type = 'OnPlayerJoined'
                        THEN location END)
                FROM gamelog_join_leave
                WHERE account_user_id = $account
                  AND display_name = $display COLLATE NOCASE
                """;
            command.Parameters.AddWithValue("$userId", string.Empty);
            command.Parameters.AddWithValue("$account", accountUserId);
            command.Parameters.AddWithValue("$display", displayName);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            var conflictingIds = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
            var nameOnlyOccurrences = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            var nameOnlyLocations = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            return conflictingIds == 0 &&
                   nameOnlyOccurrences == 1 &&
                   nameOnlyLocations == 1;
        }
    }

    /// <summary>
    /// Deletes every stored session represented by one view row in a single
    /// transaction. A merged row can span several sessions, and deleting them
    /// together prevents regrouping from changing later session ids mid-delete.
    /// </summary>
    public int DeleteMany(IEnumerable<string> ids)
    {
        var wanted = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            EnsureInitialized();
            InvalidateCache();
            InvalidateEventRows();
            using var connection = OpenConnection();
            var entries = BuildEntries(connection, accountUserId: null)
                .Where(entry => wanted.Contains(entry.Id))
                .ToList();
            if (entries.Count == 0)
            {
                return 0;
            }

            using var transaction = connection.BeginTransaction();
            var eventIds = entries
                .SelectMany(entry => entry.EventIds)
                .Distinct()
                .ToList();
            if (eventIds.Count > 0)
            {
                DeleteEventRows(connection, transaction, eventIds);
            }

            foreach (var entry in entries)
            {
                using var cleanup = connection.CreateCommand();
                cleanup.Transaction = transaction;
                cleanup.CommandText = """
                    DELETE FROM gamelog_location
                    WHERE account_user_id = $account
                      AND location = $location
                      AND NOT EXISTS (
                          SELECT 1 FROM gamelog_join_leave e
                          WHERE e.account_user_id = gamelog_location.account_user_id
                            AND e.location = gamelog_location.location)
                    """;
                cleanup.Parameters.AddWithValue("$account", entry.AccountUserId);
                cleanup.Parameters.AddWithValue("$location", entry.Location);
                cleanup.ExecuteNonQuery();
            }

            transaction.Commit();
            _locationVersion++;
            BumpRevision();
            return entries.Count;
        }
    }

    /// <summary>Closes any live session left open by a crashed/closed client.</summary>
    public int CloseOpenSessions(string accountUserId, DateTimeOffset? at = null)
    {
        InvalidateCache();
        if (string.IsNullOrWhiteSpace(accountUserId))
        {
            return 0;
        }

        lock (_gate)
        {
            EnsureInitialized();
            var closed = 0;
            var open = ForAccount(accountUserId).Where(entry => entry.LeftAt is null).ToList();
            foreach (var entry in open)
            {
                var self = entry.Players.FirstOrDefault(player =>
                    string.Equals(player.UserId, accountUserId, StringComparison.Ordinal));
                var displayName = self?.DisplayName ?? "Me";
                var leftAt = at ?? (entry.LastEventAt == default
                    ? entry.LastSeenAt
                    : entry.LastEventAt);
                if (leftAt < entry.JoinedAt)
                {
                    leftAt = entry.JoinedAt;
                }

                AppendEvent(
                    accountUserId,
                    entry.Location,
                    accountUserId,
                    displayName,
                    LeftEvent,
                    leftAt,
                    self?.AvatarUrl ?? string.Empty);
                closed++;
            }

            return closed;
        }
    }

    public int CloseOpenSessionsOlderThan(string accountUserId, DateTimeOffset cutoff)
    {
        InvalidateCache();
        if (string.IsNullOrWhiteSpace(accountUserId))
        {
            return 0;
        }

        lock (_gate)
        {
            EnsureInitialized();
            var closed = 0;
            foreach (var entry in ForAccount(accountUserId)
                         .Where(entry => entry.LeftAt is null &&
                                         (entry.LastEventAt == default
                                             ? entry.LastSeenAt
                                             : entry.LastEventAt) < cutoff))
            {
                var self = entry.Players.FirstOrDefault(player =>
                    string.Equals(player.UserId, accountUserId, StringComparison.Ordinal));
                AppendEvent(
                    accountUserId,
                    entry.Location,
                    accountUserId,
                    self?.DisplayName ?? "Me",
                    LeftEvent,
                    entry.LastEventAt == default ? entry.LastSeenAt : entry.LastEventAt,
                    self?.AvatarUrl ?? string.Empty);
                closed++;
            }

            return closed;
        }
    }

    /// <summary>No-op for compatibility: events are written immediately.</summary>
    public void Save()
    {
        InvalidateCache();
        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Records one observed join/leave event from the live log.</summary>
    public void RecordPlayerEvent(
        string accountUserId,
        string location,
        string userId,
        string displayName,
        string avatarUrl,
        bool joined,
        DateTimeOffset at)
    {
        InvalidateCache();
        if (string.IsNullOrWhiteSpace(accountUserId) ||
            !LaunchService.IsLocation(location))
        {
            return;
        }

        lock (_gate)
        {
            EnsureInitialized();
            AppendEvent(
                accountUserId,
                location,
                userId ?? string.Empty,
                displayName ?? string.Empty,
                joined ? JoinedEvent : LeftEvent,
                at,
                avatarUrl ?? string.Empty);
        }
    }

    /// <summary>Closes one live session with a real self leave event.</summary>
    public void CloseSession(InstanceHistoryEntry entry, DateTimeOffset at)
    {
        InvalidateCache();
        if (entry is null || string.IsNullOrWhiteSpace(entry.AccountUserId))
        {
            return;
        }

        var self = entry.Players.FirstOrDefault(player =>
            string.Equals(player.UserId, entry.AccountUserId, StringComparison.Ordinal));
        RecordPlayerEvent(
            entry.AccountUserId,
            entry.Location,
            entry.AccountUserId,
            self?.DisplayName ?? "Me",
            self?.AvatarUrl ?? string.Empty,
            joined: false,
            at < entry.JoinedAt ? entry.JoinedAt : at);
    }

    /// <summary>Persists API-enriched world/creator metadata for a history row.</summary>
    public void UpsertMetadata(InstanceHistoryEntry entry)
    {
        InvalidateCache();
        if (!IsUsable(entry))
        {
            return;
        }

        lock (_gate)
        {
            EnsureInitialized();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            UpsertLocation(connection, transaction, entry);
            transaction.Commit();
            _locationVersion++;
            BumpRevision();
        }
    }

    internal static InstanceHistoryEntry? NewFromLog(
        string accountUserId,
        string location,
        DateTimeOffset joinedAt)
    {
        var worldId = LaunchService.GetWorldIdFromLocation(location);
        var instanceId = LaunchService.GetInstanceIdFromLocation(location);
        if (worldId is null || instanceId is null)
        {
            return null;
        }

        return new InstanceHistoryEntry
        {
            Id = StableId(accountUserId, location, joinedAt),
            AccountUserId = accountUserId,
            Location = location,
            WorldId = worldId,
            InstanceId = instanceId,
            OwnerId = OwnerFromLocation(location),
            JoinedAt = joinedAt,
            LastSeenAt = joinedAt
        };
    }

    internal static string StableId(string accountUserId, string location, DateTimeOffset joinedAt)
    {
        var input = string.Join(
            "|",
            accountUserId,
            location,
            joinedAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    internal static string OwnerFromLocation(string location)
    {
        foreach (var tag in new[] { "~private(", "~friends(", "~hidden(" })
        {
            var index = location.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var start = index + tag.Length;
                var end = location.IndexOf(')', start);
                if (end > start)
                {
                    return location[start..end];
                }
            }
        }

        var group = location.IndexOf("~group(", StringComparison.OrdinalIgnoreCase);
        if (group >= 0)
        {
            var start = group + "~group(".Length;
            var end = location.IndexOf(')', start);
            if (end > start)
            {
                return location[start..end];
            }
        }

        return string.Empty;
    }

    private static bool IsUsable(InstanceHistoryEntry entry)
        => !string.IsNullOrWhiteSpace(entry.Id) &&
           !string.IsNullOrWhiteSpace(entry.AccountUserId) &&
           LaunchService.IsLocation(entry.Location);

    private SqliteConnection OpenConnection()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
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

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS gamelog_location (
                account_user_id TEXT NOT NULL,
                location TEXT NOT NULL,
                world_id TEXT NOT NULL DEFAULT '',
                instance_id TEXT NOT NULL DEFAULT '',
                world_name TEXT NOT NULL DEFAULT '',
                thumbnail_url TEXT NOT NULL DEFAULT '',
                author_name TEXT NOT NULL DEFAULT '',
                owner_id TEXT NOT NULL DEFAULT '',
                owner_name TEXT NOT NULL DEFAULT '',
                first_seen_at TEXT NOT NULL,
                last_updated_at TEXT NOT NULL,
                PRIMARY KEY (account_user_id, location)
            );

            CREATE TABLE IF NOT EXISTS gamelog_join_leave (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                account_user_id TEXT NOT NULL,
                location TEXT NOT NULL,
                user_id TEXT NOT NULL DEFAULT '',
                display_name TEXT NOT NULL DEFAULT '',
                avatar_url TEXT NOT NULL DEFAULT '',
                event_type TEXT NOT NULL,
                created_at TEXT NOT NULL,
                created_at_ts INTEGER NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_gamelog_join_leave_event
                ON gamelog_join_leave
                   (account_user_id, location, user_id, display_name, event_type, created_at_ts);
            CREATE INDEX IF NOT EXISTS ix_gamelog_join_leave_account_time
                ON gamelog_join_leave (account_user_id, created_at_ts, id);
            CREATE INDEX IF NOT EXISTS ix_gamelog_join_leave_location_time
                ON gamelog_join_leave (account_user_id, location, created_at_ts, id);
            """;
        command.ExecuteNonQuery();
        _initialized = true;
    }

    private long CountEvents()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM gamelog_join_leave";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void UpsertLocation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        InstanceHistoryEntry entry)
    {
        var worldId = string.IsNullOrWhiteSpace(entry.WorldId)
            ? LaunchService.GetWorldIdFromLocation(entry.Location) ?? string.Empty
            : entry.WorldId;
        var instanceId = string.IsNullOrWhiteSpace(entry.InstanceId)
            ? LaunchService.GetInstanceIdFromLocation(entry.Location) ?? string.Empty
            : entry.InstanceId;
        var ownerId = string.IsNullOrWhiteSpace(entry.OwnerId)
            ? OwnerFromLocation(entry.Location)
            : entry.OwnerId;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO gamelog_location (
                account_user_id, location, world_id, instance_id, world_name,
                thumbnail_url, author_name, owner_id, owner_name,
                first_seen_at, last_updated_at)
            VALUES (
                $account, $location, $worldId, $instanceId, $worldName,
                $thumbnail, $author, $ownerId, $ownerName, $firstSeen, $updated)
            ON CONFLICT(account_user_id, location) DO UPDATE SET
                world_id = CASE WHEN excluded.world_id <> '' THEN excluded.world_id ELSE gamelog_location.world_id END,
                instance_id = CASE WHEN excluded.instance_id <> '' THEN excluded.instance_id ELSE gamelog_location.instance_id END,
                world_name = CASE WHEN excluded.world_name <> '' THEN excluded.world_name ELSE gamelog_location.world_name END,
                thumbnail_url = CASE WHEN excluded.thumbnail_url <> '' THEN excluded.thumbnail_url ELSE gamelog_location.thumbnail_url END,
                author_name = CASE WHEN excluded.author_name <> '' THEN excluded.author_name ELSE gamelog_location.author_name END,
                owner_id = CASE WHEN excluded.owner_id <> '' THEN excluded.owner_id ELSE gamelog_location.owner_id END,
                owner_name = CASE WHEN excluded.owner_name <> '' THEN excluded.owner_name ELSE gamelog_location.owner_name END,
                last_updated_at = excluded.last_updated_at
            """;
        command.Parameters.AddWithValue("$account", entry.AccountUserId);
        command.Parameters.AddWithValue("$location", entry.Location);
        command.Parameters.AddWithValue("$worldId", worldId);
        command.Parameters.AddWithValue("$instanceId", instanceId);
        command.Parameters.AddWithValue("$worldName", entry.WorldName ?? string.Empty);
        command.Parameters.AddWithValue("$thumbnail", entry.WorldThumbnailUrl ?? string.Empty);
        command.Parameters.AddWithValue("$author", entry.WorldAuthorName ?? string.Empty);
        command.Parameters.AddWithValue("$ownerId", ownerId);
        command.Parameters.AddWithValue("$ownerName", entry.OwnerName ?? string.Empty);
        command.Parameters.AddWithValue("$firstSeen", ToStorage(entry.JoinedAt));
        command.Parameters.AddWithValue("$updated", ToStorage(entry.LastSeenAt));
        command.ExecuteNonQuery();
    }

    private static void WriteEntryEvents(
        SqliteConnection connection,
        SqliteTransaction transaction,
        InstanceHistoryEntry entry)
    {
        var players = entry.Players.ToList();
        if (!players.Any(player =>
                string.Equals(player.UserId, entry.AccountUserId, StringComparison.Ordinal)))
        {
            var self = new InstanceHistoryPlayer
            {
                UserId = entry.AccountUserId,
                DisplayName = "Me"
            };
            self.StartVisit(entry.JoinedAt).LeftAt = entry.LeftAt;
            players.Add(self);
        }

        var events = new List<(DateTimeOffset At, bool Joined, InstanceHistoryPlayer Player)>();
        foreach (var player in players)
        {
            foreach (var visit in player.Visits)
            {
                events.Add((visit.JoinedAt, true, player));
                if (visit.LeftAt is { } left)
                {
                    events.Add((left, false, player));
                }
            }
        }

        foreach (var item in events
                     .OrderBy(item => item.At)
                     .ThenByDescending(item => item.Joined))
        {
            InsertEvent(
                connection,
                transaction,
                entry.AccountUserId,
                entry.Location,
                item.Player.UserId,
                item.Player.DisplayName,
                item.Player.AvatarUrl,
                item.Joined ? JoinedEvent : LeftEvent,
                item.At);
        }
    }

    private void AppendEvent(
        string accountUserId,
        string location,
        string userId,
        string displayName,
        string eventType,
        DateTimeOffset at,
        string avatarUrl)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        InsertEvent(
            connection,
            transaction,
            accountUserId,
            location,
            userId,
            displayName,
            avatarUrl,
            eventType,
            at);
        transaction.Commit();
        BumpRevision();
    }

    private static void InsertEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountUserId,
        string location,
        string userId,
        string displayName,
        string avatarUrl,
        string eventType,
        DateTimeOffset at)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO gamelog_join_leave (
                account_user_id, location, user_id, display_name, avatar_url,
                event_type, created_at, created_at_ts)
            VALUES ($account, $location, $userId, $name, $avatar, $type, $created, $ts)
            """;
        command.Parameters.AddWithValue("$account", accountUserId);
        command.Parameters.AddWithValue("$location", location);
        command.Parameters.AddWithValue("$userId", userId ?? string.Empty);
        command.Parameters.AddWithValue("$name", displayName ?? string.Empty);
        command.Parameters.AddWithValue("$avatar", avatarUrl ?? string.Empty);
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$created", ToStorage(at));
        command.Parameters.AddWithValue("$ts", at.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static void DeleteEventRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<long> ids)
    {
        // SQLite has a parameter limit, so delete a large roster in chunks.
        for (var offset = 0; offset < ids.Count; offset += 400)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var names = new List<string>();
            for (var index = offset; index < Math.Min(offset + 400, ids.Count); index++)
            {
                var name = "$id" + index;
                names.Add(name);
                command.Parameters.AddWithValue(name, ids[index]);
            }

            command.CommandText =
                "DELETE FROM gamelog_join_leave WHERE id IN (" + string.Join(", ", names) + ")";
            command.ExecuteNonQuery();
        }
    }

    private IReadOnlyList<InstanceHistoryEntry> BuildEntries(
        SqliteConnection connection,
        string? accountUserId)
    {
        var locations = ReadLocationsCached(connection, accountUserId);
        var events = ReadEvents(connection, accountUserId);
        if (events.Count == 0)
        {
            return Array.Empty<InstanceHistoryEntry>();
        }

        _eventCaches.TryGetValue(accountUserId ?? string.Empty, out var cache);
        var groups = GroupRows(cache, events);

        var lastIndexByAccount = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < groups.Count; index++)
        {
            lastIndexByAccount[groups[index].AccountUserId] = index;
        }

        // A finished group is built once and reused. Grouping is strictly
        // sequential - a group can only merge with the one before it, and once a
        // newer group exists for that account no row can ever join it again - so
        // only each account's last group has to be rebuilt. That last group is
        // the open session, whose LeftAt and LastSeenAt still move on every read.
        var result = new List<InstanceHistoryEntry>(Math.Min(groups.Count, MaximumEntries));
        for (var index = groups.Count - 1; index >= 0 && result.Count < MaximumEntries; index--)
        {
            var group = groups[index];
            var isLastGroup = lastIndexByAccount[group.AccountUserId] == index;
            locations.TryGetValue((group.AccountUserId, group.Location), out var location);
            var entry = isLastGroup ? null : group.Entry;
            if (entry is null || !Equals(group.EntryLocation, location))
            {
                entry = BuildEntry(group, isLastGroup, location);
                group.Entry = isLastGroup ? null : entry;
                group.EntryLocation = isLastGroup ? null : location;
            }

            result.Add(entry);
        }

        // Groups are already in join order, so walking them backwards gives the
        // newest-first order the screens want without sorting every entry.
        return result;
    }

    /// <summary>
    /// Keeps the sequential grouping between reads. Only the rows appended since
    /// the last read are grouped again; a full read (a delete, an edit in place
    /// or an out-of-order import) replaces the rows and rebuilds every group.
    /// </summary>
    private static List<GroupState> GroupRows(EventRowCache? cache, List<EventRow> events)
    {
        if (cache is null)
        {
            return GroupAll(events);
        }

        if (cache.Groups is null ||
            !ReferenceEquals(cache.GroupedRows, events) ||
            events.Count < cache.GroupedCount)
        {
            cache.Groups = GroupAll(events);
            cache.GroupedRows = events;
            cache.GroupedCount = events.Count;
            return cache.Groups;
        }

        if (events.Count > cache.GroupedCount)
        {
            AppendGroups(cache.Groups, events, cache.GroupedCount);
            cache.GroupedCount = events.Count;
        }

        return cache.Groups;
    }

    private static List<GroupState> GroupAll(List<EventRow> events)
    {
        var groups = new List<GroupState>();
        foreach (var row in events)
        {
            var current = groups.Count == 0 ? null : groups[^1];
            if (current is null || StartsNewGroup(current.Rows, row))
            {
                groups.Add(new GroupState(row));
                continue;
            }

            current.Rows.Add(row);
        }

        return groups;
    }

    private static void AppendGroups(IList<GroupState> groups, List<EventRow> events, int start)
    {
        for (var index = start; index < events.Count; index++)
        {
            var row = events[index];
            var current = groups.Count == 0 ? null : groups[^1];
            if (current is null || StartsNewGroup(current.Rows, row))
            {
                groups.Add(new GroupState(row));

                // A newer group for the same account means the previous one is
                // no longer the open session, so its built row is out of date.
                if (current is not null &&
                    string.Equals(current.AccountUserId, row.AccountUserId, StringComparison.Ordinal))
                {
                    current.Entry = null;
                    current.EntryLocation = null;
                }

                continue;
            }

            // The last group just grew, so its built row no longer matches.
            current!.Rows.Add(row);
            current.Entry = null;
            current.EntryLocation = null;
        }
    }

    private static bool StartsNewGroup(IReadOnlyList<EventRow> group, EventRow row)
    {
        var last = group[^1];
        if (!string.Equals(last.AccountUserId, row.AccountUserId, StringComparison.Ordinal) ||
            !string.Equals(last.Location, row.Location, StringComparison.Ordinal))
        {
            return true;
        }

        var hasSelfEvent = group.Any(item =>
            string.Equals(item.UserId, row.AccountUserId, StringComparison.Ordinal));
        if (hasSelfEvent)
        {
            // A self session stays together across quiet hours. It only ends
            // when the log reports that the local player left; the next event
            // belongs to the next instance.
            var selfLeftTs = group
                .Where(item =>
                    string.Equals(item.UserId, row.AccountUserId, StringComparison.Ordinal) &&
                    item.EventType == LeftEvent)
                .Select(item => item.CreatedAtTs)
                .DefaultIfEmpty(long.MinValue)
                .Max();
            return (selfLeftTs != long.MinValue &&
                    (row.EventType == JoinedEvent ||
                     row.CreatedAtTs - selfLeftTs > GroupingToleranceMilliseconds)) ||
                   (string.Equals(row.UserId, row.AccountUserId, StringComparison.Ordinal) &&
                    row.EventType == JoinedEvent &&
                    row.CreatedAtTs - last.CreatedAtTs > GroupingToleranceMilliseconds);
        }

        return row.CreatedAtTs - last.CreatedAtTs > GroupingToleranceMilliseconds &&
               !(last.EventType == JoinedEvent && row.EventType == LeftEvent);
    }

    private static InstanceHistoryEntry BuildEntry(
        GroupState group,
        bool isLastGroup,
        LocationRow? location)
    {
        var rows = group.Rows;
        var row = rows[0];
        var selfEvents = rows
            .Where(item => string.Equals(item.UserId, row.AccountUserId, StringComparison.Ordinal))
            .ToList();
        var selfJoin = selfEvents.FirstOrDefault(item => item.EventType == JoinedEvent);
        var selfLeft = selfEvents.LastOrDefault(item => item.EventType == LeftEvent);
        var joinedAt = selfJoin?.CreatedAt ?? row.CreatedAt;
        var groupEnd = rows[^1].CreatedAt;
        var hasSelfLeft = selfLeft is not null &&
                          (selfJoin is null || selfLeft.CreatedAtTs >= selfJoin.CreatedAtTs);
        var closed = hasSelfLeft || !isLastGroup;
        // Your own leave is the end of the visit. Player events that arrive
        // after it must not stretch the row's duration to the last log line.
        var leftAt = closed
            ? (hasSelfLeft ? selfLeft!.CreatedAt : groupEnd)
            : (DateTimeOffset?)null;
        var lastSeenAt = closed ? leftAt!.Value : DateTimeOffset.Now;
        if (lastSeenAt < joinedAt)
        {
            lastSeenAt = joinedAt;
        }

        var entry = new InstanceHistoryEntry
        {
            Id = StableId(row.AccountUserId, row.Location, joinedAt),
            AccountUserId = row.AccountUserId,
            Location = row.Location,
            WorldId = location?.WorldId ?? LaunchService.GetWorldIdFromLocation(row.Location) ?? string.Empty,
            InstanceId = location?.InstanceId ?? LaunchService.GetInstanceIdFromLocation(row.Location) ?? string.Empty,
            WorldName = location?.WorldName ?? string.Empty,
            WorldThumbnailUrl = location?.ThumbnailUrl ?? string.Empty,
            WorldAuthorName = location?.AuthorName ?? string.Empty,
            OwnerId = location?.OwnerId ?? OwnerFromLocation(row.Location),
            OwnerName = location?.OwnerName ?? string.Empty,
            JoinedAt = joinedAt,
            LeftAt = leftAt,
            LastSeenAt = lastSeenAt,
            LastEventAt = groupEnd,
            EventIds = rows.Select(item => item.Id).ToList()
        };

        BuildPlayers(entry, rows, closed ? groupEnd : null);
        return entry;
    }
    private static void BuildPlayers(
        InstanceHistoryEntry entry,
        IReadOnlyList<EventRow> group,
        DateTimeOffset? closeOpenAt)
    {
        foreach (var row in group)
        {
            var player = entry.GetOrAddPlayer(row.UserId, row.DisplayName, row.AvatarUrl);
            if (row.EventType == JoinedEvent)
            {
                if (!player.HasOpenVisit)
                {
                    player.StartVisit(row.CreatedAt);
                }
                continue;
            }

            if (row.EventType != LeftEvent)
            {
                continue;
            }

            for (var index = player.Visits.Count - 1; index >= 0; index--)
            {
                if (player.Visits[index].LeftAt is not null)
                {
                    continue;
                }

                var visit = player.Visits[index];
                visit.LastSeenAt = row.CreatedAt;
                visit.LeftAt = row.CreatedAt < visit.JoinedAt ? visit.JoinedAt : row.CreatedAt;
                break;
            }
        }

        if (!entry.Players.Any(player =>
                string.Equals(player.UserId, entry.AccountUserId, StringComparison.Ordinal)))
        {
            var self = entry.GetOrAddPlayer(entry.AccountUserId, "Me");
            self.StartVisit(entry.JoinedAt);
        }

        if (closeOpenAt is { } closeAt)
        {
            foreach (var player in entry.Players)
            {
                foreach (var visit in player.Visits.Where(visit => visit.LeftAt is null))
                {
                    visit.LastSeenAt = closeAt;
                    visit.LeftAt = closeAt < visit.JoinedAt ? visit.JoinedAt : closeAt;
                }
            }
        }
    }

    private Dictionary<(string Account, string Location), LocationRow> ReadLocationsCached(
        SqliteConnection connection,
        string? accountUserId)
    {
        var key = accountUserId ?? string.Empty;
        var (count, updated) = ReadLocationStats(connection, accountUserId);
        if (_locationCaches.TryGetValue(key, out var cached) &&
            cached.Version == _locationVersion &&
            cached.Count == count &&
            string.Equals(cached.Updated, updated, StringComparison.Ordinal))
        {
            return cached.Rows;
        }

        var rows = ReadLocations(connection, accountUserId);
        if (_locationCaches.Count >= MaximumCachedEventAccounts)
        {
            _locationCaches.Clear();
        }

        _locationCaches[key] = new LocationCache
        {
            Version = _locationVersion,
            Count = count,
            Updated = updated,
            Rows = rows
        };
        return rows;
    }

    /// <summary>
    /// A cheap proof that the location table still matches the cached rows: a
    /// write from this store also bumps the version, and this catches a write
    /// from anywhere else (an importer's own store on the same database).
    /// </summary>
    private static (int Count, string Updated) ReadLocationStats(
        SqliteConnection connection,
        string? accountUserId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = accountUserId is null
            ? "SELECT COUNT(*), COALESCE(MAX(last_updated_at), '') FROM gamelog_location"
            : "SELECT COUNT(*), COALESCE(MAX(last_updated_at), '') FROM gamelog_location WHERE account_user_id = $account";
        if (accountUserId is not null)
        {
            command.Parameters.AddWithValue("$account", accountUserId);
        }

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ((int)reader.GetInt64(0), reader.GetString(1))
            : (0, string.Empty);
    }

    private static Dictionary<(string Account, string Location), LocationRow> ReadLocations(
        SqliteConnection connection,
        string? accountUserId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = accountUserId is null
            ? """
              SELECT account_user_id, location, world_id, instance_id, world_name,
                     thumbnail_url, author_name, owner_id, owner_name
              FROM gamelog_location
              """
            : """
              SELECT account_user_id, location, world_id, instance_id, world_name,
                     thumbnail_url, author_name, owner_id, owner_name
              FROM gamelog_location
              WHERE account_user_id = $account
              """;
        if (accountUserId is not null)
        {
            command.Parameters.AddWithValue("$account", accountUserId);
        }

        var rows = new Dictionary<(string, string), LocationRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var account = reader.GetString(0);
            var location = reader.GetString(1);
            rows[(account, location)] = new LocationRow(
                account,
                location,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8));
        }

        return rows;
    }

    private List<EventRow> ReadEvents(SqliteConnection connection, string? accountUserId)
    {
        var key = accountUserId ?? string.Empty;
        var (count, maxId) = ReadEventStats(connection, accountUserId);
        if (_eventCaches.TryGetValue(key, out var cache))
        {
            if (cache.Count == count && cache.MaxId == maxId)
            {
                return cache.Rows;
            }

            // The table only grows at its end for live events. Anything that
            // arrives with an older timestamp (an imported history) would sort
            // into the middle, so it must not be appended to the cached rows.
            if (count > cache.Count && maxId > cache.MaxId)
            {
                var appended = ReadEventRows(connection, accountUserId, afterId: cache.MaxId);
                if (appended.Count == count - cache.Count &&
                    (appended.Count == 0 || appended[0].CreatedAtTs >= cache.LastTimestamp))
                {
                    cache.Rows.AddRange(appended);
                    cache.Count = count;
                    cache.MaxId = maxId;
                    if (appended.Count > 0)
                    {
                        cache.LastTimestamp = appended[^1].CreatedAtTs;
                    }

                    return cache.Rows;
                }
            }
        }

        var rows = ReadEventRows(connection, accountUserId, afterId: null);
        if (_eventCaches.Count >= MaximumCachedEventAccounts)
        {
            _eventCaches.Clear();
        }

        _eventCaches[key] = new EventRowCache
        {
            Rows = rows,
            Count = rows.Count,
            MaxId = rows.Count == 0 ? 0 : rows[^1].Id,
            LastTimestamp = rows.Count == 0 ? 0 : rows[^1].CreatedAtTs
        };
        return rows;
    }

    private static (int Count, long MaxId) ReadEventStats(
        SqliteConnection connection,
        string? accountUserId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = accountUserId is null
            ? "SELECT COUNT(*), COALESCE(MAX(id), 0) FROM gamelog_join_leave"
            : "SELECT COUNT(*), COALESCE(MAX(id), 0) FROM gamelog_join_leave WHERE account_user_id = $account";
        if (accountUserId is not null)
        {
            command.Parameters.AddWithValue("$account", accountUserId);
        }

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ((int)reader.GetInt64(0), reader.GetInt64(1))
            : (0, 0);
    }

    private static List<EventRow> ReadEventRows(
        SqliteConnection connection,
        string? accountUserId,
        long? afterId)
    {
        const string projection =
            "SELECT id, account_user_id, location, user_id, display_name, avatar_url, " +
            "event_type, created_at, created_at_ts FROM gamelog_join_leave";
        using var command = connection.CreateCommand();
        command.CommandText = (accountUserId is null, afterId is null) switch
        {
            (true, true) => projection + " ORDER BY created_at_ts ASC, id ASC",
            (true, false) => projection + " WHERE id > $after ORDER BY created_at_ts ASC, id ASC",
            (false, true) => projection + " WHERE account_user_id = $account ORDER BY created_at_ts ASC, id ASC",
            _ => projection + " WHERE account_user_id = $account AND id > $after ORDER BY created_at_ts ASC, id ASC"
        };
        if (accountUserId is not null)
        {
            command.Parameters.AddWithValue("$account", accountUserId);
        }

        if (afterId is { } after)
        {
            command.Parameters.AddWithValue("$after", after);
        }

        var rows = new List<EventRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new EventRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                ParseStorage(reader.GetString(7)),
                reader.GetInt64(8)));
        }

        return rows;
    }

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

    private sealed class EventRowCache
    {
        public List<EventRow> Rows { get; init; } = new();

        public int Count { get; set; }

        public long MaxId { get; set; }

        public long LastTimestamp { get; set; }

        /// <summary>The sequential grouping the cached rows were cut into.</summary>
        public List<GroupState>? Groups { get; set; }

        /// <summary>The row list those groups were built from.</summary>
        public List<EventRow>? GroupedRows { get; set; }

        /// <summary>How many of those rows the groups already contain.</summary>
        public int GroupedCount { get; set; }
    }

    /// <summary>
    /// One run of consecutive events at the same location. A finished group
    /// keeps its built row so a refresh does not rebuild history that cannot
    /// change any more.
    /// </summary>
    private sealed class GroupState
    {
        public GroupState(EventRow first) => Rows.Add(first);

        public List<EventRow> Rows { get; } = new();

        public string AccountUserId => Rows[0].AccountUserId;

        public string Location => Rows[0].Location;

        /// <summary>The built row, kept only while the group stays finished.</summary>
        public InstanceHistoryEntry? Entry { get; set; }

        /// <summary>The stored world metadata that row was built from.</summary>
        public LocationRow? EntryLocation { get; set; }
    }

    /// <summary>
    /// One account's stored world metadata, kept until a location write changes
    /// it. The dictionary is shared with every caller and never mutated.
    /// </summary>
    private sealed class LocationCache
    {
        public long Version { get; init; }

        public int Count { get; init; }

        public string Updated { get; init; } = string.Empty;

        public Dictionary<(string Account, string Location), LocationRow> Rows { get; init; } = new();
    }

    private sealed record LocationRow(
        string AccountUserId,
        string Location,
        string WorldId,
        string InstanceId,
        string WorldName,
        string ThumbnailUrl,
        string AuthorName,
        string OwnerId,
        string OwnerName);

    private sealed record EventRow(
        long Id,
        string AccountUserId,
        string Location,
        string UserId,
        string DisplayName,
        string AvatarUrl,
        string EventType,
        DateTimeOffset CreatedAt,
        long CreatedAtTs);
}
