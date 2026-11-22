using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>The last complete friend list seen for one account.</summary>
public sealed class FriendSnapshot
{
    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAt { get; set; }

    [JsonPropertyName("friends")]
    public List<FriendSnapshotEntry> Friends { get; set; } = new();

    /// <summary>Minimal user records for seeding the change detector.</summary>
    public IReadOnlyList<VrcUser> Users => Friends.Select(friend => friend.ToUser()).ToList();
}

/// <summary>
/// Only the fields needed to identify a friend and render a later
/// unfriend/readd event. Presence, bio and other profile data are intentionally
/// omitted: a large friends list would otherwise make every snapshot huge.
/// </summary>
public sealed class FriendSnapshotEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("developerType")]
    public string? DeveloperType { get; set; }

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("iconFrame")]
    public string? IconFrame { get; set; }

    public VrcUser ToUser() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Username = Username,
        Tags = Tags ?? new List<string>(),
        DeveloperType = DeveloperType,
        UserIcon = AvatarUrl,
        IconFrame = IconFrame,
        IsFriend = true
    };

    public static FriendSnapshotEntry From(VrcUser friend) => new()
    {
        Id = friend.Id,
        DisplayName = friend.DisplayName,
        Username = friend.Username,
        Tags = friend.Tags is { Count: > 0 } ? new List<string>(friend.Tags) : null,
        DeveloperType = friend.DeveloperType,
        AvatarUrl = ImageUrlHelper.PickAvatarThumbnailUrl(
            friend.UserIcon,
            friend.ProfilePicOverrideThumbnail ?? friend.IconUrl,
            friend.CurrentAvatarThumbnailImageUrl,
            friend.CurrentAvatarImageUrl,
            256),
        IconFrame = friend.IconFrame
    };
}

/// <summary>
/// Persists the last known friend list per account so a refresh after a
/// relaunch can detect friended/unfriended events that happened while the app
/// was closed. VRChat has no historical friendship endpoint, so these are
/// verified against friendStatus like any live list diff.
/// </summary>
public sealed class FriendSnapshotStore
{
    public static FriendSnapshotStore Shared { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private readonly string _path;

    public FriendSnapshotStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
        if (!string.IsNullOrWhiteSpace(path) || !File.Exists(_path))
        {
            return;
        }

        // Warm the parse off the UI thread; the launch diff then reads memory.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            lock (_sync)
            {
                LoadFile();
            }
        });
    }

    public FriendSnapshot? Load(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return null;
        }

        lock (_sync)
        {
            var file = LoadFile();
            if (!file.Accounts.TryGetValue(accountId, out var snapshot) || snapshot is null)
            {
                return null;
            }

            return new FriendSnapshot
            {
                CapturedAt = snapshot.CapturedAt,
                Friends = (snapshot.Friends ?? new List<FriendSnapshotEntry>())
                    .Where(friend => friend is not null && !string.IsNullOrWhiteSpace(friend.Id))
                    .GroupBy(friend => friend.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList()
            };
        }
    }

    public void Save(
        string? accountId,
        IEnumerable<VrcUser> friends,
        DateTimeOffset? capturedAt = null)
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
                var next = new FriendSnapshot
                {
                    CapturedAt = capturedAt ?? DateTimeOffset.UtcNow,
                    Friends = friends
                        .Where(friend => friend is not null && !string.IsNullOrWhiteSpace(friend.Id))
                        .GroupBy(friend => friend.Id, StringComparer.Ordinal)
                        .Select(group => FriendSnapshotEntry.From(group.First()))
                        .OrderBy(friend => friend.Id, StringComparer.Ordinal)
                        .ToList()
                };

                if (file.Accounts.TryGetValue(accountId, out var existing) &&
                    AreEquivalent(existing, next))
                {
                    return;
                }

                file.Accounts[accountId] = next;
                WriteFile(file);
            }
            catch (Exception ex)
            {
                // A failed snapshot must not interrupt a friend refresh.
                Debug.WriteLine($"friend snapshot save {_path}: {ex.Message}");
            }
        }
    }

    private static bool AreEquivalent(FriendSnapshot? left, FriendSnapshot right)
    {
        if (left?.Friends is null || left.Friends.Count != right.Friends.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Friends.Count; index++)
        {
            var a = left.Friends[index];
            var b = right.Friends[index];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal) ||
                !string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal) ||
                !string.Equals(a.Username, b.Username, StringComparison.Ordinal) ||
                !string.Equals(a.DeveloperType, b.DeveloperType, StringComparison.Ordinal) ||
                !string.Equals(a.AvatarUrl, b.AvatarUrl, StringComparison.Ordinal) ||
                !string.Equals(a.IconFrame, b.IconFrame, StringComparison.Ordinal) ||
                !SequenceEqual(a.Tags, b.Tags))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SequenceEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        => (left ?? Array.Empty<string>()).SequenceEqual(right ?? Array.Empty<string>(), StringComparer.Ordinal);

    private static string DefaultPath() => UserDataPaths.Combine("friend-snapshots.json");

    // Parsed once and reused until the file changes; both Save and the launch
    // read otherwise re-parse the whole snapshot on the UI thread.
    private StoredFile? _cachedFile;
    private long _cachedLength = -1;
    private DateTime _cachedWritten;

    private StoredFile LoadFile()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new StoredFile();
            }

            var info = new FileInfo(_path);
            if (_cachedFile is not null &&
                info.Length == _cachedLength &&
                info.LastWriteTimeUtc == _cachedWritten)
            {
                return _cachedFile;
            }

            var file = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(_path), JsonOptions);
            file ??= new StoredFile();
            file.Accounts ??= new Dictionary<string, FriendSnapshot>(StringComparer.Ordinal);
            _cachedFile = file;
            _cachedLength = info.Length;
            _cachedWritten = info.LastWriteTimeUtc;
            return file;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"friend snapshot load {_path}: {ex.Message}");
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

        var info = new FileInfo(_path);
        _cachedFile = file;
        _cachedLength = info.Exists ? info.Length : -1;
        _cachedWritten = info.Exists ? info.LastWriteTimeUtc : default;
    }

    private sealed class StoredFile
    {
        [JsonPropertyName("accounts")]
        public Dictionary<string, FriendSnapshot> Accounts { get; set; } = new(StringComparer.Ordinal);
    }
}
