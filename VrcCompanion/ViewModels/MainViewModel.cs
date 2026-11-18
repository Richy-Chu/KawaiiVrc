using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly VrcApiClient _api;
    private readonly ImageLoader _images;
    private readonly SettingsStore _settingsStore;
    private readonly CompanionSettings _settings;
    private readonly List<IRelayCommand> _commands = new();
    private readonly List<string> _twoFactorMethods = new();
    private readonly NavItem _notificationsNav;
    private readonly NavItem _activityNav;
    private readonly NavItem _friendsNav;
    private readonly NavItem _instanceNav;
    private NavItem? _instanceReturnNav;
    private readonly ICollectionView _friendsView;
    private readonly ICollectionView _friendLocationsView;
    private readonly ICollectionView _friendChangesView;
    private readonly ICollectionView _notificationsView;
    private readonly DispatcherTimer _clockTimer;
    private VrChatLogPlayerService _logPlayerService;
    private readonly VrChatRealtimeService _realtime;
    private readonly InstanceHistoryStore _instanceHistoryStore;
    private readonly NotificationPersistenceStore _notificationStore;
    private InviteMessageMemoryStore _inviteMessageMemory = InviteMessageMemoryStore.Shared;
    private string _greetingName = string.Empty;
    private readonly HashSet<string> _favoriteFriends;
    private readonly HashSet<string> _favoriteWorlds;
    private readonly HashSet<string> _onlineFriendIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeFriendIds = new(StringComparer.Ordinal);

    private NavItem _selectedSection = null!;
    private StatusOption _selectedStatus = null!;
    private string _statusMessageInput = string.Empty;
    private bool _isStatusDirty;
    private bool _loadingRemainingFriends;
    private Task? _fullFriendLoad;
    private readonly FriendChangeCoalescer _friendChanges;
    private bool _suppressStatusDirty;
    private bool _initialized;
    private bool _worldsLoaded;
    private bool _disposed;
    private int _busyDepth;
    private string? _avatarUrl;
    private DateTimeOffset? _lastUpdated;
    private DateTimeOffset _lastFullFriendLoadAt = DateTimeOffset.MinValue;
    private TwoFactorMethod _twoFactorMethod = TwoFactorMethod.Authenticator;
    private ImageSource? _avatarImage;
    private FriendItem? _selectedFriend;
    private FriendDetailViewModel? _friendDetail;
    private CancellationTokenSource? _friendDetailCancellation;
    private WorldDialogViewModel? _worldDialog;
    private CancellationTokenSource? _worldDialogCancellation;
    private NotificationItem? _selectedNotification;
    private NotificationDetailViewModel? _notificationDetail;
    private NotificationItem? _boopPreview;
    private readonly HashSet<string> _boopHydrationIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _groupHydrationIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? Name, string? IconUrl)> _groupMetadataCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _groupMetadataGate = new(3);
    private CancellationTokenSource? _notificationDetailCancellation;
    private readonly HashSet<string> _resolvedFriendRequestIds = new(StringComparer.Ordinal);
    private HashSet<string> _clearedNotificationIds = new(StringComparer.Ordinal);
    private string _notificationsAccountId = string.Empty;
    private bool _isInviteComposerOpen;
    private string _composerError = string.Empty;
    private FriendItem? _inviteComposerFriend;
    private NotificationItem? _inviteComposerRequestNotification;
    private string _inviteComposerMessage = string.Empty;
    private string _inviteComposerMode = "invite";
    private InviteMessageOption? _selectedInviteMessage;
    private bool _suppressInviteModeLoad;
    private bool _isInviteMessagesLoading;
    private string _inviteMessagesStatus = string.Empty;
    private int _inviteMessagesLoadVersion;
    private bool _inviteMessagesLoaded;
    private string _inviteMessagesLoadedType = string.Empty;
    private bool _isResponseComposerOpen;
    private NotificationItem? _responseComposerNotification;
    private string _responseComposerMessage = string.Empty;
    private FilterOption _selectedFriendFilter = null!;
    private FilterOption _selectedNotificationFilter = null!;
    private string _notificationSearch = string.Empty;
    private string _notificationFilterSummary = string.Empty;
    private bool _hasNoVisibleNotifications;
    private string _notificationsEmptyMessage = "You're all caught up.";
    private FilterOption _selectedBackdropStyle = null!;
    private FilterOption _selectedFriendChangeFilter = null!;
    private bool _isFriendLocationsOpen;
    private bool _isFriendChangesOpen;
    private bool _loadingFriendLocations;
    private string _playerSearchQuery = string.Empty;
    private string _playerSearchSummary = string.Empty;
    private bool _isPlayerSearchOpen;
    private bool _isPlayerSearchBusy;

    // --------------------------------------------------------- new features

    private AvatarViewModel? _avatarViewModel;
    private GroupViewModel? _groupViewModel;
    private ActivityFeedViewModel? _activityFeedViewModel;
    private InstanceViewModel? _instanceViewModel;
    private InstanceHistoryViewModel? _instanceHistoryViewModel;
    private RecentPlayersViewModel? _recentPlayersViewModel;
    private ContinuityViewModel? _continuityViewModel;
    private ContextViewModel? _contextViewModel;
    private bool _contextLiveRefreshRunning;
    private bool _contextLiveRefreshPending;
    private string _contextLogSignature = string.Empty;
    private InstanceHistoryRecorder? _instanceHistoryRecorder;
    private DispatcherTimer? _instanceHistoryTimer;
    private DispatcherTimer? _activityPollTimer;
    private bool _activityPollBusy;
    private DispatcherTimer? _bioPollTimer;
    private bool _bioPollBusy;
    private DateTimeOffset _bioPollPausedUntil = DateTimeOffset.MinValue;
    private readonly HashSet<string> _bioRefreshInFlight = new(StringComparer.Ordinal);
    private bool _currentUserRefreshBusy;
    private DateTimeOffset _lastSelfActivityRefreshAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastLocalAvatarSwitchAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, string> _lastLoggedAvatars = new(StringComparer.Ordinal);
    private string _lastLoggedAvatarLocation = string.Empty;
    private string _historyAccountId = string.Empty;
    private int _profileZIndex = 30;
    private bool _historyReady;
    private bool _realtimeFriendsBusy;
    private bool _realtimeFriendsRequested;
    private bool _realtimeNotificationsBusy;
    private bool _realtimeNotificationsRequested;
    private MultiAccountViewModel? _multiAccountViewModel;


    public AvatarViewModel? AvatarViewModel
    {
        get => _avatarViewModel;
        private set
        {
            if (ReferenceEquals(_avatarViewModel, value))
            {
                return;
            }

            if (_avatarViewModel is not null)
            {
                _avatarViewModel.AvatarWorn -= OnAvatarWorn;
            }

            if (SetProperty(ref _avatarViewModel, value) && value is not null)
            {
                value.AvatarWorn += OnAvatarWorn;
            }
        }
    }

    public MediaLibraryViewModel MediaViewModel { get; private set; }

    public ChatboxViewModel ChatboxViewModel { get; }

    public CreatorResourcesViewModel CreatorResourcesViewModel { get; }

    public BraveBrowserViewModel BraveBrowserViewModel { get; }

    public DiscordBrowserViewModel DiscordBrowserViewModel { get; }

    public GroupViewModel? GroupViewModel
    {
        get => _groupViewModel;
        private set
        {
            if (ReferenceEquals(_groupViewModel, value))
            {
                return;
            }

            if (_groupViewModel is not null)
            {
                _groupViewModel.MembershipChanged -= OnGroupMembershipChanged;
            }

            if (SetProperty(ref _groupViewModel, value) && value is not null)
            {
                value.MembershipChanged += OnGroupMembershipChanged;
            }
        }
    }

    private void OnGroupMembershipChanged(Group group, bool isMember)
        => SelectedFriendDetail?.ApplyGroupMembership(group, isMember);

    private void OnAvatarSearchResultsChanged()
        => SelectedFriendDetail?.RefreshAvatarLookupFromSearch();

    public ActivityFeedViewModel? ActivityFeedViewModel
    {
        get => _activityFeedViewModel;
        private set => SetProperty(ref _activityFeedViewModel, value);
    }

    public InstanceViewModel? InstanceViewModel
    {
        get => _instanceViewModel;
        private set => SetProperty(ref _instanceViewModel, value);
    }

    public InstanceHistoryViewModel? InstanceHistoryViewModel
    {
        get => _instanceHistoryViewModel;
        private set => SetProperty(ref _instanceHistoryViewModel, value);
    }

    public RecentPlayersViewModel? RecentPlayersViewModel
    {
        get => _recentPlayersViewModel;
        private set => SetProperty(ref _recentPlayersViewModel, value);
    }

    public ContinuityViewModel? ContinuityViewModel
    {
        get => _continuityViewModel;
        private set => SetProperty(ref _continuityViewModel, value);
    }

    public ContextViewModel? ContextViewModel
    {
        get => _contextViewModel;
        private set => SetProperty(ref _contextViewModel, value);
    }

    public int ProfileZIndex
    {
        get => _profileZIndex;
        private set
        {
            if (SetProperty(ref _profileZIndex, value))
            {
                OnPropertyChanged(nameof(ProfilePopupZIndex));
            }
        }
    }

    /// <summary>The group overlays must stay above whichever profile layer opened them.</summary>
    public int ProfilePopupZIndex => ProfileZIndex + 1;

    /// <summary>The in-app updater. Silent unless a release is actually available.</summary>
    public UpdateViewModel UpdateViewModel { get; }

    public MultiAccountViewModel? MultiAccountViewModel
    {
        get => _multiAccountViewModel;
        private set => SetProperty(ref _multiAccountViewModel, value);
    }

    public MainViewModel()
        : this(new VrcApiClient())
    {
    }

    internal MainViewModel(MediaLibraryViewModel mediaViewModel)
        : this(new VrcApiClient(), mediaViewModel)
    {
    }

    internal MainViewModel(VrcApiClient api)
        : this(api, new MediaLibraryViewModel())
    {
    }

    internal MainViewModel(ChatboxViewModel chatboxViewModel)
        : this(new VrcApiClient(), new MediaLibraryViewModel(), chatboxViewModel)
    {
    }

    internal MainViewModel(VrcApiClient api, MediaLibraryViewModel mediaViewModel)
        : this(api, mediaViewModel, null)
    {
    }

    internal MainViewModel(
        VrcApiClient api,
        MediaLibraryViewModel mediaViewModel,
        ChatboxViewModel? chatboxViewModel)
    {
        _api = api;
        _friendChanges = new FriendChangeCoalescer(api, PublishFriendChangesAsync);
        _friendChanges.SnapshotAccepted += (accountId, friends) =>
            FriendSnapshotStore.Shared.Save(accountId, friends);
        _images = ImageLoader.Shared;
        CosmeticCatalog.Changed += OnCosmeticCatalogChanged;
        _logPlayerService = new VrChatLogPlayerService();
        _realtime = new VrChatRealtimeService(_api);
        _realtime.EventReceived += OnRealtimeEvent;
        _realtime.StatusChanged += OnRealtimeStatusChanged;
        _instanceHistoryStore = new InstanceHistoryStore();
        _notificationStore = new NotificationPersistenceStore();
        _settingsStore = SettingsStore.Shared;
        _settings = _settingsStore.Settings;
        _favoriteFriends = new HashSet<string>(_settings.FavoriteFriends, StringComparer.Ordinal);
        _favoriteWorlds = new HashSet<string>(_settings.FavoriteWorlds, StringComparer.Ordinal);
        LaunchProfiles = new ObservableCollection<LaunchProfileItem>();
        LoadLaunchProfiles();

        _notificationsNav = new NavItem("notifications", "Notifications", "\uEA8F");
        _activityNav = new NavItem("activity", "Activity", "\uE790");
        _friendsNav = new NavItem("friends", "Friends", "\uE716") { IsVisible = false };
        _instanceNav = new NavItem("instance", "Instance", "\uE7F4");
        Sections = new ObservableCollection<NavItem>
        {
            new("profile", "Profile", "\uE77B"),
            _activityNav,
            _friendsNav,
            new("continuity", "Continuity", "\uE81C"),
            new("context", "Context", "\uE8A7"),
            _notificationsNav,
            new("worlds", "Worlds", "\uE909"),
            new("avatars", "Uploaded Avatars", "\uE7FD"),
            new("favorites", "Favourites", "\uE735"),
            new("avatarsearch", "Avatar Search", "\uE721"),
            new("groups", "Groups", "\uE70E"),
            new("resources", "Creator Resources", "\uE719"),
            new("brave", "Brave Browser", "\uE774"),
            new("discord", "Discord", "\uE8BD"),
            new("chatbox", "KawaiiChatbox", "\uE8BD"),
            new("media", "Media", "\uEB9F"),
            new("accounts", "Accounts", "\uE716"),
            new("settings", "Settings", "\uE713")
        };
        _selectedSection = Sections[0];
        MediaViewModel = mediaViewModel;
        UpdateViewModel = new UpdateViewModel();
        ChatboxViewModel = chatboxViewModel ?? new ChatboxViewModel();
        CreatorResourcesViewModel = new CreatorResourcesViewModel();
        BraveBrowserViewModel = new BraveBrowserViewModel();
        DiscordBrowserViewModel = new DiscordBrowserViewModel();
        ChatboxViewModel.DiscordVoiceStateProvider = DiscordBrowserViewModel.ReadVoiceStateAsync;
        CreatorResourcesViewModel.PropertyChanged += OnBrowserFocusChanged;
        BraveBrowserViewModel.PropertyChanged += OnBrowserFocusChanged;
        DiscordBrowserViewModel.PropertyChanged += OnBrowserFocusChanged;

        Statuses = new ObservableCollection<StatusOption>
        {
            new("active", "Online", UserPresence.OnlineColor),
            new("join me", "Join Me", UserPresence.JoinMeColor),
            new("ask me", "Ask Me", UserPresence.AskMeColor),
            new("busy", "Busy", UserPresence.BusyColor),
            new("offline", "Offline", UserPresence.OfflineColor)
        };
        _selectedStatus = Statuses[0];

        Friends = new RangeObservableCollection<FriendItem>();
        _friendsView = CollectionViewSource.GetDefaultView(Friends);
        _friendsView.Filter = FilterFriend;
        if (_friendsView is ListCollectionView friendListView)
        {
            friendListView.CustomSort = new FriendComparer();
        }

        _friendLocationsView = new ListCollectionView(Friends)
        {
            Filter = FilterFriendLocation,
            CustomSort = new FriendComparer()
        };

        FriendChanges = new RangeObservableCollection<FriendChangeItem>();
        _friendChangesView = CollectionViewSource.GetDefaultView(FriendChanges);
        _friendChangesView.Filter = FilterFriendChange;
        FriendChangeFilters = new ObservableCollection<FilterOption>
        {
            new("all", "All changes"),
            new("friended", "Friended"),
            new("unfriended", "Unfriended")
        };
        _selectedFriendChangeFilter = FriendChangeFilters[0];

        FriendFilters = new ObservableCollection<FilterOption>
        {
            new("all", "All"),
            new("online", "Online"),
            new("active", "Active"),
            new("joinme", "Join Me"),
            new("offline", "Offline")
        };
        _selectedFriendFilter = FriendFilters[0];

        InviteComposerModes = new ObservableCollection<FilterOption>
        {
            new("invite", "Invite them to my world"),
            new("request", "Request their invite")
        };
        InviteMessages = new ObservableCollection<InviteMessageOption>();

        Notifications = new ObservableCollection<NotificationItem>();
        _notificationsView = CollectionViewSource.GetDefaultView(Notifications);
        _notificationsView.Filter = FilterNotification;
        NotificationFilters = new ObservableCollection<FilterOption>
        {
            new("all", "All"),
            new("unread", "Unread"),
            new("friend-requests", "Friend requests"),
            new("world-invites", "World invites"),
            new("invite-requests", "Invite requests"),
            new("groups", "Groups"),
            new("boops", "Boops"),
            new("other", "Other")
        };
        _selectedNotificationFilter = NotificationFilters[0];

        Worlds = new ObservableCollection<WorldItem>();
        PlayerSearchResults = new ObservableCollection<FriendItem>();

        BackdropStyles = new ObservableCollection<FilterOption>
        {
            new("Aurora", "Aurora"),
            new("Midnight", "Midnight"),
            new("Ember", "Ember"),
            new("Sakura", "Sakura")
        };
        _selectedBackdropStyle = BackdropStyles.FirstOrDefault(style =>
                                    string.Equals(style.Key, _settings.BackdropStyle, StringComparison.OrdinalIgnoreCase))
                                ?? BackdropStyles[0];

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _clockTimer.Tick += (_, _) => OnPropertyChanged(nameof(ClockText));
        _clockTimer.Start();

        ClearErrorCommand = Track(new RelayCommand(ClearError));
        RefreshCommand = Track(new AsyncRelayCommand(RefreshAsync, () => IsLoggedIn && !IsBusy));
        LogoutCommand = Track(new AsyncRelayCommand(LogoutAsync, () => IsLoggedIn && !IsBusy));
        VerifyTwoFactorCommand = Track(new AsyncRelayCommand(
            VerifyTwoFactorAsync,
            () => IsTwoFactorRequired && !IsBusy && !string.IsNullOrWhiteSpace(TwoFactorCode)));
        CancelTwoFactorCommand = Track(new AsyncRelayCommand(CancelTwoFactorAsync, () => IsTwoFactorRequired && !IsBusy));
        UseEmailCodeCommand = Track(new RelayCommand(
            () => SetTwoFactorMethod(TwoFactorMethod.Email),
            () => ShowEmailSwitchLink));
        UseTotpCodeCommand = Track(new RelayCommand(
            () => SetTwoFactorMethod(TwoFactorMethod.Authenticator),
            () => ShowTotpSwitchLink));
        UseRecoveryCodeCommand = Track(new RelayCommand(
            () => SetTwoFactorMethod(TwoFactorMethod.Recovery),
            () => ShowRecoverySwitchLink));
        SaveStatusCommand = Track(new AsyncRelayCommand(
            SaveStatusAsync,
            () => IsLoggedIn && !IsBusy && CurrentUser is not null && SelectedStatus is not null && IsStatusDirty));
        MarkAllReadCommand = Track(new AsyncRelayCommand(
            MarkAllReadAsync,
            () => IsLoggedIn && !IsBusy && UnreadNotificationCount > 0));
        MarkNotificationSeenCommand = Track(new AsyncRelayCommand<NotificationItem>(
            MarkNotificationSeenAsync,
            item => item is { Seen: false } && IsLoggedIn && !IsBusy));
        OpenBoopPreviewCommand = Track(new RelayCommand<NotificationItem>(
            OpenBoopPreview,
            item => item is { HasBoopImage: true }));
        OpenNotificationGroupCommand = Track(new RelayCommand<NotificationItem>(
            OpenNotificationGroup,
            item => item is { HasGroupId: true } && IsLoggedIn && GroupViewModel is not null));
        AcceptGroupInviteCommand = Track(new AsyncRelayCommand<NotificationItem>(
            AcceptGroupInviteAsync,
            item => item is { CanAcceptGroupInvite: true } && IsLoggedIn && !IsBusy));
        DeclineGroupInviteCommand = Track(new AsyncRelayCommand<NotificationItem>(
            DeclineGroupInviteAsync,
            item => item is { CanDeclineGroupInvite: true } && IsLoggedIn && !IsBusy));
        CloseBoopPreviewCommand = Track(new RelayCommand(CloseBoopPreview));
        JoinNotificationUserCommand = Track(new AsyncRelayCommand<NotificationDetailViewModel>(
            JoinNotificationUserAsync,
            detail => detail is not null && detail.CanJoinUser && !IsBusy));
        SearchWorldsCommand = Track(new AsyncRelayCommand(
            () => SearchWorldsAsync(WorldQuery),
            () => IsLoggedIn && !IsBusy));
        SearchSuggestionCommand = Track(new AsyncRelayCommand<string>(
            SearchSuggestionAsync,
            _ => IsLoggedIn && !IsBusy));
        OpenWorldCommand = Track(new RelayCommand<WorldItem>(OpenWorld, world => world is not null));
        CloseFriendDetailCommand = Track(new RelayCommand(ClearFriendSelection));
        OpenOwnProfileCommand = Track(new RelayCommand(OpenOwnProfile, () => CurrentUser is not null));
        ChooseLaunchModeCommand = Track(new RelayCommand<string>(
            ChooseLaunchMode,
            key => IsLaunchChooserOpen && !string.IsNullOrWhiteSpace(key)));
        CancelLaunchChoiceCommand = Track(new RelayCommand(CloseLaunchChoice));
        ShowSenderProfileCommand = Track(new AsyncRelayCommand<NotificationDetailViewModel>(
            ShowSenderProfileAsync,
            detail => detail is not null && !IsBusy));
        InjectSenderLocationCommand = Track(new AsyncRelayCommand(VerifySenderWorldAsync, () => IsDevMode));
        ToggleDevToolsCommand = Track(new RelayCommand(ToggleDevTools));
        LoadDevToolsSetting();
        SetLaunchModeCommand = Track(new RelayCommand<string>(SetDefaultLaunchMode));
        OpenVrChatCommand = Track(new RelayCommand(OpenVrChat));
        RejoinWorldCommand = Track(new AsyncRelayCommand(
            RejoinLastWorldAsync,
            () => IsLoggedIn && !IsBusy));
        OpenAdvancedLaunchCommand = Track(new RelayCommand(OpenAdvancedLaunch));
        NewLaunchProfileCommand = Track(new RelayCommand(NewLaunchProfile));
        DuplicateLaunchProfileCommand = Track(new RelayCommand(
            DuplicateLaunchProfile,
            () => SelectedLaunchProfile is not null));
        DeleteLaunchProfileCommand = Track(new RelayCommand(
            DeleteLaunchProfile,
            () => CanDeleteLaunchProfile));
        SaveLaunchProfileCommand = Track(new RelayCommand(
            () => SaveSelectedLaunchProfile(),
            () => CanSaveLaunchProfile));
        UseLaunchProfileCommand = Track(new RelayCommand(UseSelectedLaunchProfile));
        CancelAdvancedLaunchCommand = Track(new RelayCommand(CloseAdvancedLaunch));
        SelfInviteLaunchCommand = Track(new AsyncRelayCommand(
            SelfInvitePendingLaunchAsync,
            () => IsLaunchChooserOpen && IsLoggedIn && !IsBusy && _pendingLaunchLocation is not null));
        OpenInstanceHistoryCommand = Track(new RelayCommand(
            OpenInstanceHistory,
            () => IsLoggedIn && InstanceHistoryViewModel is not null));
        OpenRecentPlayersCommand = Track(new AsyncRelayCommand(
            OpenRecentPlayersAsync,
            () => IsLoggedIn && RecentPlayersViewModel is not null && !IsBusy));
        CloseRecentPlayersCommand = Track(new RelayCommand(
            () => RecentPlayersViewModel?.Close()));
        RefreshFriendProfileCommand = Track(new AsyncRelayCommand<FriendDetailViewModel>(
            RefreshFriendProfileAsync,
            _ => !IsBusy));
        ToggleFriendCommand = Track(new AsyncRelayCommand<FriendDetailViewModel>(
            ToggleFriendAsync,
            detail => detail is { CanToggleFriendship: true } && IsLoggedIn && !IsBusy));
        JoinFriendCommand = Track(new AsyncRelayCommand<FriendItem>(
            JoinFriendAsync,
            friend => friend is not null && friend.IsOnline && LaunchService.IsLocation(friend.Location) && !IsBusy));
        SelfInviteFriendCommand = Track(new AsyncRelayCommand<FriendItem>(
            SelfInviteFriendAsync,
            friend => friend is not null && LaunchService.IsLocation(friend.Location) && IsLoggedIn && !IsBusy));
        InviteFriendCommand = Track(new RelayCommand<FriendItem>(
            friend => OpenInviteComposer(friend),
            friend => friend is not null && IsLoggedIn && !IsBusy));
        RequestInviteFriendCommand = Track(new RelayCommand<FriendItem>(
            friend => OpenInviteComposer(friend),
            friend => friend is not null && IsLoggedIn && !IsBusy));
        SelectInviteComposerModeCommand = Track(new RelayCommand<string>(
            mode => InviteComposerMode = mode));
        SaveInvitePresetCommand = Track(new AsyncRelayCommand(
            SaveInvitePresetAsync,
            () => CanSaveInvitePreset));
        ClearInvitePresetCommand = Track(new RelayCommand(
            () => SelectedInviteMessage = null));
        SendInviteChoiceCommand = Track(new AsyncRelayCommand<string>(
            SendInviteChoiceAsync,
            mode => _inviteComposerFriend is not null && !IsBusy &&
                    (string.Equals(mode, "invite", StringComparison.Ordinal) ||
                     string.Equals(mode, "request", StringComparison.Ordinal))));
        CancelInviteComposerCommand = Track(new RelayCommand(CloseInviteComposer));
        AcceptInviteRequestCommand = Track(new AsyncRelayCommand<NotificationItem>(
            OpenAcceptInviteRequestAsync,
            item => item is { CanAcceptInviteRequest: true } && !IsBusy));
        DeclineNotificationCommand = Track(new AsyncRelayCommand<NotificationItem>(
            DeclineNotificationAsync,
            item => item is { CanDecline: true } && !IsBusy));
        DeclineNotificationWithMessageCommand = Track(new RelayCommand<NotificationItem>(
            OpenResponseComposer,
            item => item is { CanDecline: true } && !IsBusy));
        SendDeclineWithMessageCommand = Track(new AsyncRelayCommand(
            SendDeclineWithMessageAsync,
            () => _responseComposerNotification is not null &&
                  !IsBusy &&
                  !string.IsNullOrWhiteSpace(ResponseComposerMessage)));
        CancelResponseComposerCommand = Track(new RelayCommand(CloseResponseComposer));
        OpenFriendProfileCommand = Track(new RelayCommand<FriendItem>(
            OpenFriendProfile,
            friend => friend is not null));
        CopyFriendLocationCommand = Track(new RelayCommand<FriendItem>(
            CopyFriendLocation,
            friend => friend is not null && LaunchService.IsLocation(friend.Location)));
        CopyFriendUserIdCommand = Track(new RelayCommand<FriendItem>(
            CopyFriendUserId,
            friend => !string.IsNullOrWhiteSpace(friend?.Id)));
        CopyFriendProfileLinkCommand = Track(new RelayCommand<FriendItem>(
            CopyFriendProfileLink,
            friend => !string.IsNullOrWhiteSpace(friend?.Id)));
        OpenFriendWorldCommand = Track(new AsyncRelayCommand<FriendItem>(
            OpenFriendWorldAsync,
            friend => friend is not null && LaunchService.GetWorldIdFromLocation(friend.Location) is not null && !IsBusy));
        OpenFriendLocationsCommand = Track(new AsyncRelayCommand(
            OpenFriendLocationsAsync,
            () => IsLoggedIn && !IsBusy));
        OpenFriendsCommand = Track(new RelayCommand(OpenFriends, () => IsLoggedIn));
        OpenInstanceCommand = Track(new RelayCommand(OpenInstance, () => IsLoggedIn));
        OpenInstancePlayerCommand = Track(new AsyncRelayCommand<InstancePlayerItem>(
            OpenInstancePlayerAsync,
            player => player is { CanOpenProfile: true } && IsLoggedIn && !IsBusy));
        CloseFriendLocationsCommand = Track(new RelayCommand(() => IsFriendLocationsOpen = false));
        OpenFriendLocationCommand = Track(new RelayCommand<FriendItem>(
            OpenFriendLocation,
            friend => friend is not null));
        OpenFriendChangesCommand = Track(new RelayCommand(() => IsFriendChangesOpen = true));
        CloseFriendChangesCommand = Track(new RelayCommand(() => IsFriendChangesOpen = false));
        OpenFriendChangeCommand = Track(new RelayCommand<FriendChangeItem>(
            OpenFriendChange,
            change => change is not null));
        OpenActivityAvatarCommand = Track(new AsyncRelayCommand<ActivityEntry>(
            OpenActivityAvatarAsync,
            entry => entry is not null && entry.CanOpenAvatar && !IsBusy));
        OpenActivityUserCommand = Track(new AsyncRelayCommand<ActivityEntry>(
            OpenActivityUserAsync,
            entry => entry is not null && !string.IsNullOrWhiteSpace(entry.UserId) && IsLoggedIn && !IsBusy));
        CloseWorldDialogCommand = Track(new RelayCommand(CloseWorldDialog));
        RefreshWorldDialogCommand = Track(new AsyncRelayCommand(
            RefreshWorldDialogAsync,
            () => WorldDialog is not null && !WorldDialog.IsLoading && !IsBusy));
        CreateInstanceCommand = Track(new AsyncRelayCommand(
            CreateInstanceAsync,
            () => WorldDialog is { IsCreating: false } && !IsBusy));
        LaunchCreatedInstanceCommand = Track(new RelayCommand(
            LaunchCreatedInstance,
            () => WorldDialog?.CreatedInstance is not null));
        CopyCreatedInstanceLinkCommand = Track(new RelayCommand(
            CopyCreatedInstanceLink,
            () => !string.IsNullOrWhiteSpace(WorldDialog?.CreatedShareLink)));
        OpenWorldInBrowserCommand = Track(new RelayCommand<WorldItem>(
            OpenWorldInBrowser,
            world => world is not null));
        JoinWorldInstanceCommand = Track(new RelayCommand<WorldInstanceItem>(
            JoinWorldInstance,
            instance => instance is not null && instance.CanJoin));
        OpenWorldInstanceGroupCommand = Track(new RelayCommand<WorldInstanceItem>(
            OpenWorldInstanceGroup,
            instance => instance is not null && instance.HasGroup));
        QuickJoinCommand = Track(new AsyncRelayCommand(
            QuickJoinAsync,
            () => IsLoggedIn && !IsBusy && !string.IsNullOrWhiteSpace(QuickJoinInput)));
        AcceptFriendRequestCommand = Track(new AsyncRelayCommand<NotificationItem>(
            AcceptFriendRequestAsync,
            item => item is not null && item.CanAcceptFriendRequest && !IsBusy));
        JoinInviteCommand = Track(new AsyncRelayCommand<NotificationItem>(
            JoinInviteAsync,
            item => item is not null && item.CanJoinInvite && !IsBusy));
        JoinDialogInputCommand = Track(new AsyncRelayCommand(JoinDialogInputAsync));
        DismissInfoCommand = Track(new RelayCommand(ClearInfo));

        ToggleFavoriteFriendCommand = Track(new RelayCommand<FriendItem>(
            ToggleFavoriteFriend,
            friend => friend is not null));
        ToggleFavoriteWorldCommand = Track(new RelayCommand<WorldItem>(
            ToggleFavoriteWorld,
            world => world is not null));
        OpenNotificationProfileCommand = Track(new RelayCommand<NotificationDetailViewModel>(
            OpenNotificationProfile,
            detail => detail is not null));
        CopyNotificationLocationCommand = Track(new RelayCommand<NotificationDetailViewModel>(
            CopyNotificationLocation,
            detail => detail is not null && LaunchService.IsLocation(detail.InviteLocation)));
        OpenNotificationWorldCommand = Track(new AsyncRelayCommand(
            OpenNotificationWorldAsync,
            () => NotificationDetail is { } detail && detail.InviteLocation.Length > 0 && !IsBusy));
        RefreshNotificationDetailCommand = Track(new AsyncRelayCommand(
            RefreshNotificationDetailAsync,
            () => NotificationDetail is not null && !IsBusy));
        ClearNotificationSelectionCommand = Track(new RelayCommand(ClearNotificationSelection));
        ChooseBackdropCommand = Track(new RelayCommand(ChooseBackdrop));
        ClearBackdropCommand = Track(new RelayCommand(ClearBackdrop, () => HasCustomBackdrop));
        ChooseProfileBackdropCommand = Track(new RelayCommand(ChooseProfileBackdrop));
        ClearProfileBackdropCommand = Track(new RelayCommand(ClearProfileBackdrop, () => HasProfileBackdrop));
        DownloadBackdropFromUrlCommand = Track(new AsyncRelayCommand(
            DownloadBackdropFromUrlAsync,
            () => !string.IsNullOrWhiteSpace(BackdropUrlInput) && !IsBusy));

        // New feature commands (these run on the UI thread like the rest of the app)
        LoadAvatarsCommand = Track(new AsyncRelayCommand(
            () => AvatarViewModel?.LoadAsync() ?? Task.CompletedTask,
            () => AvatarViewModel is { IsBusy: false }));
        LoadGroupsCommand = Track(new AsyncRelayCommand(
            () => GroupViewModel?.LoadAsync() ?? Task.CompletedTask,
            () => GroupViewModel is { IsBusy: false }));
        LoadActivityFeedCommand = Track(new AsyncRelayCommand(
            () => ActivityFeedViewModel?.LoadAsync() ?? Task.CompletedTask,
            () => ActivityFeedViewModel is { IsBusy: false }));
        RefreshInstanceCommand = Track(new AsyncRelayCommand(
            LoadInstanceAsync,
            () => IsLoggedIn && InstanceViewModel is { IsLoading: false }));
        CopyInstanceLinkCommand = Track(new RelayCommand(
            CopyInstanceLink,
            () => !string.IsNullOrWhiteSpace(InstanceViewModel?.ShareLink)));
        CloseInstanceCommand = Track(new RelayCommand(CloseInstance));
        PlayerSearchCommand = Track(new AsyncRelayCommand(
            SearchPlayersAsync,
            () => IsLoggedIn && !IsPlayerSearchBusy && !string.IsNullOrWhiteSpace(PlayerSearchQuery)));
        OpenPlayerSearchResultCommand = Track(new AsyncRelayCommand<FriendItem>(
            OpenPlayerSearchResultAsync,
            result => result is not null && IsLoggedIn && !IsBusy));
        ClearPlayerSearchCommand = Track(new RelayCommand(ClearPlayerSearch));
    }

    // ------------------------------------------------------------ shell state

    public ObservableCollection<NavItem> Sections { get; }

    public bool IsAnyBrowserFocused => SelectedSection.Key switch
    {
        "brave" => BraveBrowserViewModel.IsFocusMode,
        "discord" => DiscordBrowserViewModel.IsFocusMode,
        "resources" => CreatorResourcesViewModel.IsFocusMode,
        _ => false
    };

    /// <summary>
    /// Applies to all three embedded browsers. It is deliberately one setting
    /// instead of three, so switching it in any browser keeps the others honest.
    /// </summary>
    public bool BrowserAdBlockEnabled
    {
        get => _settings.BrowserAdBlockEnabled;
        set
        {
            if (_settings.BrowserAdBlockEnabled == value)
            {
                return;
            }

            _settings.BrowserAdBlockEnabled = value;
            _settingsStore.Save();
            OnPropertyChanged();
        }
    }

    public bool ExitBrowserFocus()
    {
        if (!IsAnyBrowserFocused)
        {
            return false;
        }

        switch (SelectedSection.Key)
        {
            case "brave":
                BraveBrowserViewModel.IsFocusMode = false;
                return true;
            case "discord":
                DiscordBrowserViewModel.IsFocusMode = false;
                return true;
            case "resources":
                CreatorResourcesViewModel.IsFocusMode = false;
                return true;
            default:
                return false;
        }
    }

    public NavItem SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is null)
            {
                return;
            }

            var previousSectionKey = _selectedSection.Key;
            if (value.Key == "instance" && _selectedSection.Key != "instance")
            {
                _instanceReturnNav = _selectedSection;
            }

            if (!SetProperty(ref _selectedSection, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SectionTitle));
            OnPropertyChanged(nameof(SectionSubtitle));
            OnPropertyChanged(nameof(IsAnyBrowserFocused));

            // Log polling is only useful while the popup is on screen.
            InstanceViewModel?.SetActive(value.Key == "instance");

            // Automatic browse retries only run while their tab is on screen.
            if (AvatarViewModel is not null)
            {
                AvatarViewModel.IsBrowseTabActive = value.Key == "avatarsearch";
            }

            if (previousSectionKey == "avatarsearch" && value.Key != "avatarsearch")
            {
                SelectedFriendDetail?.RefreshAvatarLookupFromSearch();
            }

            // Media and Continuity are local, so they load even before a
            // session is restored. Continuity still filters by account once
            // a user is signed in.
            if (value.Key == "media")
            {
                _ = MediaViewModel.EnsureLoadedAsync();
            }

            if (value.Key == "continuity")
            {
                _ = ContinuityViewModel?.EnsureLoadedAsync();
            }

            if (value.Key == "context")
            {
                _ = ContextViewModel?.EnsureLoadedAsync();
            }

            if (!IsLoggedIn)
            {
                return;
            }

            // Sections load themselves on first visit instead of needing Refresh.
            switch (value.Key)
            {
                case "activity" when ActivityFeedViewModel is { IsBusy: false } &&
                                      DateTimeOffset.UtcNow - _lastFullFriendLoadAt > TimeSpan.FromMinutes(1):
                    // The Activity page needs the complete friend list, not just
                    // the online-only background snapshot, or offline friends'
                    // avatar changes never reach the timeline.
                    _ = ActivityFeedViewModel.LoadAsync();
                    break;
                case "worlds":
                    _ = EnsureWorldsLoadedAsync();
                    break;
                case "instance":
                    _ = LoadInstanceAsync();
                    break;
                case "avatars":
                    _ = AvatarViewModel?.EnsureLoadedAsync();
                    break;
                case "favorites":
                    // Favourites have their own request so this tab fills in even
                    // when it is opened while the avatar library is still loading.
                    _ = AvatarViewModel?.EnsureFavoritesAsync();
                    break;
                case "avatarsearch":
                    _ = AvatarViewModel?.EnsureLoadedAsync();
                    AvatarViewModel?.BrowseCatalog();
                    break;
                case "groups" when GroupViewModel is { HasLoaded: false, IsBusy: false }:
                    _ = GroupViewModel.LoadAsync();
                    break;
            }
        }
    }

    public string SectionTitle => SelectedSection.Title;

    public string SectionSubtitle => SelectedSection.Key switch
    {
        "friends" => "Who's online, what they're up to, and where they are.",
        "instance" => "The world you're in, its details, and everyone VRChat shows.",
        "notifications" => "Invites, friend requests, and other VRChat activity.",
        "worlds" => "Search the public VRChat world catalog.",
        "avatars" => "Your uploaded VRChat avatars, with the same sort options as the game.",
        "favorites" => "The avatars you have favourited in VRChat, slot by slot.",
        "avatarsearch" => "Search every avatar VRChat knows about and wear or save it.",
        "groups" => "Manage your VRChat groups, roles, and members.",
        "resources" => "Open external creator marketplaces and official VRChat tools.",
        "brave" => "Search the web and open Spotify, YouTube, or any site in the app.",
        "discord" => "Sign in to Discord and search friends, servers, or settings.",
        "activity" => "See what your friends have been up to.",
        "continuity" => "A durable local memory of the people, worlds, and sessions from VRCX and your own logs.",
        "context" => "What is true right now, with a read-only local snapshot for other tools.",
        "chatbox" => "KawaiiChatbox · send and rotate VRChat chatbox messages over OSC.",
        "media" => "Your in-game pictures, grouped by world and month.",
        "accounts" => "Switch between multiple VRChat accounts.",
        _ => "Your account, status, and trust level at a glance."
    };

    private bool _isLoggedIn;
    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        private set
        {
            if (SetProperty(ref _isLoggedIn, value))
            {
                RaiseCommandStates();
                OnPropertyChanged(nameof(IsCreatorAccount));
                OnPropertyChanged(nameof(IsDevMode));
            }
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                RaiseCommandStates();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    private string _busyText = string.Empty;
    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public string LastUpdatedText => _lastUpdated is null
        ? "Not refreshed yet"
        : $"Updated {_lastUpdated.Value:h:mm tt}";

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    private string? _infoMessage;
    public string? InfoMessage
    {
        get => _infoMessage;
        private set => SetProperty(ref _infoMessage, value);
    }

    public bool HasInfo => !string.IsNullOrWhiteSpace(InfoMessage);

    // ----------------------------------------------------------- login fields

    private string _username = string.Empty;
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    private bool _rememberSession = true;
    public bool RememberSession
    {
        get => _rememberSession;
        set => SetProperty(ref _rememberSession, value);
    }

    private bool _isTwoFactorRequired;
    public bool IsTwoFactorRequired
    {
        get => _isTwoFactorRequired;
        private set
        {
            if (SetProperty(ref _isTwoFactorRequired, value))
            {
                OnPropertyChanged(nameof(TwoFactorHint));
                OnPropertyChanged(nameof(TwoFactorMethodLabel));
                OnPropertyChanged(nameof(ShowEmailSwitchLink));
                OnPropertyChanged(nameof(ShowTotpSwitchLink));
                OnPropertyChanged(nameof(ShowRecoverySwitchLink));
                RaiseCommandStates();
            }
        }
    }

    private string _twoFactorCode = string.Empty;
    public string TwoFactorCode
    {
        get => _twoFactorCode;
        set
        {
            if (SetProperty(ref _twoFactorCode, value))
            {
                VerifyTwoFactorCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TwoFactorHint => _twoFactorMethod switch
    {
        TwoFactorMethod.Email => "Enter the code VRChat sent to your email address.",
        TwoFactorMethod.Recovery => "Enter one of your VRChat two-factor recovery codes.",
        _ => "Enter the 6-digit code from your authenticator app."
    };

    public string TwoFactorMethodLabel => _twoFactorMethod switch
    {
        TwoFactorMethod.Email => "Email code",
        TwoFactorMethod.Recovery => "Recovery code",
        _ => "Authenticator app"
    };

    public bool ShowEmailSwitchLink
        => _twoFactorMethod != TwoFactorMethod.Email && HasEmailTwoFactor;

    public bool ShowTotpSwitchLink
        => _twoFactorMethod != TwoFactorMethod.Authenticator && HasTotpTwoFactor;

    public bool ShowRecoverySwitchLink
        => _twoFactorMethod != TwoFactorMethod.Recovery && HasRecoveryTwoFactor;

    private bool HasTotpTwoFactor => _twoFactorMethods.Contains("totp", StringComparer.OrdinalIgnoreCase);

    private bool HasEmailTwoFactor => _twoFactorMethods.Contains("emailOtp", StringComparer.OrdinalIgnoreCase);

    private bool HasRecoveryTwoFactor => _twoFactorMethods.Contains("otp", StringComparer.OrdinalIgnoreCase);

    // --------------------------------------------------------- current user

    private VrcUser? _currentUser;

    public VrcUser? CurrentUser
    {
        get => _currentUser;
        private set
        {
            if (ReferenceEquals(_currentUser, value))
            {
                return;
            }

            _currentUser = value;
            OnPropertyChanged(nameof(CurrentUser));
            SyncDevToolsAccount(value?.Id);
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(CurrentUser?.DisplayName)
        ? "VRChat user"
        : CurrentUser!.DisplayName;

    public string UsernameHandle => string.IsNullOrWhiteSpace(CurrentUser?.Username)
        ? string.Empty
        : $"@{CurrentUser!.Username}";

    public string AvatarInitial
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

    public ImageSource? AvatarImage
    {
        get => _avatarImage;
        private set => SetProperty(ref _avatarImage, value);
    }

    public string BioText => string.IsNullOrWhiteSpace(CurrentUser?.Bio)
        ? "No bio set."
        : CurrentUser!.Bio!.Trim();

    public string JoinedText
    {
        get
        {
            if (DateTime.TryParse(
                    CurrentUser?.DateJoined,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var joined))
            {
                return $"Member since {joined:MMM yyyy}";
            }

            return string.Empty;
        }
    }

    public string TrustLabel => UserPresentation.TrustLabel(CurrentUser?.Tags, CurrentUser?.DeveloperType);

    public Brush TrustBrush => UserPresentation.TrustBrush(CurrentUser?.Tags, CurrentUser?.DeveloperType);

    public string IconFrameUrl => UserPresentation.IconFrameUrl(CurrentUser?.IconFrame) ?? string.Empty;

    public string IconFrameIntroUrl => UserPresentation.IconFrameIntroUrl(CurrentUser?.IconFrame) ?? string.Empty;

    public bool HasIconFrame => IconFrameUrl.Length > 0;

    public string ProfileEffectUrl => UserPresentation.ProfileEffectUrl(CurrentUser?.ProfileEffect) ?? string.Empty;

    public string ProfileEffectIntroUrl => UserPresentation.ProfileEffectIntroUrl(CurrentUser?.ProfileEffect) ?? string.Empty;

    public bool HasProfileEffect => ProfileEffectUrl.Length > 0;

    private ProfileThemeColors CurrentProfileTheme => UserPresentation.Theme(
        CurrentUser?.ThemeIconColor,
        CurrentUser?.ThemeButtonColor,
        CurrentUser?.ThemeSubtextColor);

    public Brush ProfileNameBrush => CurrentProfileTheme.Icon;

    public Brush ProfileButtonBrush => CurrentProfileTheme.Button;

    public Brush ProfileSubtextBrush => CurrentProfileTheme.Subtext;

    public bool IsVrcPlus => CurrentUser?.Tags.Contains("system_supporter", StringComparer.OrdinalIgnoreCase) == true;

    public bool IsAgeVerified => CurrentUser?.AgeVerified == true;

    public bool HasPronouns => !string.IsNullOrWhiteSpace(CurrentUser?.Pronouns);

    public string PronounsText => CurrentUser?.Pronouns?.Trim() ?? string.Empty;

    public string ProfileStatusLabel => StatusLabel(CurrentUser?.Status);

    public string ProfileStatusDescription => CurrentUser?.StatusDescription?.Trim() ?? string.Empty;

    public bool HasProfileStatusDescription => !string.IsNullOrWhiteSpace(CurrentUser?.StatusDescription);

    public Brush ProfileStatusBrush
    {
        get
        {
            var status = (CurrentUser?.Status ?? string.Empty).ToLowerInvariant();
            return Statuses.FirstOrDefault(option =>
                       string.Equals(option.Value, status, StringComparison.OrdinalIgnoreCase))?.Brush
                   ?? Statuses[^1].Brush;
        }
    }

    public string TwoFactorText => CurrentUser?.TwoFactorAuthEnabled == true ? "Enabled" : "Disabled";

    public string EmailVerifiedText => CurrentUser?.EmailVerified == true ? "Verified" : "Not verified";

    public string AccountStandingText
    {
        get
        {
            var standing = CurrentUser?.AccountStanding;
            if (string.IsNullOrWhiteSpace(standing))
            {
                return "Unknown";
            }
            return char.ToUpperInvariant(standing[0]) + standing[1..];
        }
    }

    // ---------------------------------------------------------- status editor

    public ObservableCollection<StatusOption> Statuses { get; }

    public StatusOption SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (value is not null && SetProperty(ref _selectedStatus, value) && !_suppressStatusDirty)
            {
                SetStatusDirty(true);
            }
        }
    }

    public string StatusMessageInput
    {
        get => _statusMessageInput;
        set
        {
            if (SetProperty(ref _statusMessageInput, value ?? string.Empty) && !_suppressStatusDirty)
            {
                SetStatusDirty(true);
            }
        }
    }

    public bool IsStatusDirty
    {
        get => _isStatusDirty;
        private set
        {
            if (SetProperty(ref _isStatusDirty, value))
            {
                SaveStatusCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // --------------------------------------------------------------- friends

    public ObservableCollection<FriendItem> Friends { get; }

    public ObservableCollection<FriendItem> PlayerSearchResults { get; }

    public string PlayerSearchQuery
    {
        get => _playerSearchQuery;
        set
        {
            if (SetProperty(ref _playerSearchQuery, value))
            {
                PlayerSearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PlayerSearchSummary
    {
        get => _playerSearchSummary;
        private set => SetProperty(ref _playerSearchSummary, value);
    }

    public bool IsPlayerSearchOpen
    {
        get => _isPlayerSearchOpen;
        set => SetProperty(ref _isPlayerSearchOpen, value);
    }

    public bool IsPlayerSearchBusy
    {
        get => _isPlayerSearchBusy;
        private set
        {
            if (SetProperty(ref _isPlayerSearchBusy, value))
            {
                PlayerSearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ICollectionView FriendsView => _friendsView;

    public ICollectionView FriendLocationsView => _friendLocationsView;

    public bool IsFriendLocationsOpen
    {
        get => _isFriendLocationsOpen;
        private set => SetProperty(ref _isFriendLocationsOpen, value);
    }

    public string FriendLocationsSummary => _loadingFriendLocations
        ? "Loading world names…"
        : $"{_friendLocationsView.Cast<object>().Count()} friends in worlds";

    public bool HasFriendLocations => _friendLocationsView.Cast<object>().Any();

    public RangeObservableCollection<FriendChangeItem> FriendChanges { get; }

    public ICollectionView FriendChangesView => _friendChangesView;

    public ObservableCollection<FilterOption> FriendChangeFilters { get; }

    public FilterOption SelectedFriendChangeFilter
    {
        get => _selectedFriendChangeFilter;
        set
        {
            if (value is not null && SetProperty(ref _selectedFriendChangeFilter, value))
            {
                _friendChangesView.Refresh();
                OnPropertyChanged(nameof(FriendChangesSummary));
                OnPropertyChanged(nameof(HasNoFriendChanges));
            }
        }
    }

    public string FriendChangesSummary => FriendChanges.Count == 0
        ? "No friend-list changes seen yet."
        : $"{_friendChangesView.Cast<object>().Count()} shown · {FriendChanges.Count} changes";

    public bool HasNoFriendChanges => !_friendChangesView.Cast<object>().Any();

    public bool IsFriendChangesOpen
    {
        get => _isFriendChangesOpen;
        private set => SetProperty(ref _isFriendChangesOpen, value);
    }

    // ------------------------------------------------------- launch options

    private bool _launchChooserOpen;
    private string? _pendingLaunchLocation;
    private string _pendingLaunchFriendName = string.Empty;
    private bool _isAdvancedLaunchOpen;
    private LaunchProfileItem? _selectedLaunchProfile;
    private LaunchProfileItem? _activeLaunchProfile;
    private string _launchProfileName = string.Empty;
    private string _launchProfileArguments = string.Empty;
    private string _launchProfileError = string.Empty;

    public RelayCommand<string> ChooseLaunchModeCommand { get; }

    public RelayCommand CancelLaunchChoiceCommand { get; }

    public RelayCommand<string> SetLaunchModeCommand { get; }

    public RelayCommand OpenVrChatCommand { get; }

    public AsyncRelayCommand RejoinWorldCommand { get; }

    public RelayCommand OpenAdvancedLaunchCommand { get; }

    public RelayCommand NewLaunchProfileCommand { get; }

    public RelayCommand DuplicateLaunchProfileCommand { get; }

    public RelayCommand DeleteLaunchProfileCommand { get; }

    public RelayCommand SaveLaunchProfileCommand { get; }

    public RelayCommand UseLaunchProfileCommand { get; }

    public RelayCommand CancelAdvancedLaunchCommand { get; }

    public ObservableCollection<LaunchProfileItem> LaunchProfiles { get; }

    public AsyncRelayCommand SelfInviteLaunchCommand { get; }

    public RelayCommand OpenInstanceHistoryCommand { get; }

    public AsyncRelayCommand OpenRecentPlayersCommand { get; }

    public RelayCommand CloseRecentPlayersCommand { get; }

    public AsyncRelayCommand<NotificationDetailViewModel> JoinNotificationUserCommand { get; }

    /// <summary>
    /// Opens the sender of a notification in the one shared profile popup (the
    /// same view model and card the Friends tab uses).
    /// </summary>
    public AsyncRelayCommand<NotificationDetailViewModel> ShowSenderProfileCommand { get; }

    /// <summary>The account that owns this build; only it can unlock developer tools.</summary>
    public const string CreatorUserId = "usr_61cc28f3-76ea-41b4-a9da-ede327ef3703";

    private bool _devToolsEnabled;
    private bool _devToolsLoaded;

    /// <summary>Signed in as the creator account, so the toggle is even offered.</summary>
    public bool IsCreatorAccount
        => string.Equals(CurrentUser?.Id, CreatorUserId, StringComparison.Ordinal);

    /// <summary>Creator tools are usable only for that account with the toggle on.</summary>
    public bool IsDevMode => _devToolsEnabled && IsCreatorAccount;

    public string DevToolsButtonText => IsDevMode
        ? "Developer tools: ON"
        : "Developer tools: OFF";

    private static string DevToolsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcCompanion",
        "creator.json");

    private void LoadDevToolsSetting()
    {
        if (_devToolsLoaded)
        {
            return;
        }

        _devToolsLoaded = true;
        try
        {
            if (System.IO.File.Exists(DevToolsPath))
            {
                _devToolsEnabled = System.Text.Json.JsonSerializer.Deserialize<bool>(
                    System.IO.File.ReadAllText(DevToolsPath));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void ToggleDevTools()
    {
        if (!IsCreatorAccount)
        {
            return;
        }

        _devToolsEnabled = !_devToolsEnabled;
        SaveDevTools();
        OnPropertyChanged(nameof(IsDevMode));
        OnPropertyChanged(nameof(DevToolsButtonText));
    }

    private void SaveDevTools()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DevToolsPath)!);
            System.IO.File.WriteAllText(
                DevToolsPath,
                System.Text.Json.JsonSerializer.Serialize(_devToolsEnabled));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private string? _devToolsAccountId;

    /// <summary>
    /// Binds the creator toggle to the account that turned it on. Any other
    /// account (or a sign-out) forces it back off and persists that.
    /// </summary>
    private void SyncDevToolsAccount(string? userId)
    {
        var changedAccount = !string.Equals(userId, _devToolsAccountId, StringComparison.Ordinal);
        if (changedAccount)
        {
            _devToolsAccountId = userId;
        }

        if (_devToolsEnabled && !string.Equals(userId, CreatorUserId, StringComparison.Ordinal))
        {
            _devToolsEnabled = false;
            SaveDevTools();
        }

        OnPropertyChanged(nameof(IsCreatorAccount));
        OnPropertyChanged(nameof(IsDevMode));
        OnPropertyChanged(nameof(DevToolsButtonText));
    }

    public RelayCommand ToggleDevToolsCommand { get; }

    public AsyncRelayCommand InjectSenderLocationCommand { get; }

    /// <summary>
    /// Dev-only Join test. It never invents a world: it re-reads the live user
    /// record and only exposes Join when VRChat reports an actual world instance
    /// for that person, so the joined world is verifiably theirs.
    /// </summary>
    private async Task VerifySenderWorldAsync()
    {
        if (!IsDevMode || SelectedFriendDetail is not { } profile)
        {
            return;
        }

        try
        {
            var live = await _api.GetUserAsync(profile.Id, forceRefresh: true);
            if (live is null)
            {
                ShowError("DEV: VRChat returned no live record for this person.");
                return;
            }

            profile.ApplyUser(live);
            RaiseCommandStates();

            if (!LaunchService.IsLocation(live.Location))
            {
                ShowError($"DEV: not joinable right now — their location is '{live.Location ?? "unknown"}'.");
                return;
            }

            var worldId = LaunchService.GetWorldIdFromLocation(live.Location);
            var world = worldId is null ? null : await _api.GetWorldAsync(worldId);
            ShowError(world is null
                ? $"DEV: live location is {live.Location}."
                : $"DEV: {profile.DisplayName} is in \"{world.Name}\" — Join uses that exact instance.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("DEV: could not read their live world.");
        }
    }

    /// <summary>Desktop or VR; persisted so the next join starts the usual way.</summary>
    public LaunchMode DefaultLaunchMode
    {
        get => string.Equals(_settings.LaunchMode, "desktop", StringComparison.OrdinalIgnoreCase)
            ? LaunchMode.Desktop
            : LaunchMode.Vr;
        set
        {
            var stored = value == LaunchMode.Desktop ? "desktop" : "vr";
            if (string.Equals(_settings.LaunchMode, stored, StringComparison.Ordinal))
            {
                return;
            }

            _settings.LaunchMode = stored;
            _settingsStore.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDesktopDefault));
            OnPropertyChanged(nameof(IsVrDefault));
            OnPropertyChanged(nameof(LaunchModeLabel));
            OnPropertyChanged(nameof(LaunchDefaultText));
        }
    }

    public bool IsDesktopDefault
    {
        get => DefaultLaunchMode == LaunchMode.Desktop;
        set
        {
            if (value)
            {
                DefaultLaunchMode = LaunchMode.Desktop;
            }
        }
    }

    public bool IsVrDefault
    {
        get => DefaultLaunchMode == LaunchMode.Vr;
        set
        {
            if (value)
            {
                DefaultLaunchMode = LaunchMode.Vr;
            }
        }
    }

    public string LaunchModeLabel => DefaultLaunchMode == LaunchMode.Desktop ? "Desktop" : "VR";

    public string ActiveLaunchProfileName => _activeLaunchProfile?.DisplayName ?? "Default";

    public string ActiveLaunchProfileLabel => $"Active profile: {ActiveLaunchProfileName}";

    public string LaunchDefaultText => _activeLaunchProfile is null
        ? $"{LaunchModeLabel} mode with no extra arguments. Applies to opening VRChat and every join."
        : $"{LaunchModeLabel} mode with the {ActiveLaunchProfileName} arguments. Applies to opening VRChat and every join.";

    public bool IsAdvancedLaunchOpen
    {
        get => _isAdvancedLaunchOpen;
        private set => SetProperty(ref _isAdvancedLaunchOpen, value);
    }

    public LaunchProfileItem? SelectedLaunchProfile
    {
        get => _selectedLaunchProfile;
        set
        {
            if (!SetProperty(ref _selectedLaunchProfile, value))
            {
                return;
            }

            LaunchProfileName = value?.Name ?? string.Empty;
            LaunchProfileArguments = value?.Arguments ?? string.Empty;
            LaunchProfileError = string.Empty;
            NotifyLaunchProfileState();
        }
    }

    public string LaunchProfileName
    {
        get => _launchProfileName;
        set
        {
            if (SetProperty(ref _launchProfileName, value ?? string.Empty))
            {
                NotifyLaunchProfileState();
            }
        }
    }

    public string LaunchProfileArguments
    {
        get => _launchProfileArguments;
        set
        {
            if (SetProperty(ref _launchProfileArguments, value ?? string.Empty))
            {
                NotifyLaunchProfileState();
            }
        }
    }

    public string LaunchProfileError
    {
        get => _launchProfileError;
        private set
        {
            if (SetProperty(ref _launchProfileError, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasLaunchProfileError));
            }
        }
    }

    public bool HasLaunchProfileError => !string.IsNullOrWhiteSpace(LaunchProfileError);

    public bool CanEditLaunchProfile => SelectedLaunchProfile is { IsDefault: false };

    public bool CanDeleteLaunchProfile => CanEditLaunchProfile;

    public bool CanSaveLaunchProfile => CanEditLaunchProfile;

    public bool IsLaunchChooserOpen
    {
        get => _launchChooserOpen;
        private set
        {
            if (SetProperty(ref _launchChooserOpen, value))
            {
                OnPropertyChanged(nameof(LaunchChoiceTitle));
                OnPropertyChanged(nameof(LaunchChoiceSubtitle));
                RaiseCommandStates();
            }
        }
    }

    public string LaunchChoiceTitle => string.IsNullOrWhiteSpace(_pendingLaunchFriendName)
        ? "How should VRChat open?"
        : $"Join {_pendingLaunchFriendName}";

    public string LaunchChoiceSubtitle => _activeLaunchProfile is null
        ? $"VRChat opens in {LaunchModeLabel} mode with no extra arguments."
        : $"VRChat opens in {LaunchModeLabel} mode with the {ActiveLaunchProfileName} arguments.";

    public ObservableCollection<FilterOption> FriendFilters { get; }

    public FilterOption SelectedFriendFilter
    {
        get => _selectedFriendFilter;
        set
        {
            if (value is not null && SetProperty(ref _selectedFriendFilter, value))
            {
                RefreshFriendsView();
            }
        }
    }

    private bool _showOnlineOnly;
    public bool ShowOnlineOnly
    {
        get => _showOnlineOnly;
        set
        {
            if (SetProperty(ref _showOnlineOnly, value))
            {
                RefreshFriendsView();
            }
        }
    }

    private string _friendFilter = string.Empty;
    public string FriendFilter
    {
        get => _friendFilter;
        set
        {
            if (SetProperty(ref _friendFilter, value ?? string.Empty))
            {
                RefreshFriendsView();
            }
        }
    }

    private string _friendsSummary = string.Empty;
    public string FriendsSummary
    {
        get => _friendsSummary;
        private set => SetProperty(ref _friendsSummary, value);
    }

    private bool _hasNoVisibleFriends;
    public bool HasNoVisibleFriends
    {
        get => _hasNoVisibleFriends;
        private set => SetProperty(ref _hasNoVisibleFriends, value);
    }

    public int OnlineFriendCount { get; private set; }

    public int TotalFriendCount { get; private set; }

    public int FavoriteFriendCount { get; private set; }

    public string FriendsEmptyMessage => TotalFriendCount == 0
        ? "Your friend list is empty."
        : "No friends match the current filter.";

    public FriendItem? SelectedFriend
    {
        get => _selectedFriend;
        set
        {
            if (!SetProperty(ref _selectedFriend, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedFriend));
            OnPropertyChanged(nameof(SelectedFriendDetail));
            _ = LoadFriendDetailAsync(value);
            RaiseCommandStates();
        }
    }

    public FriendDetailViewModel? SelectedFriendDetail
    {
        get => _friendDetail;
        private set
        {
            // Closing or switching the profile commits a note that was typed but
            // never saved, so navigating away cannot silently drop it.
            if (!ReferenceEquals(_friendDetail, value))
            {
                _friendDetail?.SavePendingNote();
                _friendDetail?.CloseTransientPopups();
            }

            if (SetProperty(ref _friendDetail, value))
            {
                OnPropertyChanged(nameof(HasSelectedFriend));
                RaiseCommandStates();
            }
        }
    }

    public bool HasSelectedFriend => _friendDetail is not null;

    // --------------------------------------------------------- notifications

    public ObservableCollection<NotificationItem> Notifications { get; }

    public ICollectionView NotificationsView => _notificationsView;

    public ObservableCollection<FilterOption> NotificationFilters { get; }

    public FilterOption SelectedNotificationFilter
    {
        get => _selectedNotificationFilter;
        set
        {
            if (value is not null && SetProperty(ref _selectedNotificationFilter, value))
            {
                RefreshNotificationsView();
            }
        }
    }

    public string NotificationSearch
    {
        get => _notificationSearch;
        set
        {
            if (SetProperty(ref _notificationSearch, value ?? string.Empty))
            {
                RefreshNotificationsView();
            }
        }
    }

    private int _unreadNotificationCount;
    public int UnreadNotificationCount
    {
        get => _unreadNotificationCount;
        private set => SetProperty(ref _unreadNotificationCount, value);
    }

    private string _notificationsSummary = string.Empty;
    public string NotificationsSummary
    {
        get => _notificationsSummary;
        private set => SetProperty(ref _notificationsSummary, value);
    }

    public string NotificationFilterSummary
    {
        get => _notificationFilterSummary;
        private set => SetProperty(ref _notificationFilterSummary, value);
    }

    public bool HasNoVisibleNotifications
    {
        get => _hasNoVisibleNotifications;
        private set => SetProperty(ref _hasNoVisibleNotifications, value);
    }

    public string NotificationsEmptyMessage
    {
        get => _notificationsEmptyMessage;
        private set => SetProperty(ref _notificationsEmptyMessage, value);
    }

    public NotificationItem? SelectedNotification
    {
        get => _selectedNotification;
        set
        {
            if (!SetProperty(ref _selectedNotification, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedNotification));
            _ = LoadNotificationDetailAsync(value);
            RaiseCommandStates();
        }
    }

    public NotificationDetailViewModel? NotificationDetail
    {
        get => _notificationDetail;
        private set
        {
            if (SetProperty(ref _notificationDetail, value))
            {
                OnPropertyChanged(nameof(HasSelectedNotification));
                RaiseCommandStates();
            }

        }
    }

    public bool HasSelectedNotification => _notificationDetail is not null;

    public NotificationItem? BoopPreview
    {
        get => _boopPreview;
        private set
        {
            if (SetProperty(ref _boopPreview, value))
            {
                OnPropertyChanged(nameof(IsBoopPreviewOpen));
                OnPropertyChanged(nameof(BoopPreviewTitle));
                OnPropertyChanged(nameof(BoopPreviewKindText));
            }
        }
    }

    public bool IsBoopPreviewOpen => _boopPreview is not null;

    public string BoopPreviewTitle => _boopPreview?.BoopAttachmentTitle ?? string.Empty;

    public string BoopPreviewKindText => _boopPreview?.BoopKindText ?? string.Empty;

    private void OpenBoopPreview(NotificationItem? item)
    {
        if (item is null || !item.HasBoopImage)
        {
            return;
        }

        BoopPreview = item;
    }

    private void CloseBoopPreview() => BoopPreview = null;

    public bool IsInviteComposerOpen
    {
        get => _isInviteComposerOpen;
        private set => SetProperty(ref _isInviteComposerOpen, value);
    }

    public string InviteComposerTitle => _inviteComposerFriend is null
        ? "Send an invite"
        : $"Invite {_inviteComposerFriend.DisplayName}";

    public string InviteComposerSubtitle => LaunchService.IsLocation(CurrentUser?.Location)
        ? "Choose one way. A custom message is optional and stays with the invite."
        : "Choose one way. Join a world first to invite them to yours, or request their invite.";

    public ObservableCollection<FilterOption> InviteComposerModes { get; }

    public ObservableCollection<InviteMessageOption> InviteMessages { get; }

    public string InviteComposerMode
    {
        get => _inviteComposerMode;
        set
        {
            var next = string.Equals(value, "request", StringComparison.Ordinal)
                ? "request"
                : "invite";
            if (!SetProperty(ref _inviteComposerMode, next))
            {
                return;
            }

            SelectedInviteMessage = null;
            OnPropertyChanged(nameof(InviteComposerSendLabel));
            if (!_suppressInviteModeLoad)
            {
                _ = LoadInviteMessagesAsync(next);
            }
        }
    }

    public string InviteComposerSendLabel => string.Equals(
        InviteComposerMode,
        "request",
        StringComparison.Ordinal)
        ? "Send request"
        : "Send invite";

    public bool HasInviteComposerLocationHint => !LaunchService.IsLocation(CurrentUser?.Location);

    public string InviteComposerLocationHint =>
        "You are not in a world right now. Join one to invite them, or request their invite instead.";

    /// <summary>Errors raised by either composer stay visible inside the dialog.</summary>
    public string ComposerError
    {
        get => _composerError;
        private set
        {
            if (SetProperty(ref _composerError, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasComposerError));
            }
        }
    }

    public bool HasComposerError => !string.IsNullOrWhiteSpace(ComposerError);

    public string InviteComposerMessage
    {
        get => _inviteComposerMessage;
        set
        {
            var next = value ?? string.Empty;
            if (next.Length > 64)
            {
                next = next[..64];
            }

            if (SetProperty(ref _inviteComposerMessage, next))
            {
                OnPropertyChanged(nameof(InviteComposerMessageCount));
                NotifyInvitePresetState();
            }
        }
    }

    public string InviteComposerMessageCount => $"{InviteComposerMessage.Length}/64";

    public bool IsInviteMessagesLoading
    {
        get => _isInviteMessagesLoading;
        private set => SetProperty(ref _isInviteMessagesLoading, value);
    }

    public string InviteMessagesStatus
    {
        get => _inviteMessagesStatus;
        private set => SetProperty(ref _inviteMessagesStatus, value ?? string.Empty);
    }

    public InviteMessageOption? SelectedInviteMessage
    {
        get => _selectedInviteMessage;
        set
        {
            if (!SetProperty(ref _selectedInviteMessage, value))
            {
                return;
            }

            if (value is not null)
            {
                if (IsResponseComposerOpen)
                {
                    ResponseComposerMessage = value.Message;
                }
                else
                {
                    InviteComposerMessage = value.Message;
                }
            }

            NotifyInvitePresetState();
        }
    }

    private string ActivePresetText => IsResponseComposerOpen
        ? ResponseComposerMessage
        : InviteComposerMessage;

    public bool IsInvitePresetDirty => _selectedInviteMessage is not null &&
        !string.Equals(_selectedInviteMessage.Message, ActivePresetText, StringComparison.Ordinal);

    public bool CanSaveInvitePreset => _selectedInviteMessage is not null &&
        IsInvitePresetDirty &&
        !string.IsNullOrWhiteSpace(ActivePresetText) &&
        !_selectedInviteMessage.HasCooldown;

    public string InvitePresetStatus
    {
        get
        {
            var selected = _selectedInviteMessage;
            if (selected is null)
            {
                return "No preset selected. Typing uses the next free slot.";
            }

            var state = selected.HasCooldown
                ? $"cooling down for {selected.RemainingCooldownMinutes} min"
                : IsInvitePresetDirty
                    ? "unsaved changes"
                    : "saved";
            return $"Editing {selected.TypeLabel.ToLowerInvariant()} slot {selected.Slot}: {state}.";
        }
    }

    public string InviteSlot11Summary => InvitePresetStatus;

    private void NotifyInvitePresetState()
    {
        OnPropertyChanged(nameof(IsInvitePresetDirty));
        OnPropertyChanged(nameof(CanSaveInvitePreset));
        OnPropertyChanged(nameof(InvitePresetStatus));
        OnPropertyChanged(nameof(InviteSlot11Summary));
        SaveInvitePresetCommand.RaiseCanExecuteChanged();
    }

    public bool IsResponseComposerOpen
    {
        get => _isResponseComposerOpen;
        private set => SetProperty(ref _isResponseComposerOpen, value);
    }

    public string ResponseComposerTitle => _responseComposerNotification is null
        ? "Decline with a message"
        : $"Decline {_responseComposerNotification.SenderName}'s {(_responseComposerNotification.IsInvite ? "invite" : "invite request")}";

    public string ResponseComposerSubtitle => "Send a short reply, then remove the notification from your list.";

    public string ResponseComposerMessage
    {
        get => _responseComposerMessage;
        set
        {
            var next = value ?? string.Empty;
            if (next.Length > 64)
            {
                next = next[..64];
            }

            if (SetProperty(ref _responseComposerMessage, next))
            {
                OnPropertyChanged(nameof(ResponseComposerMessageCount));
                SendDeclineWithMessageCommand.RaiseCanExecuteChanged();
                NotifyInvitePresetState();
            }
        }
    }

    public string ResponseComposerMessageCount => $"{ResponseComposerMessage.Length}/64";

    public bool HasUnreadNotifications => UnreadNotificationCount > 0;

    public bool HasNoNotifications => Notifications.Count == 0;

    // ---------------------------------------------------------------- worlds

    public ObservableCollection<WorldItem> Worlds { get; }

    private string _worldQuery = string.Empty;
    public string WorldQuery
    {
        get => _worldQuery;
        set => SetProperty(ref _worldQuery, value ?? string.Empty);
    }

    private string _worldsSummary = string.Empty;
    public string WorldsSummary
    {
        get => _worldsSummary;
        private set => SetProperty(ref _worldsSummary, value);
    }

    private string _worldsEmptyMessage = "Search for a world by name to get started.";
    public string WorldsEmptyMessage
    {
        get => _worldsEmptyMessage;
        private set => SetProperty(ref _worldsEmptyMessage, value);
    }

    public bool HasNoWorlds => Worlds.Count == 0;

    public WorldDialogViewModel? WorldDialog
    {
        get => _worldDialog;
        private set
        {
            if (SetProperty(ref _worldDialog, value))
            {
                OnPropertyChanged(nameof(HasWorldDialog));
                RaiseCommandStates();
            }
        }
    }

    public bool HasWorldDialog => _worldDialog is not null;

    private string _quickJoinInput = string.Empty;
    public string QuickJoinInput
    {
        get => _quickJoinInput;
        set
        {
            if (SetProperty(ref _quickJoinInput, value ?? string.Empty))
            {
                QuickJoinCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // ------------------------------------------------------------ appearance

    public ObservableCollection<FilterOption> BackdropStyles { get; }

    public FilterOption SelectedBackdropStyle
    {
        get => _selectedBackdropStyle;
        set
        {
            if (value is null || !SetProperty(ref _selectedBackdropStyle, value))
            {
                return;
            }

            _settings.BackdropStyle = value.Key;
            _settingsStore.Save();
            OnPropertyChanged(nameof(BackdropVariant));
        }
    }

    public string BackdropVariant => _selectedBackdropStyle.Key;

    public bool AnimateBackdrop
    {
        get => _settings.AnimateBackdrop;
        set
        {
            if (_settings.AnimateBackdrop == value)
            {
                return;
            }

            _settings.AnimateBackdrop = value;
            _settingsStore.Save();
            OnPropertyChanged();
        }
    }

    private string? _customBackdropPath;
    public string? CustomBackdropPath
    {
        get
        {
            if (_customBackdropPath is null && !string.IsNullOrWhiteSpace(_settings.BackdropImagePath))
            {
                _customBackdropPath = _settings.BackdropImagePath;
            }
            return _customBackdropPath;
        }
        set
        {
            if (SetProperty(ref _customBackdropPath, value))
            {
                _settings.BackdropImagePath = value;
                _settingsStore.Save();
                OnPropertyChanged(nameof(HasCustomBackdrop));
                OnPropertyChanged(nameof(BackdropFileName));
            }
        }
    }

    public double BackdropDim
    {
        get => _settings.BackdropDim;
        set
        {
            if (_settings.BackdropDim == value) return;
            _settings.BackdropDim = value;
            _settingsStore.Save();
            OnPropertyChanged();
        }
    }

    // --- New feature settings ---

    public string ActivityDefaultFilter
    {
        get => _settings.ActivityDefaultFilter;
        set
        {
            if (_settings.ActivityDefaultFilter == value) return;
            _settings.ActivityDefaultFilter = value;
            _settingsStore.Save();
            OnPropertyChanged();
            ActivityFeedViewModel?.SetDefaultFilter(value);
        }
    }

    public bool ActivityShowOfflineFriends
    {
        get => _settings.ActivityShowOfflineFriends;
        set
        {
            if (_settings.ActivityShowOfflineFriends == value) return;
            _settings.ActivityShowOfflineFriends = value;
            _settingsStore.Save();
            OnPropertyChanged();
            if (ActivityFeedViewModel is not null)
            {
                ActivityFeedViewModel.ShowOfflineFriends = value;
            }
        }
    }

    public string? LastActiveAccountId
    {
        get => _settings.LastActiveAccountId;
        set
        {
            if (_settings.LastActiveAccountId == value) return;
            _settings.LastActiveAccountId = value;
            _settingsStore.Save();
            OnPropertyChanged();
        }
    }

    public List<string> BackgroundPlaylist
    {
        get => _settings.BackgroundPlaylist;
        set
        {
            if (_settings.BackgroundPlaylist == value) return;
            _settings.BackgroundPlaylist = value;
            _settingsStore.Save();
            OnPropertyChanged();
        }
    }

    public string? ProfileBackdropPath
    {
        get => _settings.ProfileBackdropImagePath;
        set
        {
            if (string.Equals(_settings.ProfileBackdropImagePath, value, StringComparison.Ordinal))
            {
                return;
            }

            _settings.ProfileBackdropImagePath = value;
            _settingsStore.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasProfileBackdrop));
            OnPropertyChanged(nameof(ProfileBackdropFileName));
            ClearProfileBackdropCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasProfileBackdrop
        => !string.IsNullOrWhiteSpace(ProfileBackdropPath) && File.Exists(ProfileBackdropPath);

    public string ProfileBackdropFileName => HasProfileBackdrop
        ? Path.GetFileName(ProfileBackdropPath) ?? string.Empty
        : "No popup background";

    public bool HasCustomBackdrop
        => !string.IsNullOrWhiteSpace(CustomBackdropPath) && File.Exists(CustomBackdropPath);

    public string BackdropFileName => HasCustomBackdrop
        ? Path.GetFileName(CustomBackdropPath) ?? string.Empty
        : "Built-in animated backdrop";

    private string _backdropUrlInput = string.Empty;
    public string BackdropUrlInput
    {
        get => _backdropUrlInput;
        set
        {
            if (SetProperty(ref _backdropUrlInput, value ?? string.Empty))
            {
                DownloadBackdropFromUrlCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ClockText => DateTime.Now.ToString("h:mm tt", CultureInfo.CurrentCulture);

    public string GreetingText
    {
        get
        {
            var greeting = DateTime.Now.Hour switch
            {
                < 5 => "Still up",
                < 12 => "Good morning",
                < 18 => "Good afternoon",
                _ => "Good evening"
            };

            return string.IsNullOrWhiteSpace(_greetingName)
                ? greeting
                : $"{greeting}, {_greetingName}";
        }
    }

    // -------------------------------------------------------------- commands

    public RelayCommand ClearErrorCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }
    public AsyncRelayCommand VerifyTwoFactorCommand { get; }
    public AsyncRelayCommand CancelTwoFactorCommand { get; }
    public RelayCommand UseEmailCodeCommand { get; }
    public RelayCommand UseTotpCodeCommand { get; }
    public RelayCommand UseRecoveryCodeCommand { get; }
    public AsyncRelayCommand SaveStatusCommand { get; }
    public AsyncRelayCommand MarkAllReadCommand { get; }
    public AsyncRelayCommand<NotificationItem> MarkNotificationSeenCommand { get; }
    public RelayCommand<NotificationItem> OpenBoopPreviewCommand { get; }
    public RelayCommand<NotificationItem> OpenNotificationGroupCommand { get; }
    public AsyncRelayCommand<NotificationItem> AcceptGroupInviteCommand { get; }
    public AsyncRelayCommand<NotificationItem> DeclineGroupInviteCommand { get; }
    public RelayCommand CloseBoopPreviewCommand { get; }
    public AsyncRelayCommand SearchWorldsCommand { get; }
    public AsyncRelayCommand<string> SearchSuggestionCommand { get; }
    public RelayCommand<WorldItem> OpenWorldCommand { get; }
    public RelayCommand CloseFriendDetailCommand { get; }

    public RelayCommand OpenOwnProfileCommand { get; }
    public AsyncRelayCommand<FriendDetailViewModel> RefreshFriendProfileCommand { get; }
    public AsyncRelayCommand<FriendDetailViewModel> ToggleFriendCommand { get; }
    public AsyncRelayCommand PlayerSearchCommand { get; }
    public AsyncRelayCommand<FriendItem> OpenPlayerSearchResultCommand { get; }
    public RelayCommand ClearPlayerSearchCommand { get; }
    public AsyncRelayCommand<FriendItem> JoinFriendCommand { get; }
    public AsyncRelayCommand<FriendItem> SelfInviteFriendCommand { get; }
    public RelayCommand<FriendItem> InviteFriendCommand { get; }
    public RelayCommand<FriendItem> RequestInviteFriendCommand { get; }
    public RelayCommand<string> SelectInviteComposerModeCommand { get; }
    public AsyncRelayCommand SaveInvitePresetCommand { get; }
    public RelayCommand ClearInvitePresetCommand { get; }
    public AsyncRelayCommand<string> SendInviteChoiceCommand { get; }
    public RelayCommand CancelInviteComposerCommand { get; }
    public AsyncRelayCommand<NotificationItem> AcceptInviteRequestCommand { get; }
    public AsyncRelayCommand<NotificationItem> DeclineNotificationCommand { get; }
    public RelayCommand<NotificationItem> DeclineNotificationWithMessageCommand { get; }
    public AsyncRelayCommand SendDeclineWithMessageCommand { get; }
    public RelayCommand CancelResponseComposerCommand { get; }
    public RelayCommand<FriendItem> OpenFriendProfileCommand { get; }
    public RelayCommand<FriendItem> CopyFriendLocationCommand { get; }
    public RelayCommand<FriendItem> CopyFriendUserIdCommand { get; }
    public RelayCommand<FriendItem> CopyFriendProfileLinkCommand { get; }
    public AsyncRelayCommand<FriendItem> OpenFriendWorldCommand { get; }
    public AsyncRelayCommand OpenFriendLocationsCommand { get; }
    public RelayCommand OpenFriendsCommand { get; }
    public RelayCommand OpenInstanceCommand { get; }
    public AsyncRelayCommand<InstancePlayerItem> OpenInstancePlayerCommand { get; }
    public RelayCommand CloseFriendLocationsCommand { get; }
    public RelayCommand<FriendItem> OpenFriendLocationCommand { get; }
    public RelayCommand OpenFriendChangesCommand { get; }
    public RelayCommand CloseFriendChangesCommand { get; }
    public RelayCommand<FriendChangeItem> OpenFriendChangeCommand { get; }
    public AsyncRelayCommand<ActivityEntry> OpenActivityAvatarCommand { get; }
    public AsyncRelayCommand<ActivityEntry> OpenActivityUserCommand { get; }
    public RelayCommand CloseWorldDialogCommand { get; }
    public AsyncRelayCommand RefreshWorldDialogCommand { get; }
    public AsyncRelayCommand CreateInstanceCommand { get; }
    public RelayCommand LaunchCreatedInstanceCommand { get; }
    public RelayCommand CopyCreatedInstanceLinkCommand { get; }
    public RelayCommand<WorldItem> OpenWorldInBrowserCommand { get; }
    public RelayCommand<WorldInstanceItem> JoinWorldInstanceCommand { get; }
    public RelayCommand<WorldInstanceItem> OpenWorldInstanceGroupCommand { get; }
    public AsyncRelayCommand QuickJoinCommand { get; }
    public AsyncRelayCommand<NotificationItem> AcceptFriendRequestCommand { get; }
    public AsyncRelayCommand<NotificationItem> JoinInviteCommand { get; }
    public AsyncRelayCommand JoinDialogInputCommand { get; }
    public RelayCommand DismissInfoCommand { get; }
    public RelayCommand<FriendItem> ToggleFavoriteFriendCommand { get; }
    public RelayCommand<WorldItem> ToggleFavoriteWorldCommand { get; }
    public RelayCommand<NotificationDetailViewModel> OpenNotificationProfileCommand { get; }
    public RelayCommand<NotificationDetailViewModel> CopyNotificationLocationCommand { get; }
    public AsyncRelayCommand OpenNotificationWorldCommand { get; }
    public AsyncRelayCommand RefreshNotificationDetailCommand { get; }
    public RelayCommand ClearNotificationSelectionCommand { get; }
    public RelayCommand ChooseBackdropCommand { get; }
    public RelayCommand ClearBackdropCommand { get; }
    public RelayCommand ChooseProfileBackdropCommand { get; }
    public RelayCommand ClearProfileBackdropCommand { get; }
    public AsyncRelayCommand DownloadBackdropFromUrlCommand { get; }

    // New feature commands
    public AsyncRelayCommand LoadAvatarsCommand { get; }
    public AsyncRelayCommand LoadGroupsCommand { get; }
    public AsyncRelayCommand LoadActivityFeedCommand { get; }
    public AsyncRelayCommand RefreshInstanceCommand { get; }
    public RelayCommand CopyInstanceLinkCommand { get; }
    public RelayCommand CloseInstanceCommand { get; }

    // ----------------------------------------------------------------- flows

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        // A no-op unless this is an installed Velopack build.
        UpdateViewModel.Start();

        // Initialize new feature view models on the signed-in client
        AvatarViewModel = new AvatarViewModel(_api);
        AvatarViewModel.GlobalResultsChanged += OnAvatarSearchResultsChanged;
        GroupViewModel = new GroupViewModel(_api, OpenLaunchChooser);
        ActivityFeedViewModel = new ActivityFeedViewModel(_api, ActivityStore.Shared);
        ActivityFeedViewModel.HistoryLoaded += LoadFriendChangesFromHistory;
        ActivityFeedViewModel.FilterChanged += RememberActivityFilter;
        ActivityFeedViewModel.SetDefaultFilter(ActivityDefaultFilter);
        ActivityFeedViewModel.ShowOfflineFriends = ActivityShowOfflineFriends;
        _instanceHistoryStore.Load();
        _instanceHistoryRecorder = new InstanceHistoryRecorder(_instanceHistoryStore);
        InstanceHistoryViewModel = new InstanceHistoryViewModel(_api, _instanceHistoryStore)
        {
            LaunchRequested = OpenHistoryLaunch,
            OpenInGameRequested = OpenHistoryInGame,
            PlayerProfileRequested = OpenHistoryPlayer
        };
        RecentPlayersViewModel = new RecentPlayersViewModel(_api, _instanceHistoryStore)
        {
            PlayerProfileRequested = OpenRecentPlayer,
            WorldRequested = session => _ = OpenRecentWorldAsync(session)
        };
        ContinuityViewModel = new ContinuityViewModel(_api)
        {
            PersonProfileRequested = OpenContinuityPerson,
            WorldRequested = worldId => _ = OpenContinuityWorldAsync(worldId)
        };
        ContextViewModel = new ContextViewModel
        {
            RefreshRequested = RefreshContextAsync,
            PlayerProfileRequested = OpenContextPlayer,
            PersonProfileRequested = OpenContinuityPerson,
            SessionRequested = OpenContinuitySession,
            WorldRequested = worldId => _ = OpenContinuityWorldAsync(worldId),
            PersonAvatarRequested = ContinuityViewModel.RequestAvatar
        };
        // Warm the durable local history in the background so the first visit
        // already has real rows instead of an empty placeholder.
        _ = ContinuityViewModel.EnsureLoadedAsync();
        InstanceViewModel = new InstanceViewModel(_api, _images, _logPlayerService);
        InstanceViewModel.PlayerAvatarChanged += (previous, current) =>
        {
            if (Friends.Any(friend =>
                    string.Equals(friend.Id, current.Id, StringComparison.Ordinal)))
            {
                ActivityFeedViewModel?.ApplyFriendUpdate(current, previous);
            }
        };
        InstanceViewModel.PropertyChanged += (_, args) =>
        {
            if (string.IsNullOrEmpty(args.PropertyName) ||
                args.PropertyName == nameof(InstanceViewModel.IsLoading))
            {
                RefreshInstanceCommand.RaiseCanExecuteChanged();
                CopyInstanceLinkCommand.RaiseCanExecuteChanged();
            }
        };
        ActivityFeedViewModel.RefreshRequested = LoadAllFriendsAsync;
        _instanceHistoryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _instanceHistoryTimer.Tick += (_, _) => PollInstanceHistory();
        _instanceHistoryTimer.Start();

        _activityPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Realtime is the primary path. This poll is the safety net for
            // missed pipeline events, so it has to run on every tab and much
            // faster than the old 30-second Activity-only tick.
            Interval = TimeSpan.FromSeconds(5)
        };
        _activityPollTimer.Tick += OnActivityPollTick;
        _activityPollTimer.Start();

        _bioPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // VRCX checks one stale friend bio every few seconds; friend-update
            // events do not carry bio, so a periodic profile read is required.
            Interval = TimeSpan.FromSeconds(3)
        };
        _bioPollTimer.Tick += OnBioPollTick;
        _bioPollTimer.Start();
        MultiAccountViewModel = new MultiAccountViewModel(_api);
        MultiAccountViewModel.SessionSwitched += OnAccountSessionSwitched;

        if (!_api.HasSavedSession)
        {
            return;
        }

        PushBusy("Restoring your VRChat session…");
        try
        {
            var user = await _api.RestoreSessionAsync();
            if (user is null)
            {
                ShowError("Your saved VRChat session expired. Sign in again.");
                return;
            }

            ApplyCurrentUser(user);
            IsLoggedIn = true;
            _realtime.Start();
            SelectedSection = _friendsNav;
            MultiAccountViewModel?.RememberCurrentAccount(user);
            await RefreshDataAsync(refreshUser: false);
        }
        catch (VrcApiException ex)
        {
            ShowError($"Couldn't restore your session. {ex.Message}");
        }
        catch (HttpRequestException)
        {
            ShowError("Couldn't restore your session because VRChat is unreachable. You can sign in manually.");
        }
        catch (TaskCanceledException)
        {
            ShowError("Restoring your session timed out. You can sign in manually.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Something went wrong while restoring your session.");
        }
        finally
        {
            PopBusy();
        }
    }

    public async Task LoginAsync(string username, string password)
    {
        if (IsBusy)
        {
            return;
        }

        ClearError();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            ShowError("Enter your VRChat username and password.");
            return;
        }

        PushBusy("Signing in to VRChat…");
        try
        {
            var result = await _api.LoginAsync(username.Trim(), password);
            if (result.RequiresTwoFactor)
            {
                ConfigureTwoFactor(result.TwoFactorMethods);
                return;
            }

            if (result.User is null)
            {
                ShowError("VRChat returned an unexpected login response.");
                return;
            }

            await CompleteLoginAsync(result.User);
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat. Check your internet connection.");
        }
        catch (TaskCanceledException)
        {
            ShowError("The sign-in request timed out. Try again.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Something went wrong while signing in.");
        }
        finally
        {
            PopBusy();
        }
    }

    public async Task RefreshAsync()
    {
        if (!IsLoggedIn || IsBusy)
        {
            return;
        }

        await RefreshDataAsync(refreshUser: true);
    }

    /// <summary>
    /// Refreshes the account location first so the instance popup reflects a
    /// world change immediately, then asks the instance view model to reload
    /// the world, metadata and real player roster.
    /// </summary>
    private async Task LoadInstanceAsync()
    {
        var viewModel = InstanceViewModel;
        if (!IsLoggedIn || viewModel is null)
        {
            return;
        }

        try
        {
            var result = await _api.GetCurrentUserAsync();
            if (result.User is null)
            {
                viewModel.SetError("Your VRChat session expired. Sign in again.");
                return;
            }

            ApplyCurrentUser(result.User);
            await viewModel.LoadAsync(result.User, Friends.ToList());
        }
        catch (VrcApiException ex) when (ex.StatusCode == 401)
        {
            SignOutLocally("Your VRChat session expired. Sign in again.");
            _api.ClearSavedSession();
        }
        catch (VrcApiException ex)
        {
            viewModel.SetError(ex.Message);
        }
        catch (HttpRequestException)
        {
            viewModel.SetError("Could not reach VRChat. Check your internet connection.");
        }
        catch (TaskCanceledException)
        {
            viewModel.SetError("The instance request timed out. Try refresh.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            viewModel.SetError("Could not load this VRChat instance.");
        }
    }

    private void CopyInstanceLink()
    {
        var link = InstanceViewModel?.ShareLink;
        if (string.IsNullOrWhiteSpace(link))
        {
            return;
        }

        CopyText(link, "Instance link copied to your clipboard.");
    }

    private void OpenInstance()
    {
        if (!IsLoggedIn)
        {
            return;
        }

        if (SelectedSection.Key == "instance")
        {
            _ = LoadInstanceAsync();
            return;
        }

        SelectedSection = _instanceNav;
    }

    private void OpenFriends()
    {
        if (IsLoggedIn)
        {
            SelectedSection = _friendsNav;
        }
    }

    private async Task OpenInstancePlayerAsync(InstancePlayerItem? player)
    {
        if (player is null || !player.CanOpenProfile || IsBusy)
        {
            return;
        }

        if (player.IsSelf && CurrentUser is not null)
        {
            SelectedFriend = null;
            OpenOwnProfile();
            return;
        }

        // A row can be visible before the roster's profile pass has merged the
        // full friend record. Prefer the friend-list copy in that case so the
        // popup still gets the real avatar instead of a bare initial.
        var friend = Friends.FirstOrDefault(item =>
            string.Equals(item.Id, player.UserId, StringComparison.Ordinal));
        var user = player.User;
        if (string.IsNullOrWhiteSpace(player.AvatarUrl) && friend?.User is { } friendUser)
        {
            user = friendUser;
        }
        else if (string.IsNullOrWhiteSpace(player.AvatarUrl))
        {
            var instanceUser = InstanceViewModel?.Instance?.Users.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, player.UserId, StringComparison.Ordinal));
            if (instanceUser is not null)
            {
                user = instanceUser;
            }
        }

        user ??= new VrcUser
        {
            Id = player.UserId,
            DisplayName = player.DisplayName,
            Username = player.Username
        };

        // Everyone in this list is standing in the instance currently being
        // shown. Friend records can still say "private", so use the real
        // instance location rather than rendering "In a private world".
        if (InstanceViewModel?.Instance?.Location is { } location)
        {
            user.Location = location;
            user.State = "online";
        }

        user.IsFriend = user.IsFriend || player.IsFriend;
        SelectedFriend = null;
        await LoadFriendDetailAsync(
            CreatePresenceAwareFriend(user),
            forceRefresh: true,
            instanceLocation: InstanceViewModel?.Instance?.Location);
    }

    private void CloseInstance()
    {
        if (SelectedSection.Key != "instance")
        {
            return;
        }

        SelectedFriendDetail?.ClearPinnedLocation();

        var target = _instanceReturnNav ?? _activityNav;
        _instanceReturnNav = null;
        SelectedSection = target;
    }

    /// <summary>
    /// Refresh used by the background timer. Offline friends change rarely and
    /// cost one request per 100 friends, so only the fast-changing data is
    /// fetched here; the full download stays on startup and manual refresh.
    /// </summary>
    private async void OnActivityPollTick(object? sender, EventArgs e)
    {
        if (!IsLoggedIn || IsBusy || _activityPollBusy)
        {
            return;
        }

        _activityPollBusy = true;
        try
        {
            var friends = await _api.GetFriendsAsync(
                forceRefresh: true,
                onlineOnly: true);
            if (!IsLoggedIn)
            {
                return;
            }

            FriendAvatarStore.Shared.ObserveAll(CurrentUser?.Id, friends);
            var onlineIds = new HashSet<string>(
                friends.Select(friend => friend.Id),
                StringComparer.Ordinal);
            var offlineIds = CurrentUser?.OfflineFriends
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            if (offlineIds is { Count: 0 })
            {
                offlineIds = null;
            }

            // The Friends tab has to demote the same people the Activity feed
            // does; otherwise a friend who left can stay green until a manual
            // full refresh even though the fast poll already knows.
            foreach (var friend in Friends)
            {
                if (!onlineIds.Contains(friend.Id) &&
                    (offlineIds is null || offlineIds.Contains(friend.Id)))
                {
                    friend.MarkOffline();
                }
            }

            UpdateFriends(friends, keepMissing: true);
            ActivityFeedViewModel?.ApplyOnlineFriends(friends, offlineIds);
            RefreshFriendsView();

            // Avatar switches made from VRChat's own menu are not part of the
            // friend payload. Refresh the signed-in user separately, less often
            // than the friend poll, so those switches still become self Activity.
            if (DateTimeOffset.UtcNow - _lastSelfActivityRefreshAt >= TimeSpan.FromSeconds(10))
            {
                _lastSelfActivityRefreshAt = DateTimeOffset.UtcNow;
                try
                {
                    var self = await _api.GetCurrentUserAsync();
                    if (IsLoggedIn &&
                        self.User is { } latest &&
                        CurrentUser is { } current &&
                        ((!string.IsNullOrWhiteSpace(latest.CurrentAvatar) &&
                          !string.Equals(latest.CurrentAvatar, current.CurrentAvatar, StringComparison.Ordinal)) ||
                         (!string.IsNullOrWhiteSpace(latest.CurrentAvatarName) &&
                          !string.Equals(latest.CurrentAvatarName, current.CurrentAvatarName, StringComparison.Ordinal)) ||
                         (latest.Bio is not null &&
                          !string.Equals(latest.Bio, current.Bio, StringComparison.Ordinal))))
                    {
                        ApplyCurrentUser(latest);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"activity self poll: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // The 30-second poll is a safety net; the regular refresh reports
            // real errors and the next tick can recover on its own.
            Debug.WriteLine($"activity friend poll: {ex.Message}");
        }
        finally
        {
            _activityPollBusy = false;
        }
    }

    /// <summary>
    /// VRCX-style bio scan: one stale profile every few seconds. VRChat removed
    /// bio from websocket/friend payloads, so a profile read is the only source
    /// that catches edits made on the website or another client.
    /// </summary>
    private async void OnBioPollTick(object? sender, EventArgs e)
    {
        if (!IsLoggedIn ||
            _bioPollBusy ||
            DateTimeOffset.UtcNow < _bioPollPausedUntil ||
            ActivityFeedViewModel is null ||
            CurrentUser is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        // Your own edits should show up almost immediately, even on a very
        // large friend list. Friends are rechecked much more often than the old
        // 12-hour window; online friends go first because they are the ones
        // most likely to have just edited their profile elsewhere.
        var userId = ActivityFeedViewModel.IsBioCheckDue(
                CurrentUser.Id,
                now,
                TimeSpan.FromMinutes(1))
            ? CurrentUser.Id
            : ActivityFeedViewModel.NextBioCandidateId(
                  Friends.Where(friend => friend.IsPresent).Select(friend => friend.Id),
                  now,
                  TimeSpan.FromMinutes(5)) ??
              ActivityFeedViewModel.NextBioCandidateId(
                  Friends.Select(friend => friend.Id),
                  now,
                  TimeSpan.FromMinutes(15));
        if (string.IsNullOrWhiteSpace(userId))
        {
            // Everything is fresh. VRCX pauses instead of spinning through the
            // whole list again immediately.
            _bioPollPausedUntil = now.AddMinutes(1);
            return;
        }

        _bioPollBusy = true;
        try
        {
            var profile = await _api.GetProfileAsync(userId, forceRefresh: true);
            if (!IsLoggedIn)
            {
                return;
            }

            if (profile is null || profile.Bio is null)
            {
                ActivityFeedViewModel.MarkBioChecked(userId, DateTimeOffset.UtcNow);
                return;
            }

            var friend = string.Equals(userId, CurrentUser.Id, StringComparison.Ordinal)
                ? CurrentUser
                : Friends.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, userId, StringComparison.Ordinal))?.User;
            if (friend is null)
            {
                ActivityFeedViewModel.MarkBioChecked(userId, DateTimeOffset.UtcNow);
                return;
            }

            friend.Bio = profile.Bio;
            ActivityFeedViewModel.ObserveProfileBio(
                friend,
                profile.Bio,
                DateTimeOffset.UtcNow);
            if (string.Equals(friend.Id, CurrentUser.Id, StringComparison.Ordinal))
            {
                CurrentUser.Bio = profile.Bio;
                OnPropertyChanged(nameof(BioText));
            }
        }
        catch (VrcApiException ex) when (ex.StatusCode == 429)
        {
            // VRChat is rate limiting profile reads. Back off for five minutes
            // rather than burning the remaining request budget.
            _bioPollPausedUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"profile bio scan {userId}: {ex.Message}");
            _bioPollPausedUntil = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        finally
        {
            _bioPollBusy = false;
        }
    }

    public async Task RefreshInBackgroundAsync()
    {
        if (!IsLoggedIn || IsBusy)
        {
            return;
        }

        // While the Activity page is open, refresh the complete friend list so
        // avatar changes made by offline friends are picked up too.
        if (string.Equals(SelectedSection.Key, "activity", StringComparison.Ordinal) &&
            DateTimeOffset.UtcNow - _lastFullFriendLoadAt > TimeSpan.FromMinutes(2))
        {
            await LoadAllFriendsAsync();
            return;
        }

        await RefreshDataAsync(refreshUser: true, includeOfflineFriends: false);
    }

    /// <summary>
    /// Downloads the offline half of the friend list once the UI is usable, so
    /// startup does not wait for one request per 100 offline friends. Pages are
    /// merged as they arrive.
    /// </summary>
    private async Task LoadRemainingFriendsAsync()
    {
        if (!IsLoggedIn || _loadingRemainingFriends)
        {
            return;
        }

        _loadingRemainingFriends = true;
        try
        {
            await LoadAllFriendsAsync();
        }
        catch (Exception ex)
        {
            // Keep whatever online friends are already on screen.
            Debug.WriteLine(ex);
        }
        finally
        {
            _loadingRemainingFriends = false;
        }
    }

    /// <summary>
    /// Fetches the complete friend list once and hands the same result to the
    /// friend list and the activity feed. Concurrent callers share this task,
    /// so the activity tab and Friend changes cannot observe separate snapshots.
    /// </summary>
    private async Task LoadAllFriendsAsync()
    {
        if (_fullFriendLoad is { } inFlight)
        {
            await inFlight;
            return;
        }

        var task = LoadAllFriendsCoreAsync();
        _fullFriendLoad = task;
        try
        {
            await task;
        }
        finally
        {
            if (ReferenceEquals(_fullFriendLoad, task))
            {
                _fullFriendLoad = null;
            }
        }
    }

    private async Task LoadAllFriendsCoreAsync()
    {
        var friends = await _api.GetFriendsAsync(
            forceRefresh: true,
            onPartial: partial =>
            {
                if (!IsLoggedIn)
                {
                    return;
                }

                FriendAvatarStore.Shared.ObserveAll(CurrentUser?.Id, partial);
                UpdateFriends(partial, keepMissing: true);
            });
        if (!IsLoggedIn)
        {
            return;
        }

        FriendAvatarStore.Shared.ObserveAll(CurrentUser?.Id, friends);
        UpdateFriends(friends);
        _friendChanges.Queue(friends);
        ActivityFeedViewModel?.ApplyFriends(friends);
        _lastFullFriendLoadAt = DateTimeOffset.UtcNow;
        _lastUpdated = DateTimeOffset.Now;
        OnPropertyChanged(nameof(LastUpdatedText));
        _ = LoadFriendCosmeticsAsync();
    }

    /// <summary>
    /// Decoration ids are resolved from VRChat's cosmetic index once, then the
    /// already-visible friend rows and open profile are repainted.
    /// </summary>
    private async Task LoadFriendCosmeticsAsync()
    {
        if (!CosmeticCatalog.IsLoaded)
        {
            try
            {
                await CosmeticCatalog.EnsureLoadedAsync(_api);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return;
            }
        }

        if (_disposed)
        {
            return;
        }

        foreach (var friend in Friends)
        {
            friend.RefreshPresentation();
        }
        foreach (var result in PlayerSearchResults)
        {
            result.RefreshPresentation();
        }

        SelectedFriendDetail?.RefreshCosmetics();
        OnPropertyChanged(nameof(IconFrameUrl));
        OnPropertyChanged(nameof(IconFrameIntroUrl));
        OnPropertyChanged(nameof(HasIconFrame));
        OnPropertyChanged(nameof(ProfileEffectUrl));
        OnPropertyChanged(nameof(ProfileEffectIntroUrl));
        OnPropertyChanged(nameof(HasProfileEffect));
        OnPropertyChanged(nameof(TrustBrush));
    }

    private void OnCosmeticCatalogChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            _ = LoadFriendCosmeticsAsync();
            return;
        }

        _ = dispatcher.BeginInvoke(new Action(() => _ = LoadFriendCosmeticsAsync()));
    }

    /// <summary>
    /// Runs on the UI dispatcher after the coalescer has verified a list delta.
    /// The coalescer advances its baseline only after this method completes.
    /// </summary>
    private async Task PublishFriendChangesAsync(FriendListDelta verified)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            throw new InvalidOperationException("The main window dispatcher is not available.");
        }

        var detectedAt = DateTimeOffset.UtcNow;
        await dispatcher.InvokeAsync(() =>
        {
            foreach (var friend in verified.Added)
            {
                RecordFriendChange(friend, added: true, detectedAt, verified.DetectedAfterRelaunch);
            }

            foreach (var friend in verified.Removed)
            {
                RecordFriendChange(friend, added: false, detectedAt, verified.DetectedAfterRelaunch);
            }
        }, DispatcherPriority.Background);
    }

    private void RecordFriendChange(
        VrcUser friend,
        bool added,
        DateTimeOffset? timestamp = null,
        bool detectedAfterRelaunch = false)
    {
        var at = timestamp ?? DateTimeOffset.UtcNow;
        var lastRelationshipEvent = FriendChanges
            .Where(change => string.Equals(change.Friend.Id, friend.Id, StringComparison.Ordinal))
            .OrderByDescending(change => change.Timestamp)
            .FirstOrDefault();
        var duplicate = lastRelationshipEvent is not null &&
                        lastRelationshipEvent.IsAdded == added;

        if (!duplicate)
        {
            FriendChanges.Insert(
                0,
                new FriendChangeItem(new FriendItem(friend), added, at, detectedAfterRelaunch));
            while (FriendChanges.Count > 100)
            {
                FriendChanges.RemoveAt(FriendChanges.Count - 1);
            }
        }

        _friendChangesView.Refresh();
        OnPropertyChanged(nameof(FriendChangesSummary));
        OnPropertyChanged(nameof(HasNoFriendChanges));
        if (!duplicate)
        {
            ActivityFeedViewModel?.ReportFriendChange(
                friend,
                added,
                at,
                detectedAfterRelaunch);
        }
    }

    /// <summary>
    /// Starts the change detector from the last complete list this account had
    /// before the app closed. The next full refresh then verifies changes that
    /// happened in between through friendStatus.
    /// </summary>
    private void RestoreFriendChangeBaseline(string? accountId)
    {
        _friendChanges.Reset();
        _friendChanges.SetAccount(accountId);
        var snapshot = FriendSnapshotStore.Shared.Load(accountId);
        if (snapshot is not null)
        {
            _friendChanges.SeedBaseline(snapshot.Users);
        }
    }

    /// <summary>
    /// Rebuilds the friend-change popup from the persisted activity timeline,
    /// so both lists show the same events and timestamps after a relaunch.
    /// </summary>
    private void LoadFriendChangesFromHistory(IReadOnlyList<ActivityEntry> entries)
    {
        FriendChanges.Clear();
        foreach (var entry in entries
                     .Where(entry => entry.Type is ActivityType.FriendAdded or ActivityType.FriendRemoved)
                     .OrderByDescending(entry => entry.Timestamp)
                     .Take(100))
        {
            FriendChanges.Add(new FriendChangeItem(entry));
        }

        _friendChangesView.Refresh();
        OnPropertyChanged(nameof(FriendChangesSummary));
        OnPropertyChanged(nameof(HasNoFriendChanges));
    }

    private async Task RefreshDataAsync(bool refreshUser, bool includeOfflineFriends = true)
    {
        if (!IsLoggedIn)
        {
            return;
        }

        var loadOfflineAfterwards = false;
        PushBusy(refreshUser ? "Refreshing your VRChat data…" : "Loading your VRChat data…");
        try
        {
            if (refreshUser)
            {
                var result = await _api.GetCurrentUserAsync();
                if (result.User is null)
                {
                    SignOutLocally("Your VRChat session expired. Sign in again.");
                    _api.ClearSavedSession();
                    return;
                }

                ApplyCurrentUser(result.User);
            }

            // Only fast-changing data blocks the UI: current user, online friends
            // and notifications. Offline friends stream in after the overlay clears.
            var friends = await _api.GetFriendsAsync(
                forceRefresh: true,
                onlineOnly: true,
                onPartial: partial => UpdateFriends(partial, keepMissing: true));

            // Anyone missing from the online set has gone offline; demote them
            // instead of leaving a stale "Online" badge until the next full load.
            var onlineIds = new HashSet<string>(friends.Select(friend => friend.Id), StringComparer.Ordinal);
            foreach (var friend in Friends)
            {
                if (!onlineIds.Contains(friend.Id))
                {
                    friend.MarkOffline();
                }
            }

            var notifications = await _api.GetNotificationsAsync();

            UpdateFriends(friends, keepMissing: true);
            ActivityFeedViewModel?.ApplyOnlineFriends(friends);
            UpdateNotifications(notifications);

            _lastUpdated = DateTimeOffset.Now;
            OnPropertyChanged(nameof(LastUpdatedText));
            ClearError();

            loadOfflineAfterwards = includeOfflineFriends;
        }
        catch (VrcApiException ex) when (ex.StatusCode == 401)
        {
            SignOutLocally("Your VRChat session expired. Sign in again.");
            _api.ClearSavedSession();
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat. Check your internet connection.");
        }
        catch (TaskCanceledException)
        {
            ShowError("The request timed out. Try again.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Something went wrong while refreshing.");
        }
        finally
        {
            PopBusy();
        }

        if (loadOfflineAfterwards)
        {
            // Manual refresh must feel instant: render the online set now and
            // stream the offline pages in behind it instead of holding the
            // whole refresh open for one request per 100 offline friends.
            _ = LoadRemainingFriendsAsync();
        }
    }

    private async Task CompleteLoginAsync(VrcUser user)
    {
        ApplyCurrentUser(user);
        IsLoggedIn = true;
        _realtime.Start();
        IsTwoFactorRequired = false;
        TwoFactorCode = string.Empty;
        _twoFactorMethods.Clear();

        // A sign-in may belong to a different account than the previous session.
        ResetAccountScopedViewModels();

        if (RememberSession)
        {
            _api.SaveSession();
            MultiAccountViewModel?.RememberCurrentAccount(user);
        }
        else
        {
            _api.ClearSavedSession();
        }

        SelectedSection = _friendsNav;
        RaiseCommandStates();
        await RefreshDataAsync(refreshUser: false);
        if (ActivityFeedViewModel is { HasLoaded: false, IsBusy: false })
        {
            await ActivityFeedViewModel.LoadAsync();
        }
    }

    private async void OnAccountSessionSwitched(VrcUser user)
    {
        ApplyCurrentUser(user);
        IsLoggedIn = true;
        _realtime.Start();
        IsTwoFactorRequired = false;
        SelectedSection = _friendsNav;

        ResetAccountScopedViewModels();

        if (RememberSession)
        {
            _api.SaveSession();
        }
        else
        {
            _api.ClearSavedSession();
        }

        RaiseCommandStates();
        await RefreshDataAsync(refreshUser: false);
    }

    private async Task VerifyTwoFactorAsync()
    {
        var code = TwoFactorCode.Trim();
        if (code.Length == 0 || !IsTwoFactorRequired)
        {
            return;
        }

        ClearError();
        PushBusy("Verifying your two-factor code…");
        try
        {
            var user = await _api.VerifyTwoFactorAsync(code, _twoFactorMethod);
            await CompleteLoginAsync(user);
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat. Check your internet connection.");
        }
        catch (TaskCanceledException)
        {
            ShowError("The verification request timed out. Try again.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Something went wrong while verifying the code.");
        }
        finally
        {
            PopBusy();
        }
    }

    private async Task CancelTwoFactorAsync()
    {
        PushBusy("Cancelling sign-in…");
        try
        {
            await _api.LogoutAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            IsTwoFactorRequired = false;
            TwoFactorCode = string.Empty;
            _twoFactorMethods.Clear();
            ClearError();
            PopBusy();
        }
    }

    /// <summary>
    /// Clears the view models that hold per-account data (avatar library and
    /// favourites cache, groups, activity feed) so nothing carries over between
    /// accounts or survives a sign-out.
    /// </summary>
    private void ResetAccountScopedViewModels()
    {
        AvatarViewModel?.Reset();
        GroupViewModel?.Reset();
        ActivityFeedViewModel?.Reset();
        InstanceViewModel?.Reset();
        InstanceHistoryViewModel?.Close();
        ContinuityViewModel?.SetAccount(null);
        ContextViewModel?.SetAccount(null);
        _contextLogSignature = string.Empty;
        _contextLiveRefreshPending = false;
        _instanceHistoryRecorder?.Stop();
        FriendChanges.Clear();
        RestoreFriendChangeBaseline(CurrentUser?.Id);
        ActivityFeedViewModel?.SetAccount(CurrentUser?.Id);
        // Reset clears the feed's filter state; reapply the saved Activity
        // preferences so signing in again does not silently switch to All/online.
        ActivityFeedViewModel?.SetDefaultFilter(ActivityDefaultFilter);
        if (ActivityFeedViewModel is not null)
        {
            ActivityFeedViewModel.ShowOfflineFriends = ActivityShowOfflineFriends;
        }
        _friendChangesView.Refresh();
        OnPropertyChanged(nameof(FriendChangesSummary));
        OnPropertyChanged(nameof(HasNoFriendChanges));
    }

    private async Task LogoutAsync()
    {
        PushBusy("Signing out…");
        try
        {
            await _api.LogoutAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            SignOutLocally(null);
            PopBusy();
        }
    }

    private async Task SaveStatusAsync()
    {
        if (CurrentUser is null || SelectedStatus is null || IsBusy)
        {
            return;
        }

        var description = (StatusMessageInput ?? string.Empty).Trim();
        if (description.Length > 32)
        {
            description = description[..32];
        }

        PushBusy("Updating your status…");
        try
        {
            var updated = await _api.UpdateStatusAsync(SelectedStatus.Value, description);
            IsStatusDirty = false;
            ApplyCurrentUser(updated);
            ClearError();
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat. Check your internet connection.");
        }
        catch (TaskCanceledException)
        {
            ShowError("The request timed out. Try again.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Something went wrong while updating your status.");
        }
        finally
        {
            PopBusy();
        }
    }

    private async Task MarkNotificationSeenAsync(NotificationItem? item)
    {
        if (item is null || item.Seen || IsBusy)
        {
            return;
        }

        PushBusy("Marking notification as read…");
        try
        {
            await _api.MarkNotificationSeenAsync(item.Id, item.IsV2);
            item.MarkSeen();
            UpdateUnreadState();
            PersistNotifications();
            ClearError();
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat. Check your internet connection.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Couldn't mark that notification as read.");
        }
        finally
        {
            PopBusy();
        }
    }

    private async Task MarkAllReadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var unseen = Notifications.Where(notification => !notification.Seen).ToList();
        if (unseen.Count == 0)
        {
            return;
        }

        PushBusy($"Marking {unseen.Count} notification{(unseen.Count == 1 ? string.Empty : "s")} as read…");
        try
        {
            foreach (var notification in unseen)
            {
                await _api.MarkNotificationSeenAsync(notification.Id, notification.IsV2);
                notification.MarkSeen();
                UpdateUnreadState();
            }

            PersistNotifications();
            ClearError();
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat. Check your internet connection.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Couldn't mark every notification as read.");
        }
        finally
        {
            UpdateUnreadState();
            PopBusy();
        }
    }

    private async Task EnsureWorldsLoadedAsync()
    {
        if (_worldsLoaded || !IsLoggedIn || IsBusy || !string.IsNullOrWhiteSpace(WorldQuery))
        {
            return;
        }

        await SearchWorldsAsync(string.Empty);
    }

    public async Task SearchWorldsAsync(string? query)
    {
        if (!IsLoggedIn || IsBusy)
        {
            return;
        }

        if (query is not null)
        {
            WorldQuery = query;
        }

        var effectiveQuery = (WorldQuery ?? string.Empty).Trim();
        PushBusy(effectiveQuery.Length == 0 ? "Loading popular worlds…" : "Searching VRChat worlds…");
        try
        {
            var worlds = await _api.SearchWorldsAsync(effectiveQuery);

            Worlds.Clear();
            foreach (var world in worlds
                         .Select(world => new WorldItem(world)
                         {
                             IsFavorite = _favoriteWorlds.Contains(world.Id)
                         })
                         .OrderByDescending(world => world.IsFavorite))
            {
                Worlds.Add(world);
            }

            _worldsLoaded = true;
            WorldsSummary = effectiveQuery.Length == 0
                ? $"Popular public worlds · {Worlds.Count}"
                : $"{Worlds.Count} result{(Worlds.Count == 1 ? string.Empty : "s")} for \u201c{effectiveQuery}\u201d";
            WorldsEmptyMessage = Worlds.Count == 0
                ? effectiveQuery.Length == 0
                    ? "No popular worlds came back. Try a search."
                    : $"No worlds found for \u201c{effectiveQuery}\u201d."
                : string.Empty;
            OnPropertyChanged(nameof(HasNoWorlds));

            _ = LoadWorldImagesAsync(Worlds.ToList());
            ClearError();
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat. Check your internet connection.");
        }
        catch (TaskCanceledException)
        {
            ShowError("The world search timed out. Try again.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Something went wrong while searching worlds.");
        }
        finally
        {
            PopBusy();
        }
    }

    private async Task SearchSuggestionAsync(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        WorldQuery = query;
        await SearchWorldsAsync(query);
    }

    /// <summary>
    /// Header player lookup. A value beginning with usr_ is treated as an exact
    /// VRChat user ID; everything else is searched by display name or username.
    /// Results open in the same profile card used by the Friends tab.
    /// </summary>
    private async Task SearchPlayersAsync()
    {
        if (!IsLoggedIn || IsPlayerSearchBusy)
        {
            return;
        }

        var query = PlayerSearchQuery.Trim();
        if (query.Length == 0)
        {
            return;
        }

        IsPlayerSearchBusy = true;
        PlayerSearchSummary = "Searching VRChat…";
        try
        {
            var users = await _api.SearchUserListAsync(query, limit: 10);
            PlayerSearchResults.Clear();
            foreach (var user in users
                         .Where(user => !string.IsNullOrWhiteSpace(user.Id))
                         .GroupBy(user => user.Id, StringComparer.Ordinal)
                         .Select(group => group.First())
                         .Take(10))
            {
                PlayerSearchResults.Add(CreatePresenceAwareFriend(user));
            }

            _ = LoadFriendCosmeticsAsync();
            PlayerSearchSummary = PlayerSearchResults.Count == 0
                ? $"No players found for “{query}”."
                : $"{PlayerSearchResults.Count} player{(PlayerSearchResults.Count == 1 ? string.Empty : "s")} found";
            IsPlayerSearchOpen = true;
        }
        catch (VrcApiException ex)
        {
            PlayerSearchResults.Clear();
            PlayerSearchSummary = ex.Message;
            IsPlayerSearchOpen = true;
        }
        catch (HttpRequestException)
        {
            PlayerSearchResults.Clear();
            PlayerSearchSummary = "Could not reach VRChat. Check your internet connection.";
            IsPlayerSearchOpen = true;
        }
        catch (TaskCanceledException)
        {
            PlayerSearchResults.Clear();
            PlayerSearchSummary = "The player search timed out. Try again.";
            IsPlayerSearchOpen = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            PlayerSearchResults.Clear();
            PlayerSearchSummary = "Could not search VRChat players.";
            IsPlayerSearchOpen = true;
        }
        finally
        {
            IsPlayerSearchBusy = false;
        }
    }

    private async Task OpenPlayerSearchResultAsync(FriendItem? result)
    {
        if (result is null || IsBusy)
        {
            return;
        }

        IsPlayerSearchOpen = false;
        await LoadFriendDetailAsync(result, forceRefresh: true);
    }

    private void ClearPlayerSearch()
    {
        PlayerSearchQuery = string.Empty;
        PlayerSearchResults.Clear();
        PlayerSearchSummary = string.Empty;
        IsPlayerSearchOpen = false;
    }

    private async Task LoadWorldImagesAsync(IReadOnlyList<WorldItem> items)
    {
        var tasks = items.Select(async item =>
        {
            var image = await _images.LoadAsync(item.ThumbnailUrl, 360);
            if (image is not null)
            {
                item.Image = image;
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void OpenWorld(WorldItem? world)
    {
        if (world is null)
        {
            return;
        }

        _worldDialogCancellation?.Cancel();
        _worldDialogCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _worldDialogCancellation = cancellation;

        var dialog = new WorldDialogViewModel(world, _api, _images);
        WorldDialog = dialog;
        ClearInfo();
        _ = dialog.LoadAsync(forceRefresh: false, cancellation.Token);
    }

    private void CloseWorldDialog()
    {
        _worldDialogCancellation?.Cancel();
        _worldDialogCancellation?.Dispose();
        _worldDialogCancellation = null;
        WorldDialog = null;
    }

    private async Task RefreshWorldDialogAsync()
    {
        var dialog = WorldDialog;
        if (dialog is null)
        {
            return;
        }

        _worldDialogCancellation?.Cancel();
        _worldDialogCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _worldDialogCancellation = cancellation;
        await dialog.LoadAsync(forceRefresh: true, cancellation.Token);
    }

    private async Task CreateInstanceAsync()
    {
        var dialog = WorldDialog;
        if (dialog is null || dialog.IsCreating)
        {
            return;
        }

        ClearError();
        ClearInfo();
        var cancellation = _worldDialogCancellation?.Token ?? CancellationToken.None;
        var instance = await dialog.CreateInstanceAsync(cancellation);
        if (instance is null)
        {
            return;
        }

        ShowInfo($"{dialog.CreatedTitle}. Launch VRChat or share the invite link below.");
        LaunchCreatedInstanceCommand.RaiseCanExecuteChanged();
        CopyCreatedInstanceLinkCommand.RaiseCanExecuteChanged();
    }

    private void LaunchCreatedInstance()
    {
        var instance = WorldDialog?.CreatedInstance;
        if (instance is null)
        {
            return;
        }

        OpenLaunchChooser(instance.Location, $"{WorldDialog?.Name ?? "your"} instance");
    }

    private void CopyCreatedInstanceLink()
    {
        var link = WorldDialog?.CreatedShareLink;
        if (string.IsNullOrWhiteSpace(link))
        {
            return;
        }

        CopyText(link, "Invite link copied to your clipboard.");
    }

    private async Task ShowSenderProfileAsync(NotificationDetailViewModel? detail)
    {
        var senderId = detail?.Notification.SenderUserId;
        if (detail is null || string.IsNullOrWhiteSpace(senderId))
        {
            return;
        }

        if (SelectedFriendDetail is { } existing && string.Equals(existing.Id, senderId, StringComparison.Ordinal))
        {
            return;
        }

        // Carry the location the notification detail already fetched so the
        // profile card can offer Join, not just people on your friends list.
        var user = new VrcUser
        {
            Id = senderId,
            DisplayName = detail.SenderName,
            Username = detail.Notification.SenderUsername,
            Location = LaunchService.IsLocation(detail.UserLocation) ? detail.UserLocation : null
        };

        var profile = new FriendDetailViewModel(
            CreatePresenceAwareFriend(user),
            _api,
            _images,
            openGroup: OpenGroupInfoFromProfile,
            openAvatar: ShowFriendAvatar);
        profile.ToggleFriendshipCommand = ToggleFriendCommand;
        profile.ShowInstanceHistoryCommand = new RelayCommand(() => OpenInstanceHistoryFor(profile));

        if (InstanceViewModel?.Instance?.Location is { } instanceLocation &&
            (IsPlayerInCurrentInstance(senderId) ||
             SameInstanceLocation(user.Location, instanceLocation)))
        {
            profile.PinToInstance(instanceLocation, () => IsPlayerInCurrentInstance(profile.Id));
        }

        // One profile card for the whole app: the same view model and popup the
        // Friends tab uses, so the notification sender gets the identical card.
        SelectedFriendDetail = profile;
        await profile.LoadAsync(forceRefresh: false, CancellationToken.None);

        // A notification only knows who sent it, not where they are now, so pull
        // their live state; that is what makes Join appear for someone in a world.
        try
        {
            var live = await _api.GetUserAsync(senderId, forceRefresh: true);
            if (live is not null && string.Equals(SelectedFriendDetail?.Id, senderId, StringComparison.Ordinal))
            {
                profile.ApplyUser(live);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task JoinNotificationUserAsync(NotificationDetailViewModel? detail)
    {
        if (detail is null || !detail.CanJoinUser)
        {
            ShowError("VRChat is not showing a joinable world for that person.");
            return;
        }

        OpenLaunchChooser(detail.UserLocation, detail.SenderName);
        await Task.CompletedTask;
    }

    private void JoinWorldInstance(WorldInstanceItem? instance)
    {
        if (instance is null || !LaunchService.IsLocation(instance.Location))
        {
            return;
        }

        // Same desktop/VR choice as friends, so every join behaves the same.
        OpenLaunchChooser(instance.Location, WorldDialog?.Name ?? "this world");
    }

    /// <summary>
    /// The group chip on a world's instance row. The group popup already carries the
    /// membership state, join/request/leave and the group's own instances.
    /// </summary>
    private void OpenWorldInstanceGroup(WorldInstanceItem? instance)
    {
        if (instance?.GroupId is not { Length: > 0 } groupId)
        {
            return;
        }

        if (GroupViewModel is not null)
        {
            _ = GroupViewModel.OpenGroupInfoAsync(groupId);
            return;
        }

        // Guests can browse worlds without the group popup existing, so fall back to the web.
        try
        {
            LaunchService.OpenUrl($"https://vrchat.com/home/group/{groupId}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Couldn't open that group in your browser.");
        }
    }

    private void OpenWorldInBrowser(WorldItem? world)
    {
        if (world is null)
        {
            return;
        }

        try
        {
            LaunchService.OpenUrl(world.WebUrl);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Couldn't open that world in your browser.");
        }
    }

    private async Task QuickJoinAsync()
    {
        if (!IsLoggedIn || IsBusy)
        {
            return;
        }

        var input = QuickJoinInput.Trim();
        if (!InstanceLinkParser.TryParse(input, null, out var parsed, out var error))
        {
            ShowError(error);
            return;
        }

        ClearError();
        ClearInfo();

        if (parsed.Location is not null)
        {
            OpenLaunchChooser(parsed.Location, "this instance");
            return;
        }

        if (string.IsNullOrWhiteSpace(parsed.ShortName))
        {
            ShowError("That does not look like a VRChat instance link or short code.");
            return;
        }

        PushBusy("Resolving the instance short code…");
        try
        {
            var instance = await _api.GetInstanceByShortNameAsync(parsed.ShortName);
            if (instance is null)
            {
                ShowError($"No active instance was found for “{parsed.ShortName}”.");
                return;
            }

            var location = !string.IsNullOrWhiteSpace(instance.Location)
                ? instance.Location
                : $"{instance.WorldId}:{instance.InstanceId}";

            if (!LaunchService.IsLocation(location))
            {
                ShowError("VRChat returned an instance without a usable location.");
                return;
            }

            OpenLaunchChooser(location, "this instance");
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat to resolve that short code.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not resolve that instance short code.");
        }
        finally
        {
            PopBusy();
        }
    }

    private async Task JoinDialogInputAsync()
    {
        var dialog = WorldDialog;
        if (dialog is null)
        {
            return;
        }

        var input = dialog.JoinInstanceInput.Trim();
        if (!InstanceLinkParser.TryParse(input, dialog.WorldId, out var parsed, out var error))
        {
            dialog.ReportError(error);
            return;
        }

        dialog.ReportError(string.Empty);

        if (parsed.Location is not null)
        {
            OpenLaunchChooser(parsed.Location, dialog.Name);
            return;
        }

        if (string.IsNullOrWhiteSpace(parsed.ShortName))
        {
            dialog.ReportError("That does not look like a VRChat instance link or short code.");
            return;
        }

        PushBusy("Resolving the instance short code…");
        try
        {
            var instance = await _api.GetInstanceByShortNameAsync(parsed.ShortName);
            if (instance is null)
            {
                dialog.ReportError($"No active instance was found for “{parsed.ShortName}”.");
                return;
            }

            var location = !string.IsNullOrWhiteSpace(instance.Location)
                ? instance.Location
                : $"{instance.WorldId}:{instance.InstanceId}";

            if (!LaunchService.IsLocation(location))
            {
                dialog.ReportError("VRChat returned an instance without a usable location.");
                return;
            }

            OpenLaunchChooser(location, dialog.Name);
        }
        catch (VrcApiException ex)
        {
            dialog.ReportError(ex.Message);
        }
        catch (HttpRequestException)
        {
            dialog.ReportError("Could not reach VRChat to resolve that short code.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            dialog.ReportError("Could not resolve that instance short code.");
        }
        finally
        {
            PopBusy();
        }
    }

    private async Task AcceptFriendRequestAsync(NotificationItem? item)
    {
        if (item is null || IsBusy)
        {
            return;
        }

        PushBusy("Accepting the friend request…");
        try
        {
            await _api.AcceptFriendRequestAsync(item.Id);
            ResolveFriendRequest(item);
            ShowInfo("Friend request accepted.");
            await RefreshDataAsync(refreshUser: false);
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat to accept that request.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not accept that friend request.");
        }
        finally
        {
            PopBusy();
        }
    }

    private async Task AcceptGroupInviteAsync(NotificationItem? item)
    {
        if (item is null || !item.CanAcceptGroupInvite || IsBusy)
        {
            return;
        }

        var groupId = item.GroupId;
        var groupName = item.GroupName;
        PushBusy("Accepting the group invite…");
        try
        {
            await _api.JoinGroupAsync(groupId);
            await DismissGroupInviteAsync(item);
            ShowInfo($"Joined {groupName}.");

            // The notification is resolved, but the group itself opens in the
            // same popup as a group chip or an invitation row.
            if (GroupViewModel is not null)
            {
                await GroupViewModel.OpenGroupInfoAsync(groupId);
            }
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat to accept that group invite.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not accept that group invite.");
        }
        finally
        {
            PopBusy();
        }
    }

    private async Task DeclineGroupInviteAsync(NotificationItem? item)
    {
        if (item is null || !item.CanDeclineGroupInvite || IsBusy)
        {
            return;
        }

        var groupId = item.GroupId;
        PushBusy("Declining the group invite…");
        try
        {
            await _api.DeclineGroupInviteAsync(groupId);
            await DismissGroupInviteAsync(item);
            ShowInfo("Group invite declined.");
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat to decline that group invite.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not decline that group invite.");
        }
        finally
        {
            PopBusy();
        }
    }

    private async Task DismissGroupInviteAsync(NotificationItem item)
    {
        try
        {
            await _api.HideNotificationAsync(item.Id, item.IsV2);
        }
        catch (Exception ex)
        {
            // The membership change already succeeded; a stale notification
            // must not turn that into an error. The local cleared-id set keeps
            // it hidden even if VRChat's feed still returns it.
            Debug.WriteLine($"hide resolved group invite {item.Id}: {ex.Message}");
        }

        ClearPersistentNotification(item);
    }

    private async Task JoinInviteAsync(NotificationItem? item)
    {
        if (item is null || item.InviteLocation is null)
        {
            return;
        }

        OpenLaunchChooser(item.InviteLocation, item.SenderName);
        if (!IsBusy)
        {
            try
            {
                await _api.MarkNotificationSeenAsync(item.Id, item.IsV2);
                item.MarkSeen();
                UpdateUnreadState();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }

    private void LaunchLocation(string location, LaunchMode chosen)
    {
        _ = LaunchLocationAsync(location, chosen, ActiveLaunchArguments);
    }

    private async Task LaunchLocationAsync(string location, LaunchMode chosen, string extraArguments)
    {
        try
        {
            string? shortName = null;
            if (LaunchService.NeedsShortName(location))
            {
                try
                {
                    shortName = await _api.GetInstanceShortNameAsync(location);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"instance short name: {ex}");
                }
            }

            var outcome = await LaunchService.OpenLocationAsync(
                location,
                chosen,
                shortName,
                extraArguments);
            ClearError();
            if (outcome == LaunchOutcome.AttachedToRunningGame)
            {
                ShowInfo("Opened the instance inside the running VRChat client.");
                return;
            }

            if (outcome == LaunchOutcome.AttachFailed)
            {
                // Never cold-start a second VRChat process when one is already
                // running. VRChat's own fallback is a self-invite, which the
                // user accepts from inside the game.
                try
                {
                    await _api.InviteSelfAsync(location, shortName);
                    ShowInfo("VRChat is already running but did not accept the join link. A self-invite was sent; accept it in VRChat.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"self-invite fallback: {ex}");
                    ShowError("VRChat is running, but the join link and the self-invite both failed. Try joining from inside VRChat.");
                }
                return;
            }

            ShowInfo($"Opening VRChat in {chosen.ToString().ToLowerInvariant()} mode and joining the instance.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not launch VRChat. Make sure the game is installed and registered for vrchat:// links.");
        }
    }

    private void CopyText(string text, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            ShowInfo(successMessage);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not access the clipboard.");
        }
    }

    // --------------------------------------------------------- friend detail

    private async Task LoadFriendDetailAsync(
        FriendItem? friend,
        bool forceRefresh = false,
        string? instanceLocation = null)
    {
        _friendDetailCancellation?.Cancel();
        _friendDetailCancellation?.Dispose();
        _friendDetailCancellation = null;

        if (friend is null)
        {
            SelectedFriendDetail = null;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _friendDetailCancellation = cancellation;
        var isSelf = CurrentUser is not null &&
                     string.Equals(friend.Id, CurrentUser.Id, StringComparison.Ordinal);
        var detail = new FriendDetailViewModel(
            friend,
            _api,
            _images,
            isSelf: isSelf,
            openGroup: OpenGroupInfoFromProfile,
            openAvatar: ShowFriendAvatar);
        detail.ToggleFriendshipCommand = ToggleFriendCommand;
        detail.ShowInstanceHistoryCommand = new RelayCommand(() => OpenInstanceHistoryFor(detail));

        // A profile for someone standing in the instance currently on screen
        // must not jump to a stale private/offline/old friend-record location.
        var liveInstanceLocation = instanceLocation ?? InstanceViewModel?.Instance?.Location;
        if (liveInstanceLocation is { } location &&
            (instanceLocation is not null ||
             IsPlayerInCurrentInstance(friend.Id) ||
             SameInstanceLocation(friend.Location, location)))
        {
            // A profile opened directly from the live roster is known to be in
            // this instance. Keep that instance until the profile or instance
            // closes; roster refreshes must not unpin it for a frame and let a
            // stale friend record replace the world.
            detail.PinToInstance(
                location,
                instanceLocation is null ? () => IsPlayerInCurrentInstance(detail.Id) : null);
        }

        SelectedFriendDetail = detail;
        await detail.LoadAsync(forceRefresh, cancellation.Token);
    }

    private bool IsPlayerInCurrentInstance(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || InstanceViewModel?.Instance is not { } instance)
        {
            return false;
        }

        return InstanceViewModel.Players.Any(player =>
                   string.Equals(player.UserId, userId, StringComparison.Ordinal)) ||
               instance.Users.Any(user =>
                   string.Equals(user.Id, userId, StringComparison.Ordinal));
    }

    private static bool SameInstanceLocation(string? left, string? right)
    {
        if (!LaunchService.IsLocation(left) || !LaunchService.IsLocation(right))
        {
            return false;
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshFriendProfileAsync(FriendDetailViewModel? requested)
    {
        // Reload the card whose button was clicked, never whichever row happens
        // to be selected after a list refresh rebuilt the popup.
        var detail = requested ?? SelectedFriendDetail;
        if (detail is null || !ReferenceEquals(detail, SelectedFriendDetail))
        {
            return;
        }

        _friendDetailCancellation?.Cancel();
        _friendDetailCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _friendDetailCancellation = cancellation;
        await detail.LoadAsync(forceRefresh: true, cancellation.Token);
    }

    /// <summary>
    /// A profile avatar card and every other avatar surface open the same
    /// detail popup, so wear/favourite actions stay in one place.
    /// </summary>
    private void ShowFriendAvatar(Avatar avatar)
        => AvatarViewModel?.ShowAvatarDetails(avatar, returnToProfile: true);

    /// <summary>
    /// One action for the profile card: add, cancel, accept, or remove a
    /// friendship. The card's flags come from VRChat's friendStatus endpoint,
    /// not from guessing based on whether the row came from the friend list.
    /// </summary>
    private async Task ToggleFriendAsync(FriendDetailViewModel? detail)
    {
        if (detail is null || !detail.CanToggleFriendship || IsBusy)
        {
            return;
        }

        detail.IsFriendMenuOpen = false;

        var removing = detail.IsFriend;
        var cancelling = !removing && detail.HasOutgoingFriendRequest;
        var accepting = !removing && !cancelling && detail.HasIncomingFriendRequest;
        PushBusy(removing
            ? "Removing friend…"
            : cancelling
                ? "Cancelling friend request…"
                : accepting
                    ? "Accepting friend request…"
                    : "Sending friend request…");

        try
        {
            if (removing)
            {
                await _api.UnfriendAsync(detail.Id);
                detail.ApplyFriendshipState(false, false, false);
                RemoveFriendFromList(detail.Id);
                _friendChanges.RemoveFriend(detail.Id);
                RecordFriendChange(detail.Friend.User, added: false);
                ShowInfo($"{detail.DisplayName} was removed from your friends list.");
            }
            else if (cancelling)
            {
                await _api.DeleteFriendRequestAsync(detail.Id);
                detail.ApplyFriendshipState(false, false, false);
                ShowInfo("Friend request cancelled.");
            }
            else if (accepting)
            {
                var request = Notifications.FirstOrDefault(notification =>
                    notification.IsFriendRequest &&
                    notification.CanAcceptFriendRequest &&
                    string.Equals(notification.SenderUserId, detail.Id, StringComparison.Ordinal));
                if (request is null)
                {
                    ShowError("Open Notifications to accept this friend request.");
                    return;
                }

                await _api.AcceptFriendRequestAsync(request.Id);
                ResolveFriendRequest(request);
                detail.ApplyFriendshipState(true, false, false);
                _friendChanges.MarkFriend(detail.Friend.User);
                RecordFriendChange(detail.Friend.User, added: true);
                ShowInfo($"You are now friends with {detail.DisplayName}.");
            }
            else
            {
                await _api.SendFriendRequestAsync(detail.Id);
                detail.ApplyFriendshipState(false, true, false);
                ShowInfo($"Friend request sent to {detail.DisplayName}.");
            }

            ToggleFriendCommand.RaiseCanExecuteChanged();
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (System.Net.Http.HttpRequestException)
        {
            ShowError("Could not reach VRChat. Check your internet connection.");
        }
        catch (TaskCanceledException)
        {
            ShowError("The friend request timed out. Try again.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not update this friendship.");
        }
        finally
        {
            PopBusy();
        }
    }

    private void RemoveFriendFromList(string userId)
    {
        var friend = Friends.FirstOrDefault(item => string.Equals(item.Id, userId, StringComparison.Ordinal));
        if (friend is null)
        {
            return;
        }

        Friends.Remove(friend);
        RefreshFriendsView();
        PublishFriendDirectory();
    }

    private void ClearFriendSelection()
    {
        ProfileZIndex = 30;
        SelectedFriend = null;
        SelectedFriendDetail = null;
    }

    /// <summary>
    /// Opens the signed-in account in the same popup the friend profiles use,
    /// so the sidebar card is a shortcut to the full profile instead of nothing.
    /// </summary>
    private void OpenOwnProfile()
    {
        if (CurrentUser is null)
        {
            return;
        }

        SelectedFriend = null;
        var profile = new FriendDetailViewModel(
            new FriendItem(CurrentUser),
            _api,
            _images,
            FriendNotesStore.Shared,
            isSelf: true,
            openGroup: OpenGroupInfoFromProfile,
            openAvatar: ShowFriendAvatar);
        profile.ToggleFriendshipCommand = ToggleFriendCommand;
        profile.ShowInstanceHistoryCommand = new RelayCommand(() => OpenInstanceHistoryFor(profile));
        SelectedFriendDetail = profile;
        _ = profile.LoadAsync(forceRefresh: true, CancellationToken.None);
    }

    private async Task JoinFriendAsync(FriendItem? friend)
    {
        if (friend is null || !LaunchService.IsLocation(friend.Location))
        {
            ShowError("That friend is not in a joinable world right now.");
            return;
        }

        // Every join uses the one global launch profile.
        OpenLaunchChooser(friend.Location, friend.DisplayName);
        await Task.CompletedTask;
    }

    /// <summary>
    /// A reachable self-invite that does not depend on the old launch chooser.
    /// It always uses the live instance location and resolves VRChat's short
    /// name first, which private and non-public group instances require.
    /// </summary>
    private async Task SelfInviteFriendAsync(FriendItem? friend)
    {
        if (friend is null || !LaunchService.IsLocation(friend.Location) || IsBusy)
        {
            return;
        }

        PushBusy("Sending a self-invite…");
        try
        {
            await SelfInviteLocationAsync(friend.Location);
            ShowInfo($"Self-invite sent for {friend.DisplayName}'s instance. Accept it inside VRChat.");
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat to send the self-invite.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not send the self-invite. The instance may be closed.");
        }
        finally
        {
            PopBusy();
        }
    }

    private async Task SelfInviteLocationAsync(string location)
    {
        string? shortName = null;
        if (LaunchService.NeedsShortName(location))
        {
            try
            {
                shortName = await _api.GetInstanceShortNameAsync(location);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"instance short name: {ex}");
            }
        }

        await _api.InviteSelfAsync(location, shortName);
    }

    private void OpenLaunchChooser(string? location, string friendName)
    {
        _ = friendName;
        if (string.IsNullOrWhiteSpace(location))
        {
            LaunchClientInMode(DefaultLaunchMode);
            return;
        }

        LaunchLocation(location, DefaultLaunchMode);
    }

    private void CloseLaunchChoice()
    {
        _pendingLaunchLocation = null;
        _pendingLaunchFriendName = string.Empty;
        IsLaunchChooserOpen = false;
    }

    private void ChooseLaunchMode(string? key)
    {
        var mode = string.Equals(key, "desktop", StringComparison.OrdinalIgnoreCase)
            ? LaunchMode.Desktop
            : LaunchMode.Vr;

        // Picking a mode here becomes the app-wide default the Friends bar shows.
        DefaultLaunchMode = mode;

        var location = _pendingLaunchLocation;
        CloseLaunchChoice();

        if (location is null)
        {
            LaunchClientInMode(mode);
        }
        else
        {
            LaunchLocation(location, mode);
        }
    }

    private void LaunchClientInMode(LaunchMode mode)
    {
        try
        {
            if (LaunchService.IsGameRunning())
            {
                ShowInfo("VRChat is already running. Use Join to open an instance inside the running game.");
                return;
            }

            LaunchService.LaunchClient(mode, ActiveLaunchArguments);
            ClearError();
            ShowInfo(_activeLaunchProfile is null
                ? $"Opening VRChat in {mode.ToString().ToLowerInvariant()} mode."
                : $"Opening VRChat in {mode.ToString().ToLowerInvariant()} mode with {ActiveLaunchProfileName}.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not launch VRChat. Make sure the game is installed and registered for vrchat:// links.");
        }
    }

    private void SetDefaultLaunchMode(string? key)
    {
        DefaultLaunchMode = string.Equals(key, "desktop", StringComparison.OrdinalIgnoreCase)
            ? LaunchMode.Desktop
            : LaunchMode.Vr;
    }

    private void LoadLaunchProfiles()
    {
        LaunchProfiles.Clear();
        var defaultItem = new LaunchProfileItem(
            new LaunchProfile { Id = string.Empty, Name = "Default" },
            isDefault: true);
        LaunchProfiles.Add(defaultItem);

        _settings.LaunchProfiles ??= new List<LaunchProfile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in _settings.LaunchProfiles)
        {
            if (profile is null)
            {
                continue;
            }

            profile.Id ??= string.Empty;
            profile.Name ??= string.Empty;
            profile.Arguments ??= string.Empty;
            if (string.IsNullOrWhiteSpace(profile.Id) || !seen.Add(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
                seen.Add(profile.Id);
            }

            LaunchProfiles.Add(new LaunchProfileItem(profile));
        }

        _activeLaunchProfile = LaunchProfiles.FirstOrDefault(profile =>
            !profile.IsDefault &&
            string.Equals(profile.Id, _settings.ActiveLaunchProfileId, StringComparison.Ordinal));
        if (_activeLaunchProfile is null && !string.IsNullOrWhiteSpace(_settings.ActiveLaunchProfileId))
        {
            _settings.ActiveLaunchProfileId = null;
        }

        _selectedLaunchProfile = defaultItem;
        _launchProfileName = defaultItem.Name;
        _launchProfileArguments = string.Empty;
    }

    private void OpenAdvancedLaunch()
    {
        SelectedLaunchProfile = _activeLaunchProfile ?? LaunchProfiles[0];
        LaunchProfileError = string.Empty;
        IsAdvancedLaunchOpen = true;
    }

    private void CloseAdvancedLaunch()
    {
        IsAdvancedLaunchOpen = false;
        LaunchProfileError = string.Empty;
    }

    private void NewLaunchProfile()
    {
        var profile = new LaunchProfile
        {
            Name = UniqueLaunchProfileName("New profile"),
            Arguments = string.Empty
        };
        _settings.LaunchProfiles.Add(profile);
        var item = new LaunchProfileItem(profile);
        LaunchProfiles.Add(item);
        _settingsStore.Save();
        SelectedLaunchProfile = item;
        LaunchProfileError = string.Empty;
    }

    private void DuplicateLaunchProfile()
    {
        var source = SelectedLaunchProfile;
        if (source is null)
        {
            return;
        }

        var profile = new LaunchProfile
        {
            Name = UniqueLaunchProfileName(
                source.IsDefault ? "New profile" : $"{source.DisplayName} copy"),
            Arguments = source.Arguments
        };
        _settings.LaunchProfiles.Add(profile);
        var item = new LaunchProfileItem(profile);
        LaunchProfiles.Add(item);
        _settingsStore.Save();
        SelectedLaunchProfile = item;
        LaunchProfileError = string.Empty;
    }

    private void DeleteLaunchProfile()
    {
        var selected = SelectedLaunchProfile;
        if (selected is null || selected.IsDefault)
        {
            return;
        }

        var wasActive = ReferenceEquals(selected, _activeLaunchProfile);
        _settings.LaunchProfiles.Remove(selected.Profile);
        LaunchProfiles.Remove(selected);
        if (wasActive)
        {
            _activeLaunchProfile = null;
            _settings.ActiveLaunchProfileId = null;
            RaiseActiveLaunchProfileChanged();
        }

        SelectedLaunchProfile = LaunchProfiles[0];
        _settingsStore.Save();
    }

    private bool SaveSelectedLaunchProfile()
    {
        var selected = SelectedLaunchProfile;
        if (selected is null || selected.IsDefault)
        {
            LaunchProfileError = "The Default profile cannot be edited.";
            return false;
        }

        var name = LaunchProfileName.Trim();
        if (name.Length == 0)
        {
            LaunchProfileError = "Give this profile a name.";
            return false;
        }

        if (name.Length > 48)
        {
            LaunchProfileError = "Profile names are limited to 48 characters.";
            return false;
        }

        if (LaunchProfiles.Any(profile =>
                !profile.IsDefault &&
                !ReferenceEquals(profile, selected) &&
                string.Equals(profile.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            LaunchProfileError = "Another profile already has that name.";
            return false;
        }

        var arguments = LaunchProfileArguments.Trim();
        var error = LaunchService.ValidateExtraArguments(arguments);
        if (error is not null)
        {
            LaunchProfileError = error;
            return false;
        }

        selected.Name = name;
        selected.Arguments = arguments;
        LaunchProfileName = name;
        LaunchProfileArguments = arguments;
        LaunchProfileError = string.Empty;
        _settingsStore.Save();

        if (ReferenceEquals(selected, _activeLaunchProfile))
        {
            RaiseActiveLaunchProfileChanged();
        }

        return true;
    }

    private void UseSelectedLaunchProfile()
    {
        var selected = SelectedLaunchProfile;
        if (selected is null)
        {
            return;
        }

        if (selected.IsDefault)
        {
            SetActiveLaunchProfile(selected);
            CloseAdvancedLaunch();
            return;
        }

        if (!SaveSelectedLaunchProfile())
        {
            return;
        }

        SetActiveLaunchProfile(selected);
        CloseAdvancedLaunch();
    }

    private void SetActiveLaunchProfile(LaunchProfileItem item)
    {
        _activeLaunchProfile = item.IsDefault ? null : item;
        _settings.ActiveLaunchProfileId = _activeLaunchProfile?.Id;
        _settingsStore.Save();
        RaiseActiveLaunchProfileChanged();
    }

    private void RaiseActiveLaunchProfileChanged()
    {
        OnPropertyChanged(nameof(ActiveLaunchProfileName));
        OnPropertyChanged(nameof(ActiveLaunchProfileLabel));
        OnPropertyChanged(nameof(LaunchDefaultText));
        OnPropertyChanged(nameof(LaunchChoiceSubtitle));
    }

    private string UniqueLaunchProfileName(string baseName)
    {
        var name = baseName;
        var index = 2;
        while (LaunchProfiles.Any(profile =>
                   !profile.IsDefault &&
                   string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} {index++}";
        }

        return name;
    }

    private void NotifyLaunchProfileState()
    {
        OnPropertyChanged(nameof(CanEditLaunchProfile));
        OnPropertyChanged(nameof(CanDeleteLaunchProfile));
        OnPropertyChanged(nameof(CanSaveLaunchProfile));
        SaveLaunchProfileCommand.RaiseCanExecuteChanged();
        DeleteLaunchProfileCommand.RaiseCanExecuteChanged();
        DuplicateLaunchProfileCommand.RaiseCanExecuteChanged();
    }

    private void RememberActivityFilter(string filter)
    {
        if (string.Equals(_settings.ActivityDefaultFilter, filter, StringComparison.Ordinal))
        {
            return;
        }

        _settings.ActivityDefaultFilter = filter;
        _settingsStore.Save();
        OnPropertyChanged(nameof(ActivityDefaultFilter));
    }

    private string ActiveLaunchArguments => _activeLaunchProfile?.Arguments.Trim() ?? string.Empty;

    private void OpenVrChat() => LaunchClientInMode(DefaultLaunchMode);

    /// <summary>
    /// Rejoins the newest real instance recorded for this account. The history
    /// entry is the same one the instance-history UI uses, so private instances
    /// still go through the normal short-name and self-invite fallback path.
    /// </summary>
    private async Task RejoinLastWorldAsync()
    {
        if (!IsLoggedIn || CurrentUser is null || IsBusy)
        {
            return;
        }

        InstanceHistoryEntry? latest;
        try
        {
            latest = _instanceHistoryStore
                .ForAccount(CurrentUser.Id)
                .Where(entry => LaunchService.IsLocation(entry.Location))
                .OrderByDescending(entry => entry.LastSeenAt)
                .ThenByDescending(entry => entry.JoinedAt)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"rejoin history: {ex}");
            ShowError("Could not read your recent world history.");
            return;
        }

        if (latest is null)
        {
            ShowError("There is no recent VRChat world to rejoin yet.");
            return;
        }

        if (LaunchService.IsGameRunning())
        {
            // VRChat refuses a URL launch into the instance it is already in
            // (it opens the Worlds page instead), and it exposes no rejoin API.
            // Restarting the client is the only way to re-enter the instance
            // without requiring a click inside the game.
            ShowInfo("Restarting VRChat to rejoin the instance…");
            if (!await LaunchService.CloseGameAsync())
            {
                ShowError("VRChat did not close, so it could not rejoin automatically. Close VRChat and try again.");
                return;
            }

            // Give the old client a moment to release its URL handler and
            // launch pipe before starting the replacement.
            await Task.Delay(1200);
        }

        var worldName = string.IsNullOrWhiteSpace(latest.WorldName)
            ? "your last world"
            : latest.WorldName;
        ShowInfo($"Rejoining {worldName}…");
        await LaunchLocationAsync(latest.Location, DefaultLaunchMode, ActiveLaunchArguments);
    }

    // ------------------------------------------------------ instance history

    /// <summary>
    /// Keeps Context in step with VRChat when the account joins another world
    /// or instance, or when the local roster changes. Only an already-opened
    /// Context page refreshes, so log changes do not pull local history unless
    /// someone is using it.
    /// </summary>
    private void QueueContextLiveRefresh()
    {
        if (!IsLoggedIn || ContextViewModel is not { IsLoaded: true })
        {
            return;
        }

        if (_contextLiveRefreshRunning)
        {
            _contextLiveRefreshPending = true;
            return;
        }

        _ = RunContextLiveRefreshAsync();
    }

    private async Task RunContextLiveRefreshAsync()
    {
        _contextLiveRefreshRunning = true;
        try
        {
            do
            {
                _contextLiveRefreshPending = false;
                if (!IsLoggedIn || ContextViewModel is not { IsLoaded: true })
                {
                    return;
                }

                await ContextViewModel.RefreshAsync(
                    CurrentUser,
                    InstanceViewModel,
                    Friends.ToList());
            }
            while (_contextLiveRefreshPending && IsLoggedIn);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("context live refresh: " + ex);
        }
        finally
        {
            _contextLiveRefreshRunning = false;
        }
    }

    private async Task RefreshContextAsync()
    {
        if (ContextViewModel is null)
        {
            return;
        }

        // Context reads the same durable rows as Continuity. Load and hydrate
        // their world names first so today's sessions never fall back to an
        // "Unknown world" label when Continuity has not been opened yet.
        if (ContinuityViewModel is not null)
        {
            await ContinuityViewModel.EnsureLoadedAsync();
            await ContinuityViewModel.HydrateWorldNamesAsync();
        }

        await ContextViewModel.RefreshAsync(CurrentUser, InstanceViewModel, Friends.ToList());
    }

    private async Task SwitchHistoryAccountAsync(VrcUser user)
    {
        if (string.IsNullOrWhiteSpace(user.Id) ||
            string.Equals(_historyAccountId, user.Id, StringComparison.Ordinal))
        {
            return;
        }

        _historyAccountId = user.Id;
        _historyReady = false;
        InstanceHistoryViewModel?.SetAccount(user);
        RecentPlayersViewModel?.SetAccount(user);

        try
        {
            var imported = await Task.Run(() =>
                new InstanceHistoryLogImporter().Import(user.Id, user.DisplayName));
            if (!string.Equals(_historyAccountId, user.Id, StringComparison.Ordinal))
            {
                return;
            }

            // A log belongs to one VRChat run. If the game is not running, any
            // still-open imported row ended when the log last changed; closing
            // it there keeps a crashed/closed client from looking live forever.
            if (!_logPlayerService.IsGameRunning)
            {
                _instanceHistoryStore.CloseOpenSessions(user.Id);

                foreach (var open in imported.Where(entry => entry.LeftAt is null))
                {
                    open.Close(open.LastSeenAt);
                }
            }

            _instanceHistoryStore.Merge(imported);
            _instanceHistoryStore.Save();
            _instanceHistoryRecorder?.SetAccount(user);
            _historyReady = true;

            if (InstanceHistoryViewModel is { IsOpen: true })
            {
                await InstanceHistoryViewModel.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _instanceHistoryRecorder?.SetAccount(user);
            _historyReady = true;
        }
    }

    private void ApplyCurrentLocationFromLog()
    {
        if (CurrentUser is null || !_logPlayerService.IsGameRunning)
        {
            return;
        }

        var snapshot = _logPlayerService.Poll();
        if (snapshot is not null && LaunchService.IsLocation(snapshot.Location))
        {
            ApplyObservedCurrentLocation(snapshot.Location, CurrentUser.Id);
        }

        ObserveContextLogSnapshot(snapshot);
    }

    /// <summary>
    /// The Instance view already refreshes when the log's location or roster
    /// changes. Reuse that exact signature for Context so a player joining the
    /// current instance updates it without a manual Refresh.
    /// </summary>
    private void ObserveContextLogSnapshot(VrChatLogSnapshot? snapshot)
    {
        if (InstanceViewModel is null || ContextViewModel is not { IsLoaded: true })
        {
            return;
        }

        var signature = InstanceViewModel.BuildLogSignature(snapshot);
        if (signature.Length == 0)
        {
            _contextLogSignature = string.Empty;
            return;
        }

        if (string.Equals(signature, _contextLogSignature, StringComparison.Ordinal))
        {
            return;
        }

        _contextLogSignature = signature;
        QueueContextLiveRefresh();
    }

    /// <summary>
    /// VRChat's log records every avatar switch it sees, including friends in
    /// the current instance whose friend-list row still has the old avatar.
    /// Feed those switches into Activity so it does not depend on the API.
    /// </summary>
    private void ApplyLoggedAvatarChanges(VrChatLogSnapshot snapshot)
    {
        if (!IsLoggedIn || CurrentUser is null)
        {
            return;
        }

        if (!string.Equals(snapshot.Location, _lastLoggedAvatarLocation, StringComparison.Ordinal))
        {
            _lastLoggedAvatarLocation = snapshot.Location;
            // Friend avatar names are instance-scoped, but the signed-in
            // account keeps its avatar across worlds. Preserve that baseline or
            // a switch made just after joining would be mistaken for the
            // starting state and silently skipped.
            var selfAvatar = CurrentUser is { Id: { Length: > 0 } selfId } self &&
                             _lastLoggedAvatars.TryGetValue(selfId, out var knownSelf)
                ? knownSelf
                : null;
            _lastLoggedAvatars.Clear();
            if (CurrentUser is { Id: { Length: > 0 } currentId } && selfAvatar is not null)
            {
                _lastLoggedAvatars[currentId] = selfAvatar;
            }
        }

        if (snapshot.AvatarSwitches is { Count: > 0 } selfSwitches &&
            !string.IsNullOrWhiteSpace(CurrentUser.DisplayName) &&
            selfSwitches.TryGetValue(CurrentUser.DisplayName.Trim(), out var selfSwitch))
        {
            ApplySelfLoggedAvatar(selfSwitch);
        }

        if (snapshot.Avatars is not { Count: > 0 } avatars)
        {
            return;
        }

        var userIdsByName = snapshot.Players
            .Where(player => !string.IsNullOrWhiteSpace(player.UserId) &&
                             !string.IsNullOrWhiteSpace(player.DisplayName))
            .GroupBy(player => player.DisplayName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().UserId, StringComparer.Ordinal);

        foreach (var (key, logged) in avatars)
        {
            if (logged is null || string.IsNullOrWhiteSpace(logged.Name))
            {
                continue;
            }

            var displayName = key.StartsWith("name:", StringComparison.Ordinal)
                ? key["name:".Length..]
                : null;
            var userId = key.StartsWith("usr_", StringComparison.Ordinal)
                ? key
                : displayName is not null
                    ? userIdsByName.GetValueOrDefault(displayName)
                    : null;
            if (string.IsNullOrWhiteSpace(userId) && displayName is not null)
            {
                // Older logs omit the usr_ id on some join rows. The friend
                // list is authoritative for the display name in that case.
                userId = Friends.FirstOrDefault(candidate =>
                    string.Equals(candidate.User.DisplayName, displayName, StringComparison.Ordinal))?.Id;
            }

            if (string.IsNullOrWhiteSpace(userId))
            {
                continue;
            }

            if (!_lastLoggedAvatars.TryGetValue(userId, out var previousName))
            {
                // The first snapshot is the state when this app started. It is
                // not a change, so do not flood Activity with old switches.
                _lastLoggedAvatars[userId] = logged.Name;
                continue;
            }

            if (string.Equals(previousName, logged.Name, StringComparison.Ordinal))
            {
                continue;
            }

            _lastLoggedAvatars[userId] = logged.Name;
            var friend = Friends.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, userId, StringComparison.Ordinal));
            if (friend is null)
            {
                continue;
            }

            ActivityFeedViewModel?.ApplyLoggedAvatarChange(
                friend.User,
                logged.Name,
                logged.AuthorName);
        }
    }

    private void ApplySelfLoggedAvatar(LoggedAvatar logged)
    {
        if (CurrentUser is null || string.IsNullOrWhiteSpace(logged.Name))
        {
            return;
        }

        if (!_lastLoggedAvatars.TryGetValue(CurrentUser.Id, out var previousName))
        {
            _lastLoggedAvatars[CurrentUser.Id] = logged.Name;
            return;
        }

        if (string.Equals(previousName, logged.Name, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedAvatars[CurrentUser.Id] = logged.Name;
        _lastLocalAvatarSwitchAt = DateTimeOffset.UtcNow;
        var known = AvatarLookupCache.FindByName(logged.Name);
        var avatarId = known?.Id ?? string.Empty;
        var image = !string.IsNullOrWhiteSpace(known?.ThumbnailImageUrl)
            ? known.ThumbnailImageUrl
            : known?.ImageUrl ?? string.Empty;

        CurrentUser.CurrentAvatar = avatarId;
        CurrentUser.CurrentAvatarName = logged.Name;
        CurrentUser.CurrentAvatarThumbnailImageUrl = image;
        CurrentUser.CurrentAvatarImageUrl = image;
        ActivityFeedViewModel?.ApplySelfLoggedAvatarChange(
            CurrentUser,
            logged.Name,
            avatarId,
            image,
            logged.AuthorName);
        OnAllPropertiesChanged();
    }

    private void PollInstanceHistory()
    {
        if (!IsLoggedIn || CurrentUser is null)
        {
            return;
        }

        var gameRunning = _logPlayerService.IsGameRunning;
        var snapshot = gameRunning ? _logPlayerService.Poll() : null;
        if (snapshot is not null && LaunchService.IsLocation(snapshot.Location))
        {
            ApplyObservedCurrentLocation(snapshot.Location, CurrentUser.Id);
            ApplyLoggedAvatarChanges(snapshot);
        }

        // Context uses the same log signature as Instance: location and the
        // people in it. A same-instance join or leave must refresh it too.
        ObserveContextLogSnapshot(snapshot);

        // The profile can be open while its subject switches avatars. Feed the
        // polled log change into that profile so its current card follows in
        // near real time instead of staying on the avatar it opened with.
        if (snapshot?.Avatars is { } loggedAvatars &&
            SelectedFriendDetail is { } profile &&
            loggedAvatars.TryGetValue(profile.Id, out var loggedAvatar))
        {
            profile.ApplyLoggedAvatar(loggedAvatar);
        }

        if (!_historyReady || _instanceHistoryRecorder is null)
        {
            return;
        }

        var changed = _instanceHistoryRecorder.Observe(
            snapshot,
            gameRunning,
            DateTimeOffset.Now);
        if (changed && InstanceHistoryViewModel is { IsOpen: true, IsListVisible: true })
        {
            _ = InstanceHistoryViewModel.RefreshAsync();
        }
    }

    private void OpenInstanceHistory()
    {
        if (!IsLoggedIn || InstanceHistoryViewModel is null)
        {
            return;
        }

        if (SelectedFriendDetail is { } profile)
        {
            profile.IsFriendMenuOpen = false;
        }

        ProfileZIndex = 30;
        InstanceHistoryViewModel.Show();
    }

    private Task OpenRecentPlayersAsync()
    {
        if (!IsLoggedIn || CurrentUser is null || RecentPlayersViewModel is null)
        {
            return Task.CompletedTask;
        }

        if (SelectedFriendDetail is { } profile)
        {
            profile.IsFriendMenuOpen = false;
        }

        RecentPlayersViewModel.Show(
            CurrentUser.Id,
            Friends.Select(friend => friend.Id));
        return Task.CompletedTask;
    }

    private void OpenContinuityPerson(ContinuityPersonItem person)
    {
        if (string.IsNullOrWhiteSpace(person.UserId) || CurrentUser is null)
        {
            return;
        }

        ProfileZIndex = 34;
        SelectedFriend = null;
        var friend = Friends.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, person.UserId, StringComparison.Ordinal));
        var user = friend?.User ??
                   (person.IsCurrentUser && CurrentUser is not null
                       ? CurrentUser
                       : new VrcUser
                       {
                           Id = person.UserId,
                           DisplayName = person.DisplayName,
                           UserIcon = person.AvatarUrl
                       });
        _ = LoadFriendDetailAsync(new FriendItem(user), forceRefresh: true);
    }

    private void OpenContextPlayer(InstancePlayerItem player)
    {
        _ = OpenInstancePlayerAsync(player);
    }

    private void OpenContinuitySession(ContinuitySessionItem session)
    {
        if (ContinuityViewModel is null)
        {
            return;
        }

        var section = Sections.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, "continuity", StringComparison.Ordinal));
        if (section is not null)
        {
            SelectedSection = section;
        }

        ContinuityViewModel.ShowSession(session);
    }

    private async Task OpenContinuityWorldAsync(string? worldId)
    {
        if (!IsLoggedIn || string.IsNullOrWhiteSpace(worldId) || IsBusy)
        {
            return;
        }

        PushBusy("Loading that world…");
        try
        {
            var world = await _api.GetWorldAsync(worldId);
            if (world is null)
            {
                ShowError("VRChat did not return that world.");
                return;
            }

            OpenWorld(new WorldItem(world)
            {
                IsFavorite = _favoriteWorlds.Contains(world.Id)
            });
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat to load that world.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not load that world.");
        }
        finally
        {
            PopBusy();
        }
    }

    private void OpenRecentPlayer(RecentPlayerItem player)
    {
        if (!player.CanOpenProfile || CurrentUser is null)
        {
            return;
        }

        ProfileZIndex = 30;
        SelectedFriend = null;
        var friend = Friends.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, player.UserId, StringComparison.Ordinal));
        var user = friend?.User ?? new VrcUser
        {
            Id = player.UserId,
            DisplayName = player.DisplayName,
            UserIcon = player.AvatarUrl
        };
        _ = LoadFriendDetailAsync(new FriendItem(user), forceRefresh: true);
    }

    private async Task OpenRecentWorldAsync(RecentPlayerSessionItem session)
    {
        if (!IsLoggedIn || !session.CanOpenWorld || IsBusy)
        {
            return;
        }

        RecentPlayersViewModel?.Close();
        PushBusy("Loading that world…");
        try
        {
            var world = await _api.GetWorldAsync(session.WorldId);
            if (world is null)
            {
                ShowError("VRChat did not return that world.");
                return;
            }

            OpenWorld(new WorldItem(world)
            {
                IsFavorite = _favoriteWorlds.Contains(world.Id)
            });
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat to load that world.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not load that world.");
        }
        finally
        {
            PopBusy();
        }
    }

    internal void OpenInstanceHistoryFor(FriendDetailViewModel profile)
    {
        if (!IsLoggedIn || InstanceHistoryViewModel is null)
        {
            return;
        }

        profile.IsFriendMenuOpen = false;
        ProfileZIndex = 30;
        if (profile.IsSelf)
        {
            InstanceHistoryViewModel.Show();
            InstanceHistoryViewModel.CanReturnToProfile = true;
            return;
        }

        InstanceHistoryViewModel.ShowForUser(profile.Id, profile.DisplayName);
        InstanceHistoryViewModel.CanReturnToProfile = true;
    }

    internal void OpenHistoryLaunch(InstanceHistoryItem item)
    {
        if (!LaunchService.IsLocation(item.Entry.Location))
        {
            return;
        }

        LaunchLocation(item.Entry.Location, DefaultLaunchMode);
    }

    private void OpenHistoryInGame(InstanceHistoryItem item)
    {
        if (!LaunchService.IsLocation(item.Entry.Location))
        {
            return;
        }

        LaunchLocation(item.Entry.Location, DefaultLaunchMode);
    }

    internal void OpenHistoryPlayer(InstanceHistoryPlayerItem player)
    {
        if (string.IsNullOrWhiteSpace(player.UserId))
        {
            return;
        }

        ProfileZIndex = 34;
        if (player.IsSelf && CurrentUser is not null)
        {
            OpenOwnProfile();
            return;
        }

        var user = new VrcUser
        {
            Id = player.UserId,
            DisplayName = player.DisplayName,
            UserIcon = player.AvatarUrl
        };
        SelectedFriend = null;
        _ = LoadFriendDetailAsync(new FriendItem(user), forceRefresh: true);
    }

    private async Task SelfInvitePendingLaunchAsync()
    {
        var location = _pendingLaunchLocation;
        if (location is null || IsBusy)
        {
            return;
        }

        CloseLaunchChoice();
        PushBusy("Sending a self-invite…");
        try
        {
            await SelfInviteLocationAsync(location);
            ShowInfo("Self-invite sent. Check VRChat's notifications to join this instance.");
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat to send the self-invite.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not send the self-invite. The instance may be closed.");
        }
        finally
        {
            PopBusy();
        }
    }

    /// <summary>
    /// Opens one small choice surface for both profile actions. The user picks
    /// exactly one direction (invite them here or request their invite) and can
    /// add an optional 64-character message.
    /// </summary>
    private void OpenInviteComposer(
        FriendItem? friend,
        string? requestedMode = null,
        NotificationItem? acceptedRequest = null)
    {
        if (friend is null || !IsLoggedIn || IsBusy)
        {
            return;
        }

        _inviteComposerFriend = friend;
        _inviteComposerRequestNotification = acceptedRequest;
        InviteComposerMessage = string.Empty;
        ComposerError = string.Empty;
        IsInviteComposerOpen = true;
        ApplyCurrentLocationFromLog();
        var canInvite = LaunchService.IsLocation(CurrentUser?.Location);
        _suppressInviteModeLoad = true;
        InviteComposerMode = string.Equals(requestedMode, "request", StringComparison.Ordinal)
            ? "request"
            : string.Equals(requestedMode, "invite", StringComparison.Ordinal)
                ? "invite"
                : canInvite ? "invite" : "request";
        _suppressInviteModeLoad = false;
        _ = LoadInviteMessagesAsync(InviteComposerMode);
        OnPropertyChanged(nameof(InviteComposerTitle));
        OnPropertyChanged(nameof(InviteComposerSubtitle));
        OnPropertyChanged(nameof(HasInviteComposerLocationHint));
        SendInviteChoiceCommand.RaiseCanExecuteChanged();

        // Only refresh when the current location is missing. If the app already
        // has a live location there is no reason to spend another API request.
        if (!canInvite)
        {
            _ = RefreshInviteComposerStateAsync();
        }
    }

    private void CloseInviteComposer()
    {
        _inviteComposerFriend = null;
        _inviteComposerRequestNotification = null;
        IsInviteComposerOpen = false;
        InviteComposerMessage = string.Empty;
        ComposerError = string.Empty;
        _inviteMessagesLoadVersion++;
        InviteMessages.Clear();
        InviteMessagesStatus = string.Empty;
        SelectedInviteMessage = null;
        SendInviteChoiceCommand.RaiseCanExecuteChanged();
    }

    private Task LoadInviteMessagesAsync(string mode)
    {
        var messageType = string.Equals(mode, "request", StringComparison.Ordinal) ? "request" : "message";
        return LoadComposerPresetsAsync(
            messageType,
            "Presets are shared across invites, requests, and replies. Select one to use or edit it.",
            "message",
            "request",
            "response",
            "requestResponse");
    }

    private Task LoadResponsePresetsAsync(NotificationItem item)
    {
        var messageType = item.IsInvite ? "response" : "requestResponse";
        return LoadComposerPresetsAsync(
            messageType,
            "Reply presets are shared across invites, requests, and replies. Select one to use or edit it.",
            "message",
            "request",
            "response",
            "requestResponse");
    }

    private async Task LoadComposerPresetsAsync(
        string messageType,
        string loadedStatus,
        params string[] messageTypes)
    {
        var version = ++_inviteMessagesLoadVersion;
        var userId = CurrentUser?.Id;
        InviteMessages.Clear();
        _inviteMessagesLoaded = false;
        _inviteMessagesLoadedType = string.Empty;
        SelectedInviteMessage = null;
        InviteMessagesStatus = string.Empty;
        OnPropertyChanged(nameof(InviteSlot11Summary));

        if (string.IsNullOrWhiteSpace(userId) || (!IsInviteComposerOpen && !IsResponseComposerOpen))
        {
            return;
        }

        IsInviteMessagesLoading = true;
        InviteMessagesStatus = "Loading presets\u2026";
        try
        {
            var targetMessages = await LoadInviteMessageListAsync(userId, messageType);
            var otherOptions = new List<InviteMessageOption>();
            foreach (var otherType in messageTypes
                         .Where(type => !string.Equals(type, messageType, StringComparison.Ordinal))
                         .Distinct(StringComparer.Ordinal))
            {
                var otherMessages = await LoadInviteMessageListAsync(userId, otherType);
                var otherBySlot = otherMessages
                    .Where(message => message.Slot is >= 0 and <= 11)
                    .GroupBy(message => message.Slot)
                    .ToDictionary(group => group.Key, group => group.First());
                OverlayRememberedMessage(otherBySlot, userId, otherType);
                otherOptions.AddRange(otherBySlot.Values
                    .Where(message => !string.IsNullOrWhiteSpace(message.Message))
                    .OrderBy(message => message.Slot)
                    .Select(message => new InviteMessageOption(
                        message,
                        PresetTypeLabel(otherType))));
            }

            if (version != _inviteMessagesLoadVersion ||
                (!IsInviteComposerOpen && !IsResponseComposerOpen))
            {
                return;
            }

            var targetBySlot = targetMessages
                .Where(message => message.Slot is >= 0 and <= 11)
                .GroupBy(message => message.Slot)
                .ToDictionary(group => group.Key, group => group.First());

            OverlayRememberedMessage(targetBySlot, userId, messageType);

            var targetOptions = new List<InviteMessageOption>();
            var targetLabel = PresetTypeLabel(messageType);
            for (var slot = 0; slot <= 11; slot++)
            {
                if (!targetBySlot.TryGetValue(slot, out var message))
                {
                    message = new InviteMessage
                    {
                        Slot = slot,
                        MessageType = messageType,
                        CanBeUpdated = true
                    };
                }

                targetOptions.Add(new InviteMessageOption(message, targetLabel));
            }

            // Saved presets stay visible. Empty slots for the current action come
            // after them so a message written to a high slot is not hidden below
            // a wall of empty rows.
            var populated = targetOptions
                .Where(option => !string.IsNullOrWhiteSpace(option.Message))
                .Concat(otherOptions
                    .Where(option => !targetOptions.Any(target =>
                        string.Equals(target.Message, option.Message, StringComparison.Ordinal))))
                .ToList();
            var empty = targetOptions
                .Where(option => string.IsNullOrWhiteSpace(option.Message))
                .ToList();

            foreach (var option in populated.Concat(empty))
            {
                InviteMessages.Add(option);
            }

            _inviteMessagesLoaded = true;
            _inviteMessagesLoadedType = messageType;
            InviteMessagesStatus = loadedStatus;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"saved invite messages: {ex}");
            _inviteMessagesLoaded = false;
            if (version == _inviteMessagesLoadVersion)
            {
                InviteMessagesStatus = "Could not load presets. Custom text still works.";
            }
        }
        finally
        {
            if (version == _inviteMessagesLoadVersion)
            {
                IsInviteMessagesLoading = false;
                NotifyInvitePresetState();
            }
        }
    }

    private static string PresetTypeLabel(string messageType) => messageType switch
    {
        "request" => "Request",
        "response" => "Invite reply",
        "requestResponse" => "Request reply",
        _ => "Invite"
    };

    private async Task<IReadOnlyList<InviteMessage>> LoadInviteMessageListAsync(
        string userId,
        string messageType)
    {
        try
        {
            return await _api.GetInviteMessagesAsync(userId, messageType);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"saved invite messages {messageType}: {ex}");
            return Array.Empty<InviteMessage>();
        }
    }

    private void OverlayRememberedMessage(
        Dictionary<int, InviteMessage> bySlot,
        string userId,
        string messageType)
    {
        var remembered = _inviteMessageMemory.GetEntry(userId, messageType);
        if (remembered is null || string.IsNullOrWhiteSpace(remembered.Text))
        {
            return;
        }

        if (bySlot.TryGetValue(remembered.Slot, out var existing))
        {
            if (string.IsNullOrWhiteSpace(existing.Message))
            {
                existing.Message = remembered.Text;
            }

            return;
        }

        bySlot[remembered.Slot] = new InviteMessage
        {
            Slot = remembered.Slot,
            MessageType = messageType,
            Message = remembered.Text,
            CanBeUpdated = true
        };
    }

    private async Task RefreshInviteComposerStateAsync()
    {
        try
        {
            var result = await _api.GetCurrentUserAsync();
            if (result.User is not null)
            {
                ApplyCurrentUser(result.User);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"invite composer location refresh: {ex}");
        }
        finally
        {
            OnPropertyChanged(nameof(InviteComposerSubtitle));
            OnPropertyChanged(nameof(HasInviteComposerLocationHint));
        }
    }

    private async Task SendInviteChoiceAsync(string? mode)
    {
        var friend = _inviteComposerFriend;
        if (friend is null || IsBusy)
        {
            return;
        }

        var requestInstead = string.Equals(mode, "request", StringComparison.Ordinal);
        PushBusy(requestInstead
            ? $"Asking {friend.DisplayName} for an invite…"
            : $"Inviting {friend.DisplayName}…");
        try
        {
            var message = InviteComposerMessage.Trim();
            var plan = new InviteMessageSlotPlan(0);

            if (!requestInstead)
            {
                var location = await GetCurrentLocationForInviteAsync();
                if (location is null)
                {
                    ComposerError = "You are not in a world. Join one first, or request their invite instead.";
                    OnPropertyChanged(nameof(HasInviteComposerLocationHint));
                    return;
                }

                try
                {
                    plan = await PrepareInviteMessageSlotAsync("message", message);
                }
                catch (VrcApiException ex) when (ex.StatusCode == 429)
                {
                    plan = new InviteMessageSlotPlan(0, true);
                }

                await _api.InviteFriendAsync(friend.Id, location, plan.Slot);
                if (_inviteComposerRequestNotification is { } acceptedRequest)
                {
                    try
                    {
                        await _api.HideNotificationAsync(acceptedRequest.Id, acceptedRequest.IsV2);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"hide accepted invite request: {ex}");
                    }

                    ClearPersistentNotification(acceptedRequest);
                }

                ShowInfo(DescribeInviteSend(friend.DisplayName, requestInstead, plan));
            }
            else
            {
                try
                {
                    plan = await PrepareInviteMessageSlotAsync("request", message);
                }
                catch (VrcApiException ex) when (ex.StatusCode == 429)
                {
                    plan = new InviteMessageSlotPlan(0, true);
                }

                await _api.RequestInviteAsync(friend.Id, plan.Slot);
                ShowInfo(DescribeInviteSend(friend.DisplayName, requestInstead, plan));
            }

            CloseInviteComposer();
        }
        catch (VrcApiException ex)
        {
            ComposerError = ex.StatusCode == 429
                ? "VRChat allows invite messages to change only once an hour. Try again later or send without a message."
                : ex.Message;
        }
        catch (HttpRequestException)
        {
            ComposerError = requestInstead
                ? "Could not reach VRChat to request an invite."
                : "Could not reach VRChat to send that invite.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ComposerError = requestInstead ? "Could not request an invite." : "Could not send that invite.";
        }
        finally
        {
            PopBusy();
        }
    }

    private sealed record InviteMessageSlotPlan(
        int Slot,
        bool SkippedCustomText = false,
        bool UsedSavedPreset = false);

    private static string DescribeInviteSend(
        string displayName,
        bool requestInstead,
        InviteMessageSlotPlan plan)
    {
        var sent = requestInstead
            ? $"Invite request sent to {displayName}."
            : $"Invite sent to {displayName}.";
        if (plan.UsedSavedPreset)
        {
            return sent + " The edited preset is cooling down, so its saved text was sent instead.";
        }

        return plan.SkippedCustomText
            ? sent + " VRChat's message slots are still cooling down, so the default message was used."
            : sent;
    }

    /// <summary>
    /// Reuses a matching preset, edits the selected preset, or writes the text
    /// into the next slot that is not on cooldown. This follows the slot flow
    /// used by VRCX and VRCNext rather than forcing every message into slot 11.
    /// </summary>
    private async Task<InviteMessageSlotPlan> PrepareInviteMessageSlotAsync(
        string messageType,
        string? message)
    {
        var text = (message ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new InviteMessageSlotPlan(0);
        }

        var currentUserId = CurrentUser?.Id;
        if (string.IsNullOrWhiteSpace(currentUserId))
        {
            throw new InvalidOperationException("The signed-in VRChat account is unavailable.");
        }

        if (text.Length > 64)
        {
            text = text[..64];
        }

        var selected = SelectedInviteMessage;
        if (selected is not null &&
            string.Equals(selected.MessageType, messageType, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(selected.Message, text, StringComparison.Ordinal))
            {
                return new InviteMessageSlotPlan(selected.Slot);
            }

            if (selected.HasCooldown)
            {
                return new InviteMessageSlotPlan(selected.Slot, true, true);
            }

            await SaveInvitePresetCoreAsync(selected, text);
            return new InviteMessageSlotPlan(selected.Slot);
        }

        var matching = InviteMessages.FirstOrDefault(option =>
            string.Equals(option.MessageType, messageType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.Message, text, StringComparison.Ordinal));
        if (matching is not null)
        {
            return new InviteMessageSlotPlan(matching.Slot);
        }

        var loadedForType = _inviteMessagesLoaded &&
            string.Equals(_inviteMessagesLoadedType, messageType, StringComparison.OrdinalIgnoreCase);
        var remembered = _inviteMessageMemory.GetEntry(currentUserId, messageType);
        if (remembered is not null &&
            string.Equals(remembered.Text, text, StringComparison.Ordinal) &&
            (!loadedForType ||
             string.Equals(
                 InviteMessages.FirstOrDefault(option => option.Slot == remembered.Slot)?.Message,
                 text,
                 StringComparison.Ordinal)))
        {
            return new InviteMessageSlotPlan(remembered.Slot);
        }

        if (!loadedForType)
        {
            // The preset list could not be loaded. Try the conventional custom
            // slot, and let the caller retry without text if VRChat returns 429.
            await _api.UpdateInviteMessageAsync(currentUserId, messageType, 11, text);
            ApplySavedInviteMessage(11, messageType, text);
            return new InviteMessageSlotPlan(11);
        }

        var free = InviteMessages
            .Where(option =>
                string.Equals(option.MessageType, messageType, StringComparison.OrdinalIgnoreCase) &&
                !option.HasCooldown &&
                option.CanBeUpdated)
            .OrderByDescending(option => option.Slot)
            .FirstOrDefault();
        if (free is null)
        {
            return new InviteMessageSlotPlan(0, true);
        }

        await SaveInvitePresetCoreAsync(free, text);
        return new InviteMessageSlotPlan(free.Slot);
    }

    private async Task SaveInvitePresetCoreAsync(InviteMessageOption option, string text)
    {
        var currentUserId = CurrentUser?.Id;
        if (string.IsNullOrWhiteSpace(currentUserId))
        {
            throw new InvalidOperationException("The signed-in VRChat account is unavailable.");
        }

        var messageType = option.MessageType;
        await _api.UpdateInviteMessageAsync(currentUserId, messageType, option.Slot, text);
        ApplySavedInviteMessage(option.Slot, messageType, text);
    }

    private void ApplySavedInviteMessage(int slot, string messageType, string text)
    {
        var currentUserId = CurrentUser?.Id;
        var index = -1;
        InviteMessageOption? existing = null;
        for (var i = 0; i < InviteMessages.Count; i++)
        {
            if (InviteMessages[i].Slot == slot &&
                string.Equals(InviteMessages[i].MessageType, messageType, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                existing = InviteMessages[i];
                break;
            }
        }

        var updated = new InviteMessageOption(
            new InviteMessage
            {
                Slot = slot,
                MessageType = messageType,
                Message = text,
                CanBeUpdated = false,
                RemainingCooldownMinutes = 60,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            existing?.TypeLabel ?? PresetTypeLabel(messageType));
        if (index >= 0)
        {
            InviteMessages[index] = updated;
        }

        if (!string.IsNullOrWhiteSpace(currentUserId))
        {
            _inviteMessageMemory.Set(currentUserId, messageType, text, slot);
        }

        if (_selectedInviteMessage is not null &&
            _selectedInviteMessage.Slot == slot &&
            string.Equals(_selectedInviteMessage.MessageType, messageType, StringComparison.OrdinalIgnoreCase))
        {
            _selectedInviteMessage = updated;
            OnPropertyChanged(nameof(SelectedInviteMessage));
        }

        NotifyInvitePresetState();
    }

    private async Task SaveInvitePresetAsync()
    {
        var selected = SelectedInviteMessage;
        if (selected is null || IsBusy || !CanSaveInvitePreset)
        {
            return;
        }

        PushBusy("Saving preset…");
        try
        {
            ComposerError = string.Empty;
            await SaveInvitePresetCoreAsync(selected, InviteComposerMessage.Trim());
            InviteMessagesStatus = $"Slot {selected.Slot} saved. It will be used for this message.";
        }
        catch (VrcApiException ex)
        {
            ComposerError = ex.StatusCode == 429
                ? "That preset is on cooldown. Pick a slot that is ready, or send the saved text as-is."
                : ex.Message;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"save invite preset: {ex}");
            ComposerError = "Could not save that preset.";
        }
        finally
        {
            PopBusy();
            NotifyInvitePresetState();
        }
    }

    private async Task<string?> GetCurrentLocationForInviteAsync()
    {
        if (LaunchService.IsLocation(CurrentUser?.Location))
        {
            return CurrentUser!.Location;
        }

        try
        {
            var result = await _api.GetCurrentUserAsync();
            if (result.User is not null)
            {
                ApplyCurrentUser(result.User);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"current location refresh: {ex}");
        }

        return LaunchService.IsLocation(CurrentUser?.Location) ? CurrentUser!.Location : null;
    }

    private async Task OpenAcceptInviteRequestAsync(NotificationItem? item)
    {
        if (item is null || !item.CanAcceptInviteRequest || IsBusy)
        {
            return;
        }

        var location = await GetCurrentLocationForInviteAsync();
        if (location is null)
        {
            ShowError("Join a world first, then accept invite requests.");
            return;
        }

        var friend = new FriendItem(new VrcUser
        {
            Id = item.SenderUserId,
            DisplayName = item.SenderName
        });
        OpenInviteComposer(friend, "invite", item);
    }

    private async Task DeclineNotificationAsync(NotificationItem? item)
    {
        if (item is null || !item.CanDecline || IsBusy)
        {
            return;
        }

        PushBusy("Declining notification…");
        try
        {
            await _api.HideNotificationAsync(item.Id, item.IsV2);
            ClearPersistentNotification(item);
            ShowInfo(item.IsInvite ? "Invite declined." : "Invite request declined.");
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat to decline that notification.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not decline that notification.");
        }
        finally
        {
            PopBusy();
        }
    }

    private void OpenResponseComposer(NotificationItem? item)
    {
        if (item is null || !item.CanDecline || IsBusy)
        {
            return;
        }

        _responseComposerNotification = item;
        _inviteMessagesLoadVersion++;
        InviteMessages.Clear();
        SelectedInviteMessage = null;
        InviteMessagesStatus = string.Empty;
        IsInviteMessagesLoading = false;
        ResponseComposerMessage = string.Empty;
        ComposerError = string.Empty;
        IsResponseComposerOpen = true;
        _ = LoadResponsePresetsAsync(item);
        OnPropertyChanged(nameof(ResponseComposerTitle));
        OnPropertyChanged(nameof(ResponseComposerSubtitle));
        SendDeclineWithMessageCommand.RaiseCanExecuteChanged();
    }

    private void CloseResponseComposer()
    {
        _responseComposerNotification = null;
        IsResponseComposerOpen = false;
        ResponseComposerMessage = string.Empty;
        ComposerError = string.Empty;
        _inviteMessagesLoadVersion++;
        InviteMessages.Clear();
        SelectedInviteMessage = null;
        InviteMessagesStatus = string.Empty;
        IsInviteMessagesLoading = false;
        SendDeclineWithMessageCommand.RaiseCanExecuteChanged();
    }

    private async Task SendDeclineWithMessageAsync()
    {
        var item = _responseComposerNotification;
        var message = ResponseComposerMessage.Trim();
        if (item is null || message.Length == 0 || IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.SenderUserId))
        {
            ShowError("VRChat did not include the sender for this notification.");
            return;
        }

        PushBusy($"Sending your reply to {item.SenderName}…");
        try
        {
            // Invites answer with the response collection; invite requests use
            // the requestResponse collection. VRChat selects the right one from
            // the notification type at the response endpoint.
            var messageType = item.IsInvite ? "response" : "requestResponse";
            var plan = await PrepareInviteMessageSlotAsync(messageType, message);
            await _api.RespondInviteAsync(item.Id, plan.Slot);
            try
            {
                await _api.HideNotificationAsync(item.Id, item.IsV2);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"hide responded notification: {ex}");
            }

            ClearPersistentNotification(item);
            CloseResponseComposer();
            ShowInfo(plan.SkippedCustomText
                ? $"Reply sent to {item.SenderName}. VRChat's reply slots are cooling down, so the default reply was used."
                : $"Reply sent to {item.SenderName}.");
        }
        catch (VrcApiException ex)
        {
            ComposerError = ex.StatusCode == 429
                ? "VRChat allows invite messages to change only once an hour. Try again later or decline without a message."
                : ex.Message;
        }
        catch (HttpRequestException)
        {
            ComposerError = "Could not reach VRChat to send that reply.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ComposerError = "Could not send that reply.";
        }
        finally
        {
            PopBusy();
        }
    }

    private void ClearPersistentNotification(NotificationItem item)
    {
        _clearedNotificationIds.Add(item.Id);
        Notifications.Remove(item);

        if (ReferenceEquals(_selectedNotification, item))
        {
            SelectedNotification = null;
            NotificationDetail = null;
        }

        UpdateUnreadState();
        PersistNotifications();
    }

    private void PersistNotifications()
    {
        if (string.IsNullOrWhiteSpace(_notificationsAccountId))
        {
            return;
        }

        _notificationStore.Save(
            _notificationsAccountId,
            Notifications
                .Where(notification => notification.IsPersistent)
                .Select(notification => notification.Snapshot),
            _clearedNotificationIds);
    }

    /// <summary>
    /// Loads the saved invites for an account before the first API refresh. They
    /// stay visible until the user declines or clears them, including after a
    /// relaunch or an account switch.
    /// </summary>
    private void LoadNotificationAccount(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) ||
            string.Equals(accountId, _notificationsAccountId, StringComparison.Ordinal))
        {
            return;
        }

        _notificationsAccountId = accountId;
        var stored = _notificationStore.Load(accountId);
        _clearedNotificationIds = new HashSet<string>(stored.ClearedIds, StringComparer.Ordinal);

        var persisted = stored.Notifications
            .Where(notification => !_clearedNotificationIds.Contains(notification.Id))
            .Select(notification => new NotificationItem(notification))
            .ToList();
        SyncCollection(Notifications, persisted, notification => notification.Id);
        UpdateUnreadState();
        HydrateBoopAttachments(Notifications);
        HydrateGroupMetadata(Notifications);
    }

    private void OpenFriendProfile(FriendItem? friend)
    {
        if (friend is null)
        {
            return;
        }

        try
        {
            LaunchService.OpenUrl($"https://vrchat.com/home/user/{friend.Id}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Couldn't open that profile in your browser.");
        }
    }

    private void CopyFriendLocation(FriendItem? friend)
    {
        if (friend is null || !LaunchService.IsLocation(friend.Location))
        {
            return;
        }

        CopyText(friend.Location, "World location copied to your clipboard.");
    }

    private void CopyFriendUserId(FriendItem? friend)
    {
        if (friend is null || string.IsNullOrWhiteSpace(friend.Id))
        {
            return;
        }

        CopyText(friend.Id, "User ID copied to your clipboard.");
    }

    private void CopyFriendProfileLink(FriendItem? friend)
    {
        if (friend is null || string.IsNullOrWhiteSpace(friend.Id))
        {
            return;
        }

        CopyText(
            $"https://vrchat.com/home/user/{friend.Id}",
            "Profile link copied to your clipboard.");
    }

    private void ToggleFavoriteFriend(FriendItem? friend)
    {
        if (friend is null)
        {
            return;
        }

        friend.IsFavorite = !friend.IsFavorite;
        if (friend.IsFavorite)
        {
            _favoriteFriends.Add(friend.Id);
        }
        else
        {
            _favoriteFriends.Remove(friend.Id);
        }

        _settings.FavoriteFriends = _favoriteFriends.ToList();
        _settingsStore.Save();
        RefreshFriendsView();
        ShowInfo(friend.IsFavorite
            ? $"{friend.DisplayName} pinned to the top of your friends list."
            : $"{friend.DisplayName} unpinned.");
    }

    private void ToggleFavoriteWorld(WorldItem? world)
    {
        if (world is null)
        {
            return;
        }

        world.IsFavorite = !world.IsFavorite;
        if (world.IsFavorite)
        {
            _favoriteWorlds.Add(world.Id);
        }
        else
        {
            _favoriteWorlds.Remove(world.Id);
        }

        _settings.FavoriteWorlds = _favoriteWorlds.ToList();
        _settingsStore.Save();
        ShowInfo(world.IsFavorite
            ? $"{world.Name} saved as a favorite world."
            : $"{world.Name} removed from favorite worlds.");
    }

    private void OpenNotificationProfile(NotificationDetailViewModel? detail)
    {
        if (detail is null)
        {
            return;
        }

        try
        {
            LaunchService.OpenUrl(detail.SenderProfileUrl);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Couldn't open that profile in your browser.");
        }
    }

    private void CopyNotificationLocation(NotificationDetailViewModel? detail)
    {
        if (detail is null || !LaunchService.IsLocation(detail.InviteLocation))
        {
            return;
        }

        CopyText(detail.InviteLocation, "Invite location copied to your clipboard.");
    }

    private async Task OpenNotificationWorldAsync()
    {
        var detail = NotificationDetail;
        if (detail is null || IsBusy)
        {
            return;
        }

        var world = detail.World;
        if (world is null)
        {
            var worldId = LaunchService.GetWorldIdFromLocation(detail.InviteLocation);
            if (worldId is null)
            {
                ShowError("That notification does not point to a world.");
                return;
            }

            PushBusy("Loading the invited world…");
            try
            {
                world = await _api.GetWorldAsync(worldId);
                if (world is null)
                {
                    ShowError("VRChat did not return that world.");
                    return;
                }
            }
            catch (VrcApiException ex)
            {
                ShowError(ex.Message);
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                ShowError("Could not load that world.");
                return;
            }
            finally
            {
                PopBusy();
            }
        }

        if (world is null)
        {
            return;
        }

        OpenWorld(new WorldItem(world)
        {
            IsFavorite = _favoriteWorlds.Contains(world.Id)
        });
    }

    private void ChooseBackdrop()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a backdrop image",
            Filter = "Images and GIFs|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        CustomBackdropPath = dialog.FileName;
        if (dialog.FileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            AnimateBackdrop = true;
        }
        _settingsStore.Save();
        ClearBackdropCommand.RaiseCanExecuteChanged();
        ShowInfo("Backdrop updated. GIFs animate slowly to stay lightweight.");
    }

    private void ClearBackdrop()
    {
        CustomBackdropPath = null;
        ClearBackdropCommand.RaiseCanExecuteChanged();
        ShowInfo("Switched back to the animated built-in backdrop.");
    }

    private void ChooseProfileBackdrop()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a profile popup background",
            Filter = "Images and GIFs|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ProfileBackdropPath = dialog.FileName;
        if (dialog.FileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            // The picker promises animated GIFs; do not require the separate
            // backdrop switch to be found first.
            AnimateBackdrop = true;
        }
        ShowInfo("Profile popup background updated.");
    }

    private void ClearProfileBackdrop()
    {
        ProfileBackdropPath = null;
        ShowInfo("Profile popup uses the normal panel background.");
    }

    private void OpenGroupInfoFromProfile(VrcProfileGroup group)
        => _ = GroupViewModel?.OpenGroupInfoAsync(group.Id, group.IsMutual);

    private void OpenNotificationGroup(NotificationItem? item)
    {
        if (item is null || !item.HasGroupId)
        {
            return;
        }

        _ = GroupViewModel?.OpenGroupInfoAsync(item.GroupId);
    }

    private async Task DownloadBackdropFromUrlAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(BackdropUrlInput))
        {
            return;
        }

        var url = BackdropUrlInput.Trim();
        PushBusy("Downloading backdrop…");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.Referrer = new Uri("https://vrchat.com/");
            http.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            http.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            http.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
            http.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "image");
            http.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "no-cors");
            http.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!response.IsSuccessStatusCode)
            {
                ShowError($"Failed to download backdrop: HTTP {(int)response.StatusCode} {response.StatusCode}. Paste a direct image link (e.g. .../image.gif).");
                return;
            }

            var isDeclaredImage = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

            // Read the bytes. If the server didn't declare an image Content-Type,
            // we still accept it when the file content itself is a known image format.
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
            {
                ShowError("The downloaded file is empty.");
                return;
            }

            var isContentImage = isDeclaredImage || ImageLoader.IsImageBytes(bytes);
            if (!isContentImage)
            {
                ShowError("That URL did not return an image. Please paste a direct link to a PNG, GIF, JPG, or WEBP file (not a page URL).");
                return;
            }

            // Pick the extension from the actual file content so a GIF served
            // without a .gif in the URL still saves (and animates) as a GIF.
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(ext) || ext.Length > 8)
            {
                ext = ImageLoader.IsGifBytes(bytes) ? ".gif" : ".bin";
            }

            var fileName = $"backdrop_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}{ext}";
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VrcCompanion", "backdrops");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);

            await File.WriteAllBytesAsync(path, bytes);

            CustomBackdropPath = path;
            BackdropUrlInput = string.Empty;
            ClearBackdropCommand.RaiseCanExecuteChanged();
            DownloadBackdropFromUrlCommand.RaiseCanExecuteChanged();
            ShowInfo("Backdrop downloaded and applied.");
        }
        catch (Exception ex)
        {
            ShowError($"Failed to download backdrop: {ex.Message}");
        }
        finally
        {
            PopBusy();
        }
    }

    private async Task OpenFriendWorldAsync(FriendItem? friend)
    {
        if (friend is null || IsBusy)
        {
            return;
        }

        var worldId = LaunchService.GetWorldIdFromLocation(friend.Location);
        if (worldId is null)
        {
            ShowError("That friend is not in a joinable world right now.");
            return;
        }

        PushBusy("Loading that world…");
        try
        {
            var world = await _api.GetWorldAsync(worldId);
            if (world is null)
            {
                ShowError("VRChat did not return that world.");
                return;
            }

            OpenWorld(new WorldItem(world));
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat to load that world.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not load that world.");
        }
        finally
        {
            PopBusy();
        }
    }

    private Task OpenActivityAvatarAsync(ActivityEntry? entry)
    {
        if (entry is null || !entry.CanOpenAvatar || IsBusy)
        {
            return Task.CompletedTask;
        }

        // Render the row's picture immediately. VRChat and the community image
        // indexes can take seconds, and they must never hold the popup closed.
        var placeholder = CreateActivityAvatarFallback(entry);
        AvatarViewModel?.ShowAvatarDetails(placeholder);
        _ = ResolveActivityAvatarInBackgroundAsync(entry, placeholder);
        return Task.CompletedTask;
    }

    private async Task ResolveActivityAvatarInBackgroundAsync(ActivityEntry entry, Avatar placeholder)
    {
        try
        {
            var avatar = await ResolveActivityAvatarAsync(entry);
            if (avatar is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(avatar.ImageUrl) &&
                !string.IsNullOrWhiteSpace(placeholder.ImageUrl))
            {
                avatar.ImageUrl = placeholder.ImageUrl;
                avatar.ThumbnailImageUrl = placeholder.ThumbnailImageUrl;
            }

            avatar.IsWearing = string.Equals(
                CurrentUser?.CurrentAvatar,
                avatar.Id,
                StringComparison.Ordinal);

            // Never reopen the popup (or replace a different avatar) if the user
            // closed it or moved on while the lookup was still running.
            if (AvatarViewModel is { } avatars &&
                ReferenceEquals(avatars.SelectedAvatar, placeholder))
            {
                avatars.ShowAvatarDetails(avatar);
            }
        }
        catch (Exception ex)
        {
            // The picture-only popup is already usable; a failed index lookup
            // must not turn it into an error banner.
            Debug.WriteLine($"activity avatar resolution: {ex.Message}");
        }
    }

    private async Task<Avatar?> ResolveActivityAvatarAsync(ActivityEntry entry)
    {
        if (!entry.HasAvatar && !string.IsNullOrWhiteSpace(entry.UserId))
        {
            // Prefer the locally retained avatar history; it is instant and
            // avoids touching a community index when the picture is known.
            var accountId = _api.CurrentUser?.Id;
            if (!string.IsNullOrWhiteSpace(accountId))
            {
                var fileId = ImageUrlHelper.ExtractFileId(entry.AvatarUrl);
                var match = FriendAvatarStore.Shared
                    .Load(accountId, entry.UserId)
                    .FirstOrDefault(candidate =>
                        !string.IsNullOrWhiteSpace(candidate.AvatarId) &&
                        (string.Equals(candidate.ThumbnailUrl, entry.AvatarUrl, StringComparison.Ordinal) ||
                         string.Equals(candidate.ImageUrl, entry.AvatarUrl, StringComparison.Ordinal) ||
                         (fileId is not null &&
                          (string.Equals(ImageUrlHelper.ExtractFileId(candidate.ThumbnailUrl), fileId, StringComparison.Ordinal) ||
                           string.Equals(ImageUrlHelper.ExtractFileId(candidate.ImageUrl), fileId, StringComparison.Ordinal)))));
                if (match is not null)
                {
                    entry.AvatarId = match.AvatarId;
                    entry.SetAvatarName(match.Name);
                }
            }
        }

        if (!entry.HasAvatar && !string.IsNullOrWhiteSpace(entry.UserId))
        {
            // A recent avatar-change row normally still matches the friend's
            // current avatar. Only trust it when the image file id proves it is
            // the same avatar, never just because the user is the same.
            try
            {
                var user = await _api.GetUserAsync(entry.UserId, forceRefresh: true);
                if (user is not null &&
                    !string.IsNullOrWhiteSpace(user.CurrentAvatar) &&
                    ActivityAvatarImageMatches(user, entry.AvatarUrl))
                {
                    entry.AvatarId = user.CurrentAvatar;
                    entry.SetAvatarName(user.CurrentAvatarName);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"activity avatar current user: {ex.Message}");
            }
        }

        var name = entry.EffectiveAvatarName;
        Avatar? knownPrivate = null;
        if (entry.HasAvatar)
        {
            // Try the id we already have first. It is normally the correct public
            // record, and returning it here avoids a slow name-index search just
            // to display an avatar we can already load.
            try
            {
                var avatar = await _api.GetAvatarAsync(entry.AvatarId);
                if (avatar is not null)
                {
                    var nameMatches = string.IsNullOrWhiteSpace(name) ||
                                      string.IsNullOrWhiteSpace(avatar.Name) ||
                                      string.Equals(
                                          avatar.Name.Trim(),
                                          name.Trim(),
                                          StringComparison.OrdinalIgnoreCase);
                    if (!avatar.IsPrivate && !avatar.IsUnavailable && nameMatches)
                    {
                        AvatarLookupCache.Remember(avatar);
                        return avatar;
                    }

                    if (avatar.IsPrivate)
                    {
                        // Keep the authoritative private answer in case the
                        // public name search has no better record.
                        knownPrivate = avatar;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"activity avatar record: {ex.Message}");
            }
        }

        if (entry.HasAvatarName)
        {
            // A log-only row knows the avatar name but not its id, and an older
            // name lookup may have cached a private same-name record. Search
            // the public indexes so a verified public record can replace it.
            var named = await ResolveActivityAvatarByNameAsync(entry);
            if (named is not null)
            {
                return named;
            }
        }

        if (knownPrivate is not null)
        {
            return knownPrivate;
        }

        Avatar? indexed = null;
        try
        {
            indexed = await _api.ResolveCommunityAvatarByImageAsync(
                entry.AvatarUrl,
                entry.UserId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"activity avatar image index: {ex.Message}");
        }

        if (indexed is null || string.IsNullOrWhiteSpace(indexed.Id))
        {
            return null;
        }

        entry.AvatarId = indexed.Id;
        entry.SetAvatarName(indexed.Name);
        try
        {
            var avatar = await _api.GetAvatarAsync(indexed.Id);
            if (avatar is not null)
            {
                return avatar;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"activity avatar indexed record: {ex.Message}");
        }

        // The index identified a record VRChat will not expose. Keep its useful
        // name/metadata, but deliberately leave the actions locked.
        indexed.ReleaseStatus = "unavailable";
        indexed.Description =
            "VRChat did not expose this avatar's record. It may be private or unlisted, so it can only be viewed.";
        if (!string.IsNullOrWhiteSpace(entry.AvatarUrl))
        {
            indexed.ImageUrl = entry.AvatarUrl;
            indexed.ThumbnailImageUrl = entry.AvatarUrl;
        }

        return indexed;
    }

    private async Task<Avatar?> ResolveActivityAvatarByNameAsync(ActivityEntry entry)
    {
        var name = entry.EffectiveAvatarName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Rows already resolved in this session can open immediately instead of
        // repeating a rate-limited public-index request.
        Avatar? match = AvatarLookupCache.FindByImage(entry.AvatarUrl) ??
                       AvatarLookupCache.FindByName(name);
        // Query the public avatar index first. VRChat's marketplace search can
        // return a private same-name duplicate; that record must never override
        // the actual public avatar the person is wearing.
        if (match is null || match.IsPrivate || match.IsUnavailable)
        {
            match = null;
            try
            {
                var primary = await _api.SearchAllCommunityAvatarsAsync(
                    name,
                    limit: 25,
                    providerLimit: 25,
                    providers: new[] { "avtrDB" });
                match = PickActivityAvatarByName(primary, name, entry.AvatarAuthor);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"activity avatar primary index {name}: {ex.Message}");
            }
        }

        if (match is null)
        {
            try
            {
                var result = await _api.SearchAvatarsAsync(new AvatarFilter
                {
                    SearchTerm = name,
                    PageSize = 50,
                    ReleaseStatus = "all"
                });
                match = PickActivityAvatarByName(result.Avatars, name, entry.AvatarAuthor);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"activity avatar name search {name}: {ex.Message}");
            }
        }

        if (match is null)
        {
            try
            {
                var community = await _api.SearchAllCommunityAvatarsAsync(
                    name,
                    limit: 50,
                    providerLimit: 50);
                match = PickActivityAvatarByName(community, name, entry.AvatarAuthor);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"activity avatar community search {name}: {ex.Message}");
            }
        }

        if (match is null || string.IsNullOrWhiteSpace(match.Id))
        {
            return null;
        }

        AvatarLookupCache.Remember(match);
        entry.AvatarId = match.Id;
        var image = !string.IsNullOrWhiteSpace(match.ThumbnailImageUrl)
            ? match.ThumbnailImageUrl
            : match.ImageUrl;
        entry.AvatarUrl = ImageUrlHelper.ToThumbnailUrl(image, 256) ?? image;
        entry.SetAvatarName(string.IsNullOrWhiteSpace(match.Name) ? name : match.Name);

        if (!string.IsNullOrWhiteSpace(entry.UserId) &&
            !string.IsNullOrWhiteSpace(_api.CurrentUser?.Id))
        {
            FriendAvatarStore.Shared.RememberDetails(
                _api.CurrentUser.Id,
                entry.UserId,
                match,
                isCurrent: false);
        }

        try
        {
            var full = await _api.GetAvatarAsync(match.Id);
            if (full is not null)
            {
                if (string.IsNullOrWhiteSpace(full.ImageUrl) && !string.IsNullOrWhiteSpace(match.ImageUrl))
                {
                    full.ImageUrl = match.ImageUrl;
                }

                if (string.IsNullOrWhiteSpace(full.ThumbnailImageUrl) &&
                    !string.IsNullOrWhiteSpace(match.ThumbnailImageUrl))
                {
                    full.ThumbnailImageUrl = match.ThumbnailImageUrl;
                }

                AvatarLookupCache.Remember(full);
                return full;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"activity avatar name record {match.Id}: {ex.Message}");
        }

        return match;
    }

    private static Avatar? PickActivityAvatarByName(
        IEnumerable<Avatar> avatars,
        string name,
        string? author)
    {
        var candidates = avatars
            .Where(avatar => !string.IsNullOrWhiteSpace(avatar.Id) &&
                             !VrcApiClient.IsPlaceholderAvatarId(avatar.Id) &&
                             !avatar.IsPrivate &&
                             !avatar.IsUnavailable &&
                             string.Equals(avatar.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var authorMatch = candidates.FirstOrDefault(avatar =>
            !string.IsNullOrWhiteSpace(author) &&
            !string.IsNullOrWhiteSpace(avatar.AuthorName) &&
            string.Equals(avatar.AuthorName, author, StringComparison.OrdinalIgnoreCase));
        return authorMatch ?? candidates[0];
    }

    private static bool ActivityAvatarImageMatches(VrcUser user, string? imageUrl)
    {
        var fileId = ImageUrlHelper.ExtractFileId(imageUrl);
        return fileId is not null &&
               (string.Equals(
                    fileId,
                    ImageUrlHelper.ExtractFileId(user.CurrentAvatarThumbnailImageUrl),
                    StringComparison.Ordinal) ||
                string.Equals(
                    fileId,
                    ImageUrlHelper.ExtractFileId(user.CurrentAvatarImageUrl),
                    StringComparison.Ordinal));
    }

    private Avatar CreateActivityAvatarFallback(ActivityEntry entry)
    {
        var name = entry.EffectiveAvatarName;
        var imageUrl = ResolveActivityAvatarImage(entry);

        return new Avatar
        {
            Id = entry.AvatarId,
            Name = string.IsNullOrWhiteSpace(name)
                ? "Private or unlisted avatar"
                : name,
            ImageUrl = imageUrl,
            ThumbnailImageUrl = imageUrl,
            // This is a temporary object shown while the real record loads. Even
            // when the row has an id, that id has not been confirmed as private,
            // so never label a potentially public avatar as private.
            ReleaseStatus = "unavailable",
            Description =
                "VRChat has not confirmed this avatar's record yet. The app is checking the public indexes; reopen it if it does not resolve."
        };
    }

    private string ResolveActivityAvatarImage(ActivityEntry entry)
    {
        if (entry.HasAvatarImage)
        {
            return entry.AvatarUrl;
        }

        var accountId = _api.CurrentUser?.Id;
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(entry.UserId))
        {
            return string.Empty;
        }

        var nearest = FriendAvatarStore.Shared
            .Load(accountId, entry.UserId)
            .OrderBy(avatar => Math.Abs((avatar.LastSeen - entry.Timestamp).TotalMinutes))
            .FirstOrDefault();
        return nearest?.ThumbnailUrl is { Length: > 0 } thumbnail
            ? thumbnail
            : nearest?.ImageUrl ?? string.Empty;
    }

    private async Task OpenActivityUserAsync(ActivityEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.UserId) || IsBusy)
        {
            return;
        }

        var friend = CreatePresenceAwareFriend(new VrcUser
        {
            Id = entry.UserId,
            DisplayName = entry.DisplayName,
            Username = entry.UserName,
            Location = LaunchService.IsLocation(entry.Location) ? entry.Location : null
        });

        SelectedFriend = null;
        await LoadFriendDetailAsync(friend, forceRefresh: true);
    }

    // -------------------------------------------------------------- updating

    private void OnRealtimeStatusChanged(object? sender, VrChatRealtimeStatus status)
    {
        Debug.WriteLine($"VRChat realtime: {status.State} {status.Reason}");
    }

    private void OnRealtimeEvent(object? sender, VrChatRealtimeEvent realtimeEvent)
    {
        if (_disposed)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            try
            {
                dispatcher.BeginInvoke(new Action(() => OnRealtimeEvent(sender, realtimeEvent)));
            }
            catch (InvalidOperationException)
            {
                // The window is shutting down; there is no UI left to update.
            }
            return;
        }

        if (!IsLoggedIn)
        {
            return;
        }

        var friendsById = Friends
            .GroupBy(friend => friend.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().User, StringComparer.Ordinal);
        var route = VrChatRealtimeRouter.Route(realtimeEvent, friendsById);
        switch (route.Action)
        {
            case VrChatRealtimeRouteAction.UpdateFriend:
                ApplyRealtimeFriendUpdate(route.UserId, route.Friend);
                break;
            case VrChatRealtimeRouteAction.AddFriend:
                ApplyRealtimeFriendAdd(route.UserId, route.Friend);
                break;
            case VrChatRealtimeRouteAction.RefreshFriends:
                QueueRealtimeFriendsRefresh();
                if (string.Equals(realtimeEvent.Type, "user-update", StringComparison.Ordinal))
                {
                    QueueCurrentUserRefresh();
                }
                break;
            case VrChatRealtimeRouteAction.RemoveFriend:
                if (!string.IsNullOrWhiteSpace(route.UserId))
                {
                    // friend-delete is authoritative: the person is no longer
                    // on this account's friends list, so record the unfriend
                    // now instead of waiting for a later list diff.
                    var removedFriend = Friends.FirstOrDefault(friend =>
                        string.Equals(friend.Id, route.UserId, StringComparison.Ordinal));
                    RemoveFriendFromList(route.UserId);
                    _friendChanges.RemoveFriend(route.UserId);
                    if (removedFriend is not null)
                    {
                        RecordFriendChange(removedFriend.User, added: false);
                    }
                }
                break;
            case VrChatRealtimeRouteAction.RefreshNotifications:
                QueueRealtimeNotificationsRefresh();
                break;
            case VrChatRealtimeRouteAction.UpdateCurrentUserLocation:
                ApplyObservedCurrentLocation(route.Location, route.UserId);
                break;
        }
    }

    /// <summary>
    /// The pipeline's user-location event is the authoritative answer for the
    /// signed-in account. The REST user payload can lag or report "private"
    /// while the client is already standing in a world.
    /// </summary>
    private void ApplyObservedCurrentLocation(string? location, string? userId = null)
    {
        if (CurrentUser is null || string.IsNullOrWhiteSpace(location))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(userId) &&
            !string.Equals(CurrentUser.Id, userId, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(CurrentUser.Location, location, StringComparison.Ordinal))
        {
            return;
        }

        CurrentUser.Location = location;
        if (!string.Equals(location, "offline", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(CurrentUser.Status, "offline", StringComparison.OrdinalIgnoreCase))
            {
                CurrentUser.Status = "active";
            }

            CurrentUser.State = "online";
        }

        ActivityFeedViewModel?.ApplyCurrentUser(CurrentUser);
        OnPropertyChanged(nameof(CurrentUser));
        OnPropertyChanged(nameof(InviteComposerSubtitle));
        OnPropertyChanged(nameof(HasInviteComposerLocationHint));
        SendInviteChoiceCommand.RaiseCanExecuteChanged();
        QueueContextLiveRefresh();
    }

    private void ApplyRealtimeFriendUpdate(string? userId, VrcUser? updated)
    {
        if (string.IsNullOrWhiteSpace(userId) || updated is null)
        {
            return;
        }

        var friend = Friends.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, userId, StringComparison.Ordinal));
        if (friend is null)
        {
            // The friend page missed this person. A realtime online/update event
            // is still real data: insert the row now, then let the complete list
            // fill in the rest in the background.
            friend = new FriendItem(updated)
            {
                IsFavorite = _favoriteFriends.Contains(userId)
            };
            Friends.Insert(0, friend);
            QueueRealtimeFriendsRefresh();
        }

        var previousUser = friend.User;
        FriendAvatarStore.Shared.ObserveAll(CurrentUser?.Id, new[] { updated });
        ActivityFeedViewModel?.ApplyFriendUpdate(
            updated,
            ReferenceEquals(previousUser, updated) ? null : previousUser);
        UpdatePresenceSets(userId, updated);
        friend.Update(updated);
        friend.ApplyPresenceSets(
            _onlineFriendIds.Contains(userId),
            _activeFriendIds.Contains(userId));
        RefreshFriendsView();
        SelectedFriendDetail?.UpdateFriend(friend);
        if (updated.Bio is null)
        {
            // The realtime payload usually omits bio. A targeted profile read
            // makes the edit visible right away instead of waiting for this
            // friend's turn in the slow background scan.
            QueueFriendBioRefresh(userId);
        }
    }

    private void QueueFriendBioRefresh(string userId)
    {
        if (!IsLoggedIn ||
            ActivityFeedViewModel is null ||
            string.IsNullOrWhiteSpace(userId) ||
            !_bioRefreshInFlight.Add(userId))
        {
            return;
        }

        var candidate = ActivityFeedViewModel.NextBioCandidateId(
            new[] { userId },
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(10));
        if (candidate is null)
        {
            _bioRefreshInFlight.Remove(userId);
            return;
        }

        _ = RefreshFriendBioAsync(userId);
    }

    private async Task RefreshFriendBioAsync(string userId)
    {
        try
        {
            var profile = await _api.GetProfileAsync(userId, forceRefresh: true);
            if (!IsLoggedIn || ActivityFeedViewModel is null)
            {
                return;
            }

            if (profile?.Bio is null)
            {
                ActivityFeedViewModel.MarkBioChecked(userId, DateTimeOffset.UtcNow);
                return;
            }

            var friend = Friends.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, userId, StringComparison.Ordinal));
            if (friend is not null)
            {
                friend.User.Bio = profile.Bio;
                ActivityFeedViewModel.ObserveProfileBio(
                    friend.User,
                    profile.Bio,
                    DateTimeOffset.UtcNow);
            }
        }
        catch (VrcApiException ex) when (ex.StatusCode == 429)
        {
            _bioPollPausedUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"realtime bio refresh {userId}: {ex.Message}");
        }
        finally
        {
            _bioRefreshInFlight.Remove(userId);
        }
    }

    private void ApplyRealtimeFriendAdd(string? userId, VrcUser? added)
    {
        if (string.IsNullOrWhiteSpace(userId) || added is null)
        {
            QueueRealtimeFriendsRefresh();
            return;
        }

        var friend = Friends.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, userId, StringComparison.Ordinal));
        if (friend is null)
        {
            friend = new FriendItem(added)
            {
                IsFavorite = _favoriteFriends.Contains(userId)
            };
            Friends.Insert(0, friend);
        }
        else
        {
            friend.Update(added);
        }

        UpdatePresenceSets(userId, added);
        friend.ApplyPresenceSets(
            _onlineFriendIds.Contains(userId),
            _activeFriendIds.Contains(userId));
        RefreshFriendsView();
        RecordFriendChange(added, added: true);
        ActivityFeedViewModel?.ApplyFriendUpdate(added);
        QueueRealtimeFriendsRefresh();
    }

    /// <summary>
    /// A self user-update can carry a bio or avatar change that is not in the
    /// friend payload. Re-read the authoritative user once, throttled.
    /// </summary>
    private void QueueCurrentUserRefresh()
    {
        if (_currentUserRefreshBusy || !IsLoggedIn)
        {
            return;
        }

        _ = RefreshCurrentUserAsync();
    }

    private async Task RefreshCurrentUserAsync()
    {
        _currentUserRefreshBusy = true;
        try
        {
            var result = await _api.GetCurrentUserAsync();
            if (result.User is not null && IsLoggedIn)
            {
                ApplyCurrentUser(result.User);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"current user realtime refresh: {ex.Message}");
        }
        finally
        {
            _currentUserRefreshBusy = false;
        }
    }

    private void QueueRealtimeFriendsRefresh()
    {
        _realtimeFriendsRequested = true;
        if (_realtimeFriendsBusy)
        {
            return;
        }

        _ = RunRealtimeFriendsRefreshAsync();
    }

    private async Task RunRealtimeFriendsRefreshAsync()
    {
        _realtimeFriendsBusy = true;
        try
        {
            while (_realtimeFriendsRequested && IsLoggedIn)
            {
                _realtimeFriendsRequested = false;
                await Task.Delay(600);
                if (!IsLoggedIn)
                {
                    return;
                }

                await LoadAllFriendsAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"realtime friend refresh: {ex}");
        }
        finally
        {
            _realtimeFriendsBusy = false;
        }
    }

    private void QueueRealtimeNotificationsRefresh()
    {
        _realtimeNotificationsRequested = true;
        if (_realtimeNotificationsBusy)
        {
            return;
        }

        _ = RunRealtimeNotificationsRefreshAsync();
    }

    private async Task RunRealtimeNotificationsRefreshAsync()
    {
        _realtimeNotificationsBusy = true;
        try
        {
            while (_realtimeNotificationsRequested && IsLoggedIn)
            {
                _realtimeNotificationsRequested = false;
                await Task.Delay(750);
                if (!IsLoggedIn)
                {
                    return;
                }

                var notifications = await _api.GetNotificationsAsync();
                if (!IsLoggedIn)
                {
                    return;
                }

                UpdateNotifications(notifications);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"realtime notification refresh: {ex}");
        }
        finally
        {
            _realtimeNotificationsBusy = false;
        }
    }

    private void OnAvatarWorn(Avatar avatar)
    {
        if (CurrentUser is null || string.IsNullOrWhiteSpace(avatar.Id))
        {
            return;
        }

        _lastLocalAvatarSwitchAt = DateTimeOffset.UtcNow;
        CurrentUser.CurrentAvatar = avatar.Id;
        CurrentUser.CurrentAvatarName = avatar.Name;
        if (!string.IsNullOrWhiteSpace(avatar.ThumbnailImageUrl))
        {
            CurrentUser.CurrentAvatarThumbnailImageUrl = avatar.ThumbnailImageUrl;
        }

        if (!string.IsNullOrWhiteSpace(avatar.ImageUrl))
        {
            CurrentUser.CurrentAvatarImageUrl = avatar.ImageUrl;
        }

        if (!string.IsNullOrWhiteSpace(CurrentUser.Id))
        {
            // The log observer may see this switch too; record it now so it
            // cannot produce a second Activity row for the same change.
            _lastLoggedAvatars[CurrentUser.Id] = avatar.Name;
        }

        FriendAvatarStore.Shared.ObserveAll(CurrentUser.Id, new[] { CurrentUser });
        ActivityFeedViewModel?.ApplyCurrentUser(CurrentUser);
        OnAllPropertiesChanged();
    }

    private void ApplyCurrentUser(VrcUser user)
    {
        var previousAccountId = CurrentUser?.Id;
        var previousLocation = CurrentUser?.Location;
        // A just-completed local switch is authoritative. VRChat's account
        // endpoint can briefly still serve the old avatar; applying that stale
        // value would create a false switch back.
        if (string.Equals(previousAccountId, user.Id, StringComparison.Ordinal) &&
            CurrentUser is not null &&
            DateTimeOffset.UtcNow - _lastLocalAvatarSwitchAt < TimeSpan.FromSeconds(15) &&
            !string.IsNullOrWhiteSpace(CurrentUser.CurrentAvatar) &&
            !string.Equals(user.CurrentAvatar, CurrentUser.CurrentAvatar, StringComparison.Ordinal))
        {
            user.CurrentAvatar = CurrentUser.CurrentAvatar;
            user.CurrentAvatarName = CurrentUser.CurrentAvatarName;
            user.CurrentAvatarImageUrl = CurrentUser.CurrentAvatarImageUrl;
            user.CurrentAvatarThumbnailImageUrl = CurrentUser.CurrentAvatarThumbnailImageUrl;
        }
        if (!string.Equals(previousAccountId, user.Id, StringComparison.Ordinal))
        {
            _contextLogSignature = string.Empty;
        }
        // Restore this account's saved invites before any fresh notification
        // response can replace the empty startup collection.
        LoadNotificationAccount(user.Id);

        CurrentUser = user;
        if (!string.IsNullOrWhiteSpace(user.Id) &&
            !_lastLoggedAvatars.ContainsKey(user.Id))
        {
            _lastLoggedAvatars[user.Id] = user.CurrentAvatarName ?? string.Empty;
        }

        FriendAvatarStore.Shared.ObserveAll(user.Id, new[] { user });
        ActivityFeedViewModel?.SetAccount(user.Id);
        ActivityFeedViewModel?.ApplyCurrentUser(user);
        if (!string.IsNullOrWhiteSpace(user.Id) &&
            !string.IsNullOrWhiteSpace(user.CurrentAvatarName))
        {
            _lastLoggedAvatars[user.Id] = user.CurrentAvatarName;
        }
        ContinuityViewModel?.SetAccount(user);
        ContextViewModel?.SetAccount(user);
        if (!string.Equals(previousLocation, user.Location, StringComparison.Ordinal))
        {
            QueueContextLiveRefresh();
        }
        // Only a real account change resets this baseline. Applying a periodic
        // user refresh must not silently seed over a pending friend diff.
        if (!string.Equals(previousAccountId, user.Id, StringComparison.Ordinal))
        {
            RestoreFriendChangeBaseline(user.Id);
        }

        ApplyCurrentUserPresenceSets(user);
        if (!string.IsNullOrWhiteSpace(user.Id) &&
            !string.Equals(_historyAccountId, user.Id, StringComparison.Ordinal))
        {
            _ = SwitchHistoryAccountAsync(user);
        }
        _greetingName = (user.DisplayName ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;

        if (!IsStatusDirty)
        {
            SetStatusEditorFromUser(user);
        }

        OnAllPropertiesChanged();
        OnPropertyChanged(nameof(GreetingText));
        RaiseCommandStates();
        if (!CosmeticCatalog.IsLoaded)
        {
            _ = LoadFriendCosmeticsAsync();
        }

        var url = ImageUrlHelper.PickAvatarThumbnailUrl(
            user.UserIcon,
            user.ProfilePicOverrideThumbnail ?? user.IconUrl,
            user.CurrentAvatarThumbnailImageUrl,
            user.CurrentAvatarImageUrl,
            256);

        if (!string.Equals(url, _avatarUrl, StringComparison.Ordinal))
        {
            _avatarUrl = url;
            AvatarImage = null;
            if (!string.IsNullOrWhiteSpace(url))
            {
                _ = LoadAvatarAsync(url);
            }
        }
    }

    /// <summary>
    /// The current-user payload carries the authoritative online/active friend
    /// buckets. They resolve friends whose location is VRChat's stale "private"
    /// sentinel, which otherwise looks Offline in a profile card.
    /// </summary>
    private void ApplyCurrentUserPresenceSets(VrcUser user)
    {
        _onlineFriendIds.Clear();
        _activeFriendIds.Clear();

        foreach (var id in user.OnlineFriends)
        {
            if (!string.IsNullOrWhiteSpace(id)) _onlineFriendIds.Add(id);
        }

        foreach (var id in user.ActiveFriends)
        {
            if (!string.IsNullOrWhiteSpace(id)) _activeFriendIds.Add(id);
        }

        ApplyPresenceSetsToFriends();
    }

    private void ApplyPresenceSetsToFriends()
    {
        foreach (var friend in Friends)
        {
            friend.ApplyPresenceSets(
                _onlineFriendIds.Contains(friend.Id),
                _activeFriendIds.Contains(friend.Id));
        }
    }

    private FriendItem CreatePresenceAwareFriend(VrcUser user)
    {
        var friend = new FriendItem(user);
        friend.ApplyPresenceSets(
            _onlineFriendIds.Contains(user.Id),
            _activeFriendIds.Contains(user.Id));
        return friend;
    }

    private void UpdatePresenceSets(string userId, VrcUser user)
    {
        var state = (user.State ?? string.Empty).Trim().ToLowerInvariant();
        var location = user.Location ?? string.Empty;
        var inWorld = location.StartsWith("wrld_", StringComparison.OrdinalIgnoreCase) ||
                      location.StartsWith("traveling", StringComparison.OrdinalIgnoreCase);

        if (state == "active")
        {
            _activeFriendIds.Add(userId);
            _onlineFriendIds.Remove(userId);
        }
        else if (state == "offline" ||
                 string.Equals(location, "offline", StringComparison.OrdinalIgnoreCase))
        {
            _activeFriendIds.Remove(userId);
            _onlineFriendIds.Remove(userId);
        }
        else if (state == "online" || inWorld)
        {
            _onlineFriendIds.Add(userId);
            _activeFriendIds.Remove(userId);
        }
    }

    private async Task LoadAvatarAsync(string url)
    {
        var image = await _images.LoadAsync(url, 256);
        if (!_disposed && image is not null && string.Equals(_avatarUrl, url, StringComparison.Ordinal))
        {
            AvatarImage = image;
        }
    }

    private void SetStatusEditorFromUser(VrcUser user)
    {
        _suppressStatusDirty = true;
        try
        {
            SelectedStatus = Statuses.FirstOrDefault(option =>
                               string.Equals(option.Value, user.Status, StringComparison.OrdinalIgnoreCase))
                           ?? Statuses[0];
            StatusMessageInput = user.StatusDescription ?? string.Empty;
        }
        finally
        {
            _suppressStatusDirty = false;
        }

        IsStatusDirty = false;
    }

    private void SetStatusDirty(bool dirty)
    {
        if (_isStatusDirty == dirty)
        {
            return;
        }

        IsStatusDirty = dirty;
    }

    /// <summary>
    /// Merges friend data into the list. <paramref name="keepMissing"/> keeps
    /// friends that are absent from this batch, which is used while the offline
    /// pages are still loading.
    /// </summary>
    private void UpdateFriends(IReadOnlyList<VrcUser> users, bool keepMissing = false)
    {
        var existingById = Friends
            .GroupBy(friend => friend.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var desired = users
            .Where(user => !string.IsNullOrWhiteSpace(user.Id))
            .GroupBy(user => user.Id)
            .Select(group => group.First())
            .Select(user =>
            {
                // Every record from /auth/user/friends is, by definition, a friend.
                user.IsFriend = true;
                if (existingById.TryGetValue(user.Id, out var existing))
                {
                    existing.Update(user);
                    existing.IsFavorite = _favoriteFriends.Contains(user.Id);
                    existing.ApplyPresenceSets(
                        _onlineFriendIds.Contains(user.Id),
                        _activeFriendIds.Contains(user.Id));
                    return existing;
                }

                var created = new FriendItem(user)
                {
                    IsFavorite = _favoriteFriends.Contains(user.Id)
                };
                created.ApplyPresenceSets(
                    _onlineFriendIds.Contains(user.Id),
                    _activeFriendIds.Contains(user.Id));
                return created;
            })
            .OrderByDescending(friend => friend.IsFavorite)
            .ThenByDescending(friend => friend.PresenceSortRank)
            .ThenBy(friend => friend.StatusRank)
            .ThenBy(friend => friend.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (keepMissing)
        {
            var incoming = new HashSet<string>(desired.Select(friend => friend.Id), StringComparer.Ordinal);
            desired.AddRange(Friends.Where(friend => !incoming.Contains(friend.Id)));
        }

        // The list is rebuilt underneath the user, so hold on to whatever popup
        // is open and put the highlight back once the rows exist again.
        var keepSelected = _selectedFriend;
        var keepDetail = _friendDetail;

        // A first fill (or a large account catching up) is swapped in one go.
        // Thousands of individual row notifications are what made this crawl.
        if (Friends is RangeObservableCollection<FriendItem> range &&
            (Friends.Count == 0 || desired.Count > Friends.Count + 250))
        {
            range.ReplaceAll(desired);
        }
        else
        {
            SyncCollection(Friends, desired, friend => friend.Id);
        }

        RefreshFriendsView();
        RestoreSelection(keepSelected, keepDetail);
        InstanceViewModel?.ApplyFriends(Friends.ToList());
        PublishFriendDirectory();
    }

    /// <summary>
    /// Continuity and Context read people from local history, which can be
    /// older than the live friend list. Keep their relationship labels wired to
    /// the same friend directory the Friends tab uses.
    /// </summary>
    private void PublishFriendDirectory()
    {
        var ids = Friends.Select(friend => friend.Id).ToArray();
        ContinuityViewModel?.SetFriendIds(ids);
        ContextViewModel?.SetFriendIds(ids);
    }

    /// <summary>
    /// Keeps the selected friend and their open popup across a list rebuild
    /// (startup fill, background refresh, filter change).
    /// </summary>
    private void RestoreSelection(FriendItem? keepSelected, FriendDetailViewModel? keepDetail)
    {
        // Match by user ID, not by row object. A refresh can rebuild the rows
        // underneath the popup; restoring by object identity either loses the
        // popup or lets the list selection bind to a neighbouring user.
        var keepId = keepDetail?.Id ?? keepSelected?.Id;
        var restored = string.IsNullOrWhiteSpace(keepId)
            ? null
            : Friends.FirstOrDefault(friend =>
                string.Equals(friend.Id, keepId, StringComparison.Ordinal));

        if (restored is not null)
        {
            if (!ReferenceEquals(_selectedFriend, restored))
            {
                _selectedFriend = restored;
                OnPropertyChanged(nameof(SelectedFriend));
            }

            if (keepDetail is not null && !ReferenceEquals(_friendDetail, keepDetail))
            {
                _friendDetail = keepDetail;
                OnPropertyChanged(nameof(SelectedFriendDetail));
                OnPropertyChanged(nameof(HasSelectedFriend));
            }

            keepDetail?.UpdateFriend(restored);
            RaiseCommandStates();
            return;
        }

        if (_selectedFriend is not null)
        {
            SelectedFriend = null;
            SelectedFriendDetail = null;
        }
    }

    private bool FilterFriend(object item)
    {
        if (item is not FriendItem friend)
        {
            return false;
        }

        if (ShowOnlineOnly && !friend.IsPresent)
        {
            return false;
        }

        switch (SelectedFriendFilter?.Key)
        {
            case "online" when !friend.IsOnline:
                return false;
            case "active" when !friend.IsActive:
                return false;
            case "joinme" when !(friend.IsOnline &&
                                 string.Equals(friend.User.Status, "join me", StringComparison.OrdinalIgnoreCase)):
                return false;
            case "offline" when friend.IsPresent:
                return false;
        }

        var query = (FriendFilter ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return true;
        }

        return friend.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || friend.Username.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool FilterFriendLocation(object item)
        => item is FriendItem friend &&
           friend.IsOnline &&
           LaunchService.GetWorldIdFromLocation(friend.Location) is not null;

    private bool FilterFriendChange(object item)
        => item is FriendChangeItem change &&
           SelectedFriendChangeFilter?.Key switch
           {
               "friended" => change.IsAdded,
               "unfriended" => change.IsRemoved,
               _ => true
           };

    private async Task OpenFriendLocationsAsync()
    {
        IsFriendLocationsOpen = true;
        await LoadFriendWorldNamesAsync();
    }

    private async Task LoadFriendWorldNamesAsync()
    {
        if (_loadingFriendLocations)
        {
            return;
        }

        _loadingFriendLocations = true;
        OnPropertyChanged(nameof(FriendLocationsSummary));
        try
        {
            var worldIds = Friends
                .Select(friend => LaunchService.GetWorldIdFromLocation(friend.Location))
                .Where(worldId => !string.IsNullOrWhiteSpace(worldId))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var worldId in worldIds)
            {
                try
                {
                    var world = await _api.GetWorldAsync(worldId!);
                    foreach (var friend in Friends.Where(friend =>
                                 string.Equals(
                                     LaunchService.GetWorldIdFromLocation(friend.Location),
                                     worldId,
                                     StringComparison.Ordinal)))
                    {
                        friend.SetWorldName(world?.Name);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }
        }
        finally
        {
            _loadingFriendLocations = false;
            _friendLocationsView.Refresh();
            OnPropertyChanged(nameof(FriendLocationsSummary));
            OnPropertyChanged(nameof(HasFriendLocations));
        }
    }

    private void OpenFriendLocation(FriendItem? friend)
    {
        if (friend is null)
        {
            return;
        }

        IsFriendLocationsOpen = false;
        SelectedFriend = friend;
    }

    private void OpenFriendChange(FriendChangeItem? change)
    {
        if (change is null)
        {
            return;
        }

        IsFriendChangesOpen = false;
        SelectedFriend = change.Friend;
    }

    private void RefreshFriendsView()
    {
        _friendsView.Refresh();
        _friendLocationsView.Refresh();
        UpdateFriendsSummary();
        OnPropertyChanged(nameof(FriendLocationsSummary));
        OnPropertyChanged(nameof(HasFriendLocations));
    }

    private void UpdateFriendsSummary()
    {
        TotalFriendCount = Friends.Count;
        OnlineFriendCount = Friends.Count(friend => friend.IsPresent);
        FavoriteFriendCount = Friends.Count(friend => friend.IsFavorite);

        var visible = _friendsView.Cast<object>().Count();
        FriendsSummary = TotalFriendCount == 0
            ? "No friends yet"
            : visible == TotalFriendCount
                ? $"{OnlineFriendCount} online · {TotalFriendCount} friends"
                : $"{visible} shown · {OnlineFriendCount} online";

        HasNoVisibleFriends = visible == 0;
        OnPropertyChanged(nameof(TotalFriendCount));
        OnPropertyChanged(nameof(OnlineFriendCount));
        OnPropertyChanged(nameof(FavoriteFriendCount));
        OnPropertyChanged(nameof(FriendsEmptyMessage));
    }

    private bool FilterNotification(object item)
    {
        if (item is not NotificationItem notification)
        {
            return false;
        }

        switch (SelectedNotificationFilter?.Key)
        {
            case "unread" when notification.Seen:
                return false;
            case "friend-requests" when !notification.IsFriendRequest:
                return false;
            case "world-invites" when !notification.IsInvite:
                return false;
            case "invite-requests" when !notification.IsInviteRequest:
                return false;
            case "groups" when !notification.IsGroup:
                return false;
            case "boops" when !notification.IsBoop:
                return false;
            case "other" when notification.IsFriendRequest ||
                              notification.IsInvite ||
                              notification.IsInviteRequest ||
                              notification.IsGroup ||
                              notification.IsBoop:
                return false;
        }

        var query = (NotificationSearch ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return true;
        }

        return notification.SenderName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || notification.GroupName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || notification.GroupTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || notification.MessageText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || notification.DetailsText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || notification.TypeLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshNotificationsView()
    {
        _notificationsView.Refresh();
        UpdateNotificationFilterState();
    }

    private void UpdateNotificationFilterState()
    {
        var total = Notifications.Count;
        var visible = _notificationsView.Cast<object>().Count();
        var unread = Notifications.Count(notification => !notification.Seen);

        NotificationFilterSummary = total == 0
            ? "Waiting for something fun to happen."
            : $"{visible} shown · {unread} unread · {total} total";

        HasNoVisibleNotifications = visible == 0;

        var query = (NotificationSearch ?? string.Empty).Trim();
        if (total == 0)
        {
            NotificationsEmptyMessage = "You're all caught up.";
        }
        else if (query.Length > 0)
        {
            NotificationsEmptyMessage = $"No notifications match \u201c{query}\u201d.";
        }
        else
        {
            NotificationsEmptyMessage = SelectedNotificationFilter?.Key switch
            {
                "unread" => "No unread notifications. Nice.",
                "friend-requests" => "No friend requests right now.",
                "world-invites" => "No world invites right now.",
                "invite-requests" => "No invite requests right now.",
                "groups" => "No group updates right now.",
                "boops" => "No boops yet. Your friends are slacking.",
                "other" => "Nothing else in your notification feed.",
                _ => "No notifications match the current filter."
            };
        }

        OnPropertyChanged(nameof(NotificationFilterSummary));
        OnPropertyChanged(nameof(HasNoVisibleNotifications));
        OnPropertyChanged(nameof(NotificationsEmptyMessage));
    }

    private async Task LoadNotificationDetailAsync(NotificationItem? notification)
    {
        _notificationDetailCancellation?.Cancel();
        _notificationDetailCancellation?.Dispose();
        _notificationDetailCancellation = null;

        if (notification is null)
        {
            NotificationDetail = null;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _notificationDetailCancellation = cancellation;
        var detail = new NotificationDetailViewModel(notification, _api);
        NotificationDetail = detail;
        await detail.LoadAsync(forceRefresh: false, cancellation.Token);

    }

    private async Task RefreshNotificationDetailAsync()
    {
        var detail = NotificationDetail;
        if (detail is null)
        {
            return;
        }

        _notificationDetailCancellation?.Cancel();
        _notificationDetailCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _notificationDetailCancellation = cancellation;
        await detail.LoadAsync(forceRefresh: true, cancellation.Token);
    }

    private void ClearNotificationSelection()
    {
        SelectedNotification = null;
        NotificationDetail = null;
        CloseBoopPreview();
    }

    private void UpdateNotifications(IReadOnlyList<VrcNotification> notifications)
    {
        var existingById = Notifications
            .GroupBy(notification => notification.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var desired = new List<NotificationItem>();
        foreach (var notification in notifications
                     .Where(notification => !string.IsNullOrWhiteSpace(notification.Id) &&
                                            !_clearedNotificationIds.Contains(notification.Id))
                     .GroupBy(notification => notification.Id)
                     .Select(group => group.First()))
        {
            if (_resolvedFriendRequestIds.Contains(notification.Id) &&
                string.Equals(notification.Type, "friendRequest", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (existingById.TryGetValue(notification.Id, out var existing))
            {
                // A refresh can return an older, unseen copy. Once read here,
                // keep it read instead of bringing the row back as new.
                var wasSeen = existing.Seen;
                notification.CopyResolvedBoopAttachmentFrom(existing.Snapshot);
                notification.CopyResolvedGroupMetadataFrom(existing.Snapshot);
                existing.Update(notification);
                if (wasSeen)
                {
                    existing.MarkSeen();
                }

                desired.Add(existing);
            }
            else
            {
                desired.Add(new NotificationItem(notification));
            }
        }

        // VRChat's notification feed can stop returning an invite or request
        // after it is read. Keep those rows so the destination and actions stay
        // available. Friend requests stay until this app accepts them.
        var desiredIds = desired.Select(notification => notification.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var existing in Notifications)
        {
            if (desiredIds.Contains(existing.Id))
            {
                continue;
            }

            var keep = existing.IsPersistent ||
                       (existing.IsFriendRequest && !_resolvedFriendRequestIds.Contains(existing.Id));
            if (!keep)
            {
                continue;
            }

            desired.Add(existing);
            desiredIds.Add(existing.Id);
        }

        desired = desired
            .OrderByDescending(notification => notification.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();

        SyncCollection(Notifications, desired, notification => notification.Id);
        UpdateUnreadState();
        PersistNotifications();
        HydrateBoopAttachments(Notifications);
        HydrateGroupMetadata(Notifications);

        if (_selectedNotification is not null && !Notifications.Contains(_selectedNotification))
        {
            SelectedNotification = null;
            NotificationDetail = null;
        }
    }

    private void HydrateBoopAttachments(IEnumerable<NotificationItem> notifications)
    {
        foreach (var item in notifications)
        {
            if (!item.IsBoop || !item.HasBoopAttachment)
            {
                continue;
            }

            var snapshot = item.Snapshot;
            if (!string.IsNullOrWhiteSpace(snapshot.BoopName) ||
                !string.IsNullOrWhiteSpace(snapshot.BoopImageUrl))
            {
                continue;
            }

            _ = HydrateBoopAttachmentAsync(item);
        }
    }

    /// <summary>
    /// Group notifications identify the group by id. Resolve that id once so
    /// the row uses the group's real name and icon rather than guessing from
    /// an announcement title.
    /// </summary>
    private void HydrateGroupMetadata(IEnumerable<NotificationItem> notifications)
    {
        foreach (var item in notifications)
        {
            if (!item.IsGroup || !item.HasGroupId)
            {
                continue;
            }

            var snapshot = item.Snapshot;
            if (!string.IsNullOrWhiteSpace(snapshot.ResolvedGroupName) &&
                !string.IsNullOrWhiteSpace(snapshot.ResolvedGroupIconUrl))
            {
                continue;
            }

            if (_groupMetadataCache.TryGetValue(item.GroupId, out var cached))
            {
                item.ApplyGroupMetadata(cached.Name, cached.IconUrl);
                continue;
            }

            _ = HydrateGroupMetadataAsync(item);
        }
    }

    private async Task HydrateGroupMetadataAsync(NotificationItem item)
    {
        if (!_groupHydrationIds.Add(item.Id))
        {
            return;
        }

        await _groupMetadataGate.WaitAsync();
        try
        {
            if (_groupMetadataCache.TryGetValue(item.GroupId, out var cached))
            {
                item.ApplyGroupMetadata(cached.Name, cached.IconUrl);
                return;
            }

            var group = await _api.GetGroupAsync(item.GroupId);
            if (group is not null)
            {
                var iconUrl = string.IsNullOrWhiteSpace(group.IconUrl)
                    ? group.ImageUrl
                    : group.IconUrl;
                _groupMetadataCache[item.GroupId] = (group.Name, iconUrl);
                item.ApplyGroupMetadata(group.Name, iconUrl);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"group metadata {item.Id}: {ex.Message}");
        }
        finally
        {
            _groupMetadataGate.Release();
            _groupHydrationIds.Remove(item.Id);
        }
    }

    private async Task HydrateBoopAttachmentAsync(NotificationItem item)
    {
        if (!_boopHydrationIds.Add(item.Id))
        {
            return;
        }

        var snapshot = item.Snapshot;
        try
        {
            var (name, imageUrl, kind) = await ResolveBoopAttachmentAsync(snapshot);
            item.ApplyBoopAttachment(name, imageUrl, kind);
            PersistNotifications();
            OpenBoopPreviewCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"boop attachment {item.Id}: {ex}");
        }
        finally
        {
            _boopHydrationIds.Remove(item.Id);
        }
    }

    private async Task<(string? Name, string? ImageUrl, string? Kind)> ResolveBoopAttachmentAsync(
        VrcNotification notification)
    {
        var senderId = notification.SenderUserId ?? string.Empty;
        var inventoryItemId = notification.BoopInventoryItemId;
        if (!string.IsNullOrWhiteSpace(inventoryItemId))
        {
            var name = "Inventory emoji";
            string? imageUrl = null;
            try
            {
                var item = await _api.GetUserInventoryItemAsync(senderId, inventoryItemId);
                if (item is not null)
                {
                    name = item.FriendlyName;
                    imageUrl = item.DisplayImageUrl;
                    if (string.IsNullOrWhiteSpace(imageUrl) &&
                        !string.IsNullOrWhiteSpace(item.Metadata?.FileId))
                    {
                        var file = await _api.GetFileAsync(item.Metadata.FileId);
                        imageUrl = file?.ImageUrl;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"boop inventory {inventoryItemId}: {ex.Message}");
            }

            return (name, imageUrl, "inventory");
        }

        var emojiId = notification.BoopEmojiId;
        if (string.IsNullOrWhiteSpace(emojiId))
        {
            return (null, null, null);
        }

        if (emojiId.StartsWith("file_", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var file = await _api.GetFileAsync(emojiId);
                if (file is not null)
                {
                    return (file.FriendlyName, file.ImageUrl, "custom");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"boop emoji file {emojiId}: {ex.Message}");
            }

            return ("Custom emoji", null, "custom");
        }

        var builtIn = BoopEmojiOption.FindById(emojiId);
        if (builtIn is not null)
        {
            return (builtIn.Name, builtIn.ImageUrl, "emoji");
        }

        var fallback = emojiId.StartsWith("default_", StringComparison.OrdinalIgnoreCase)
            ? emojiId["default_".Length..]
            : emojiId;
        fallback = fallback.Replace('_', ' ').Trim();
        if (fallback.Length == 0)
        {
            fallback = "emoji";
        }

        var display = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(fallback.ToLowerInvariant());
        return (display, null, emojiId.StartsWith("default_", StringComparison.OrdinalIgnoreCase)
            ? "emoji"
            : "custom");
    }

    private void ResolveFriendRequest(NotificationItem item)
    {
        _resolvedFriendRequestIds.Add(item.Id);
        Notifications.Remove(item);

        if (ReferenceEquals(_selectedNotification, item))
        {
            SelectedNotification = null;
            NotificationDetail = null;
        }

        UpdateUnreadState();
    }

    private void UpdateUnreadState()
    {
        UnreadNotificationCount = Notifications.Count(notification => !notification.Seen);
        NotificationsSummary = Notifications.Count == 0
            ? "You're all caught up."
            : UnreadNotificationCount > 0
                ? $"{UnreadNotificationCount} unread · {Notifications.Count} total"
                : $"{Notifications.Count} notifications · all read";

        _notificationsNav.Badge = UnreadNotificationCount > 0
            ? UnreadNotificationCount.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

        OnPropertyChanged(nameof(HasUnreadNotifications));
        OnPropertyChanged(nameof(HasNoNotifications));
        RefreshNotificationsView();
        MarkAllReadCommand.RaiseCanExecuteChanged();
    }

    private void ConfigureTwoFactor(IReadOnlyList<string> methods)
    {
        _twoFactorMethods.Clear();
        _twoFactorMethods.AddRange(methods);

        var hasTotp = methods.Contains("totp", StringComparer.OrdinalIgnoreCase);
        var hasEmail = methods.Contains("emailOtp", StringComparer.OrdinalIgnoreCase);
        var hasRecovery = methods.Contains("otp", StringComparer.OrdinalIgnoreCase);

        _twoFactorMethod = hasTotp
            ? TwoFactorMethod.Authenticator
            : hasEmail
                ? TwoFactorMethod.Email
                : hasRecovery
                    ? TwoFactorMethod.Recovery
                    : TwoFactorMethod.Authenticator;

        TwoFactorCode = string.Empty;
        IsTwoFactorRequired = true;
        OnPropertyChanged(nameof(TwoFactorHint));
        OnPropertyChanged(nameof(TwoFactorMethodLabel));
        OnPropertyChanged(nameof(ShowEmailSwitchLink));
        OnPropertyChanged(nameof(ShowTotpSwitchLink));
        OnPropertyChanged(nameof(ShowRecoverySwitchLink));
        RaiseCommandStates();
    }

    private void SetTwoFactorMethod(TwoFactorMethod method)
    {
        _twoFactorMethod = method;
        TwoFactorCode = string.Empty;
        ClearError();

        OnPropertyChanged(nameof(TwoFactorHint));
        OnPropertyChanged(nameof(TwoFactorMethodLabel));
        OnPropertyChanged(nameof(ShowEmailSwitchLink));
        OnPropertyChanged(nameof(ShowTotpSwitchLink));
        OnPropertyChanged(nameof(ShowRecoverySwitchLink));
        UseEmailCodeCommand.RaiseCanExecuteChanged();
        UseTotpCodeCommand.RaiseCanExecuteChanged();
        UseRecoveryCodeCommand.RaiseCanExecuteChanged();
    }

    private void SignOutLocally(string? message)
    {
        _realtime.Stop();
        CurrentUser = null;
        IsLoggedIn = false;
        _historyAccountId = string.Empty;
        _historyReady = false;
        _contextLogSignature = string.Empty;
        _lastLoggedAvatars.Clear();
        _lastLoggedAvatarLocation = string.Empty;
        _contextLiveRefreshPending = false;
        _instanceHistoryRecorder?.Stop();
        IsTwoFactorRequired = false;
        TwoFactorCode = string.Empty;
        _twoFactorMethods.Clear();

        _avatarUrl = null;
        AvatarImage = null;

        // Sign-out must drop the previous account's avatars, groups and feed.
        ResetAccountScopedViewModels();
        _api.ClearCaches();
        _notificationsAccountId = string.Empty;
        _clearedNotificationIds = new HashSet<string>(StringComparer.Ordinal);
        CloseInviteComposer();
        CloseResponseComposer();

        CloseWorldDialog();
        ClearFriendSelection();
        ClearPlayerSearch();
        ClearNotificationSelection();
        CloseBoopPreview();
        QuickJoinInput = string.Empty;
        ClearInfo();
        _greetingName = string.Empty;
        OnPropertyChanged(nameof(GreetingText));
        SelectedFriendFilter = FriendFilters[0];
        SelectedNotificationFilter = NotificationFilters[0];
        NotificationSearch = string.Empty;

        Friends.Clear();
        _friendsView.Refresh();
        UpdateFriendsSummary();

        Notifications.Clear();
        UpdateUnreadState();

        Worlds.Clear();
        _worldsLoaded = false;
        WorldsSummary = string.Empty;
        WorldsEmptyMessage = "Search for a world by name to get started.";
        OnPropertyChanged(nameof(HasNoWorlds));

        _lastUpdated = null;
        _lastFullFriendLoadAt = DateTimeOffset.MinValue;
        OnPropertyChanged(nameof(LastUpdatedText));

        _suppressStatusDirty = true;
        try
        {
            SelectedStatus = Statuses[0];
            StatusMessageInput = string.Empty;
        }
        finally
        {
            _suppressStatusDirty = false;
        }
        IsStatusDirty = false;

        SelectedSection = Sections[0];
        OnAllPropertiesChanged();
        RaiseCommandStates();

        if (message is null)
        {
            ClearError();
        }
        else
        {
            ShowError(message);
        }
    }

    // -------------------------------------------------------------- helpers

    private void OnBrowserFocusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BraveBrowserViewModel.IsFocusMode))
        {
            OnPropertyChanged(nameof(IsAnyBrowserFocused));
        }
    }

    private void PushBusy(string text)
    {
        _busyDepth++;
        BusyText = text;
        IsBusy = true;
    }

    private void PopBusy()
    {
        _busyDepth = Math.Max(0, _busyDepth - 1);
        if (_busyDepth == 0)
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private void ShowError(string message)
    {
        ErrorMessage = message;
        OnPropertyChanged(nameof(HasError));
    }

    private void ShowInfo(string message)
    {
        InfoMessage = message;
        OnPropertyChanged(nameof(HasInfo));
    }

    private void ClearInfo()
    {
        if (InfoMessage is null)
        {
            return;
        }

        InfoMessage = null;
        OnPropertyChanged(nameof(HasInfo));
    }

    private void ClearError()
    {
        if (ErrorMessage is null)
        {
            return;
        }

        ErrorMessage = null;
        OnPropertyChanged(nameof(HasError));
    }

    private static string StatusLabel(string? status) => (status ?? string.Empty).ToLowerInvariant() switch
    {
        "active" => "Online",
        "join me" => "Join Me",
        "ask me" => "Ask Me",
        "busy" => "Busy",
        "offline" => "Offline",
        var value when !string.IsNullOrWhiteSpace(value) => char.ToUpperInvariant(value[0]) + value[1..],
        _ => "Unknown"
    };

    private T Track<T>(T command) where T : IRelayCommand
    {
        _commands.Add(command);
        return command;
    }

    private void RaiseCommandStates()
    {
        foreach (var command in _commands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private static void SyncCollection<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> desired,
        Func<T, string> keySelector)
        where T : class
    {
        var desiredKeys = desired.Select(keySelector).ToHashSet(StringComparer.Ordinal);

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredKeys.Contains(keySelector(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var key = keySelector(desired[i]);
            var existingIndex = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (string.Equals(keySelector(target[j]), key, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                target.Insert(i, desired[i]);
            }
            else if (existingIndex != i)
            {
                target.Move(existingIndex, i);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CosmeticCatalog.Changed -= OnCosmeticCatalogChanged;
        _friendChanges.Dispose();
        _friendDetailCancellation?.Cancel();
        _friendDetailCancellation?.Dispose();
        _worldDialogCancellation?.Cancel();
        _worldDialogCancellation?.Dispose();
        _instanceHistoryTimer?.Stop();
        _activityPollTimer?.Stop();
        _bioPollTimer?.Stop();
        _instanceHistoryRecorder?.Stop();
        _realtime.EventReceived -= OnRealtimeEvent;
        _realtime.StatusChanged -= OnRealtimeStatusChanged;
        _realtime.Dispose();
        InstanceHistoryViewModel?.Dispose();
        CreatorResourcesViewModel.PropertyChanged -= OnBrowserFocusChanged;
        BraveBrowserViewModel.PropertyChanged -= OnBrowserFocusChanged;
        DiscordBrowserViewModel.PropertyChanged -= OnBrowserFocusChanged;
        ChatboxViewModel.Dispose();
        _api.Dispose();
        _images.Dispose();
    }
}
