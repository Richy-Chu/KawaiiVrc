using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Input;
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
    private readonly FriendAvatarStore _avatarStore;
    private readonly string _userId;
    private FriendItem _friend;
    private string _noteText = string.Empty;
    private string _savedNoteText = string.Empty;
    private string _noteStatusText = string.Empty;
    private VrcProfile? _profile;
    private VrcMutuals? _mutuals;
    private ImageSource? _avatarImage;
    private ImageSource? _bannerImage;
    private string? _worldName;
    private string? _worldImageUrl;
    private string? _error;
    private bool _isLoading;
    private VrcFriendStatus? _friendStatus;
    private bool _isFriendMenuOpen;
    private string _presenceStatus;
    private string? _pinnedLocation;
    private string? _pinnedStatus;
    private Func<bool>? _pinnedLocationIsCurrent;
    private bool _isLoadingAvatars;
    private bool _isResolvingAvatars;
    private bool _avatarResolveAgain;
    private bool _isResolvingProfileIconAvatar;
    private string _profileIconAvatarLookupKey = string.Empty;
    private string _loggedAvatarKey = string.Empty;
    private bool _isLoadingIndexedAvatars;
    private bool _indexedAvatarsLoaded;
    private bool _indexedAvatarLookupAgain;
    private string _indexedAvatarLookupKey = string.Empty;
    private bool _isBooping;
    private bool _isBoopPickerOpen;
    private bool _boopChoicesLoaded;
    private string _boopStatusText = string.Empty;

    public FriendDetailViewModel(
        FriendItem friend,
        VrcApiClient api,
        ImageLoader images,
        FriendNotesStore? notes = null,
        bool isSelf = false,
        Action<VrcProfileGroup>? openGroup = null,
        FriendAvatarStore? avatarStore = null,
        Action<Avatar>? openAvatar = null)
    {
        _friend = friend;
        _userId = friend.Id;
        _presenceStatus = UserPresence.Normalize(friend.User.Status);
        _worldName = friend.HasWorldName ? friend.WorldName : null;
        _api = api;
        _images = images;
        _notes = notes ?? FriendNotesStore.Shared;
        _avatarStore = avatarStore ?? FriendAvatarStore.Shared;
        OpenAvatar = openAvatar;
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
        ToggleFriendMenuCommand = new RelayCommand(() => IsFriendMenuOpen = !IsFriendMenuOpen);
        ShowProfileGroupsCommand = new RelayCommand(() => GroupsTab = ProfileGroupsTab.Profile);
        ShowMutualGroupsCommand = new RelayCommand(() => GroupsTab = ProfileGroupsTab.Mutual);
        ShowMyGroupsCommand = new RelayCommand(() => GroupsTab = ProfileGroupsTab.Mine);
        ToggleBoopPickerCommand = new RelayCommand(
            ToggleBoopPicker,
            () => CanBoop);
        SendBoopCommand = new AsyncRelayCommand<BoopEmojiOption>(
            SendBoopAsync,
            _ => CanBoop);
        SendPlainBoopCommand = new AsyncRelayCommand(
            () => SendBoopAsync(null),
            () => CanBoop);
        CloseBoopPickerCommand = new RelayCommand(() => IsBoopPickerOpen = false);

        foreach (var emoji in BoopEmojiOption.DefaultCatalog)
        {
            BoopEmojis.Add(emoji);
        }

        var stored = _notes.Get(_friend.Id);
        _noteText = stored?.Text ?? string.Empty;
        _savedNoteText = _noteText;
        _noteStatusText = stored is null
            ? "Only you can see this; it stays on this PC."
            : $"Saved locally · {stored.UpdatedAt.ToLocalTime():g}";
        SaveNoteCommand = new RelayCommand(SaveNote);
        OpenAvatarCommand = new RelayCommand<FriendAvatar>(
            item => _ = OpenAvatarAsync(item),
            item => item is { CanOpen: true });
        ToggleAvatarsCommand = new RelayCommand(ToggleAvatars);
        Avatars.CollectionChanged += (_, _) =>
        {
            RebuildVisibleAvatars();
            OnPropertyChanged(nameof(CanToggleAvatars));
            OnPropertyChanged(nameof(AvatarsToggleText));
            OnPropertyChanged(nameof(AvatarsSummary));
        };

        LoadStoredAvatars();
        RememberCurrentAvatar(_friend.User, _friend.User.CurrentAvatar, _friend.User.CurrentAvatarName);
    }

    /// <summary>
    /// Current and previously seen avatars for this profile. VRChat only exposes
    /// the current one; the older rows are what this app has observed locally.
    /// </summary>
    public ObservableCollection<FriendAvatar> Avatars { get; } = new();

    /// <summary>
    /// What the profile grid actually renders. It starts with only the avatar
    /// the person is wearing so a long local history does not bury the profile.
    /// </summary>
    public ObservableCollection<FriendAvatar> VisibleAvatars { get; } = new();

    private bool _isAvatarsExpanded;

    public bool IsAvatarsExpanded
    {
        get => _isAvatarsExpanded;
        private set
        {
            if (SetProperty(ref _isAvatarsExpanded, value))
            {
                OnPropertyChanged(nameof(AvatarsToggleText));
                OnPropertyChanged(nameof(AvatarsSummary));
                RebuildVisibleAvatars();
            }
        }
    }

    public bool CanToggleAvatars => Avatars.Count > 1;

    public string AvatarsToggleText => !CanToggleAvatars
        ? string.Empty
        : IsAvatarsExpanded
            ? "Show current only"
            : $"Show all {Avatars.Count}";

    public RelayCommand ToggleAvatarsCommand { get; }

    public bool HasAvatars => Avatars.Count > 0;

    /// <summary>At least one public avatar came from an author lookup.</summary>
    public bool HasIndexedAvatars => Avatars.Any(avatar => avatar.IsIndexed);

    public bool ShowNoAvatarsMessage => !HasAvatars && !IsLoadingAvatars;

    public string AvatarsSummary
    {
        get
        {
            if (Avatars.Count == 0)
            {
                return string.Empty;
            }

            if (Avatars.Count == 1)
            {
                return "1 avatar";
            }

            return IsAvatarsExpanded
                ? $"{Avatars.Count} avatars"
                : $"1 of {Avatars.Count} avatars";
        }
    }

    private void ToggleAvatars() => IsAvatarsExpanded = !IsAvatarsExpanded;

    private void RebuildVisibleAvatars()
    {
        List<FriendAvatar> desired;
        if (Avatars.Count == 0)
        {
            desired = new List<FriendAvatar>();
        }
        else if (IsAvatarsExpanded)
        {
            desired = Avatars.ToList();
        }
        else
        {
            desired = new List<FriendAvatar>
            {
                Avatars.FirstOrDefault(avatar => avatar.IsCurrent) ?? Avatars[0]
            };
        }

        if (VisibleAvatars.SequenceEqual(desired))
        {
            return;
        }

        VisibleAvatars.Clear();
        foreach (var avatar in desired)
        {
            VisibleAvatars.Add(avatar);
        }
    }

    public bool IsLoadingAvatars
    {
        get => _isLoadingAvatars;
        private set
        {
            if (SetProperty(ref _isLoadingAvatars, value))
            {
                OnPropertyChanged(nameof(ShowNoAvatarsMessage));
            }
        }
    }

    public RelayCommand<FriendAvatar> OpenAvatarCommand { get; }

    /// <summary>The built-in VRChat emoji choices shown by the profile boop picker.</summary>
    public ObservableCollection<BoopEmojiOption> BoopEmojis { get; } = new();

    public RelayCommand ToggleBoopPickerCommand { get; }

    public AsyncRelayCommand<BoopEmojiOption> SendBoopCommand { get; }

    public AsyncRelayCommand SendPlainBoopCommand { get; }

    public RelayCommand CloseBoopPickerCommand { get; }

    /// <summary>Friend profiles show the action; self and strangers do not.</summary>
    public bool ShowBoopAction => !IsSelf && IsFriend;

    /// <summary>
    /// Boops are friend-only. VRCX-0 also disables them when the signed-in
    /// account has turned booping off, and the self profile never has this action.
    /// </summary>
    public bool CanBoop => ShowBoopAction && !IsBooping &&
                           _api.CurrentUser?.IsBoopingEnabled != false;

    public bool IsBooping
    {
        get => _isBooping;
        private set
        {
            if (SetProperty(ref _isBooping, value))
            {
                OnPropertyChanged(nameof(CanBoop));
                ToggleBoopPickerCommand.RaiseCanExecuteChanged();
                SendBoopCommand.RaiseCanExecuteChanged();
                SendPlainBoopCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBoopPickerOpen
    {
        get => _isBoopPickerOpen;
        set => SetProperty(ref _isBoopPickerOpen, value);
    }

    /// <summary>
    /// Re-checks the avatar-search cache while this profile is already open.
    /// No requests are made unless a cached public avatar actually matches.
    /// </summary>
    public void RefreshAvatarLookupFromSearch()
    {
        if (HasKnownCurrentAvatar)
        {
            return;
        }

        if (_isResolvingAvatars)
        {
            _avatarResolveAgain = true;
            return;
        }

        _ = AddRememberedCurrentAvatarAsync(CancellationToken.None);
    }

    /// <summary>Closes popups that live outside the profile's clipped content.</summary>
    public void CloseTransientPopups()
    {
        IsBoopPickerOpen = false;
        IsFriendMenuOpen = false;
    }

    public string BoopStatusText
    {
        get => _boopStatusText;
        private set
        {
            if (SetProperty(ref _boopStatusText, value))
            {
                OnPropertyChanged(nameof(HasBoopStatus));
            }
        }
    }

    public bool HasBoopStatus => !string.IsNullOrWhiteSpace(BoopStatusText);

    private void ToggleBoopPicker()
    {
        if (!CanBoop)
        {
            return;
        }

        BoopStatusText = string.Empty;
        var opening = !IsBoopPickerOpen;
        IsBoopPickerOpen = opening;
        if (opening && !_boopChoicesLoaded)
        {
            _ = LoadBoopChoicesAsync(CancellationToken.None);
        }
    }

    private async Task SendBoopAsync(BoopEmojiOption? emoji)
    {
        if (!CanBoop)
        {
            return;
        }

        IsBooping = true;
        BoopStatusText = string.Empty;
        try
        {
            var inventoryItemId = emoji?.InventoryItemId;
            await _api.SendBoopAsync(
                Id,
                inventoryItemId is null ? emoji?.Id : null,
                inventoryItemId);
            IsBoopPickerOpen = false;
            BoopStatusText = emoji is null
                ? $"Booped {DisplayName}."
                : $"Booped {DisplayName} with {emoji.Name}.";
        }
        catch (VrcApiException ex)
        {
            BoopStatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            BoopStatusText = "Could not reach VRChat. Check your connection and try again.";
        }
        catch (TaskCanceledException)
        {
            BoopStatusText = "The boop timed out. Try again.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            BoopStatusText = "Could not send that boop.";
        }
        finally
        {
            IsBooping = false;
        }
    }

    /// <summary>Opens the shared avatar popup; supplied by the shell.</summary>
    public Action<Avatar>? OpenAvatar { get; set; }

    /// <summary>
    /// Seeds the profile avatar list from the last saved observation. This is
    /// synchronous so the section is already populated on the first frame.
    /// </summary>
    private void LoadStoredAvatars()
    {
        foreach (var stored in _avatarStore.Load(_api.CurrentUser?.Id, _userId))
        {
            // Your own private avatars are known to belong to you; a failed
            // network lookup must not turn them into "unavailable" cards.
            if (IsSelf && stored.IsPrivate)
            {
                stored.IsPrivacyUnverified = false;
            }

            Avatars.Add(stored);
        }

        RaiseAvatarsChanged();
    }

    /// <summary>
    /// Adds or refreshes the current avatar from a VRChat user payload. The
    /// picture is present on every user record, so this needs no extra request.
    /// </summary>
    private void RememberCurrentAvatar(
        VrcUser user,
        string? avatarId = null,
        string? name = null,
        string? imageUrl = null,
        string? thumbnailUrl = null,
        string? profileArtwork = null)
    {
        var id = string.IsNullOrWhiteSpace(avatarId) ? user.CurrentAvatar : avatarId;
        imageUrl ??= user.CurrentAvatarImageUrl;
        thumbnailUrl ??= user.CurrentAvatarThumbnailImageUrl;

        profileArtwork ??= user.ProfilePicOverrideThumbnail ??
                           user.ProfilePicOverride ??
                           user.UserIcon ??
                           user.IconUrl;
        var profileArtworkCandidates = new[]
        {
            profileArtwork,
            user.ProfilePicOverrideThumbnail,
            user.ProfilePicOverride,
            user.UserIcon,
            user.IconUrl
        };
        var avatarImageIsProfileArtwork = profileArtworkCandidates.Any(candidate =>
            SameImage(imageUrl, candidate) || SameImage(thumbnailUrl, candidate));
        var pictureOnlyCandidate = string.IsNullOrWhiteSpace(id) &&
            string.IsNullOrWhiteSpace(name) &&
            (!string.IsNullOrWhiteSpace(imageUrl) || !string.IsNullOrWhiteSpace(thumbnailUrl)) &&
            !avatarImageIsProfileArtwork;

        var imageLookupUrl = string.IsNullOrWhiteSpace(imageUrl) ? thumbnailUrl : imageUrl;
        var knownImage = _avatarStore.GetImageResolution(_api.CurrentUser?.Id, imageLookupUrl);
        if (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(knownImage?.AvatarId))
        {
            id = knownImage.AvatarId;
        }

        // VRCX-0 keeps the last known current-avatar id when a refreshed
        // profile omits currentAvatar. Do the same only when the picture is
        // unchanged (or absent), so a real avatar change is never hidden under
        // the previous id.
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
        {
            var storedCurrent = Avatars.FirstOrDefault(candidate =>
                candidate.IsCurrent && !string.IsNullOrWhiteSpace(candidate.AvatarId));
            var pictureMissing = string.IsNullOrWhiteSpace(imageUrl) &&
                                 string.IsNullOrWhiteSpace(thumbnailUrl);
            var pictureMatches = storedCurrent is not null &&
                (SameAvatarImage(imageUrl, storedCurrent.ImageUrl) ||
                 SameAvatarImage(thumbnailUrl, storedCurrent.ThumbnailUrl));
            if (storedCurrent is not null && (pictureMissing || pictureMatches))
            {
                id = storedCurrent.AvatarId;
                name = string.IsNullOrWhiteSpace(storedCurrent.Name) ? null : storedCurrent.Name;
                imageUrl = string.IsNullOrWhiteSpace(imageUrl) ? storedCurrent.ImageUrl : imageUrl;
                thumbnailUrl = string.IsNullOrWhiteSpace(thumbnailUrl)
                    ? storedCurrent.ThumbnailUrl
                    : thumbnailUrl;
            }
        }

        // VRC+ profile pictures and the merged iconUrl are profile artwork,
        // not avatar records. /users no longer carries currentAvatar or its
        // image fields for everyone, so an image-only record with a profile
        // icon must not become a fake "private" avatar. A current-avatar image
        // that differs from the profile artwork is still a real lookup clue,
        // though, and must be sent to the indexes before we fall back to
        // authored avatars.
        if (string.IsNullOrWhiteSpace(id) &&
            string.IsNullOrWhiteSpace(name) &&
            !string.IsNullOrWhiteSpace(profileArtwork) &&
            string.IsNullOrWhiteSpace(knownImage?.AvatarId) &&
            !pictureOnlyCandidate)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(id) &&
            string.IsNullOrWhiteSpace(imageUrl) &&
            string.IsNullOrWhiteSpace(thumbnailUrl) &&
            string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var observed = string.IsNullOrWhiteSpace(id)
            ? null
            : _avatarStore.Observe(
                _api.CurrentUser?.Id,
                _userId,
                id,
                name,
                imageUrl,
                thumbnailUrl);

        var item = string.IsNullOrWhiteSpace(id)
            ? Avatars.FirstOrDefault(candidate => candidate.IsCurrent && string.IsNullOrWhiteSpace(candidate.AvatarId))
            : Avatars.FirstOrDefault(candidate =>
                  string.Equals(candidate.AvatarId, id, StringComparison.Ordinal)) ??
              Avatars.FirstOrDefault(candidate =>
                  candidate.IsCurrent && string.IsNullOrWhiteSpace(candidate.AvatarId));

        if (observed is not null)
        {
            // The store has the stable row. Drop a picture-only placeholder so
            // the same avatar is not listed twice when the id arrives.
            if (item is not null && !ReferenceEquals(item, observed))
            {
                Avatars.Remove(item);
            }

            item = observed;
            if (!Avatars.Contains(item))
            {
                Avatars.Insert(0, item);
            }
        }
        else if (item is null)
        {
            item = new FriendAvatar
            {
                AvatarId = id ?? string.Empty,
                LastSeen = DateTimeOffset.UtcNow
            };
            Avatars.Insert(0, item);
        }
        else if (!string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(item.AvatarId))
        {
            item.AvatarId = id;
        }

        // A locked placeholder is replaced by a picture that arrives later.
        // Clear its fallback privacy so the new picture can be checked normally.
        if (item.IsPrivate &&
            string.IsNullOrWhiteSpace(item.AvatarId) &&
            string.IsNullOrWhiteSpace(knownImage?.ReleaseStatus) &&
            (!string.IsNullOrWhiteSpace(imageUrl) || !string.IsNullOrWhiteSpace(thumbnailUrl)))
        {
            item.ReleaseStatus = string.Empty;
        }

        var knownStatus = string.IsNullOrWhiteSpace(observed?.ReleaseStatus)
            ? knownImage?.ReleaseStatus
            : observed.ReleaseStatus;
        item.Merge(
            name,
            imageUrl,
            thumbnailUrl,
            knownStatus,
            DateTimeOffset.UtcNow,
            isCurrent: true);
        item.IsProfileImageCandidate = pictureOnlyCandidate &&
                                         string.IsNullOrWhiteSpace(item.AvatarId);

        if (IsSelf && item.IsPrivate)
        {
            item.IsPrivacyUnverified = false;
        }

        if (!item.IsProfilePictureFallback &&
            (!string.IsNullOrWhiteSpace(item.AvatarId) ||
             item.HasThumbnail ||
             !string.IsNullOrWhiteSpace(item.Name)))
        {
            RemoveProfilePictureFallback();
        }

        foreach (var other in Avatars.Where(candidate => !ReferenceEquals(candidate, item)))
        {
            other.IsCurrent = false;
        }

        RaiseAvatarsChanged();
    }

    private static bool SameImage(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return SameAvatarImage(left, right) ||
               string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Author indexes know what a person uploaded, not what they are wearing.
    /// Only accept a row that is the same picture or exact current-avatar name.
    /// </summary>
    private bool MatchesCurrentAvatarHints(Avatar avatar)
    {
        var imageUrls = new[]
        {
            _profile?.CurrentAvatarImageUrl,
            _profile?.CurrentAvatarThumbnailImageUrl,
            _profile?.IconUrl,
            _profile?.UserIcon,
            _friend.User.CurrentAvatarImageUrl,
            _friend.User.CurrentAvatarThumbnailImageUrl,
            _friend.User.IconUrl,
            _friend.User.UserIcon,
            _friend.AvatarUrl
        };
        if (imageUrls.Any(url =>
                SameAvatarImage(avatar.ImageUrl, url) ||
                SameAvatarImage(avatar.ThumbnailImageUrl, url)))
        {
            return true;
        }

        var name = _profile?.CurrentAvatarName ?? _friend.User.CurrentAvatarName;
        return !string.IsNullOrWhiteSpace(name) &&
               string.Equals(avatar.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void AddProfilePictureFallback()
    {
        var artwork = _profile?.IconUrl ??
                      _profile?.UserIcon ??
                      _friend.User.IconUrl ??
                      _friend.User.UserIcon ??
                      _friend.AvatarUrl;
        if (string.IsNullOrWhiteSpace(artwork))
        {
            return;
        }

        var item = new FriendAvatar
        {
            Name = "Profile picture",
            ImageUrl = artwork,
            ThumbnailUrl = ImageUrlHelper.ToThumbnailUrl(artwork, 256) ?? artwork,
            IsCurrent = true,
            IsProfilePictureFallback = true,
            LastSeen = DateTimeOffset.UtcNow
        };
        Avatars.Insert(0, item);
        RaiseAvatarsChanged();
    }

    private static bool SameAvatarImage(string? left, string? right)
    {
        var leftFile = ImageUrlHelper.ExtractFileId(left);
        var rightFile = ImageUrlHelper.ExtractFileId(right);
        return !string.IsNullOrWhiteSpace(leftFile) &&
               !string.IsNullOrWhiteSpace(rightFile) &&
               string.Equals(leftFile, rightFile, StringComparison.Ordinal);
    }

    private void RememberProfileAvatar(VrcProfile profile)
    {
        RememberCurrentAvatar(
            _friend.User,
            profile.CurrentAvatar,
            profile.CurrentAvatarName,
            profile.CurrentAvatarImageUrl,
            profile.CurrentAvatarThumbnailImageUrl,
            profile.IconUrl ?? profile.UserIcon);
        EnsureCurrentAvatarPlaceholder();
    }

    /// <summary>
    /// VRChat can omit every avatar field for a private or limited profile. Show
    /// the locked card rather than an empty section; a later image/user payload
    /// fills the same card in.
    /// </summary>
    private void EnsureCurrentAvatarPlaceholder()
    {
        if (Avatars.Count > 0 || IsSelf)
        {
            return;
        }

        // A VRC+ profile picture is not evidence of an avatar record. Do not
        // manufacture a locked "avatar" card from it.
        var hasOnlyProfileArtwork =
            _profile is not null
                ? string.IsNullOrWhiteSpace(_profile.CurrentAvatar) &&
                  string.IsNullOrWhiteSpace(_profile.CurrentAvatarName) &&
                  (!string.IsNullOrWhiteSpace(_profile.IconUrl) ||
                   !string.IsNullOrWhiteSpace(_profile.UserIcon))
                : string.IsNullOrWhiteSpace(_friend.User.CurrentAvatar) &&
                  string.IsNullOrWhiteSpace(_friend.User.CurrentAvatarName) &&
                  (!string.IsNullOrWhiteSpace(_friend.User.IconUrl) ||
                   !string.IsNullOrWhiteSpace(_friend.User.UserIcon) ||
                   !string.IsNullOrWhiteSpace(_friend.User.ProfilePicOverride) ||
                   !string.IsNullOrWhiteSpace(_friend.User.ProfilePicOverrideThumbnail));
        if (hasOnlyProfileArtwork)
        {
            return;
        }

        var placeholder = new FriendAvatar
        {
            LastSeen = DateTimeOffset.UtcNow,
            IsCurrent = true
        };
        placeholder.MarkUnavailable();
        Avatars.Insert(0, placeholder);
        RaiseAvatarsChanged();
    }

    private void RemoveProfilePictureFallback()
    {
        foreach (var fallback in Avatars.Where(avatar => avatar.IsProfilePictureFallback).ToList())
        {
            Avatars.Remove(fallback);
        }
    }

    /// <summary>
    /// Resolves the first few avatar ids through VRChat. A public avatar fills
    /// in the shared detail popup; a 403/404 means the thumbnail stays visible
    /// with a private lock instead of the whole section failing.
    /// </summary>
    private async Task LoadAvatarDetailsAsync(CancellationToken cancellationToken)
    {
        if (_isResolvingAvatars)
        {
            _avatarResolveAgain = true;
            return;
        }

        _isResolvingAvatars = true;
        IsLoadingAvatars = true;
        try
        {
            // A profile response can contain the current-avatar picture without
            // the avatar id. Refresh the live user record first, otherwise a
            // perfectly public avatar can never be looked up and gets mistaken
            // for a private one.
            if (Avatars.Any(candidate => candidate.IsCurrent &&
                    (string.IsNullOrWhiteSpace(candidate.AvatarId) || candidate.IsPrivacyUnverified)))
            {
                try
                {
                    var liveUser = await _api.GetUserAsync(_userId, forceRefresh: true, cancellationToken);
                    if (liveUser is not null && !string.IsNullOrWhiteSpace(liveUser.CurrentAvatar))
                    {
                        RememberCurrentAvatar(liveUser, liveUser.CurrentAvatar, liveUser.CurrentAvatarName);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"avatar id refresh {_userId}: {ex.Message}");
                }
            }

            foreach (var item in Avatars
                         .Where(candidate => candidate.Details is null &&
                             (!candidate.IsPrivate ||
                              candidate.HasThumbnail ||
                              !string.IsNullOrWhiteSpace(candidate.ImageUrl)))
                         .Take(6)
                         .ToList())
            {
                await ResolveAvatarAsync(item, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }

            // A timeout or network failure can leave an unverified picture
            // candidate behind. Do not let that stop the author fallback or
            // masquerade as a locked avatar.
            foreach (var unresolved in Avatars
                         .Where(candidate => candidate.IsProfileImageCandidate)
                         .ToList())
            {
                Avatars.Remove(unresolved);
            }

            // VRChat can expose only profile artwork for a non-friend. In that
            // case the avatar section is empty, but a public avatar already
            // found by Avatar Search can still identify the current avatar.
            if (!cancellationToken.IsCancellationRequested && !HasKnownCurrentAvatar)
            {
                await AddRememberedCurrentAvatarAsync(cancellationToken);
            }

            // VRCX/VRCNext resolve an avatar used as a profile icon through
            // VRChat's file metadata. The file name carries the avatar name and
            // its ownerId is the uploader, even when /profile hides the avatar.
            if (!cancellationToken.IsCancellationRequested && !HasKnownCurrentAvatar)
            {
                await AddProfileIconAvatarAsync(cancellationToken);
            }

            // VRChat's own log records the avatar switch for remote players.
            // VRCNext uses this when /profile omits the avatar entirely.
            if (!cancellationToken.IsCancellationRequested && !HasKnownCurrentAvatar)
            {
                await AddLoggedAvatarAsync(cancellationToken);
            }

            // If the search cache has no match, look for the profile's actual
            // current avatar in the public indexes. Author-only rows are not
            // enough: those are the person's uploads, not what they are wearing.
            if (!cancellationToken.IsCancellationRequested && !HasKnownCurrentAvatar)
            {
                await LoadIndexedPublicAvatarsAsync(cancellationToken);
            }

            // Some profiles expose neither a wearable avatar nor any matching
            // index row. Keep a clearly-labelled profile picture card instead
            // of an empty AVATARS section.
            if (!cancellationToken.IsCancellationRequested && Avatars.Count == 0)
            {
                AddProfilePictureFallback();
            }
        }
        finally
        {
            _isResolvingAvatars = false;
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoadingAvatars = false;
            }

            if (_avatarResolveAgain)
            {
                _avatarResolveAgain = false;
                _ = LoadAvatarDetailsAsync(CancellationToken.None);
            }
        }
    }

    private bool HasKnownCurrentAvatar => Avatars.Any(candidate =>
        candidate.IsCurrent &&
        !candidate.IsProfilePictureFallback &&
        (!string.IsNullOrWhiteSpace(candidate.AvatarId) ||
         candidate.HasThumbnail ||
         !string.IsNullOrWhiteSpace(candidate.Name)));

    /// <summary>
    /// Recovers a current avatar that only exists in this session's Avatar
    /// Search results. This covers profiles where VRChat hides the avatar id
    /// and its picture is the same as the profile artwork, so the normal
    /// candidate was intentionally not added yet.
    /// </summary>
    private async Task AddProfileIconAvatarAsync(CancellationToken cancellationToken)
    {
        if (_profile is null)
        {
            return;
        }

        var artwork = _profile.IconUrl ??
                      _profile.UserIcon ??
                      _friend.User.IconUrl ??
                      _friend.User.UserIcon;
        if (string.IsNullOrWhiteSpace(artwork))
        {
            return;
        }

        var fileId = ImageUrlHelper.ExtractFileId(artwork);
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return;
        }

        var lookupKey = fileId + "|" + artwork;
        if (string.Equals(_profileIconAvatarLookupKey, lookupKey, StringComparison.Ordinal) ||
            _isResolvingProfileIconAvatar)
        {
            return;
        }

        if (Avatars.Any(avatar =>
                SameAvatarImage(avatar.ImageUrl, artwork) ||
                SameAvatarImage(avatar.ThumbnailUrl, artwork)))
        {
            return;
        }

        _isResolvingProfileIconAvatar = true;
        try
        {
            VrcFile? file;
            try
            {
                file = await _api.GetFileAsync(fileId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"profile icon file {fileId}: {ex.Message}");
                return;
            }

            // A custom profile icon is tagged "icon". An avatar image may be
            // tagged "avatarimage" and named file_..._blob, so do not require
            // the optional embedded avatar name here.
            if (file is null || !file.IsAvatarImage)
            {
                _profileIconAvatarLookupKey = lookupKey;
                return;
            }

            var avatarName = file.AvatarName;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            Avatar? indexed = null;

            try
            {
                indexed = await _api.ResolveCommunityAvatarByImageAsync(
                    artwork,
                    _userId,
                    deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Provider deadline is a miss; continue with the other indexes.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"profile icon image index {_userId}: {ex.Message}");
            }

            if (indexed is null && !string.IsNullOrWhiteSpace(avatarName))
            {
                try
                {
                    var rows = await _api.SearchAllCommunityAvatarsAsync(
                        avatarName,
                        providerLimit: 40,
                        cancellationToken: deadline.Token);
                    indexed = rows.FirstOrDefault(avatar =>
                        SameAvatarImage(avatar.ImageUrl, artwork) ||
                        SameAvatarImage(avatar.ThumbnailImageUrl, artwork));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A slow index must not block the profile icon fallback.
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"profile icon name index {file.AvatarName}: {ex.Message}");
                }
            }

            if (indexed is null && !string.IsNullOrWhiteSpace(file.OwnerId))
            {
                try
                {
                    var rows = await _api.SearchCommunityByAuthorAsync(
                        file.OwnerId,
                        providerLimit: 40,
                        cancellationToken: deadline.Token);
                    indexed = rows.FirstOrDefault(avatar =>
                        SameAvatarImage(avatar.ImageUrl, artwork) ||
                        SameAvatarImage(avatar.ThumbnailImageUrl, artwork) ||
                        (!string.IsNullOrWhiteSpace(avatarName) &&
                         string.Equals(avatar.Name.Trim(), avatarName,
                             StringComparison.OrdinalIgnoreCase)));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Provider deadline; the metadata-only card is still useful.
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"profile icon author index {file.OwnerId}: {ex.Message}");
                }
            }

            FriendAvatar item;
            if (indexed is not null && !string.IsNullOrWhiteSpace(indexed.Id))
            {
                Avatar? full = null;
                try
                {
                    full = await _api.GetAvatarAsync(indexed.Id, deadline.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"profile icon avatar detail {indexed.Id}: {ex.Message}");
                }

                if (full is not null)
                {
                    FillMissingAvatarDetails(full, indexed);
                    indexed = full;
                }

                item = new FriendAvatar
                {
                    AvatarId = indexed.Id,
                    SourceName = indexed.SourceName ?? string.Empty,
                    IsCurrent = true,
                    LastSeen = DateTimeOffset.UtcNow
                };
                item.ApplyDetails(indexed);
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    item.Name = string.IsNullOrWhiteSpace(avatarName)
                        ? "Current avatar"
                        : avatarName;
                }
            }
            else
            {
                // VRCX shows the name/author from file metadata even when no
                // public database knows the avatar. Keep the card identified
                // instead of returning an empty AVATARS section.
                item = new FriendAvatar
                {
                    Name = string.IsNullOrWhiteSpace(avatarName)
                        ? "Current avatar"
                        : avatarName,
                    ImageUrl = artwork,
                    ThumbnailUrl = ImageUrlHelper.ToThumbnailUrl(artwork, 256) ?? artwork,
                    IsCurrent = true,
                    LastSeen = DateTimeOffset.UtcNow
                };
            }

            foreach (var other in Avatars.Where(avatar => !ReferenceEquals(avatar, item)))
            {
                other.IsCurrent = false;
            }

            RemoveProfilePictureFallback();
            Avatars.Insert(0, item);
            _profileIconAvatarLookupKey = lookupKey;
            RaiseAvatarsChanged();
        }
        finally
        {
            _isResolvingProfileIconAvatar = false;
        }
    }

    /// <summary>
    /// The five-second log poll hands over the avatar switch. Ignore duplicates
    /// and let the normal resolver replace the current card when it changes.
    /// </summary>
    public void ApplyLoggedAvatar(LoggedAvatar? logged)
    {
        if (logged is null || string.IsNullOrWhiteSpace(logged.Name))
        {
            return;
        }

        var key = logged.Name.Trim() + "|" + (logged.AuthorName ?? string.Empty).Trim();
        if (string.Equals(_loggedAvatarKey, key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = AddLoggedAvatarAsync(logged, CancellationToken.None);
    }

    private async Task AddLoggedAvatarAsync(CancellationToken cancellationToken)
    {
        var logged = VrChatLogAvatarService.Shared.GetAvatar(
            _userId,
            _profile?.DisplayName ?? _friend.DisplayName);
        if (logged is null || string.IsNullOrWhiteSpace(logged.Name))
        {
            return;
        }

        await AddLoggedAvatarAsync(logged, cancellationToken);
    }

    private async Task AddLoggedAvatarAsync(
        LoggedAvatar logged,
        CancellationToken cancellationToken)
    {
        var loggedName = logged.Name.Trim();
        _loggedAvatarKey = loggedName + "|" + (logged.AuthorName ?? string.Empty).Trim();
        var loggedAuthor = logged.AuthorName?.Trim();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        Avatar? match;
        try
        {
            var rows = new List<Avatar>();
            // The log pairs the switch with "Unpacking Avatar (name by author)".
            // Search the author first because names like "jane" are not unique.
            if (!string.IsNullOrWhiteSpace(loggedAuthor))
            {
                rows.AddRange(await _api.SearchAllCommunityAvatarsAsync(
                    loggedAuthor,
                    providerLimit: 100,
                    cancellationToken: deadline.Token));
            }

            match = PickLoggedAvatar(rows, loggedName, loggedAuthor);
            if (match is null)
            {
                rows.AddRange(await _api.SearchAllCommunityAvatarsAsync(
                    loggedName,
                    providerLimit: 100,
                    cancellationToken: deadline.Token));
                match = PickLoggedAvatar(rows, loggedName, loggedAuthor);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"logged avatar {loggedName}: {ex.Message}");
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (match is null)
        {
            // The new avatar may already be in the profile's local history.
            // Promote that card instead of leaving the previous avatar current.
            var known = Avatars.FirstOrDefault(avatar =>
                !avatar.IsProfilePictureFallback &&
                string.Equals(avatar.Name.Trim(), loggedName, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                PromoteCurrentAvatar(known);
            }

            return;
        }

        try
        {
            var full = await _api.GetAvatarAsync(match.Id, deadline.Token);
            if (full is not null)
            {
                FillMissingAvatarDetails(full, match);
                match = full;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"logged avatar detail {match.Id}: {ex.Message}");
        }

        var item = Avatars.FirstOrDefault(avatar =>
            !avatar.IsProfilePictureFallback &&
            (string.Equals(avatar.AvatarId, match.Id, StringComparison.Ordinal) ||
             (string.IsNullOrWhiteSpace(avatar.AvatarId) &&
              string.Equals(avatar.Name.Trim(), loggedName, StringComparison.OrdinalIgnoreCase))));
        if (item is null)
        {
            item = new FriendAvatar();
            Avatars.Insert(0, item);
        }

        item.AvatarId = match.Id;
        item.SourceName = match.SourceName ?? string.Empty;
        item.LastSeen = DateTimeOffset.UtcNow;
        item.ApplyDetails(match);
        PromoteCurrentAvatar(item);
    }

    private void PromoteCurrentAvatar(FriendAvatar item)
    {
        RemoveProfilePictureFallback();
        foreach (var other in Avatars.Where(avatar => !ReferenceEquals(avatar, item)))
        {
            other.IsCurrent = false;
        }

        item.IsCurrent = true;
        var index = Avatars.IndexOf(item);
        if (index > 0)
        {
            Avatars.Move(index, 0);
        }
        else if (index < 0)
        {
            Avatars.Insert(0, item);
        }

        RaiseAvatarsChanged();
    }

    private static Avatar? PickLoggedAvatar(
        IEnumerable<Avatar> rows,
        string loggedName,
        string? loggedAuthor)
    {
        var exactName = rows
            .Where(avatar =>
                !string.IsNullOrWhiteSpace(avatar.Id) &&
                !avatar.IsPrivate &&
                !avatar.IsUnavailable &&
                string.Equals(avatar.Name.Trim(), loggedName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(avatar => avatar.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (exactName.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(loggedAuthor))
        {
            var byAuthor = exactName
                .Where(avatar => string.Equals(
                    (avatar.AuthorName ?? string.Empty).Trim(),
                    loggedAuthor ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byAuthor.Count > 0)
            {
                return byAuthor[0];
            }

            // The log named an author but no index result matches it. Do not
            // guess between several avatars that merely share a common name.
            return exactName.Count == 1 ? exactName[0] : null;
        }

        return exactName.Count == 1
            ? exactName[0]
            : exactName
                .OrderByDescending(avatar => avatar.UpdatedAt == default
                    ? avatar.CreatedAt
                    : avatar.UpdatedAt)
                .FirstOrDefault();
    }

    private async Task AddRememberedCurrentAvatarAsync(CancellationToken cancellationToken)
    {
        var imageUrl = _profile?.CurrentAvatarImageUrl ?? _friend.User.CurrentAvatarImageUrl;
        var thumbnailUrl = _profile?.CurrentAvatarThumbnailImageUrl ??
                           _friend.User.CurrentAvatarThumbnailImageUrl;
        var name = _profile?.CurrentAvatarName ?? _friend.User.CurrentAvatarName;
        var profileArtwork = _profile?.UserIcon ??
                             _profile?.IconUrl ??
                             _friend.User.UserIcon ??
                             _friend.User.IconUrl;
        if (string.IsNullOrWhiteSpace(imageUrl) &&
            string.IsNullOrWhiteSpace(thumbnailUrl) &&
            string.IsNullOrWhiteSpace(name) &&
            string.IsNullOrWhiteSpace(profileArtwork))
        {
            return;
        }

        // A profile can set its profile picture to an avatar thumbnail. Only
        // accept that picture when an avatar search row matches its file id;
        // otherwise it is still profile artwork, not an avatar.
        var item = new FriendAvatar
        {
            Name = name ?? string.Empty,
            ImageUrl = imageUrl ?? profileArtwork ?? string.Empty,
            ThumbnailUrl = thumbnailUrl ?? profileArtwork ?? string.Empty,
            IsCurrent = true,
            LastSeen = DateTimeOffset.UtcNow
        };
        var resolved = await ResolveRememberedAvatarAsync(
            item,
            cancellationToken,
            profileArtwork);
        if (resolved is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        RemoveProfilePictureFallback();
        if (Avatars.Any(avatar =>
                string.Equals(avatar.AvatarId, item.AvatarId, StringComparison.Ordinal)))
        {
            return;
        }

        Avatars.Insert(0, item);
        RaiseAvatarsChanged();
    }

    /// <summary>
    /// VRChat does not expose another user's public avatar catalogue through
    /// the user profile. The VRCX provider contract does, by exact author id.
    /// Query the community indexes so people with no visible current avatar
    /// still get real, clickable public avatars.
    /// </summary>
    private string CurrentIndexedAvatarKey()
    {
        // VRChat returns the profile in a later request than the friend/live
        // user. The initial lookup has no avatar author for a non-friend, so a
        // completed empty lookup must be retried when the profile supplies new
        // avatar hints.
        return string.Join("|", new[]
        {
            _profile?.DisplayName ?? _friend.DisplayName,
            _profile?.CurrentAvatarAuthorName ?? string.Empty,
            _profile?.CurrentAvatarName ?? _friend.User.CurrentAvatarName ?? string.Empty,
            _profile?.CurrentAvatarImageUrl ?? _friend.User.CurrentAvatarImageUrl ?? string.Empty,
            _profile?.CurrentAvatarThumbnailImageUrl ??
                _friend.User.CurrentAvatarThumbnailImageUrl ?? string.Empty,
            _profile?.UserIcon ?? _profile?.IconUrl ??
                _friend.User.UserIcon ?? _friend.User.IconUrl ?? string.Empty
        });
    }

    private async Task LoadIndexedPublicAvatarsAsync(CancellationToken cancellationToken)
    {
        if (_api.CurrentUser is null || string.IsNullOrWhiteSpace(_userId))
        {
            return;
        }

        var lookupKey = CurrentIndexedAvatarKey();
        if (_indexedAvatarsLoaded &&
            string.Equals(_indexedAvatarLookupKey, lookupKey, StringComparison.Ordinal))
        {
            return;
        }

        if (_isLoadingIndexedAvatars)
        {
            _indexedAvatarLookupAgain = true;
            return;
        }

        _isLoadingIndexedAvatars = true;
        try
        {
            var communityTask = SearchCommunityPublicAvatarsAsync(cancellationToken);
            var avatarAuthorTask = SearchAvatarAuthorAvatarsAsync(cancellationToken);
            await Task.WhenAll(communityTask, avatarAuthorTask);
            var community = await communityTask;
            var avatarAuthorAvatars = await avatarAuthorTask;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var seen = Avatars
                .Where(avatar => !string.IsNullOrWhiteSpace(avatar.AvatarId))
                .Select(avatar => avatar.AvatarId)
                .ToHashSet(StringComparer.Ordinal);
            var authored = community
                .Where(avatar =>
                    !string.IsNullOrWhiteSpace(avatar.Id) &&
                    !avatar.IsPrivate &&
                    !avatar.IsUnavailable &&
                    (string.IsNullOrWhiteSpace(avatar.AuthorId) ||
                     string.Equals(avatar.AuthorId, _userId, StringComparison.Ordinal)));
            var indexed = authored
                .Concat(avatarAuthorAvatars)
                .Where(avatar =>
                    !string.IsNullOrWhiteSpace(avatar.Id) &&
                    !avatar.IsPrivate &&
                    !avatar.IsUnavailable &&
                    MatchesCurrentAvatarHints(avatar))
                .GroupBy(avatar => avatar.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(avatar => avatar.UpdatedAt == default
                    ? avatar.CreatedAt
                    : avatar.UpdatedAt)
                .Take(24);

            foreach (var avatar in indexed)
            {
                if (!seen.Add(avatar.Id))
                {
                    continue;
                }

                var item = new FriendAvatar
                {
                    AvatarId = avatar.Id,
                    SourceName = avatar.SourceName ?? string.Empty,
                    IsIndexed = false,
                    LastSeen = DateTimeOffset.UtcNow
                };
                item.ApplyDetails(avatar);
                item.IsCurrent = true;
                RemoveProfilePictureFallback();
                Avatars.Add(item);
            }

            _indexedAvatarLookupKey = lookupKey;
            _indexedAvatarsLoaded = true;
            RaiseAvatarsChanged();
        }
        catch (OperationCanceledException)
        {
            // The profile was closed or switched while the indexes were loading.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"indexed avatars {_userId}: {ex.Message}");
        }
        finally
        {
            _isLoadingIndexedAvatars = false;
            if (_indexedAvatarLookupAgain)
            {
                _indexedAvatarLookupAgain = false;
                _indexedAvatarsLoaded = false;
                _ = LoadIndexedPublicAvatarsAsync(CancellationToken.None);
            }
        }
    }

    private async Task<List<Avatar>> SearchCommunityPublicAvatarsAsync(
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            return await _api.SearchCommunityByAuthorAsync(
                _userId,
                providerLimit: 30,
                cancellationToken: deadline.Token,
                authorName: _profile?.DisplayName ?? _friend.DisplayName);
        }
        catch (OperationCanceledException)
        {
            return new List<Avatar>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"community author avatars {_userId}: {ex.Message}");
            return new List<Avatar>();
        }
    }

    private async Task<List<Avatar>> SearchAvatarAuthorAvatarsAsync(
        CancellationToken cancellationToken)
    {
        var authorName = _profile?.CurrentAvatarAuthorName;
        if (string.IsNullOrWhiteSpace(authorName) ||
            string.Equals(authorName.Trim(), _profile?.DisplayName ?? _friend.DisplayName,
                StringComparison.OrdinalIgnoreCase))
        {
            return new List<Avatar>();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            return await _api.SearchCommunityByAuthorNameAsync(
                authorName,
                providerLimit: 30,
                cancellationToken: deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return new List<Avatar>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"community avatar author {authorName}: {ex.Message}");
            return new List<Avatar>();
        }
    }

    /// <summary>
    /// Resolves an avatar id through the public avatar indexes and applies it to the card.
    /// VRChat refusing a record by id is not proof that the avatar is private, so the card
    /// is only locked after an index that carries this exact id has also been checked - and
    /// a hit here is that same avatar, not a namesake or a same-looking picture.
    /// </summary>
    private async Task<Avatar?> ConfirmPublicAvatarAsync(
        FriendAvatar item,
        string avatarId,
        CancellationToken cancellationToken)
    {
        Avatar? indexed;
        try
        {
            indexed = await _api.FindPublicAvatarByIdAsync(
                avatarId,
                item.Name,
                TrustedAvatarPicture(item),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"public avatar id {avatarId}: {ex.Message}");
            return null;
        }

        if (indexed is null || string.IsNullOrWhiteSpace(indexed.Id))
        {
            return null;
        }

        item.AvatarId = indexed.Id;
        item.ApplyDetails(indexed);
        _avatarStore.RememberImageResolution(
            _api.CurrentUser?.Id,
            string.IsNullOrWhiteSpace(item.ImageUrl) ? item.ThumbnailUrl : item.ImageUrl,
            indexed.Id,
            indexed.ReleaseStatus);
        _avatarStore.RememberDetails(_api.CurrentUser?.Id, _userId, indexed, item.IsCurrent);
        return indexed;
    }

    /// <summary>
    /// The picture this card can compare with an index record. Only the profile's own
    /// current-avatar image for this row counts: a custom profile image (icon, profile
    /// picture override, or artwork VRChat echoed into the avatar fields) is a different
    /// file from the avatar's picture, so comparing it would reject the correct record and
    /// lock a public avatar.
    /// </summary>
    private string? TrustedAvatarPicture(FriendAvatar item)
    {
        if (!item.IsCurrent)
        {
            return null;
        }

        var profileId = _profile?.CurrentAvatar;
        if (!string.IsNullOrWhiteSpace(profileId) &&
            !string.IsNullOrWhiteSpace(item.AvatarId) &&
            !string.Equals(profileId.Trim(), item.AvatarId.Trim(), StringComparison.Ordinal))
        {
            // The profile now names a different avatar, so this row's picture is history.
            return null;
        }

        var image = FirstNonEmpty(
            _profile?.CurrentAvatarImageUrl,
            _friend.User.CurrentAvatarImageUrl,
            _profile?.CurrentAvatarThumbnailImageUrl,
            _friend.User.CurrentAvatarThumbnailImageUrl);
        if (image is null)
        {
            return null;
        }

        var artwork = new[]
        {
            _profile?.UserIcon,
            _profile?.IconUrl,
            _friend.User.ProfilePicOverride,
            _friend.User.ProfilePicOverrideThumbnail,
            _friend.User.UserIcon,
            _friend.User.IconUrl
        };
        return artwork.Any(candidate => SameImage(image, candidate)) ? null : image;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private async Task<Avatar?> ResolveAvatarAsync(
        FriendAvatar item,
        CancellationToken cancellationToken)
    {
        if (item.Details is not null && !item.IsPrivate && !item.Details.IsUnavailable)
        {
            return item.Details;
        }

        // Always ask VRChat for an avatar id before trusting a stored "private"
        // label. Profile pictures are custom thumbnails, so an image-index miss
        // is not proof that the avatar is private. The VRChat record is
        // authoritative for public/private, name, author, dates, and platforms;
        // it also returns the full record for your own private avatars.
        if (!string.IsNullOrWhiteSpace(item.AvatarId) &&
            (!item.IsPrivate || item.IsPrivacyUnverified || IsSelf))
        {
            var recordRejected = false;
            try
            {
                var resolvedAvatar = await _api.GetAvatarAsync(item.AvatarId, cancellationToken);
                if (resolvedAvatar is not null)
                {
                    CompletePrivateOwner(resolvedAvatar, item);
                    item.ApplyDetails(resolvedAvatar);
                    _avatarStore.RememberImageResolution(
                        _api.CurrentUser?.Id,
                        string.IsNullOrWhiteSpace(item.ImageUrl) ? item.ThumbnailUrl : item.ImageUrl,
                        resolvedAvatar.Id,
                        resolvedAvatar.ReleaseStatus);
                    _avatarStore.RememberDetails(_api.CurrentUser?.Id, _userId, resolvedAvatar, item.IsCurrent);
                    return resolvedAvatar;
                }

                // A 403/404 from the avatar endpoint means the record is no
                // longer publicly readable. Keep the locked state, but only
                // after the authoritative lookup failed.
                recordRejected = true;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                // A timeout, network error, or 429 is not proof that the
                // avatar is private. Preserve the state we already have and
                // retry on the next profile open.
                Debug.WriteLine($"avatar details {item.AvatarId}: {ex.Message}");
                return item.Details;
            }

            if (recordRejected)
            {
                // VRChat refused the record by id, which happens for a deleted avatar but
                // also for a public one it will not expose. Only lock the card when no
                // public index carries this same id.
                var confirmed = await ConfirmPublicAvatarAsync(item, item.AvatarId, cancellationToken);
                if (confirmed is not null)
                {
                    return confirmed;
                }

                item.MarkUnavailable();
            }
        }

        if (string.IsNullOrWhiteSpace(item.AvatarId) || item.IsPrivate)
        {
            if (item.IsPrivate)
            {
                // A stored "private" label is only a guess until the record confirms it.
                // A public index that lists this exact id proves the avatar is public.
                if (item.IsPrivacyUnverified && !string.IsNullOrWhiteSpace(item.AvatarId))
                {
                    var confirmed = await ConfirmPublicAvatarAsync(item, item.AvatarId, cancellationToken);
                    if (confirmed is not null)
                    {
                        return confirmed;
                    }
                }

                // A public avatar-search hit is stronger evidence than a
                // refused profile lookup. Use it before leaving the card locked.
                var searchAvatar = await ResolveRememberedAvatarAsync(item, cancellationToken);
                if (searchAvatar is not null)
                {
                    return searchAvatar;
                }

                // If the avatar endpoint just failed, the community lookup still
                // may recover the name, dates, and platforms from its public
                // history without changing the locked state.
                // Private VRChat records return 403/404, but a community index
                // may still know the name/date/platforms of an avatar that was
                // public before. Use that metadata without enabling its actions.
                if (item.Details is null &&
                    (item.HasThumbnail || !string.IsNullOrWhiteSpace(item.ImageUrl)))
                {
                    try
                    {
                        var privateMetadata = await _api.ResolveCommunityAvatarByImageAsync(
                            string.IsNullOrWhiteSpace(item.ImageUrl) ? item.ThumbnailUrl : item.ImageUrl,
                            _userId,
                            cancellationToken);
                        if (privateMetadata is not null)
                        {
                            // The index may identify the correct avatar id when
                            // the user payload carried none, or when the stored
                            // id was stale. Check that id against VRChat before
                            // accepting an inferred private state; a public
                            // avatar must not stay locked because its picture
                            // was a custom thumbnail.
                            if (!string.IsNullOrWhiteSpace(privateMetadata.Id) &&
                                !string.Equals(privateMetadata.Id, item.AvatarId, StringComparison.Ordinal))
                            {
                                Avatar? publicRecord = null;
                                try
                                {
                                    publicRecord = await _api.GetAvatarAsync(
                                        privateMetadata.Id,
                                        cancellationToken);
                                }
                                catch (OperationCanceledException)
                                {
                                    return null;
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"private avatar id check {privateMetadata.Id}: {ex.Message}");
                                }

                                if (publicRecord is not null && !publicRecord.IsPrivate)
                                {
                                    CompletePrivateOwner(publicRecord, item);
                                    item.AvatarId = publicRecord.Id;
                                    item.ApplyDetails(publicRecord);
                                    _avatarStore.RememberImageResolution(
                                        _api.CurrentUser?.Id,
                                        string.IsNullOrWhiteSpace(item.ImageUrl) ? item.ThumbnailUrl : item.ImageUrl,
                                        publicRecord.Id,
                                        publicRecord.ReleaseStatus);
                                    _avatarStore.RememberDetails(
                                        _api.CurrentUser?.Id,
                                        _userId,
                                        publicRecord,
                                        item.IsCurrent);
                                    return publicRecord;
                                }
                            }

                            // A community index can still know the old public
                            // details of an avatar whose VRChat record is now
                            // blocked. It can fill metadata, but it cannot prove
                            // that the avatar is private, so keep the state
                            // explicitly unverified instead of guessing.
                            privateMetadata.ReleaseStatus = "unavailable";
                            if (string.IsNullOrWhiteSpace(privateMetadata.Description))
                            {
                                privateMetadata.Description =
                                    "VRChat did not expose this avatar's record. It may be private, deleted, or otherwise unavailable.";
                            }
                            CompletePrivateOwner(privateMetadata, item);
                            if (string.IsNullOrWhiteSpace(item.AvatarId))
                            {
                                item.AvatarId = privateMetadata.Id;
                            }

                            item.ApplyDetails(privateMetadata);
                            item.IsPrivacyUnverified = true;
                            _avatarStore.RememberImageResolution(
                                _api.CurrentUser?.Id,
                                string.IsNullOrWhiteSpace(item.ImageUrl) ? item.ThumbnailUrl : item.ImageUrl,
                                privateMetadata.Id,
                                "private");
                            return privateMetadata;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"private avatar metadata {_userId}: {ex.Message}");
                    }
                }

                // Even when no index knew this picture, never leave a bare
                // "private" labels on a record that VRChat refused to expose.
                item.MarkUnavailable();
                return null;
            }

            try
            {
                // VRChat can return only the avatar picture for another user.
                // VRCX resolves that exact case through the public avatar
                // indexes by the image's file id.
                var communityAvatar = await _api.ResolveCommunityAvatarByImageAsync(
                    string.IsNullOrWhiteSpace(item.ImageUrl) ? item.ThumbnailUrl : item.ImageUrl,
                    _userId,
                    cancellationToken);
                if (communityAvatar is not null)
                {
                    // The community hit identifies the avatar but often omits
                    // creation time and platform bundles. Fetch the public
                    // VRChat record now so the popup is complete; keep the
                    // community record if VRChat no longer exposes it.
                    Avatar? fullAvatar = null;
                    try
                    {
                        fullAvatar = await _api.GetAvatarAsync(communityAvatar.Id, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                    catch (Exception ex)
                    {
                        // Keep the public community record even if the detail
                        // endpoint is temporarily unavailable.
                        Debug.WriteLine($"avatar detail hydration {communityAvatar.Id}: {ex.Message}");
                    }

                    if (fullAvatar is not null)
                    {
                        FillMissingAvatarDetails(fullAvatar, communityAvatar);
                        communityAvatar = fullAvatar;
                    }

                    item.AvatarId = communityAvatar.Id;
                    item.ApplyDetails(communityAvatar);
                    _avatarStore.RememberImageResolution(
                        _api.CurrentUser?.Id,
                        string.IsNullOrWhiteSpace(item.ImageUrl) ? item.ThumbnailUrl : item.ImageUrl,
                        communityAvatar.Id,
                        communityAvatar.ReleaseStatus);
                    _avatarStore.RememberDetails(
                        _api.CurrentUser?.Id,
                        _userId,
                        communityAvatar,
                        item.IsCurrent);
                    return communityAvatar;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A community provider hit its own short lookup deadline. That
                // is a miss, not a reason to discard an avatar-search result.
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"avatar image resolve {_userId}: {ex.Message}");
            }

            // The avatar search may already have the exact public record even
            // when the image index does not answer for its file id.
            var rememberedAvatar = await ResolveRememberedAvatarAsync(item, cancellationToken);
            if (rememberedAvatar is not null)
            {
                return rememberedAvatar;
            }

            // Non-friends often expose the current avatar's name without its
            // id (the image is a custom profile thumbnail). Try the public
            // avatar search by exact name before giving up on the record.
            if (string.IsNullOrWhiteSpace(item.AvatarId) &&
                !string.IsNullOrWhiteSpace(item.Name) &&
                !string.Equals(item.Name, "Avatar", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.Name, "Private avatar", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.Name, "Avatar unavailable", StringComparison.OrdinalIgnoreCase))
            {
                var namedAvatar = await ResolvePublicAvatarByNameAsync(item, cancellationToken);
                if (namedAvatar is not null)
                {
                    return namedAvatar;
                }
            }

            // A profile picture that differs from the profile icon was only
            // a lookup candidate. If the indexes cannot identify it, remove it
            // instead of claiming the avatar is private when VRChat never
            // supplied an avatar record at all.
            if (item.IsProfileImageCandidate)
            {
                Avatars.Remove(item);
                RaiseAvatarsChanged();
                return null;
            }

            // A picture no public index can identify is treated as private /
            // unavailable, so the card gets a lock instead of staying on
            // "Checking…" forever.
            if (item.HasThumbnail || !string.IsNullOrWhiteSpace(item.ImageUrl))
            {
                item.MarkUnavailable();
                _avatarStore.RememberImageResolution(
                    _api.CurrentUser?.Id,
                    string.IsNullOrWhiteSpace(item.ImageUrl) ? item.ThumbnailUrl : item.ImageUrl,
                    null,
                    "private");
            }

            return null;
        }

        try
        {
            var avatar = await _api.GetAvatarAsync(item.AvatarId, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (avatar is null)
            {
                // Before locking a card whose id VRChat would not answer for, ask the
                // public indexes for that exact id. A public avatar that lost nothing but
                // VRChat's endpoint must not stay locked.
                var confirmed = await ConfirmPublicAvatarAsync(item, item.AvatarId, cancellationToken);
                if (confirmed is not null)
                {
                    return confirmed;
                }

                item.MarkUnavailable();
                _avatarStore.MarkUnavailable(_api.CurrentUser?.Id, _userId, item.AvatarId);
                _avatarStore.RememberImageResolution(
                    _api.CurrentUser?.Id,
                    string.IsNullOrWhiteSpace(item.ImageUrl) ? item.ThumbnailUrl : item.ImageUrl,
                    item.AvatarId,
                    "private");
                return null;
            }

            item.ApplyDetails(avatar);
            _avatarStore.RememberImageResolution(
                _api.CurrentUser?.Id,
                string.IsNullOrWhiteSpace(item.ImageUrl) ? item.ThumbnailUrl : item.ImageUrl,
                avatar.Id,
                avatar.ReleaseStatus);
            _avatarStore.RememberDetails(_api.CurrentUser?.Id, _userId, avatar, item.IsCurrent);
            return avatar;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"avatar details {item.AvatarId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Uses a public avatar that the avatar search already loaded. The search
    /// row is matched by picture first, then by exact name, so a profile whose
    /// avatar id is hidden can still resolve the avatar the person is wearing
    /// without trusting an unrelated private or similarly named record.
    /// </summary>
    private async Task<Avatar?> ResolveRememberedAvatarAsync(
        FriendAvatar item,
        CancellationToken cancellationToken,
        params string?[] extraImageUrls)
    {
        var remembered = AvatarLookupCache.FindByImage(
            new[] { item.ImageUrl, item.ThumbnailUrl }
                .Concat(extraImageUrls)
                .ToArray());
        if (remembered is null &&
            !string.Equals(item.Name, "Avatar", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.Name, "Private avatar", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.Name, "Avatar unavailable", StringComparison.OrdinalIgnoreCase))
        {
            remembered = AvatarLookupCache.FindByName(item.Name);
        }

        if (remembered is null || string.IsNullOrWhiteSpace(remembered.Id))
        {
            return null;
        }

        Avatar? full = null;
        try
        {
            full = await _api.GetAvatarAsync(remembered.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            // The search row is already public and complete enough to use
            // when VRChat's detail endpoint is temporarily unavailable.
            Debug.WriteLine($"avatar remembered detail {remembered.Id}: {ex.Message}");
        }

        if (full is not null)
        {
            FillMissingAvatarDetails(full, remembered);
            remembered = full;
        }

        if (remembered.IsPrivate || remembered.IsUnavailable)
        {
            return null;
        }

        item.AvatarId = remembered.Id;
        item.ApplyDetails(remembered);
        _avatarStore.RememberImageResolution(
            _api.CurrentUser?.Id,
            string.IsNullOrWhiteSpace(item.ImageUrl) ? item.ThumbnailUrl : item.ImageUrl,
            remembered.Id,
            remembered.ReleaseStatus);
        _avatarStore.RememberDetails(
            _api.CurrentUser?.Id,
            _userId,
            remembered,
            item.IsCurrent);
        return remembered;
    }

    /// <summary>
    /// A limited profile can include the current avatar's name but omit its
    /// id. Search the public catalogue for an exact name match; never guess
    /// when the name is ambiguous or the result is not public.
    /// </summary>
    private async Task<Avatar?> ResolvePublicAvatarByNameAsync(
        FriendAvatar item,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _api.SearchAvatarsAsync(
                new AvatarFilter
                {
                    SearchTerm = item.Name.Trim(),
                    ReleaseStatus = "all",
                    PageSize = 50
                },
                cancellationToken);
            var matches = result.Avatars
                .Where(avatar =>
                    !string.IsNullOrWhiteSpace(avatar.Id) &&
                    string.Equals(avatar.Name.Trim(), item.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                return null;
            }

            var match = matches[0];
            Avatar? full = null;
            try
            {
                full = await _api.GetAvatarAsync(match.Id, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"avatar name detail {match.Id}: {ex.Message}");
            }

            if (full is not null)
            {
                FillMissingAvatarDetails(full, match);
                match = full;
            }

            if (match.IsPrivate || match.IsUnavailable)
            {
                return null;
            }

            item.AvatarId = match.Id;
            item.ApplyDetails(match);
            _avatarStore.RememberImageResolution(
                _api.CurrentUser?.Id,
                string.IsNullOrWhiteSpace(item.ImageUrl) ? item.ThumbnailUrl : item.ImageUrl,
                match.Id,
                match.ReleaseStatus);
            _avatarStore.RememberDetails(_api.CurrentUser?.Id, _userId, match, item.IsCurrent);
            return match;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"avatar name resolve {_userId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Keeps provider-only artwork or metadata when VRChat returns a partial record.</summary>
    private static void FillMissingAvatarDetails(Avatar primary, Avatar fallback)
    {
        if (string.IsNullOrWhiteSpace(primary.Name)) primary.Name = fallback.Name;
        if (string.IsNullOrWhiteSpace(primary.AuthorId)) primary.AuthorId = fallback.AuthorId;
        if (string.IsNullOrWhiteSpace(primary.AuthorName)) primary.AuthorName = fallback.AuthorName;
        if (string.IsNullOrWhiteSpace(primary.ImageUrl)) primary.ImageUrl = fallback.ImageUrl;
        if (string.IsNullOrWhiteSpace(primary.ThumbnailImageUrl)) primary.ThumbnailImageUrl = fallback.ThumbnailImageUrl;
        if (string.IsNullOrWhiteSpace(primary.ReleaseStatus)) primary.ReleaseStatus = fallback.ReleaseStatus;
        if (string.IsNullOrWhiteSpace(primary.Description)) primary.Description = fallback.Description;
        if (primary.CreatedAt == default) primary.CreatedAt = fallback.CreatedAt;
        if (primary.UpdatedAt == default) primary.UpdatedAt = fallback.UpdatedAt;
        if (primary.UnityPackages.Count == 0) primary.UnityPackages = fallback.UnityPackages;
        if (primary.Platform.Count == 0) primary.Platform = fallback.Platform;
        if (primary.Tags.Count == 0) primary.Tags = fallback.Tags;
        if (string.IsNullOrWhiteSpace(primary.SourceName)) primary.SourceName = fallback.SourceName;
    }

    private async Task OpenAvatarAsync(FriendAvatar? item)
    {
        if (item is null || !item.CanOpen)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.AvatarId))
        {
            // The instance/notification row sometimes carries only the picture.
            // Ask for the live user record before opening, so the popup gets the
            // real avatar id, name and public/private state instead of a blank
            // placeholder. This is also the non-friend path, where /profile can
            // be limited.
            try
            {
                var live = await _api.GetUserAsync(_userId, forceRefresh: true);
                if (!string.IsNullOrWhiteSpace(live?.CurrentAvatar))
                {
                    RememberCurrentAvatar(live, live.CurrentAvatar, live.CurrentAvatarName);
                    item = Avatars.FirstOrDefault(candidate => candidate.IsCurrent) ?? item;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"avatar user lookup {_userId}: {ex.Message}");
            }
        }

        await ResolveAvatarAsync(item, CancellationToken.None);
        var isWearing = string.Equals(
            _api.CurrentUser?.CurrentAvatar,
            item.AvatarId,
            StringComparison.Ordinal);
        var avatar = item.ToAvatar(isWearing);
        CompletePrivateOwner(avatar, item);
        OpenAvatar?.Invoke(avatar);
    }

    /// <summary>
    /// Private avatars are owned by the user whose profile they appear on, so
    /// use that user as the author when VRChat withholds the private record.
    /// The current-user check still decides whether the Wear button is enabled.
    /// </summary>
    private void CompletePrivateOwner(Avatar avatar, FriendAvatar item)
    {
        if (!avatar.IsPrivate && !avatar.IsUnavailable)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(avatar.AuthorId))
        {
            avatar.AuthorId = _userId;
        }

        if (string.IsNullOrWhiteSpace(avatar.AuthorName))
        {
            avatar.AuthorName = !string.IsNullOrWhiteSpace(_profile?.CurrentAvatarAuthorName) &&
                               string.Equals(_profile.CurrentAvatar, item.AvatarId, StringComparison.Ordinal)
                ? _profile.CurrentAvatarAuthorName
                : IsSelf
                    ? _api.CurrentUser?.DisplayName ?? "you"
                    : _friend.User.DisplayName;
        }
    }

    private void RaiseAvatarsChanged()
    {
        RebuildVisibleAvatars();
        OnPropertyChanged(nameof(HasAvatars));
        OnPropertyChanged(nameof(HasIndexedAvatars));
        OnPropertyChanged(nameof(ShowNoAvatarsMessage));
        OnPropertyChanged(nameof(AvatarsSummary));
        OnPropertyChanged(nameof(CanToggleAvatars));
        OnPropertyChanged(nameof(AvatarsToggleText));
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

    /// <summary>Whether VRChat currently reports this user as one of your friends.</summary>
    public bool IsFriend => !IsSelf && (_friendStatus?.IsFriend ?? _friend.IsFriend);

    public bool HasOutgoingFriendRequest => !IsSelf && _friendStatus?.OutgoingRequest == true;

    public bool HasIncomingFriendRequest => !IsSelf && _friendStatus?.IncomingRequest == true;

    public bool CanToggleFriendship => !IsSelf;

    /// <summary>Whether the compact friend-actions menu is open on the profile.</summary>
    public bool IsFriendMenuOpen
    {
        get => _isFriendMenuOpen;
        set => SetProperty(ref _isFriendMenuOpen, value);
    }

    public RelayCommand ToggleFriendMenuCommand { get; }

    /// <summary>The main shell's add/remove command, exposed for the popup menu.</summary>
    public ICommand? ToggleFriendshipCommand { get; set; }

    /// <summary>Opens the signed-in account's local instance history.</summary>
    public ICommand? ShowInstanceHistoryCommand { get; set; }

    public string FriendshipLabel => IsSelf
        ? string.Empty
        : IsFriend
            ? "Friend"
            : HasIncomingFriendRequest
                ? "Friend request received"
                : HasOutgoingFriendRequest
                    ? "Friend request sent"
                    : "Not friends";

    public string FriendActionLabel => IsSelf
        ? string.Empty
        : IsFriend
            ? "Remove friend"
            : HasIncomingFriendRequest
                ? "Accept friend"
                : HasOutgoingFriendRequest
                    ? "Cancel request"
                    : "Add friend";

    /// <summary>Applies the authoritative relationship flags from VRChat.</summary>
    public void ApplyFriendStatus(VrcFriendStatus? status)
    {
        _friendStatus = status;
        RaiseFriendshipChanged();
    }

    /// <summary>Updates the card immediately after an add/remove action succeeds.</summary>
    public void ApplyFriendshipState(bool isFriend, bool outgoingRequest, bool incomingRequest)
    {
        _friendStatus = new VrcFriendStatus
        {
            IsFriend = isFriend,
            OutgoingRequest = outgoingRequest,
            IncomingRequest = incomingRequest
        };
        RaiseFriendshipChanged();
    }

    private void RaiseFriendshipChanged()
    {
        OnPropertyChanged(nameof(IsFriend));
        OnPropertyChanged(nameof(HasOutgoingFriendRequest));
        OnPropertyChanged(nameof(HasIncomingFriendRequest));
        OnPropertyChanged(nameof(CanToggleFriendship));
        OnPropertyChanged(nameof(ShowBoopAction));
        OnPropertyChanged(nameof(CanBoop));
        OnPropertyChanged(nameof(FriendshipLabel));
        OnPropertyChanged(nameof(FriendActionLabel));
        ToggleBoopPickerCommand.RaiseCanExecuteChanged();
        SendBoopCommand.RaiseCanExecuteChanged();
        SendPlainBoopCommand.RaiseCanExecuteChanged();
    }

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
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(StatusDotBrush));
    }

    /// <summary>
    /// Keeps the real instance location authoritative while this player is
    /// still in the instance that opened the profile. Friend-list refreshes can
    /// report a stale "private", "offline", or older world for an in-game
    /// friend; VRCX does not let those replace the live instance.
    /// </summary>
    public void PinToInstance(string location, Func<bool>? isStillInInstance = null)
    {
        if (string.IsNullOrWhiteSpace(location) || !LaunchService.IsLocation(location))
        {
            return;
        }

        _pinnedLocation = location;
        _pinnedLocationIsCurrent = isStillInInstance;
        _pinnedStatus = IsOfflineStatus(_presenceStatus) ? "active" : _presenceStatus;
        OnAllPropertiesChanged();
    }

    public void ClearPinnedLocation()
    {
        if (_pinnedLocation is null)
        {
            return;
        }

        _pinnedLocation = null;
        _pinnedStatus = null;
        _pinnedLocationIsCurrent = null;
        OnAllPropertiesChanged();
    }

    private bool IsPinned => _pinnedLocation is not null &&
                             (_pinnedLocationIsCurrent?.Invoke() ?? true);

    private string EffectiveLocation => IsPinned ? _pinnedLocation! : _friend.Location;

    private static bool IsOfflineStatus(string? status)
        => string.IsNullOrWhiteSpace(status) ||
           string.Equals(status, "offline", StringComparison.OrdinalIgnoreCase);

    /// <summary>Replaces the friend payload with fresher data (e.g. a live location).</summary>
    public void ApplyUser(VrcUser user)
    {
        if (!string.Equals(user.Id, _userId, StringComparison.Ordinal))
        {
            return;
        }

        // Capture the avatar even when the rest of this payload is a stale
        // offline response that presence logic will reject below.
        RememberCurrentAvatar(user, user.CurrentAvatar, user.CurrentAvatarName);
        if (!string.IsNullOrWhiteSpace(user.CurrentAvatar) ||
            !string.IsNullOrWhiteSpace(user.CurrentAvatarImageUrl) ||
            !string.IsNullOrWhiteSpace(user.CurrentAvatarThumbnailImageUrl))
        {
            _ = LoadAvatarDetailsAsync(CancellationToken.None);
        }

        var incomingStatus = UserPresence.Normalize(user.Status);
        var incomingOffline = IsOfflineStatus(incomingStatus) ||
                              string.IsNullOrWhiteSpace(user.Location) ||
                              string.Equals(user.Location, "offline", StringComparison.OrdinalIgnoreCase);
        if (incomingOffline && _friend.IsPresent && _friend.IsInOnlineFriends)
        {
            // VRChat's user endpoint can lag behind the live friend buckets for
            // an in-game friend. Do not let that stale response turn the popup
            // Offline after it was opened from a blue friend row.
            return;
        }

        var status = incomingStatus;
        if (!string.IsNullOrWhiteSpace(status) &&
            (!IsPinned || !IsOfflineStatus(status)) &&
            (!IsOfflineStatus(status) || !_friend.IsPresent))
        {
            _presenceStatus = status;
        }

        var previousLocation = _friend.Location;
        _friend.Update(user);
        if (!IsPinned && !string.Equals(previousLocation, user.Location, StringComparison.Ordinal))
        {
            _worldName = null;
            OnPropertyChanged(nameof(WorldName));
            OnPropertyChanged(nameof(HasWorldName));
            OnPropertyChanged(nameof(WorldLine));
            _ = LoadExtrasAsync(CancellationToken.None);
        }

        OnPropertyChanged(nameof(IsFriend));
        OnPropertyChanged(nameof(ShowBoopAction));
        OnPropertyChanged(nameof(CanBoop));
        OnPropertyChanged(nameof(FriendshipLabel));
        OnPropertyChanged(nameof(FriendActionLabel));
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(JoinLocation));
        OnPropertyChanged(nameof(WorldLine));
        OnPropertyChanged(nameof(WorldId));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(StatusDotBrush));
        OnPropertyChanged(nameof(ActivityText));
        OnPropertyChanged(nameof(MemberSince));
        OnPropertyChanged(nameof(HasMemberSince));
        OnPropertyChanged(nameof(MemberSinceText));
        OnPropertyChanged(nameof(MemberSinceDetailText));
    }

    public VrcProfile? Profile
    {
        get => _profile;
        internal set
        {
            if (value is not null &&
                !string.IsNullOrWhiteSpace(value.Id) &&
                !string.Equals(value.Id, _userId, StringComparison.Ordinal))
            {
                return;
            }

            if (SetProperty(ref _profile, value))
            {
                if (value is not null)
                {
                    // Profile data is the authoritative source for a non-friend's
                    // current-avatar author. Even when the visible hints did not
                    // change, a new profile response gets one fresh index attempt
                    // instead of inheriting an early empty result.
                    _indexedAvatarsLoaded = false;
                    if (_isLoadingIndexedAvatars)
                    {
                        _indexedAvatarLookupAgain = true;
                    }

                    RememberProfileAvatar(value);
                    _ = LoadAvatarDetailsAsync(CancellationToken.None);
                }

                if (value is not null && !string.IsNullOrWhiteSpace(value.Status))
                {
                    var status = UserPresence.Normalize(value.Status);
                    if ((!IsPinned || !IsOfflineStatus(status)) &&
                        (!IsOfflineStatus(status) || !_friend.IsPresent))
                    {
                        _presenceStatus = status;
                    }
                }
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

    public string Id => _userId;

    public string DisplayName => string.IsNullOrWhiteSpace(Profile?.DisplayName)
        ? _friend.DisplayName
        : Profile!.DisplayName;

    public string UsernameHandle => string.IsNullOrWhiteSpace(_friend.Username)
        ? string.Empty
        : $"@{_friend.Username}";

    public string Initial => _friend.Initial;

    public bool IsOnline => IsSelf
        ? SelfPresenceStatus != "offline" && SelfPresenceStatus.Length > 0
        : ProfilePresenceOnline;

    /// <summary>Active on the web/mobile client rather than in an instance.</summary>
    public bool IsActive => !IsSelf && Presence == UserPresenceState.Active;

    public string StatusLabel => IsSelf
        ? SelfStatusLabel
        : UserPresence.Label(PresenceStatus, Presence);

    public Brush StatusBrush => IsSelf
        ? SelfStatusBrush
        : UserPresence.Brush(PresenceStatus, Presence);

    public Brush StatusDotBrush => IsSelf
        ? SelfStatusBrush
        : UserPresence.DotBrush(PresenceStatus, Presence);

    private string SelfStatusLabel => SelfPresenceStatus switch
    {
        "active" => "Online",
        "join me" => "Join Me",
        "ask me" => "Ask Me",
        "busy" => "Busy",
        _ => "Offline"
    };

    private Brush SelfStatusBrush => SelfPresenceStatus switch
    {
        "active" => SelfOnlineBrush,
        "join me" => SelfJoinMeBrush,
        "ask me" => SelfAskMeBrush,
        "busy" => SelfBusyBrush,
        _ => SelfOfflineBrush
    };

    /// <summary>
    /// The profile payload is the stable presence source once it arrives. The
    /// mutable friend-list copy only seeds the card, so location or background
    /// refreshes cannot make an open profile jump between Online and Ask Me. A
    /// later API profile load replaces this snapshot explicitly.
    /// </summary>
    private string PresenceStatus => IsSelf
        ? SelfPresenceStatus
        : IsPinned && IsOfflineStatus(_presenceStatus)
            ? _pinnedStatus ?? "active"
            : _presenceStatus;

    private UserPresenceState Presence => IsSelf
        ? UserPresence.Classify(null, SelfPresenceStatus, forceOnline: true)
        : IsPinned
            ? UserPresenceState.Online
        : UserPresence.Classify(
            _friend.Location,
            PresenceStatus,
            _friend.User.State,
            _friend.User.Platform ?? _friend.User.LastPlatform,
            inOnlineFriends: _friend.IsInOnlineFriends,
            inActiveFriends: _friend.IsInActiveFriends);

    private bool ProfilePresenceOnline
    {
        get
        {
            if (IsPinned)
            {
                return true;
            }

            // Profile status describes the selected presence mode, not whether
            // the person is connected. The API state/location decides that; an
            // active profile must not resurrect an offline friend.
            return Presence == UserPresenceState.Online;
        }
    }

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

    /// <summary>
    /// When this account joined VRChat. Every user record carries date_joined -
    /// friends from the friend list, strangers from the /users/{id} record that
    /// fills their card, and the signed-in account - so one card serves them all.
    /// The /profile payload has no join date, so it is not read here.
    /// </summary>
    public DateTimeOffset? MemberSince => ParseDateJoined(_friend.User.DateJoined);

    public bool HasMemberSince => MemberSince is not null;

    public string MemberSinceText => MemberSince is { } joined
        ? $"Member since {joined.ToLocalTime().ToString("MMM yyyy", CultureInfo.CurrentCulture)}"
        : string.Empty;

    /// <summary>The exact day, shown as the card's tooltip.</summary>
    public string MemberSinceDetailText => MemberSince is { } joined
        ? $"Joined VRChat on {joined.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}"
        : string.Empty;

    private static DateTimeOffset? ParseDateJoined(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var joined)
            ? joined
            : null;
    }

    public string TrustLabel => UserPresentation.TrustLabel(
        Profile?.TrustTags is { Count: > 0 } ? Profile.TrustTags : _friend.Tags,
        _friend.User.DeveloperType);

    public Brush TrustBrush => UserPresentation.TrustBrush(
        Profile?.TrustTags is { Count: > 0 } ? Profile.TrustTags : _friend.Tags,
        _friend.User.DeveloperType);

    private VrcUser CosmeticUser => IsSelf ? _api.CurrentUser ?? _friend.User : _friend.User;

    public string IconFrameUrl => UserPresentation.IconFrameUrl(
        Profile?.IconFrame ?? CosmeticUser.IconFrame) ?? string.Empty;

    public string IconFrameIntroUrl => UserPresentation.IconFrameIntroUrl(
        Profile?.IconFrame ?? CosmeticUser.IconFrame) ?? string.Empty;

    public bool HasIconFrame => IconFrameUrl.Length > 0;

    public string ProfileEffectUrl => UserPresentation.ProfileEffectUrl(
        Profile?.ProfileEffect ?? CosmeticUser.ProfileEffect) ?? string.Empty;

    public string ProfileEffectIntroUrl => UserPresentation.ProfileEffectIntroUrl(
        Profile?.ProfileEffect ?? CosmeticUser.ProfileEffect) ?? string.Empty;

    public bool HasProfileEffect => ProfileEffectUrl.Length > 0;

    public string NameplateEffectUrl => UserPresentation.NameplateEffectUrl(
        Profile?.NameplateEffect ?? CosmeticUser.NameplateEffect) ?? string.Empty;

    public string NameplateEffectIntroUrl => UserPresentation.NameplateEffectIntroUrl(
        Profile?.NameplateEffect ?? CosmeticUser.NameplateEffect) ?? string.Empty;

    public bool HasNameplateEffect => NameplateEffectUrl.Length > 0;

    public Brush? NameplateBrush => UserPresentation.NameplateGradient(
        Profile?.NameplateEffect ?? CosmeticUser.NameplateEffect);

    public Brush ProfileNameBrush => Theme.Icon;

    public Brush ProfileButtonBrush => Theme.Button;

    public Brush ProfileButtonTextBrush => UserPresentation.ButtonText(Theme.Button);

    public Brush ProfileSubtextBrush => Theme.Subtext;

    private ProfileThemeColors Theme => UserPresentation.Theme(
        Profile?.ThemeIconColor ?? CosmeticUser.ThemeIconColor,
        Profile?.ThemeButtonColor ?? CosmeticUser.ThemeButtonColor,
        Profile?.ThemeSubtextColor ?? CosmeticUser.ThemeSubtextColor);

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
    /// <summary>
    /// The represented group is displayed from its own payload object, not the
    /// full group list, so it needs the same Joined/Mutual pass separately.
    /// </summary>
    internal void AnnotateRepresentedGroup(IReadOnlyList<Group> mine)
    {
        if (Profile?.RepresentedGroup is not { } represented)
        {
            return;
        }

        represented.IsMine = mine.Any(group =>
            string.Equals(group.EffectiveId, represented.Id, StringComparison.Ordinal));
        represented.IsMutual = !IsSelf && represented.IsMine;
    }

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

    /// <summary>The world's own picture, so the profile shows the place and not just its name.</summary>
    public string WorldImageUrl => _worldImageUrl ?? string.Empty;

    public bool HasWorldImage => !string.IsNullOrWhiteSpace(_worldImageUrl);

    /// <summary>True when the profile points at a real world the details dialog can open.</summary>
    public bool HasWorldId => WorldId is not null;

    public string WorldLine
    {
        get
        {
            if (IsSelf)
            {
                return "You are signed in through KawaiiVrc; status and platform come from your account.";
            }

            if (IsPinned)
            {
                var location = EffectiveLocation;
                if (!string.IsNullOrWhiteSpace(_worldName))
                {
                    return $"{_worldName} · {PinnedAccessLabel(location)}";
                }

                return string.Equals(location, "private", StringComparison.OrdinalIgnoreCase)
                    ? "In a private world"
                    : location.StartsWith("wrld_", StringComparison.OrdinalIgnoreCase)
                        ? $"In a world · {PinnedAccessLabel(location)}"
                        : "Online";
            }

            if (!string.IsNullOrWhiteSpace(_worldName))
            {
                return $"{_worldName} · {_friend.LocationAccessLabel}";
            }

            return _friend.LocationText;
        }
    }

    public string? WorldId => LaunchService.GetWorldIdFromLocation(EffectiveLocation);

    public string? WorldUrl => WorldId is null ? null : $"https://vrchat.com/home/world/{WorldId}";

    // A real world/instance location already means they are in game, which also
    // covers senders who are not on the friends list (no status payload at all).
    public bool CanJoin => LaunchService.IsLocation(EffectiveLocation);

    public string JoinLocation => LaunchService.IsLocation(EffectiveLocation) ? EffectiveLocation : string.Empty;

    public bool CanRequestInvite => IsPinned
        ? string.Equals(EffectiveLocation, "private", StringComparison.OrdinalIgnoreCase) ||
          EffectiveLocation.Contains("~private(", StringComparison.OrdinalIgnoreCase)
        : _friend.IsOnline && _friend.IsInPrivateOrInviteWorld;

    private static string PinnedAccessLabel(string location)
    {
        if (location.Contains("~group(", StringComparison.OrdinalIgnoreCase)) return "Group";
        if (location.Contains("~private(", StringComparison.OrdinalIgnoreCase)) return "Invite";
        if (location.Contains("~friends(", StringComparison.OrdinalIgnoreCase)) return "Friends";
        if (location.Contains("~hidden(", StringComparison.OrdinalIgnoreCase)) return "Friends+";
        return "Public";
    }

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
        _ = LoadFriendStatusAsync(cancellationToken);
        _ = LoadCosmeticsAsync(cancellationToken);
        _ = LoadLiveUserAsync(cancellationToken);
        _ = LoadAvatarDetailsAsync(cancellationToken);

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

            if (profile is not null && !string.Equals(profile.Id, _userId, StringComparison.Ordinal))
            {
                ErrorMessage = "VRChat returned a different profile; the current profile was not replaced.";
                return;
            }

            Profile = profile;
            _ = LoadAvatarDetailsAsync(cancellationToken);
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

            // /profile carries display data but not a reliable live presence.
            // The user record was already requested alongside the profile, so
            // its avatar/live-presence result can arrive independently.
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

    /// <summary>
    /// Cosmetics are ids on the user/profile; resolve them once through the
    /// shared index, then repaint the popup when the artwork is available.
    /// </summary>
    private async Task LoadCosmeticsAsync(CancellationToken cancellationToken)
    {
        if (CosmeticCatalog.IsLoaded)
        {
            return;
        }

        try
        {
            await CosmeticCatalog.EnsureLoadedAsync(_api, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                OnAllPropertiesChanged();
            }
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

    public void RefreshCosmetics() => OnAllPropertiesChanged();

    private async Task LoadLiveUserAsync(CancellationToken cancellationToken)
    {
        try
        {
            var live = await _api.GetUserAsync(Id, forceRefresh: true, cancellationToken);
            if (!cancellationToken.IsCancellationRequested &&
                live is not null &&
                string.Equals(live.Id, Id, StringComparison.Ordinal))
            {
                ApplyUser(live);
                EnsureCurrentAvatarPlaceholder();
            }
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
    /// VRC+ accounts can attach their uploaded emoji or an owned emoji item to
    /// a boop. These are optional: a failed request leaves the default catalog
    /// usable instead of surfacing an error on the profile.
    /// </summary>
    private async Task LoadBoopChoicesAsync(CancellationToken cancellationToken)
    {
        if (_boopChoicesLoaded ||
            IsSelf ||
            _api.CurrentUser?.Tags?.Any(tag =>
                string.Equals(tag, "system_supporter", StringComparison.OrdinalIgnoreCase)) != true)
        {
            return;
        }

        _boopChoicesLoaded = true;
        try
        {
            var files = await _api.GetBoopEmojiFilesAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                foreach (var file in files)
                {
                    if (string.IsNullOrWhiteSpace(file.Id) ||
                        string.IsNullOrWhiteSpace(file.ImageUrl))
                    {
                        continue;
                    }

                    BoopEmojis.Add(new BoopEmojiOption(
                        file.Id,
                        file.FriendlyName,
                        file.ImageUrl));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"boop emoji files {_userId}: {ex.Message}");
        }

        try
        {
            var inventory = await _api.GetBoopInventoryEmojisAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                foreach (var item in inventory)
                {
                    if (string.IsNullOrWhiteSpace(item.Id) ||
                        string.IsNullOrWhiteSpace(item.DisplayImageUrl) ||
                        string.IsNullOrWhiteSpace(item.Metadata?.FileId))
                    {
                        continue;
                    }

                    BoopEmojis.Add(new BoopEmojiOption(
                        item.Id,
                        item.FriendlyName,
                        item.DisplayImageUrl,
                        inventoryItemId: item.Id));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Profile closed.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"boop inventory emoji {_userId}: {ex.Message}");
        }
    }

    private async Task LoadFriendStatusAsync(CancellationToken cancellationToken)
    {
        if (IsSelf)
        {
            return;
        }

        try
        {
            var status = await _api.GetFriendStatusAsync(Id, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                ApplyFriendStatus(status);
            }
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

    public void UpdateFriend(FriendItem friend)
    {
        if (!string.Equals(friend.Id, _userId, StringComparison.Ordinal))
        {
            // VRCX never lets a background refresh replace the dialog's
            // identity. A different row belongs to a different popup.
            return;
        }

        var locationChanged = !string.Equals(_friend.Location, friend.Location, StringComparison.Ordinal);
        _friend = friend;
        RememberCurrentAvatar(friend.User, friend.User.CurrentAvatar, friend.User.CurrentAvatarName);
        if (friend.User is not null &&
            (!string.IsNullOrWhiteSpace(friend.User.CurrentAvatarImageUrl) ||
             !string.IsNullOrWhiteSpace(friend.User.CurrentAvatarThumbnailImageUrl)))
        {
            _ = LoadAvatarDetailsAsync(CancellationToken.None);
        }
        if (IsPinned)
        {
            // The live instance is still authoritative for this profile. Keep
            // its world name while the stale friend record is replaced.
        }
        else if (locationChanged)
        {
            _worldName = friend.HasWorldName ? friend.WorldName : null;
            _worldImageUrl = null;
            _ = LoadWorldInfoAsync(CancellationToken.None);
        }
        else if (friend.HasWorldName)
        {
            _worldName = friend.WorldName;
        }

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
                profile.IconUrl,
                profile.CurrentAvatarThumbnailImageUrl,
                profile.CurrentAvatarImageUrl,
                256);
        }

        if (string.IsNullOrWhiteSpace(avatarUrl))
        {
            avatarUrl = _friend.AvatarUrl;
        }

        VrcUser? userFallback = null;
        if (string.IsNullOrWhiteSpace(avatarUrl))
        {
            try
            {
                userFallback = await _api.GetUserAsync(Id, forceRefresh: true, cancellationToken);
                if (userFallback is not null)
                {
                    avatarUrl = ImageUrlHelper.PickAvatarThumbnailUrl(
                        userFallback.UserIcon,
                        userFallback.ProfilePicOverrideThumbnail ?? userFallback.IconUrl,
                        userFallback.CurrentAvatarThumbnailImageUrl,
                        userFallback.CurrentAvatarImageUrl,
                        256);
                }
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

        var bannerUrl = ImageUrlHelper.ToThumbnailUrl(
            profile?.BannerUrl ?? _friend.BannerUrl ?? userFallback?.BannerUrl,
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
            AnnotateRepresentedGroup(mine);
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
        await LoadWorldInfoAsync(cancellationToken);

        try
        {
            var mutuals = await _api.GetMutualsAsync(Id, cancellationToken: cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _mutuals = mutuals;
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

    /// <summary>
    /// Reads the world the profile points at: its name and picture. The details button
    /// then opens that exact instance, not just the world in general.
    /// </summary>
    private async Task LoadWorldInfoAsync(CancellationToken cancellationToken)
    {
        var worldId = WorldId;
        if (worldId is null)
        {
            _worldImageUrl = null;
            OnPropertyChanged(nameof(WorldImageUrl));
            OnPropertyChanged(nameof(HasWorldImage));
            OnPropertyChanged(nameof(HasWorldId));
            return;
        }

        try
        {
            var world = await _api.GetWorldAsync(worldId, cancellationToken: cancellationToken);
            if (cancellationToken.IsCancellationRequested ||
                !string.Equals(WorldId, worldId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(world?.Name))
            {
                _worldName = world!.Name;
            }

            _worldImageUrl = ImageUrlHelper.ToThumbnailUrl(
                string.IsNullOrWhiteSpace(world?.ImageUrl) ? world?.ThumbnailImageUrl : world!.ImageUrl,
                132);

            OnPropertyChanged(nameof(WorldName));
            OnPropertyChanged(nameof(HasWorldName));
            OnPropertyChanged(nameof(WorldLine));
            OnPropertyChanged(nameof(WorldImageUrl));
            OnPropertyChanged(nameof(HasWorldImage));
            OnPropertyChanged(nameof(HasWorldId));
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

    private static Brush MakeBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
