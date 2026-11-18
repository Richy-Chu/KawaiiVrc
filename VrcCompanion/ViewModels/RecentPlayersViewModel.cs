using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// One world visit shared with a recent player. The values come from the
/// locally recorded VRChat log, never from placeholder or generated data.
/// </summary>
public sealed class RecentPlayerSessionItem : ViewModelBase
{
    private string _worldName;
    private string _thumbnailUrl;

    public RecentPlayerSessionItem(
        string worldName,
        string worldId,
        string location,
        string thumbnailUrl,
        DateTimeOffset joinedAt,
        DateTimeOffset? leftAt,
        DateTimeOffset lastSeenAt,
        TimeSpan duration,
        Action<RecentPlayerSessionItem>? openWorld)
    {
        _worldName = string.IsNullOrWhiteSpace(worldName) ? "Unknown world" : worldName;
        WorldId = worldId ?? string.Empty;
        Location = location ?? string.Empty;
        _thumbnailUrl = thumbnailUrl ?? string.Empty;
        JoinedAt = joinedAt;
        LeftAt = leftAt;
        LastSeenAt = lastSeenAt;
        Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        OpenWorldCommand = new RelayCommand(
            () => openWorld?.Invoke(this),
            () => CanOpenWorld);
    }

    public string WorldName
    {
        get => _worldName;
        private set => SetProperty(ref _worldName, value);
    }

    public string WorldId { get; }

    public string Location { get; }

    public string ThumbnailUrl
    {
        get => _thumbnailUrl;
        private set => SetProperty(ref _thumbnailUrl, value);
    }

    public DateTimeOffset JoinedAt { get; }

    public DateTimeOffset? LeftAt { get; }

    public DateTimeOffset LastSeenAt { get; }

    public TimeSpan Duration { get; }

    public bool CanOpenWorld => !string.IsNullOrWhiteSpace(WorldId);

    public RelayCommand OpenWorldCommand { get; }

    public string WhenText => JoinedAt.ToLocalTime().ToString(
        "ddd d MMM · h:mm tt",
        CultureInfo.CurrentCulture);

    public string TimeRangeText
    {
        get
        {
            var start = JoinedAt.ToLocalTime();
            return LeftAt is { } left
                ? $"{start:h:mm tt} – {left.ToLocalTime():h:mm tt}"
                : $"{start:h:mm tt} – now";
        }
    }

    public string DurationText => RecentPlayerTime.FormatDuration(Duration);

    public string TogetherText => $"Together {DurationText}";

    public string DetailText => $"{WhenText} · {TimeRangeText}";

    public void ApplyWorld(VrcWorld world)
    {
        if (string.IsNullOrWhiteSpace(world.Name) || WorldName != "Unknown world")
        {
            return;
        }

        WorldName = world.Name;
        ThumbnailUrl = ImageUrlHelper.PickWorldThumbnailUrl(
            world.ThumbnailImageUrl,
            world.ImageUrl,
            256) ?? string.Empty;
    }
}

/// <summary>
/// One person met in a recorded instance, with every shared session and the
/// total time spent together. Friends and non-friends use the same local data.
/// </summary>
public sealed class RecentPlayerItem : ViewModelBase
{
    private bool _isFriend;
    private bool _sessionsExpanded;
    private string _displayName;
    private string _avatarUrl;

    public RecentPlayerItem(
        string userId,
        string displayName,
        string avatarUrl,
        bool isFriend,
        IReadOnlyList<RecentPlayerSessionItem> sessions,
        Action<RecentPlayerItem>? openProfile)
    {
        UserId = userId ?? string.Empty;
        _displayName = string.IsNullOrWhiteSpace(displayName) ? "Unknown player" : displayName.Trim();
        _avatarUrl = avatarUrl ?? string.Empty;
        _isFriend = isFriend;
        Sessions = sessions;
        foreach (var session in sessions)
        {
            session.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RecentPlayerSessionItem.WorldName))
                {
                    OnPropertyChanged(nameof(LastWorldText));
                    OnPropertyChanged(nameof(SearchText));
                }
            };
        }

        TotalTime = TimeSpan.FromTicks(sessions.Sum(session => session.Duration.Ticks));
        FirstSeenAt = sessions.Count == 0 ? DateTimeOffset.MinValue : sessions.Min(session => session.JoinedAt);
        LastSeenAt = sessions.Count == 0
            ? DateTimeOffset.MinValue
            : sessions.Max(session => session.LastSeenAt);
        OpenProfileCommand = new RelayCommand(
            () => openProfile?.Invoke(this),
            () => CanOpenProfile);
        ToggleSessionsCommand = new RelayCommand(ToggleSessions, () => HasMoreSessions);
    }

    public string UserId { get; }

    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (SetProperty(ref _displayName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Initial));
            }
        }
    }

    public string AvatarUrl
    {
        get => _avatarUrl;
        private set
        {
            if (SetProperty(ref _avatarUrl, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasAvatar));
            }
        }
    }

    public bool IsFriend
    {
        get => _isFriend;
        private set
        {
            if (SetProperty(ref _isFriend, value))
            {
                OnPropertyChanged(nameof(RelationshipText));
            }
        }
    }

    public IReadOnlyList<RecentPlayerSessionItem> Sessions { get; }

    public IReadOnlyList<RecentPlayerSessionItem> VisibleSessions => _sessionsExpanded
        ? Sessions
        : Sessions.Take(5).ToList();

    public bool HasMoreSessions => Sessions.Count > 5;

    public string MoreSessionsText => _sessionsExpanded
        ? "Show fewer sessions"
        : HasMoreSessions
            ? $"+{Sessions.Count - 5:N0} earlier session{(Sessions.Count - 5 == 1 ? string.Empty : "s")}"
            : string.Empty;

    public TimeSpan TotalTime { get; }

    public DateTimeOffset FirstSeenAt { get; }

    public DateTimeOffset LastSeenAt { get; }

    public bool CanOpenProfile => !string.IsNullOrWhiteSpace(UserId);

    public RelayCommand OpenProfileCommand { get; }

    public RelayCommand ToggleSessionsCommand { get; }

    public string Initial
    {
        get
        {
            var name = DisplayName.Trim();
            if (name.Length == 0)
            {
                return "?";
            }

            return new StringInfo(name).SubstringByTextElements(0, 1).ToUpperInvariant();
        }
    }

    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);

    public string RelationshipText => IsFriend ? "Friend" : "Not a friend";

    public string TotalTimeText => RecentPlayerTime.FormatDuration(TotalTime);

    public string SessionCountText => Sessions.Count == 1
        ? "1 session"
        : $"{Sessions.Count:N0} sessions";

    public string LastSeenText => LastSeenAt == DateTimeOffset.MinValue
        ? "Never recorded"
        : $"Last seen {TimeText.Relative(LastSeenAt)}";

    public string FirstSeenText => FirstSeenAt == DateTimeOffset.MinValue
        ? string.Empty
        : $"First met {FirstSeenAt.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";

    public string LastWorldText => Sessions.Count == 0 ? string.Empty : Sessions[0].WorldName;

    public string SummaryText => $"{TotalTimeText} together · {SessionCountText}";

    public string SearchText => string.Join(
        " ",
        new[]
        {
            DisplayName,
            LastWorldText,
            string.Join(" ", Sessions.Select(session => session.WorldName))
        });

    public void SetFriend(bool isFriend) => IsFriend = isFriend;

    private void ToggleSessions()
    {
        if (!HasMoreSessions)
        {
            return;
        }

        _sessionsExpanded = !_sessionsExpanded;
        OnPropertyChanged(nameof(VisibleSessions));
        OnPropertyChanged(nameof(MoreSessionsText));
    }

    public void ApplyProfile(VrcUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            DisplayName = user.DisplayName;
        }

        var avatar = ImageUrlHelper.PickAvatarThumbnailUrl(
            user.UserIcon,
            user.ProfilePicOverrideThumbnail ?? user.ProfilePicOverride ?? user.IconUrl,
            user.CurrentAvatarThumbnailImageUrl,
            user.CurrentAvatarImageUrl,
            256);
        if (!string.IsNullOrWhiteSpace(avatar))
        {
            AvatarUrl = avatar;
        }
    }
}

/// <summary>
/// Aggregates the account's persisted instance history into people instead of
/// visits. VRChat has no recent-player endpoint; both VRCX and VRCNext derive
/// this view from the local game log, which is also what this app stores.
/// </summary>
public sealed class RecentPlayersViewModel : ViewModelBase
{
    private readonly InstanceHistoryStore _store;
    private readonly VrcApiClient _api;
    private readonly ObservableCollection<RecentPlayerItem> _players = new();
    private readonly ListCollectionView _playersView;
    private readonly HashSet<string> _avatarRequests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _avatarGate = new(4);
    private HashSet<string> _friendIds = new(StringComparer.Ordinal);
    private string _accountUserId = string.Empty;
    private string _searchText = string.Empty;
    private string _summaryText = string.Empty;
    private bool _isOpen;
    private bool _isLoading;
    private int _loadVersion;
    private FilterOption _selectedSort = null!;

    public RecentPlayersViewModel(VrcApiClient api, InstanceHistoryStore store)
    {
        _api = api;
        _store = store;
        _playersView = new ListCollectionView(_players)
        {
            Filter = FilterPlayer
        };
        SortOptions = new ObservableCollection<FilterOption>
        {
            new("most-played", "Most played"),
            new("recent", "Most recent")
        };
        _selectedSort = SortOptions[0];
        ApplySort();

        CloseCommand = new RelayCommand(Close);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsOpen && !IsLoading);
    }

    public Action<RecentPlayerItem>? PlayerProfileRequested { get; set; }

    public Action<RecentPlayerSessionItem>? WorldRequested { get; set; }

    public ObservableCollection<RecentPlayerItem> Players => _players;

    public ICollectionView PlayersView => _playersView;

    public ObservableCollection<FilterOption> SortOptions { get; }

    public FilterOption SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (value is null || !SetProperty(ref _selectedSort, value))
            {
                return;
            }

            ApplySort();
            _playersView.Refresh();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
            {
                return;
            }

            _playersView.Refresh();
            OnPropertyChanged(nameof(HasNoPlayers));
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (!SetProperty(ref _isOpen, value))
            {
                return;
            }

            RefreshCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value))
            {
                return;
            }

            RefreshCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    public bool HasPlayers => _players.Count > 0;

    public bool HasNoPlayers => !IsLoading && !_playersView.Cast<object>().Any();

    public bool ShowEmptyState => !IsLoading && !_playersView.Cast<object>().Any();

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value ?? string.Empty);
    }

    public string DescriptionText =>
        "Everyone recorded in your local VRChat log, friends and non-friends alike. Click a person for their profile, or a world to open it.";

    public string EmptyTitle => _players.Count == 0
        ? "No recent players yet"
        : "No matching people";

    public string EmptyMessage => _players.Count == 0
        ? "Join a VRChat world while this app is running. Recent people are built from the same local log VRCX uses, and stay here after you close the app."
        : $"No recorded player matches \u201c{SearchText.Trim()}\u201d.";

    public RelayCommand CloseCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public void SetAccount(VrcUser user)
    {
        _accountUserId = user.Id ?? string.Empty;
        if (IsOpen)
        {
            _ = RefreshAsync();
        }
    }

    public void SetFriendIds(IEnumerable<string> friendIds)
    {
        _friendIds = friendIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var player in _players)
        {
            player.SetFriend(_friendIds.Contains(player.UserId));
        }

        _playersView.Refresh();
    }

    public void Show(string accountUserId, IEnumerable<string> friendIds)
    {
        _accountUserId = accountUserId ?? string.Empty;
        SetFriendIds(friendIds);
        SearchText = string.Empty;
        IsOpen = true;
        _ = RefreshAsync();
    }

    public void Close()
    {
        IsOpen = false;
        SearchText = string.Empty;
    }

    /// <summary>
    /// Called when a virtualized person row is realized or recycled. Only the
    /// rows the user can actually see trigger a profile lookup, so a history
    /// with thousands of people still opens immediately.
    /// </summary>
    public void RequestAvatar(RecentPlayerItem player)
    {
        if (player.HasAvatar || !player.CanOpenProfile ||
            !_avatarRequests.Add(player.UserId))
        {
            return;
        }

        _ = LoadAvatarAsync(player, _loadVersion);
    }

    public Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(_accountUserId))
        {
            _players.Clear();
            UpdateSummary();
            return Task.CompletedTask;
        }

        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            // The store is an indexed local SQLite query over at most 500 sessions.
            // Keeping the collection update on the caller's dispatcher preserves
            // WPF's CollectionView thread affinity in both the app and tests.
            var rows = _store.ForAccount(_accountUserId);
            if (version != _loadVersion)
            {
                return Task.CompletedTask;
            }

            var players = Build(
                rows,
                _accountUserId,
                _friendIds,
                PlayerProfileRequested,
                WorldRequested);
            _avatarRequests.Clear();
            _players.Clear();
            foreach (var player in players)
            {
                _players.Add(player);
            }

            ApplySort();
            _playersView.Refresh();
            UpdateSummary();
            _ = HydrateAsync(players, version);
            return Task.CompletedTask;
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(HasPlayers));
                OnPropertyChanged(nameof(HasNoPlayers));
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    private static IReadOnlyList<RecentPlayerItem> Build(
        IReadOnlyList<InstanceHistoryEntry> rows,
        string accountUserId,
        IReadOnlySet<string> friendIds,
        Action<RecentPlayerItem>? openProfile,
        Action<RecentPlayerSessionItem>? openWorld)
    {
        var pairs = rows
            .SelectMany(entry => entry.Players
                .Where(player =>
                    !string.IsNullOrWhiteSpace(player.DisplayName) &&
                    !string.Equals(player.UserId, accountUserId, StringComparison.Ordinal))
                .Select(player => (Entry: entry, Player: player)))
            .ToList();

        return pairs
            .GroupBy(
                pair => string.IsNullOrWhiteSpace(pair.Player.UserId)
                    ? "name:" + pair.Player.DisplayName.Trim()
                    : pair.Player.UserId,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildPlayer(group.ToList(), friendIds, openProfile, openWorld))
            .OrderByDescending(player => player.TotalTime)
            .ThenByDescending(player => player.LastSeenAt)
            .ToList();
    }

    private static RecentPlayerItem BuildPlayer(
        IReadOnlyList<(InstanceHistoryEntry Entry, InstanceHistoryPlayer Player)> group,
        IReadOnlySet<string> friendIds,
        Action<RecentPlayerItem>? openProfile,
        Action<RecentPlayerSessionItem>? openWorld)
    {
        var ordered = group
            .OrderByDescending(pair => pair.Entry.JoinedAt)
            .ToList();
        var latest = ordered[0];
        var allSessions = ordered
            .Select(pair => ToSession(pair.Entry, pair.Player, openWorld))
            .ToList();
        var avatarUrl = ordered
            .Select(pair => pair.Player.AvatarUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? string.Empty;

        return new RecentPlayerItem(
            latest.Player.UserId,
            ordered
                .Select(pair => pair.Player.DisplayName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Unknown player",
            avatarUrl,
            friendIds.Contains(latest.Player.UserId),
            allSessions,
            openProfile);
    }

    private static RecentPlayerSessionItem ToSession(
        InstanceHistoryEntry entry,
        InstanceHistoryPlayer player,
        Action<RecentPlayerSessionItem>? openWorld)
    {
        var visits = player.Visits
            .Where(visit => visit.JoinedAt != default)
            .OrderBy(visit => visit.JoinedAt)
            .ToList();
        var joinedAt = visits.Count > 0 ? visits[0].JoinedAt : entry.JoinedAt;
        var now = DateTimeOffset.Now;
        var isOpen = visits.Count > 0
            ? visits.Any(visit => visit.LeftAt is null)
            : entry.LeftAt is null;
        var leftAt = visits.Count > 0
            ? visits[^1].LeftAt
            : entry.LeftAt;
        if (leftAt is null && entry.LeftAt is not null)
        {
            leftAt = entry.LeftAt;
        }

        var endForDuration = isOpen
            ? now
            : visits.Count > 0
                ? visits[^1].LeftAt ?? entry.LastSeenAt
                : entry.LeftAt ?? entry.LastSeenAt;
        var duration = visits.Count > 0
            ? TimeSpan.FromTicks(visits.Sum(visit =>
            {
                var end = visit.LeftAt ?? (isOpen ? now : entry.LastSeenAt);
                return Math.Max(0, (end - visit.JoinedAt).Ticks);
            }))
            : endForDuration > joinedAt
                ? endForDuration - joinedAt
                : TimeSpan.Zero;

        return new RecentPlayerSessionItem(
            entry.WorldName,
            entry.WorldId,
            entry.Location,
            entry.WorldThumbnailUrl,
            joinedAt,
            leftAt,
            isOpen ? now : endForDuration,
            duration,
            openWorld);
    }

    private bool FilterPlayer(object item)
    {
        if (item is not RecentPlayerItem player)
        {
            return false;
        }

        var query = SearchText.Trim();
        return query.Length == 0 ||
               player.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               player.UserId.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private async Task HydrateAsync(IReadOnlyList<RecentPlayerItem> players, int version)
    {
        var worlds = players
            .SelectMany(player => player.Sessions)
            .Where(session => session.CanOpenWorld && session.WorldName == "Unknown world")
            .GroupBy(session => session.WorldId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(20)
            .ToList();
        foreach (var session in worlds)
        {
            if (version != _loadVersion)
            {
                return;
            }

            try
            {
                var world = await _api.GetWorldAsync(session.WorldId);
                if (world is null)
                {
                    continue;
                }

                foreach (var matching in players.SelectMany(player => player.Sessions)
                             .Where(candidate =>
                                 string.Equals(candidate.WorldId, session.WorldId, StringComparison.Ordinal)))
                {
                    matching.ApplyWorld(world);
                }

                _playersView.Refresh();
            }
            catch (Exception)
            {
                // The recorded world id still opens from the row's action.
            }
        }
    }

    private async Task LoadAvatarAsync(RecentPlayerItem player, int version)
    {
        await _avatarGate.WaitAsync();
        try
        {
            if (version != _loadVersion || player.HasAvatar)
            {
                return;
            }

            var user = await _api.GetUserAsync(player.UserId);
            if (user is not null && version == _loadVersion)
            {
                player.ApplyProfile(user);
            }
        }
        catch (Exception)
        {
            // The silhouette remains usable when VRChat exposes no icon.
        }
        finally
        {
            _avatarGate.Release();
        }
    }

    private void ApplySort()
    {
        _playersView.CustomSort = new RecentPlayerComparer(SelectedSort.Key);
    }

    private void UpdateSummary()
    {
        var shown = _playersView.Cast<object>().Count();
        var sessions = _players.Sum(player => player.Sessions.Count);
        SummaryText = shown == 0
            ? "No recorded people yet."
            : $"{shown:N0} people · {sessions:N0} shared sessions · stored locally";
        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(HasNoPlayers));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private sealed class RecentPlayerComparer : IComparer
    {
        private readonly string _mode;

        public RecentPlayerComparer(string mode)
        {
            _mode = mode;
        }

        public int Compare(object? x, object? y)
        {
            if (x is not RecentPlayerItem left || y is not RecentPlayerItem right)
            {
                return 0;
            }

            var comparison = _mode == "recent"
                ? right.LastSeenAt.CompareTo(left.LastSeenAt)
                : right.TotalTime.CompareTo(left.TotalTime);
            return comparison != 0
                ? comparison
                : string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}

internal static class RecentPlayerTime
{
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalHours >= 1)
        {
            var hours = (int)duration.TotalHours;
            return duration.Minutes > 0
                ? $"{hours}h {duration.Minutes}m"
                : $"{hours}h";
        }

        return duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}m"
            : "<1m";
    }
}
