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
    // without a small fixed cap. 20k keeps months of per-filter history for a
    // large friends list while keeping each atomic rewrite bounded.
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

            return stored.Entries
                .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Id))
                .GroupBy(entry => entry.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(entry => entry.Timestamp)
                .Take(MaximumEntries)
                .ToList();
        }
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

                stored.Entries = entries
                    .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Id))
                    .GroupBy(entry => entry.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderByDescending(entry => entry.Timestamp)
                    .Take(MaximumEntries)
                    .ToList();
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
        StoredFile? snapshot = null;
        lock (_sync)
        {
            timer = _feedTimer;
            _feedTimer = null;
            if (_feedDirty)
            {
                _feedDirty = false;
                snapshot = SnapshotFeed();
            }
        }

        timer?.Dispose();
        if (snapshot is not null)
        {
            QueueWrite(_path, snapshot);
        }
    }

    /// <summary>Writes the newest bio baselines now, for shutdown and for tests.</summary>
    private void FlushPendingBios()
    {
        Timer? timer;
        StoredFile? snapshot = null;
        lock (_sync)
        {
            timer = _biosTimer;
            _biosTimer = null;
            if (_biosDirty)
            {
                _biosDirty = false;
                snapshot = new StoredFile();
                foreach (var (id, account) in BiosAccounts())
                {
                    snapshot.Accounts[id] = account;
                }
            }
        }

        timer?.Dispose();
        if (snapshot is not null)
        {
            QueueWrite(_biosPath, snapshot);
        }
    }

    private StoredFile SnapshotFeed()
    {
        var file = LoadFile();
        var snapshot = new StoredFile();
        foreach (var (id, account) in file.Accounts)
        {
            snapshot.Accounts[id] = account;
        }

        return snapshot;
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

            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));

            if (!string.Equals(path, _path, StringComparison.Ordinal))
            {
                return;
            }

            lock (_sync)
            {
                var info = new FileInfo(_path);
                _cachedLength = info.Exists ? info.Length : -1;
                _cachedWritten = info.Exists ? info.LastWriteTimeUtc : default;
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

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcCompanion",
        "activity.json");

    // The feed file is ~14 MB once it holds months of rows; parsing it for every
    // read made opening Activity and every bio poll cost hundreds of ms on the
    // UI thread. The parsed copy is kept until the file itself changes.
    private StoredFile? _cachedFile;
    private long _cachedLength = -1;
    private DateTime _cachedWritten;

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
