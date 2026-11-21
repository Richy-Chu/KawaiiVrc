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
                var file = LoadFile();
                file.Accounts[accountId] = new StoredAccount
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

                WriteFile(file);
            }
            catch (Exception ex)
            {
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

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcCompanion",
        "notifications.json");

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

        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(file, JsonOptions));
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
