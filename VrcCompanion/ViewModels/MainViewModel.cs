using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
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
    private readonly ICollectionView _friendsView;
    private readonly ICollectionView _friendLocationsView;
    private readonly ICollectionView _friendChangesView;
    private readonly ICollectionView _notificationsView;
    private readonly DispatcherTimer _clockTimer;
    private string _greetingName = string.Empty;
    private readonly HashSet<string> _favoriteFriends;
    private readonly HashSet<string> _favoriteWorlds;

    private NavItem _selectedSection = null!;
    private StatusOption _selectedStatus = null!;
    private string _statusMessageInput = string.Empty;
    private bool _isStatusDirty;
    private bool _loadingRemainingFriends;
    private Task? _fullFriendLoad;
    private bool _suppressStatusDirty;
    private bool _initialized;
    private bool _worldsLoaded;
    private bool _disposed;
    private int _busyDepth;
    private string? _avatarUrl;
    private DateTimeOffset? _lastUpdated;
    private TwoFactorMethod _twoFactorMethod = TwoFactorMethod.Authenticator;
    private ImageSource? _avatarImage;
    private FriendItem? _selectedFriend;
    private FriendDetailViewModel? _friendDetail;
    private CancellationTokenSource? _friendDetailCancellation;
    private WorldDialogViewModel? _worldDialog;
    private CancellationTokenSource? _worldDialogCancellation;
    private NotificationItem? _selectedNotification;
    private NotificationDetailViewModel? _notificationDetail;
    private CancellationTokenSource? _notificationDetailCancellation;
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

    // --------------------------------------------------------- new features

    private AvatarViewModel? _avatarViewModel;
    private GroupViewModel? _groupViewModel;
    private ActivityFeedViewModel? _activityFeedViewModel;
    private MultiAccountViewModel? _multiAccountViewModel;

    public AvatarViewModel? AvatarViewModel
    {
        get => _avatarViewModel;
        private set => SetProperty(ref _avatarViewModel, value);
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

    public ActivityFeedViewModel? ActivityFeedViewModel
    {
        get => _activityFeedViewModel;
        private set => SetProperty(ref _activityFeedViewModel, value);
    }

    public MultiAccountViewModel? MultiAccountViewModel
    {
        get => _multiAccountViewModel;
        private set => SetProperty(ref _multiAccountViewModel, value);
    }

    public MainViewModel()
        : this(new VrcApiClient())
    {
    }

    internal MainViewModel(VrcApiClient api)
    {
        _api = api;
        _images = ImageLoader.Shared;
        _settingsStore = SettingsStore.Shared;
        _settings = _settingsStore.Settings;
        _favoriteFriends = new HashSet<string>(_settings.FavoriteFriends, StringComparer.Ordinal);
        _favoriteWorlds = new HashSet<string>(_settings.FavoriteWorlds, StringComparer.Ordinal);

        _notificationsNav = new NavItem("notifications", "Notifications", "\uEA8F");
        _activityNav = new NavItem("activity", "Activity", "\uE790");
        Sections = new ObservableCollection<NavItem>
        {
            new("profile", "Profile", "\uE77B"),
            _activityNav,
            new("friends", "Friends", "\uE716"),
            _notificationsNav,
            new("worlds", "Worlds", "\uE909"),
            new("avatars", "Avatars", "\uE7FD"),
            new("favorites", "Favourites", "\uE735"),
            new("avatarsearch", "Avatar Search", "\uE721"),
            new("groups", "Groups", "\uE70E"),
            new("resources", "Creator Resources", "\uE719"),
            new("brave", "Brave Browser", "\uE774"),
            new("discord", "Discord", "\uE8BD"),
            new("chatbox", "Chatbox", "\uE8BD"),
            new("media", "Media", "\uEB9F"),
            new("accounts", "Accounts", "\uE716"),
            new("settings", "Settings", "\uE713")
        };
        _selectedSection = Sections[0];
        MediaViewModel = new MediaLibraryViewModel();
        ChatboxViewModel = new ChatboxViewModel();
        CreatorResourcesViewModel = new CreatorResourcesViewModel();
        BraveBrowserViewModel = new BraveBrowserViewModel();
        DiscordBrowserViewModel = new DiscordBrowserViewModel();

        Statuses = new ObservableCollection<StatusOption>
        {
            new("active", "Online", "#3DDC97"),
            new("join me", "Join Me", "#FFC857"),
            new("ask me", "Ask Me", "#FF9F43"),
            new("busy", "Busy", "#FF5C7A"),
            new("offline", "Offline", "#6B7488")
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
            new("joinme", "Join Me"),
            new("offline", "Offline")
        };
        _selectedFriendFilter = FriendFilters[0];

        Notifications = new ObservableCollection<NotificationItem>();
        _notificationsView = CollectionViewSource.GetDefaultView(Notifications);
        _notificationsView.Filter = FilterNotification;
        NotificationFilters = new ObservableCollection<FilterOption>
        {
            new("all", "All"),
            new("unread", "Unread"),
            new("requests", "Requests"),
            new("invites", "Invites"),
            new("other", "Other")
        };
        _selectedNotificationFilter = NotificationFilters[0];

        Worlds = new ObservableCollection<WorldItem>();

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
        RefreshFriendProfileCommand = Track(new AsyncRelayCommand(
            RefreshFriendProfileAsync,
            () => SelectedFriendDetail is not null && !IsBusy));
        JoinFriendCommand = Track(new AsyncRelayCommand<FriendItem>(
            JoinFriendAsync,
            friend => friend is not null && friend.IsOnline && LaunchService.IsLocation(friend.Location) && !IsBusy));
        InviteFriendCommand = Track(new AsyncRelayCommand<FriendItem>(
            InviteFriendAsync,
            friend => friend is not null && IsLoggedIn && !IsBusy && LaunchService.IsLocation(CurrentUser?.Location)));
        RequestInviteFriendCommand = Track(new AsyncRelayCommand<FriendItem>(
            RequestInviteFriendAsync,
            friend => friend is not null && friend.IsOnline && friend.IsInPrivateOrInviteWorld && !IsBusy));
        OpenFriendProfileCommand = Track(new RelayCommand<FriendItem>(
            OpenFriendProfile,
            friend => friend is not null));
        CopyFriendLocationCommand = Track(new RelayCommand<FriendItem>(
            CopyFriendLocation,
            friend => friend is not null && LaunchService.IsLocation(friend.Location)));
        OpenFriendWorldCommand = Track(new AsyncRelayCommand<FriendItem>(
            OpenFriendWorldAsync,
            friend => friend is not null && LaunchService.GetWorldIdFromLocation(friend.Location) is not null && !IsBusy));
        OpenFriendLocationsCommand = Track(new AsyncRelayCommand(
            OpenFriendLocationsAsync,
            () => IsLoggedIn && !IsBusy));
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
            entry => entry is not null && entry.HasAvatar && !IsBusy));
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
    }

    // ------------------------------------------------------------ shell state

    public ObservableCollection<NavItem> Sections { get; }

    public NavItem SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is null)
            {
                return;
            }

            if (!SetProperty(ref _selectedSection, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SectionTitle));
            OnPropertyChanged(nameof(SectionSubtitle));

            // Automatic browse retries only run while their tab is on screen.
            if (AvatarViewModel is not null)
            {
                AvatarViewModel.IsBrowseTabActive = value.Key == "avatarsearch";
            }

            // Media is local, so it loads even before a session is restored.
            if (value.Key == "media")
            {
                _ = MediaViewModel.EnsureLoadedAsync();
            }

            if (!IsLoggedIn)
            {
                return;
            }

            // Sections load themselves on first visit instead of needing Refresh.
            switch (value.Key)
            {
                case "worlds":
                    _ = EnsureWorldsLoadedAsync();
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
        "notifications" => "Invites, friend requests, and other VRChat activity.",
        "worlds" => "Search the public VRChat world catalog.",
        "avatars" => "Browse, search, and manage your VRChat avatars.",
        "favorites" => "The avatars you have favourited in VRChat, slot by slot.",
        "avatarsearch" => "Search every avatar VRChat knows about and wear or save it.",
        "groups" => "Manage your VRChat groups, roles, and members.",
        "resources" => "Open external creator marketplaces and official VRChat tools.",
        "brave" => "Search the web and open Spotify, YouTube, or any site in the app.",
        "discord" => "Sign in to Discord and search friends, servers, or settings.",
        "activity" => "See what your friends have been up to.",
        "chatbox" => "Send and rotate VRChat chatbox messages over OSC.",
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
        : $"Updated {_lastUpdated.Value:HH:mm}";

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

    public string TrustLabel
    {
        get
        {
            var tags = CurrentUser?.Tags ?? new List<string>();
            if (tags.Contains("system_trust_legend", StringComparer.OrdinalIgnoreCase)) return "Trusted Legend";
            if (tags.Contains("system_trust_veteran", StringComparer.OrdinalIgnoreCase)) return "Trusted User";
            if (tags.Contains("system_trust_trusted", StringComparer.OrdinalIgnoreCase)) return "Known User";
            if (tags.Contains("system_trust_known", StringComparer.OrdinalIgnoreCase)) return "User";
            if (tags.Contains("system_trust_basic", StringComparer.OrdinalIgnoreCase)) return "New User";
            if (tags.Contains("system_trust_visitor", StringComparer.OrdinalIgnoreCase)) return "Visitor";
            return "User";
        }
    }

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

    public RelayCommand<string> ChooseLaunchModeCommand { get; }

    public RelayCommand CancelLaunchChoiceCommand { get; }

    public RelayCommand<string> SetLaunchModeCommand { get; }

    public RelayCommand OpenVrChatCommand { get; }

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

    public string LaunchDefaultText => $"Every join asks desktop or VR. Last used: {LaunchModeLabel}.";

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

    public string LaunchChoiceSubtitle => $"VRChat starts in the mode you pick and then joins the world. Last used: {LaunchModeLabel}.";

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

    public string ClockText => DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);

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
    public AsyncRelayCommand SearchWorldsCommand { get; }
    public AsyncRelayCommand<string> SearchSuggestionCommand { get; }
    public RelayCommand<WorldItem> OpenWorldCommand { get; }
    public RelayCommand CloseFriendDetailCommand { get; }

    public RelayCommand OpenOwnProfileCommand { get; }
    public AsyncRelayCommand RefreshFriendProfileCommand { get; }
    public AsyncRelayCommand<FriendItem> JoinFriendCommand { get; }
    public AsyncRelayCommand<FriendItem> InviteFriendCommand { get; }
    public AsyncRelayCommand<FriendItem> RequestInviteFriendCommand { get; }
    public RelayCommand<FriendItem> OpenFriendProfileCommand { get; }
    public RelayCommand<FriendItem> CopyFriendLocationCommand { get; }
    public AsyncRelayCommand<FriendItem> OpenFriendWorldCommand { get; }
    public AsyncRelayCommand OpenFriendLocationsCommand { get; }
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

    // ----------------------------------------------------------------- flows

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        // Initialize new feature view models on the signed-in client
        AvatarViewModel = new AvatarViewModel(_api);
        GroupViewModel = new GroupViewModel(_api, OpenLaunchChooser);
        ActivityFeedViewModel = new ActivityFeedViewModel(_api);
        ActivityFeedViewModel.RefreshRequested = LoadAllFriendsAsync;
        ActivityFeedViewModel.FriendListChanged += OnFriendListChanged;
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
            SelectedSection = _activityNav;
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
    /// Refresh used by the background timer. Offline friends change rarely and
    /// cost one request per 100 friends, so only the fast-changing data is
    /// fetched here; the full download stays on startup and manual refresh.
    /// </summary>
    public async Task RefreshInBackgroundAsync()
    {
        if (!IsLoggedIn || IsBusy)
        {
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
        var friends = await _api.GetFriendsAsync(forceRefresh: true);
        if (!IsLoggedIn)
        {
            return;
        }

        UpdateFriends(friends);
        ActivityFeedViewModel?.ApplyFriends(friends);
        _lastUpdated = DateTimeOffset.Now;
        OnPropertyChanged(nameof(LastUpdatedText));
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
            if (ActivityFeedViewModel is { HasLoaded: false })
            {
                ActivityFeedViewModel.ShowOnlineFriends(friends);
            }
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
            await LoadRemainingFriendsAsync();
        }
    }

    private async Task CompleteLoginAsync(VrcUser user)
    {
        ApplyCurrentUser(user);
        IsLoggedIn = true;
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

        SelectedSection = _activityNav;
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
        IsTwoFactorRequired = false;
        SelectedSection = _activityNav;

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
        FriendChanges.Clear();
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
            await _api.MarkNotificationSeenAsync(item.Id);
            item.MarkSeen();
            UpdateUnreadState();
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
                await _api.MarkNotificationSeenAsync(notification.Id);
                notification.MarkSeen();
                UpdateUnreadState();
            }

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
            new FriendItem(user),
            _api,
            _images,
            openGroup: OpenGroupInfoFromProfile);

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
            item.MarkSeen();
            UpdateUnreadState();
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
                await _api.MarkNotificationSeenAsync(item.Id);
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
        try
        {
            LaunchService.Launch(location, chosen);
            ClearError();
            ShowInfo($"Launching VRChat in {chosen.ToString().ToLowerInvariant()} mode. If nothing happens, make sure VRChat is installed and the vrchat:// link handler is registered.");
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

    private async Task LoadFriendDetailAsync(FriendItem? friend, bool forceRefresh = false)
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
        var detail = new FriendDetailViewModel(
            friend,
            _api,
            _images,
            openGroup: OpenGroupInfoFromProfile);
        SelectedFriendDetail = detail;
        await detail.LoadAsync(forceRefresh, cancellation.Token);
    }

    private async Task RefreshFriendProfileAsync()
    {
        var detail = SelectedFriendDetail;
        if (detail is null)
        {
            return;
        }

        _friendDetailCancellation?.Cancel();
        _friendDetailCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _friendDetailCancellation = cancellation;
        await detail.LoadAsync(forceRefresh: true, cancellation.Token);
    }

    private void ClearFriendSelection()
    {
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
            openGroup: OpenGroupInfoFromProfile);
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

        // Every join asks desktop or VR; the choice becomes the next default.
        OpenLaunchChooser(friend.Location, friend.DisplayName);
        await Task.CompletedTask;
    }

    private void OpenLaunchChooser(string? location, string friendName)
    {
        _pendingLaunchLocation = location;
        _pendingLaunchFriendName = friendName;
        IsLaunchChooserOpen = true;
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
            LaunchService.LaunchClient(mode);
            ClearError();
            ShowInfo($"Opening VRChat in {mode.ToString().ToLowerInvariant()} mode.");
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

    private void OpenVrChat() => OpenLaunchChooser(null, string.Empty);

    private async Task InviteFriendAsync(FriendItem? friend)
    {
        if (friend is null || IsBusy)
        {
            return;
        }

        var location = CurrentUser?.Location;
        if (!LaunchService.IsLocation(location))
        {
            ShowError("Join a world first, then invite friends to it.");
            return;
        }

        PushBusy($"Inviting {friend.DisplayName}…");
        try
        {
            await _api.InviteFriendAsync(friend.Id, location!);
            ShowInfo($"Invite sent to {friend.DisplayName}.");
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat to send that invite.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not send that invite.");
        }
        finally
        {
            PopBusy();
        }
    }

    private async Task RequestInviteFriendAsync(FriendItem? friend)
    {
        if (friend is null || IsBusy)
        {
            return;
        }

        PushBusy($"Asking {friend.DisplayName} for an invite…");
        try
        {
            await _api.RequestInviteAsync(friend.Id);
            ShowInfo($"Invite request sent to {friend.DisplayName}.");
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat to request an invite.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not request an invite.");
        }
        finally
        {
            PopBusy();
        }
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

    private async Task OpenActivityAvatarAsync(ActivityEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.AvatarId) || IsBusy)
        {
            return;
        }

        PushBusy("Loading that avatar…");
        try
        {
            var avatar = await _api.GetAvatarAsync(entry.AvatarId);
            if (avatar is null)
            {
                ShowError("VRChat did not return that avatar.");
                return;
            }

            if (avatar.IsPrivate)
            {
                ShowError("That avatar is private, so it cannot be worn or favourited from activity.");
                return;
            }

            avatar.IsWearing = string.Equals(
                CurrentUser?.CurrentAvatar,
                avatar.Id,
                StringComparison.Ordinal);
            AvatarViewModel?.ShowAvatarDetails(avatar);
        }
        catch (VrcApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach VRChat to load that avatar.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowError("Could not load that avatar.");
        }
        finally
        {
            PopBusy();
        }
    }

    private async Task OpenActivityUserAsync(ActivityEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.UserId) || IsBusy)
        {
            return;
        }

        var friend = new FriendItem(new VrcUser
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

    private void ApplyCurrentUser(VrcUser user)
    {
        CurrentUser = user;
        _greetingName = (user.DisplayName ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;

        if (!IsStatusDirty)
        {
            SetStatusEditorFromUser(user);
        }

        OnAllPropertiesChanged();
        OnPropertyChanged(nameof(GreetingText));
        RaiseCommandStates();

        var url = ImageUrlHelper.PickAvatarThumbnailUrl(
            user.UserIcon,
            user.ProfilePicOverrideThumbnail,
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
                if (existingById.TryGetValue(user.Id, out var existing))
                {
                    existing.Update(user);
                    existing.IsFavorite = _favoriteFriends.Contains(user.Id);
                    return existing;
                }
                return new FriendItem(user)
                {
                    IsFavorite = _favoriteFriends.Contains(user.Id)
                };
            })
            .OrderByDescending(friend => friend.IsFavorite)
            .ThenByDescending(friend => friend.IsOnline)
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
    }

    /// <summary>
    /// Keeps the selected friend and their open popup across a list rebuild
    /// (startup fill, background refresh, filter change).
    /// </summary>
    private void RestoreSelection(FriendItem? keepSelected, FriendDetailViewModel? keepDetail)
    {
        if (keepSelected is not null && Friends.Contains(keepSelected))
        {
            if (!ReferenceEquals(_selectedFriend, keepSelected))
            {
                _selectedFriend = keepSelected;
                OnPropertyChanged(nameof(SelectedFriend));
            }

            if (keepDetail is not null && !ReferenceEquals(_friendDetail, keepDetail))
            {
                _friendDetail = keepDetail;
                OnPropertyChanged(nameof(SelectedFriendDetail));
                OnPropertyChanged(nameof(HasSelectedFriend));
            }

            keepDetail?.UpdateFriend(keepSelected);
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

        if (ShowOnlineOnly && !friend.IsOnline)
        {
            return false;
        }

        switch (SelectedFriendFilter?.Key)
        {
            case "online" when !friend.IsOnline:
                return false;
            case "joinme" when !(friend.IsOnline &&
                                 string.Equals(friend.StatusLabel, "Join Me", StringComparison.OrdinalIgnoreCase)):
                return false;
            case "offline" when friend.IsOnline:
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

    private void OnFriendListChanged(FriendListDelta delta)
    {
        var timestamp = DateTimeOffset.UtcNow;
        foreach (var removed in delta.Removed)
        {
            FriendChanges.Insert(0, new FriendChangeItem(new FriendItem(removed), false, timestamp));
        }

        foreach (var added in delta.Added)
        {
            FriendChanges.Insert(0, new FriendChangeItem(new FriendItem(added), true, timestamp));
        }

        while (FriendChanges.Count > 100)
        {
            FriendChanges.RemoveAt(FriendChanges.Count - 1);
        }

        _friendChangesView.Refresh();
        OnPropertyChanged(nameof(FriendChangesSummary));
        OnPropertyChanged(nameof(HasNoFriendChanges));
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
        OnlineFriendCount = Friends.Count(friend => friend.IsOnline);
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
            case "requests" when notification.Category != "request":
                return false;
            case "invites" when notification.Category != "invite":
                return false;
            case "other" when notification.Category != "other":
                return false;
        }

        var query = (NotificationSearch ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return true;
        }

        return notification.SenderName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
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
                "requests" => "No friend requests right now.",
                "invites" => "No world invites right now.",
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
    }

    private void UpdateNotifications(IReadOnlyList<VrcNotification> notifications)
    {
        var existingById = Notifications
            .GroupBy(notification => notification.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var desired = notifications
            .Where(notification => !string.IsNullOrWhiteSpace(notification.Id))
            .GroupBy(notification => notification.Id)
            .Select(group => group.First())
            .OrderByDescending(notification => notification.CreatedAt ?? DateTimeOffset.MinValue)
            .Select(notification =>
            {
                if (existingById.TryGetValue(notification.Id, out var existing))
                {
                    existing.Update(notification);
                    return existing;
                }
                return new NotificationItem(notification);
            })
            .ToList();

        SyncCollection(Notifications, desired, notification => notification.Id);
        UpdateUnreadState();

        if (_selectedNotification is not null && !Notifications.Contains(_selectedNotification))
        {
            SelectedNotification = null;
            NotificationDetail = null;
        }
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
        CurrentUser = null;
        IsLoggedIn = false;
        IsTwoFactorRequired = false;
        TwoFactorCode = string.Empty;
        _twoFactorMethods.Clear();

        _avatarUrl = null;
        AvatarImage = null;

        // Sign-out must drop the previous account's avatars, groups and feed.
        ResetAccountScopedViewModels();
        _api.ClearCaches();

        CloseWorldDialog();
        ClearFriendSelection();
        ClearNotificationSelection();
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
        _friendDetailCancellation?.Cancel();
        _friendDetailCancellation?.Dispose();
        _worldDialogCancellation?.Cancel();
        _worldDialogCancellation?.Dispose();
        ChatboxViewModel.Dispose();
        _api.Dispose();
        _images.Dispose();
    }
}
