using System.Diagnostics;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Everything shown in the friend detail pane. Base friend data appears
/// immediately; the richer public profile, world, and mutual counts fill in
/// asynchronously so selecting a friend never blocks the UI.
/// </summary>
public sealed class FriendDetailViewModel : ViewModelBase
{
    private static readonly Brush DefaultBannerBrush = MakeBrush("#2A2145");
    private static readonly Brush SelfOnlineBrush = MakeBrush("#3BD671");
    private static readonly Brush SelfJoinMeBrush = MakeBrush("#4AA8FF");
    private static readonly Brush SelfAskMeBrush = MakeBrush("#F5A623");
    private static readonly Brush SelfBusyBrush = MakeBrush("#FF5A5A");
    private static readonly Brush SelfOfflineBrush = MakeBrush("#6B7280");

    private readonly VrcApiClient _api;
    private readonly ImageLoader _images;
    private readonly FriendNotesStore _notes;
    private FriendItem _friend;
    private string _noteText = string.Empty;
    private string _savedNoteText = string.Empty;
    private string _noteStatusText = string.Empty;
    private VrcProfile? _profile;
    private VrcMutuals? _mutuals;
    private ImageSource? _avatarImage;
    private ImageSource? _bannerImage;
    private string? _worldName;
    private string? _error;
    private bool _isLoading;

    public FriendDetailViewModel(
        FriendItem friend,
        VrcApiClient api,
        ImageLoader images,
        FriendNotesStore? notes = null,
        bool isSelf = false,
        Action<VrcProfileGroup>? openGroup = null)
    {
        _friend = friend;
        _api = api;
        _images = images;
        _notes = notes ?? FriendNotesStore.Shared;
        IsSelf = isSelf;
        OpenGroupCommand = new RelayCommand<VrcProfileGroup>(
            group =>
            {
                IsGroupsOpen = false;
                if (group is not null && openGroup is not null && !string.IsNullOrWhiteSpace(group.Id))
                {
                    openGroup(group);
                }
            },
            group => group is not null && !string.IsNullOrWhiteSpace(group.Id));
        OpenGroupsCommand = new RelayCommand(() =>
        {
            GroupsTab = ProfileGroupsTab.Profile;
            IsGroupsOpen = true;
        });
        CloseGroupsCommand = new RelayCommand(() => IsGroupsOpen = false);
        ShowProfileGroupsCommand = new RelayCommand(() => GroupsTab = ProfileGroupsTab.Profile);
        ShowMutualGroupsCommand = new RelayCommand(() => GroupsTab = ProfileGroupsTab.Mutual);
        ShowMyGroupsCommand = new RelayCommand(() => GroupsTab = ProfileGroupsTab.Mine);

        var stored = _notes.Get(_friend.Id);
        _noteText = stored?.Text ?? string.Empty;
        _savedNoteText = _noteText;
        _noteStatusText = stored is null
            ? "Only you can see this; it stays on this PC."
            : $"Saved locally · {stored.UpdatedAt.ToLocalTime():g}";
        SaveNoteCommand = new RelayCommand(SaveNote);
    }

    /// <summary>
    /// A local note about this friend. VRChat has no note endpoint, so this is
    /// never sent to VRChat and is stored in the app's own JSON file.
    /// </summary>
    public string Note
    {
        get => _noteText;
        set
        {
            if (SetProperty(ref _noteText, value))
            {
                OnPropertyChanged(nameof(HasUnsavedNote));
            }
        }
    }

    /// <summary>True when the draft differs from what is on disk (ignoring edge spaces).</summary>
    public bool HasUnsavedNote
        => !string.Equals((_noteText ?? string.Empty).Trim(), _savedNoteText, StringComparison.Ordinal);

    /// <summary>
    /// Called when the profile pane closes or switches to someone else, so a
    /// half-typed note is not lost without the user pressing Save.
    /// </summary>
    public void SavePendingNote()
    {
        if (HasUnsavedNote)
        {
            SaveNote();
        }
    }

    public string NoteStatusText
    {
        get => _noteStatusText;
        private set => SetProperty(ref _noteStatusText, value);
    }

    public bool HasNote => !string.IsNullOrWhiteSpace(_notes.Get(Friend.Id)?.Text);

    public RelayCommand SaveNoteCommand { get; }

    private void SaveNote()
    {
        var saved = _notes.Set(Friend.Id, _noteText);
        _noteText = saved?.Text ?? string.Empty;
        _savedNoteText = _noteText;
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasUnsavedNote));
        OnPropertyChanged(nameof(HasNote));
        NoteStatusText = saved is null
            ? "Note cleared."
            : $"Saved locally · {saved.UpdatedAt.ToLocalTime():g}";
    }

    public FriendItem Friend => _friend;

    /// <summary>True when this popup is showing the signed-in account itself.</summary>
    public bool IsSelf { get; }

    /// <summary>
    /// Dev-only: pretend this person is standing in a public world, so the Join
    /// button and the desktop/VR picker can be exercised without waiting for a
    /// real in-game sender.
    /// </summary>
    public void ForceLocation(string? location)
    {
        _friend.Update(new VrcUser
        {
            Id = _friend.Id,
            DisplayName = _friend.DisplayName,
            Username = _friend.Username,
            Location = location,
            Status = "active"
        });

        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(JoinLocation));
        OnPropertyChanged(nameof(WorldLine));
        OnPropertyChanged(nameof(WorldId));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(IsOnline));
    }

    /// <summary>Replaces the friend payload with fresher data (e.g. a live location).</summary>
    public void ApplyUser(VrcUser user)
    {
        _friend.Update(user);
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(JoinLocation));
        OnPropertyChanged(nameof(WorldLine));
        OnPropertyChanged(nameof(WorldId));
        OnPropertyChanged(nameof(StatusLabel));
    }

    public VrcProfile? Profile
    {
        get => _profile;
        internal set
        {
            if (SetProperty(ref _profile, value))
            {
                OnAllPropertiesChanged();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public ImageSource? AvatarImage
    {
        get => _avatarImage;
        private set => SetProperty(ref _avatarImage, value);
    }

    public ImageSource? BannerImage
    {
        get => _bannerImage;
        private set => SetProperty(ref _bannerImage, value);
    }

    public string Id => _friend.Id;

    public string DisplayName => string.IsNullOrWhiteSpace(Profile?.DisplayName)
        ? _friend.DisplayName
        : Profile!.DisplayName;

    public string UsernameHandle => string.IsNullOrWhiteSpace(_friend.Username)
        ? string.Empty
        : $"@{_friend.Username}";

    public string Initial => _friend.Initial;

    public bool IsOnline => IsSelf ? SelfPresenceStatus != "offline" && SelfPresenceStatus.Length > 0 : _friend.IsOnline;

    public string StatusLabel => IsSelf
        ? SelfPresenceStatus switch
        {
            "active" => "Online",
            "join me" => "Join Me",
            "ask me" => "Ask Me",
            "busy" => "Busy",
            _ => "Offline"
        }
        : _friend.StatusLabel;

    public Brush StatusBrush => IsSelf
        ? SelfPresenceStatus switch
        {
            "active" => SelfOnlineBrush,
            "join me" => SelfJoinMeBrush,
            "ask me" => SelfAskMeBrush,
            "busy" => SelfBusyBrush,
            _ => SelfOfflineBrush
        }
        : _friend.StatusBrush;

    public string StatusDescription => string.IsNullOrWhiteSpace(Profile?.StatusDescription)
        ? _friend.SubText
        : Profile!.StatusDescription!.Trim();

    public string ActivityText => IsSelf
        ? (string.IsNullOrWhiteSpace(PlatformText) ? "Signed in" : $"Signed in · {PlatformText}")
        : _friend.ActivityText;

    /// <summary>Your presence comes from the signed-in account, not the friend-list location rules.</summary>
    private string SelfPresenceStatus => (_api.CurrentUser?.Status ?? string.Empty).Trim().ToLowerInvariant();

    private static string PlatformLabel(VrcUser? user)
    {
        var platform = (user?.LastPlatform ?? user?.Platform ?? string.Empty).ToLowerInvariant();
        return platform switch
        {
            "standalonewindows" or "standalonewindows64" => "PC",
            "android" => "Quest",
            "ios" => "iOS",
            "web" => "Web",
            "" => string.Empty,
            _ => char.ToUpperInvariant(platform[0]) + platform[1..]
        };
    }

    public string PlatformText => IsSelf ? PlatformLabel(_api.CurrentUser) : _friend.PlatformText;

    public string BioText => string.IsNullOrWhiteSpace(Profile?.Bio)
        ? "No bio set."
        : Profile!.Bio!.Trim();

    public bool HasBio => !string.IsNullOrWhiteSpace(Profile?.Bio);

    public IReadOnlyList<string> BioLinks
        => Profile?.BioLinks?.Where(link => !string.IsNullOrWhiteSpace(link)).Take(4).ToList()
           ?? (IReadOnlyList<string>)Array.Empty<string>();

    public bool HasBioLinks => BioLinks.Count > 0;

    public string TrustLabel => UserPresentation.TrustLabel(
        Profile?.TrustTags is { Count: > 0 } ? Profile.TrustTags : _friend.Tags);

    public bool IsVrcPlus => Profile?.HasVrcPlus == true;

    public bool IsAgeVerified => Profile?.AgeVerified == true;

    public bool HasPronouns => !string.IsNullOrWhiteSpace(Profile?.Pronouns);

    public string PronounsText => Profile?.Pronouns?.Trim() ?? string.Empty;

    public IReadOnlyList<VrcProfileGroup> Groups
        => Profile?.Groups?.List?.Where(group => !string.IsNullOrWhiteSpace(group.Name)).ToList()
           ?? (IReadOnlyList<VrcProfileGroup>)Array.Empty<VrcProfileGroup>();

    public bool HasGroups => Groups.Count > 0;

    /// <summary>The one group this profile represents, shown alone on the card.</summary>
    public VrcProfileGroup? RepresentedGroup => Profile?.RepresentedGroup;

    public bool HasRepresentedGroup => RepresentedGroup is not null;

    public bool HasNoRepresentedGroup => RepresentedGroup is null;

    /// <summary>
    /// The profile payload carries only a sample of a user's groups; its count
    /// tells whether that sample is complete before spending a second request.
    /// </summary>
    internal static bool NeedsFullGroupList(VrcProfile? profile)
        => profile?.Groups is { } groups && groups.Count > groups.List.Count;

    /// <summary>
    /// VRChat may leave the represented group out of the payload's short list.
    /// It still belongs in the profile's groups so its chip is always shown.
    /// </summary>
    internal static bool IncludeRepresentedGroup(VrcProfile? profile)
    {
        if (profile?.RepresentedGroup is not { } represented || string.IsNullOrWhiteSpace(represented.Id))
        {
            return false;
        }

        profile.Groups ??= new VrcProfileGroups();
        if (profile.Groups.List.Any(group => string.Equals(group.Id, represented.Id, StringComparison.Ordinal)))
        {
            return false;
        }

        profile.Groups.List.Insert(0, represented);
        profile.Groups.Count = Math.Max(profile.Groups.Count, profile.Groups.List.Count);
        return true;
    }

    public enum ProfileGroupsTab
    {
        Profile,
        Mutual,
        Mine
    }

    private IReadOnlyList<VrcProfileGroup> _myGroups = Array.Empty<VrcProfileGroup>();
    private ProfileGroupsTab _groupsTab = ProfileGroupsTab.Profile;

    /// <summary>Every group the signed-in account is in, including all 100+.</summary>
    public IReadOnlyList<VrcProfileGroup> MyGroups => _myGroups;

    public ProfileGroupsTab GroupsTab
    {
        get => _groupsTab;
        set
        {
            if (SetProperty(ref _groupsTab, value))
            {
                OnPropertyChanged(nameof(VisibleGroups));
                OnPropertyChanged(nameof(IsProfileGroupsTab));
                OnPropertyChanged(nameof(IsMutualGroupsTab));
                OnPropertyChanged(nameof(IsMyGroupsTab));
                OnPropertyChanged(nameof(ProfileGroupsTabText));
            }
        }
    }

    public IReadOnlyList<VrcProfileGroup> VisibleGroups => GroupsTab switch
    {
        ProfileGroupsTab.Mutual => Groups.Where(group => group.IsMutual).ToList(),
        ProfileGroupsTab.Mine => _myGroups,
        _ => Groups
    };

    public bool IsProfileGroupsTab => GroupsTab == ProfileGroupsTab.Profile;
    public bool IsMutualGroupsTab => GroupsTab == ProfileGroupsTab.Mutual;
    public bool IsMyGroupsTab => GroupsTab == ProfileGroupsTab.Mine;

    public string MyGroupsTabText => $"My groups ({_myGroups.Count})";

    public string TheirGroupsTabText => $"Their groups ({Groups.Count})";

    public string MutualGroupsTabText => $"Mutual ({MutualGroupCount})";

    public string ProfileGroupsTabText => GroupsTab switch
    {
        ProfileGroupsTab.Mutual => $"Mutual ({MutualGroupCount})",
        ProfileGroupsTab.Mine => MyGroupsTabText,
        _ => $"Their groups ({Groups.Count})"
    };

    public int MutualGroupCount => Groups.Count(group => group.IsMutual);

    public bool HasMutualGroups => MutualGroupCount > 0;

    public string MutualGroupsText => MutualGroupCount == 0
        ? "No groups in common"
        : $"{MutualGroupCount} group{(MutualGroupCount == 1 ? string.Empty : "s")} in common";

    /// <summary>
    /// Keeps the open profile in step with a join or leave made in the group
    /// popup: the My groups list and any matching profile chips change at once.
    /// </summary>
    internal void ApplyGroupMembership(Group group, bool isMember)
    {
        var id = group.EffectiveId;
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var mine = _myGroups.Where(existing => !string.Equals(existing.Id, id, StringComparison.Ordinal)).ToList();
        if (isMember)
        {
            mine.Add(new VrcProfileGroup
            {
                Id = id,
                Name = group.Name,
                IconUrl = group.IconUrl,
                IsMine = true,
                IsMutual = !IsSelf && Groups.Any(profileGroup => string.Equals(profileGroup.Id, id, StringComparison.Ordinal))
            });
            mine.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
        }

        _myGroups = mine;

        foreach (var profileGroup in Groups.Where(profileGroup => string.Equals(profileGroup.Id, id, StringComparison.Ordinal)))
        {
            profileGroup.IsMine = isMember;
            profileGroup.IsMutual = isMember && !IsSelf;
        }

        // The Mutual tab disappears when its last group is left; don't strand the
        // popup on an empty list, show the profile's groups instead.
        if (GroupsTab == ProfileGroupsTab.Mutual && MutualGroupCount == 0)
        {
            GroupsTab = ProfileGroupsTab.Profile;
        }

        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(RepresentedGroup));
        OnPropertyChanged(nameof(HasRepresentedGroup));
        OnPropertyChanged(nameof(HasNoRepresentedGroup));
        OnPropertyChanged(nameof(MyGroups));
        OnPropertyChanged(nameof(VisibleGroups));
        OnPropertyChanged(nameof(MyGroupsTabText));
        OnPropertyChanged(nameof(TheirGroupsTabText));
        OnPropertyChanged(nameof(MutualGroupsTabText));
        OnPropertyChanged(nameof(ProfileGroupsTabText));
        OnPropertyChanged(nameof(GroupsSummary));
        OnPropertyChanged(nameof(MutualGroupCount));
        OnPropertyChanged(nameof(HasMutualGroups));
        OnPropertyChanged(nameof(MutualGroupsText));
    }

    /// <summary>Opens a group chip in the group info popup (join/leave/details).</summary>
    public RelayCommand<VrcProfileGroup> OpenGroupCommand { get; }

    public RelayCommand OpenGroupsCommand { get; }

    public RelayCommand CloseGroupsCommand { get; }

    public RelayCommand ShowProfileGroupsCommand { get; }

    public RelayCommand ShowMutualGroupsCommand { get; }

    public RelayCommand ShowMyGroupsCommand { get; }

    private bool _isGroupsOpen;

    /// <summary>Scrollable list of every group on this profile.</summary>
    public bool IsGroupsOpen
    {
        get => _isGroupsOpen;
        set
        {
            if (SetProperty(ref _isGroupsOpen, value))
            {
                OnPropertyChanged(nameof(Groups));
                OnPropertyChanged(nameof(VisibleGroups));
                OnPropertyChanged(nameof(ProfileGroupsTabText));
            }
        }
    }

    public string GroupsSummary
    {
        get
        {
            var groups = Groups;
            if (groups.Count == 0)
            {
                return string.Empty;
            }

            var summary = $"{groups.Count} group{(groups.Count == 1 ? string.Empty : "s")}";
            return IsSelf || MutualGroupCount == 0
                ? summary
                : $"{summary} · {MutualGroupCount} mutual";
        }
    }

    public IReadOnlyList<VrcBadge> Badges
        => Profile?.Badges?
               .Where(badge => !string.IsNullOrWhiteSpace(badge.BadgeName))
               .OrderByDescending(badge => badge.Showcased)
               .Take(6)
               .ToList()
           ?? (IReadOnlyList<VrcBadge>)Array.Empty<VrcBadge>();

    public bool HasBadges => Badges.Count > 0;

    public Brush BannerBrush
    {
        get
        {
            var value = Profile?.BannerColor;
            if (string.IsNullOrWhiteSpace(value))
            {
                return DefaultBannerBrush;
            }

            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
                brush.Freeze();
                return brush;
            }
            catch
            {
                return DefaultBannerBrush;
            }
        }
    }

    public string WorldName => _worldName ?? string.Empty;

    public bool HasWorldName => !string.IsNullOrWhiteSpace(_worldName);

    public string WorldLine
    {
        get
        {
            if (IsSelf)
            {
                return "You are signed in through VRChat Companion; status and platform come from your account.";
            }

            if (!string.IsNullOrWhiteSpace(_worldName))
            {
                return $"{_worldName} · {_friend.LocationAccessLabel}";
            }

            return _friend.LocationText;
        }
    }

    public string? WorldId => LaunchService.GetWorldIdFromLocation(_friend.Location);

    public string? WorldUrl => WorldId is null ? null : $"https://vrchat.com/home/world/{WorldId}";

    // A real world/instance location already means they are in game, which also
    // covers senders who are not on the friends list (no status payload at all).
    public bool CanJoin => LaunchService.IsLocation(_friend.Location);

    public string JoinLocation => LaunchService.IsLocation(_friend.Location) ? _friend.Location : string.Empty;

    public bool CanRequestInvite => _friend.IsOnline && _friend.IsInPrivateOrInviteWorld;

    public string MutualText
    {
        get
        {
            if (_mutuals is null)
            {
                return string.Empty;
            }

            if (_mutuals.Friends == 0 && _mutuals.Groups == 0)
            {
                return "No mutual friends or groups.";
            }

            var parts = new List<string>();
            if (_mutuals.Friends > 0)
            {
                parts.Add($"{_mutuals.Friends} mutual friend{(_mutuals.Friends == 1 ? string.Empty : "s")}");
            }
            if (_mutuals.Groups > 0)
            {
                parts.Add($"{_mutuals.Groups} mutual group{(_mutuals.Groups == 1 ? string.Empty : "s")}");
            }
            return string.Join(" · ", parts);
        }
    }

    public bool HasMutuals => !string.IsNullOrWhiteSpace(MutualText);

    public string ProfileUrl => $"https://vrchat.com/home/user/{Id}";

    public async Task LoadAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var profile = await _api.GetProfileAsync(
                Id,
                forceRefresh,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Profile = profile;
            if (profile is null)
            {
                ErrorMessage = "VRChat did not return this profile. Showing the friend-list details instead.";
            }
            else if (IncludeRepresentedGroup(profile))
            {
                OnPropertyChanged(nameof(Groups));
                OnPropertyChanged(nameof(TheirGroupsTabText));
                OnPropertyChanged(nameof(ProfileGroupsTabText));
                OnPropertyChanged(nameof(GroupsSummary));
            }

            _ = LoadImagesAsync(profile, cancellationToken);
            _ = AnnotateGroupsAsync(forceRefresh, cancellationToken);
            _ = LoadExtrasAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Selection changed.
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat to load this profile.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not load this friend's full profile.";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    public void UpdateFriend(FriendItem friend)
    {
        _friend = friend;
        OnAllPropertiesChanged();
    }

    private async Task LoadImagesAsync(VrcProfile? profile, CancellationToken cancellationToken)
    {
        // Build the best avatar URL: full profile first, then friend-list data.
        string? avatarUrl = null;

        if (profile is not null)
        {
            avatarUrl = ImageUrlHelper.PickAvatarThumbnailUrl(
                profile.UserIcon,
                null,
                profile.CurrentAvatarThumbnailImageUrl,
                profile.CurrentAvatarImageUrl,
                256);
        }

        if (string.IsNullOrWhiteSpace(avatarUrl))
        {
            avatarUrl = _friend.AvatarUrl;
        }

        var bannerUrl = ImageUrlHelper.ToThumbnailUrl(
            profile?.BannerUrl ?? _friend.BannerUrl,
            900);

        try
        {
            var avatarTask = string.IsNullOrWhiteSpace(avatarUrl)
                ? Task.FromResult<ImageSource?>(null)
                : _images.LoadAsync(avatarUrl, 256, cancellationToken);
            var bannerTask = string.IsNullOrWhiteSpace(bannerUrl)
                ? Task.FromResult<ImageSource?>(null)
                : _images.LoadAsync(bannerUrl, 900, cancellationToken);
            await Task.WhenAll(avatarTask, bannerTask);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            AvatarImage = avatarTask.Result;
            BannerImage = bannerTask.Result;
        }
        catch (OperationCanceledException)
        {
            // Selection changed.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    /// <summary>
    /// Marks each profile group as one of the account's own ("Joined") and, for
    /// another user, as a group in common ("Mutual"). Membership is not inferred
    /// from the profile payload: it is cross-checked against the account's groups.
    /// </summary>
    private async Task AnnotateGroupsAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        try
        {
            // Load every group on the account, not just the ones shown on this
            // profile: the popup offers a "My groups" tab with the full list.
            var mine = await _api.GetMyGroupsAsync(cancellationToken, forceRefresh);

            // A profile payload ships only a few of the user's groups (the count
            // says how many exist), so read the full membership list instead.
            IReadOnlyList<Group>? allProfileGroups = IsSelf ? mine : null;
            if (!IsSelf && NeedsFullGroupList(Profile))
            {
                try
                {
                    allProfileGroups = await _api.GetUserGroupsAsync(Id, cancellationToken, forceRefresh);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }

            if (allProfileGroups is { Count: > 0 } && Profile?.Groups is { } payload)
            {
                payload.List = allProfileGroups
                    .Select(group => new VrcProfileGroup
                    {
                        Id = group.EffectiveId,
                        Name = group.Name,
                        IconUrl = group.IconUrl
                    })
                    .Where(group => !string.IsNullOrWhiteSpace(group.Name))
                    .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                payload.Count = payload.List.Count;
            }

            IncludeRepresentedGroup(Profile);

            var groups = Groups;
            if (Profile?.RepresentedGroup is { } represented)
            {
                represented.IsMine = mine.Any(group =>
                    string.Equals(group.EffectiveId, represented.Id, StringComparison.Ordinal));
                represented.IsMutual = !IsSelf && represented.IsMine;
            }
            var ids = mine.Select(group => group.EffectiveId).ToHashSet(StringComparer.Ordinal);
            var profileIds = groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);

            if (IsSelf)
            {
                foreach (var group in groups)
                {
                    group.IsMine = true;
                    group.IsMutual = false;
                }
            }
            else
            {
                foreach (var group in groups)
                {
                    group.IsMine = ids.Contains(group.Id);
                    group.IsMutual = group.IsMine;
                }
            }

            _myGroups = mine
                .Select(group => new VrcProfileGroup
                {
                    Id = group.EffectiveId,
                    Name = group.Name,
                    IconUrl = group.IconUrl,
                    IsMine = true,
                    IsMutual = !IsSelf && profileIds.Contains(group.EffectiveId)
                })
                .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(RepresentedGroup));
        OnPropertyChanged(nameof(HasRepresentedGroup));
        OnPropertyChanged(nameof(HasNoRepresentedGroup));
        OnPropertyChanged(nameof(MyGroups));
        OnPropertyChanged(nameof(VisibleGroups));
        OnPropertyChanged(nameof(MyGroupsTabText));
        OnPropertyChanged(nameof(TheirGroupsTabText));
        OnPropertyChanged(nameof(MutualGroupsTabText));
        OnPropertyChanged(nameof(ProfileGroupsTabText));
        OnPropertyChanged(nameof(GroupsSummary));
        OnPropertyChanged(nameof(MutualGroupCount));
        OnPropertyChanged(nameof(HasMutualGroups));
        OnPropertyChanged(nameof(MutualGroupsText));
    }

    private async Task LoadExtrasAsync(CancellationToken cancellationToken)
    {
        var worldId = WorldId;
        try
        {
            var mutualsTask = _api.GetMutualsAsync(Id, cancellationToken: cancellationToken);
            VrcWorld? world = null;
            if (worldId is not null)
            {
                world = await _api.GetWorldAsync(worldId, cancellationToken: cancellationToken);
            }
            var mutuals = await mutualsTask;

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _worldName = world?.Name;
            _mutuals = mutuals;
            OnPropertyChanged(nameof(WorldName));
            OnPropertyChanged(nameof(HasWorldName));
            OnPropertyChanged(nameof(WorldLine));
            OnPropertyChanged(nameof(MutualText));
            OnPropertyChanged(nameof(HasMutuals));
        }
        catch (OperationCanceledException)
        {
            // Selection changed.
        }
        catch (VrcApiException ex)
        {
            Debug.WriteLine(ex.Message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static Brush MakeBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
