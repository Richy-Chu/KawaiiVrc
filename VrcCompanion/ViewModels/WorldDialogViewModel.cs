using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
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
public sealed class WorldInstancePlayerItem
{
    public WorldInstancePlayerItem(VrcUser user, Action<WorldInstancePlayerItem>? openProfile)
    {
        UserId = user.Id ?? string.Empty;
        DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? "Unknown player" : user.DisplayName.Trim();
        AvatarUrl = ImageUrlHelper.PickAvatarThumbnailUrl(
            user.UserIcon,
            user.ProfilePicOverrideThumbnail ?? user.IconUrl,
            user.CurrentAvatarThumbnailImageUrl,
            user.CurrentAvatarImageUrl,
            64) ?? string.Empty;
        IsFriend = user.IsFriend;
        OpenProfileCommand = new RelayCommand(
            () => openProfile?.Invoke(this),
            () => CanOpenProfile);
    }

    public string UserId { get; }
    public string DisplayName { get; }
    public string AvatarUrl { get; }
    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);
    public bool IsFriend { get; }
    public bool CanOpenProfile => !string.IsNullOrWhiteSpace(UserId);
    public RelayCommand OpenProfileCommand { get; }
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

    public bool HasPlayers => Players.Count > 0;

    public bool ShowNoPlayers => !IsLoadingPlayers && !HasPlayersError && !HasPlayers;

    /// <summary>True once a real player list came back, so reopening does not refetch it.</summary>
    public bool PlayersLoaded { get; private set; }

    public void SetPlayers(IReadOnlyList<VrcUser> users, Action<WorldInstancePlayerItem>? openProfile)
    {
        Players.Clear();
        foreach (var user in users)
        {
            Players.Add(new WorldInstancePlayerItem(user, openProfile));
        }

        PlayersLoaded = true;
        PlayersError = null;
        OnPropertyChanged(nameof(HasPlayers));
        RaisePlayerState();
    }

    public void SetPlayersError(string message)
    {
        Players.Clear();
        PlayersError = message;
        OnPropertyChanged(nameof(HasPlayers));
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
    private WorldInstanceItem? _selectedInstance;
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
        Action<string, string, string>? openUserProfile = null)
    {
        _summary = summary;
        _api = api;
        _images = images;
        _focusLocation = focusLocation;
        _focusNote = focusNote;
        _openUserProfile = openUserProfile;
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
        CloseInstancePlayersCommand = new RelayCommand(() => SelectedInstance = null);

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
            }
        }
    }

    public bool HasSelectedInstance => _selectedInstance is not null;

    public RelayCommand<WorldInstanceItem> ShowInstancePlayersCommand { get; }

    public RelayCommand CloseInstancePlayersCommand { get; }

    /// <summary>
    /// Reads the instance itself (region, capacity and, when VRChat shares it, the
    /// user list). A pinned invite is not in the world's visible instances, so the
    /// instance endpoint is what makes its people available.
    /// </summary>
    public async Task ShowInstancePlayersAsync(WorldInstanceItem item)
    {
        SelectedInstance = item;
        if (item.PlayersLoaded)
        {
            return;
        }

        item.BeginPlayerLoad();
        try
        {
            var instance = await _api.GetInstanceAsync(item.Location);
            if (instance is null)
            {
                item.SetPlayersError("VRChat could not load this instance. It may have closed.");
                return;
            }

            item.SetPlayers(
                instance.Users,
                player => _openUserProfile?.Invoke(player.UserId, player.DisplayName, player.AvatarUrl));
        }
        catch (VrcApiException ex)
        {
            item.SetPlayersError(ex.Message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            item.SetPlayersError("Could not reach VRChat to load this instance's players.");
        }
        finally
        {
            item.EndPlayerLoad();
        }
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
        Instances.Clear();
        FocusInstance = null;
        SelectedInstance = null;
        if (World is null)
        {
            OnPropertyChanged(nameof(HasActiveInstances));
            return;
        }

        var items = World.SlimInstances
            .Where(item => !string.IsNullOrWhiteSpace(item.InstanceId))
            .OrderByDescending(item => item.Occupants)
            .Take(12)
            .Select(instance => new WorldInstanceItem(instance, World.Id, viewPlayers: ShowInstancePlayers))
            .ToList();

        var added = new List<WorldInstanceItem>();

        // The instance the dialog was opened for goes first and stays even when VRChat
        // does not list it (a private invite is not in the world's visible instances).
        var focus = BuildFocusInstance(items);
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

        OnPropertyChanged(nameof(HasActiveInstances));
        GroupHydration = HydrateGroupsAsync(added, cancellationToken);
    }

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
