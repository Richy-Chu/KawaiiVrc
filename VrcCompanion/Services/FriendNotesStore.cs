using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcCompanion.Services;

/// <summary>A private note about one friend, stored only on this PC.</summary>
public sealed record FriendNote(string UserId, string Text, DateTime UpdatedAt);

/// <summary>
/// Notes keyed by VRChat user id in %LOCALAPPDATA%\VrcCompanion\friend-notes.json.
/// VRChat's public API has no note endpoint, so nothing here is sent to VRChat.
/// Writes are atomic and a damaged file is treated as empty instead of failing.
/// </summary>
public sealed class FriendNotesStore
{
    public static FriendNotesStore Shared { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly Dictionary<string, FriendNote> _notes;

    public FriendNotesStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
        _notes = Load();
    }

    public string FilePath => _path;

    public FriendNote? Get(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        lock (_sync)
        {
            return _notes.TryGetValue(userId, out var note) ? note : null;
        }
    }

    /// <summary>Saves a note, or removes it when the text is blank. Returns the saved note.</summary>
    public FriendNote? Set(string? userId, string? text)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var trimmed = text?.Trim() ?? string.Empty;
        FriendNote? saved;

        lock (_sync)
        {
            if (trimmed.Length == 0)
            {
                _notes.Remove(userId);
                saved = null;
            }
            else
            {
                saved = new FriendNote(userId, trimmed, DateTime.UtcNow);
                _notes[userId] = saved;
            }

            SaveLocked();
        }

        return saved;
    }

    private static string DefaultPath() => UserDataPaths.Combine("friend-notes.json");

    private Dictionary<string, FriendNote> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, FriendNote>(StringComparer.Ordinal);
            }

            var json = File.ReadAllText(_path);
            var stored = JsonSerializer.Deserialize<Dictionary<string, StoredNote>>(json, JsonOptions);
            if (stored is null)
            {
                return new Dictionary<string, FriendNote>(StringComparer.Ordinal);
            }

            var notes = new Dictionary<string, FriendNote>(StringComparer.Ordinal);
            foreach (var (userId, note) in stored)
            {
                if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(note.Text))
                {
                    notes[userId] = new FriendNote(userId, note.Text, note.UpdatedAt);
                }
            }

            return notes;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"friend notes load {_path}: {ex.Message}");
            return new Dictionary<string, FriendNote>(StringComparer.Ordinal);
        }
    }

    private void SaveLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = _notes.ToDictionary(
                pair => pair.Key,
                pair => new StoredNote { Text = pair.Value.Text, UpdatedAt = pair.Value.UpdatedAt },
                StringComparer.Ordinal);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception ex)
        {
            // A failed note write must never take the app down.
            Debug.WriteLine($"friend notes save {_path}: {ex.Message}");
        }
    }

    private sealed class StoredNote
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("updatedAt")]
        public DateTime UpdatedAt { get; set; }
    }
}
