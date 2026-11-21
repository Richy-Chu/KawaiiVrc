using System.Diagnostics;
using System.IO;
using System.Text.Json;
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

    public ActivityStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
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
                // restart test) sees the rows immediately; the 14 MB disk write
                // happens on a worker thread instead of the UI thread.
                QueueWrite(file);
            }
            catch (Exception ex)
            {
                // Losing history must never take the activity feed down.
                Debug.WriteLine($"activity store save {_path}: {ex.Message}");
            }
        }
    }

    private string? _pendingJson;
    private bool _writing;
    private Task _writeTask = Task.CompletedTask;

    /// <summary>Serialises the newest snapshot off-thread, coalescing bursts.</summary>
    private void QueueWrite(StoredFile file)
    {
        _pendingJson = JsonSerializer.Serialize(file, JsonOptions);
        if (_writing)
        {
            return;
        }

        _writing = true;
        _writeTask = Task.Run(async () =>
        {
            while (true)
            {
                string? json;
                lock (_sync)
                {
                    json = _pendingJson;
                    _pendingJson = null;
                    if (json is null)
                    {
                        _writing = false;
                        return;
                    }
                }

                WriteJson(json);
            }
        });
    }

    /// <summary>Completes once every queued write has reached the file.</summary>
    public async Task FlushAsync()
    {
        while (true)
        {
            Task pending;
            lock (_sync)
            {
                if (!_writing && _pendingJson is null)
                {
                    return;
                }

                pending = _writeTask;
            }

            await pending.ConfigureAwait(false);
        }
    }

    private void WriteJson(string json)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            AtomicFile.WriteAllText(_path, json);

            lock (_sync)
            {
                var info = new FileInfo(_path);
                _cachedLength = info.Exists ? info.Length : -1;
                _cachedWritten = info.Exists ? info.LastWriteTimeUtc : default;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"activity store write {_path}: {ex.Message}");
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
            var file = LoadFile();
            if (!file.Accounts.TryGetValue(accountId, out var stored) || stored?.Bios is null)
            {
                return new Dictionary<string, BioBaseline>(StringComparer.Ordinal);
            }

            return stored.Bios
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
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
                var file = LoadFile();
                if (!file.Accounts.TryGetValue(accountId, out var stored) || stored is null)
                {
                    stored = new StoredAccount();
                    file.Accounts[accountId] = stored;
                }

                stored.Bios = baselines
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().Value,
                        StringComparer.Ordinal);
                QueueWrite(file);
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
