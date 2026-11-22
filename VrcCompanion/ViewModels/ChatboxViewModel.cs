using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// MagicChatbox-style Chatbox. Status is a saved library used for the automatic
/// line; Chatting is a separate composer with a short history and a temporary
/// override of that line. VRChat only has one chatbox, so a typed message takes
/// over for the configured countdown and then automatic status resumes.
/// </summary>
public sealed class ChatboxViewModel : ViewModelBase, IDisposable
{
    public const int ChatLimit = OscMessage.ChatboxLimit;
    private const int MaxChatHistory = 5;
    private const int MaxStatusLength = OscMessage.ChatboxLimit;

    private readonly SettingsStore _store;
    private readonly CompanionSettings _settings;
    private readonly IChatboxSender _osc;
    private static readonly TimeSpan OscKeepAlive = TimeSpan.FromSeconds(12);
    private readonly NowPlayingService _nowPlaying;
    private readonly LyricsService _lyricsService;
    private readonly IWindowActivityService _windowActivity;
    private readonly IAfkSensor _afk;
    private readonly NetworkStatsService _networkStats = new();
    private readonly SystemStatsService _systemStats = new();
    private readonly WeatherService _weatherService = new();
    private readonly VrChatRadarService _radarService = new();
    private readonly PulsoidService _pulsoidService = new();
    private readonly TwitchService _twitchService = new();
    private readonly TikTokProfileService _tikTokService = new();
    private readonly SoundpadService _soundpadService = new();
    private readonly OpenVrService _openVrService = new();
    private readonly ChatboxOutputBuilder _outputBuilder = new();
    private readonly List<IChatboxProvider> _providers;
    private ChatboxBuildResult _lastBuild = new(
        string.Empty,
        Array.Empty<string>(),
        Array.Empty<string>(),
        new Dictionary<string, int>());
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _mediaTimer;
    private readonly DispatcherTimer _liveTypingTimer;
    private readonly DispatcherTimer _liveTypingFinalizeTimer;
    private readonly DispatcherTimer _integrationTimer;

    private string _activePage = "status";
    private string _statusText = "Enable OSC in VRChat: Action Menu → Options → OSC.";

    private string _newStatusText = string.Empty;
    private bool _cycleStatus;
    private int _cycleSeconds;
    private bool _randomStatusOrder;
    private bool _includeStatus;
    private bool _showStatusIcon;
    private string _statusIcon;
    private bool _afkEnabled;
    private int _afkTimeoutSeconds;
    private string _afkPrefix;
    private string _afkText;
    private bool _isAfk;
    private bool _includeTime;
    private string _lyricsFormat;
    private string _timeFormat;
    private string _weatherFormat = "{emoji} {temperature}°C";
    private string _networkFormat = "{network}";
    private string _radarFormat = "{world} | 👥 {players}";
    private string _componentFormat = "{stats}";
    private string _soundpadFormat = "🎶 '{sound}'";
    private string _heartRateFormat = "❤ {bpm} bpm";
    private string _twitchFormat = "{twitch}";
    private string _tikTokFormat = "TikTok @{profile} | {followers} followers";
    private string _spotifyFormat = "▶ {display}{source}";
    private string _discordFormat = "🔊 {channel} ({count}) | 🎙 {speaking}";
    private string _vrBatteryFormat = "{icon} {name} {batt}%";
    private string _vrPerformanceFormat = "🎮 {fps} FPS";
    private string _weatherCity = "London";
    private string _pulsoidToken = string.Empty;
    private string _twitchClientId = string.Empty;
    private string _twitchAccessToken = string.Empty;
    private string _twitchChannel = string.Empty;
    private string _tikTokUsername = string.Empty;
    private string _newGroupName = string.Empty;
    private string? _selectedStatusGroupId;
    private DateTime _lastCycle = DateTime.UtcNow;

    private string _chatText = string.Empty;
    private int _chatVisibleSeconds;
    private bool _keepUpdatingChat;
    private int _chatUpdateRateSeconds;
    private bool _liveEdit;
    private bool _realTimeEdit;
    private bool _liveTyping;
    private int _liveTypingRateMs;
    private int _liveTypingFinalizeMs;
    private bool _autocompleteEnabled;
    private int _autocompleteMinChars;
    private int _autocompleteMaxWords;
    private string _autocompleteSuffix = string.Empty;
    private bool _prefixChat;
    private bool _chatFx;
    private bool _sendAgainFx;
    private bool _chatOverride;
    private DateTime _chatVisibleUntil = DateTime.MinValue;
    private DateTime _lastChatUpdate = DateTime.MinValue;
    private string _lastSentLine = string.Empty;
    private bool _liveTypingHolding;
    private bool _suppressChatTextEffects;

    private NowPlayingInfo? _currentTrack;
    private WindowActivityInfo? _currentWindow;
    private LyricsDocument? _lyricsDocument;
    private string _lyricsTrackKey = string.Empty;
    private string _currentLyric = string.Empty;
    private string _lyricsStatusText = "Play a track to look for lyrics.";
    private CancellationTokenSource? _lyricsCancellation;
    private bool _pollingMedia;
    private bool _pollingIntegrations;
    private DateTime _lastWeatherRefresh = DateTime.MinValue;
    private DateTime _lastTwitchRefresh = DateTime.MinValue;
    private DateTime _lastPulsoidRefresh = DateTime.MinValue;
    private DateTime _lastTikTokRefresh = DateTime.MinValue;
    private DateTime _lastDiscordRefresh = DateTime.MinValue;
    private DateTime _lastOpenVrRefresh = DateTime.MinValue;
    private bool _pollingDiscord;
    private string _weatherText = string.Empty;
    private string _networkText = string.Empty;
    private string _radarText = string.Empty;
    private string _componentText = string.Empty;
    private string _soundpadText = string.Empty;
    private string _heartRateText = string.Empty;
    private string _twitchText = string.Empty;
    private string _tikTokText = string.Empty;
    private string _spotifyText = string.Empty;
    private string _discordText = string.Empty;
    private string _vrTrackerText = string.Empty;
    private string _vrPerformanceText = string.Empty;
    private string _vrBatteryDiagnosticText = string.Empty;

    private bool _includeMusic;
    private bool _includeWindow;
    private bool _includeLyrics;
    private string _musicFormat;
    private string _windowFormat;
    private string _separator;
    private bool _separateWithEnters;
    private string _linePrefix;
    private string _lineSuffix;
    private int _scanSeconds;
    private string _kawaiiEmoji = "🌸";
    private string _kawaiiAccent = "#5A3796";
    private string _kawaiiAccentInput = "#5A3796";
    private string _kawaiiAccentFeedback = "Purple is the default.";
    private bool _oscEnabled;
    private bool _isVrRunning;
    private string _host;
    private int _port;
    private DateTime _lastOscSentUtc = DateTime.MinValue;
    private bool _disposed;

    public Func<Task<DiscordVoiceSnapshot?>>? DiscordVoiceStateProvider { get; set; }

    public ChatboxViewModel(
        SettingsStore? store = null,
        IChatboxSender? osc = null,
        NowPlayingService? nowPlaying = null,
        LyricsService? lyrics = null,
        IWindowActivityService? windowActivity = null,
        IAfkSensor? afk = null,
        bool startTimers = true)
    {
        _store = store ?? SettingsStore.Shared;
        _settings = _store.Settings;
        _osc = osc ?? new OscChatboxService();
        _nowPlaying = nowPlaying ?? new NowPlayingService();
        _lyricsService = lyrics ?? new LyricsService();
        _windowActivity = windowActivity ?? new WindowActivityService();
        _afk = afk ?? new AfkSensor();

        _host = string.IsNullOrWhiteSpace(_settings.OscHost) ? "127.0.0.1" : _settings.OscHost;
        _port = Math.Clamp(_settings.OscPort, 1, 65535);
        _osc.Host = _host;
        _osc.Port = _port;

        _cycleStatus = _settings.ChatboxCycleEnabled;
        _cycleSeconds = Math.Clamp(_settings.ChatboxCycleSeconds, 1, 600);
        _randomStatusOrder = _settings.ChatboxCycleRandom;
        _includeStatus = _settings.ChatboxIncludeStatus;
        _showStatusIcon = _settings.ChatboxStatusIconEnabled;
        _statusIcon = string.IsNullOrWhiteSpace(_settings.ChatboxStatusIconText)
            ? "💭"
            : _settings.ChatboxStatusIconText.Trim();
        _afkEnabled = _settings.ChatboxAfkEnabled;
        _afkTimeoutSeconds = Math.Clamp(_settings.ChatboxAfkTimeoutSeconds, 30, 86400);
        _afkPrefix = string.IsNullOrWhiteSpace(_settings.ChatboxAfkPrefix)
            ? "💤"
            : _settings.ChatboxAfkPrefix.Trim();
        _afkText = string.IsNullOrWhiteSpace(_settings.ChatboxAfkText)
            ? "AFK"
            : _settings.ChatboxAfkText.Trim();
        _includeMusic = _settings.ChatboxIncludeMusic;
        _includeWindow = _settings.ChatboxIncludeWindowActivity;
        _includeLyrics = _settings.ChatboxIncludeLyrics;
        _includeTime = _settings.ChatboxIncludeTime;
        _musicFormat = NormalizeMusicFormat(_settings.ChatboxMusicFormat);
        _windowFormat = NormalizeWindowFormat(_settings.ChatboxWindowFormat);
        _lyricsFormat = NormalizeLyricsFormat(_settings.ChatboxLyricsFormat);
        _timeFormat = NormalizeTimeFormat(_settings.ChatboxTimeFormat);
        _weatherFormat = NormalizeTemplate(_settings.ChatboxWeatherFormat, "{emoji} {temperature}°C");
        _networkFormat = NormalizeTemplate(_settings.ChatboxNetworkFormat, "{network}");
        _radarFormat = NormalizeTemplate(_settings.ChatboxRadarFormat, "{world} | 👥 {players}");
        _componentFormat = NormalizeTemplate(_settings.ChatboxComponentFormat, "{stats}");
        _soundpadFormat = NormalizeTemplate(_settings.ChatboxSoundpadFormat, "🎶 '{sound}'");
        _heartRateFormat = NormalizeTemplate(_settings.ChatboxHeartRateFormat, "❤ {bpm} bpm");
        _twitchFormat = NormalizeTemplate(_settings.ChatboxTwitchFormat, "{twitch}");
        _tikTokFormat = NormalizeTemplate(_settings.ChatboxTikTokFormat, "TikTok @{profile} | {followers} followers");
        _spotifyFormat = NormalizeTemplate(_settings.ChatboxSpotifyFormat, "▶ {display}{source}");
        _discordFormat = NormalizeTemplate(_settings.ChatboxDiscordFormat, "🔊 {channel} ({count}) | 🎙 {speaking}");
        _vrBatteryFormat = NormalizeTemplate(_settings.ChatboxVrBatteryFormat, "{icon} {name} {batt}%");
        _vrPerformanceFormat = NormalizeTemplate(_settings.ChatboxVrPerformanceFormat, "🎮 {fps} FPS");
        _weatherCity = string.IsNullOrWhiteSpace(_settings.ChatboxWeatherCity) ? "London" : _settings.ChatboxWeatherCity.Trim();
        _pulsoidToken = _settings.ChatboxPulsoidToken ?? string.Empty;
        _twitchClientId = _settings.ChatboxTwitchClientId ?? string.Empty;
        _twitchAccessToken = _settings.ChatboxTwitchAccessToken ?? string.Empty;
        _twitchChannel = _settings.ChatboxTwitchChannel ?? string.Empty;
        _tikTokUsername = _settings.ChatboxTikTokUsername ?? string.Empty;
        _separator = string.IsNullOrWhiteSpace(_settings.ChatboxSeparator) ? " ┆ " : _settings.ChatboxSeparator;
        _separateWithEnters = _settings.ChatboxSeparateWithEnters;
        _linePrefix = _settings.ChatboxPrefix ?? string.Empty;
        _lineSuffix = _settings.ChatboxSuffix ?? string.Empty;
        _scanSeconds = Math.Clamp(_settings.ChatboxScanSeconds, 1, 60);
        _kawaiiEmoji = NormalizeKawaiiEmoji(_settings.KawaiiEmoji);
        _kawaiiAccent = NormalizeHex(_settings.KawaiiAccent, "#5A3796");
        _kawaiiAccentInput = _kawaiiAccent;
        _kawaiiAccentFeedback = string.Equals(_kawaiiAccent, "#5A3796", StringComparison.Ordinal)
            ? "Purple is the default."
            : $"{_kawaiiAccent} is saved.";
        KawaiiAccentPresets = new[]
        {
            new KawaiiAccentPreset("Lavender", "#7C4DFF"),
            new KawaiiAccentPreset("Sakura", "#E85D9E"),
            new KawaiiAccentPreset("Mint", "#2EC4A6"),
            new KawaiiAccentPreset("Ocean", "#3B82F6"),
            new KawaiiAccentPreset("Sunset", "#F97316"),
            new KawaiiAccentPreset("Rose", "#F43F5E"),
            new KawaiiAccentPreset("Honey", "#D4A017"),
            new KawaiiAccentPreset("Cloud", "#64748B")
        };
        _oscEnabled = _settings.ChatboxOscEnabled;
        _isVrRunning = VrModeDetector.IsVrRunning();

        _chatVisibleSeconds = Math.Clamp(_settings.ChatboxChatVisibleSeconds, 1, 3600);
        _keepUpdatingChat = _settings.ChatboxKeepUpdatingChat;
        _chatUpdateRateSeconds = Math.Clamp(_settings.ChatboxUpdateRateSeconds, 1, 60);
        _liveEdit = _settings.ChatboxLiveEdit;
        _realTimeEdit = _settings.ChatboxRealTimeEdit;
        _liveTyping = _settings.ChatboxLiveTyping;
        _liveTypingRateMs = Math.Clamp(_settings.ChatboxLiveTypingRateMs, 200, 10000);
        _liveTypingFinalizeMs = Math.Clamp(_settings.ChatboxLiveTypingFinalizeMs, 500, 60000);
        _autocompleteEnabled = _settings.ChatboxAutocomplete;
        _autocompleteMinChars = Math.Clamp(_settings.ChatboxAutocompleteMinChars, 1, 40);
        _autocompleteMaxWords = Math.Clamp(_settings.ChatboxAutocompleteMaxWords, 1, 10);
        _prefixChat = _settings.ChatboxPrefixChat;
        _chatFx = _settings.ChatboxChatFx;
        _sendAgainFx = _settings.ChatboxSendAgainFx;

        StatusGroups = new ObservableCollection<ChatboxStatusGroup>();
        foreach (var group in _settings.ChatboxStatusGroups ?? new List<ChatboxStatusGroup>())
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Name))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(group.Id))
            {
                group.Id = Guid.NewGuid().ToString("N");
            }

            group.Changed += (_, _) => SaveGroups();
            StatusGroups.Add(group);
        }

        StatusItems = new ObservableCollection<ChatboxStatusItem>();
        foreach (var item in _settings.ChatboxStatusItems ?? new List<ChatboxStatusItem>())
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Message))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Id))
            {
                item.Id = Guid.NewGuid().ToString("N");
            }

            item.IsEditing = false;
            item.IsSelected = false;
            if (!string.IsNullOrWhiteSpace(item.GroupId) &&
                !StatusGroups.Any(group => group.Id == item.GroupId))
            {
                item.GroupId = null;
            }

            WireStatusItem(item);
            StatusItems.Add(item);
        }

        for (var index = 0; index < StatusItems.Count; index++)
        {
            StatusItems[index].SortOrder = index;
        }

        FilteredStatusItems = new ObservableCollection<ChatboxStatusItem>();
        IntegrationCards = new ObservableCollection<ChatboxIntegrationCard>();
        _providers = new List<IChatboxProvider>
        {
            new StatusChatboxProvider(),
            new MediaChatboxProvider(),
            new LyricsChatboxProvider(),
            new SnapshotChatboxProvider("Spotify", "Spotify", 25),
            new WindowChatboxProvider(),
            new SnapshotChatboxProvider("VrcRadar", "VRChat Radar", 35),
            new SnapshotChatboxProvider("HeartRate", "Heart rate", 40),
            new SnapshotChatboxProvider("Discord", "Discord", 45),
            new SnapshotChatboxProvider("Twitch", "Twitch", 50),
            new SnapshotChatboxProvider("TikTokLive", "TikTok", 52),
            new SnapshotChatboxProvider("TrackerBattery", "VR gear battery", 60),
            new SnapshotChatboxProvider("VrPerformance", "VR performance", 65),
            new SnapshotChatboxProvider("ComponentStats", "Component stats", 70),
            new SnapshotChatboxProvider("Soundpad", "Soundpad", 75),
            new SnapshotChatboxProvider("Voicemod", "Voicemod", 80),
            new SnapshotChatboxProvider("Network", "Network stats", 80),
            new SnapshotChatboxProvider("Weather", "Weather", 85),
            new TimeChatboxProvider()
        };
        BuildProviderOrder();
        RefreshStatusFilter();

        var active = StatusItems.FirstOrDefault(item => item.Id == _settings.ChatboxActiveStatusId)
                     ?? StatusItems.FirstOrDefault(item => item.IsActive)
                     ?? StatusItems.FirstOrDefault();
        SetActiveStatus(active, save: false);

        ChatHistory = new ObservableCollection<ChatboxChatItem>();
        foreach (var message in (_settings.ChatboxHistory ?? new List<string>())
                     .Where(message => !string.IsNullOrWhiteSpace(message))
                     .TakeLast(MaxChatHistory))
        {
            var item = new ChatboxChatItem
            {
                Message = OscMessage.FitChatboxText(message, ChatLimit),
                CanEdit = _liveEdit
            };
            WireChatItem(item);
            ChatHistory.Add(item);
        }

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_scanSeconds) };
        _tickTimer.Tick += (_, _) => Tick();
        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _mediaTimer.Tick += (_, _) => _ = PollNowPlayingAsync();
        _integrationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _integrationTimer.Tick += (_, _) => PollIntegrations();
        _liveTypingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_liveTypingRateMs) };
        _liveTypingTimer.Tick += (_, _) => SendLiveTypingPreview();
        _liveTypingFinalizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_liveTypingFinalizeMs) };
        _liveTypingFinalizeTimer.Tick += (_, _) =>
        {
            StopLiveTyping();
            SendChat();
        };

        SendChatCommand = new RelayCommand(SendChat, () => !string.IsNullOrWhiteSpace(ChatText));
        AddStatusCommand = new RelayCommand(AddStatus, () => !string.IsNullOrWhiteSpace(NewStatusText));
        ShowStatusPageCommand = new RelayCommand(() => SetPage("status"));
        ShowChattingPageCommand = new RelayCommand(() => SetPage("chatting"));
        ShowIntegrationsPageCommand = new RelayCommand(() => SetPage("integrations"));
        ShowOptionsPageCommand = new RelayCommand(() => SetPage("options"));
        ShowStatusTabCommand = ShowStatusPageCommand;
        ShowChatTabCommand = ShowChattingPageCommand;
        ShowAllStatusesCommand = new RelayCommand(() => SelectStatusGroup(null));
        AddStatusGroupCommand = new RelayCommand(AddStatusGroup, () => !string.IsNullOrWhiteSpace(NewGroupName));
        DeleteStatusGroupCommand = new RelayCommand<ChatboxStatusGroup>(DeleteStatusGroup, group => group is not null);
        SelectStatusGroupCommand = new RelayCommand<ChatboxStatusGroup>(
            group => SelectStatusGroup(group),
            group => group is not null);
        ToggleStatusGroupCycleCommand = new RelayCommand<ChatboxStatusGroup>(
            ToggleStatusGroupCycle,
            group => group is not null);
        SelectAllVisibleStatusesCommand = new RelayCommand(SelectAllVisibleStatuses, () => FilteredStatusItems.Count > 0);
        ClearStatusSelectionCommand = new RelayCommand(ClearStatusSelection, () => StatusItems.Any(item => item.IsSelected));
        DeleteSelectedStatusesCommand = new RelayCommand(DeleteSelectedStatuses, () => StatusItems.Any(item => item.IsSelected));
        ToggleStatusSelectedCommand = new RelayCommand<ChatboxStatusItem>(
            ToggleStatusSelected,
            item => item is not null);
        AssignStatusToSelectedGroupCommand = new RelayCommand<ChatboxStatusItem>(
            AssignStatusToSelectedGroup,
            item => item is not null && _selectedStatusGroupId is not null);
        MoveStatusUpCommand = new RelayCommand<ChatboxStatusItem>(
            item => MoveStatus(item, -1),
            item => item is not null);
        MoveStatusDownCommand = new RelayCommand<ChatboxStatusItem>(
            item => MoveStatus(item, 1),
            item => item is not null);
        MoveIntegrationUpCommand = new RelayCommand<ChatboxIntegrationCard>(
            card => MoveIntegration(card, -1),
            card => card is not null);
        MoveIntegrationDownCommand = new RelayCommand<ChatboxIntegrationCard>(
            card => MoveIntegration(card, 1),
            card => card is not null);
        StopChatCommand = new RelayCommand(
            () => EndChatOverride(clearChatbox: true, "Chat cleared. Automatic status will resume."),
            () => _chatOverride);
        ClearChatHistoryCommand = new RelayCommand(ClearChatHistory, () => ChatHistory.Count > 0);
        ClearStatusesCommand = new RelayCommand(ClearStatuses, () => StatusItems.Count > 0);
        UseStatusCommand = new RelayCommand<ChatboxStatusItem>(
            item => SetActiveStatus(item, save: true),
            item => item is not null);
        RemoveStatusCommand = new RelayCommand<ChatboxStatusItem>(
            RemoveStatus,
            item => item is not null);
        ToggleStatusCycleCommand = new RelayCommand<ChatboxStatusItem>(
            ToggleStatusCycle,
            item => item is not null);
        EditStatusCommand = new RelayCommand<ChatboxStatusItem>(
            BeginStatusEdit,
            item => item is not null);
        SaveStatusEditCommand = new RelayCommand<ChatboxStatusItem>(
            SaveStatusEdit,
            item => item is not null && !string.IsNullOrWhiteSpace(item.EditText));
        CancelStatusEditCommand = new RelayCommand<ChatboxStatusItem>(
            CancelStatusEdit,
            item => item is not null);
        CycleStatusNowCommand = new RelayCommand(
            () => CycleToNextStatus(),
            () => CycleCandidates().Count > 0);
        ResendChatCommand = new RelayCommand<ChatboxChatItem>(
            ResendChat,
            item => item is not null);
        BeginChatEditCommand = new RelayCommand<ChatboxChatItem>(
            BeginChatEdit,
            item => item?.CanEdit == true);
        SaveChatEditCommand = new RelayCommand<ChatboxChatItem>(
            SaveChatEdit,
            item => item is not null && item.IsEditing);
        CancelChatEditCommand = new RelayCommand<ChatboxChatItem>(
            CancelChatEdit,
            item => item?.IsEditing == true);
        AcceptAutocompleteCommand = new RelayCommand(
            AcceptAutocomplete,
            () => AutocompleteSuffix.Length > 0);
        InsertChatEmojiCommand = new RelayCommand<string>(
            InsertChatEmoji,
            emoji => !string.IsNullOrWhiteSpace(emoji));
        SetKawaiiEmojiCommand = new RelayCommand<string>(
            emoji => KawaiiEmoji = emoji ?? string.Empty,
            emoji => !string.IsNullOrWhiteSpace(emoji));
        ApplyKawaiiAccentCommand = new RelayCommand(ApplyKawaiiAccent);
        SetKawaiiAccentCommand = new RelayCommand<string>(
            hex =>
            {
                KawaiiAccentInput = hex ?? string.Empty;
                ApplyKawaiiAccent();
            },
            hex => !string.IsNullOrWhiteSpace(hex));
        AddNowPlayingAsStatusCommand = new RelayCommand(
            AddNowPlayingAsStatus,
            () => CurrentTrack is not null);
        AddWindowAsStatusCommand = new RelayCommand(
            AddWindowAsStatus,
            () => CurrentWindow is not null);

        RefreshWindowActivity();
        if (startTimers)
        {
            _tickTimer.Start();
            _mediaTimer.Start();
            _integrationTimer.Start();
            _ = PollNowPlayingAsync();
            PollIntegrations();
        }
    }

        public ObservableCollection<ChatboxStatusItem> StatusItems { get; }

    public ObservableCollection<ChatboxStatusItem> FilteredStatusItems { get; }

    public ObservableCollection<ChatboxStatusGroup> StatusGroups { get; }

    public ObservableCollection<ChatboxIntegrationCard> IntegrationCards { get; }

    public ObservableCollection<ChatboxChatItem> ChatHistory { get; }

    public RelayCommand SendChatCommand { get; }

    public RelayCommand SendCommand => SendChatCommand;

    public RelayCommand AddStatusCommand { get; }

    public RelayCommand ShowStatusPageCommand { get; }

    public RelayCommand ShowChattingPageCommand { get; }

    public RelayCommand ShowIntegrationsPageCommand { get; }

    public RelayCommand ShowOptionsPageCommand { get; }

    public RelayCommand ShowStatusTabCommand { get; }

    public RelayCommand ShowChatTabCommand { get; }

    public RelayCommand StopChatCommand { get; }

    public RelayCommand ClearChatHistoryCommand { get; }

    public RelayCommand ClearStatusesCommand { get; }

    public RelayCommand<ChatboxStatusItem> UseStatusCommand { get; }

    public RelayCommand<ChatboxStatusItem> RemoveStatusCommand { get; }

    public RelayCommand<ChatboxStatusItem> ToggleStatusCycleCommand { get; }

    public RelayCommand<ChatboxStatusItem> EditStatusCommand { get; }

    public RelayCommand<ChatboxStatusItem> SaveStatusEditCommand { get; }

    public RelayCommand<ChatboxStatusItem> CancelStatusEditCommand { get; }

    public RelayCommand CycleStatusNowCommand { get; }

    public RelayCommand<ChatboxChatItem> ResendChatCommand { get; }

    public RelayCommand<ChatboxChatItem> BeginChatEditCommand { get; }

    public RelayCommand<ChatboxChatItem> SaveChatEditCommand { get; }

    public RelayCommand<ChatboxChatItem> CancelChatEditCommand { get; }

    public RelayCommand AcceptAutocompleteCommand { get; }

    public RelayCommand<string> InsertChatEmojiCommand { get; }

    public RelayCommand<string> SetKawaiiEmojiCommand { get; }

    public RelayCommand ApplyKawaiiAccentCommand { get; }

    public RelayCommand<string> SetKawaiiAccentCommand { get; }

    public IReadOnlyList<string> EmojiChoices { get; } = new[] { "🌸", "✨", "💜", "🎀", "💬", "🐾" };

    public RelayCommand AddNowPlayingAsStatusCommand { get; }

    public RelayCommand AddWindowAsStatusCommand { get; }

    public RelayCommand ShowAllStatusesCommand { get; }

    public RelayCommand AddStatusGroupCommand { get; }

    public RelayCommand<ChatboxStatusGroup> DeleteStatusGroupCommand { get; }

    public RelayCommand<ChatboxStatusGroup> SelectStatusGroupCommand { get; }

    public RelayCommand<ChatboxStatusGroup> ToggleStatusGroupCycleCommand { get; }

    public RelayCommand SelectAllVisibleStatusesCommand { get; }

    public RelayCommand ClearStatusSelectionCommand { get; }

    public RelayCommand DeleteSelectedStatusesCommand { get; }

    public RelayCommand<ChatboxStatusItem> ToggleStatusSelectedCommand { get; }

    public RelayCommand<ChatboxStatusItem> AssignStatusToSelectedGroupCommand { get; }

    public RelayCommand<ChatboxStatusItem> MoveStatusUpCommand { get; }

    public RelayCommand<ChatboxStatusItem> MoveStatusDownCommand { get; }

    public RelayCommand<ChatboxIntegrationCard> MoveIntegrationUpCommand { get; }

    public RelayCommand<ChatboxIntegrationCard> MoveIntegrationDownCommand { get; }

    public bool IsStatusPage => _activePage == "status";

    public bool IsChattingPage => _activePage == "chatting";

    public bool IsIntegrationsPage => _activePage == "integrations";

    public bool IsOptionsPage => _activePage == "options";

    public bool IsStatusTab => IsStatusPage;

    public bool IsChatTab => IsChattingPage;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(ChatFeedback));
            }
        }
    }

    public string ChatFeedback => StatusText;

    public string NewStatusText
    {
        get => _newStatusText;
        set
        {
            if (!SetProperty(ref _newStatusText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(NewStatusCountText));
            AddStatusCommand.RaiseCanExecuteChanged();
        }
    }

    public string NewStatusCountText => $"{NewStatusText.Length}/{MaxStatusLength}";

    public bool HasStatusItems => StatusItems.Count > 0;

    public bool HasNoStatusItems => StatusItems.Count == 0;

    public bool HasNoFilteredStatuses => FilteredStatusItems.Count == 0;

    public bool HasStatusGroups => StatusGroups.Count > 0;

    public string StatusCountText => $"{FilteredStatusItems.Count} shown · {StatusItems.Count} saved";

    public string SelectedGroupText => _selectedStatusGroupId is null
        ? "All statuses"
        : StatusGroups.FirstOrDefault(group => group.Id == _selectedStatusGroupId)?.DisplayName ?? "All statuses";

    public string NewGroupName
    {
        get => _newGroupName;
        set
        {
            if (!SetProperty(ref _newGroupName, value))
            {
                return;
            }

            AddStatusGroupCommand.RaiseCanExecuteChanged();
        }
    }

    public string SelectedStatusCountText => $"{StatusItems.Count(item => item.IsSelected)} selected";

    public bool HasSelectedStatuses => StatusItems.Any(item => item.IsSelected);

    public ChatboxStatusItem? ActiveStatus => StatusItems.FirstOrDefault(item => item.IsActive);

    public string ActiveStatusText => ActiveStatus?.Message ?? "No active status";

    public string StatusPreviewText => BuildAutomaticLine();

    public bool CycleStatus
    {
        get => _cycleStatus;
        set
        {
            if (!SetProperty(ref _cycleStatus, value))
            {
                return;
            }

            _settings.ChatboxCycleEnabled = value;
            _lastCycle = DateTime.UtcNow;
            Save();
            OnPropertyChanged(nameof(StatusModeText));
        }
    }

    public int CycleSeconds
    {
        get => _cycleSeconds;
        set
        {
            var clamped = Math.Clamp(value, 1, 600);
            if (!SetProperty(ref _cycleSeconds, clamped))
            {
                return;
            }

            _settings.ChatboxCycleSeconds = clamped;
            Save();
            OnPropertyChanged(nameof(CycleSecondsText));
        }
    }

    public string CycleSecondsText => $"{CycleSeconds}s";

    public bool RandomStatusOrder
    {
        get => _randomStatusOrder;
        set
        {
            if (!SetProperty(ref _randomStatusOrder, value))
            {
                return;
            }

            _settings.ChatboxCycleRandom = value;
            Save();
        }
    }

    public bool IncludeStatus
    {
        get => _includeStatus;
        set
        {
            if (!SetProperty(ref _includeStatus, value))
            {
                return;
            }

            _settings.ChatboxIncludeStatus = value;
            Save();
            RefreshPreview();
        }
    }

    public bool ShowStatusIcon
    {
        get => _showStatusIcon;
        set
        {
            if (!SetProperty(ref _showStatusIcon, value))
            {
                return;
            }

            _settings.ChatboxStatusIconEnabled = value;
            Save();
            RefreshPreview();
        }
    }

    public string StatusIcon
    {
        get => _statusIcon;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "💭" : value.Trim();
            if (!SetProperty(ref _statusIcon, normalized))
            {
                return;
            }

            _settings.ChatboxStatusIconText = normalized;
            Save();
            RefreshPreview();
        }
    }

    public bool AfkEnabled
    {
        get => _afkEnabled;
        set
        {
            if (!SetProperty(ref _afkEnabled, value))
            {
                return;
            }

            _settings.ChatboxAfkEnabled = value;
            Save();
            RefreshAfkState();
            OnPropertyChanged(nameof(AfkStateText));
            RefreshPreview();
        }
    }

    public int AfkTimeoutSeconds
    {
        get => _afkTimeoutSeconds;
        set
        {
            var clamped = Math.Clamp(value, 30, 86400);
            if (!SetProperty(ref _afkTimeoutSeconds, clamped))
            {
                return;
            }

            _settings.ChatboxAfkTimeoutSeconds = clamped;
            Save();
            RefreshAfkState();
            OnPropertyChanged(nameof(AfkStateText));
            RefreshPreview();
        }
    }

    public string AfkPrefix
    {
        get => _afkPrefix;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "💤" : value.Trim();
            if (!SetProperty(ref _afkPrefix, normalized))
            {
                return;
            }

            _settings.ChatboxAfkPrefix = normalized;
            Save();
            OnPropertyChanged(nameof(AfkStatusMessage));
            OnPropertyChanged(nameof(AfkStateText));
            RefreshPreview();
        }
    }

    public string AfkText
    {
        get => _afkText;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "AFK" : value.Trim();
            if (!SetProperty(ref _afkText, normalized))
            {
                return;
            }

            _settings.ChatboxAfkText = normalized;
            Save();
            OnPropertyChanged(nameof(AfkStatusMessage));
            OnPropertyChanged(nameof(AfkStateText));
            RefreshPreview();
        }
    }

    public bool IsAfk
    {
        get => _isAfk;
        private set => SetProperty(ref _isAfk, value);
    }

    public string AfkStatusMessage => $"{AfkPrefix} {AfkText}".Trim();

    public string AfkStateText => !AfkEnabled
        ? "AFK detection is off."
        : IsAfk
            ? $"You are idle, so the chatbox shows {AfkStatusMessage}."
            : $"AFK appears after {AfkTimeoutSeconds} seconds without input.";

    public string StatusModeText => CycleStatus ? "Cycling automatic status" : "Manual status";

    public string ChatText
    {
        get => _chatText;
        set
        {
            var limited = value.Length > OscMessage.ChatboxLimit
                ? value[..OscMessage.ChatboxLimit]
                : value;
            if (!SetProperty(ref _chatText, limited))
            {
                return;
            }

            OnPropertyChanged(nameof(ChatCountText));
            OnPropertyChanged(nameof(IsChatOverLimit));
            OnPropertyChanged(nameof(DraftPreviewText));
            OnPropertyChanged(nameof(IsComposing));
            OnPropertyChanged(nameof(PreviewStatusText));
            OnPropertyChanged(nameof(ChatboxDisplayTag));
            SendChatCommand.RaiseCanExecuteChanged();
            UpdateAutocomplete();

            if (_suppressChatTextEffects)
            {
                return;
            }

            if (LiveTyping)
            {
                if (string.IsNullOrWhiteSpace(ChatText))
                {
                    StopLiveTyping(clearDraft: true);
                }
                else
                {
                    ScheduleLiveTyping();
                }
            }
            else
            {
                // Composing is deliberately silent. Sending /chatbox/typing
                // makes VRChat open the chatbox while the user is only typing.
                StopLiveTyping(clearDraft: false);
            }
        }
    }

    public string ChatCountText => $"{ChatText.Length}/{ChatLimit}";

    public bool IsChatOverLimit => ChatText.Length > ChatLimit;

    public string DraftPreviewText => string.IsNullOrWhiteSpace(ChatText)
        ? "Type a message to preview it."
        : BuildChatOutgoing(ChatText);

    public bool HasChatHistory => ChatHistory.Count > 0;

    public bool HasNoChatHistory => ChatHistory.Count == 0;

    public string ChatCountdownText
    {
        get
        {
            if (!_chatOverride)
            {
                return CycleStatus && CycleCandidates().Count > 0
                    ? "Automatic status cycling"
                    : "Automatic status";
            }

            var seconds = Math.Max(0, (int)Math.Ceiling((_chatVisibleUntil - DateTime.UtcNow).TotalSeconds));
            return $"Chat visible · {seconds}s";
        }
    }

    public bool IsChatShowing => _chatOverride;

    public bool IsLiveTypingShowing => _liveTypingHolding;

    public bool IsComposing => !string.IsNullOrWhiteSpace(ChatText);

    public int ChatVisibleSeconds
    {
        get => _chatVisibleSeconds;
        set
        {
            var clamped = Math.Clamp(value, 1, 3600);
            if (!SetProperty(ref _chatVisibleSeconds, clamped))
            {
                return;
            }

            _settings.ChatboxChatVisibleSeconds = clamped;
            Save();
        }
    }

    public bool KeepUpdatingChat
    {
        get => _keepUpdatingChat;
        set
        {
            if (!SetProperty(ref _keepUpdatingChat, value))
            {
                return;
            }

            _settings.ChatboxKeepUpdatingChat = value;
            Save();
        }
    }

    public int ChatUpdateRateSeconds
    {
        get => _chatUpdateRateSeconds;
        set
        {
            var clamped = Math.Clamp(value, 1, 60);
            if (!SetProperty(ref _chatUpdateRateSeconds, clamped))
            {
                return;
            }

            _settings.ChatboxUpdateRateSeconds = clamped;
            Save();
        }
    }

    public bool LiveEdit
    {
        get => _liveEdit;
        set
        {
            if (!SetProperty(ref _liveEdit, value))
            {
                return;
            }

            _settings.ChatboxLiveEdit = value;
            Save();
            foreach (var item in ChatHistory)
            {
                item.CanEdit = value;
            }

            BeginChatEditCommand.RaiseCanExecuteChanged();
        }
    }

    public bool RealTimeEdit
    {
        get => _realTimeEdit;
        set
        {
            if (!SetProperty(ref _realTimeEdit, value))
            {
                return;
            }

            _settings.ChatboxRealTimeEdit = value;
            Save();
        }
    }

    public bool LiveTyping
    {
        get => _liveTyping;
        set
        {
            if (!SetProperty(ref _liveTyping, value))
            {
                return;
            }

            _settings.ChatboxLiveTyping = value;
            Save();
            if (!value)
            {
                StopLiveTyping(clearDraft: true);
            }
            else if (!string.IsNullOrWhiteSpace(ChatText))
            {
                ScheduleLiveTyping();
            }
        }
    }

    public int LiveTypingRateMs
    {
        get => _liveTypingRateMs;
        set
        {
            var clamped = Math.Clamp(value, 200, 10000);
            if (!SetProperty(ref _liveTypingRateMs, clamped))
            {
                return;
            }

            _settings.ChatboxLiveTypingRateMs = clamped;
            _liveTypingTimer.Interval = TimeSpan.FromMilliseconds(clamped);
            Save();
        }
    }

    public int LiveTypingFinalizeMs
    {
        get => _liveTypingFinalizeMs;
        set
        {
            var clamped = Math.Clamp(value, 500, 60000);
            if (!SetProperty(ref _liveTypingFinalizeMs, clamped))
            {
                return;
            }

            _settings.ChatboxLiveTypingFinalizeMs = clamped;
            _liveTypingFinalizeTimer.Interval = TimeSpan.FromMilliseconds(clamped);
            Save();
        }
    }

    public bool AutocompleteEnabled
    {
        get => _autocompleteEnabled;
        set
        {
            if (!SetProperty(ref _autocompleteEnabled, value))
            {
                return;
            }

            _settings.ChatboxAutocomplete = value;
            Save();
            UpdateAutocomplete();
        }
    }

    public int AutocompleteMinChars
    {
        get => _autocompleteMinChars;
        set
        {
            var clamped = Math.Clamp(value, 1, 40);
            if (!SetProperty(ref _autocompleteMinChars, clamped))
            {
                return;
            }

            _settings.ChatboxAutocompleteMinChars = clamped;
            Save();
            UpdateAutocomplete();
        }
    }

    public int AutocompleteMaxWords
    {
        get => _autocompleteMaxWords;
        set
        {
            var clamped = Math.Clamp(value, 1, 10);
            if (!SetProperty(ref _autocompleteMaxWords, clamped))
            {
                return;
            }

            _settings.ChatboxAutocompleteMaxWords = clamped;
            Save();
            UpdateAutocomplete();
        }
    }

    public string AutocompleteSuffix
    {
        get => _autocompleteSuffix;
        private set
        {
            if (!SetProperty(ref _autocompleteSuffix, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasAutocompleteSuffix));
            OnPropertyChanged(nameof(AutocompleteDisplay));
            AcceptAutocompleteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasAutocompleteSuffix => AutocompleteSuffix.Length > 0;

    public string AutocompleteDisplay => HasAutocompleteSuffix
        ? $"{ChatText}{AutocompleteSuffix}"
        : string.Empty;

    public bool PrefixChat
    {
        get => _prefixChat;
        set
        {
            if (!SetProperty(ref _prefixChat, value))
            {
                return;
            }

            _settings.ChatboxPrefixChat = value;
            Save();
            OnPropertyChanged(nameof(DraftPreviewText));
        }
    }

    public bool ChatFx
    {
        get => _chatFx;
        set
        {
            if (!SetProperty(ref _chatFx, value))
            {
                return;
            }

            _settings.ChatboxChatFx = value;
            Save();
        }
    }

    public bool SendAgainFx
    {
        get => _sendAgainFx;
        set
        {
            if (!SetProperty(ref _sendAgainFx, value))
            {
                return;
            }

            _settings.ChatboxSendAgainFx = value;
            Save();
        }
    }

    public bool IncludeMusic
    {
        get => _includeMusic;
        set
        {
            if (!SetProperty(ref _includeMusic, value))
            {
                return;
            }

            _settings.ChatboxIncludeMusic = value;
            Save();
            RefreshPreview();
        }
    }

    public bool IncludeWindow
    {
        get => _includeWindow;
        set
        {
            if (!SetProperty(ref _includeWindow, value))
            {
                return;
            }

            _settings.ChatboxIncludeWindowActivity = value;
            Save();
            RefreshPreview();
        }
    }

    public bool IncludeLyrics
    {
        get => _includeLyrics;
        set
        {
            if (!SetProperty(ref _includeLyrics, value))
            {
                return;
            }

            _settings.ChatboxIncludeLyrics = value;
            Save();
            if (value)
            {
                _ = LoadLyricsForCurrentTrackAsync();
            }
            else
            {
                _lyricsCancellation?.Cancel();
                _lyricsDocument = null;
                CurrentLyric = string.Empty;
                LyricsStatusText = "Lyrics are off.";
                RefreshPreview();
            }
        }
    }

    public bool IncludeTime
    {
        get => _includeTime;
        set
        {
            if (!SetProperty(ref _includeTime, value))
            {
                return;
            }

            _settings.ChatboxIncludeTime = value;
            Save();
            RefreshPreview();
        }
    }

    public string LyricsFormat
    {
        get => _lyricsFormat;
        set
        {
            var normalized = NormalizeLyricsFormat(value);
            if (!SetProperty(ref _lyricsFormat, normalized))
            {
                return;
            }

            _settings.ChatboxLyricsFormat = normalized;
            Save();
            RefreshPreview();
        }
    }

    public string TimeFormat
    {
        get => _timeFormat;
        set
        {
            var normalized = NormalizeTimeFormat(value);
            if (!SetProperty(ref _timeFormat, normalized))
            {
                return;
            }

            _settings.ChatboxTimeFormat = normalized;
            Save();
            RefreshPreview();
        }
    }

    public string WeatherFormat
    {
        get => _weatherFormat;
        set
        {
            var next = NormalizeTemplate(value, "{emoji} {temperature}°C");
            if (SetProperty(ref _weatherFormat, next))
            {
                _settings.ChatboxWeatherFormat = next;
                Save();
                RefreshPreview();
            }
        }
    }

    public string WeatherCity
    {
        get => _weatherCity;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "London" : value.Trim();
            if (!SetProperty(ref _weatherCity, next))
            {
                return;
            }

            _settings.ChatboxWeatherCity = next;
            Save();
            _lastWeatherRefresh = DateTime.MinValue;
            PollIntegrations();
        }
    }

    public string WeatherText => string.IsNullOrWhiteSpace(_weatherText)
        ? "Set a city above to load the weather."
        : _weatherText;

    public string NetworkText => string.IsNullOrWhiteSpace(_networkText)
        ? "Network counters are starting…"
        : _networkText;

    public string RadarText => string.IsNullOrWhiteSpace(_radarText)
        ? "Join a VRChat world to fill the radar."
        : _radarText;

    public string ComponentText => string.IsNullOrWhiteSpace(_componentText)
        ? "Component stats are starting…"
        : _componentText;

    public string SoundpadText => string.IsNullOrWhiteSpace(_soundpadText)
        ? "Play a Soundpad clip to show it here."
        : _soundpadText;

    public string HeartRateText => string.IsNullOrWhiteSpace(_heartRateText)
        ? "Add your Pulsoid token above to read your heart rate."
        : _heartRateText;

    public string TwitchText => string.IsNullOrWhiteSpace(_twitchText)
        ? "Add your Twitch Helix credentials above to show the stream."
        : _twitchText;

    public string SpotifyText => string.IsNullOrWhiteSpace(_spotifyText)
        ? "Start Spotify playback to fill this provider."
        : _spotifyText;

    public string NetworkFormat
    {
        get => _networkFormat;
        set => SetTemplate(ref _networkFormat, value, "{network}", _settings.ChatboxNetworkFormat,
            next => _settings.ChatboxNetworkFormat = next);
    }

    public string RadarFormat
    {
        get => _radarFormat;
        set => SetTemplate(ref _radarFormat, value, "{world} | 👥 {players}", _settings.ChatboxRadarFormat,
            next => _settings.ChatboxRadarFormat = next);
    }

    public string ComponentFormat
    {
        get => _componentFormat;
        set => SetTemplate(ref _componentFormat, value, "{stats}", _settings.ChatboxComponentFormat,
            next => _settings.ChatboxComponentFormat = next);
    }

    public string SoundpadFormat
    {
        get => _soundpadFormat;
        set => SetTemplate(ref _soundpadFormat, value, "🎶 '{sound}'", _settings.ChatboxSoundpadFormat,
            next => _settings.ChatboxSoundpadFormat = next);
    }

    public string HeartRateFormat
    {
        get => _heartRateFormat;
        set => SetTemplate(ref _heartRateFormat, value, "❤ {bpm} bpm", _settings.ChatboxHeartRateFormat,
            next => _settings.ChatboxHeartRateFormat = next);
    }

    public string TwitchFormat
    {
        get => _twitchFormat;
        set => SetTemplate(ref _twitchFormat, value, "{twitch}", _settings.ChatboxTwitchFormat,
            next => _settings.ChatboxTwitchFormat = next);
    }

    public string TikTokFormat
    {
        get => _tikTokFormat;
        set => SetTemplate(ref _tikTokFormat, value, "TikTok @{profile} | {followers} followers",
            _settings.ChatboxTikTokFormat, next => _settings.ChatboxTikTokFormat = next);
    }

    public string SpotifyFormat
    {
        get => _spotifyFormat;
        set => SetTemplate(ref _spotifyFormat, value, "▶ {display}{source}", _settings.ChatboxSpotifyFormat,
            next => _settings.ChatboxSpotifyFormat = next);
    }

    public string DiscordFormat
    {
        get => _discordFormat;
        set => SetTemplate(ref _discordFormat, value, "🔊 {channel} ({count}) | 🎙 {speaking}",
            _settings.ChatboxDiscordFormat, next => _settings.ChatboxDiscordFormat = next);
    }

    public string VrBatteryFormat
    {
        get => _vrBatteryFormat;
        set => SetTemplate(ref _vrBatteryFormat, value, "{icon} {name} {batt}%",
            _settings.ChatboxVrBatteryFormat, next => _settings.ChatboxVrBatteryFormat = next);
    }

    public string VrPerformanceFormat
    {
        get => _vrPerformanceFormat;
        set => SetTemplate(ref _vrPerformanceFormat, value, "🎮 {fps} FPS",
            _settings.ChatboxVrPerformanceFormat, next => _settings.ChatboxVrPerformanceFormat = next);
    }

    public string PulsoidToken
    {
        get => _pulsoidToken;
        set
        {
            if (!SetProperty(ref _pulsoidToken, value ?? string.Empty))
            {
                return;
            }

            _settings.ChatboxPulsoidToken = _pulsoidToken;
            Save();
            _lastPulsoidRefresh = DateTime.MinValue;
        }
    }

    public string TwitchClientId
    {
        get => _twitchClientId;
        set
        {
            if (!SetProperty(ref _twitchClientId, value ?? string.Empty))
            {
                return;
            }

            _settings.ChatboxTwitchClientId = _twitchClientId;
            Save();
            _lastTwitchRefresh = DateTime.MinValue;
        }
    }

    public string TwitchAccessToken
    {
        get => _twitchAccessToken;
        set
        {
            if (!SetProperty(ref _twitchAccessToken, value ?? string.Empty))
            {
                return;
            }

            _settings.ChatboxTwitchAccessToken = _twitchAccessToken;
            Save();
            _lastTwitchRefresh = DateTime.MinValue;
        }
    }

    public string TwitchChannel
    {
        get => _twitchChannel;
        set
        {
            if (!SetProperty(ref _twitchChannel, value ?? string.Empty))
            {
                return;
            }

            _settings.ChatboxTwitchChannel = _twitchChannel;
            Save();
            _lastTwitchRefresh = DateTime.MinValue;
        }
    }

    public string TikTokUsername
    {
        get => _tikTokUsername;
        set
        {
            if (!SetProperty(ref _tikTokUsername, value ?? string.Empty))
            {
                return;
            }

            _settings.ChatboxTikTokUsername = _tikTokUsername;
            Save();
            _lastTikTokRefresh = DateTime.MinValue;
        }
    }

    public string TikTokText => string.IsNullOrWhiteSpace(_tikTokText)
        ? "Add a TikTok profile name above to read the follower count."
        : _tikTokText;

    public string DiscordText => string.IsNullOrWhiteSpace(_discordText)
        ? "Open Discord in this app, sign in, and join a voice channel."
        : _discordText;

    public string VrTrackerText => string.IsNullOrWhiteSpace(_vrTrackerText)
        ? "Start SteamVR to read headset, controller and tracker battery."
        : _vrTrackerText;

    public string VrPerformanceText => string.IsNullOrWhiteSpace(_vrPerformanceText)
        ? "Start SteamVR to read compositor frame timing."
        : _vrPerformanceText;

    public string VrBatteryDiagnosticText => string.IsNullOrWhiteSpace(_vrBatteryDiagnosticText)
        ? "Start SteamVR to inspect what it exposes for the headset, controllers and trackers."
        : _vrBatteryDiagnosticText;

    public NowPlayingInfo? CurrentTrack
    {
        get => _currentTrack;
        private set
        {
            if (!SetProperty(ref _currentTrack, value))
            {
                return;
            }

            OnPropertyChanged(nameof(NowPlayingText));
            OnPropertyChanged(nameof(HasNowPlaying));
            OnPropertyChanged(nameof(HasNoNowPlaying));
            OnPropertyChanged(nameof(MusicFormatPreview));
            AddNowPlayingAsStatusCommand.RaiseCanExecuteChanged();
            var lyricsKey = value is null
                ? string.Empty
                : $"{value.Title}\u001f{value.Artist}\u001f{(int)value.Duration.TotalSeconds}";
            if (!string.Equals(lyricsKey, _lyricsTrackKey, StringComparison.Ordinal))
            {
                _lyricsTrackKey = lyricsKey;
                _lyricsDocument = null;
                CurrentLyric = string.Empty;
                if (IncludeLyrics)
                {
                    _ = LoadLyricsForCurrentTrackAsync();
                }
            }

            RefreshPreview();
        }
    }

    public string NowPlayingText => CurrentTrack?.DisplayText ?? "Nothing playing right now.";

    public bool HasNowPlaying => CurrentTrack is not null;

    public bool HasNoNowPlaying => CurrentTrack is null;

    public string MusicFormat
    {
        get => _musicFormat;
        set
        {
            var normalized = NormalizeMusicFormat(value);
            if (!SetProperty(ref _musicFormat, normalized))
            {
                return;
            }

            _settings.ChatboxMusicFormat = normalized;
            Save();
            OnPropertyChanged(nameof(MusicFormatPreview));
            RefreshPreview();
        }
    }

    public string MusicFormatPreview => CurrentTrack is null
        ? "Play a track to preview the music line."
        : FormatMusic(CurrentTrack);

    public WindowActivityInfo? CurrentWindow
    {
        get => _currentWindow;
        private set
        {
            if (!SetProperty(ref _currentWindow, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CurrentWindowText));
            OnPropertyChanged(nameof(HasCurrentWindow));
            OnPropertyChanged(nameof(HasNoCurrentWindow));
            OnPropertyChanged(nameof(WindowFormatPreview));
            AddWindowAsStatusCommand.RaiseCanExecuteChanged();
            RefreshPreview();
        }
    }

    public string CurrentWindowText => CurrentWindow?.DisplayText ?? "No foreground app detected.";

    public bool HasCurrentWindow => CurrentWindow is not null;

    public bool HasNoCurrentWindow => CurrentWindow is null;

    public string WindowFormat
    {
        get => _windowFormat;
        set
        {
            var normalized = NormalizeWindowFormat(value);
            if (!SetProperty(ref _windowFormat, normalized))
            {
                return;
            }

            _settings.ChatboxWindowFormat = normalized;
            Save();
            OnPropertyChanged(nameof(WindowFormatPreview));
            RefreshPreview();
        }
    }

    public string WindowFormatPreview => CurrentWindow is null
        ? "Switch to another app to preview the active-app line."
        : FormatWindow(CurrentWindow);

    public string CurrentLyric
    {
        get => _currentLyric;
        private set
        {
            if (!SetProperty(ref _currentLyric, value))
            {
                return;
            }

            OnPropertyChanged(nameof(LyricsText));
            RefreshPreview();
        }
    }

    public string LyricsText => string.IsNullOrWhiteSpace(CurrentLyric) ? _lyricsStatusText : CurrentLyric;

    public string LyricsStatusText
    {
        get => _lyricsStatusText;
        private set
        {
            if (SetProperty(ref _lyricsStatusText, value))
            {
                OnPropertyChanged(nameof(LyricsText));
            }
        }
    }

    public string Separator
    {
        get => _separator;
        set
        {
            if (!SetProperty(ref _separator, value))
            {
                return;
            }

            _settings.ChatboxSeparator = value;
            Save();
            RefreshPreview();
        }
    }

    public bool SeparateWithEnters
    {
        get => _separateWithEnters;
        set
        {
            if (!SetProperty(ref _separateWithEnters, value))
            {
                return;
            }

            _settings.ChatboxSeparateWithEnters = value;
            Save();
            RefreshPreview();
        }
    }

    public string LinePrefix
    {
        get => _linePrefix;
        set
        {
            if (!SetProperty(ref _linePrefix, value ?? string.Empty))
            {
                return;
            }

            _settings.ChatboxPrefix = _linePrefix;
            Save();
            RefreshPreview();
        }
    }

    public string LineSuffix
    {
        get => _lineSuffix;
        set
        {
            if (!SetProperty(ref _lineSuffix, value ?? string.Empty))
            {
                return;
            }

            _settings.ChatboxSuffix = _lineSuffix;
            Save();
            RefreshPreview();
        }
    }

    public int ScanSeconds
    {
        get => _scanSeconds;
        set
        {
            var clamped = Math.Clamp(value, 1, 60);
            if (!SetProperty(ref _scanSeconds, clamped))
            {
                return;
            }

            _settings.ChatboxScanSeconds = clamped;
            _tickTimer.Interval = TimeSpan.FromSeconds(clamped);
            Save();
        }
    }

    public string KawaiiEmoji
    {
        get => _kawaiiEmoji;
        set
        {
            var normalized = NormalizeKawaiiEmoji(value);
            if (!SetProperty(ref _kawaiiEmoji, normalized))
            {
                return;
            }

            _settings.KawaiiEmoji = normalized;
            Save();
            OnPropertyChanged(nameof(KawaiiChatboxTitle));
        }
    }

    public string KawaiiChatboxTitle => $"KawaiiChatbox {KawaiiEmoji}".Trim();

    public IReadOnlyList<KawaiiAccentPreset> KawaiiAccentPresets { get; }

    public string KawaiiAccentInput
    {
        get => _kawaiiAccentInput;
        set => SetProperty(ref _kawaiiAccentInput, value ?? string.Empty);
    }

    public string KawaiiAccentHex
    {
        get => _kawaiiAccent;
        private set
        {
            var normalized = NormalizeHex(value, _kawaiiAccent);
            if (string.Equals(_kawaiiAccent, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _kawaiiAccent = normalized;
            _settings.KawaiiAccent = normalized;
            Save();
            OnPropertyChanged(nameof(KawaiiAccentHex));
            RaiseKawaiiAccentChanged();
        }
    }

    public string KawaiiAccentFeedback
    {
        get => _kawaiiAccentFeedback;
        private set => SetProperty(ref _kawaiiAccentFeedback, value);
    }

    public Color KawaiiAccentColor => ParseColor(_kawaiiAccent);

    public Color KawaiiAccentSoftColor => Blend(KawaiiAccentColor, Color.FromRgb(0x10, 0x08, 0x1C), 0.82);

    public Color KawaiiHeaderStartColor => Blend(KawaiiAccentColor, Colors.White, 0.18);

    public Color KawaiiHeaderEndColor => Blend(KawaiiAccentColor, Color.FromRgb(0x12, 0x0A, 0x22), 0.78);

    public Color KawaiiHeaderTextColor => Blend(KawaiiAccentColor, Colors.White, 0.70);

    public Color KawaiiPanelColor => Blend(KawaiiAccentColor, Color.FromRgb(0x10, 0x08, 0x1C), 0.86);

    public Color KawaiiBorderColor => Blend(KawaiiAccentColor, Color.FromRgb(0x10, 0x08, 0x1C), 0.68);

    public Color KawaiiChipColor => Blend(KawaiiAccentColor, Color.FromRgb(0x10, 0x08, 0x1C), 0.76);

    public Color KawaiiMutedColor => Blend(KawaiiAccentColor, Colors.White, 0.72);

    public Color KawaiiShellColor => Blend(KawaiiAccentColor, Color.FromRgb(0x08, 0x05, 0x10), 0.93);

    public Color KawaiiTabStripColor => Blend(KawaiiAccentColor, Color.FromRgb(0x10, 0x08, 0x1C), 0.88);

    public Color KawaiiListColor => Blend(KawaiiAccentColor, Color.FromRgb(0x10, 0x08, 0x1C), 0.90);

    /// <summary>
    /// MagicChatBox's master switch: when this is off the app is a preview only
    /// and no chatbox packets are sent to VRChat.
    /// </summary>
    public bool OscEnabled
    {
        get => _oscEnabled;
        set
        {
            if (!SetProperty(ref _oscEnabled, value))
            {
                return;
            }

            _settings.ChatboxOscEnabled = value;
            Save();
            _lastSentLine = string.Empty;
            _lastOscSentUtc = DateTime.MinValue;

            if (value)
            {
                StatusText = "VRChat output is on. Automatic status will be sent now.";
                Tick();
            }
            else
            {
                // Match MagicChatBox: turning the master switch off clears the
                // in-world line instead of leaving stale text behind.
                SendClearPacket();
                StatusText = "VRChat output is off. Nothing is sent to VRChat.";
            }

            OnPropertyChanged(nameof(OscStatusText));
            OnPropertyChanged(nameof(ChatboxDisplayTag));
            OnPropertyChanged(nameof(ChatboxDisplayStatus));
            OnPropertyChanged(nameof(PreviewStatusText));
        }
    }

    public string OscStatusText => OscEnabled ? "On" : "Off";

    public bool IsVrRunning => _isVrRunning;

    public string OutputModeText => IsVrRunning ? "VR" : "Desktop";

    public string Host
    {
        get => _host;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
            if (!SetProperty(ref _host, trimmed))
            {
                return;
            }

            _osc.Host = trimmed;
            _settings.OscHost = trimmed;
            Save();
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            var clamped = Math.Clamp(value, 1, 65535);
            if (!SetProperty(ref _port, clamped))
            {
                return;
            }

            _osc.Port = clamped;
            _settings.OscPort = clamped;
            Save();
        }
    }

    public string PreviewLine
    {
        get
        {
            var line = _liveTypingHolding
                ? ChatText
                : _chatOverride
                    ? ChatHistory.FirstOrDefault(item => item.IsRunning)?.Message ?? string.Empty
                    : BuildAutomaticLine();

            if (string.IsNullOrWhiteSpace(line))
            {
                return "No status selected. Add a status line or enable an integration.";
            }

            return OscMessage.FitChatboxText(line, OscMessage.ChatboxLimit);
        }
    }

    public string PreviewCountText
    {
        get
        {
            var text = PreviewLine;
            return text.StartsWith("No status selected", StringComparison.Ordinal)
                ? string.Empty
                : $"{text.Length}/{OscMessage.ChatboxLimit}";
        }
    }

    public bool IsPreviewOverLimit => PreviewLine.Length > OscMessage.ChatboxLimit;

    public string ProviderSummaryText => _lastBuild.TrimmedProviders.Count == 0
        ? $"{_lastBuild.IncludedProviders.Count} providers in the line · {_lastBuild.Length}/{ChatLimit}"
        : $"{_lastBuild.IncludedProviders.Count} kept · trimmed {string.Join(", ", _lastBuild.TrimmedProviders)}";

    public string PreviewStatusText => _liveTypingHolding
        ? "Live typing is holding the chatbox. Automatic status is paused until you send, stop typing, or clear the draft."
        : !OscEnabled
            ? "VRChat output is off. This is a preview only."
        : IsComposing
            ? "Draft is local. Nothing is sent to VRChat until you press Send or enable Live typing."
            : _chatOverride
                ? $"Your chat is showing. Automatic status returns in {Math.Max(0, (int)Math.Ceiling((_chatVisibleUntil - DateTime.UtcNow).TotalSeconds))}s."
                : string.IsNullOrWhiteSpace(BuildAutomaticLine())
                    ? "Automatic status is empty."
                    : "This is the automatic line VRChat is showing.";

    public string PreviewText => PreviewLine;

    public string ChatboxDisplayText => PreviewLine;

    public string ChatboxDisplayHint => PreviewStatusText;

    public string ChatboxDisplayCountText => PreviewCountText;

    public string ChatboxDisplayTitle => IsChattingPage
        ? "Chat message"
        : IsIntegrationsPage ? "Integration preview" : "Automatic status";

    public string ChatboxDisplayTag => IsLiveTypingShowing
        ? "TYPING"
        : !OscEnabled
            ? "OFF"
        : IsComposing
            ? "DRAFT"
            : IsChatShowing
                ? "CHAT"
                : IsIntegrationsPage ? "PROVIDERS" : "STATUS";

    public string ChatboxDisplayStatus => !OscEnabled
        ? "VRChat output off"
        : IsChatShowing ? "Chat override" : "Status live";

    internal void Tick()
    {
        if (_disposed)
        {
            return;
        }

        RefreshAfkState();

        if (!OscEnabled)
        {
            return;
        }

        RefreshOutputMode();
        var now = DateTime.UtcNow;
        RefreshWindowActivity();
        RefreshLyricLine();

        // A live draft owns the chatbox. Without this guard the automatic
        // status scan overwrites the draft every second, which is exactly the
        // "my text changes by itself" behaviour this state machine prevents.
        if (_liveTypingHolding)
        {
            OnPropertyChanged(nameof(ChatCountdownText));
            return;
        }

        // A non-empty composer is a local draft. Keep the current VRChat line
        // untouched until Send; otherwise the automatic scan refreshes the
        // chatbox while the user is typing.
        if (IsComposing)
        {
            OnPropertyChanged(nameof(ChatCountdownText));
            return;
        }

        if (_chatOverride)
        {
            if (now >= _chatVisibleUntil)
            {
                EndChatOverride(clearChatbox: true, "Chat expired. Automatic status resumed.");
            }
            else
            {
                var running = ChatHistory.FirstOrDefault(item => item.IsRunning);
                if (KeepUpdatingChat &&
                    running is not null &&
                    now - _lastChatUpdate >= TimeSpan.FromSeconds(ChatUpdateRateSeconds))
                {
                    SendPreparedLine(running.Message);
                    _lastChatUpdate = now;
                }

                OnPropertyChanged(nameof(ChatCountdownText));
                OnPropertyChanged(nameof(PreviewStatusText));
            }

            return;
        }

        if (CycleStatus && now - _lastCycle >= TimeSpan.FromSeconds(CycleSeconds))
        {
            if (CycleCandidates().Count > 1)
            {
                CycleToNextStatus();
            }

            _lastCycle = now;
        }

        var line = BuildAutomaticLine();
        if (!string.IsNullOrWhiteSpace(line))
        {
            var changed = !string.Equals(line, _lastSentLine, StringComparison.Ordinal);
            var keepAliveDue = now - _lastOscSentUtc >= OscKeepAlive;
            if (changed || keepAliveDue)
            {
                SendPreparedLine(line);
                _lastSentLine = line;
            }
        }
        else if (_lastSentLine.Length > 0)
        {
            SendPreparedLine(string.Empty);
            _lastSentLine = string.Empty;
        }
    }

    internal void CycleStatusForTest() => CycleToNextStatus();

    private void RefreshOutputMode()
    {
        var running = VrModeDetector.IsVrRunning();
        if (running == _isVrRunning)
        {
            return;
        }

        _isVrRunning = running;
        OnPropertyChanged(nameof(IsVrRunning));
        OnPropertyChanged(nameof(OutputModeText));
        RefreshPreview();
    }

    internal void RefreshForTest() => Tick();

    private void RefreshAfkState()
    {
        var next = AfkEnabled &&
                   _afk.GetIdleTime() >= TimeSpan.FromSeconds(AfkTimeoutSeconds);
        if (next == _isAfk)
        {
            return;
        }

        IsAfk = next;
        OnPropertyChanged(nameof(AfkStateText));
        RefreshPreview();
    }

    internal void SetVrModeForTest(bool isVrRunning)
    {
        _isVrRunning = isVrRunning;
        RefreshPreview();
    }

    internal void SetTrackForTest(NowPlayingInfo? track) => CurrentTrack = track;

    internal void SetWindowForTest(WindowActivityInfo? window) => CurrentWindow = window;

    internal void FlushLiveTypingForTest() => SendLiveTypingPreview();

    internal Task LyricsLoadTaskForTest => _lyricsLoadTask ?? Task.CompletedTask;

    private Task? _lyricsLoadTask;

    private void SetPage(string page)
    {
        if (_activePage == page)
        {
            return;
        }

        _activePage = page;
        OnPropertyChanged(nameof(IsStatusPage));
        OnPropertyChanged(nameof(IsChattingPage));
        OnPropertyChanged(nameof(IsIntegrationsPage));
        OnPropertyChanged(nameof(IsOptionsPage));
        OnPropertyChanged(nameof(IsStatusTab));
        OnPropertyChanged(nameof(IsChatTab));
        OnPropertyChanged(nameof(ChatboxDisplayTitle));
        OnPropertyChanged(nameof(ChatboxDisplayTag));
        OnPropertyChanged(nameof(ChatboxDisplayTag));
    }

    private void WireStatusItem(ChatboxStatusItem item)
    {
        item.Changed += (_, _) =>
        {
            SaveStatuses();
            RefreshPreview();
        };
    }

    private void WireChatItem(ChatboxChatItem item)
    {
        item.EditTextChanged += (_, _) => OnChatEditTextChanged(item);
    }

    private void AddStatus()
    {
        var text = OscMessage.FitChatboxText(NewStatusText.Trim(), MaxStatusLength);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (StatusItems.Any(item => item.Message.Equals(text, StringComparison.Ordinal)))
        {
            StatusText = "That status is already in the library.";
            return;
        }

        var item = new ChatboxStatusItem
        {
            Message = text,
            UseInCycle = true,
            GroupId = _selectedStatusGroupId,
            SortOrder = StatusItems.Count,
            CreatedAt = DateTime.UtcNow
        };
        WireStatusItem(item);
        StatusItems.Add(item);
        NewStatusText = string.Empty;
        if (ActiveStatus is null)
        {
            SetActiveStatus(item, save: false);
        }

        SaveStatuses();
        RaiseStatusCollectionChanged();
        RefreshPreview();
        StatusText = $"Status added ({StatusItems.Count}).";
    }

    private void SetActiveStatus(ChatboxStatusItem? item, bool save)
    {
        foreach (var status in StatusItems)
        {
            status.IsActive = ReferenceEquals(status, item);
        }

        if (item is not null)
        {
            item.LastUsedAt = DateTime.UtcNow;
            _settings.ChatboxActiveStatusId = item.Id;
        }
        else
        {
            _settings.ChatboxActiveStatusId = null;
        }

        if (save)
        {
            SaveStatuses();
        }

        _lastCycle = DateTime.UtcNow;
        OnPropertyChanged(nameof(ActiveStatus));
        OnPropertyChanged(nameof(ActiveStatusText));
        OnPropertyChanged(nameof(StatusPreviewText));
        OnPropertyChanged(nameof(StatusModeText));
        RefreshPreview();
    }

    private void RemoveStatus(ChatboxStatusItem? item)
    {
        if (item is null)
        {
            return;
        }

        var wasActive = item.IsActive;
        StatusItems.Remove(item);
        if (wasActive)
        {
            SetActiveStatus(StatusItems.FirstOrDefault(), save: false);
        }

        SaveStatuses();
        RaiseStatusCollectionChanged();
        RefreshPreview();
        StatusText = "Status removed.";
    }

    private void ToggleStatusCycle(ChatboxStatusItem? item)
    {
        if (item is null)
        {
            return;
        }

        item.UseInCycle = !item.UseInCycle;
        SaveStatuses();
        CycleStatusNowCommand.RaiseCanExecuteChanged();
        RefreshPreview();
        StatusText = item.UseInCycle ? "Included in the status cycle." : "Removed from the status cycle.";
    }

    private void BeginStatusEdit(ChatboxStatusItem? item)
    {
        if (item is null)
        {
            return;
        }

        foreach (var status in StatusItems)
        {
            status.IsEditing = false;
        }

        item.EditText = item.Message;
        item.IsEditing = true;
        StatusText = "Editing status.";
    }

    private void SaveStatusEdit(ChatboxStatusItem? item)
    {
        if (item is null)
        {
            return;
        }

        var text = OscMessage.FitChatboxText(item.EditText.Trim(), MaxStatusLength);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (StatusItems.Any(status =>
                !ReferenceEquals(status, item) &&
                status.Message.Equals(text, StringComparison.Ordinal)))
        {
            StatusText = "That status already exists.";
            return;
        }

        item.EditText = text;
        item.Message = text;
        item.IsEditing = false;
        SaveStatuses();
        RaiseStatusCollectionChanged();
        RefreshPreview();
        StatusText = "Status updated.";
    }

    private void CancelStatusEdit(ChatboxStatusItem? item)
    {
        if (item is null)
        {
            return;
        }

        item.EditText = item.Message;
        item.IsEditing = false;
        StatusText = "Status edit cancelled.";
    }

    private void ClearStatuses()
    {
        StatusItems.Clear();
        SetActiveStatus(null, save: false);
        SaveStatuses();
        RaiseStatusCollectionChanged();
        RefreshPreview();
        StatusText = "Status library cleared.";
    }

    private void AddStatusGroup()
    {
        var name = NewGroupName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (StatusGroups.Any(group => group.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "That status group already exists.";
            return;
        }

        var group = new ChatboxStatusGroup { Name = name, IsActiveForCycle = true };
        group.Changed += (_, _) => SaveGroups();
        StatusGroups.Add(group);
        NewGroupName = string.Empty;
        _selectedStatusGroupId = group.Id;
        SaveGroups();
        RefreshStatusFilter();
        OnPropertyChanged(nameof(HasStatusGroups));
        OnPropertyChanged(nameof(SelectedGroupText));
        StatusText = $"Status group '{name}' created.";
    }

    private void DeleteStatusGroup(ChatboxStatusGroup? group)
    {
        if (group is null || !StatusGroups.Remove(group))
        {
            return;
        }

        foreach (var item in StatusItems.Where(item => item.GroupId == group.Id))
        {
            item.GroupId = null;
        }

        if (_selectedStatusGroupId == group.Id)
        {
            _selectedStatusGroupId = null;
        }

        SaveStatuses();
        SaveGroups();
        RefreshStatusFilter();
        OnPropertyChanged(nameof(HasStatusGroups));
        StatusText = "Status group deleted. Its statuses are ungrouped, not deleted.";
    }

    private void SelectStatusGroup(ChatboxStatusGroup? group)
    {
        _selectedStatusGroupId = group?.Id;
        RefreshStatusFilter();
        OnPropertyChanged(nameof(SelectedGroupText));
        StatusText = group is null ? "Showing every saved status." : $"Showing {group.DisplayName}.";
    }

    private void ToggleStatusGroupCycle(ChatboxStatusGroup? group)
    {
        if (group is null)
        {
            return;
        }

        group.IsActiveForCycle = !group.IsActiveForCycle;
        SaveGroups();
        CycleStatusNowCommand.RaiseCanExecuteChanged();
        RefreshPreview();
    }

    private void RefreshStatusFilter()
    {
        foreach (var group in StatusGroups)
        {
            group.IsFilterActive = group.Id == _selectedStatusGroupId;
        }

        FilteredStatusItems.Clear();
        var items = StatusItems
            .OrderBy(item => item.SortOrder)
            .Where(item => _selectedStatusGroupId is null || item.GroupId == _selectedStatusGroupId);
        foreach (var item in items)
        {
            FilteredStatusItems.Add(item);
        }

        OnPropertyChanged(nameof(HasNoFilteredStatuses));
        OnPropertyChanged(nameof(StatusCountText));
        SelectAllVisibleStatusesCommand?.RaiseCanExecuteChanged();
        AssignStatusToSelectedGroupCommand?.RaiseCanExecuteChanged();
    }

    private void AssignStatusToSelectedGroup(ChatboxStatusItem? item)
    {
        if (item is null || _selectedStatusGroupId is null)
        {
            return;
        }

        item.GroupId = _selectedStatusGroupId;
        SaveStatuses();
        RefreshPreview();
        StatusText = "Status moved into the selected group.";
    }

    private void ToggleStatusSelected(ChatboxStatusItem? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsSelected = !item.IsSelected;
        RaiseSelectionChanged();
    }

    private void SelectAllVisibleStatuses()
    {
        foreach (var item in FilteredStatusItems)
        {
            item.IsSelected = true;
        }

        RaiseSelectionChanged();
    }

    private void ClearStatusSelection()
    {
        foreach (var item in StatusItems)
        {
            item.IsSelected = false;
        }

        RaiseSelectionChanged();
    }

    private void DeleteSelectedStatuses()
    {
        var selected = StatusItems.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var activeRemoved = ActiveStatus is not null && selected.Contains(ActiveStatus);
        foreach (var item in selected)
        {
            StatusItems.Remove(item);
        }

        for (var index = 0; index < StatusItems.Count; index++)
        {
            StatusItems[index].SortOrder = index;
        }

        if (activeRemoved)
        {
            SetActiveStatus(StatusItems.FirstOrDefault(), save: false);
        }

        SaveStatuses();
        RaiseStatusCollectionChanged();
        RefreshPreview();
        StatusText = $"{selected.Count} status{(selected.Count == 1 ? string.Empty : "es")} deleted.";
    }

    private void MoveStatus(ChatboxStatusItem? item, int direction)
    {
        if (item is null)
        {
            return;
        }

        var visible = FilteredStatusItems.ToList();
        var index = visible.IndexOf(item);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= visible.Count)
        {
            return;
        }

        var other = visible[target];
        (item.SortOrder, other.SortOrder) = (other.SortOrder, item.SortOrder);
        var ordered = StatusItems.OrderBy(status => status.SortOrder).ToList();
        StatusItems.Clear();
        foreach (var status in ordered)
        {
            StatusItems.Add(status);
        }

        for (var i = 0; i < StatusItems.Count; i++)
        {
            StatusItems[i].SortOrder = i;
        }

        SaveStatuses();
        RefreshStatusFilter();
        RefreshPreview();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedStatusCountText));
        OnPropertyChanged(nameof(HasSelectedStatuses));
        ClearStatusSelectionCommand?.RaiseCanExecuteChanged();
        DeleteSelectedStatusesCommand?.RaiseCanExecuteChanged();
    }

    private void SaveGroups()
    {
        _settings.ChatboxStatusGroups = StatusGroups.ToList();
        Save();
        CycleStatusNowCommand.RaiseCanExecuteChanged();
    }

    private List<ChatboxStatusItem> CycleCandidates()
    {
        var activeGroupIds = StatusGroups
            .Where(group => group.IsActiveForCycle)
            .Select(group => group.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return StatusItems
            .Where(item => item.UseInCycle &&
                           (item.GroupId is null || activeGroupIds.Contains(item.GroupId)))
            .ToList();
    }

    private void CycleToNextStatus()
    {
        var candidates = CycleCandidates();
        if (candidates.Count == 0)
        {
            StatusText = "Mark at least one status as cycling first.";
            return;
        }

        ChatboxStatusItem next;
        if (RandomStatusOrder && candidates.Count > 1)
        {
            // MagicChatBox weights random picks by how long an item has been
            // waiting, so a status just shown is unlikely to repeat at once.
            var weights = candidates
                .Select(item => Math.Max(1, (DateTime.UtcNow - item.LastUsedAt).TotalSeconds) * Random.Shared.NextDouble())
                .ToList();
            var total = weights.Sum();
            var roll = total <= 0 ? 0 : Random.Shared.NextDouble() * total;
            var selected = 0;
            for (var i = 0; i < weights.Count; i++)
            {
                roll -= weights[i];
                if (roll <= 0)
                {
                    selected = i;
                    break;
                }

                selected = i;
            }

            next = candidates[selected];
        }
        else
        {
            var currentIndex = candidates.IndexOf(ActiveStatus!);
            next = candidates[(currentIndex + 1) % candidates.Count];
        }

        SetActiveStatus(next, save: true);
        StatusText = "Status changed.";
    }

    private void AddNowPlayingAsStatus()
    {
        if (CurrentTrack is null)
        {
            return;
        }

        NewStatusText = FormatMusic(CurrentTrack);
        SetPage("status");
        StatusText = "Now playing placed in the status editor.";
    }

    private void AddWindowAsStatus()
    {
        if (CurrentWindow is null)
        {
            return;
        }

        NewStatusText = FormatWindow(CurrentWindow);
        SetPage("status");
        StatusText = "Active app placed in the status editor.";
    }

    private void SendChat()
    {
        if (!OscEnabled)
        {
            StatusText = "VRChat output is off. Turn it on before sending a chat.";
            return;
        }

        var message = BuildChatOutgoing(ChatText);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StopLiveTyping();
        foreach (var old in ChatHistory)
        {
            old.IsRunning = false;
            old.CanEdit = false;
            old.IsEditing = false;
        }

        var item = new ChatboxChatItem
        {
            Message = message,
            CanEdit = LiveEdit,
            IsRunning = true,
            SentAt = DateTime.UtcNow
        };
        WireChatItem(item);
        ChatHistory.Add(item);
        while (ChatHistory.Count > MaxChatHistory)
        {
            ChatHistory.RemoveAt(0);
        }

        _chatOverride = true;
        _chatVisibleUntil = DateTime.UtcNow.AddSeconds(ChatVisibleSeconds);
        _lastChatUpdate = DateTime.UtcNow;
        _lastSentLine = message;

        SendPreparedLine(message, playSound: ChatFx);
        StopChatCommand.RaiseCanExecuteChanged();
        _suppressChatTextEffects = true;
        try
        {
            ChatText = string.Empty;
        }
        finally
        {
            _suppressChatTextEffects = false;
        }
        AutocompleteSuffix = string.Empty;
        SaveHistory();
        RaiseChatCollectionChanged();
        OnPropertyChanged(nameof(IsChatShowing));
        OnPropertyChanged(nameof(ChatCountdownText));
        OnPropertyChanged(nameof(PreviewLine));
        OnPropertyChanged(nameof(PreviewCountText));
        OnPropertyChanged(nameof(PreviewStatusText));
        OnPropertyChanged(nameof(ChatboxDisplayTag));
        OnPropertyChanged(nameof(ChatboxDisplayStatus));
        StatusText = $"Chat sent. It stays up for {ChatVisibleSeconds}s, then status resumes.";
    }

    private void ResendChat(ChatboxChatItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!OscEnabled)
        {
            StatusText = "VRChat output is off. Turn it on before resending a chat.";
            return;
        }

        foreach (var old in ChatHistory)
        {
            old.IsRunning = ReferenceEquals(old, item);
            old.CanEdit = ReferenceEquals(old, item) && LiveEdit;
            old.IsEditing = false;
        }

        item.IsRunning = true;
        _chatOverride = true;
        _chatVisibleUntil = DateTime.UtcNow.AddSeconds(ChatVisibleSeconds);
        _lastChatUpdate = DateTime.UtcNow;
        _lastSentLine = item.Message;
        SendPreparedLine(item.Message, playSound: ChatFx && SendAgainFx);
        StopChatCommand.RaiseCanExecuteChanged();
        RaiseChatCollectionChanged();
        OnPropertyChanged(nameof(IsChatShowing));
        OnPropertyChanged(nameof(ChatCountdownText));
        OnPropertyChanged(nameof(PreviewLine));
        OnPropertyChanged(nameof(PreviewStatusText));
        StatusText = "Chat sent again.";
    }

    private void BeginChatEdit(ChatboxChatItem? item)
    {
        if (item is null || !item.CanEdit)
        {
            return;
        }

        foreach (var old in ChatHistory)
        {
            old.IsEditing = false;
        }

        item.EditText = item.Message;
        item.IsEditing = true;
        SaveChatEditCommand.RaiseCanExecuteChanged();
        CancelChatEditCommand.RaiseCanExecuteChanged();
        StatusText = "Editing the shown chat. Changes can update live.";
    }

    private void OnChatEditTextChanged(ChatboxChatItem item)
    {
        if (!RealTimeEdit || !item.IsEditing || !item.IsRunning || _disposed)
        {
            return;
        }

        var text = BuildChatOutgoing(item.EditText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        item.Message = text;
        _lastChatUpdate = DateTime.UtcNow;
        SendPreparedLine(text);
        OnPropertyChanged(nameof(PreviewLine));
    }

    private void SaveChatEdit(ChatboxChatItem? item)
    {
        if (item is null)
        {
            return;
        }

        var text = BuildChatOutgoing(item.EditText);
        if (string.IsNullOrWhiteSpace(text))
        {
            CancelChatEdit(item);
            return;
        }

        item.Message = text;
        item.IsEditing = false;
        SaveChatEditCommand.RaiseCanExecuteChanged();
        CancelChatEditCommand.RaiseCanExecuteChanged();
        if (item.IsRunning)
        {
            SendPreparedLine(text);
            _lastSentLine = text;
            _lastChatUpdate = DateTime.UtcNow;
        }

        SaveHistory();
        OnPropertyChanged(nameof(PreviewLine));
        StatusText = "Chat updated.";
    }

    private void CancelChatEdit(ChatboxChatItem? item)
    {
        if (item is null)
        {
            return;
        }

        item.EditText = item.Message;
        item.IsEditing = false;
        SaveChatEditCommand.RaiseCanExecuteChanged();
        CancelChatEditCommand.RaiseCanExecuteChanged();
        StatusText = "Chat edit cancelled.";
    }

    private void ClearChatHistory()
    {
        EndChatOverride(clearChatbox: true, "Chat cleared. Automatic status will resume.");
        ChatHistory.Clear();
        SaveHistory();
        RaiseChatCollectionChanged();
    }

    private void EndChatOverride(bool clearChatbox, string feedback)
    {
        _chatOverride = false;
        _chatVisibleUntil = DateTime.MinValue;
        _lastSentLine = string.Empty;
        foreach (var item in ChatHistory)
        {
            item.IsRunning = false;
            item.CanEdit = false;
            item.IsEditing = false;
        }

        if (clearChatbox)
        {
            SendPreparedLine(string.Empty);
        }

        StatusText = feedback;
        RaiseChatCollectionChanged();
        OnPropertyChanged(nameof(IsChatShowing));
        OnPropertyChanged(nameof(ChatCountdownText));
        OnPropertyChanged(nameof(PreviewLine));
        OnPropertyChanged(nameof(PreviewCountText));
        OnPropertyChanged(nameof(PreviewStatusText));
        OnPropertyChanged(nameof(ChatboxDisplayTag));
        OnPropertyChanged(nameof(ChatboxDisplayStatus));
        StopChatCommand.RaiseCanExecuteChanged();
    }

    private string BuildChatOutgoing(string text)
    {
        var message = (text ?? string.Empty).Trim();
        if (message.Length == 0)
        {
            return string.Empty;
        }

        if (PrefixChat && !message.StartsWith("💬 ", StringComparison.Ordinal))
        {
            message = "💬 " + message;
        }

        return OscMessage.FitChatboxText(message, ChatLimit);
    }

    private string BuildAutomaticLine()
    {
        _lastBuild = _outputBuilder.Build(
            _providers,
            BuildProviderContext(),
            Separator,
            SeparateWithEnters,
            LinePrefix,
            LineSuffix);
        return _lastBuild.Message;
    }

    private ChatboxProviderContext BuildProviderContext(bool previewAll = false)
        => new()
        {
            IsVR = IsVrRunning,
            Now = DateTime.Now,
            Limit = ChatLimit,
            IncludeStatus = previewAll || IsProviderEnabled("Status"),
            IncludeMedia = previewAll || IsProviderEnabled("Media"),
            IncludeLyrics = previewAll || IsProviderEnabled("Lyrics"),
            IncludeWindow = previewAll || IsProviderEnabled("Window"),
            IncludeTime = previewAll || IsProviderEnabled("Time"),
            ActiveStatus = IsAfk ? AfkStatusMessage : ActiveStatus?.Message ?? string.Empty,
            ShowStatusIcon = ShowStatusIcon && !IsAfk,
            StatusIcon = StatusIcon,
            Track = CurrentTrack,
            Lyric = CurrentLyric,
            Window = CurrentWindow,
            MusicFormat = MusicFormat,
            LyricsFormat = LyricsFormat,
            WindowFormat = WindowFormat,
            TimeFormat = TimeFormat,
            EnabledIntegrations = BuildEnabledMap(previewAll),
            IntegrationTexts = BuildIntegrationTexts(),
            SuppressSpotifyInMedia = IsProviderEnabled("Spotify")
        };

    private IReadOnlyDictionary<string, bool> BuildEnabledMap(bool previewAll)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in ChatboxIntegrationCatalog.All)
        {
            map[definition.Key] = previewAll || IsProviderEnabled(definition.Key);
        }

        return map;
    }

    private IReadOnlyDictionary<string, string> BuildIntegrationTexts()
    {
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Weather"] = _weatherText,
            ["Network"] = _networkText,
            ["VrcRadar"] = _radarText,
            ["ComponentStats"] = _componentText,
            ["Soundpad"] = _soundpadText,
            ["HeartRate"] = _heartRateText,
            ["Discord"] = _discordText,
            ["Twitch"] = _twitchText,
            ["TikTokLive"] = _tikTokText,
            ["TrackerBattery"] = _vrTrackerText,
            ["VrPerformance"] = _vrPerformanceText,
            ["Spotify"] = _spotifyText
        };
        return texts;
    }

    private void BuildProviderOrder()
    {
        var defaultOrder = ChatboxIntegrationCatalog.Keys;
        var saved = (_settings.ChatboxProviderOrder ?? new List<string>())
            .Where(key => defaultOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var key in defaultOrder)
        {
            if (!saved.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                saved.Add(key);
            }
        }

        var ordered = new List<IChatboxProvider>();
        foreach (var key in saved)
        {
            var provider = _providers.FirstOrDefault(candidate =>
                candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (provider is not null)
            {
                ordered.Add(provider);
            }
        }

        _providers.Clear();
        _providers.AddRange(ordered);
        _settings.ChatboxProviderOrder = saved;

        IntegrationCards.Clear();
        for (var index = 0; index < _providers.Count; index++)
        {
            var provider = _providers[index];
            var definition = ChatboxIntegrationCatalog.Get(provider.Key);
            IntegrationCards.Add(new ChatboxIntegrationCard(
                provider.Key,
                definition.DisplayName,
                definition.Description,
                index + 1,
                IsProviderTurnedOn(provider.Key),
                enabled => SetIntegrationEnabled(provider.Key, enabled),
                definition.Category,
                definition.SetupHint,
                definition.NeedsSetup,
                definition.IsAvailable,
                definition.UnavailableReason,
                IsProviderAllowedInMode(provider.Key, isVr: true),
                IsProviderAllowedInMode(provider.Key, isVr: false),
                enabled => SetIntegrationMode(provider.Key, isVr: true, enabled),
                enabled => SetIntegrationMode(provider.Key, isVr: false, enabled)));
        }

        RefreshIntegrationCards();
    }

    private bool IsProviderEnabled(string key)
        => IsProviderTurnedOn(key) && IsProviderAllowedInMode(key, IsVrRunning);

    private bool IsProviderTurnedOn(string key)
    {
        if (!ChatboxIntegrationCatalog.Get(key).IsAvailable)
        {
            return false;
        }

        return key switch
        {
            "Status" => IncludeStatus,
            "Media" => IncludeMusic,
            "Lyrics" => IncludeLyrics,
            "Window" => IncludeWindow,
            "Time" => IncludeTime,
            _ => _settings.ChatboxIntegrationEnabled.TryGetValue(key, out var enabled) && enabled
        };
    }

    private bool IsProviderAllowedInMode(string key, bool isVr)
    {
        var modes = isVr
            ? _settings.ChatboxIntegrationVr
            : _settings.ChatboxIntegrationDesktop;
        return !modes.TryGetValue(key, out var allowed) || allowed;
    }

    private void SetIntegrationMode(string key, bool isVr, bool enabled)
    {
        if (!ChatboxIntegrationCatalog.Get(key).IsAvailable)
        {
            return;
        }

        var modes = isVr
            ? _settings.ChatboxIntegrationVr
            : _settings.ChatboxIntegrationDesktop;
        modes[key] = enabled;
        Save();
        RefreshPreview();
        PollIntegrations();
    }

    private void SetIntegrationEnabled(string key, bool enabled)
    {
        var definition = ChatboxIntegrationCatalog.Get(key);
        if (!definition.IsAvailable)
        {
            StatusText = $"{definition.DisplayName} is unavailable.";
            return;
        }

        switch (key)
        {
            case "Status":
                IncludeStatus = enabled;
                return;
            case "Media":
                IncludeMusic = enabled;
                return;
            case "Lyrics":
                IncludeLyrics = enabled;
                return;
            case "Window":
                IncludeWindow = enabled;
                return;
            case "Time":
                IncludeTime = enabled;
                return;
        }

        _settings.ChatboxIntegrationEnabled[key] = enabled;
        Save();
        if (!enabled)
        {
            ClearIntegrationData(key);
        }
        else if (key is "Weather" or "VrRadar" or "Network" or "ComponentStats" or
            "Soundpad" or "HeartRate" or "Twitch" or "TikTokLive")
        {
            PollIntegrations();
        }

        RefreshPreview();
    }

    private void ClearIntegrationData(string key)
    {
        switch (key)
        {
            case "Weather":
                _weatherText = string.Empty;
                OnPropertyChanged(nameof(WeatherText));
                break;
            case "Network":
                _networkText = string.Empty;
                OnPropertyChanged(nameof(NetworkText));
                break;
            case "VrRadar":
                _radarText = string.Empty;
                OnPropertyChanged(nameof(RadarText));
                break;
            case "ComponentStats":
                _componentText = string.Empty;
                OnPropertyChanged(nameof(ComponentText));
                break;
            case "Soundpad":
                _soundpadText = string.Empty;
                OnPropertyChanged(nameof(SoundpadText));
                break;
            case "HeartRate":
                _heartRateText = string.Empty;
                OnPropertyChanged(nameof(HeartRateText));
                break;
            case "Twitch":
                _twitchText = string.Empty;
                OnPropertyChanged(nameof(TwitchText));
                break;
            case "TikTokLive":
                _tikTokText = string.Empty;
                OnPropertyChanged(nameof(TikTokText));
                break;
            case "Spotify":
                _spotifyText = string.Empty;
                OnPropertyChanged(nameof(SpotifyText));
                break;
        }
    }

    private void MoveIntegration(ChatboxIntegrationCard? card, int direction)
    {
        if (card is null)
        {
            return;
        }

        var index = IntegrationCards.IndexOf(card);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= IntegrationCards.Count)
        {
            return;
        }

        IntegrationCards.Move(index, target);
        _settings.ChatboxProviderOrder = IntegrationCards.Select(item => item.Key).ToList();
        for (var i = 0; i < IntegrationCards.Count; i++)
        {
            IntegrationCards[i].Order = i + 1;
        }

        var provider = _providers[index];
        _providers.RemoveAt(index);
        _providers.Insert(target, provider);
        Save();
        RefreshPreview();
        StatusText = $"{card.DisplayName} moved to position {target + 1}.";
    }

    private void RefreshIntegrationCards()
    {
        if (IntegrationCards.Count == 0)
        {
            return;
        }

        var previewContext = BuildProviderContext(previewAll: true);
        foreach (var provider in _providers)
        {
            var card = IntegrationCards.FirstOrDefault(candidate => candidate.Key == provider.Key);
            if (card is null)
            {
                continue;
            }

            card.IsEnabled = IsProviderTurnedOn(provider.Key);
            card.IsTrimmed = _lastBuild.TrimmedProviders.Contains(provider.Key, StringComparer.OrdinalIgnoreCase);
            if (!card.IsAvailable || !card.IsEnabled)
            {
                card.IsTrimmed = false;
                card.Preview = string.Empty;
                continue;
            }

            var preview = provider.Build(previewContext);
            card.Preview = string.IsNullOrWhiteSpace(preview) ? string.Empty : preview.Trim();
        }
    }

    private void RefreshPreview()
    {
        BuildAutomaticLine();
        RefreshIntegrationCards();
        OnPropertyChanged(nameof(PreviewLine));
        OnPropertyChanged(nameof(PreviewCountText));
        OnPropertyChanged(nameof(PreviewStatusText));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(IsPreviewOverLimit));
        OnPropertyChanged(nameof(ChatboxDisplayText));
        OnPropertyChanged(nameof(ChatboxDisplayHint));
        OnPropertyChanged(nameof(ChatboxDisplayCountText));
        OnPropertyChanged(nameof(ChatboxDisplayTag));
        OnPropertyChanged(nameof(ChatboxDisplayStatus));
        OnPropertyChanged(nameof(StatusPreviewText));
        OnPropertyChanged(nameof(ActiveStatusText));
        OnPropertyChanged(nameof(StatusModeText));
        OnPropertyChanged(nameof(DraftPreviewText));
        OnPropertyChanged(nameof(ProviderSummaryText));
        OnPropertyChanged(nameof(SelectedStatusCountText));
        OnPropertyChanged(nameof(HasSelectedStatuses));
    }

    /// <summary>
    /// Sends one chatbox line the way MagicChatBox does: every /chatbox/input
    /// uses sendImmediately=true, otherwise VRChat ignores updates while its
    /// chatbox is closed and the text only appears in this app's preview.
    /// </summary>
    private void SendPreparedLine(string text, bool playSound = false)
    {
        if (!OscEnabled)
        {
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _osc.SendChatbox(string.Empty, sendImmediately: true, playSound: false);
                _lastOscSentUtc = DateTime.UtcNow;
                return;
            }

            var payload = OscMessage.FitChatboxText(text, OscMessage.ChatboxLimit);
            _osc.SendChatbox(payload, sendImmediately: true, playSound);
            _lastOscSentUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not send to VRChat: {ex.Message}";
        }
    }

    private void SendClearPacket()
    {
        try
        {
            _osc.SendChatbox(string.Empty, sendImmediately: true, playSound: false);
            _lastOscSentUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not clear VRChat: {ex.Message}";
        }
    }

    private void ScheduleLiveTyping()
    {
        if (!LiveTyping || _disposed)
        {
            return;
        }

        _liveTypingTimer.Stop();
        _liveTypingFinalizeTimer.Stop();
        if (string.IsNullOrWhiteSpace(ChatText))
        {
            _liveTypingHolding = false;
            return;
        }

        _liveTypingHolding = true;
        OnPropertyChanged(nameof(IsLiveTypingShowing));
        OnPropertyChanged(nameof(PreviewLine));
        OnPropertyChanged(nameof(PreviewStatusText));
        _liveTypingTimer.Start();
        _liveTypingFinalizeTimer.Start();
    }

    private void SendLiveTypingPreview()
    {
        if (!LiveTyping || !OscEnabled || _disposed)
        {
            return;
        }

        _liveTypingTimer.Stop();
        var draft = BuildChatOutgoing(ChatText);
        if (string.IsNullOrWhiteSpace(draft))
        {
            StopLiveTyping(clearDraft: true);
            return;
        }

        _liveTypingHolding = true;
        SendPreparedLine(draft);
        _lastSentLine = draft;
        OnPropertyChanged(nameof(IsLiveTypingShowing));
        OnPropertyChanged(nameof(PreviewLine));
        OnPropertyChanged(nameof(PreviewStatusText));
        OnPropertyChanged(nameof(ChatboxDisplayTag));
        if (LiveTyping && !string.IsNullOrWhiteSpace(ChatText) && !_disposed)
        {
            _liveTypingTimer.Start();
        }

        StatusText = "Live typing preview is in VRChat. Automatic status is paused.";
    }

    private void StopLiveTyping(bool clearDraft = false)
    {
        var wasHolding = _liveTypingHolding;
        _liveTypingHolding = false;
        _liveTypingTimer.Stop();
        _liveTypingFinalizeTimer.Stop();
        OnPropertyChanged(nameof(IsLiveTypingShowing));
        OnPropertyChanged(nameof(PreviewLine));
        OnPropertyChanged(nameof(PreviewStatusText));
        OnPropertyChanged(nameof(ChatboxDisplayTag));

        if (!clearDraft || !wasHolding)
        {
            return;
        }

        if (_chatOverride)
        {
            var running = ChatHistory.FirstOrDefault(item => item.IsRunning);
            if (running is not null)
            {
                SendPreparedLine(running.Message);
                _lastSentLine = running.Message;
                return;
            }
        }

        SendPreparedLine(string.Empty);
        _lastSentLine = string.Empty;
    }

    private void UpdateAutocomplete()
    {
        AutocompleteSuffix = string.Empty;
        if (!AutocompleteEnabled || ChatText.Length < AutocompleteMinChars)
        {
            return;
        }

        var query = ChatText.Trim();
        var candidate = ChatHistory
                            .Select(item => item.Message)
                            .Concat(StatusItems.Select(item => item.Message))
                            .FirstOrDefault(text =>
                                text.Length > query.Length &&
                                text.StartsWith(query, StringComparison.OrdinalIgnoreCase));

        if (candidate is null)
        {
            return;
        }

        var suffix = candidate[query.Length..];
        var words = suffix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > AutocompleteMaxWords)
        {
            suffix = string.Join(" ", words.Take(AutocompleteMaxWords));
        }

        AutocompleteSuffix = suffix;
    }

    private void AcceptAutocomplete()
    {
        if (AutocompleteSuffix.Length == 0)
        {
            return;
        }

        ChatText += AutocompleteSuffix;
        AutocompleteSuffix = string.Empty;
    }

    private void RefreshWindowActivity()
        => CurrentWindow = _windowActivity.GetCurrent();

    private async Task PollNowPlayingAsync()
    {
        if (_pollingMedia)
        {
            return;
        }

        _pollingMedia = true;
        try
        {
            var track = await _nowPlaying.GetCurrentAsync().ConfigureAwait(true);
            if (!Equals(track, CurrentTrack))
            {
                CurrentTrack = track;
            }

            UpdateSpotifyText(track);
        }
        catch
        {
            // Media sessions can disappear at any time; the next poll retries.
        }
        finally
        {
            _pollingMedia = false;
        }
    }

    private void PollIntegrations()
    {
        if (_disposed || _pollingIntegrations)
        {
            return;
        }

        _pollingIntegrations = true;
        // The probes below read NIC and component counters, which block for
        // hundreds of milliseconds. They run on the pool so the window keeps
        // painting instead of freezing for the length of a poll.
        _ = PollIntegrationsAsync();
    }

    private async Task PollIntegrationsAsync()
    {
        try
        {
            var changed = false;

            if (IsProviderEnabled("Network"))
            {
                var network = await Task.Run(() => _networkStats.Poll()).ConfigureAwait(true);
                changed |= UpdateText(ref _networkText, network is null
                    ? string.Empty
                    : ChatboxTemplate.Format(
                        NetworkFormat,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["network"] = network.Text,
                            ["down"] = NetworkStatsSnapshot.FormatRate(network.DownBitsPerSecond),
                            ["up"] = NetworkStatsSnapshot.FormatRate(network.UpBitsPerSecond)
                        }));
            }

            if (IsProviderEnabled("ComponentStats"))
            {
                var system = await Task.Run(() => _systemStats.Poll()).ConfigureAwait(true);
                changed |= UpdateText(ref _componentText, system is null
                    ? string.Empty
                    : ChatboxTemplate.Format(
                        ComponentFormat,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["stats"] = system.Text,
                            ["cpu"] = system.CpuPercent.ToString("0"),
                            ["gpu"] = system.GpuPercent?.ToString("0") ?? string.Empty,
                            ["vram"] = system.VramUsedGb?.ToString("0.0") ?? string.Empty,
                            ["ram"] = system.MemoryPercent.ToString("0"),
                            ["ram_used"] = system.MemoryUsedGb.ToString("0.0"),
                            ["ram_total"] = system.MemoryTotalGb.ToString("0.0")
                        }));
            }

            if (IsProviderEnabled("VrcRadar"))
            {
                var radar = await Task.Run(() => _radarService.Poll()).ConfigureAwait(true);
                changed |= UpdateText(ref _radarText, radar is null
                    ? string.Empty
                    : ChatboxTemplate.Format(
                        RadarFormat,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["world"] = radar.WorldName,
                            ["players"] = radar.Players.ToString(),
                            ["instance"] = radar.InstanceType,
                            ["region"] = radar.Region
                        }));
            }

            UpdateSpotifyText(CurrentTrack);

            if (IsProviderEnabled("HeartRate") &&
                DateTime.UtcNow - _lastPulsoidRefresh >= TimeSpan.FromSeconds(20))
            {
                _lastPulsoidRefresh = DateTime.UtcNow;
                _ = PollPulsoidAsync();
            }

            if (IsProviderEnabled("Twitch") &&
                DateTime.UtcNow - _lastTwitchRefresh >= TimeSpan.FromSeconds(60))
            {
                _lastTwitchRefresh = DateTime.UtcNow;
                _ = PollTwitchAsync();
            }

            if (IsProviderEnabled("TikTokLive") &&
                DateTime.UtcNow - _lastTikTokRefresh >= TimeSpan.FromMinutes(10))
            {
                _lastTikTokRefresh = DateTime.UtcNow;
                _ = PollTikTokAsync();
            }

            if (IsProviderEnabled("Weather") &&
                DateTime.UtcNow - _lastWeatherRefresh >= TimeSpan.FromMinutes(10))
            {
                _lastWeatherRefresh = DateTime.UtcNow;
                _ = PollWeatherAsync();
            }

            if (IsProviderEnabled("Soundpad"))
            {
                _ = PollSoundpadAsync();
            }

            if (IsProviderEnabled("Discord") &&
                DiscordVoiceStateProvider is not null &&
                DateTime.UtcNow - _lastDiscordRefresh >= TimeSpan.FromSeconds(2))
            {
                _lastDiscordRefresh = DateTime.UtcNow;
                _ = PollDiscordAsync();
            }

            if ((IsProviderEnabled("TrackerBattery") || IsProviderEnabled("VrPerformance")) &&
                DateTime.UtcNow - _lastOpenVrRefresh >= TimeSpan.FromSeconds(5))
            {
                _lastOpenVrRefresh = DateTime.UtcNow;
                _ = PollOpenVrAsync();
            }

            if (changed)
            {
                OnPropertyChanged(nameof(NetworkText));
                OnPropertyChanged(nameof(ComponentText));
                OnPropertyChanged(nameof(RadarText));
                RefreshPreview();
            }
        }
        finally
        {
            _pollingIntegrations = false;
        }
    }

    private async Task PollDiscordAsync()
    {
        if (_pollingDiscord || DiscordVoiceStateProvider is not { } provider)
        {
            return;
        }

        _pollingDiscord = true;
        try
        {
            var snapshot = await provider().ConfigureAwait(true);
            if (UpdateText(ref _discordText, snapshot?.ToText(DiscordFormat)))
            {
                OnPropertyChanged(nameof(DiscordText));
                RefreshPreview();
            }
        }
        catch
        {
            // Discord may be navigating or signed out; the next poll retries.
        }
        finally
        {
            _pollingDiscord = false;
        }
    }

    private async Task PollOpenVrAsync()
    {
        try
        {
            var snapshot = await _openVrService.PollAsync().ConfigureAwait(true);
            var trackerChanged = UpdateText(ref _vrTrackerText, snapshot?.BuildTrackerText(VrBatteryFormat));
            var performanceChanged = UpdateText(ref _vrPerformanceText, snapshot?.BuildPerformanceText(VrPerformanceFormat));
            var diagnosticText = snapshot is null
                ? "SteamVR is not running, or its OpenVR runtime is unavailable."
                : snapshot.BuildBatteryDiagnosticText();
            var diagnosticChanged = UpdateText(ref _vrBatteryDiagnosticText, diagnosticText);
            if (trackerChanged)
            {
                OnPropertyChanged(nameof(VrTrackerText));
            }

            if (performanceChanged)
            {
                OnPropertyChanged(nameof(VrPerformanceText));
            }

            if (diagnosticChanged)
            {
                OnPropertyChanged(nameof(VrBatteryDiagnosticText));
            }

            if (trackerChanged || performanceChanged)
            {
                RefreshPreview();
            }
        }
        catch
        {
            // SteamVR can stop at any time; the next poll retries.
        }
    }

    private async Task PollWeatherAsync()
    {
        if (!IsProviderEnabled("Weather"))
        {
            return;
        }

        try
        {
            var weather = await _weatherService.RefreshAsync(WeatherCity).ConfigureAwait(true);
            if (!IsProviderEnabled("Weather"))
            {
                return;
            }

            if (weather is null)
            {
                if (UpdateText(ref _weatherText, string.Empty))
                {
                    OnPropertyChanged(nameof(WeatherText));
                    RefreshPreview();
                }

                return;
            }

            var text = ChatboxTemplate.Format(
                WeatherFormat,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["emoji"] = weather.Emoji,
                    ["temperature"] = weather.TemperatureC.ToString("0.#"),
                    ["temperature_f"] = (weather.TemperatureC * 9 / 5 + 32).ToString("0.#"),
                    ["feels"] = weather.FeelsLikeC.ToString("0.#"),
                    ["humidity"] = weather.Humidity.ToString(),
                    ["wind"] = weather.WindKph.ToString("0.#"),
                    ["condition"] = weather.Description,
                    ["city"] = weather.City,
                    ["detail"] = weather.DetailedText
                });
            if (UpdateText(ref _weatherText, text))
            {
                OnPropertyChanged(nameof(WeatherText));
                RefreshPreview();
            }
        }
        catch
        {
            // The next interval retries; weather never blocks the chatbox.
        }
    }

    private async Task PollPulsoidAsync()
    {
        if (!IsProviderEnabled("HeartRate"))
        {
            return;
        }

        var bpm = await _pulsoidService.PollAsync(PulsoidToken).ConfigureAwait(true);
        if (!IsProviderEnabled("HeartRate"))
        {
            return;
        }

        if (bpm is null)
        {
            if (UpdateText(ref _heartRateText, string.Empty))
            {
                OnPropertyChanged(nameof(HeartRateText));
                RefreshPreview();
            }

            return;
        }

        var text = ChatboxTemplate.Format(
            HeartRateFormat,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["bpm"] = bpm.Value.ToString()
            });
        if (UpdateText(ref _heartRateText, text))
        {
            OnPropertyChanged(nameof(HeartRateText));
            RefreshPreview();
        }
    }

    private async Task PollTwitchAsync()
    {
        if (!IsProviderEnabled("Twitch"))
        {
            return;
        }

        try
        {
            var twitch = await _twitchService.PollAsync(
                TwitchClientId,
                TwitchAccessToken,
                TwitchChannel).ConfigureAwait(true);
            if (!IsProviderEnabled("Twitch"))
            {
                return;
            }

            if (twitch is null)
            {
                if (UpdateText(ref _twitchText, string.Empty))
                {
                    OnPropertyChanged(nameof(TwitchText));
                    RefreshPreview();
                }

                return;
            }

            var text = ChatboxTemplate.Format(
                TwitchFormat,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["twitch"] = twitch.Text,
                    ["channel"] = twitch.Channel,
                    ["live"] = twitch.IsLive ? "LIVE" : "offline",
                    ["viewers"] = twitch.Viewers.ToString(),
                    ["game"] = twitch.Game,
                    ["title"] = twitch.Title
                });
            if (UpdateText(ref _twitchText, text))
            {
                OnPropertyChanged(nameof(TwitchText));
                RefreshPreview();
            }
        }
        catch
        {
            // The next interval retries.
        }
    }

    private async Task PollTikTokAsync()
    {
        if (!IsProviderEnabled("TikTokLive"))
        {
            return;
        }

        try
        {
            var profile = await _tikTokService.PollAsync(TikTokUsername).ConfigureAwait(true);
            if (!IsProviderEnabled("TikTokLive"))
            {
                return;
            }

            if (profile is null)
            {
                if (UpdateText(ref _tikTokText, string.Empty))
                {
                    OnPropertyChanged(nameof(TikTokText));
                    RefreshPreview();
                }

                return;
            }

            var text = ChatboxTemplate.Format(
                TikTokFormat,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["profile"] = profile.Username,
                    ["followers"] = profile.FollowersText,
                    ["followers_raw"] = profile.Followers.ToString()
                });
            if (UpdateText(ref _tikTokText, text))
            {
                OnPropertyChanged(nameof(TikTokText));
                RefreshPreview();
            }
        }
        catch
        {
            // Public profile scraping is best-effort; the next interval retries.
        }
    }

    private async Task PollSoundpadAsync()
    {
        if (!IsProviderEnabled("Soundpad"))
        {
            return;
        }

        try
        {
            var sound = await _soundpadService.PollAsync().ConfigureAwait(true);
            if (!IsProviderEnabled("Soundpad"))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(sound))
            {
                if (UpdateText(ref _soundpadText, string.Empty))
                {
                    OnPropertyChanged(nameof(SoundpadText));
                    RefreshPreview();
                }

                return;
            }

            var text = ChatboxTemplate.Format(
                SoundpadFormat,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sound"] = sound
                });
            if (UpdateText(ref _soundpadText, text))
            {
                OnPropertyChanged(nameof(SoundpadText));
                RefreshPreview();
            }
        }
        catch
        {
            // Soundpad is optional and may not be installed.
        }
    }

    private void UpdateSpotifyText(NowPlayingInfo? track)
    {
        var text = IsProviderEnabled("Spotify") &&
                   track is not null &&
                   track.Source.Contains("spotify", StringComparison.OrdinalIgnoreCase)
            ? ChatboxTemplate.Format(
                SpotifyFormat,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["display"] = track.DisplayText,
                    ["title"] = track.Title,
                    ["artist"] = track.Artist,
                    ["source"] = " · Spotify",
                    ["state"] = track.IsPlaying ? "▶" : "⏸"
                })
            : string.Empty;

        if (UpdateText(ref _spotifyText, text))
        {
            OnPropertyChanged(nameof(SpotifyText));
            RefreshPreview();
        }
    }

    private static bool UpdateText(ref string field, string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.Equals(field, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        field = normalized;
        return true;
    }

    private void SetTemplate(
        ref string field,
        string? value,
        string fallback,
        string persisted,
        Action<string> persist)
    {
        var next = NormalizeTemplate(value, fallback);
        if (!SetProperty(ref field, next))
        {
            return;
        }

        persist(next);
        Save();
        RefreshPreview();
    }

    private static string NormalizeTemplate(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();


    private Task LoadLyricsForCurrentTrackAsync()
    {
        _lyricsCancellation?.Cancel();
        _lyricsCancellation?.Dispose();
        _lyricsDocument = null;
        CurrentLyric = string.Empty;

        var track = CurrentTrack;
        if (!IncludeLyrics || track is null)
        {
            LyricsStatusText = IncludeLyrics
                ? "Play a track to look for lyrics."
                : "Lyrics are off.";
            return Task.CompletedTask;
        }

        var cancellation = new CancellationTokenSource();
        _lyricsCancellation = cancellation;
        var task = LoadLyricsAsync(track, cancellation);
        _lyricsLoadTask = task;
        return task;
    }

    private async Task LoadLyricsAsync(NowPlayingInfo track, CancellationTokenSource cancellation)
    {
        try
        {
            LyricsStatusText = "Looking for lyrics…";
            var result = await _lyricsService.GetForTrackAsync(
                track.Title,
                track.Artist,
                cancellation.Token,
                track.Duration).ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _lyricsDocument = result.Lyrics;
            LyricsStatusText = result.Status;
            RefreshLyricLine();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LyricsStatusText = $"Lyrics lookup failed: {ex.Message}";
        }
    }

    private void RefreshLyricLine()
    {
        if (!IncludeLyrics || _lyricsDocument is null)
        {
            return;
        }

        var line = _lyricsDocument.PreviewLine(CurrentTrack?.Position ?? TimeSpan.Zero);
        if (!string.IsNullOrWhiteSpace(line) &&
            !string.Equals(line, CurrentLyric, StringComparison.Ordinal))
        {
            CurrentLyric = line.Trim();
        }
    }

    private void SaveStatuses()
    {
        for (var index = 0; index < StatusItems.Count; index++)
        {
            StatusItems[index].SortOrder = index;
        }

        _settings.ChatboxStatusItems = StatusItems.ToList();
        Save();
        OnPropertyChanged(nameof(HasStatusItems));
        OnPropertyChanged(nameof(HasNoStatusItems));
        OnPropertyChanged(nameof(StatusCountText));
        OnPropertyChanged(nameof(ActiveStatus));
        OnPropertyChanged(nameof(ActiveStatusText));
        CycleStatusNowCommand.RaiseCanExecuteChanged();
        RefreshStatusFilter();
    }

    private void SaveHistory()
    {
        _settings.ChatboxHistory = ChatHistory.Select(item => item.Message).ToList();
        Save();
    }

    private void RaiseStatusCollectionChanged()
    {
        OnPropertyChanged(nameof(HasStatusItems));
        OnPropertyChanged(nameof(HasNoStatusItems));
        OnPropertyChanged(nameof(StatusCountText));
        OnPropertyChanged(nameof(ActiveStatus));
        OnPropertyChanged(nameof(ActiveStatusText));
        CycleStatusNowCommand.RaiseCanExecuteChanged();
        RefreshStatusFilter();
        RaiseSelectionChanged();
    }

    private void RaiseChatCollectionChanged()
    {
        OnPropertyChanged(nameof(HasChatHistory));
        OnPropertyChanged(nameof(HasNoChatHistory));
        ClearChatHistoryCommand.RaiseCanExecuteChanged();
        StopChatCommand.RaiseCanExecuteChanged();
        BeginChatEditCommand.RaiseCanExecuteChanged();
        SaveChatEditCommand.RaiseCanExecuteChanged();
        CancelChatEditCommand.RaiseCanExecuteChanged();
    }

    private static string NormalizeMusicFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ? ChatboxProviderContext.DefaultMusicFormat : format.Trim();

    private static string NormalizeWindowFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ? "🪟 {app}" : format.Trim();

    private static string NormalizeLyricsFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ? ChatboxProviderContext.DefaultLyricsFormat : format.Trim();

    private static string NormalizeTimeFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ? "{time}" : format.Trim();

    private static string NormalizeKawaiiEmoji(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return string.Concat(text.EnumerateRunes().Take(3).Select(rune => rune.ToString()));
    }

    private static string NormalizeHex(string? value, string fallback)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return fallback;
        }

        if (!text.StartsWith('#'))
        {
            text = "#" + text;
        }

        if (text.Length == 4)
        {
            text = $"#{text[1]}{text[1]}{text[2]}{text[2]}{text[3]}{text[3]}";
        }

        if (text.Length != 7 || !text[1..].All(Uri.IsHexDigit))
        {
            return fallback;
        }

        return text.ToUpperInvariant();
    }

    private static bool TryNormalizeHex(string? value, out string normalized)
    {
        normalized = NormalizeHex(value, string.Empty);
        return normalized.Length > 0;
    }

    private static Color ParseColor(string value)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value)!;
        }
        catch
        {
            return Color.FromRgb(0x5A, 0x37, 0x96);
        }
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        var ratio = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * ratio),
            (byte)Math.Round(from.G + (to.G - from.G) * ratio),
            (byte)Math.Round(from.B + (to.B - from.B) * ratio));
    }

    private void ApplyKawaiiAccent()
    {
        if (!TryNormalizeHex(KawaiiAccentInput, out var normalized))
        {
            KawaiiAccentFeedback = "Use a hex color like #E85D9E.";
            return;
        }

        KawaiiAccentHex = normalized;
        KawaiiAccentInput = normalized;
        KawaiiAccentFeedback = $"Applied {normalized}.";
    }

    private void RaiseKawaiiAccentChanged()
    {
        OnPropertyChanged(nameof(KawaiiAccentColor));
        OnPropertyChanged(nameof(KawaiiAccentSoftColor));
        OnPropertyChanged(nameof(KawaiiHeaderStartColor));
        OnPropertyChanged(nameof(KawaiiHeaderEndColor));
        OnPropertyChanged(nameof(KawaiiHeaderTextColor));
        OnPropertyChanged(nameof(KawaiiPanelColor));
        OnPropertyChanged(nameof(KawaiiBorderColor));
        OnPropertyChanged(nameof(KawaiiChipColor));
        OnPropertyChanged(nameof(KawaiiMutedColor));
        OnPropertyChanged(nameof(KawaiiShellColor));
        OnPropertyChanged(nameof(KawaiiTabStripColor));
        OnPropertyChanged(nameof(KawaiiListColor));
    }

    private void InsertChatEmoji(string? emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji))
        {
            return;
        }

        ChatText += emoji;
    }

    private string FormatMusic(NowPlayingInfo track)
    {
        var source = track.Source.Length > 0 ? $" · {track.Source}" : string.Empty;
        var text = MusicFormat
            .Replace("{display}", track.DisplayText, StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", track.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("{artist}", track.Artist, StringComparison.OrdinalIgnoreCase)
            .Replace("{sourceName}", track.Source, StringComparison.OrdinalIgnoreCase)
            .Replace("{source}", source, StringComparison.OrdinalIgnoreCase)
            .Replace("{state}", track.IsPlaying ? "▶" : "⏸", StringComparison.OrdinalIgnoreCase)
            .Replace("{elapsed}", FormatClock(track.Position), StringComparison.OrdinalIgnoreCase)
            .Replace("{remaining}", FormatClock(track.Duration - track.Position, roundUp: true), StringComparison.OrdinalIgnoreCase)
            .Replace("{duration}", FormatClock(track.Duration), StringComparison.OrdinalIgnoreCase)
            .Replace("{progress}", MediaChatboxProvider.ProgressBar(track.Position, track.Duration), StringComparison.OrdinalIgnoreCase)
            .Trim();
        return text.Length == 0 ? track.ChatboxText : text;
    }

    private string FormatWindow(WindowActivityInfo window)
    {
        var text = WindowFormat
            .Replace("{app}", window.App, StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", window.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("{window}", window.DisplayText, StringComparison.OrdinalIgnoreCase)
            .Trim();
        return text.Length == 0 ? window.App : text;
    }

    private static string FormatClock(TimeSpan value, bool roundUp = false)
        => MediaChatboxProvider.FormatClock(value, roundUp);

    private void Save() => _store.Save();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tickTimer.Stop();
        _mediaTimer.Stop();
        _integrationTimer.Stop();
        _liveTypingTimer.Stop();
        _liveTypingFinalizeTimer.Stop();
        _lyricsCancellation?.Cancel();
        _lyricsCancellation?.Dispose();
        _soundpadService.Dispose();
        _openVrService.Dispose();
        _systemStats.Dispose();
        if (_osc is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
