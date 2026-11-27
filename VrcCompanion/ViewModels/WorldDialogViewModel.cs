using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

public sealed class InstanceTypeOption
{
    public InstanceTypeOption(string label, string value, bool canRequestInvite, string description)
    {
        Label = label;
        Value = value;
        CanRequestInvite = canRequestInvite;
        Description = description;
    }

    public string Label { get; }
    public string Value { get; }
    public bool CanRequestInvite { get; }
    public string Description { get; }
}

/// <summary>
/// Who owns the instance the dialog creates: the signed-in account, or one of its groups
/// whose role is allowed to open an instance. A group carries the access it may create.
/// </summary>
public sealed class InstanceOwnerOption
{
    public InstanceOwnerOption(
        string label,
        string? groupId,
        string description,
        IReadOnlyList<string> accessTypes,
        string thumbnailUrl = "",
        bool ageGateAllowed = false)
    {
        Label = label;
        GroupId = groupId;
        Description = description;
        AccessTypes = accessTypes;
        ThumbnailUrl = thumbnailUrl;
        AgeGateAllowed = ageGateAllowed;
    }

    public string Label { get; }
    public string? GroupId { get; }
    public string Description { get; }

    /// <summary>The group access kinds this account's role may open: public, plus, members.</summary>
    public IReadOnlyList<string> AccessTypes { get; }

    public string ThumbnailUrl { get; }

    /// <summary>True for the account; for a group, its role needs
    /// `group-instance-age-gated-create` before the dialog offers the option.</summary>
    public bool AgeGateAllowed { get; }

    public bool IsGroup => !string.IsNullOrWhiteSpace(GroupId);

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailUrl);
}

/// <summary>One person in an instance opened from world details.</summary>
/// <summary>One person in an instance this account is in, named by the game's own log.</summary>
public sealed record InstanceRosterPlayer(
    string UserId,
    string DisplayName,
    string AvatarUrl,
    bool IsFriend,
    bool IsSelf);

public sealed class WorldInstancePlayerItem
{
    public WorldInstancePlayerItem(VrcUser user, Action<WorldInstancePlayerItem>? openProfile, bool isSelf = false)
        : this(
            user.Id ?? string.Empty,
            string.IsNullOrWhiteSpace(user.DisplayName) ? "Unknown player" : user.DisplayName.Trim(),
            ImageUrlHelper.PickAvatarThumbnailUrl(
                user.UserIcon,
                user.ProfilePicOverrideThumbnail ?? user.IconUrl,
                user.CurrentAvatarThumbnailImageUrl,
                user.CurrentAvatarImageUrl,
                64) ?? string.Empty,
            user.IsFriend,
            isSelf,
            openProfile)
    {
    }

    public WorldInstancePlayerItem(InstanceRosterPlayer player, Action<WorldInstancePlayerItem>? openProfile)
        : this(
            player.UserId,
            player.DisplayName,
            player.AvatarUrl,
            player.IsFriend,
            player.IsSelf,
            openProfile)
    {
    }

    private WorldInstancePlayerItem(
        string userId,
        string displayName,
        string avatarUrl,
        bool isFriend,
        bool isSelf,
        Action<WorldInstancePlayerItem>? openProfile)
    {
        UserId = userId ?? string.Empty;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Unknown player" : displayName.Trim();
        AvatarUrl = avatarUrl ?? string.Empty;
        IsFriend = !isSelf && isFriend;
        IsSelf = isSelf;
        OpenProfileCommand = new RelayCommand(
            () => openProfile?.Invoke(this),
            () => CanOpenProfile);
    }

    public string UserId { get; }
    public string DisplayName { get; }
    public string AvatarUrl { get; }
    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);
    public bool IsFriend { get; }
    public bool IsSelf { get; }
    public bool CanOpenProfile => !string.IsNullOrWhiteSpace(UserId);
    public RelayCommand OpenProfileCommand { get; }

    /// <summary>True when both rows draw the same person the same way, so a refresh keeps them.</summary>
    public bool SameAs(WorldInstancePlayerItem other)
        => string.Equals(UserId, other.UserId, StringComparison.Ordinal) &&
           string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal) &&
           string.Equals(AvatarUrl, other.AvatarUrl, StringComparison.Ordinal) &&
           IsFriend == other.IsFriend &&
           IsSelf == other.IsSelf;
}

public sealed class RegionOption
{
    public RegionOption(string label, string value, string detail)
    {
        Label = label;
        Value = value;
        Detail = detail;
    }

    public string Label { get; }
    public string Value { get; }
    public string Detail { get; }
}

public sealed class WorldInstanceItem : ViewModelBase
{
    private string? _groupName;
    private bool _groupResolved;
    private bool _isGroupMember;
    private string? _groupMembershipStatus;
    private string? _groupJoinState;
    private string? _focusNote;

    public WorldInstanceItem(
        VrcInstance instance,
        string worldId,
        string? focusNote = null,
        Action<WorldInstanceItem>? viewPlayers = null)
    {
        Instance = instance;
        WorldId = worldId;
        _focusNote = focusNote;
        GroupId = ParseGroupId(instance.InstanceId) ?? ParseGroupId(instance.Location);
        ViewPlayersCommand = new RelayCommand(() => viewPlayers?.Invoke(this), () => CanJoin);
    }

    /// <summary>Asks the world dialog to show who is in this instance.</summary>
    public RelayCommand ViewPlayersCommand { get; }

    private bool _isCurrent;

    /// <summary>True for the instance this account is standing in.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        private set => SetProperty(ref _isCurrent, value);
    }

    public void MarkCurrent(bool current) => IsCurrent = current;

    public ObservableCollection<WorldInstancePlayerItem> Players { get; } = new();

    private bool _isLoadingPlayers;
    public bool IsLoadingPlayers
    {
        get => _isLoadingPlayers;
        private set
        {
            if (SetProperty(ref _isLoadingPlayers, value))
            {
                RaisePlayerState();
            }
        }
    }

    private string? _playersError;
    public string? PlayersError
    {
        get => _playersError;
        private set
        {
            if (SetProperty(ref _playersError, value))
            {
                OnPropertyChanged(nameof(HasPlayersError));
                RaisePlayerState();
            }
        }
    }

    public bool HasPlayersError => !string.IsNullOrWhiteSpace(PlayersError);

    private string? _playersNote;
    /// <summary>An explanation, not a failure: VRChat only shares some instances' lists.</summary>
    public string? PlayersNote
    {
        get => _playersNote;
        private set
        {
            if (SetProperty(ref _playersNote, value))
            {
                OnPropertyChanged(nameof(HasPlayersNote));
                RaisePlayerState();
            }
        }
    }

    public bool HasPlayersNote => !string.IsNullOrWhiteSpace(PlayersNote);

    public bool HasPlayers => Players.Count > 0;

    public bool ShowNoPlayers => !IsLoadingPlayers && !HasPlayersError && !HasPlayers && !HasPlayersNote;

    /// <summary>True once a real player list came back; a quiet refresh still replaces it.</summary>
    public bool PlayersLoaded { get; private set; }

    /// <summary>True when the names came from the running game's own log (your instance).</summary>
    public bool PlayersFromLiveRoster { get; private set; }

    public bool IsLiveInstance => PlayersFromLiveRoster;

    public DateTimeOffset? PlayersUpdatedAt { get; private set; }

    public void SetPlayers(IReadOnlyList<WorldInstancePlayerItem> players, bool fromLiveRoster)
    {
        var same = players.Count == Players.Count &&
                   players.Zip(Players, (next, current) => next.SameAs(current)).All(equal => equal);

        if (!same)
        {
            Players.Clear();
            foreach (var player in players)
            {
                Players.Add(player);
            }
        }

        if (PlayersFromLiveRoster != fromLiveRoster)
        {
            PlayersFromLiveRoster = fromLiveRoster;
            OnPropertyChanged(nameof(PlayersFromLiveRoster));
            OnPropertyChanged(nameof(IsLiveInstance));
        }

        PlayersLoaded = true;
        PlayersError = null;
        OnPropertyChanged(nameof(HasPlayers));
        RaisePlayerState();
    }

    public void SetPlayersError(string message)
    {
        if (!HasPlayers)
        {
            Players.Clear();
            OnPropertyChanged(nameof(HasPlayers));
        }

        PlayersError = message;
        PlayersNote = null;
        RaisePlayerState();
    }

    public void SetPlayersNote(string? note)
    {
        PlayersError = null;
        PlayersNote = note;
        RaisePlayerState();
    }

    public void MarkPlayersUpdated(DateTimeOffset at)
    {
        PlayersUpdatedAt = at;
        OnPropertyChanged(nameof(PlayersUpdatedAt));
    }

    /// <summary>
    /// Takes the live count, region and capacity from the instance endpoint so a
    /// quiet refresh updates the row without replacing it.
    /// </summary>
    public void ApplyLiveInstance(VrcInstance live)
    {
        if (live.UserCount > 0 || live.Users.Count > 0)
        {
            var count = live.UserCount > 0 ? live.UserCount : live.Users.Count;
            Instance.UserCount = count;
            Instance.UserCountAlt = count;
        }
        else if (live.UserCountAlt > 0)
        {
            Instance.UserCount = live.UserCountAlt;
            Instance.UserCountAlt = live.UserCountAlt;
        }

        if (live.Capacity > 0)
        {
            Instance.Capacity = live.Capacity;
        }
        if (live.RecommendedCapacity > 0)
        {
            Instance.RecommendedCapacity = live.RecommendedCapacity;
        }
        if (!string.IsNullOrWhiteSpace(live.Region))
        {
            Instance.Region = live.Region;
        }
        if (!string.IsNullOrWhiteSpace(live.Type))
        {
            Instance.Type = live.Type;
        }

        OnPropertyChanged(nameof(Occupants));
        OnPropertyChanged(nameof(OccupancyText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(RegionLabel));
        OnPropertyChanged(nameof(AccessLabel));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>The log roster is the real head count; keep the row's number with it.</summary>
    public void ApplyRosterCount(int count)
    {
        if (count <= 0)
        {
            return;
        }

        Instance.UserCount = count;
        Instance.UserCountAlt = count;
        OnPropertyChanged(nameof(Occupants));
        OnPropertyChanged(nameof(OccupancyText));
        OnPropertyChanged(nameof(DetailText));
    }

    public void BeginPlayerLoad()
    {
        PlayersError = null;
        IsLoadingPlayers = true;
    }

    public void EndPlayerLoad() => IsLoadingPlayers = false;

    private void RaisePlayerState()
    {
        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(HasPlayersError));
        OnPropertyChanged(nameof(HasPlayersNote));
        OnPropertyChanged(nameof(ShowNoPlayers));
    }

    public VrcInstance Instance { get; }
    public string WorldId { get; }
    public string InstanceId => Instance.InstanceId;

    /// <summary>
    /// True for the instance the dialog was opened for: the one an invite names, the one a
    /// friend is in, or the one a location points at. It is pinned to the top of the list
    /// even when VRChat does not include it in the world's visible instances.
    /// </summary>
    public bool IsFocus => !string.IsNullOrWhiteSpace(_focusNote);

    public bool HasFocusNote => IsFocus;

    public string FocusNote => _focusNote ?? string.Empty;

    public void MarkFocus(string? note)
    {
        _focusNote = note;
        OnPropertyChanged(nameof(IsFocus));
        OnPropertyChanged(nameof(HasFocusNote));
        OnPropertyChanged(nameof(FocusNote));
        OnPropertyChanged(nameof(DetailText));
    }

    /// <summary>Group instances carry their group in the instance id,
    /// e.g. `12345~group(grp_…)~groupAccessType(public)`; other instances have none.</summary>
    public string? GroupId { get; }

    public bool HasGroup => !string.IsNullOrWhiteSpace(GroupId);

    public string? GroupName => _groupName;

    public bool IsGroupMember => _isGroupMember;

    /// <summary>Says what the viewer can do with the group, so the chip is not just "Group".</summary>
    public string GroupStateText => !HasGroup
        ? string.Empty
        : !_groupResolved
            ? "loading…"
            : _isGroupMember
                ? "Joined"
                : string.Equals(_groupMembershipStatus, "requested", StringComparison.OrdinalIgnoreCase)
                    ? "Request pending"
                    : _groupJoinState switch
                    {
                        "open" => "Open to join",
                        "request" => "Request to join",
                        null or "" or "unknown" => "View group",
                        _ => "Invite only"
                    };

    public string GroupChipText => HasGroup ? $"Group · {GroupStateText}" : string.Empty;

    public string Location
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Instance.Location))
            {
                return Instance.Location;
            }
            if (!string.IsNullOrWhiteSpace(Instance.WorldId))
            {
                return $"{Instance.WorldId}:{Instance.InstanceId}";
            }
            return $"{WorldId}:{Instance.InstanceId}";
        }
    }

    public int Occupants => Instance.Occupants;

    public string AccessLabel => InstancePresentation.AccessLabel(Instance.Type, Instance.InstanceId);
    public string RegionLabel => InstancePresentation.RegionLabel(Instance.Region ?? Instance.InstanceId);

    public string OccupancyText
    {
        get
        {
            var capacity = Instance.Capacity > 0 ? Instance.Capacity : Instance.RecommendedCapacity;
            return capacity > 0
                ? $"{Occupants:N0} / {capacity:N0} people"
                : $"{Occupants:N0} people";
        }
    }

    public string DetailText => IsFocus && Occupants <= 0
        ? $"{AccessLabel} · {RegionLabel}"
        : $"{OccupancyText} · {RegionLabel}";

    public string Title => !string.IsNullOrWhiteSpace(_groupName)
        ? _groupName!
        : string.IsNullOrWhiteSpace(Instance.DisplayName)
            ? $"{AccessLabel} instance"
            : Instance.DisplayName!;

    public bool CanJoin => !string.IsNullOrWhiteSpace(InstanceId);

    /// <summary>Applies what the group lookup learned; null means it could not be read.</summary>
    /// <summary>Keeps an open row honest when the group popup joins, requests or leaves.</summary>
    public void SetGroupMembership(bool isMember, string? membershipStatus = null)
    {
        if (_isGroupMember == isMember &&
            string.Equals(_groupMembershipStatus, membershipStatus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _isGroupMember = isMember;
        _groupMembershipStatus = membershipStatus;
        OnPropertyChanged(nameof(IsGroupMember));
        OnPropertyChanged(nameof(GroupStateText));
        OnPropertyChanged(nameof(GroupChipText));
    }

    public void ApplyGroup(Group? group)
    {
        _groupResolved = true;
        _groupName = group?.Name;
        _isGroupMember = group?.IsMember == true;
        _groupMembershipStatus = group?.MembershipStatus;
        _groupJoinState = group?.JoinState;
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsGroupMember));
        OnPropertyChanged(nameof(GroupStateText));
        OnPropertyChanged(nameof(GroupChipText));
    }

    private static string? ParseGroupId(string? value) => InstancePresentation.GroupIdFrom(value);
}

/// <summary>State for the world details overlay.</summary>
public sealed class WorldDialogViewModel : ViewModelBase
{
    private readonly WorldItem _summary;
    private readonly VrcApiClient _api;
    private readonly ImageLoader _images;
    private VrcWorld? _world;
    private ImageSource? _heroImage;
    private InstanceTypeOption _selectedType;
    private RegionOption _selectedRegion;
    private string _joinInstanceInput = string.Empty;
    private VrcInstance? _createdInstance;
    private string? _errorMessage;
    private string? _createError;
    private bool _isLoading;
    private bool _isCreating;
    private readonly string? _focusLocation;
    private readonly string? _focusNote;
    private readonly Action<string, string, string>? _openUserProfile;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<InstanceRosterPlayer>?>>? _liveRoster;
    private readonly Func<VrcInstance?>? _currentInstance;
    private readonly Func<CancellationToken, Task>? _refreshLiveState;
    private WorldInstanceItem? _selectedInstance;
    private DispatcherTimer? _playerRefreshTimer;
    private DispatcherTimer? _liveRefreshTimer;
    private CancellationTokenSource? _liveCancellation;
    private bool _liveRefreshing;
    private CancellationTokenSource? _playersCancellation;
    private WorldInstanceItem? _syntheticCurrentInstance;
    private string? _currentLocation;
    private bool _playersLoading;

    /// <summary>
    /// How often the open instance is re-read. Every tick is one API call at most,
    /// ticks never overlap, and the timer only runs while the panel is open.
    /// </summary>
    internal static TimeSpan PlayersRefreshInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How often the dialog re-checks which instance the account is in, with the Players
    /// panel closed. Reading the game log is a local file tail; VRChat is only called when
    /// the instance actually changes.
    /// </summary>
    internal static TimeSpan LiveStateRefreshInterval { get; set; } = TimeSpan.FromSeconds(15);
    private InstanceOwnerOption _selectedOwner;
    private bool _isLoadingOwners;
    private bool _ownersLoaded;
    private bool _ageGate;
    private WorldInstanceItem? _focusInstance;
    // Group details do not change while a world is open, so a refresh reuses them
    // instead of re-querying the same group for every rebuilt row.
    private readonly ConcurrentDictionary<string, Group> _groupCache = new(StringComparer.OrdinalIgnoreCase);

    public WorldDialogViewModel(
        WorldItem summary,
        VrcApiClient api,
        ImageLoader images,
        string? focusLocation = null,
        string? focusNote = null,
        Action<string, string, string>? openUserProfile = null,
        Func<string, CancellationToken, Task<IReadOnlyList<InstanceRosterPlayer>?>>? liveRoster = null,
        Func<VrcInstance?>? currentInstance = null,
        Func<CancellationToken, Task>? refreshLiveState = null)
    {
        _summary = summary;
        _api = api;
        _images = images;
        _focusLocation = focusLocation;
        _focusNote = focusNote;
        _openUserProfile = openUserProfile;
        _liveRoster = liveRoster;
        _currentInstance = currentInstance;
        _refreshLiveState = refreshLiveState;
        HeroImage = summary.Image;

        InstanceTypes = new ObservableCollection<InstanceTypeOption>(PersonalInstanceTypes);
        _selectedType = InstanceTypes[0];

        Regions = new ObservableCollection<RegionOption>
        {
            new("US West", "us", "Los Angeles"),
            new("US East", "use", "Virginia"),
            new("Europe", "eu", "Frankfurt"),
            new("Japan", "jp", "Tokyo")
        };
        _selectedRegion = Regions[0];

        Owners = new ObservableCollection<InstanceOwnerOption>
        {
            new(
                "Just you",
                null,
                "A personal instance you own",
                Array.Empty<string>(),
                ageGateAllowed: true)
        };
        _selectedOwner = Owners[0];
        UsePersonalInstanceCommand = new RelayCommand(
            () => SelectedOwner = Owners[0],
            () => IsGroupOwner);
        ShowInstancePlayersCommand = new RelayCommand<WorldInstanceItem>(
            item =>
            {
                if (item is not null)
                {
                    _ = ShowInstancePlayersAsync(item);
                }
            },
            item => item is not null && item.CanJoin);
        CloseInstancePlayersCommand = new RelayCommand(() =>
        {
            StopPlayerRefresh();
            SelectedInstance = null;
        });
        RefreshInstancePlayersCommand = new RelayCommand(
            () => _ = RefreshOpenPlayersAsync(),
            () => SelectedInstance is not null && !_playersLoading);

        Instances = new ObservableCollection<WorldInstanceItem>();
    }

    public WorldItem Summary => _summary;

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

    public ImageSource? HeroImage
    {
        get => _heroImage;
        private set => SetProperty(ref _heroImage, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsNotLoading));
            }
        }
    }

    public bool IsNotLoading => !IsLoading;

    public bool IsCreating
    {
        get => _isCreating;
        private set
        {
            if (SetProperty(ref _isCreating, value))
            {
                OnPropertyChanged(nameof(IsNotCreating));
            }
        }
    }

    public bool IsNotCreating => !IsCreating;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>VRChat's answer when creating an instance fails, shown by the button.</summary>
    public string? CreateError
    {
        get => _createError;
        private set
        {
            if (SetProperty(ref _createError, value))
            {
                OnPropertyChanged(nameof(HasCreateError));
            }
        }
    }

    public bool HasCreateError => !string.IsNullOrWhiteSpace(CreateError);

    public ObservableCollection<WorldInstanceItem> Instances { get; }

    /// <summary>The rows the panel shows; a long world's instances page instead of all drawing.</summary>
    public PagedList<WorldInstanceItem> InstancesPage { get; } = new(8);

    public bool HasActiveInstances => Instances.Count > 0;

    /// <summary>The instance whose player list is open below the active instances.</summary>
    public WorldInstanceItem? SelectedInstance
    {
        get => _selectedInstance;
        private set
        {
            if (SetProperty(ref _selectedInstance, value))
            {
                OnPropertyChanged(nameof(HasSelectedInstance));
                OnPropertyChanged(nameof(PlayersRefreshText));
                RefreshInstancePlayersCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedInstance => _selectedInstance is not null;

    public RelayCommand<WorldInstanceItem> ShowInstancePlayersCommand { get; }

    public RelayCommand CloseInstancePlayersCommand { get; }

    public RelayCommand RefreshInstancePlayersCommand { get; }

    private bool _isRefreshingPlayers;
    public bool IsRefreshingPlayers
    {
        get => _isRefreshingPlayers;
        private set
        {
            if (SetProperty(ref _isRefreshingPlayers, value))
            {
                OnPropertyChanged(nameof(PlayersRefreshText));
            }
        }
    }

    public string PlayersRefreshText
    {
        get
        {
            if (_isRefreshingPlayers)
            {
                return "Refreshing…";
            }

            return SelectedInstance?.PlayersUpdatedAt is { } at
                ? $"Updated {at:h:mm:ss tt}"
                : string.Empty;
        }
    }

    public string PlayersRefreshHint
        => $"Refreshes every {PlayersRefreshInterval.TotalSeconds:0} s while open";

    /// <summary>
    /// Shows who is in an instance. The game's own log is the only complete source for an
    /// instance this account is in (VRChat returns users only for instances it created), so
    /// that roster is preferred; otherwise the instance endpoint is read. While the panel is
    /// open a timer re-reads it, which is how joins and leaves show up without reopening.
    /// </summary>
    public async Task ShowInstancePlayersAsync(WorldInstanceItem item)
    {
        SelectedInstance = item;
        StartPlayerRefresh();
        await LoadPlayersAsync(item, announce: !item.PlayersLoaded);
    }

    /// <summary>Re-reads the open instance without closing anything. Also the timer's tick.</summary>
    public Task RefreshOpenPlayersAsync()
        => SelectedInstance is { } item ? LoadPlayersAsync(item, announce: false) : Task.CompletedTask;

    private async Task LoadPlayersAsync(WorldInstanceItem item, bool announce)
    {
        if (_playersLoading)
        {
            return;
        }

        _playersLoading = true;
        if (announce)
        {
            item.BeginPlayerLoad();
        }
        else
        {
            IsRefreshingPlayers = true;
        }

        var token = _playersCancellation?.Token ?? CancellationToken.None;
        try
        {
            // "You are here" can change while the panel is open; keep the rows honest first.
            SyncCurrentInstanceRow();

            var roster = _liveRoster is null
                ? null
                : await _liveRoster(item.Location, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (roster is not null)
            {
                var players = OrderPlayers(
                    roster.Select(player => new WorldInstancePlayerItem(player, OpenProfile)));
                item.SetPlayers(players, fromLiveRoster: true);
                item.ApplyRosterCount(roster.Count);
                item.SetPlayersNote(null);
                item.MarkPlayersUpdated(DateTimeOffset.Now);
                return;
            }

            var wasLive = item.PlayersFromLiveRoster;
            var instance = await _api.GetInstanceAsync(item.Location, token);
            if (instance is null)
            {
                item.SetPlayersError("VRChat could not load this instance. It may have closed.");
                StopPlayerRefresh();
                return;
            }

            item.ApplyLiveInstance(instance);
            if (instance.Users.Count > 0)
            {
                var selfId = _api.CurrentUser?.Id;
                var players = OrderPlayers(instance.Users.Select(user => new WorldInstancePlayerItem(
                    user,
                    OpenProfile,
                    isSelf: !string.IsNullOrWhiteSpace(selfId) &&
                            string.Equals(user.Id, selfId, StringComparison.Ordinal))));
                item.SetPlayers(players, fromLiveRoster: false);
                item.SetPlayersNote(null);
            }
            else if (wasLive)
            {
                // You left this instance: the log roster no longer applies, and the API will
                // not name the people in someone else's instance.
                item.SetPlayers(Array.Empty<WorldInstancePlayerItem>(), fromLiveRoster: false);
                item.SetPlayersNote("You are no longer in this instance.");
            }
            else if (!item.HasPlayers)
            {
                item.SetPlayers(Array.Empty<WorldInstancePlayerItem>(), fromLiveRoster: false);
                item.SetPlayersNote(
                    "VRChat only shares the player list for an instance you are in or created. " +
                    "The count above comes from VRChat; join to see who is here.");
            }

            item.MarkPlayersUpdated(DateTimeOffset.Now);
        }
        catch (OperationCanceledException)
        {
            // The panel or the dialog closed while this read was in flight.
        }
        catch (VrcApiException ex)
        {
            ReportPlayerError(item, ex.Message, announce);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ReportPlayerError(item, "Could not reach VRChat to load this instance's players.", announce);
        }
        finally
        {
            _playersLoading = false;
            IsRefreshingPlayers = false;
            item.EndPlayerLoad();
            OnPropertyChanged(nameof(PlayersRefreshText));
            RefreshInstancePlayersCommand.RaiseCanExecuteChanged();
        }
    }

    private void ReportPlayerError(WorldInstanceItem item, string message, bool announce)
    {
        if (announce || !item.HasPlayers)
        {
            item.SetPlayersError(message);
        }
    }

    private void OpenProfile(WorldInstancePlayerItem player)
        => _openUserProfile?.Invoke(player.UserId, player.DisplayName, player.AvatarUrl);

    /// <summary>You first, then friends, then everyone else by name - easy to scan.</summary>
    private static List<WorldInstancePlayerItem> OrderPlayers(IEnumerable<WorldInstancePlayerItem> players)
        => players
            .OrderByDescending(player => player.IsSelf)
            .ThenByDescending(player => player.IsFriend)
            .ThenBy(player => player.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private void StartPlayerRefresh()
    {
        _playersCancellation ??= new CancellationTokenSource();
        if (_playerRefreshTimer is null)
        {
            _playerRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = PlayersRefreshInterval
            };
            _playerRefreshTimer.Tick += (_, _) =>
            {
                if (SelectedInstance is null)
                {
                    StopPlayerRefresh();
                    return;
                }

                _ = RefreshOpenPlayersAsync();
            };
        }

        _playerRefreshTimer.Interval = PlayersRefreshInterval;
        _playerRefreshTimer.Start();
        OnPropertyChanged(nameof(PlayersRefreshHint));
    }

    /// <summary>Re-checks "you are here" against the live instance, adding or removing the row.</summary>
    internal void RefreshCurrentInstance() => SyncCurrentInstanceRow();

    /// <summary>
    /// Keeps "you are here" honest while the dialog is open, whether or not the Players
    /// panel is: the provider re-reads the live instance, then the label follows it.
    /// </summary>
    internal async Task RefreshLiveStateAsync()
    {
        if (_refreshLiveState is null || _liveRefreshing)
        {
            return;
        }

        _liveRefreshing = true;
        try
        {
            await _refreshLiveState(_liveCancellation?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // The dialog closed mid-read.
        }
        catch (Exception ex)
        {
            Debug.WriteLine("world dialog live instance: " + ex.Message);
        }
        finally
        {
            _liveRefreshing = false;
            if (!(_liveCancellation?.IsCancellationRequested ?? false))
            {
                SyncCurrentInstanceRow();
            }
        }
    }

    internal bool IsLiveRefreshRunning => _liveRefreshTimer?.IsEnabled == true;

    private void StartLiveRefresh()
    {
        if (_refreshLiveState is null)
        {
            return;
        }

        _liveCancellation ??= new CancellationTokenSource();
        if (_liveRefreshTimer is null)
        {
            _liveRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = LiveStateRefreshInterval
            };
            _liveRefreshTimer.Tick += (_, _) => _ = RefreshLiveStateAsync();
        }

        _liveRefreshTimer.Interval = LiveStateRefreshInterval;
        _liveRefreshTimer.Start();
        // The dialog opens on a world the reader may already be in; do not make the
        // label wait for the first tick.
        _ = RefreshLiveStateAsync();
    }

    /// <summary>Stops the current-instance timer; the dialog calls this when it closes.</summary>
    public void StopLiveRefresh()
    {
        _liveRefreshTimer?.Stop();
        _liveCancellation?.Cancel();
        _liveCancellation?.Dispose();
        _liveCancellation = null;
    }

    internal bool IsPlayerRefreshRunning => _playerRefreshTimer?.IsEnabled == true;

    /// <summary>Stops the timer; the dialog calls this when it closes.</summary>
    public void StopPlayerRefresh()
    {
        _playerRefreshTimer?.Stop();
        _playersCancellation?.Cancel();
        _playersCancellation?.Dispose();
        _playersCancellation = null;
        RefreshInstancePlayersCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The instance this dialog was opened for, pinned at the top of the list.</summary>
    public WorldInstanceItem? FocusInstance
    {
        get => _focusInstance;
        private set => SetProperty(ref _focusInstance, value);
    }

    /// <summary>The account itself first, then each group whose role may create an instance.</summary>
    public ObservableCollection<InstanceOwnerOption> Owners { get; }

    public InstanceOwnerOption SelectedOwner
    {
        get => _selectedOwner;
        set
        {
            if (value is null || ReferenceEquals(_selectedOwner, value))
            {
                return;
            }

            _selectedOwner = value;
            OnPropertyChanged(nameof(SelectedOwner));
            OnPropertyChanged(nameof(IsGroupOwner));
            OnPropertyChanged(nameof(CreateButtonText));
            OnPropertyChanged(nameof(HasAgeGateOption));
            OnPropertyChanged(nameof(ShowAgeGateUnavailable));
            UsePersonalInstanceCommand.RaiseCanExecuteChanged();

            // A group whose role cannot create age-gated instances must not carry a
            // tick from the account over into a create VRChat will reject.
            if (!HasAgeGateOption && _ageGate)
            {
                _ageGate = false;
                OnPropertyChanged(nameof(AgeGate));
            }

            RebuildInstanceTypes();
        }
    }

    public bool IsGroupOwner => _selectedOwner.IsGroup;

    /// <summary>The account always offers the option; a group only when its role has
    /// `group-instance-age-gated-create`.</summary>
    public bool HasAgeGateOption => !_selectedOwner.IsGroup || _selectedOwner.AgeGateAllowed;

    public bool ShowAgeGateUnavailable => !HasAgeGateOption;

    public string AgeGateUnavailableText =>
        "This group's role can't create age-gated instances, so the 18+ option is off. " +
        "A group moderator can grant it the age-gated instance permission.";

    public RelayCommand UsePersonalInstanceCommand { get; }

    public string CreateButtonText => IsGroupOwner
        ? $"Create for {_selectedOwner.Label}"
        : "Create instance";

    public bool IsLoadingOwners
    {
        get => _isLoadingOwners;
        private set
        {
            if (SetProperty(ref _isLoadingOwners, value))
            {
                OnPropertyChanged(nameof(HasGroupOwners));
                OnPropertyChanged(nameof(OwnerHintText));
            }
        }
    }

    public bool HasGroupOwners => Owners.Any(owner => owner.IsGroup);

    public string OwnerHintText => IsLoadingOwners
        ? "Checking your groups for instance permissions…"
        : HasGroupOwners
            ? "Group instances are hosted by the group. VRChat only offers the access types your group role allows."
            : "No group of yours allows instance creation, or you are not signed in. A group role needs an instance-creation permission to host for it.";

    public ObservableCollection<InstanceTypeOption> InstanceTypes { get; }

    public InstanceTypeOption SelectedType
    {
        get => _selectedType;
        set
        {
            if (value is not null && SetProperty(ref _selectedType, value))
            {
                OnPropertyChanged(nameof(SelectedTypeDescription));
            }
        }
    }

    public string SelectedTypeDescription => SelectedType.Description;

    /// <summary>Restricts the new instance to age-verified (18+) players.</summary>
    public bool AgeGate
    {
        get => _ageGate;
        set
        {
            if (value && !HasAgeGateOption)
            {
                return;
            }

            SetProperty(ref _ageGate, value);
        }
    }

    public ObservableCollection<RegionOption> Regions { get; }

    public RegionOption SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedRegion, value);
            }
        }
    }

    public string JoinInstanceInput
    {
        get => _joinInstanceInput;
        set => SetProperty(ref _joinInstanceInput, value ?? string.Empty);
    }

    public VrcInstance? CreatedInstance
    {
        get => _createdInstance;
        private set
        {
            if (SetProperty(ref _createdInstance, value))
            {
                OnPropertyChanged(nameof(HasCreatedInstance));
                OnPropertyChanged(nameof(CreatedTitle));
                OnPropertyChanged(nameof(CreatedMeta));
                OnPropertyChanged(nameof(CreatedShareLink));
            }
        }
    }

    public bool HasCreatedInstance => CreatedInstance is not null;

    public string CreatedTitle
    {
        get
        {
            if (CreatedInstance is null)
            {
                return string.Empty;
            }

            var access = InstancePresentation.AccessLabel(CreatedInstance.Type, CreatedInstance.InstanceId);
            var region = InstancePresentation.RegionLabel(CreatedInstance.Region ?? CreatedInstance.InstanceId);
            return $"{access} instance ready · {region}";
        }
    }

    public string CreatedMeta
    {
        get
        {
            if (CreatedInstance is null)
            {
                return string.Empty;
            }

            var shortName = CreatedInstance.ShortName;
            return string.IsNullOrWhiteSpace(shortName)
                ? "Created just now"
                : $"Short code {shortName} · created just now";
        }
    }

    public string CreatedShareLink
    {
        get
        {
            if (CreatedInstance is null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(CreatedInstance.ShortName))
            {
                return $"https://vrch.at/{CreatedInstance.ShortName}";
            }

            return $"https://vrchat.com/home/launch?worldId={Uri.EscapeDataString(WorldId)}&instanceId={Uri.EscapeDataString(CreatedInstance.InstanceId)}";
        }
    }

    public string Name => string.IsNullOrWhiteSpace(World?.Name) ? _summary.Name : World!.Name;

    public string AuthorText => string.IsNullOrWhiteSpace(World?.AuthorName)
        ? _summary.AuthorText
        : $"by {World!.AuthorName}";

    public string WorldId => string.IsNullOrWhiteSpace(World?.Id) ? _summary.Id : World!.Id;

    public string WebUrl => $"https://vrchat.com/home/world/{WorldId}";

    public string Description => string.IsNullOrWhiteSpace(World?.Description)
        ? "No description was provided for this world."
        : World!.Description!.Trim();

    public string PlatformsText
    {
        get
        {
            var platforms = (World?.SupportedPlatforms ?? new List<string>())
                .Select(platform => platform.ToLowerInvariant() switch
                {
                    "standalonewindows" or "standalonewindows64" => "PC",
                    "android" => "Quest",
                    "ios" => "iOS",
                    _ => platform
                })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return platforms.Length == 0 ? _summary.PlatformsText : string.Join(" · ", platforms);
        }
    }

    public string OccupancyText
    {
        get
        {
            var world = World;
            if (world is null)
            {
                return _summary.MetaText;
            }

            return $"{world.Occupants:N0} online · {world.Capacity:N0} capacity";
        }
    }

    public string VisitsText => World is null ? "—" : World.Visits.ToString("N0", CultureInfo.CurrentCulture);
    public string FavoritesText => World is null ? "—" : World.Favorites.ToString("N0", CultureInfo.CurrentCulture);

    public string ReleaseStatusText => string.IsNullOrWhiteSpace(World?.ReleaseStatus)
        ? "Public"
        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(World!.ReleaseStatus!.Replace('_', ' '));

    public string CreatedText => World?.PublicationDate is { Length: > 0 } published && published != "none"
        ? $"Published {FormatDate(published)}"
        : World?.CreatedAt is { } created
            ? $"Created {created:MMM yyyy}"
            : string.Empty;

    public bool HasCreatedText => !string.IsNullOrWhiteSpace(CreatedText);

    public string UpdatedText => World?.UpdatedAt is { } updated
        ? $"Updated {TimeText.Relative(updated)}"
        : string.Empty;

    public bool HasUpdatedText => !string.IsNullOrWhiteSpace(UpdatedText);

    public string TagsText
    {
        get
        {
            var tags = (World?.Tags ?? new List<string>())
                .Where(tag => tag.StartsWith("author_tag_", StringComparison.OrdinalIgnoreCase))
                .Select(tag => tag["author_tag_".Length..].Replace('_', ' ').Trim())
                .Where(tag => tag.Length > 0)
                .Select(tag => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();

            return tags.Length == 0 ? "Community world" : string.Join(" · ", tags);
        }
    }

    public bool HasDescription => !string.IsNullOrWhiteSpace(World?.Description);

    public async Task LoadAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        IsLoading = true;
        ErrorMessage = null;

        // Your groups load beside the world: the dialog is usable without them, and the
        // create card fills in the moment VRChat answers.
        _ = LoadOwnersAsync(cancellationToken);

        try
        {
            var world = await _api.GetWorldAsync(WorldId, forceRefresh, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (world is null)
            {
                ErrorMessage = "VRChat did not return this world's details.";
                return;
            }

            World = world;
            RebuildInstances(cancellationToken);
            StartLiveRefresh();
            _ = LoadHeroImageAsync(world, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Dialog closed or another world opened.
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat to load this world.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not load the full world details.";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    public void SetCreatedInstance(VrcInstance instance)
    {
        CreatedInstance = instance;
    }

    public VrcInstance? GetCreatedInstance() => CreatedInstance;

    public async Task<VrcInstance?> CreateInstanceAsync(CancellationToken cancellationToken)
    {
        IsCreating = true;
        ErrorMessage = null;
        CreateError = null;
        try
        {
            var owner = SelectedOwner;
            // A group instance is owned by the group, and its access comes from the
            // group access kind rather than the personal public/friends/private types.
            var instance = owner.IsGroup
                ? await _api.CreateInstanceAsync(
                    WorldId,
                    "group",
                    SelectedRegion.Value,
                    canRequestInvite: false,
                    cancellationToken,
                    ownerId: owner.GroupId,
                    groupAccessType: SelectedType.Value,
                    ageGate: AgeGate)
                : await _api.CreateInstanceAsync(
                    WorldId,
                    SelectedType.Value,
                    SelectedRegion.Value,
                    SelectedType.CanRequestInvite,
                    cancellationToken,
                    ageGate: AgeGate);

            CreatedInstance = instance;
            _ = LoadUpdatedInstancesAsync(cancellationToken);
            return instance;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (VrcApiException ex)
        {
            CreateError = ex.Message;
            return null;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            CreateError = "Could not reach VRChat to create the instance.";
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            CreateError = "Could not create that instance.";
            return null;
        }
        finally
        {
            IsCreating = false;
        }
    }

    public void ReportError(string message) => ErrorMessage = message;

    /// <summary>The group lookups the last instance rebuild started, so they can be awaited.</summary>
    internal Task? GroupHydration { get; private set; }

    /// <summary>
    /// Joining or leaving from the group popup invalidates the cached group and updates
    /// the rows already on screen, so a refresh does not resurrect the old membership.
    /// </summary>
    public void ApplyGroupMembership(string groupId, bool isMember, string? membershipStatus = null)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return;
        }

        _groupCache.TryRemove(groupId, out _);
        foreach (var item in Instances.Where(item =>
                     string.Equals(item.GroupId, groupId, StringComparison.OrdinalIgnoreCase)))
        {
            item.SetGroupMembership(isMember, membershipStatus);
        }
    }

    private async Task LoadUpdatedInstancesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var world = await _api.GetWorldAsync(WorldId, forceRefresh: true, cancellationToken);
            if (cancellationToken.IsCancellationRequested || world is null)
            {
                return;
            }

            World = world;
            RebuildInstances(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Dialog closed.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void RebuildInstances(CancellationToken cancellationToken = default)
    {
        StopPlayerRefresh();
        Instances.Clear();
        InstancesPage.Clear();
        FocusInstance = null;
        SelectedInstance = null;
        _syntheticCurrentInstance = null;
        _currentLocation = null;
        if (World is null)
        {
            OnPropertyChanged(nameof(HasActiveInstances));
            return;
        }

        var items = World.SlimInstances
            .Where(item => !string.IsNullOrWhiteSpace(item.InstanceId))
            .Select(instance => new WorldInstanceItem(instance, World.Id, viewPlayers: ShowInstancePlayers))
            .ToList();

        var added = new List<WorldInstanceItem>();

        // The instance the dialog was opened for goes first and stays even when VRChat
        // does not list it (a private invite is not in the world's visible instances).
        var focus = BuildFocusInstance(items);

        // The instance this account is standing in is pinned above everything, with a
        // "You are here" label, and it is added when VRChat's world payload omits it
        // (which is exactly the case that made a searched world look like it had no row
        // for the reader).
        var live = _currentInstance?.Invoke();
        WorldInstanceItem? current = null;
        if (live is not null && IsWorldLocation(live.Location, World?.Id ?? _summary.Id))
        {
            _currentLocation = live.Location;
            current = items.FirstOrDefault(item => SameInstance(item.Location, live.Location));
            if (current is not null)
            {
                items.Remove(current);
            }
            else if (focus is not null && SameInstance(focus.Location, live.Location))
            {
                current = focus;
                focus = null;
            }
            else
            {
                current = new WorldInstanceItem(live, World.Id, viewPlayers: ShowInstancePlayers);
                _syntheticCurrentInstance = current;
            }

            current.MarkCurrent(true);
        }

        // Most crowded first, so the list reads like a room browser.
        items = items.OrderByDescending(item => item.Occupants).ToList();
        if (current is not null && !ReferenceEquals(current, focus))
        {
            Instances.Add(current);
            added.Add(current);
        }
        if (focus is not null)
        {
            Instances.Add(focus);
            added.Add(focus);
        }
        foreach (var item in items)
        {
            Instances.Add(item);
            added.Add(item);
        }

        InstancesPage.SetSource(Instances, resetPage: true);
        OnPropertyChanged(nameof(HasActiveInstances));
        GroupHydration = HydrateGroupsAsync(added, cancellationToken);
    }

    /// <summary>
    /// Keeps the "You are here" row true while the dialog stays open. A leave removes the
    /// label (and the row added for it), a join adds it, and the panel is never closed by it.
    /// </summary>
    private void SyncCurrentInstanceRow()
    {
        if (World is null)
        {
            return;
        }

        var live = _currentInstance?.Invoke();
        var location = live is not null && IsWorldLocation(live.Location, World?.Id ?? _summary.Id)
            ? live.Location
            : null;

        if (SameInstance(location, _currentLocation))
        {
            foreach (var item in Instances)
            {
                item.MarkCurrent(SameInstance(item.Location, location));
            }
            return;
        }

        _currentLocation = location;
        if (_syntheticCurrentInstance is not null)
        {
            Instances.Remove(_syntheticCurrentInstance);
            _syntheticCurrentInstance = null;
        }

        var target = location is null
            ? null
            : Instances.FirstOrDefault(item => SameInstance(item.Location, location));
        if (target is null && location is not null && live is not null)
        {
            target = new WorldInstanceItem(live, World.Id, viewPlayers: ShowInstancePlayers);
            target.MarkCurrent(true);
            Instances.Insert(0, target);
            _syntheticCurrentInstance = target;
        }

        foreach (var item in Instances)
        {
            item.MarkCurrent(target is not null && ReferenceEquals(item, target));
        }

        InstancesPage.SetSource(Instances);
        OnPropertyChanged(nameof(HasActiveInstances));
    }

    private static bool IsWorldLocation(string? location, string worldId)
        => LaunchService.IsLocation(location) &&
           string.Equals(
               LaunchService.GetWorldIdFromLocation(location!),
               worldId,
               StringComparison.OrdinalIgnoreCase);

    private static bool SameInstance(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private void ShowInstancePlayers(WorldInstanceItem item) => _ = ShowInstancePlayersAsync(item);

    private WorldInstanceItem? BuildFocusInstance(List<WorldInstanceItem> listed)
    {
        if (!LaunchService.IsLocation(_focusLocation) || string.IsNullOrWhiteSpace(_focusNote))
        {
            return null;
        }

        if (!string.Equals(
                LaunchService.GetWorldIdFromLocation(_focusLocation),
                World?.Id ?? _summary.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var instanceId = LaunchService.GetInstanceIdFromLocation(_focusLocation);
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        var item = listed.FirstOrDefault(candidate =>
            string.Equals(candidate.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            listed.Remove(item);
        }
        else
        {
            item = new WorldInstanceItem(
                new VrcInstance
                {
                    InstanceId = instanceId,
                    WorldId = World?.Id ?? _summary.Id,
                    Location = _focusLocation ?? string.Empty
                },
                World?.Id ?? _summary.Id,
                viewPlayers: ShowInstancePlayers);
        }

        item.MarkFocus(_focusNote);
        FocusInstance = item;
        return item;
    }

    /// <summary>
    /// Your account plus the groups whose role may open an instance. VRChat sends the
    /// permissions with the membership; when it does not, the group stays listed and
    /// VRChat itself rejects an access kind the role cannot create.
    /// </summary>
    private async Task LoadOwnersAsync(CancellationToken cancellationToken)
    {
        // A refresh reloads the world and its instances; the account's groups do not
        // change while the dialog is open, so they are read once.
        if (_ownersLoaded || _api.CurrentUser is null)
        {
            return;
        }

        _ownersLoaded = true;
        IsLoadingOwners = true;
        try
        {
            var groups = await _api.GetMyGroupsAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // The membership list's myMember is not always complete; the permissions
            // endpoint is. Without this a moderator's role looked like one with no
            // age-gated (or public) instance rights at all.
            IReadOnlyDictionary<string, IReadOnlyList<string>>? reportedPermissions = null;
            string? permissionsError = null;
            try
            {
                reportedPermissions = await _api.GetMyGroupPermissionsAsync(cancellationToken);
                foreach (var group in groups)
                {
                    if (reportedPermissions.TryGetValue(group.EffectiveId, out var allowed))
                    {
                        group.MyMember ??= new GroupMember();
                        group.MyMember.Permissions = allowed.ToList();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                permissionsError = ex.Message;
                Debug.WriteLine($"world dialog group permissions: {ex.Message}");
            }

            GroupPermissionDiagnostics.Record(groups, reportedPermissions, permissionsError);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            foreach (var group in groups
                         .Where(group => group.IsMember || string.IsNullOrWhiteSpace(group.MembershipStatus))
                         .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var access = InstancePresentation.GroupInstanceAccess(group);
                if (access.Count == 0)
                {
                    continue;
                }

                var label = string.IsNullOrWhiteSpace(group.Name) ? "Group" : group.Name;
                // An unknown role is left to VRChat to judge, the same way the access
                // list is; only a known role without the permission hides the option.
                var permissions = group.MyMember?.Permissions;
                var ageGateAllowed = group.MyMember is null ||
                                     permissions is null ||
                                     permissions.Contains("*") ||
                                     permissions.Contains("group-instance-age-gated-create");
                Owners.Add(new InstanceOwnerOption(
                    label,
                    group.EffectiveId,
                    $"Hosted by {label}",
                    access,
                    group.ThumbnailUrl,
                    ageGateAllowed));
            }
        }
        catch (OperationCanceledException)
        {
            // Dialog closed.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"world dialog owners: {ex.Message}");
        }
        finally
        {
            IsLoadingOwners = false;
            OnPropertyChanged(nameof(HasGroupOwners));
        }
    }

    /// <summary>
    /// The access list follows the owner: personal types for your account, and only the
    /// group access kinds the chosen group's role allows. The default is the most
    /// limited kind available so a group instance is never listed in public by accident.
    /// </summary>
    private void RebuildInstanceTypes()
    {
        var previous = _selectedType?.Value;
        InstanceTypes.Clear();

        if (_selectedOwner.IsGroup)
        {
            foreach (var access in _selectedOwner.AccessTypes)
            {
                InstanceTypes.Add(access switch
                {
                    "public" => new InstanceTypeOption(
                        "Group public",
                        "public",
                        false,
                        "Anyone can join. The instance is listed with public instances."),
                    "plus" => new InstanceTypeOption(
                        "Group+",
                        "plus",
                        false,
                        "Friends of group members can join too."),
                    _ => new InstanceTypeOption(
                        "Group members",
                        "members",
                        false,
                        "Only members of the group can join.")
                });
            }

            if (InstanceTypes.Count == 0)
            {
                InstanceTypes.Add(new InstanceTypeOption(
                    "Group members",
                    "members",
                    false,
                    "Only members of the group can join."));
            }

            _selectedType = InstanceTypes.FirstOrDefault(option => option.Value == previous)
                            ?? InstanceTypes.Last();
        }
        else
        {
            foreach (var option in PersonalInstanceTypes)
            {
                InstanceTypes.Add(option);
            }

            _selectedType = InstanceTypes.FirstOrDefault(option => option.Value == previous)
                            ?? InstanceTypes[0];
        }

        OnPropertyChanged(nameof(SelectedType));
        OnPropertyChanged(nameof(SelectedTypeDescription));
    }

    private static readonly InstanceTypeOption[] PersonalInstanceTypes =
    {
        new("Public", "public", false, "Anyone can join."),
        new("Friends+", "hidden", false, "Friends of friends can join."),
        new("Friends", "friends", false, "Only your friends can join."),
        new("Invite+", "private", true, "Invite only, friends can ask."),
        new("Invite", "private", false, "Only invited friends can join.")
    };

    /// <summary>
    /// Instances only name their group by id, so resolve each group once and let every
    /// row using it show the real name and the viewer's membership.
    /// </summary>
    private async Task HydrateGroupsAsync(
        IReadOnlyCollection<WorldInstanceItem> items,
        CancellationToken cancellationToken)
    {
        var pending = new List<IGrouping<string, WorldInstanceItem>>();
        foreach (var grouping in items
                     .Where(item => item.HasGroup)
                     .GroupBy(item => item.GroupId!, StringComparer.OrdinalIgnoreCase))
        {
            // A refresh rebuilds every row, so a group this dialog already read is
            // applied straight from the cache instead of being fetched again.
            if (_groupCache.TryGetValue(grouping.Key, out var cached))
            {
                foreach (var item in grouping)
                {
                    item.ApplyGroup(cached);
                }

                continue;
            }

            pending.Add(grouping);
        }

        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(pending.Select(async grouping =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    Group? info = null;
                    try
                    {
                        info = await _api.GetGroupAsync(grouping.Key, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"world dialog group {grouping.Key}: {ex.Message}");
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    // Only a real answer is cached; a failed lookup is retried on the
                    // next refresh. Either way the rows resolve, so the chip stops saying
                    // "loading" and falls back to opening the group.
                    if (info is not null)
                    {
                        _groupCache[grouping.Key] = info;
                    }

                    foreach (var item in grouping)
                    {
                        item.ApplyGroup(info);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        catch (OperationCanceledException)
        {
            // Dialog closed while the groups were loading.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task LoadHeroImageAsync(VrcWorld world, CancellationToken cancellationToken)
    {
        var url = !string.IsNullOrWhiteSpace(world.ImageUrl) ? world.ImageUrl : world.ThumbnailImageUrl;
        try
        {
            var image = await _images.LoadAsync(url, 900, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && image is not null)
            {
                HeroImage = image;
            }
        }
        catch (OperationCanceledException)
        {
            // Dialog closed.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static string FormatDate(string iso)
    {
        return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.ToString("MMM yyyy", CultureInfo.CurrentCulture)
            : string.Empty;
    }
}

internal static class InstancePresentation
{
    public static string AccessLabel(string? type, string? instanceId)
    {
        var value = instanceId ?? string.Empty;
        if (value.Contains("~group(", StringComparison.OrdinalIgnoreCase))
        {
            // Group instances say which kind of group access they use, so the
            // label matches VRChat's own wording instead of a generic "Group".
            if (value.Contains("groupAccessType(plus)", StringComparison.OrdinalIgnoreCase)) return "Group+";
            if (value.Contains("groupAccessType(public)", StringComparison.OrdinalIgnoreCase)) return "Group public";
            if (value.Contains("groupAccessType(members)", StringComparison.OrdinalIgnoreCase)) return "Group members";
            return "Group";
        }
        if (value.Contains("~hidden(", StringComparison.OrdinalIgnoreCase)) return "Friends+";
        if (value.Contains("~friends(", StringComparison.OrdinalIgnoreCase)) return "Friends";
        if (value.Contains("~private(", StringComparison.OrdinalIgnoreCase))
        {
            return value.Contains("canRequestInvite", StringComparison.OrdinalIgnoreCase)
                ? "Invite+"
                : "Invite";
        }

        return type?.ToLowerInvariant() switch
        {
            "hidden" => "Friends+",
            "friends" => "Friends",
            "private" => "Invite",
            "group" => "Group",
            _ => "Public"
        };
    }

    /// <summary>
    /// The group access kinds a member's role may create. VRChat sends the account's
    /// permissions with the group membership; a group without them is left open so the
    /// server, not a guess on this side, decides what the role can do.
    /// </summary>
    public static IReadOnlyList<string> GroupInstanceAccess(Group group)
    {
        // No myMember means VRChat did not send the role permissions with this row;
        // an empty list means the role really has none, so the group is not offered.
        if (group.MyMember is null)
        {
            return new[] { "public", "plus", "members" };
        }

        var permissions = group.MyMember.Permissions ?? new List<string>();
        if (permissions.Contains("*"))
        {
            return new[] { "public", "plus", "members" };
        }

        var allowed = new List<string>();
        if (permissions.Contains("group-instance-public-create") ||
            permissions.Contains("group-instance-open-create"))
        {
            allowed.Add("public");
        }
        if (permissions.Contains("group-instance-plus-create"))
        {
            allowed.Add("plus");
        }
        if (permissions.Contains("group-instance-restricted-create") ||
            permissions.Contains("group-instance-members-create"))
        {
            allowed.Add("members");
        }

        return allowed;
    }

    /// <summary>The grp_ id inside `~~group(grp_…)~…`, if the instance has one.</summary>
    public static string? GroupIdFrom(string? value)
    {
        const string marker = "~group(";
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = value.IndexOf(')', start);
        return end <= start ? null : value[start..end];
    }

    public static string RegionLabel(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Contains("~region(use)", StringComparison.OrdinalIgnoreCase)) return "US East";
        if (text.Contains("~region(us)", StringComparison.OrdinalIgnoreCase)) return "US West";
        if (text.Contains("~region(eu)", StringComparison.OrdinalIgnoreCase)) return "Europe";
        if (text.Contains("~region(jp)", StringComparison.OrdinalIgnoreCase)) return "Japan";

        return text.ToLowerInvariant() switch
        {
            "us" => "US West",
            "use" => "US East",
            "eu" => "Europe",
            "jp" => "Japan",
            _ => "Automatic"
        };
    }
}
