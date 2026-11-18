using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>A resolved avatar id/privacy state keyed by the avatar image file id.</summary>
public sealed record FriendAvatarImageState(string? AvatarId, string? ReleaseStatus);

/// <summary>
/// Remembers the current avatar seen for each VRChat user, per signed-in
/// account. VRChat has no endpoint for another user's avatar history, so this
/// is the local equivalent of VRCX's feed_avatar rows: it lets an open profile
/// show avatars the person has used even when they later switch.
/// </summary>
public sealed class FriendAvatarStore
{
    public static FriendAvatarStore Shared { get; } = new();

    private const int MaximumPerUser = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private readonly string _path;

    public FriendAvatarStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
    }

    public string FilePath => _path;

    public IReadOnlyList<FriendAvatar> Load(string? accountId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(userId))
        {
            return Array.Empty<FriendAvatar>();
        }

        lock (_sync)
        {
            var file = LoadFile();
            return ReadAccount(file, accountId, userId)
                .Select(ToModel)
                .ToList();
        }
    }

    /// <summary>
    /// Looks up what was previously learned from an avatar picture. This is what
    /// keeps a friend/non-friend profile from returning to "Checking…" on every
    /// open when VRChat omits the avatar id but keeps the image URL.
    /// </summary>
    public FriendAvatarImageState? GetImageResolution(string? accountId, string? imageUrl)
    {
        var fileId = ImageUrlHelper.ExtractFileId(imageUrl);
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(fileId))
        {
            return null;
        }

        lock (_sync)
        {
            var file = LoadFile();
            if (!file.Accounts.TryGetValue(accountId, out var account) ||
                account?.ImageResolutions is null ||
                !account.ImageResolutions.TryGetValue(fileId, out var state) ||
                state is null)
            {
                return null;
            }

            return new FriendAvatarImageState(state.AvatarId, state.ReleaseStatus);
        }
    }

    /// <summary>Remembers the avatar id and/or public-private result for an image.</summary>
    public void RememberImageResolution(
        string? accountId,
        string? imageUrl,
        string? avatarId,
        string? releaseStatus)
    {
        var fileId = ImageUrlHelper.ExtractFileId(imageUrl);
        if (string.IsNullOrWhiteSpace(accountId) ||
            string.IsNullOrWhiteSpace(fileId) ||
            (string.IsNullOrWhiteSpace(avatarId) && string.IsNullOrWhiteSpace(releaseStatus)))
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                var file = LoadFile();
                if (!file.Accounts.TryGetValue(accountId, out var account) || account is null)
                {
                    account = new StoredAccount();
                    file.Accounts[accountId] = account;
                }

                account.ImageResolutions ??= new Dictionary<string, StoredImageResolution>(StringComparer.Ordinal);
                if (!account.ImageResolutions.TryGetValue(fileId, out var state) || state is null)
                {
                    state = new StoredImageResolution();
                    account.ImageResolutions[fileId] = state;
                }

                var nextAvatarId = string.IsNullOrWhiteSpace(avatarId) ? state.AvatarId : avatarId;
                var nextStatus = string.IsNullOrWhiteSpace(releaseStatus)
                    ? state.ReleaseStatus
                    : releaseStatus.ToLowerInvariant();
                if (string.Equals(state.AvatarId, nextAvatarId, StringComparison.Ordinal) &&
                    string.Equals(state.ReleaseStatus, nextStatus, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                state.AvatarId = nextAvatarId;
                state.ReleaseStatus = nextStatus;
                WriteFile(file);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"friend avatar image state {_path}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Records the current avatar for every user in a friend-list response.
    /// One file write covers the whole refresh; unchanged avatars are ignored.
    /// </summary>
    public void ObserveAll(string? accountId, IEnumerable<VrcUser> users, DateTimeOffset? seenAt = null)
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
                if (!file.Accounts.TryGetValue(accountId, out var account) || account is null)
                {
                    account = new StoredAccount();
                    file.Accounts[accountId] = account;
                }

                var changed = false;
                foreach (var user in users)
                {
                    if (user is null || string.IsNullOrWhiteSpace(user.Id))
                    {
                        continue;
                    }

                    changed |= ObserveLocked(
                        account,
                        user.Id,
                        user.CurrentAvatar,
                        name: null,
                        user.CurrentAvatarImageUrl,
                        user.CurrentAvatarThumbnailImageUrl,
                        releaseStatus: null,
                        seenAt ?? DateTimeOffset.UtcNow,
                        isCurrent: true);
                }

                if (changed)
                {
                    WriteFile(file);
                }
            }
            catch (Exception ex)
            {
                // History is useful, never important enough to interrupt a refresh.
                Debug.WriteLine($"friend avatar observe {_path}: {ex.Message}");
            }
        }
    }

    /// <summary>Records one user's current avatar and returns the merged row.</summary>
    public FriendAvatar? Observe(
        string? accountId,
        string userId,
        string? avatarId,
        string? name,
        string? imageUrl,
        string? thumbnailUrl,
        string? releaseStatus = null,
        DateTimeOffset? seenAt = null)
    {
        if (string.IsNullOrWhiteSpace(accountId) ||
            string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(avatarId))
        {
            return null;
        }

        lock (_sync)
        {
            try
            {
                var file = LoadFile();
                if (!file.Accounts.TryGetValue(accountId, out var account) || account is null)
                {
                    account = new StoredAccount();
                    file.Accounts[accountId] = account;
                }

                var changed = ObserveLocked(
                    account,
                    userId,
                    avatarId,
                    name,
                    imageUrl,
                    thumbnailUrl,
                    releaseStatus,
                    seenAt ?? DateTimeOffset.UtcNow,
                    isCurrent: true);
                if (changed)
                {
                    WriteFile(file);
                }

                var entry = ReadAccount(file, accountId, userId)
                    .FirstOrDefault(candidate => string.Equals(candidate.AvatarId, avatarId, StringComparison.Ordinal));
                return entry is null ? null : ToModel(entry);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"friend avatar observe {_path}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Adds the full details resolved from GET /avatars/{id}.</summary>
    public void RememberDetails(string? accountId, string userId, Avatar avatar, bool isCurrent)
    {
        if (string.IsNullOrWhiteSpace(accountId) ||
            string.IsNullOrWhiteSpace(userId) ||
            avatar is null ||
            string.IsNullOrWhiteSpace(avatar.Id))
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                var file = LoadFile();
                if (!file.Accounts.TryGetValue(accountId, out var account) || account is null)
                {
                    account = new StoredAccount();
                    file.Accounts[accountId] = account;
                }

                var changed = ObserveLocked(
                    account,
                    userId,
                    avatar.Id,
                    avatar.Name,
                    avatar.ImageUrl,
                    string.IsNullOrWhiteSpace(avatar.ThumbnailImageUrl)
                        ? avatar.ImageUrl
                        : avatar.ThumbnailImageUrl,
                    avatar.ReleaseStatus,
                    DateTimeOffset.UtcNow,
                    isCurrent);
                if (changed)
                {
                    WriteFile(file);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"friend avatar details {_path}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Marks an avatar whose record VRChat refused as private. The user payload
    /// still has its thumbnail, so the profile can keep showing the picture.
    /// </summary>
    public void MarkUnavailable(string? accountId, string userId, string avatarId)
    {
        if (string.IsNullOrWhiteSpace(accountId) ||
            string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(avatarId))
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                var file = LoadFile();
                if (!file.Accounts.TryGetValue(accountId, out var account) || account is null)
                {
                    return;
                }

                var entry = Find(account, userId, avatarId);
                if (entry is null ||
                    string.Equals(entry.ReleaseStatus, "private", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                entry.ReleaseStatus = "private";
                WriteFile(file);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"friend avatar privacy {_path}: {ex.Message}");
            }
        }
    }

    private static bool ObserveLocked(
        StoredAccount account,
        string userId,
        string? avatarId,
        string? name,
        string? imageUrl,
        string? thumbnailUrl,
        string? releaseStatus,
        DateTimeOffset seenAt,
        bool isCurrent)
    {
        if (string.IsNullOrWhiteSpace(avatarId))
        {
            return false;
        }

        if (!account.Users.TryGetValue(userId, out var avatars) || avatars is null)
        {
            avatars = new List<StoredFriendAvatar>();
            account.Users[userId] = avatars;
        }

        var normalizedThumbnail = ImageUrlHelper.ToThumbnailUrl(
            string.IsNullOrWhiteSpace(thumbnailUrl) ? imageUrl : thumbnailUrl,
            256);
        var normalizedImage = string.IsNullOrWhiteSpace(imageUrl) ? normalizedThumbnail : imageUrl;
        var entry = avatars.FirstOrDefault(candidate =>
            string.Equals(candidate.AvatarId, avatarId, StringComparison.Ordinal));
        var changed = false;

        if (entry is null)
        {
            entry = new StoredFriendAvatar { AvatarId = avatarId, SeenAt = seenAt };
            avatars.Add(entry);
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(name) &&
            !string.Equals(entry.Name, name, StringComparison.Ordinal))
        {
            entry.Name = name;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(normalizedImage) &&
            !string.Equals(entry.ImageUrl, normalizedImage, StringComparison.Ordinal))
        {
            entry.ImageUrl = normalizedImage;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(normalizedThumbnail) &&
            !string.Equals(entry.ThumbnailUrl, normalizedThumbnail, StringComparison.Ordinal))
        {
            entry.ThumbnailUrl = normalizedThumbnail;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(releaseStatus) &&
            !string.Equals(entry.ReleaseStatus, releaseStatus, StringComparison.OrdinalIgnoreCase))
        {
            entry.ReleaseStatus = releaseStatus.ToLowerInvariant();
            changed = true;
        }

        if (isCurrent)
        {
            foreach (var candidate in avatars)
            {
                var shouldBeCurrent = ReferenceEquals(candidate, entry);
                if (candidate.IsCurrent != shouldBeCurrent)
                {
                    candidate.IsCurrent = shouldBeCurrent;
                    changed = true;
                }
            }

            // Current avatar first, then the most recently discovered history.
            var index = avatars.IndexOf(entry);
            if (index > 0)
            {
                avatars.RemoveAt(index);
                avatars.Insert(0, entry);
                changed = true;
            }
        }

        if (avatars.Count > MaximumPerUser)
        {
            avatars.RemoveRange(MaximumPerUser, avatars.Count - MaximumPerUser);
            changed = true;
        }

        return changed;
    }

    private static StoredFriendAvatar? Find(StoredAccount account, string userId, string avatarId)
        => account.Users.TryGetValue(userId, out var avatars) && avatars is not null
            ? avatars.FirstOrDefault(candidate =>
                string.Equals(candidate.AvatarId, avatarId, StringComparison.Ordinal))
            : null;

    private static IReadOnlyList<StoredFriendAvatar> ReadAccount(
        StoredFile file,
        string accountId,
        string userId)
    {
        if (!file.Accounts.TryGetValue(accountId, out var account) ||
            account is null ||
            !account.Users.TryGetValue(userId, out var avatars) ||
            avatars is null)
        {
            return Array.Empty<StoredFriendAvatar>();
        }

        return avatars
            .Where(avatar => avatar is not null && !string.IsNullOrWhiteSpace(avatar.AvatarId))
            .GroupBy(avatar => avatar.AvatarId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(avatar => avatar.IsCurrent)
            .ThenByDescending(avatar => avatar.SeenAt)
            .Take(MaximumPerUser)
            .ToList();
    }

    private static FriendAvatar ToModel(StoredFriendAvatar stored)
    {
        var image = stored.ImageUrl ?? stored.ThumbnailUrl ?? string.Empty;
        var thumbnail = stored.ThumbnailUrl ?? ImageUrlHelper.ToThumbnailUrl(image, 256) ?? string.Empty;
        return new FriendAvatar
        {
            AvatarId = stored.AvatarId,
            Name = stored.Name ?? string.Empty,
            ImageUrl = image,
            ThumbnailUrl = thumbnail,
            ReleaseStatus = stored.ReleaseStatus ?? string.Empty,
            // Stored private labels came from earlier image/index failures. Let
            // the next profile open confirm them against the avatar record.
            IsPrivacyUnverified = string.Equals(stored.ReleaseStatus, "private", StringComparison.OrdinalIgnoreCase),
            LastSeen = stored.SeenAt,
            IsCurrent = stored.IsCurrent
        };
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcCompanion",
        "friend-avatars.json");

    private StoredFile LoadFile()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new StoredFile();
            }

            var file = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(_path), JsonOptions);
            file ??= new StoredFile();
            file.Accounts ??= new Dictionary<string, StoredAccount>(StringComparer.Ordinal);
            return file;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"friend avatar load {_path}: {ex.Message}");
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

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed class StoredFile
    {
        [JsonPropertyName("accounts")]
        public Dictionary<string, StoredAccount> Accounts { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class StoredAccount
    {
        [JsonPropertyName("users")]
        public Dictionary<string, List<StoredFriendAvatar>> Users { get; set; } =
            new(StringComparer.Ordinal);

        [JsonPropertyName("imageResolutions")]
        public Dictionary<string, StoredImageResolution> ImageResolutions { get; set; } =
            new(StringComparer.Ordinal);
    }

    private sealed class StoredImageResolution
    {
        [JsonPropertyName("avatarId")]
        public string? AvatarId { get; set; }

        [JsonPropertyName("releaseStatus")]
        public string? ReleaseStatus { get; set; }
    }

    private sealed class StoredFriendAvatar
    {
        [JsonPropertyName("avatarId")]
        public string AvatarId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("thumbnailUrl")]
        public string? ThumbnailUrl { get; set; }

        [JsonPropertyName("releaseStatus")]
        public string? ReleaseStatus { get; set; }

        [JsonPropertyName("seenAt")]
        public DateTimeOffset SeenAt { get; set; }

        [JsonPropertyName("isCurrent")]
        public bool IsCurrent { get; set; }
    }
}
