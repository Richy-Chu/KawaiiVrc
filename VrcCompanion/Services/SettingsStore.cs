using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcCompanion.Services;

/// <summary>Everything the app remembers between launches (never credentials).</summary>
public sealed class CompanionSettings
{
    [JsonPropertyName("backdropStyle")]
    public string BackdropStyle { get; set; } = "Aurora";

    [JsonPropertyName("backdropImagePath")]
    public string? BackdropImagePath { get; set; }

    [JsonPropertyName("animateBackdrop")]
    public bool AnimateBackdrop { get; set; } = true;

    [JsonPropertyName("backdropDim")]
    public double BackdropDim { get; set; } = 0.22;

    [JsonPropertyName("favoriteFriends")]
    public List<string> FavoriteFriends { get; set; } = new();

    [JsonPropertyName("favoriteWorlds")]
    public List<string> FavoriteWorlds { get; set; } = new();

    // --- New feature settings ---

    [JsonPropertyName("favoriteAvatars")]
    public List<string> FavoriteAvatars { get; set; } = new();

    [JsonPropertyName("favoriteGroups")]
    public List<string> FavoriteGroups { get; set; } = new();

    [JsonPropertyName("activityDefaultFilter")]
    public string ActivityDefaultFilter { get; set; } = "all";

    [JsonPropertyName("activityShowOfflineFriends")]
    public bool ActivityShowOfflineFriends { get; set; } = true;

    [JsonPropertyName("lastActiveAccountId")]
    public string? LastActiveAccountId { get; set; }

    [JsonPropertyName("backgroundPlaylist")]
    public List<string> BackgroundPlaylist { get; set; } = new();

    [JsonPropertyName("launchMode")]
    public string LaunchMode { get; set; } = "vr";

    [JsonPropertyName("profileBackdropImagePath")]
    public string? ProfileBackdropImagePath { get; set; }

    [JsonPropertyName("oscHost")]
    public string OscHost { get; set; } = "127.0.0.1";

    [JsonPropertyName("oscPort")]
    public int OscPort { get; set; } = 9000;

    [JsonPropertyName("chatboxRotationSeconds")]
    public int ChatboxRotationSeconds { get; set; } = 15;

    [JsonPropertyName("chatboxRotateEnabled")]
    public bool ChatboxRotateEnabled { get; set; }

    [JsonPropertyName("chatboxMessages")]
    public List<string> ChatboxMessages { get; set; } = new();

    [JsonPropertyName("chatboxIncludeNowPlaying")]
    public bool ChatboxIncludeNowPlaying { get; set; }

    [JsonPropertyName("chatboxShowLyrics")]
    public bool ChatboxShowLyrics { get; set; }

    [JsonPropertyName("chatboxLyricsOnVrchat")]
    public bool ChatboxLyricsOnVrchat { get; set; }

    [JsonPropertyName("chatboxPauseLyricsAfterMessage")]
    public bool ChatboxPauseLyricsAfterMessage { get; set; } = true;

    [JsonPropertyName("chatboxLyricsPauseSeconds")]
    public int ChatboxLyricsPauseSeconds { get; set; } = 30;

    [JsonPropertyName("chatboxHistory")]
    public List<string> ChatboxHistory { get; set; } = new();

    [JsonPropertyName("chatboxLiveEditEnabled")]
    public bool ChatboxLiveEditEnabled { get; set; }
}

/// <summary>
/// Small JSON settings file under %LOCALAPPDATA%\VrcCompanion. Writes are
/// atomic and failures are swallowed so a damaged file never blocks startup.
/// </summary>
public sealed class SettingsStore
{
    public static SettingsStore Shared { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private readonly string _path;

    public SettingsStore()
        : this(null)
    {
    }

    public SettingsStore(string? path)
    {
        if (path is null)
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VrcCompanion");
            _path = Path.Combine(directory, "settings.json");
        }
        else
        {
            _path = path;
        }

        Settings = Load();
    }

    public CompanionSettings Settings { get; }

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Settings, JsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch
            {
                // Losing a theme preference is not worth crashing over.
            }
        }
    }

    private CompanionSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new CompanionSettings();
            }

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<CompanionSettings>(json, JsonOptions)
                   ?? new CompanionSettings();
        }
        catch
        {
            return new CompanionSettings();
        }
    }
}
