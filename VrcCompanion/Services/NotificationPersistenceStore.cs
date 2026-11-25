using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>Invites and invite requests that should survive a relaunch.</summary>
public sealed record StoredNotificationState(
    IReadOnlyList<VrcNotification> Notifications,
    IReadOnlySet<string> ClearedIds);

/// <summary>
/// Keeps world invites and invite requests in
/// %LOCALAPPDATA%\VrcCompanion\notifications.json, keyed by account.
/// A notification is only removed when the user explicitly clears it.
/// Writes are atomic and a damaged file is treated as empty.
/// </summary>
public sealed class NotificationPersistenceStore
{
    public static NotificationPersistenceStore Shared { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private readonly string _path;

    // A fingerprint of the JSON this store last wrote for each account. A
    // refresh that changed nothing about the persisted invites must not
    // rewrite the file.
    private readonly Dictionary<string, ulong> _lastSavedAccounts = new(StringComparer.Ordinal);

    public NotificationPersistenceStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
    }

    public StoredNotificationState Load(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Empty();
        }

        lock (_sync)
        {
            var file = LoadFile();
            if (!file.Accounts.TryGetValue(accountId, out var stored) || stored is null)
            {
                return Empty();
            }

            var cleared = new HashSet<string>(
                (stored.ClearedIds ?? new List<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal);
            var notifications = (stored.Notifications ?? new List<VrcNotification>())
                .Where(notification =>
                    notification is not null &&
                    !string.IsNullOrWhiteSpace(notification.Id) &&
                    !cleared.Contains(notification.Id) &&
                    IsPersistent(notification.Type))
                .GroupBy(notification => notification.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            return new StoredNotificationState(notifications, cleared);
        }
    }

    public void Save(
        string? accountId,
        IEnumerable<VrcNotification> notifications,
        IEnumerable<string> clearedIds)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                var stored = new StoredAccount
                {
                    Notifications = notifications
                        .Where(notification =>
                            notification is not null &&
                            !string.IsNullOrWhiteSpace(notification.Id) &&
                            IsPersistent(notification.Type))
                        .GroupBy(notification => notification.Id, StringComparer.Ordinal)
                        .Select(group => group.First())
                        .ToList(),
                    // Keep a bounded history so an invite that VRChat still
                    // returns cannot reappear after the user cleared it.
                    ClearedIds = clearedIds
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .TakeLast(500)
                        .ToList()
                };

                // The notification refresh calls this every cycle and almost
                // never changes a persisted invite. Hashing this account's JSON
                // and comparing it to the last write skips the read, the parse
                // and the rewrite for that - measured at 1,502,080 B and a
                // 110 KB file write per unchanged save.
                var hash = HashStored(stored);
                if (_lastSavedAccounts.TryGetValue(accountId, out var previous) && previous == hash)
                {
                    return;
                }

                var file = LoadFile();
                file.Accounts[accountId] = stored;
                WriteFile(file);
                _lastSavedAccounts[accountId] = hash;
            }
            catch (Exception ex)
            {
                // A failed save must not leave a cached text behind that would
                // make the next save skip writing the account.
                _lastSavedAccounts.Remove(accountId);
                // Losing a persisted invite must never take the app down.
                Debug.WriteLine($"notification state save {_path}: {ex.Message}");
            }
        }
    }

    internal static bool IsPersistent(string? type)
        => string.Equals(type, "invite", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(type, "requestInvite", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(type, "group.invite", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(type, "boop", StringComparison.OrdinalIgnoreCase);

    private static StoredNotificationState Empty()
        => new(Array.Empty<VrcNotification>(), new HashSet<string>(StringComparer.Ordinal));

    private static string DefaultPath() => UserDataPaths.Combine("notifications.json");

    private StoredFile LoadFile()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new StoredFile();
            }

            var file = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(_path), JsonOptions);
            if (file is null)
            {
                return new StoredFile();
            }

            file.Accounts ??= new Dictionary<string, StoredAccount>(StringComparer.Ordinal);
            return file;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"notification state load {_path}: {ex.Message}");
            return new StoredFile();
        }
    }

    private void WriteFile(StoredFile file)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // AtomicFile.WriteJson streams the document through a Utf8JsonWriter, so
        // the file is written compact even though JsonOptions indents elsewhere.
        // Nothing reads it by eye, and 300 boops saved 110 KB -> 65 KB.
        AtomicFile.WriteJson(_path, file, JsonOptions);
    }

    /// <summary>
    /// Hashes one account's JSON without building it: the serializer writes
    /// into a sink that only folds the bytes into an FNV-1a value, so an
    /// unchanged refresh allocates almost nothing.
    /// </summary>
    private static ulong HashStored(StoredAccount stored)
    {
        var sink = new HashingSink();
        using (var writer = new Utf8JsonWriter(sink))
        {
            JsonSerializer.Serialize(writer, stored, JsonOptions);
        }

        return sink.Hash;
    }

    private sealed class HashingSink : System.Buffers.IBufferWriter<byte>
    {
        private const ulong Prime = 1099511628211UL;

        private byte[] _buffer = new byte[4096];
        private int _written;
        private ulong _hash = 14695981039346656037UL;

        public ulong Hash
        {
            get
            {
                Fold();
                return _hash;
            }
        }

        public void Advance(int count)
        {
            _written += count;
            if (_written == _buffer.Length)
            {
                Fold();
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void Ensure(int sizeHint)
        {
            if (sizeHint <= 0)
            {
                sizeHint = 1;
            }

            if (_buffer.Length - _written >= sizeHint)
            {
                return;
            }

            Fold();
            if (_buffer.Length < sizeHint)
            {
                _buffer = new byte[Math.Max(sizeHint, 4096)];
            }
        }

        private void Fold()
        {
            for (var index = 0; index < _written; index++)
            {
                _hash = (_hash ^ _buffer[index]) * Prime;
            }

            _written = 0;
        }
    }

    private sealed class StoredFile
    {
        [JsonPropertyName("accounts")]
        public Dictionary<string, StoredAccount> Accounts { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class StoredAccount
    {
        [JsonPropertyName("notifications")]
        public List<VrcNotification>? Notifications { get; set; }

        [JsonPropertyName("clearedIds")]
        public List<string>? ClearedIds { get; set; }
    }
}
