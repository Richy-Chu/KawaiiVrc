using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Context: a live "Now" surface built from the signed-in VRChat account, the
/// live instance view, and the durable Continuity history. It also writes a
/// read-only JSON snapshot for local context consumers; it never modifies
/// VRChat, VRCX, or VRCX-0.
/// </summary>
public sealed class ContextViewModel : ViewModelBase
{
    private readonly ContinuityStore _store;
    private readonly ContextSnapshotStore _snapshots;
    private HashSet<string> _friendIds = new(StringComparer.Ordinal);
    private bool _friendsKnown;
    private string _accountUserId = string.Empty;
    private string _accountName = "Not signed in";
    private string _accountAvatarUrl = string.Empty;
    private string _statusText = string.Empty;
    private string _trustText = string.Empty;
    private string _worldId = string.Empty;
    private string _worldName = string.Empty;
    private string _worldAuthor = string.Empty;
    private string _worldImageUrl = string.Empty;
    private string _location = string.Empty;
    private string _accessText = string.Empty;
    private string _regionText = string.Empty;
    private string _playerCountText = "0 players";
    private string _lastUpdatedText = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isInWorld;
    private bool _isLoading;
    private bool _isStale;
    private bool _loaded;

    public ContextViewModel()
        : this(new ContinuityStore(), new ContextSnapshotStore())
    {
    }

    internal ContextViewModel(ContinuityStore store, ContextSnapshotStore snapshots)
    {
        _store = store;
        _snapshots = snapshots;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading);
        CopySnapshotCommand = new RelayCommand(CopySnapshot);
        OpenPlayerCommand = new RelayCommand<InstancePlayerItem>(
            player => PlayerProfileRequested?.Invoke(player),
            player => player?.CanOpenProfile == true);
        OpenPersonCommand = new RelayCommand<ContinuityPersonItem>(
            person => PersonProfileRequested?.Invoke(person),
            person => person?.CanOpenProfile == true);
        OpenSessionCommand = new RelayCommand<ContinuitySessionItem>(
            session => SessionRequested?.Invoke(session),
            session => session is not null);
        OpenWorldCommand = new RelayCommand<string>(
            worldId => WorldRequested?.Invoke(worldId),
            worldId => !string.IsNullOrWhiteSpace(worldId));
    }

    public ObservableCollection<InstancePlayerItem> Players { get; } = new();

    public ObservableCollection<InstancePlayerItem> FriendsHere { get; } = new();

    public ObservableCollection<ContinuityPersonItem> RecentPeople { get; } = new();

    public ObservableCollection<ContinuitySessionItem> TodaySessions { get; } = new();

    /// <summary>
    /// The instance activity box. It is the same feed the Instance tab and the world
    /// dialog's Players panel draw, so all three read one list and one filter.
    /// </summary>
    public InstanceActivityFeed ActivityFeed { get; } = new();

    public PagedList<InstancePlayerItem> PlayersPage { get; } = new();

    public PagedList<InstancePlayerItem> FriendsHerePage { get; } = new();

    public PagedList<ContinuityPersonItem> RecentPeoplePage { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand CopySnapshotCommand { get; }

    public RelayCommand<InstancePlayerItem> OpenPlayerCommand { get; }
    public RelayCommand<ContinuityPersonItem> OpenPersonCommand { get; }
    public RelayCommand<ContinuitySessionItem> OpenSessionCommand { get; }
    public RelayCommand<string> OpenWorldCommand { get; }

    public Action<InstancePlayerItem>? PlayerProfileRequested { get; set; }
    public Action<ContinuityPersonItem>? PersonProfileRequested { get; set; }
    public Action<ContinuitySessionItem>? SessionRequested { get; set; }
    public Action<string>? WorldRequested { get; set; }
    public Action<ContinuityPersonItem>? PersonAvatarRequested { get; set; }

    /// <summary>Set by MainViewModel so Refresh can reload the live instance too.</summary>
    public Func<Task>? RefreshRequested { get; set; }

    public string AccountUserId => _accountUserId;

    public string AccountName
    {
        get => _accountName;
        private set => SetProperty(ref _accountName, value);
    }

    public string AccountAvatarUrl
    {
        get => _accountAvatarUrl;
        private set => SetProperty(ref _accountAvatarUrl, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string TrustText
    {
        get => _trustText;
        private set => SetProperty(ref _trustText, value);
    }

    public string WorldId
    {
        get => _worldId;
        private set => SetProperty(ref _worldId, value);
    }

    public string WorldName
    {
        get => _worldName;
        private set
        {
            if (SetProperty(ref _worldName, value))
            {
                OnPropertyChanged(nameof(WorldTitle));
            }
        }
    }

    public string WorldTitle => string.IsNullOrWhiteSpace(WorldName) ? "Not in a world" : WorldName;

    public string WorldAuthor
    {
        get => _worldAuthor;
        private set => SetProperty(ref _worldAuthor, value);
    }

    public string WorldImageUrl
    {
        get => _worldImageUrl;
        private set => SetProperty(ref _worldImageUrl, value);
    }

    public string Location
    {
        get => _location;
        private set => SetProperty(ref _location, value);
    }

    public string AccessText
    {
        get => _accessText;
        private set => SetProperty(ref _accessText, value);
    }

    public string RegionText
    {
        get => _regionText;
        private set => SetProperty(ref _regionText, value);
    }

    public string PlayerCountText
    {
        get => _playerCountText;
        private set => SetProperty(ref _playerCountText, value);
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetProperty(ref _lastUpdatedText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool IsInWorld
    {
        get => _isInWorld;
        private set => SetProperty(ref _isInWorld, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsNotLoading));
            }
        }
    }

    public bool IsNotLoading => !IsLoading;

    /// <summary>True when the current cards came from the durable last snapshot.</summary>
    public bool IsStale
    {
        get => _isStale;
        private set
        {
            if (SetProperty(ref _isStale, value))
            {
                OnPropertyChanged(nameof(IsLive));
            }
        }
    }

    public bool IsLive => !IsStale;

    public string SnapshotPath => _snapshots.FilePath;

    public bool HasPlayers => Players.Count > 0;

    public bool HasFriendsHere => FriendsHere.Count > 0;

    public bool HasRecentPeople => RecentPeople.Count > 0;

    public bool HasTodaySessions => TodaySessions.Count > 0;

    /// <summary>True after a live or saved refresh has populated the page.</summary>
    public bool IsLoaded => _loaded;

    /// <summary>
    /// Resolves context and history rows against the live VRChat friend list
    /// instead of trusting a friend flag stored in an old local database.
    /// </summary>
    public void SetFriendIds(IEnumerable<string> friendIds)
    {
        var next = friendIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var wasKnown = _friendsKnown;
        _friendsKnown = true;
        if (wasKnown && _friendIds.SetEquals(next))
        {
            return;
        }

        _friendIds = next;
        foreach (var player in Players)
        {
            player.ApplyFriendState(IsFriend(player.UserId));
        }

        foreach (var person in RecentPeople)
        {
            person.ApplyFriendState(IsFriend(person.UserId));
        }

        RebuildFriendsHere();
    }

    private bool IsFriend(string? userId)
        => !string.IsNullOrWhiteSpace(userId) && _friendIds.Contains(userId);

    private void RebuildFriendsHere()
    {
        FriendsHere.Clear();
        foreach (var player in Players)
        {
            if (!player.IsSelf && player.IsFriend)
            {
                FriendsHere.Add(player);
            }
        }

        PlayersPage.SetSource(Players, resetPage: false);
        FriendsHerePage.SetSource(FriendsHere, resetPage: false);
        OnPropertyChanged(nameof(HasFriendsHere));
        OnPropertyChanged(nameof(FriendsHereText));
    }

    public string PlayersText => Players.Count == 0
        ? "No players observed here yet."
        : $"{Players.Count:N0} player(s) observed";

    public string FriendsHereText => FriendsHere.Count == 0
        ? "No friends from your list are visible here."
        : $"{FriendsHere.Count:N0} friend(s) here";

    public string TodayText => TodaySessions.Count == 0
        ? "No sessions recorded today."
        : $"{TodaySessions.Count:N0} session(s) today";

    public void SetAccount(VrcUser? user)
    {
        var id = user?.Id ?? string.Empty;
        var name = string.IsNullOrWhiteSpace(user?.DisplayName) ? "Not signed in" : user!.DisplayName!;
        if (string.Equals(_accountUserId, id, StringComparison.Ordinal) &&
            string.Equals(_accountName, name, StringComparison.Ordinal))
        {
            return;
        }

        _accountUserId = id;
        _accountName = name;
        _loaded = false;
        _friendIds.Clear();
        _friendsKnown = false;
        IsStale = false;
        StatusMessage = string.Empty;
        ClearLiveFields();
        OnPropertyChanged(nameof(AccountUserId));
        OnPropertyChanged(nameof(AccountName));
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded && !IsLoading)
        {
            return;
        }

        if (RefreshRequested is not null)
        {
            await RefreshRequested();
        }
        else
        {
            await RefreshAsync(null, null, Array.Empty<FriendItem>());
        }
    }

    public async Task RefreshAsync(
        VrcUser? user,
        InstanceViewModel? instance,
        IReadOnlyList<FriendItem> friends)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        StatusMessage = string.Empty;
        try
        {
            SetAccount(user);
            if (user is null)
            {
                ApplySnapshot(_snapshots.Read());
                await LoadLocalHistoryAsync();
                return;
            }

            SetFriendIds(friends.Select(friend => friend.Id));

            if (instance is not null && !instance.IsLoading)
            {
                await instance.LoadAsync(user, friends);
            }

            ApplyLive(user, instance);
            await LoadLocalHistoryAsync();
            SaveSnapshot();
            _loaded = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("context refresh: " + ex);
            StatusMessage = "Could not refresh context: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasPlayers));
            OnPropertyChanged(nameof(HasFriendsHere));
            OnPropertyChanged(nameof(HasRecentPeople));
            OnPropertyChanged(nameof(HasTodaySessions));
            OnPropertyChanged(nameof(PlayersText));
            OnPropertyChanged(nameof(FriendsHereText));
            OnPropertyChanged(nameof(TodayText));
            OnPropertyChanged(nameof(SnapshotPath));
        }
    }

    private async Task RefreshAsync()
    {
        if (RefreshRequested is not null)
        {
            await RefreshRequested();
            return;
        }

        await RefreshAsync(null, null, Array.Empty<FriendItem>());
    }

    /// <summary>
    /// Takes the log's activity list as it was at the last read, without touching the
    /// roster, world or history. A world that writes Udon lines can therefore bring this
    /// box up to date without the page re-reading the instance from VRChat.
    /// </summary>
    internal void ApplyActivity(IReadOnlyList<VrChatLogActivity>? events)
    {
        // The box tags this account's own rows the way the Activity page does, so it
        // needs the signed-in name before the rows are built.
        ActivityFeed.SetSelf(_accountUserId, _accountName);
        ActivityFeed.Apply(events);
    }

    private void ApplyLive(VrcUser user, InstanceViewModel? instance)
    {
        IsStale = false;
        _accountUserId = user.Id ?? string.Empty;
        AccountName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Id ?? "Unknown account" : user.DisplayName!;
        AccountAvatarUrl = ImageUrlHelper.PickAvatarThumbnailUrl(
            user.UserIcon,
            user.ProfilePicOverrideThumbnail ?? user.IconUrl,
            user.CurrentAvatarThumbnailImageUrl,
            user.CurrentAvatarImageUrl,
            256) ?? string.Empty;
        StatusText = string.Join(
            " · ",
            new[] { user.Status, user.StatusDescription }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim()));
        if (string.IsNullOrWhiteSpace(StatusText))
        {
            StatusText = "Status not available";
        }

        TrustText = UserPresentation.TrustLabel(user.Tags, user.DeveloperType);
        WorldId = instance?.Instance?.WorldId ?? string.Empty;
        WorldName = instance?.WorldName ?? string.Empty;
        WorldAuthor = instance?.World?.AuthorName ?? string.Empty;
        WorldImageUrl = instance?.WorldImageUrl ?? string.Empty;
        Location = instance?.InstanceLocation ?? string.Empty;
        AccessText = instance?.InstanceAccessText ?? string.Empty;
        RegionText = instance?.RegionText ?? string.Empty;
        IsInWorld = instance?.HasInstance == true;
        var count = instance?.TotalPlayerCount ?? 0;
        PlayerCountText = count == 1 ? "1 player" : $"{count:N0} players";
        LastUpdatedText = "Live · " + DateTimeOffset.Now.ToString("h:mm:ss tt", CultureInfo.CurrentCulture);

        Players.Clear();
        FriendsHere.Clear();
        if (instance is not null)
        {
            foreach (var player in instance.Players)
            {
                if (_friendsKnown)
                {
                    player.ApplyFriendState(IsFriend(player.UserId));
                }

                Players.Add(player);
            }
        }

        RebuildFriendsHere();
        ApplyActivity(instance?.LogActivity);
        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(PlayersText));
        OnPropertyChanged(nameof(WorldTitle));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsStale));
    }

    private async Task LoadLocalHistoryAsync()
    {
        var account = _accountUserId;
        if (string.IsNullOrWhiteSpace(account))
        {
            TodaySessions.Clear();
            RecentPeople.Clear();
            RecentPeoplePage.Clear();
            OnPropertyChanged(nameof(HasTodaySessions));
            OnPropertyChanged(nameof(HasRecentPeople));
            OnPropertyChanged(nameof(TodayText));
            return;
        }

        await Task.Run(() => _store.BackfillSessionWorldNames());
        var sessions = await Task.Run(() => _store.GetSessions(account, null, 500));
        var people = await Task.Run(() => _store.GetPeople(account, null, 8));
        var today = DateTimeOffset.Now.Date;

        TodaySessions.Clear();
        foreach (var session in sessions
                     .Where(session => session.JoinedAt.ToLocalTime().Date == today)
                     .Take(20))
        {
            TodaySessions.Add(new ContinuitySessionItem(session));
        }

        RecentPeople.Clear();
        foreach (var person in people)
        {
            var item = new ContinuityPersonItem(
                person,
                _accountUserId,
                _friendsKnown ? IsFriend(person.UserId) : null);
            RecentPeople.Add(item);
            PersonAvatarRequested?.Invoke(item);
        }

        RecentPeoplePage.SetSource(RecentPeople);
        OnPropertyChanged(nameof(HasTodaySessions));
        OnPropertyChanged(nameof(HasRecentPeople));
        OnPropertyChanged(nameof(TodayText));
    }

    private void ApplySnapshot(ContextSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            IsStale = true;
            LastUpdatedText = "No saved context yet.";
            ClearLiveFields();
            return;
        }

        IsStale = true;
        _accountUserId = snapshot.AccountUserId;
        AccountName = string.IsNullOrWhiteSpace(snapshot.AccountDisplayName)
            ? "Saved account"
            : snapshot.AccountDisplayName;
        AccountAvatarUrl = snapshot.AccountAvatarUrl;
        StatusText = snapshot.StatusText;
        TrustText = snapshot.TrustText;
        WorldId = snapshot.WorldId;
        WorldName = snapshot.WorldName;
        WorldAuthor = snapshot.WorldAuthor;
        WorldImageUrl = snapshot.WorldImageUrl;
        Location = snapshot.Location;
        AccessText = snapshot.AccessText;
        RegionText = snapshot.RegionText;
        IsInWorld = !string.IsNullOrWhiteSpace(snapshot.WorldId);
        PlayerCountText = snapshot.PlayerCount == 1
            ? "1 player"
            : $"{snapshot.PlayerCount:N0} players";
        LastUpdatedText = "Saved · " +
                          snapshot.GeneratedAt.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);

        Players.Clear();
        FriendsHere.Clear();
        foreach (var player in snapshot.Players)
        {
            var isSelf = player.IsSelf ||
                         (!string.IsNullOrWhiteSpace(snapshot.AccountUserId) &&
                          string.Equals(player.UserId, snapshot.AccountUserId, StringComparison.Ordinal));
            var item = new InstancePlayerItem(
                player.UserId,
                player.DisplayName,
                _friendsKnown ? IsFriend(player.UserId) : player.IsFriend,
                isSelf: isSelf,
                isBlocked: player.IsBlocked);
            Players.Add(item);
        }

        RebuildFriendsHere();

        RecentPeople.Clear();
        foreach (var person in snapshot.RecentPeople)
        {
            RecentPeople.Add(new ContinuityPersonItem(new ContinuityPerson
            {
                PersonKey = person.UserId,
                UserId = person.UserId,
                DisplayName = person.DisplayName,
                TrustLevel = person.TrustText,
                IsFriend = person.IsFriend,
                AvatarUrl = person.AvatarUrl,
                LastSeenAt = person.LastSeenAt,
                SessionCount = person.SessionCount
            }, _accountUserId, _friendsKnown ? IsFriend(person.UserId) : person.IsFriend));
        }

        TodaySessions.Clear();
        foreach (var session in snapshot.TodaySessions)
        {
            TodaySessions.Add(new ContinuitySessionItem(new ContinuitySession
            {
                Id = ContinuitySession.MakeId("snapshot", session.Location + session.JoinedAt.UtcTicks),
                Source = "snapshot",
                SourceKey = session.Location + session.JoinedAt.UtcTicks,
                WorldName = session.WorldName,
                Location = session.Location,
                JoinedAt = session.JoinedAt,
                DurationSeconds = session.DurationSeconds,
                PlayerCount = session.PlayerCount
            }));
        }

        RecentPeoplePage.SetSource(RecentPeople);
        // A saved snapshot holds people, not events; the box must not show the last
        // session's activity as if it were happening now.
        ApplyActivity(null);
        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(HasFriendsHere));
        OnPropertyChanged(nameof(HasRecentPeople));
        OnPropertyChanged(nameof(HasTodaySessions));
        OnPropertyChanged(nameof(PlayersText));
        OnPropertyChanged(nameof(FriendsHereText));
        OnPropertyChanged(nameof(TodayText));
        OnPropertyChanged(nameof(WorldTitle));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsStale));
    }

    private void SaveSnapshot()
    {
        try
        {
            var snapshot = new ContextSnapshot
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                AccountUserId = _accountUserId,
                AccountDisplayName = AccountName,
                AccountAvatarUrl = AccountAvatarUrl,
                StatusText = StatusText,
                TrustText = TrustText,
                WorldId = WorldId,
                WorldName = WorldName,
                WorldAuthor = WorldAuthor,
                WorldImageUrl = WorldImageUrl,
                Location = Location,
                AccessText = AccessText,
                RegionText = RegionText,
                PlayerCount = Players.Count,
                Players = Players.Select(player => new ContextSnapshotPerson
                {
                    UserId = player.UserId,
                    DisplayName = player.DisplayName,
                    TrustText = player.TrustText,
                    IsFriend = player.IsFriend,
                    IsBlocked = player.IsBlocked,
                    IsSelf = player.IsSelf,
                    AvatarUrl = player.AvatarUrl
                }).ToList(),
                RecentPeople = RecentPeople.Select(person => new ContextSnapshotPerson
                {
                    UserId = person.Person.UserId,
                    DisplayName = person.DisplayName,
                    TrustText = person.TrustText,
                    IsFriend = person.IsFriend,
                    IsSelf = person.IsCurrentUser,
                    AvatarUrl = person.AvatarUrl,
                    LastSeenAt = person.Person.LastSeenAt,
                    SessionCount = person.Person.SessionCount
                }).ToList(),
                TodaySessions = TodaySessions.Select(item => new ContextSnapshotSession
                {
                    WorldName = item.WorldName,
                    Location = item.Location,
                    JoinedAt = item.Session.JoinedAt,
                    DurationSeconds = item.Session.DurationSeconds,
                    PlayerCount = item.Session.PlayerCount
                }).ToList()
            };
            _snapshots.Save(snapshot);
            OnPropertyChanged(nameof(SnapshotPath));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("context snapshot: " + ex);
            StatusMessage = "Context is live, but its local snapshot could not be written.";
        }
    }

    private void ClearLiveFields()
    {
        AccountAvatarUrl = string.Empty;
        StatusText = string.Empty;
        TrustText = string.Empty;
        WorldId = string.Empty;
        WorldName = string.Empty;
        WorldAuthor = string.Empty;
        WorldImageUrl = string.Empty;
        Location = string.Empty;
        AccessText = string.Empty;
        RegionText = string.Empty;
        IsInWorld = false;
        PlayerCountText = "0 players";
        Players.Clear();
        FriendsHere.Clear();
        ApplyActivity(null);
        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(HasFriendsHere));
        OnPropertyChanged(nameof(PlayersText));
        OnPropertyChanged(nameof(FriendsHereText));
        OnPropertyChanged(nameof(WorldTitle));
    }

    private void CopySnapshot()
    {
        try
        {
            var snapshot = new
            {
                generatedAt = DateTimeOffset.UtcNow,
                account = AccountName,
                accountUserId = _accountUserId,
                status = StatusText,
                world = WorldName,
                worldId = WorldId,
                location = Location,
                players = Players.Select(player => new
                {
                    player.UserId,
                    player.DisplayName,
                    player.IsFriend,
                    player.IsBlocked,
                    player.IsSelf,
                    player.TrustText
                }),
                recentPeople = RecentPeople.Select(person => new
                {
                    person.DisplayName,
                    person.Person.UserId,
                    person.LastSeenText,
                    person.SessionsText
                }),
                today = TodaySessions.Select(session => new
                {
                    session.WorldName,
                    session.DateText,
                    session.DurationText
                })
            };
            Clipboard.SetText(System.Text.Json.JsonSerializer.Serialize(
                snapshot,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            StatusMessage = "Read-only context copied to the clipboard.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine("context copy: " + ex);
            StatusMessage = "The clipboard is busy. Try again.";
        }
    }
}
