using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>Everything the app remembers between launches. Provider tokens are protected with Windows DPAPI.</summary>
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

    /// <summary>Sort key for the Uploaded Avatars tab (date-added, updated, name, recent, performance).</summary>
    [JsonPropertyName("uploadedAvatarSort")]
    public string UploadedAvatarSort { get; set; } = "date-added";

    /// <summary>Sort key for the Favourites tab.</summary>
    [JsonPropertyName("favoriteAvatarSort")]
    public string FavoriteAvatarSort { get; set; } = "date-added";

    /// <summary>Most recently worn avatar ids, newest first. VRChat exposes no last-used time.</summary>
    [JsonPropertyName("recentAvatarIds")]
    public List<string> RecentAvatarIds { get; set; } = new();

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

    [JsonPropertyName("launchProfiles")]
    public List<LaunchProfile> LaunchProfiles { get; set; } = new();

    [JsonPropertyName("activeLaunchProfileId")]
    public string? ActiveLaunchProfileId { get; set; }

    [JsonPropertyName("browserAdBlockEnabled")]
    public bool BrowserAdBlockEnabled { get; set; } = true;

    /// <summary>When on, labels and messages can be highlighted and copied like web text.</summary>
    [JsonPropertyName("enableTextSelection")]
    public bool EnableTextSelection { get; set; }

    /// <summary>
    /// Light-resource presentation. Null means "decide from this machine's
    /// render tier", so older PCs start light without the user finding a setting.
    /// </summary>
    [JsonPropertyName("liteMode")]
    public bool? LiteMode { get; set; }

    [JsonPropertyName("braveShields")]
    public BraveShieldsPreferences BraveShields { get; set; } = new();

    [JsonPropertyName("profileBackdropImagePath")]
    public string? ProfileBackdropImagePath { get; set; }

    [JsonPropertyName("oscHost")]
    public string OscHost { get; set; } = "127.0.0.1";

    [JsonPropertyName("oscPort")]
    public int OscPort { get; set; } = 9000;

    [JsonPropertyName("chatboxOscEnabled")]
    public bool ChatboxOscEnabled { get; set; } = true;

    [JsonPropertyName("chatboxStatusItems")]
    public List<ChatboxStatusItem> ChatboxStatusItems { get; set; } = new();

    [JsonPropertyName("chatboxStatusGroups")]
    public List<ChatboxStatusGroup> ChatboxStatusGroups { get; set; } = new();

    [JsonPropertyName("chatboxProviderOrder")]
    public List<string> ChatboxProviderOrder { get; set; } = new()
    {
        "Status", "Media", "Lyrics", "Spotify", "Window", "VrcRadar", "HeartRate",
        "Discord", "Twitch", "TikTokLive", "TrackerBattery", "VrPerformance",
        "ComponentStats", "Soundpad", "Voicemod", "Network", "Weather", "Time"
    };

    [JsonPropertyName("chatboxIntegrationEnabled")]
    public Dictionary<string, bool> ChatboxIntegrationEnabled { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("chatboxIntegrationVr")]
    public Dictionary<string, bool> ChatboxIntegrationVr { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("chatboxIntegrationDesktop")]
    public Dictionary<string, bool> ChatboxIntegrationDesktop { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("chatboxWeatherCity")]
    public string ChatboxWeatherCity { get; set; } = "London";

    [JsonPropertyName("chatboxPulsoidToken")]
    [JsonConverter(typeof(DpapiStringJsonConverter))]
    public string ChatboxPulsoidToken { get; set; } = string.Empty;

    [JsonPropertyName("chatboxTwitchClientId")]
    public string ChatboxTwitchClientId { get; set; } = string.Empty;

    [JsonPropertyName("chatboxTwitchAccessToken")]
    [JsonConverter(typeof(DpapiStringJsonConverter))]
    public string ChatboxTwitchAccessToken { get; set; } = string.Empty;

    [JsonPropertyName("chatboxTwitchChannel")]
    public string ChatboxTwitchChannel { get; set; } = string.Empty;

    [JsonPropertyName("chatboxTikTokUsername")]
    public string ChatboxTikTokUsername { get; set; } = string.Empty;

    [JsonPropertyName("chatboxTikTokFormat")]
    public string ChatboxTikTokFormat { get; set; } = "TikTok @{profile} | {followers} followers";

    [JsonPropertyName("chatboxSpotifyFormat")]
    public string ChatboxSpotifyFormat { get; set; } = "▶ {display}{source}";

    [JsonPropertyName("chatboxWeatherFormat")]
    public string ChatboxWeatherFormat { get; set; } = "{emoji} {temperature}°C";

    [JsonPropertyName("chatboxNetworkFormat")]
    public string ChatboxNetworkFormat { get; set; } = "{network}";

    [JsonPropertyName("chatboxRadarFormat")]
    public string ChatboxRadarFormat { get; set; } = "{world} | 👥 {players}";

    [JsonPropertyName("chatboxComponentFormat")]
    public string ChatboxComponentFormat { get; set; } = "{stats}";

    [JsonPropertyName("chatboxSoundpadFormat")]
    public string ChatboxSoundpadFormat { get; set; } = "🎶 '{sound}'";

    [JsonPropertyName("chatboxDiscordFormat")]
    public string ChatboxDiscordFormat { get; set; } = "🔊 {channel} ({count}) | 🎙 {speaking}";

    [JsonPropertyName("chatboxVrBatteryFormat")]
    public string ChatboxVrBatteryFormat { get; set; } = "{icon} {name} {batt}%";

    [JsonPropertyName("chatboxVrPerformanceFormat")]
    public string ChatboxVrPerformanceFormat { get; set; } = "🎮 {fps} FPS";

    [JsonPropertyName("chatboxHeartRateFormat")]
    public string ChatboxHeartRateFormat { get; set; } = "❤ {bpm} bpm";

    [JsonPropertyName("chatboxTwitchFormat")]
    public string ChatboxTwitchFormat { get; set; } = "{twitch}";

    [JsonPropertyName("chatboxActiveStatusId")]
    public string? ChatboxActiveStatusId { get; set; }

    [JsonPropertyName("chatboxCycleEnabled")]
    public bool ChatboxCycleEnabled { get; set; } = true;

    [JsonPropertyName("chatboxCycleSeconds")]
    public int ChatboxCycleSeconds { get; set; } = 5;

    [JsonPropertyName("chatboxCycleRandom")]
    public bool ChatboxCycleRandom { get; set; }

    [JsonPropertyName("chatboxIncludeStatus")]
    public bool ChatboxIncludeStatus { get; set; } = true;

    [JsonPropertyName("chatboxStatusIconEnabled")]
    public bool ChatboxStatusIconEnabled { get; set; } = true;

    [JsonPropertyName("chatboxStatusIconText")]
    public string ChatboxStatusIconText { get; set; } = "💭";

    [JsonPropertyName("chatboxAfkEnabled")]
    public bool ChatboxAfkEnabled { get; set; }

    [JsonPropertyName("chatboxAfkTimeoutSeconds")]
    public int ChatboxAfkTimeoutSeconds { get; set; } = 300;

    [JsonPropertyName("chatboxAfkPrefix")]
    public string ChatboxAfkPrefix { get; set; } = "💤";

    [JsonPropertyName("chatboxAfkText")]
    public string ChatboxAfkText { get; set; } = "AFK";

    [JsonPropertyName("chatboxIncludeMusic")]
    public bool ChatboxIncludeMusic { get; set; }

    [JsonPropertyName("chatboxIncludeWindowActivity")]
    public bool ChatboxIncludeWindowActivity { get; set; }

    [JsonPropertyName("chatboxIncludeLyrics")]
    public bool ChatboxIncludeLyrics { get; set; }

    [JsonPropertyName("chatboxIncludeTime")]
    public bool ChatboxIncludeTime { get; set; }

    [JsonPropertyName("chatboxLyricsFormat")]
    public string ChatboxLyricsFormat { get; set; } = "♪ {lyric}";

    [JsonPropertyName("chatboxTimeFormat")]
    public string ChatboxTimeFormat { get; set; } = "{time}";

    [JsonPropertyName("chatboxHistory")]
    public List<string> ChatboxHistory { get; set; } = new();

    [JsonPropertyName("chatboxMusicFormat")]
    public string ChatboxMusicFormat { get; set; } = "{state} {display}{source}";

    [JsonPropertyName("chatboxWindowFormat")]
    public string ChatboxWindowFormat { get; set; } = "🪟 {app}";

    [JsonPropertyName("chatboxChatVisibleSeconds")]
    public int ChatboxChatVisibleSeconds { get; set; } = 15;

    [JsonPropertyName("chatboxKeepUpdatingChat")]
    public bool ChatboxKeepUpdatingChat { get; set; } = true;

    [JsonPropertyName("chatboxUpdateRateSeconds")]
    public int ChatboxUpdateRateSeconds { get; set; } = 3;

    [JsonPropertyName("chatboxLiveEdit")]
    public bool ChatboxLiveEdit { get; set; } = true;

    [JsonPropertyName("chatboxRealTimeEdit")]
    public bool ChatboxRealTimeEdit { get; set; } = true;

    [JsonPropertyName("chatboxLiveTyping")]
    public bool ChatboxLiveTyping { get; set; }

    [JsonPropertyName("chatboxLiveTypingRateMs")]
    public int ChatboxLiveTypingRateMs { get; set; } = 1200;

    [JsonPropertyName("chatboxLiveTypingFinalizeMs")]
    public int ChatboxLiveTypingFinalizeMs { get; set; } = 6000;

    [JsonPropertyName("chatboxAutocomplete")]
    public bool ChatboxAutocomplete { get; set; }

    [JsonPropertyName("chatboxAutocompleteMinChars")]
    public int ChatboxAutocompleteMinChars { get; set; } = 4;

    [JsonPropertyName("chatboxAutocompleteMaxWords")]
    public int ChatboxAutocompleteMaxWords { get; set; } = 2;

    [JsonPropertyName("chatboxPrefixChat")]
    public bool ChatboxPrefixChat { get; set; }

    [JsonPropertyName("chatboxChatFx")]
    public bool ChatboxChatFx { get; set; } = true;

    [JsonPropertyName("chatboxSendAgainFx")]
    public bool ChatboxSendAgainFx { get; set; } = true;

    [JsonPropertyName("chatboxSeparator")]
    public string ChatboxSeparator { get; set; } = " ┆ ";

    [JsonPropertyName("chatboxSeparateWithEnters")]
    public bool ChatboxSeparateWithEnters { get; set; }

    [JsonPropertyName("chatboxPrefix")]
    public string ChatboxPrefix { get; set; } = string.Empty;

    [JsonPropertyName("chatboxSuffix")]
    public string ChatboxSuffix { get; set; } = string.Empty;

    [JsonPropertyName("chatboxScanSeconds")]
    public int ChatboxScanSeconds { get; set; } = 1;

    [JsonPropertyName("kawaiiEmoji")]
    public string KawaiiEmoji { get; set; } = "🌸";

    [JsonPropertyName("kawaiiAccent")]
    public string KawaiiAccent { get; set; } = "#5A3796";

    /// <summary>Version the user dismissed with "Later"; a newer release still notifies.</summary>
    [JsonPropertyName("skippedUpdateVersion")]
    public string? SkippedUpdateVersion { get; set; }

    // Legacy chatbox fields. They are read once, migrated into the current
    // status/pipeline model, and then omitted from the next save.
    [JsonPropertyName("chatboxRotationSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyChatboxRotationSeconds { get; set; }

    [JsonPropertyName("chatboxRotateEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyChatboxRotateEnabled { get; set; }

    [JsonPropertyName("chatboxMessages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyChatboxMessages { get; set; }

    [JsonPropertyName("chatboxIncludeNowPlaying")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyChatboxIncludeNowPlaying { get; set; }

    [JsonPropertyName("chatboxShowLyrics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyChatboxShowLyrics { get; set; }

    [JsonPropertyName("chatboxLyricsOnVrchat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyChatboxLyricsOnVrchat { get; set; }

    [JsonPropertyName("chatboxPauseLyricsAfterMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyChatboxPauseLyricsAfterMessage { get; set; }

    [JsonPropertyName("chatboxLyricsPauseSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyChatboxLyricsPauseSeconds { get; set; }
}

/// <summary>
/// Small JSON settings file under %LOCALAPPDATA%\VrcCompanion. Writes are
/// atomic and failures are swallowed so a damaged file never blocks startup.
/// </summary>
public sealed class SettingsStore
{
    private static SettingsStore? _shared;

    public static SettingsStore Shared => _shared ??= new();

    /// <summary>
    /// Points every component that shares the app's settings at an isolated file.
    /// The test assembly calls this before the first test, so a run neither reads
    /// nor writes the real %LOCALAPPDATA% settings (shields policy, lite mode,
    /// sorts) and every run starts from the same defaults.
    /// </summary>
    internal static void RedirectSharedForTests(string path) => _shared = new SettingsStore(path);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private readonly string _path;
    private bool _needsSecretRewrite;

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
        if (MigrateLegacyChatboxSettings() || _needsSecretRewrite)
        {
            Save();
        }
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
            _needsSecretRewrite = HasUnprotectedToken(json);
            return JsonSerializer.Deserialize<CompanionSettings>(json, JsonOptions)
                   ?? new CompanionSettings();
        }
        catch
        {
            return new CompanionSettings();
        }
    }

    private static bool HasUnprotectedToken(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return HasUnprotectedToken(document.RootElement, "chatboxPulsoidToken") ||
                   HasUnprotectedToken(document.RootElement, "chatboxTwitchAccessToken");
        }
        catch
        {
            return false;
        }
    }

    private static bool HasUnprotectedToken(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = property.GetString() ?? string.Empty;
        return value.Length > 0 &&
               !value.StartsWith(DpapiStringJsonConverter.Prefix, StringComparison.Ordinal);
    }

    private bool MigrateLegacyChatboxSettings()
    {
        var settings = Settings;
        var hasLegacySettings =
            settings.LegacyChatboxRotationSeconds is not null ||
            settings.LegacyChatboxRotateEnabled is not null ||
            settings.LegacyChatboxMessages is not null ||
            settings.LegacyChatboxIncludeNowPlaying is not null ||
            settings.LegacyChatboxShowLyrics is not null ||
            settings.LegacyChatboxLyricsOnVrchat is not null ||
            settings.LegacyChatboxPauseLyricsAfterMessage is not null ||
            settings.LegacyChatboxLyricsPauseSeconds is not null;
        if (!hasLegacySettings)
        {
            return false;
        }

        if (settings.LegacyChatboxRotateEnabled is { } rotateEnabled)
        {
            settings.ChatboxCycleEnabled = rotateEnabled;
        }

        if (settings.LegacyChatboxRotationSeconds is { } rotationSeconds)
        {
            settings.ChatboxCycleSeconds = Math.Clamp(rotationSeconds, 1, 600);
        }

        if (settings.LegacyChatboxIncludeNowPlaying is { } includeNowPlaying)
        {
            settings.ChatboxIncludeMusic = includeNowPlaying;
        }

        if (settings.LegacyChatboxShowLyrics is { } showLyrics)
        {
            settings.ChatboxIncludeLyrics = showLyrics;
        }

        if (settings.LegacyChatboxLyricsOnVrchat == true)
        {
            settings.ChatboxIncludeLyrics = true;
        }

        if (settings.LegacyChatboxPauseLyricsAfterMessage is { } pauseLyrics)
        {
            // The new chat box has one automatic-line override window. Preserve
            // the old pause duration when it was enabled, and use the shortest
            // window when the user had explicitly disabled the pause.
            settings.ChatboxChatVisibleSeconds = pauseLyrics
                ? Math.Clamp(settings.LegacyChatboxLyricsPauseSeconds ?? 30, 5, 3600)
                : 1;
        }

        settings.ChatboxStatusItems ??= new List<ChatboxStatusItem>();
        if (settings.LegacyChatboxMessages is { } legacyMessages &&
            settings.ChatboxStatusItems.Count == 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var statuses = legacyMessages
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Select(message => message.Trim())
                .Where(message =>
                    !message.Equals("{music}", StringComparison.OrdinalIgnoreCase) &&
                    !message.Equals("{lyrics}", StringComparison.OrdinalIgnoreCase))
                .Where(seen.Add)
                .Take(25)
                .Select((message, index) => new ChatboxStatusItem
                {
                    Message = message.Length > OscMessage.ChatboxLimit
                        ? message[..OscMessage.ChatboxLimit]
                        : message,
                    SortOrder = index,
                    UseInCycle = true
                })
                .ToList();
            settings.ChatboxStatusItems = statuses;
        }

        settings.LegacyChatboxRotationSeconds = null;
        settings.LegacyChatboxRotateEnabled = null;
        settings.LegacyChatboxMessages = null;
        settings.LegacyChatboxIncludeNowPlaying = null;
        settings.LegacyChatboxShowLyrics = null;
        settings.LegacyChatboxLyricsOnVrchat = null;
        settings.LegacyChatboxPauseLyricsAfterMessage = null;
        settings.LegacyChatboxLyricsPauseSeconds = null;
        return true;
    }
}
