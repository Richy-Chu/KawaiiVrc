using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// The live instance popup. Reads the signed-in account's current location from
/// VRChat, loads the full instance and world records, and merges the current
/// VRChat log with the API user list so the roster is real, not generated.
/// </summary>
public sealed class InstanceViewModel : ViewModelBase
{
    private readonly VrcApiClient _api;
    private readonly ImageLoader _images;
    private readonly VrChatLogPlayerService _logService;
    private readonly ObservableCollection<InstancePlayerItem> _players = new();
    private readonly ICollectionView _playersView;
    private readonly DispatcherTimer _logTimer;
    private bool _logPolling;
    private CancellationTokenSource? _loadCancellation;
    private int _loadVersion;
    private VrcInstance? _instance;
    private VrcWorld? _world;
    private ImageSource? _worldImage;
    private string _playerSearch = string.Empty;
    private string _emptyTitle = "You're not in an instance";
    private string _emptyMessage =
        "Join a world in VRChat. This tab reads your current location from the VRChat API.";
    private string? _errorMessage;
    private bool _isLoading;
    private bool _isResolvingProfiles;
    private bool _hasCompletePlayerList;
    private string _ownerText = string.Empty;
    private string _ownerKind = string.Empty;
    private string _ownerAvatarUrl = string.Empty;
    private DateTimeOffset? _lastUpdated;
    private VrcUser? _lastCurrentUser;
    private IReadOnlyList<FriendItem> _lastFriends = Array.Empty<FriendItem>();
    private VrChatLogSnapshot? _lastLogSnapshot;
    private string _lastLogSignature = string.Empty;
    private bool _isActive;

    public InstanceViewModel(VrcApiClient api, ImageLoader images)
        : this(api, images, new VrChatLogPlayerService())
    {
    }

    internal InstanceViewModel(
        VrcApiClient api,
        ImageLoader images,
        VrChatLogPlayerService logService)
    {
        _api = api;
        _images = images;
        _logService = logService;
        _playersView = CollectionViewSource.GetDefaultView(_players);
        _playersView.Filter = FilterPlayer;
        _logTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Poll the local log every second. Reading only the appended tail is
            // cheap, and it makes joins/leaves show up as fast as VRCX instead
            // of waiting for the next API refresh.
            Interval = TimeSpan.FromSeconds(1)
        };
        _logTimer.Tick += (_, _) => PollLogForChanges();
    }

    /// <summary>Only poll the local log while the Instance popup is on screen.</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (active)
        {
            _logTimer.Start();
        }
        else
        {
            _logTimer.Stop();
        }
    }

    /// <summary>
    /// Raised when a fresh instance roster shows a player wearing a different
    /// avatar. Activity listens to this so it follows the roster the user can
    /// already see in the Instance popup.
    /// </summary>
    public event Action<VrcUser, VrcUser>? PlayerAvatarChanged;

    public VrcInstance? Instance
    {
        get => _instance;
        private set
        {
            var previousLocation = _instance?.Location;
            if (!SetProperty(ref _instance, value))
            {
                return;
            }

            // A live refresh of the same world must not move the reader; a
            // different world starts its roster at the first page.
            if (!string.Equals(previousLocation, value?.Location, StringComparison.Ordinal))
            {
                PlayersPage.ResetPage();
            }

            OnAllPropertiesChanged();
        }
    }

    public VrcWorld? World
    {
        get => _world;
        private set
        {
            if (SetProperty(ref _world, value))
            {
                OnAllPropertiesChanged();
            }
        }
    }

    public ImageSource? WorldImage
    {
        get => _worldImage;
        private set => SetProperty(ref _worldImage, value);
    }

    public ObservableCollection<InstancePlayerItem> Players => _players;

    public ICollectionView VisiblePlayers => _playersView;

    /// <summary>The roster one page at a time; an 80-person instance builds 24 rows.</summary>
    public PagedList<InstancePlayerItem> PlayersPage { get; } = new();

    public string PlayerSearch
    {
        get => _playerSearch;
        set
        {
            if (SetProperty(ref _playerSearch, value ?? string.Empty))
            {
                RefreshPlayersView(resetPage: true);
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnAllPropertiesChanged();
            }
        }
    }

    public bool IsNotLoading => !IsLoading;

    public bool HasInstance => Instance is not null;

    public bool ShowEmptyState => !IsLoading && !HasInstance && !HasError;

    public bool ShowContent => HasInstance;

    public string EmptyTitle
    {
        get => _emptyTitle;
        private set => SetProperty(ref _emptyTitle, value);
    }

    public string EmptyMessage
    {
        get => _emptyMessage;
        private set => SetProperty(ref _emptyMessage, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsResolvingProfiles
    {
        get => _isResolvingProfiles;
        private set
        {
            if (SetProperty(ref _isResolvingProfiles, value))
            {
                OnPropertyChanged(nameof(ProfileStatusText));
                OnPropertyChanged(nameof(HasProfileStatus));
            }
        }
    }

    public string ProfileStatusText => IsResolvingProfiles ? "Loading player profiles from VRChat…" : string.Empty;

    public bool HasProfileStatus => IsResolvingProfiles;

    public string WorldName => World?.Name ?? string.Empty;

    public string HeaderTitle => string.IsNullOrWhiteSpace(WorldName) ? "Live instance" : WorldName;

    public string WorldAuthorText => string.IsNullOrWhiteSpace(World?.AuthorName)
        ? string.Empty
        : $"by {World!.AuthorName}";

    public string WorldDescriptionText => string.IsNullOrWhiteSpace(World?.Description)
        ? "No world description was provided."
        : World!.Description!.Trim();

    public string WorldImageUrl => World is null
        ? string.Empty
        : ImageUrlHelper.PickWorldThumbnailUrl(
            World.ThumbnailImageUrl,
            World.ImageUrl,
            900) ?? string.Empty;

    public string WorldVisitsText => World is null ? "—" : World.Visits.ToString("N0", CultureInfo.CurrentCulture);

    public string WorldFavoritesText => World is null ? "—" : World.Favorites.ToString("N0", CultureInfo.CurrentCulture);

    public string WorldCapacityText => World is null
        ? "—"
        : World.Capacity > 0
            ? $"{World.Capacity:N0} people"
            : "Not specified";

    public string RecommendedCapacityText => World is null
        ? "—"
        : World.RecommendedCapacity > 0
            ? $"{World.RecommendedCapacity:N0} people"
            : "Not specified";

    public string WorldPublishedText => FormatDate(World?.PublicationDate);

    public string WorldUpdatedText => FormatDate(World?.UpdatedAt);

    public string WorldTagsText
    {
        get
        {
            var tags = World?.Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(CleanTag)
                .Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList() ?? new List<string>();
            return tags.Count == 0 ? "No world tags." : string.Join("  ·  ", tags);
        }
    }

    public string InstanceAccessText => Instance is null
        ? string.Empty
        : InstancePresentation.AccessLabel(Instance.Type, Instance.InstanceId);

    public string RegionText => Instance is null
        ? string.Empty
        : InstancePresentation.RegionLabel(Instance.Region ?? Instance.InstanceId);

    public string InstanceNumberText
    {
        get
        {
            var instance = Instance;
            if (instance is null)
            {
                return string.Empty;
            }

            var value = instance.InstanceId ?? string.Empty;
            var number = value.Split('~')[0];
            return string.IsNullOrWhiteSpace(number)
                ? instance.InstanceId ?? string.Empty
                : $"#{number}";
        }
    }

    public string InstanceLocation => Instance?.Location ?? string.Empty;

    public string ShareLink
    {
        get
        {
            if (Instance is null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(Instance.ShortName))
            {
                return $"https://vrch.at/{Uri.EscapeDataString(Instance.ShortName.Trim())}";
            }

            var worldId = Instance.WorldId;
            var instanceId = Instance.InstanceId;
            if (string.IsNullOrWhiteSpace(worldId) || string.IsNullOrWhiteSpace(instanceId))
            {
                return string.Empty;
            }

            return "https://vrchat.com/home/launch" +
                   $"?worldId={Uri.EscapeDataString(worldId)}" +
                   $"&instanceId={Uri.EscapeDataString(instanceId)}";
        }
    }

    public string OwnerText => _ownerText;

    public string OwnerKindText => _ownerKind;

    public string OwnerAvatarUrl => _ownerAvatarUrl;

    public bool HasOwner => !string.IsNullOrWhiteSpace(OwnerText);

    public bool HasOwnerAvatar => !string.IsNullOrWhiteSpace(OwnerAvatarUrl);

    public string AgeGateText => Instance is null
        ? string.Empty
        : Instance.AgeGate
            ? "18+ verified"
            : "All ages";

    public string QueueText
    {
        get
        {
            if (Instance is null)
            {
                return string.Empty;
            }

            return Instance.QueueEnabled
                ? Instance.QueueSize > 0
                    ? $"Queue open · {Instance.QueueSize:N0} waiting"
                    : "Queue open"
                : "No queue";
        }
    }

    public string PlatformsText
    {
        get
        {
            if (Instance?.Platforms is { Count: > 0 } platforms)
            {
                var parts = platforms
                    .OrderByDescending(pair => pair.Value)
                    .Select(pair => $"{PlatformName(pair.Key)} {pair.Value:N0}")
                    .ToList();
                if (parts.Count > 0)
                {
                    return string.Join("  ·  ", parts);
                }
            }

            if (World?.SupportedPlatforms is { Count: > 0 } supported)
            {
                return string.Join("  ·  ", supported.Select(PlatformName));
            }

            return "Not specified";
        }
    }

    public string InstanceTagsText
    {
        get
        {
            var tags = Instance?.Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(CleanTag)
                .Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList() ?? new List<string>();
            return tags.Count == 0 ? "No instance tags." : string.Join("  ·  ", tags);
        }
    }

    public string MinimumAvatarPerformanceText => string.IsNullOrWhiteSpace(Instance?.MinimumAvatarPerformance)
        ? "No avatar limit"
        : Instance!.MinimumAvatarPerformance!;

    public int TotalPlayerCount
    {
        get
        {
            var known = Players.Count;
            var reported = Instance?.Occupants ?? 0;
            return Math.Max(known, reported);
        }
    }

    public string PlayerCountText
    {
        get
        {
            var capacity = Instance?.Capacity > 0
                ? Instance!.Capacity
                : World?.Capacity ?? 0;
            return capacity > 0
                ? $"{TotalPlayerCount:N0} / {capacity:N0}"
                : $"{TotalPlayerCount:N0} players";
        }
    }

    public string PlayersHeader => $"Players  ·  {PlayerCountText}";

    public string PlayersSummary
    {
        get
        {
            var visible = VisiblePlayerCount;
            if (Players.Count == 0)
            {
                return string.Empty;
            }

            return visible == Players.Count
                ? $"{Players.Count:N0} known"
                : $"{visible:N0} shown of {Players.Count:N0} known";
        }
    }

    public bool HasPlayers => Players.Count > 0;

    public bool HasVisiblePlayers => VisiblePlayerCount > 0;

    public string PlayersEmptyMessage
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(PlayerSearch))
            {
                return $"No players match \u201c{PlayerSearch.Trim()}\u201d.";
            }

            return "VRChat did not return the player list for this instance.";
        }
    }

    public string PlayerListSourceText => _hasCompletePlayerList
        ? "VRChat log + API"
        : Instance?.Users.Count > 0
            ? "VRChat API"
            : "VRChat API + friends";

    public string PlayerListNotice
    {
        get
        {
            if (_hasCompletePlayerList)
            {
                return "Live roster from VRChat's local log. Every row is resolved through the VRChat API when available.";
            }

            if (Players.Count > 0 && TotalPlayerCount > Players.Count)
            {
                return $"VRChat only returns a full roster for instances you created. Showing {Players.Count:N0} known player(s) from your API and friends data.";
            }

            if (Players.Count > 0)
            {
                return "Player details come from the VRChat API.";
            }

            return "VRChat does not expose this instance's player list to this account.";
        }
    }

    public string LastUpdatedText => _lastUpdated is null
        ? string.Empty
        : $"Updated {_lastUpdated.Value.ToLocalTime():h:mm tt}";

    public async Task LoadAsync(
        VrcUser? currentUser,
        IReadOnlyList<FriendItem> friends,
        CancellationToken cancellationToken = default)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadCancellation = cancellation;
        var token = cancellation.Token;
        var version = ++_loadVersion;

        _lastCurrentUser = currentUser;
        _lastFriends = friends.ToList();

        ErrorMessage = null;
        IsLoading = true;

        try
        {
            var logSnapshot = _logService.Poll();
            _lastLogSnapshot = logSnapshot;
            var apiLocationValue = currentUser?.Location ?? string.Empty;
            var apiLocation = LaunchService.IsLocation(apiLocationValue)
                ? currentUser!.Location
                : null;
            var logLocation = logSnapshot is not null && LaunchService.IsLocation(logSnapshot.Location)
                ? logSnapshot.Location
                : null;
            // VRChat's web profile often lags behind the game or omits the
            // location entirely. While the game is running, its own log is the
            // same immediate source VRCX uses, so prefer it over the web value.
            var useLogLocation = _logService.IsGameRunning && logLocation is not null;
            var location = useLogLocation ? logLocation : apiLocation;
            _lastLogSignature = BuildLogSignature(logSnapshot);

            if (string.IsNullOrWhiteSpace(location))
            {
                ClearInstance();
                EmptyTitle = currentUser is null
                    ? "Sign in to see your instance"
                    : "You're not in an instance";
                EmptyMessage = currentUser is null
                    ? "Sign in with VRChat, then join a world to see its live instance."
                    : "Join a world in VRChat. This tab reads the live world from VRChat.";
                return;
            }

            var instance = await _api.GetInstanceAsync(location, token);
            if (instance is null)
            {
                ClearInstance();
                ErrorMessage = "VRChat could not load that instance. It may have closed.";
                return;
            }

            var worldId = instance.WorldId;
            if (string.IsNullOrWhiteSpace(worldId))
            {
                worldId = LaunchService.GetWorldIdFromLocation(location) ?? string.Empty;
            }

            var world = instance.World;
            if (world is null && !string.IsNullOrWhiteSpace(worldId))
            {
                world = await _api.GetWorldAsync(worldId, cancellationToken: token);
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            Instance = instance;
            World = world;
            _lastUpdated = DateTimeOffset.Now;
            OnPropertyChanged(nameof(LastUpdatedText));

            var logMatches = logSnapshot is not null &&
                             logLocation is not null &&
                             SameInstance(logLocation, instance.Location);
            _hasCompletePlayerList = logMatches;
            var players = BuildPlayers(instance, currentUser, friends, logSnapshot, logMatches);
            ReplacePlayers(players);
            OnAllPropertiesChanged();

            _ = LoadWorldImageAsync(world, token);
            _ = LoadOwnerAsync(instance, currentUser, token);
            _ = LoadCosmeticsAsync(players, version, token);
            _ = ResolvePlayerProfilesAsync(players, version, token);
            _ = ApplyBlockedMarkersAsync(players, version, token, logSnapshot);
        }
        catch (OperationCanceledException)
        {
            // A newer load or a tab close replaced this request.
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat. Check your internet connection.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not load this VRChat instance.";
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                OnAllPropertiesChanged();
            }
        }
    }

    public void Reset()
    {
        _logTimer.Stop();
        _isActive = false;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _loadVersion++;
        _logService.Reset();
        ClearInstance();
        ErrorMessage = null;
        IsLoading = false;
        IsResolvingProfiles = false;
        PlayerSearch = string.Empty;
        PlayersPage.Clear();
        _lastUpdated = null;
        _lastCurrentUser = null;
        _lastFriends = Array.Empty<FriendItem>();
        _lastLogSnapshot = null;
        _lastLogSignature = string.Empty;
        EmptyTitle = "You're not in an instance";
        EmptyMessage = "Join a world in VRChat to see its live instance.";
        OnAllPropertiesChanged();
    }

    public void SetError(string message)
    {
        ErrorMessage = message;
        IsLoading = false;
        OnAllPropertiesChanged();
    }

    public void Dispose() => _logTimer.Stop();

    /// <summary>Refreshes existing rows when the shared friend list arrives.</summary>
    public void ApplyFriends(IReadOnlyList<FriendItem> friends)
    {
        _lastFriends = friends.ToList();
        if (_players.Count == 0)
        {
            return;
        }

        if (Instance is not null &&
            _lastCurrentUser is not null &&
            _lastLogSnapshot is not null &&
            SameInstance(_lastLogSnapshot.Location, Instance.Location))
        {
            // The full friend list carries fresher avatars/locations than the
            // log-only rows. Rebuild immediately instead of leaving the roster
            // stale until a manual instance refresh.
            _hasCompletePlayerList = true;
            var rebuilt = BuildPlayers(
                Instance,
                _lastCurrentUser,
                _lastFriends,
                _lastLogSnapshot,
                logMatches: true);
            ReplacePlayers(rebuilt);
            ResolveMissingProfiles(rebuilt);
            return;
        }

        var byId = friends
            .Where(friend => !string.IsNullOrWhiteSpace(friend.Id))
            .GroupBy(friend => friend.Id)
            .ToDictionary(group => group.Key, group => group.First().User, StringComparer.Ordinal);
        foreach (var player in _players)
        {
            if (byId.TryGetValue(player.UserId, out var friend))
            {
                player.ApplyFriend(friend);
            }
        }
    }

    private void PollLogForChanges()
    {
        // The tick fires once a second while the popup is open, and the read it used to do here
        // is a file open and a parse. At that rate it was a slice of every second the popup was
        // up, taken from the thread that draws it.
        if (_logPolling || !_isActive || IsLoading || _lastCurrentUser is null)
        {
            return;
        }

        _logPolling = true;
        _ = PollLogForChangesAsync();
    }

    private async Task PollLogForChangesAsync()
    {
        try
        {
            var snapshot = await Task.Run(() => _logService.Poll()).ConfigureAwait(true);
            if (!_isActive || IsLoading)
            {
                return;
            }

            ApplyLogSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("instance log poll: " + ex.Message);
        }
        finally
        {
            _logPolling = false;
        }
    }

    private void ApplyLogSnapshot(VrChatLogSnapshot? snapshot)
    {
        if (_lastCurrentUser is null)
        {
            return;
        }

        var signature = BuildLogSignature(snapshot);
        if (signature.Length == 0 || string.Equals(signature, _lastLogSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastLogSignature = signature;
        _lastLogSnapshot = snapshot;

        // A join/leave inside the same instance does not change the instance,
        // world or owner. Rebuild the roster straight from the log so it lands
        // on the next frame instead of waiting for a full API round trip.
        if (snapshot is not null &&
            Instance is not null &&
            _lastCurrentUser is not null &&
            SameInstance(snapshot.Location, Instance.Location))
        {
            _hasCompletePlayerList = true;
            var rebuilt = BuildPlayers(
                Instance,
                _lastCurrentUser,
                _lastFriends,
                snapshot,
                logMatches: true);
            ReplacePlayers(rebuilt);
            ResolveMissingProfiles(rebuilt);
            _lastUpdated = DateTimeOffset.Now;
            OnPropertyChanged(nameof(LastUpdatedText));
            OnAllPropertiesChanged();
            return;
        }

        _ = LoadAsync(_lastCurrentUser, _lastFriends);
    }

    /// <summary>
    /// Reads the game log and applies the roster on demand, even while the Instance page is
    /// not the open section. The world dialog's Players panel uses this: the page's own poll
    /// only runs while the page is shown, so without it joins and leaves stalled until the
    /// reader switched tabs. It loads the instance first when the page has never been opened.
    /// </summary>
    internal async Task<bool> RefreshRosterFromLogAsync(
        VrcUser currentUser,
        IReadOnlyList<FriendItem> friends,
        string? location,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(location) || !LaunchService.IsLocation(location))
        {
            return false;
        }

        _lastCurrentUser = currentUser;
        _lastFriends = friends.ToList();

        var snapshot = await Task.Run(() => _logService.Poll(), cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var logLocation = snapshot is not null && LaunchService.IsLocation(snapshot.Location)
            ? snapshot.Location
            : null;
        var effective = _logService.IsGameRunning && logLocation is not null
            ? logLocation
            : currentUser.Location;

        if (!LaunchService.IsLocation(effective) || !SameInstance(effective, location))
        {
            return false;
        }

        if (Instance is null || !SameInstance(Instance.Location, effective))
        {
            await LoadAsync(currentUser, friends, cancellationToken);
            return Instance is not null && SameInstance(Instance.Location, location);
        }

        ApplyLogSnapshot(snapshot);
        return true;
    }

    internal static string BuildLogSignature(VrChatLogSnapshot? snapshot)
    {
        if (snapshot is null || !LaunchService.IsLocation(snapshot.Location))
        {
            return string.Empty;
        }

        var players = string.Join(
            "|",
            snapshot.Players
                .Select(player => player.UserId.Length > 0
                    ? player.UserId
                    : "name:" + player.DisplayName)
                .OrderBy(value => value, StringComparer.Ordinal));
        var blocked = snapshot.BlockedPlayerNames is { Count: > 0 } blockedNames
            ? string.Join("|", blockedNames.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
            : string.Empty;
        var unblocked = snapshot.UnblockedPlayerNames is { Count: > 0 } unblockedNames
            ? string.Join("|", unblockedNames.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
            : string.Empty;
        return snapshot.Location + "|" + players + "|blocked:" + blocked + "|unblocked:" + unblocked;
    }

    private List<InstancePlayerItem> BuildPlayers(
        VrcInstance instance,
        VrcUser? currentUser,
        IReadOnlyList<FriendItem> friends,
        VrChatLogSnapshot? logSnapshot,
        bool logMatches)
    {
        var currentId = currentUser?.Id ?? string.Empty;
        var friendById = friends
            .Where(friend => !string.IsNullOrWhiteSpace(friend.Id))
            .GroupBy(friend => friend.Id)
            .ToDictionary(group => group.Key, group => group.First().User, StringComparer.Ordinal);
        var friendIds = new HashSet<string>(friendById.Keys, StringComparer.Ordinal);
        var logBlockedNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        if (logSnapshot?.BlockedPlayerNames is { } blockedNames)
        {
            logBlockedNames.UnionWith(blockedNames);
        }
        var logUnblockedNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        if (logSnapshot?.UnblockedPlayerNames is { } unblockedNames)
        {
            logUnblockedNames.UnionWith(unblockedNames);
        }
        var apiUsers = instance.Users
            .Where(user => !string.IsNullOrWhiteSpace(user.Id))
            .GroupBy(user => user.Id)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var knownProfiles = _players
            .Where(player => player.User is not null && !string.IsNullOrWhiteSpace(player.UserId))
            .GroupBy(player => player.UserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().User!, StringComparer.Ordinal);

        var result = new List<InstancePlayerItem>();
        var byId = new Dictionary<string, InstancePlayerItem>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        void AddUser(VrcUser user)
        {
            if (string.IsNullOrWhiteSpace(user.Id))
            {
                return;
            }

            var isFriend = friendIds.Contains(user.Id) || user.IsFriend;
            var isSelf = string.Equals(user.Id, currentId, StringComparison.Ordinal);
            if (byId.TryGetValue(user.Id, out var existing))
            {
                existing.ApplyUser(user, isFriend, isSelf);
                return;
            }

            // Rebuilding the roster must not throw away a profile that was
            // already resolved. Log-only rows carry no username/avatar, so keep
            // the richer existing user for those.
            var effective = knownProfiles.TryGetValue(user.Id, out var known) &&
                            string.IsNullOrWhiteSpace(user.Username) &&
                            !string.IsNullOrWhiteSpace(known.Username)
                ? known
                : user;
            var item = new InstancePlayerItem(
                effective,
                isFriend,
                isSelf,
                isBlocked: logBlockedNames.Contains(user.DisplayName));
            byId[user.Id] = item;
            result.Add(item);
            names.Add(item.DisplayName);
        }

        foreach (var user in instance.Users)
        {
            AddUser(user);
        }

        if (logMatches && logSnapshot is not null)
        {
            foreach (var logPlayer in logSnapshot.Players)
            {
                if (!string.IsNullOrWhiteSpace(logPlayer.UserId))
                {
                    if (apiUsers.TryGetValue(logPlayer.UserId, out var apiUser))
                    {
                        AddUser(apiUser);
                    }
                    else if (friendById.TryGetValue(logPlayer.UserId, out var friendUser))
                    {
                        AddUser(friendUser);
                    }
                    else if (!byId.ContainsKey(logPlayer.UserId))
                    {
                        var item = new InstancePlayerItem(
                            logPlayer.UserId,
                            logPlayer.DisplayName,
                            isFriend: false,
                            isSelf: string.Equals(logPlayer.UserId, currentId, StringComparison.Ordinal),
                            isBlocked: logBlockedNames.Contains(logPlayer.DisplayName));
                        byId[logPlayer.UserId] = item;
                        result.Add(item);
                        names.Add(item.DisplayName);
                    }

                    continue;
                }

                // Older VRChat builds omit the user id. Keep the real name from
                // the log, but only if the API has not already given us this row.
                if (logPlayer.DisplayName.Length == 0 || names.Contains(logPlayer.DisplayName))
                {
                    continue;
                }

                var nameOnly = new InstancePlayerItem(
                    string.Empty,
                    logPlayer.DisplayName,
                    isFriend: false,
                    isSelf: false,
                    isBlocked: logBlockedNames.Contains(logPlayer.DisplayName));
                result.Add(nameOnly);
                names.Add(nameOnly.DisplayName);
            }
        }

        // VRChat exposes friends' real locations even when it hides the full
        // instance roster. Include anyone the API says is in this exact instance.
        foreach (var friend in friends)
        {
            if (SameInstance(friend.Location, instance.Location))
            {
                AddUser(friend.User);
            }
        }

        if (currentUser is not null && SameInstance(currentUser.Location, instance.Location))
        {
            AddUser(currentUser);
        }

        if (logUnblockedNames.Count > 0)
        {
            foreach (var player in result)
            {
                if (logUnblockedNames.Contains(player.DisplayName))
                {
                    player.SetBlocked(false);
                }
            }
        }

        return result
            .OrderByDescending(player => player.IsSelf)
            .ThenByDescending(player => player.IsFriend)
            .ThenBy(player => player.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task LoadCosmeticsAsync(
        IReadOnlyList<InstancePlayerItem> players,
        int version,
        CancellationToken cancellationToken)
    {
        if (CosmeticCatalog.IsLoaded)
        {
            foreach (var player in players)
            {
                player.RefreshPresentation();
            }
            return;
        }

        try
        {
            await CosmeticCatalog.EnsureLoadedAsync(_api, cancellationToken);
            if (tokenIsCurrent(version, cancellationToken))
            {
                foreach (var player in players)
                {
                    player.RefreshPresentation();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A newer instance load replaced this request.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        bool tokenIsCurrent(int expected, CancellationToken token) =>
            !token.IsCancellationRequested && expected == _loadVersion;
    }

    private async Task LoadWorldImageAsync(VrcWorld? world, CancellationToken cancellationToken)
    {
        if (world is null)
        {
            WorldImage = null;
            return;
        }

        var url = ImageUrlHelper.PickWorldThumbnailUrl(
            world.ThumbnailImageUrl,
            world.ImageUrl,
            900);
        if (string.IsNullOrWhiteSpace(url))
        {
            WorldImage = null;
            return;
        }

        try
        {
            var image = await _images.LoadAsync(url, 900, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && image is not null)
            {
                WorldImage = image;
            }
        }
        catch (OperationCanceledException)
        {
            // The popup closed or moved to another instance.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task LoadOwnerAsync(
        VrcInstance instance,
        VrcUser? currentUser,
        CancellationToken cancellationToken)
    {
        var ownerId = instance.OwnerId;
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            SetOwner("No owner listed", string.Empty, string.Empty);
            return;
        }

        try
        {
            if (ownerId.StartsWith("grp_", StringComparison.OrdinalIgnoreCase))
            {
                var group = await _api.GetGroupAsync(ownerId, cancellationToken);
                if (group is not null && !cancellationToken.IsCancellationRequested)
                {
                    SetOwner(group.Name, "Group", group.ThumbnailUrl);
                }
                else
                {
                    SetOwner(ownerId, "Group", string.Empty);
                }

                return;
            }

            if (string.Equals(ownerId, currentUser?.Id, StringComparison.Ordinal))
            {
                SetOwner(currentUser?.DisplayName ?? ownerId, "You", currentUser is null
                    ? string.Empty
                    : ImageUrlHelper.PickAvatarThumbnailUrl(
                        currentUser.UserIcon,
                        currentUser.ProfilePicOverrideThumbnail ?? currentUser.IconUrl,
                        currentUser.CurrentAvatarThumbnailImageUrl,
                        currentUser.CurrentAvatarImageUrl,
                        128) ?? string.Empty);
                return;
            }

            var user = await _api.GetUserAsync(ownerId, cancellationToken: cancellationToken);
            if (user is not null && !cancellationToken.IsCancellationRequested)
            {
                SetOwner(
                    user.DisplayName,
                    "Player",
                    ImageUrlHelper.PickAvatarThumbnailUrl(
                        user.UserIcon,
                        user.ProfilePicOverrideThumbnail ?? user.IconUrl,
                        user.CurrentAvatarThumbnailImageUrl,
                        user.CurrentAvatarImageUrl,
                        128) ?? string.Empty);
            }
            else
            {
                SetOwner(ownerId, "Player", string.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // Another instance was opened.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            SetOwner(ownerId, ownerId.StartsWith("grp_", StringComparison.OrdinalIgnoreCase)
                ? "Group"
                : "Player", string.Empty);
        }
    }

    private void SetOwner(string text, string kind, string avatarUrl)
    {
        _ownerText = text;
        _ownerKind = kind;
        _ownerAvatarUrl = avatarUrl;
        OnPropertyChanged(nameof(OwnerText));
        OnPropertyChanged(nameof(OwnerKindText));
        OnPropertyChanged(nameof(OwnerAvatarUrl));
        OnPropertyChanged(nameof(HasOwner));
        OnPropertyChanged(nameof(HasOwnerAvatar));
    }

    private async Task ApplyBlockedMarkersAsync(
        IReadOnlyList<InstancePlayerItem> players,
        int version,
        CancellationToken cancellationToken,
        VrChatLogSnapshot? logSnapshot)
    {
        if (players.Count == 0 || players.All(player => string.IsNullOrWhiteSpace(player.UserId)))
        {
            return;
        }

        IReadOnlySet<string> blockedUserIds;
        try
        {
            // Force a fresh list: a player can be blocked in VRChat while an
            // older API cache still says otherwise.
            blockedUserIds = await _api.GetBlockedUserIdsAsync(
                forceRefresh: true,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The popup moved to another instance.
            return;
        }
        catch (Exception ex)
        {
            // The log may already have marked a local block; a failed API list
            // must not erase it.
            Debug.WriteLine(ex);
            return;
        }

        if (cancellationToken.IsCancellationRequested || version != _loadVersion)
        {
            return;
        }

        var logBlockedNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        if (logSnapshot?.BlockedPlayerNames is { } blockedNames)
        {
            logBlockedNames.UnionWith(blockedNames);
        }
        var logUnblockedNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        if (logSnapshot?.UnblockedPlayerNames is { } unblockedNames)
        {
            logUnblockedNames.UnionWith(unblockedNames);
        }

        foreach (var player in players)
        {
            if (logUnblockedNames.Contains(player.DisplayName))
            {
                // This session saw the unblock; a stale API row must not put
                // the marker back on.
                player.SetBlocked(false);
            }
            else if (!string.IsNullOrWhiteSpace(player.UserId) &&
                     blockedUserIds.Contains(player.UserId))
            {
                player.SetBlocked(true);
            }
            else if (!logBlockedNames.Contains(player.DisplayName))
            {
                // The API is the active block set. With no matching block and
                // no current log block, a marker left from an earlier load is
                // stale and must come off.
                player.SetBlocked(false);
            }
        }
    }

    private void ResolveMissingProfiles(IReadOnlyList<InstancePlayerItem> players)
    {
        if (_loadCancellation is not { IsCancellationRequested: false } cancellation)
        {
            return;
        }

        _ = ResolvePlayerProfilesAsync(players, _loadVersion, cancellation.Token);
    }

    private async Task ResolvePlayerProfilesAsync(
        IReadOnlyList<InstancePlayerItem> players,
        int version,
        CancellationToken cancellationToken)
    {
        var unresolved = players
            .Where(player => !string.IsNullOrWhiteSpace(player.UserId) &&
                             (!player.HasProfile || string.IsNullOrWhiteSpace(player.AvatarUrl)))
            .ToList();
        if (unresolved.Count == 0)
        {
            IsResolvingProfiles = false;
            return;
        }

        IsResolvingProfiles = true;
        try
        {
            foreach (var player in unresolved)
            {
                if (cancellationToken.IsCancellationRequested || version != _loadVersion)
                {
                    return;
                }

                try
                {
                    if (!player.HasProfile)
                    {
                        var user = await _api.GetUserAsync(
                            player.UserId,
                            cancellationToken: cancellationToken);
                        if (user is not null && !cancellationToken.IsCancellationRequested)
                        {
                            player.ApplyUser(user, user.IsFriend || player.IsFriend, player.IsSelf);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(player.AvatarUrl))
                    {
                        var profile = await _api.GetProfileAsync(
                            player.UserId,
                            cancellationToken: cancellationToken);
                        if (profile is not null && !cancellationToken.IsCancellationRequested)
                        {
                            player.ApplyProfile(profile);
                        }
                    }

                    if (!player.HasProfile)
                    {
                        player.MarkProfileAttempted();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    player.MarkProfileAttempted();
                }
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsResolvingProfiles = false;
            }
        }
    }

    private void ReplacePlayers(IReadOnlyList<InstancePlayerItem> players)
    {
        var previousById = _players
            .Where(player => !string.IsNullOrWhiteSpace(player.UserId) && player.User is not null)
            .GroupBy(player => player.UserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().User!, StringComparer.Ordinal);

        foreach (var player in players)
        {
            if (player.User is not { } current ||
                !previousById.TryGetValue(player.UserId, out var previous))
            {
                continue;
            }

            var previousKey = AvatarKey(previous);
            var currentKey = AvatarKey(current);
            if (!string.IsNullOrWhiteSpace(previousKey) &&
                !string.IsNullOrWhiteSpace(currentKey) &&
                !string.Equals(previousKey, currentKey, StringComparison.Ordinal))
            {
                PlayerAvatarChanged?.Invoke(previous, current);
            }
        }

        _players.Clear();
        foreach (var player in players)
        {
            _players.Add(player);
        }

        RefreshPlayersView();
        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(TotalPlayerCount));
        OnPropertyChanged(nameof(PlayerCountText));
        OnPropertyChanged(nameof(PlayersHeader));
        OnPropertyChanged(nameof(PlayerListSourceText));
        OnPropertyChanged(nameof(PlayerListNotice));
        OnPropertyChanged(nameof(PlayersEmptyMessage));
    }

    private static string? AvatarKey(VrcUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.CurrentAvatar))
        {
            return user.CurrentAvatar;
        }

        if (!string.IsNullOrWhiteSpace(user.CurrentAvatarThumbnailImageUrl))
        {
            return user.CurrentAvatarThumbnailImageUrl;
        }

        return user.CurrentAvatarImageUrl;
    }

    private void ClearInstance()
    {
        Instance = null;
        World = null;
        WorldImage = null;
        _players.Clear();
        _lastLogSnapshot = null;
        _hasCompletePlayerList = false;
        _ownerText = string.Empty;
        _ownerKind = string.Empty;
        _ownerAvatarUrl = string.Empty;
        IsResolvingProfiles = false;
        OnPropertyChanged(nameof(OwnerText));
        OnPropertyChanged(nameof(OwnerKindText));
        OnPropertyChanged(nameof(OwnerAvatarUrl));
        OnPropertyChanged(nameof(HasOwner));
        OnPropertyChanged(nameof(HasOwnerAvatar));
        RefreshPlayersView();
        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(TotalPlayerCount));
        OnPropertyChanged(nameof(PlayerCountText));
        OnPropertyChanged(nameof(PlayersHeader));
        OnPropertyChanged(nameof(PlayerListSourceText));
        OnPropertyChanged(nameof(PlayerListNotice));
    }

    private void RefreshPlayersView(bool resetPage = false)
    {
        _playersView.Refresh();
        // Live joins and leaves update the rows without moving the reader off
        // the page they are reading; search and a new world reset it.
        PlayersPage.SetSource(_playersView.Cast<InstancePlayerItem>(), resetPage);
        OnPropertyChanged(nameof(HasVisiblePlayers));
        OnPropertyChanged(nameof(PlayersSummary));
        OnPropertyChanged(nameof(PlayersEmptyMessage));
    }

    private int VisiblePlayerCount => _playersView.Cast<object>().Count();

    private bool FilterPlayer(object item)
    {
        if (item is not InstancePlayerItem player)
        {
            return false;
        }

        var query = PlayerSearch.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        return player.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || player.Username.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || player.TrustText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || player.PlatformText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || player.LanguageText.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool SameInstance(string? left, string? right)
    {
        if (!LaunchService.IsLocation(left) || !LaunchService.IsLocation(right))
        {
            return false;
        }

        return string.Equals(InstanceKey(left!), InstanceKey(right!), StringComparison.Ordinal);
    }

    private static string InstanceKey(string location)
    {
        var value = location ?? string.Empty;
        var separator = value.IndexOf('~');
        return separator > 0 ? value[..separator] : value;
    }

    private static string FormatDate(DateTimeOffset? value)
        => value?.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture) ?? "—";

    private static string FormatDate(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)
            : "—";

    private static string PlatformName(string? value)
    {
        var platform = (value ?? string.Empty).ToLowerInvariant();
        return platform switch
        {
            "standalonewindows" or "standalonewindows64" or "pc" => "PC",
            "android" or "quest" => "Quest",
            "ios" => "iOS",
            "web" => "Web",
            "" => "Unknown",
            _ => char.ToUpperInvariant(platform[0]) + platform[1..]
        };
    }

    private static string CleanTag(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith("author_tag_", StringComparison.OrdinalIgnoreCase))
        {
            value = value["author_tag_".Length..];
        }
        else if (value.StartsWith("content_", StringComparison.OrdinalIgnoreCase))
        {
            value = value["content_".Length..];
        }

        return value.Replace('_', ' ').Trim();
    }
}

/// <summary>A real player row. Rows can start as a log-only name while their API profile loads.</summary>
public sealed class InstancePlayerItem : ViewModelBase
{

    private VrcUser? _user;
    private string _displayName;
    private string _userId;
    private bool _isFriend;
    private bool _isSelf;
    private bool _isBlocked;
    private bool _fromLog;
    private bool _profileAttempted;

    public InstancePlayerItem(
        VrcUser user,
        bool isFriend,
        bool isSelf,
        bool isBlocked = false)
    {
        _user = user;
        _userId = user.Id;
        _displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? "Unknown user" : user.DisplayName;
        _isSelf = isSelf;
        _isFriend = !isSelf && (isFriend || user.IsFriend);
        _isBlocked = isBlocked || user.IsBlocked;
    }

    public InstancePlayerItem(
        string userId,
        string displayName,
        bool isFriend,
        bool isSelf,
        bool isBlocked = false)
    {
        _userId = userId;
        _displayName = string.IsNullOrWhiteSpace(displayName) ? "Unknown player" : displayName;
        _isSelf = isSelf;
        _isFriend = !isSelf && isFriend;
        _isBlocked = isBlocked;
        _fromLog = true;
    }

    public string UserId => _userId;

    public VrcUser? User => _user;

    public bool CanOpenProfile => !string.IsNullOrWhiteSpace(_userId);

    public string DisplayName => _displayName;

    public string Username => _user?.Username ?? string.Empty;

    public bool IsFriend => !IsSelf && _isFriend;

    public bool IsSelf => _isSelf;

    /// <summary>
    /// True only when the signed-in account has blocked this user. VRChat does
    /// not expose the reverse direction to external clients, so the companion
    /// does not claim to know it.
    /// </summary>
    public bool IsBlocked => _isBlocked;

    public string BlockedMarker => _isBlocked ? "(B)" : string.Empty;

    public bool HasBlockedMarker => _isBlocked;

    /// <summary>
    /// Applies the live friend directory to a row that was created from local
    /// history or an instance payload, so relationship labels cannot go stale.
    /// </summary>
    public void ApplyFriendState(bool isFriend)
    {
        var next = !IsSelf && isFriend;
        if (_isFriend == next)
        {
            return;
        }

        _isFriend = next;
        OnPropertyChanged(nameof(IsFriend));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(HasBadge));
    }

    /// <summary>Applies the signed-in account's blocked-user list.</summary>
    public void SetBlocked(bool isBlocked)
    {
        if (_isBlocked == isBlocked)
        {
            return;
        }

        _isBlocked = isBlocked;
        NotifyBlockStateChanged();
    }

    private void NotifyBlockStateChanged()
    {
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(BlockedMarker));
        OnPropertyChanged(nameof(HasBlockedMarker));
        OnPropertyChanged(nameof(BlockedToolTip));
    }

    public string BlockedToolTip => _isBlocked
        ? "You have blocked this user in VRChat."
        : string.Empty;

    public bool HasProfile => _user is not null;

    /// <summary>
    /// Fills in fields the instance payload omitted from the friend-list copy.
    /// VRChat often leaves avatar URLs out of profile and instance responses for
    /// VRC+ accounts, while the friend record still has them.
    /// </summary>
    public void ApplyFriend(VrcUser friend)
    {
        if (_isSelf)
        {
            return;
        }

        _isFriend = true;
        if (_user is null)
        {
            ApplyUser(friend, isFriend: true, _isSelf);
            return;
        }

        var changed = false;
        if (string.IsNullOrWhiteSpace(_user.UserIcon) && !string.IsNullOrWhiteSpace(friend.UserIcon))
        {
            _user.UserIcon = friend.UserIcon;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.IconUrl) && !string.IsNullOrWhiteSpace(friend.IconUrl))
        {
            _user.IconUrl = friend.IconUrl;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.ProfilePicOverride) && !string.IsNullOrWhiteSpace(friend.ProfilePicOverride))
        {
            _user.ProfilePicOverride = friend.ProfilePicOverride;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.ProfilePicOverrideThumbnail) && !string.IsNullOrWhiteSpace(friend.ProfilePicOverrideThumbnail))
        {
            _user.ProfilePicOverrideThumbnail = friend.ProfilePicOverrideThumbnail;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.CurrentAvatarThumbnailImageUrl) && !string.IsNullOrWhiteSpace(friend.CurrentAvatarThumbnailImageUrl))
        {
            _user.CurrentAvatarThumbnailImageUrl = friend.CurrentAvatarThumbnailImageUrl;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.CurrentAvatarImageUrl) && !string.IsNullOrWhiteSpace(friend.CurrentAvatarImageUrl))
        {
            _user.CurrentAvatarImageUrl = friend.CurrentAvatarImageUrl;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.IconFrame) && !string.IsNullOrWhiteSpace(friend.IconFrame))
        {
            _user.IconFrame = friend.IconFrame;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.ProfileEffect) && !string.IsNullOrWhiteSpace(friend.ProfileEffect))
        {
            _user.ProfileEffect = friend.ProfileEffect;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.NameplateEffect) && !string.IsNullOrWhiteSpace(friend.NameplateEffect))
        {
            _user.NameplateEffect = friend.NameplateEffect;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.ThemeIconColor) && !string.IsNullOrWhiteSpace(friend.ThemeIconColor))
        {
            _user.ThemeIconColor = friend.ThemeIconColor;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.ThemeButtonColor) && !string.IsNullOrWhiteSpace(friend.ThemeButtonColor))
        {
            _user.ThemeButtonColor = friend.ThemeButtonColor;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.ThemeSubtextColor) && !string.IsNullOrWhiteSpace(friend.ThemeSubtextColor))
        {
            _user.ThemeSubtextColor = friend.ThemeSubtextColor;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.DisplayName) && !string.IsNullOrWhiteSpace(friend.DisplayName))
        {
            _user.DisplayName = friend.DisplayName;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.Status) &&
            !string.IsNullOrWhiteSpace(friend.Status) &&
            !string.Equals(friend.Status, "offline", StringComparison.OrdinalIgnoreCase))
        {
            _user.Status = friend.Status;
            changed = true;
        }

        if (changed)
        {
            OnAllPropertiesChanged();
        }
    }

    /// <summary>
    /// Fills avatar fields from the public profile when the user/instance
    /// payloads omit them. This is the last fallback for non-friends, whose
    /// privacy settings often hide the thumbnail on the user endpoint.
    /// </summary>
    public void RefreshPresentation()
    {
        OnPropertyChanged(nameof(IconFrameUrl));
        OnPropertyChanged(nameof(IconFrameIntroUrl));
        OnPropertyChanged(nameof(HasIconFrame));
        OnPropertyChanged(nameof(TrustText));
        OnPropertyChanged(nameof(TrustBrush));
    }

    public void ApplyProfile(VrcProfile? profile)
    {
        if (profile is null)
        {
            return;
        }

        ApplyAvatar(
            profile.UserIcon,
            profile.IconUrl,
            null,
            profile.CurrentAvatarThumbnailImageUrl,
            profile.CurrentAvatarImageUrl);

        if (_user is not null)
        {
            _user.IconFrame ??= profile.IconFrame;
            _user.ProfileEffect ??= profile.ProfileEffect;
            _user.NameplateEffect ??= profile.NameplateEffect;
            _user.ThemeIconColor ??= profile.ThemeIconColor;
            _user.ThemeButtonColor ??= profile.ThemeButtonColor;
            _user.ThemeSubtextColor ??= profile.ThemeSubtextColor;
            OnPropertyChanged(nameof(IconFrameUrl));
            OnPropertyChanged(nameof(HasIconFrame));
            OnPropertyChanged(nameof(TrustText));
            OnPropertyChanged(nameof(TrustBrush));
        }

        // The profile endpoint is the authoritative source for this player's
        // presence mode. Apply it even when the instance payload already had an
        // avatar; otherwise a log-only row can stay green while the profile
        // says Ask Me, Busy, or Join Me.
        if (_user is not null &&
            !string.IsNullOrWhiteSpace(profile.Status) &&
            !string.Equals(profile.Status, "offline", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_user.Status, profile.Status, StringComparison.OrdinalIgnoreCase))
        {
            _user.Status = profile.Status;
            OnAllPropertiesChanged();
        }
    }

    private void ApplyAvatar(string? userIcon, string? iconUrl, string? profilePicOverride, string? avatarThumbnail, string? avatarImage)
    {
        if (_user is null)
        {
            _user = new VrcUser
            {
                Id = _userId,
                DisplayName = _displayName
            };
        }

        var changed = false;
        if (string.IsNullOrWhiteSpace(_user.UserIcon) && !string.IsNullOrWhiteSpace(userIcon))
        {
            _user.UserIcon = userIcon;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.IconUrl) && !string.IsNullOrWhiteSpace(iconUrl))
        {
            _user.IconUrl = iconUrl;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.ProfilePicOverrideThumbnail) && !string.IsNullOrWhiteSpace(profilePicOverride))
        {
            _user.ProfilePicOverrideThumbnail = profilePicOverride;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.CurrentAvatarThumbnailImageUrl) && !string.IsNullOrWhiteSpace(avatarThumbnail))
        {
            _user.CurrentAvatarThumbnailImageUrl = avatarThumbnail;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_user.CurrentAvatarImageUrl) && !string.IsNullOrWhiteSpace(avatarImage))
        {
            _user.CurrentAvatarImageUrl = avatarImage;
            changed = true;
        }

        if (changed)
        {
            OnAllPropertiesChanged();
        }
    }

    public string AvatarUrl => _user is null
        ? string.Empty
        : ImageUrlHelper.PickAvatarThumbnailUrl(
            _user.UserIcon,
            _user.ProfilePicOverrideThumbnail ?? _user.IconUrl,
            _user.CurrentAvatarThumbnailImageUrl,
            _user.CurrentAvatarImageUrl,
            256) ?? string.Empty;

    public string IconFrameUrl => UserPresentation.IconFrameUrl(_user?.IconFrame) ?? string.Empty;

    public string IconFrameIntroUrl => UserPresentation.IconFrameIntroUrl(_user?.IconFrame) ?? string.Empty;

    public bool HasIconFrame => IconFrameUrl.Length > 0;

    public string Initial
    {
        get
        {
            var text = DisplayName.Trim();
            if (text.Length == 0)
            {
                return "?";
            }

            var info = new StringInfo(text);
            return info.SubstringByTextElements(0, 1).ToUpperInvariant();
        }
    }

    public string StatusText => _user is null
        ? "In instance"
        : UserPresence.Label(_user.Status, UserPresenceState.Online);

    public Brush StatusBrush => _user is null
        ? UserPresence.OnlineBrush
        : UserPresence.Brush(_user.Status, UserPresenceState.Online);

    public Brush StatusDotBrush => _user is null
        ? UserPresence.OnlineBrush
        : UserPresence.DotBrush(_user.Status, UserPresenceState.Online);

    public string TrustText => UserPresentation.TrustLabel(_user?.Tags, _user?.DeveloperType);

    public Brush TrustBrush => UserPresentation.TrustBrush(_user?.Tags, _user?.DeveloperType);

    public string PlatformText => PlatformName(_user?.LastPlatform ?? _user?.Platform);

    public string LanguageText
    {
        get
        {
            var tags = _user?.Tags ?? new List<string>();
            var languages = tags
                .Where(tag => tag.StartsWith("language_", StringComparison.OrdinalIgnoreCase))
                .Select(LanguageName)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();
            return languages.Count == 0 ? string.Empty : string.Join(", ", languages);
        }
    }

    public string AgeText => _user is null
        ? string.Empty
        : _user.AgeVerified || string.Equals(_user.AgeVerificationStatus, "verified", StringComparison.OrdinalIgnoreCase)
            ? "18+ verified"
            : string.Empty;

    public string Pronouns => _user?.Pronouns ?? string.Empty;

    public string DetailText
    {
        get
        {
            if (_user is null)
            {
                if (string.IsNullOrWhiteSpace(_userId))
                {
                    return "Profile unavailable";
                }

                return _fromLog
                    ? _profileAttempted
                        ? "Profile unavailable"
                        : "Loading VRChat profile…"
                    : "Player in this instance";
            }

            var parts = new List<string>();
            var description = _user.StatusDescription?.Trim();
            if (!string.IsNullOrWhiteSpace(description))
            {
                parts.Add(description);
            }
            else
            {
                parts.Add(StatusText);
            }

            if (PlatformText.Length > 0)
            {
                parts.Add(PlatformText);
            }

            if (!string.IsNullOrWhiteSpace(Pronouns))
            {
                parts.Add(Pronouns);
            }

            return string.Join("  ·  ", parts);
        }
    }

    public string BadgeText => IsSelf ? "You" : IsFriend ? "Friend" : string.Empty;

    public bool HasBadge => BadgeText.Length > 0;

    public bool HasPlatform => PlatformText.Length > 0;

    public bool HasLanguage => LanguageText.Length > 0;

    public bool HasAge => AgeText.Length > 0;

    public void ApplyUser(VrcUser user, bool isFriend, bool isSelf)
    {
        _user = user;
        _userId = user.Id;
        _displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? _displayName : user.DisplayName;
        _isSelf = isSelf;
        _isFriend = !isSelf && (isFriend || user.IsFriend);
        _isBlocked = _isBlocked || user.IsBlocked;
        _profileAttempted = true;
        OnAllPropertiesChanged();
    }

    public void MarkProfileAttempted()
    {
        _profileAttempted = true;
        OnAllPropertiesChanged();
    }

    private static string PlatformName(string? value)
    {
        var platform = (value ?? string.Empty).ToLowerInvariant();
        return platform switch
        {
            "standalonewindows" or "standalonewindows64" or "pc" => "PC",
            "android" or "quest" => "Quest",
            "ios" => "iOS",
            "web" => "Web",
            "" => string.Empty,
            _ => char.ToUpperInvariant(platform[0]) + platform[1..]
        };
    }

    private static string LanguageName(string tag)
    {
        var code = tag["language_".Length..].ToLowerInvariant();
        return code switch
        {
            "eng" => "English",
            "jpn" => "Japanese",
            "kor" => "Korean",
            "deu" => "German",
            "fra" => "French",
            "spa" => "Spanish",
            "rus" => "Russian",
            "por" => "Portuguese",
            "ita" => "Italian",
            "pol" => "Polish",
            "tha" => "Thai",
            "vie" => "Vietnamese",
            "chs" => "Chinese (Simplified)",
            "cht" => "Chinese (Traditional)",
            "ara" => "Arabic",
            "hin" => "Hindi",
            "nld" => "Dutch",
            "swe" => "Swedish",
            "tur" => "Turkish",
            "ukr" => "Ukrainian",
            "ces" => "Czech",
            "dan" => "Danish",
            "fin" => "Finnish",
            "nor" => "Norwegian",
            "hun" => "Hungarian",
            "ron" => "Romanian",
            _ => string.Empty
        };
    }

    private static Brush MakeBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
