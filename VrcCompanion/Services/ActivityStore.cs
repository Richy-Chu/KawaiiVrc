using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Text.Json.Serialization;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Local activity timeline, keyed by account. This is the same model VRCX uses
/// for its friend feed: locally accumulated events survive relaunches because
/// VRChat has no endpoint that returns a historical activity feed.
/// Writes are atomic and a damaged file is treated as empty.
/// </summary>
public sealed class ActivityStore
{
    public static ActivityStore Shared { get; } = new();

    // VRCX keeps its feed cache at 10k live rows and its feed database grows
    // without a small fixed cap. 20k keeps several days of per-filter history
    // for a busy friends list while keeping each atomic rewrite bounded.
    private const int MaximumEntries = 20_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _biosPath;
    private readonly TimeSpan _feedWriteDelay;

    public ActivityStore(string? path = null, TimeSpan? feedWriteDelay = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
        _feedWriteDelay = feedWriteDelay ?? TimeSpan.FromSeconds(20);
        // Bios change every few seconds while the app runs, so they live in
        // their own small file instead of forcing a multi-megabyte feed rewrite.
        _biosPath = Path.Combine(
            Path.GetDirectoryName(_path) ?? string.Empty,
            Path.GetFileNameWithoutExtension(_path) + ".bios.json");
        if (!string.IsNullOrWhiteSpace(path) || !File.Exists(_path))
        {
            // A custom (test) path stays lazy so tests see an empty store.
            return;
        }

        // Parse the feed once in the background as the app starts so signing in
        // does not pay for it on the UI thread.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            lock (_sync)
            {
                LoadFile();
            }
        });
    }

    public string FilePath => _path;

    public string BiosFilePath => _biosPath;

    public IReadOnlyList<ActivityEntry> Load(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Array.Empty<ActivityEntry>();
        }

        lock (_sync)
        {
            var file = LoadFile();
            if (!file.Accounts.TryGetValue(accountId, out var stored) || stored?.Entries is null)
            {
                return Array.Empty<ActivityEntry>();
            }

            var rows = stored.Entries
                .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Id))
                .GroupBy(entry => entry.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(entry => entry.Timestamp)
                .Take(MaximumEntries)
                .ToList();

            var deduped = DedupeEvents(rows);
            ApplySharedData(stored, deduped);

            var legacyLayout = _fileHadInlineSideData;
            if (deduped.Count == rows.Count && !legacyLayout)
            {
                return deduped;
            }

            // Two older layouts are cleaned up by one rewrite: feeds written
            // before the merge dedupe hold repeats (a rebuilt snapshot stored
            // the same transition again under a new row id), and feeds from
            // before tags and avatar urls were stored once per user/avatar
            // carry a copy of them on every row.
            stored.Entries = deduped;
            _feedDirty = true;
            ScheduleFeedWrite();
            return deduped;
        }
    }

    /// <summary>
    /// Rows carry the trust tags of the friend and the url of the avatar they
    /// changed to, and both repeat on every event for the same person. They are
    /// stored once per account and put back on the rows here; a row that already
    /// has its own copy (a feed written by an older version) keeps it, which is
    /// what the next write folds into the maps.
    /// </summary>
    private static void ApplySharedData(StoredAccount account, List<ActivityEntry> rows)
    {
        if (account.UserTags is null && account.AvatarUrls is null)
        {
            return;
        }

        foreach (var row in rows)
        {
            if (row is null)
            {
                continue;
            }

            if (account.UserTags is not null &&
                row.Tags is not { Count: > 0 } &&
                !string.IsNullOrWhiteSpace(row.UserId) &&
                account.UserTags.TryGetValue(row.UserId, out var tags))
            {
                // Shared, not copied: a few hundred friends share these lists
                // and the rows only ever read them.
                row.Tags = tags;
            }

            if (account.AvatarUrls is not null && string.IsNullOrWhiteSpace(row.AvatarUrl))
            {
                // Keyed by the avatar when the row names one, otherwise by the
                // friend: most events carry the friend's picture without their
                // avatar id.
                if (!string.IsNullOrWhiteSpace(row.AvatarId) &&
                    account.AvatarUrls.TryGetValue(row.AvatarId, out var avatarUrl))
                {
                    row.AvatarUrl = avatarUrl;
                }
                else if (!string.IsNullOrWhiteSpace(row.UserId) &&
                    account.AvatarUrls.TryGetValue(row.UserId, out var userUrl))
                {
                    row.AvatarUrl = userUrl;
                }
            }
        }
    }

    /// <summary>
    /// Event identity independent of the row id, matching the merge rule in the
    /// feed view model: one type, one user, one event time.
    /// </summary>
    private static string EventKey(ActivityEntry entry)
        => $"{(int)entry.Type}|{entry.UserId}|{entry.Timestamp.UtcTicks}";

    /// <summary>
    /// Keeps one row per event. The copy carrying the avatar id or the most
    /// detail wins, because repeats were not always written with the same fields.
    /// </summary>
    private static List<ActivityEntry> DedupeEvents(List<ActivityEntry> rows)
    {
        var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<ActivityEntry>(rows.Count);
        foreach (var entry in rows)
        {
            // Only a row that names a person and an event time can be matched.
            if (string.IsNullOrWhiteSpace(entry.UserId) || entry.Timestamp == default)
            {
                kept.Add(entry);
                continue;
            }

            var key = EventKey(entry);
            if (indexByKey.TryGetValue(key, out var index))
            {
                if (IsRicher(entry, kept[index]))
                {
                    kept[index] = entry;
                }

                continue;
            }

            indexByKey[key] = kept.Count;
            kept.Add(entry);
        }

        return kept;
    }

    private static bool IsRicher(ActivityEntry candidate, ActivityEntry current)
    {
        var candidateHasAvatar = !string.IsNullOrWhiteSpace(candidate.AvatarId);
        var currentHasAvatar = !string.IsNullOrWhiteSpace(current.AvatarId);
        if (candidateHasAvatar != currentHasAvatar)
        {
            return candidateHasAvatar;
        }

        return (candidate.Details?.Length ?? 0) > (current.Details?.Length ?? 0);
    }

    public void Save(string? accountId, IEnumerable<ActivityEntry> entries)
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
                if (!file.Accounts.TryGetValue(accountId, out var stored) || stored is null)
                {
                    stored = new StoredAccount();
                    file.Accounts[accountId] = stored;
                }

                // Callers hand over the feed newest first. Only the id pass and
                // the cap happen here: sorting and normalising a 20k row feed
                // used to run on the caller's thread, which is the UI thread on
                // every presence event. The writer does that work instead.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var rows = new List<ActivityEntry>();
                foreach (var entry in entries)
                {
                    if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || !seen.Add(entry.Id))
                    {
                        continue;
                    }

                    rows.Add(entry);
                }

                if (rows.Count > MaximumEntries)
                {
                    rows.RemoveRange(MaximumEntries, rows.Count - MaximumEntries);
                }

                stored.Entries = rows;
                // Memory is updated inline so the next read (another view, a
                // restart test) sees the rows immediately. The file is written
                // on a trailing timer: an import or a burst of presence events
                // used to rewrite the whole multi-megabyte feed for every row.
                _feedDirty = true;
                ScheduleFeedWrite();
            }
            catch (Exception ex)
            {
                // Losing history must never take the activity feed down.
                Debug.WriteLine($"activity store save {_path}: {ex.Message}");
            }
        }
    }

    private readonly Dictionary<string, StoredFile> _pendingWrites = new(StringComparer.Ordinal);
    private bool _writing;
    private Task _writeTask = Task.CompletedTask;
    private Timer? _feedTimer;
    private bool _feedDirty;
    private Timer? _biosTimer;
    private bool _biosDirty;

    /// <summary>
    /// Bio baselines are only read when the account loads, so a minute between
    /// writes is plenty; the timer folds every check in that window into one.
    /// </summary>
    private static readonly TimeSpan BiosWriteDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Arms one write for the newest feed snapshot. Rows that arrive while a
    /// write is pending are folded into that same write, so a log import or a
    /// burst of presence events costs one file write instead of one per row.
    /// </summary>
    private void ScheduleFeedWrite()
    {
        if (_feedTimer is not null)
        {
            return;
        }

        _feedTimer = new Timer(_ => FlushPendingFeed(), null, _feedWriteDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes the newest feed snapshot now, for shutdown and for tests.</summary>
    private void FlushPendingFeed()
    {
        Timer? timer;
        lock (_sync)
        {
            timer = _feedTimer;
            _feedTimer = null;
            if (_feedDirty)
            {
                _feedDirty = false;
                // Queue under the lock so a concurrent FlushAsync cannot see an
                // empty queue before this snapshot reaches the writer.
                QueueWrite(_path, SnapshotFeed());
            }
        }

        timer?.Dispose();
    }

    /// <summary>Writes the newest bio baselines now, for shutdown and for tests.</summary>
    private void FlushPendingBios()
    {
        Timer? timer;
        lock (_sync)
        {
            timer = _biosTimer;
            _biosTimer = null;
            if (_biosDirty)
            {
                _biosDirty = false;
                var snapshot = new StoredFile();
                foreach (var (id, account) in BiosAccounts())
                {
                    snapshot.Accounts[id] = account;
                }

                QueueWrite(_biosPath, snapshot);
            }
        }

        timer?.Dispose();
    }

    private StoredFile SnapshotFeed()
    {
        var file = LoadFile();
        var snapshot = new StoredFile();
        foreach (var (id, account) in file.Accounts)
        {
            if (account is not null)
            {
                snapshot.Accounts[id] = Normalize(account);
            }
        }

        return snapshot;
    }

    /// <summary>
    /// The account as it is written: tags and avatar urls lifted into one map
    /// each instead of a copy on every row. Rows without either are written as
    /// they are, and the rows in memory are left alone, so the feed on screen
    /// keeps its badges and thumbnails.
    /// </summary>
    private static StoredAccount Normalize(StoredAccount account)
    {
        if (account.Entries is not { Count: > 0 } raw)
        {
            return new StoredAccount { Bios = account.Bios };
        }

        // Ordered here, on the writer thread, so the caller never pays for it.
        var entries = raw
            .Where(entry => entry is not null)
            .OrderByDescending(entry => entry.Timestamp)
            .Take(MaximumEntries)
            .ToList();

        Dictionary<string, List<string>>? userTags = null;
        Dictionary<string, string>? avatarUrls = null;
        var rows = new List<ActivityEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            var tags = entry.Tags is { Count: > 0 } && !string.IsNullOrWhiteSpace(entry.UserId)
                ? entry.Tags
                : null;
            // Keyed by the avatar when the row names one, otherwise by the
            // friend, because most events carry the friend's picture URL without
            // their avatar id. A row with neither keeps its own copy.
            var urlKey = string.IsNullOrWhiteSpace(entry.AvatarUrl)
                ? null
                : !string.IsNullOrWhiteSpace(entry.AvatarId)
                    ? entry.AvatarId
                    : !string.IsNullOrWhiteSpace(entry.UserId)
                        ? entry.UserId
                        : null;
            if (tags is null && urlKey is null)
            {
                rows.Add(entry);
                continue;
            }

            userTags ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
            avatarUrls ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (tags is not null)
            {
                // Rows are newest first, so the first copy of a person is the
                // one that still matches their current tags.
                userTags.TryAdd(entry.UserId, tags);
            }

            if (urlKey is not null)
            {
                avatarUrls.TryAdd(urlKey, entry.AvatarUrl);
            }

            var copy = entry.ShallowCopy();
            if (tags is not null)
            {
                copy.Tags = null;
            }

            if (urlKey is not null)
            {
                copy.AvatarUrl = string.Empty;
            }

            rows.Add(copy);
        }

        return new StoredAccount
        {
            Entries = rows,
            Bios = account.Bios,
            UserTags = userTags,
            AvatarUrls = avatarUrls
        };
    }

    /// <summary>
    /// Queues the newest snapshot for one file, coalescing bursts. Serialisation
    /// happens on the writer thread: a feed snapshot is several megabytes and the
    /// caller is usually the UI thread handling a timer tick or a pipeline event.
    /// </summary>
    private void QueueWrite(string path, StoredFile file)
    {
        lock (_sync)
        {
            _pendingWrites[path] = file;
            if (_writing)
            {
                return;
            }

            _writing = true;
            _writeTask = Task.Run(WriteLoop);
        }
    }

    private void WriteLoop()
    {
        while (true)
        {
            KeyValuePair<string, StoredFile>[] batch;
            lock (_sync)
            {
                if (_pendingWrites.Count == 0)
                {
                    _writing = false;
                    return;
                }

                batch = _pendingWrites.ToArray();
                _pendingWrites.Clear();
            }

            foreach (var (path, file) in batch)
            {
                WriteJson(path, file);
            }
        }
    }

    /// <summary>Completes once every queued write has reached its file.</summary>
    public async Task FlushAsync()
    {
        FlushPendingFeed();
        FlushPendingBios();

        while (true)
        {
            Task pending;
            lock (_sync)
            {
                if (!_writing && _pendingWrites.Count == 0)
                {
                    return;
                }

                pending = _writeTask;
            }

            await pending.ConfigureAwait(false);
        }
    }

    private void WriteJson(string path, StoredFile file)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            AtomicFile.WriteJson(path, file, JsonOptions);

            if (!string.Equals(path, _path, StringComparison.Ordinal))
            {
                return;
            }

            lock (_sync)
            {
                var info = new FileInfo(_path);
                _cachedLength = info.Exists ? info.Length : -1;
                _cachedWritten = info.Exists ? info.LastWriteTimeUtc : default;
                // What was just written has the shared maps, not per-row copies.
                _fileHadInlineSideData = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"activity store write {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Last bio observed for each friend. This is the baseline VRCX keeps so a
    /// bio edit made while the app was closed still becomes a Bio feed row the
    /// first time the profile is checked after launch.
    /// </summary>
    public IReadOnlyDictionary<string, BioBaseline> LoadBioBaselines(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return new Dictionary<string, BioBaseline>(StringComparer.Ordinal);
        }

        lock (_sync)
        {
            if (BiosAccounts().TryGetValue(accountId, out var stored) &&
                stored?.Bios is { Count: > 0 } baselines)
            {
                // The migrated copy is authoritative; drop the stale one the feed
                // still carries so the next feed snapshot stops shipping it.
                if (LoadFile().Accounts.TryGetValue(accountId, out var feedAccount) &&
                    feedAccount is not null)
                {
                    feedAccount.Bios = null;
                }

                return CopyBaselines(baselines);
            }

            // Reports from before bios moved into their own file kept them inside
            // the feed. Read those once; the next bio save migrates them.
            var file = LoadFile();
            if (!file.Accounts.TryGetValue(accountId, out var legacy) || legacy?.Bios is null)
            {
                return new Dictionary<string, BioBaseline>(StringComparer.Ordinal);
            }

            return CopyBaselines(legacy.Bios);
        }
    }

    public void SaveBioBaselines(
        string? accountId,
        IReadOnlyDictionary<string, BioBaseline> baselines)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                var accounts = BiosAccounts();
                accounts[accountId] = new StoredAccount { Bios = CopyBaselines(baselines) };

                _biosDirty = true;
                if (_biosTimer is null)
                {
                    _biosTimer = new Timer(_ => FlushPendingBios(), null, BiosWriteDelay, Timeout.InfiniteTimeSpan);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"activity bio baseline save {_path}: {ex.Message}");
            }
        }
    }

    private static string DefaultPath() => UserDataPaths.Combine("activity.json");

    // The feed file is ~14 MB once it holds months of rows; parsing it for every
    // read made opening Activity and every bio poll cost hundreds of ms on the
    // UI thread. The parsed copy is kept until the file itself changes.
    private StoredFile? _cachedFile;
    private long _cachedLength = -1;
    private DateTime _cachedWritten;

    /// <summary>
    /// True while the file on disk still carries tags and avatar urls on the
    /// rows. Loading one rewrites it once into the shared maps; the flag is
    /// cleared by that write so launches after it stay read-only.
    /// </summary>
    private bool _fileHadInlineSideData;

    private Dictionary<string, StoredAccount>? _biosAccounts;

    /// <summary>
    /// The bio baselines, loaded once from their own file. They are rewritten on
    /// every bio check while the app runs, which is why they are not part of the
    /// multi-megabyte feed snapshot.
    /// </summary>
    private Dictionary<string, StoredAccount> BiosAccounts()
    {
        if (_biosAccounts is not null)
        {
            return _biosAccounts;
        }

        var accounts = new Dictionary<string, StoredAccount>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_biosPath))
            {
                var file = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(_biosPath), JsonOptions);
                if (file?.Accounts is not null)
                {
                    foreach (var (id, account) in file.Accounts)
                    {
                        if (!string.IsNullOrWhiteSpace(id) && account?.Bios is not null)
                        {
                            accounts[id] = account;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"activity bio baselines {_biosPath}: {ex.Message}");
        }

        return _biosAccounts = accounts;
    }

    private static Dictionary<string, BioBaseline> CopyBaselines(
        IReadOnlyDictionary<string, BioBaseline> baselines)
        => baselines
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);

    private StoredFile LoadFile()
    {
        try
        {
            if (!File.Exists(_path))
            {
                // Keep one empty instance so a save updates what the next read
                // sees, even before the file exists on disk.
                _cachedFile ??= new StoredFile();
                _cachedLength = -1;
                _cachedWritten = default;
                _fileHadInlineSideData = false;
                return _cachedFile;
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
            file.Accounts ??= new Dictionary<string, StoredAccount>(StringComparer.Ordinal);
            _cachedFile = file;
            _cachedLength = info.Length;
            _cachedWritten = info.LastWriteTimeUtc;
            _fileHadInlineSideData = file.Accounts.Values.Any(account =>
                account?.Entries is { } entries && entries.Any(entry =>
                    entry is not null &&
                    (entry.Tags is { Count: > 0 } || !string.IsNullOrWhiteSpace(entry.AvatarUrl))));
            return file;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"activity store load {_path}: {ex.Message}");
            return new StoredFile();
        }
    }



    private sealed class StoredFile
    {
        [JsonPropertyName("accounts")]
        public Dictionary<string, StoredAccount> Accounts { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class StoredAccount
    {
        [JsonPropertyName("entries")]
        public List<ActivityEntry>? Entries { get; set; }

        /// <summary>Trust tags once per friend instead of on every row.</summary>
        [JsonPropertyName("userTags")]
        public Dictionary<string, List<string>>? UserTags { get; set; }

        /// <summary>Avatar image url once per avatar instead of on every row.</summary>
        [JsonPropertyName("avatarUrls")]
        public Dictionary<string, string>? AvatarUrls { get; set; }

        [JsonPropertyName("bios")]
        public Dictionary<string, BioBaseline>? Bios { get; set; }
    }
}

public sealed class BioBaseline
{
    [JsonPropertyName("bio")]
    public string? Bio { get; set; }

    [JsonPropertyName("checkedAt")]
    public DateTimeOffset CheckedAt { get; set; }
}
