using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// View model for the activity feed / timeline. Friend data comes from the
/// signed-in <see cref="VrcApiClient"/>, so the feed shares the session.
/// </summary>
public sealed class ActivityFeedViewModel : ViewModelBase
{
    private readonly ActivityFeedService _feedService;
    private readonly ActivityStore _store;
    private readonly FriendAvatarStore _friendAvatars;
    private readonly VrcApiClient _api;
    private Task _avatarHydration = Task.CompletedTask;
    private string? _accountId;

    private ObservableCollection<ActivityEntry> _entries = new();
    private List<ActivityEntry> _allEntries = new();
    private IReadOnlyList<VrcUser>? _previousFriends;
    private ActivityType? _selectedFilter;
    private ActivityFilterOption? _selectedFilterOption;
    private string _searchQuery = string.Empty;
    private ActivityEntry? _searchPerson;
    private List<ActivityEntry> _filteredEntries = new();
    private int _pageIndex;
    private string _pageKey = string.Empty;
    private int _pageToken;
    private bool _showOfflineFriends = true;
    private bool _isBusy;
    private bool _hasLoaded;
    private bool _suppressFilterChanged;
    private string? _errorMessage;
    private int _checkedFriendCount;
    private readonly HashSet<string> _profileAvatarLookups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedAvatarLookups = new(StringComparer.Ordinal);
    // Avatar ids whose name the API has already answered for this session, and
    // avatar images the community index already failed to match. Both stop a
    // long timeline from asking the same question on every refresh.
    private readonly Dictionary<string, string?> _resolvedAvatarNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unresolvedAvatarImages = new(StringComparer.Ordinal);
    private const int MaxAvatarNameLookupsPerPass = 24;
    private const int MaxUnresolvedAvatarImages = 4_000;
    private Dictionary<string, BioBaseline> _bioBaselines = new(StringComparer.Ordinal);
    private VrcUser? _previousSelfUser;

    private readonly List<IRelayCommand> _commands = new();

    private sealed record ProfileAvatarCandidate(
        VrcUser Friend,
        string ImageUrl,
        string FileId,
        string? PreviousFileId);

    /// <summary>
    /// The main view model owns the one full friend-list fetch. The feed asks
    /// for that load instead of fetching the same list itself.
    /// </summary>
    public Func<Task>? RefreshRequested { get; set; }

    /// <summary>Raised after persisted history for an account has been loaded.</summary>
    public event Action<IReadOnlyList<ActivityEntry>>? HistoryLoaded;

    /// <summary>Raised when the user chooses a different activity filter.</summary>
    public event Action<string>? FilterChanged;

    public ActivityFeedViewModel(
        VrcApiClient api,
        ActivityStore? store = null,
        FriendAvatarStore? friendAvatars = null)
    {
        _feedService = new ActivityFeedService();
        _store = store ?? ActivityStore.Shared;
        _friendAvatars = friendAvatars ?? FriendAvatarStore.Shared;
        _api = api;

        Filters = new[]
        {
            new ActivityFilterOption("All", null),
            new ActivityFilterOption("Online", ActivityType.FriendOnline),
            new ActivityFilterOption("Joined world", ActivityType.FriendJoinedWorld),
            new ActivityFilterOption("Offline", ActivityType.FriendOffline),
            new ActivityFilterOption("Bio changed", ActivityType.BioChanged),
            new ActivityFilterOption("Avatar changed", ActivityType.AvatarChanged),
            new ActivityFilterOption("Friended", ActivityType.FriendAdded),
            new ActivityFilterOption("Unfriended", ActivityType.FriendRemoved)
        };
        _selectedFilterOption = Filters[0];

        RefreshCommand = Track(new AsyncRelayCommand(RefreshAsync, () => !IsBusy));
        ClearSearchCommand = Track(new RelayCommand(ClearSearch));
        PreviousPageCommand = Track(new RelayCommand(PreviousPage, () => HasPreviousPage));
        NextPageCommand = Track(new RelayCommand(NextPage, () => HasNextPage));
    }

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

    public ObservableCollection<ActivityEntry> Entries
    {
        get => _entries;
        set
        {
            if (SetProperty(ref _entries, value))
            {
                OnPropertyChanged(nameof(HasNoEntries));
            }
        }
    }

    public bool HasNoEntries => Entries.Count == 0;

    /// <summary>
    /// The one person a search resolved to, so the search can show who is being
    /// looked at and open their profile even when they have no activity yet.
    /// </summary>
    public ActivityEntry? SearchPerson
    {
        get => _searchPerson;
        private set
        {
            if (SetProperty(ref _searchPerson, value))
            {
                OnPropertyChanged(nameof(HasSearchPerson));
                OnPropertyChanged(nameof(SearchPersonMeta));
            }
        }
    }

    public bool HasSearchPerson => SearchPerson is not null;

    public string SearchPersonMeta => SearchPerson is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(SearchPerson.UserName)
            ? SearchPerson.UserId
            : $"@{SearchPerson.UserName} · {SearchPerson.UserId}";

    public string NoEntriesText => string.IsNullOrWhiteSpace(SearchQuery)
        ? "No activity yet. Friend presence and profile changes will show up here."
        : $"No activity matches “{SearchQuery.Trim()}” yet.";

    /// <summary>Rows per page, so the timeline never becomes one endless scroll.</summary>
    public const int PageSize = 25;

    /// <summary>Bumped on every page change; the view scrolls to the top on it.</summary>
    public int PageToken => _pageToken;

    public int PageCount => _filteredEntries.Count == 0
        ? 0
        : (_filteredEntries.Count + PageSize - 1) / PageSize;

    public int CurrentPage => PageCount == 0 ? 0 : _pageIndex + 1;

    public string PageText => PageCount == 0
        ? string.Empty
        : $"Page {CurrentPage} of {PageCount}";

    public string PageSummaryText
    {
        get
        {
            if (_filteredEntries.Count == 0)
            {
                return string.Empty;
            }

            var first = (_pageIndex * PageSize) + 1;
            var last = Math.Min(first + PageSize - 1, _filteredEntries.Count);
            return $"Showing {first:N0}–{last:N0} of {_filteredEntries.Count:N0}";
        }
    }

    public bool HasPreviousPage => _pageIndex > 0;

    public bool HasNextPage => PageCount > 0 && _pageIndex < PageCount - 1;

    public bool HasPages => PageCount > 1;

    public ActivityType? SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                SyncSelectedFilterOption();
                ApplyFilterAndSearch();
            }
        }
    }

    public IReadOnlyList<ActivityFilterOption> Filters { get; }

    public ActivityFilterOption? SelectedFilterOption
    {
        get => _selectedFilterOption;
        set
        {
            var option = value ?? Filters[0];
            if (SetProperty(ref _selectedFilterOption, option))
            {
                if (_selectedFilter != option.Type)
                {
                    _selectedFilter = option.Type;
                    OnPropertyChanged(nameof(SelectedFilter));
                }

                ApplyFilterAndSearch();
                if (!_suppressFilterChanged)
                {
                    FilterChanged?.Invoke(option.Type?.ToString() ?? "all");
                }
            }
        }
    }

    public bool ShowOfflineFriends
    {
        get => _showOfflineFriends;
        set
        {
            if (SetProperty(ref _showOfflineFriends, value))
            {
                ApplyFilterAndSearch();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ApplyFilterAndSearch();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>True once the feed has loaded, so navigation does not refetch.</summary>
    public bool HasLoaded
    {
        get => _hasLoaded;
        private set => SetProperty(ref _hasLoaded, value);
    }

    public int CheckedFriendCount
    {
        get => _checkedFriendCount;
        private set
        {
            if (SetProperty(ref _checkedFriendCount, value))
            {
                OnPropertyChanged(nameof(CheckedFriendsText));
            }
        }
    }

    public string CheckedFriendsText => CheckedFriendCount == 0
        ? string.Empty
        : $"Checked {CheckedFriendCount:N0} friends · live VRChat log watched";

    /// <summary>
    /// Points the feed at one account's persisted timeline. Loading is
    /// idempotent, so normal refreshes never reset the current entries.
    /// </summary>
    public void SetAccount(string? accountId)
    {
        if (string.Equals(_accountId, accountId, StringComparison.Ordinal))
        {
            return;
        }

        _accountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId;
        CheckedFriendCount = 0;
        _profileAvatarLookups.Clear();
        _loggedAvatarLookups.Clear();
        _resolvedAvatarNames.Clear();
        _unresolvedAvatarImages.Clear();
        _previousFriends = null;
        _previousSelfUser = null;
        _bioBaselines = _accountId is null
            ? new Dictionary<string, BioBaseline>(StringComparer.Ordinal)
            : new Dictionary<string, BioBaseline>(
                _store.LoadBioBaselines(_accountId),
                StringComparer.Ordinal);
        _allEntries = _accountId is null
            ? new List<ActivityEntry>()
            : _store.Load(_accountId).ToList();
        MarkSelfEntries(_allEntries);
        ApplyFilterAndSearch();
        HasLoaded = _allEntries.Count > 0;
        ErrorMessage = null;
        HistoryLoaded?.Invoke(_allEntries);
        if (_allEntries.Count > 0)
        {
            QueueAvatarHydration();
        }
    }

    /// <summary>Clears the feed so a different account builds its own.</summary>
    public void Reset()
    {
        _accountId = null;
        CheckedFriendCount = 0;
        _profileAvatarLookups.Clear();
        _loggedAvatarLookups.Clear();
        _resolvedAvatarNames.Clear();
        _unresolvedAvatarImages.Clear();
        _bioBaselines = new Dictionary<string, BioBaseline>(StringComparer.Ordinal);
        _allEntries = new List<ActivityEntry>();
        _previousFriends = null;
        _previousSelfUser = null;
        _searchQuery = string.Empty;
        OnPropertyChanged(nameof(SearchQuery));
        _suppressFilterChanged = true;
        try
        {
            SelectedFilter = null;
            SelectedFilterOption = Filters[0];
        }
        finally
        {
            _suppressFilterChanged = false;
        }
        ShowOfflineFriends = true;
        _filteredEntries = new List<ActivityEntry>();
        _pageIndex = 0;
        _pageKey = string.Empty;
        Entries = new ObservableCollection<ActivityEntry>();
        SearchPerson = null;
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(PageSummaryText));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(HasPages));
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        HasLoaded = false;
        ErrorMessage = null;
    }

    public ICommand RefreshCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public RelayCommand PreviousPageCommand { get; }
    public RelayCommand NextPageCommand { get; }

    public async Task LoadAsync()
    {
        if (IsBusy || RefreshRequested is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await RefreshRequested();
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat. Check your internet connection.";
        }
        catch (TaskCanceledException)
        {
            ErrorMessage = "The activity request timed out. Try again.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not load activity.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task RefreshAsync() => LoadAsync();

    /// <summary>
    /// Rebuilds the feed from a complete friend list already fetched by the
    /// main view model, so the two views share one snapshot instead of
    /// fetching and diffing the list independently.
    /// </summary>
    public void ApplyFriends(IReadOnlyList<VrcUser> friends)
    {
        // VRChat's current friend API often keeps currentAvatar unchanged while
        // the profile avatar image changes. Capture those image changes before
        // the snapshot advances, then resolve them the way VRCX does.
        var profileChanges = FindProfileAvatarChanges(friends, _previousFriends);
        var bioEntries = ObserveFriendBios(friends, DateTimeOffset.UtcNow);
        CheckedFriendCount = friends.Count;
        SetFeed(friends);
        if (bioEntries.Count > 0)
        {
            _allEntries = MergeEntries(_allEntries, bioEntries);
            ApplyFilterAndSearch();
            HasLoaded = true;
            SaveHistory();
        }

        QueueProfileAvatarChanges(profileChanges);
    }

    /// <summary>
    /// Applies a single realtime friend update immediately. The websocket path
    /// previously refreshed only the Friends tab, so an avatar change could sit
    /// unseen until the next periodic list refresh.
    /// </summary>
    public void ApplyFriendUpdate(VrcUser friend, VrcUser? knownPrevious = null)
    {
        if (friend is null || string.IsNullOrWhiteSpace(friend.Id))
        {
            return;
        }

        var current = _previousFriends?.ToList() ?? new List<VrcUser>();
        var index = current.FindIndex(candidate =>
            string.Equals(candidate.Id, friend.Id, StringComparison.Ordinal));
        var previous = knownPrevious ?? (index >= 0 ? current[index] : null);
        var profileChanges = previous is null
            ? new List<ProfileAvatarCandidate>()
            : FindProfileAvatarChanges(new[] { friend }, new[] { previous });
        if (index >= 0)
        {
            current[index] = friend;
        }
        else
        {
            current.Add(friend);
        }

        _previousFriends = current;
        if (previous is not null)
        {
            // A realtime event can change only iconUrl/bannerUrl. That produces
            // no normal feed entry, but it still has to start the profile-file
            // lookup before this method returns.
            QueueProfileAvatarChanges(profileChanges);
        }

        var bioEntry = ObserveBioValueCore(
            friend,
            friend.Bio,
            DateTimeOffset.UtcNow,
            addEntry: true,
            updateCheckedAt: true);
        SaveBioBaselines();

        // A realtime event for a friend who was missing from the local roster is
        // still the first proof that they are online/friended. Build against an
        // empty previous list so the Online row is not lost.
        var freshEntries = previous is null
            ? _feedService.BuildFeedFromFriends(new[] { friend })
            : _feedService.BuildFeedFromFriends(new[] { friend }, new[] { previous });
        if (bioEntry is not null)
        {
            freshEntries.Add(bioEntry);
        }
        if (freshEntries.Count > 0)
        {
            var knownIds = new HashSet<string>(_allEntries.Select(entry => entry.Id), StringComparer.Ordinal);
            var hasNewEntries = freshEntries.Any(entry => knownIds.Add(entry.Id));
            _allEntries = MergeEntries(_allEntries, freshEntries);
            ApplyFilterAndSearch();
            HasLoaded = true;
            if (hasNewEntries)
            {
                SaveHistory();
            }

            QueueAvatarHydration();
        }
    }

    /// <summary>
    /// Records an avatar switch that VRChat wrote to its local log. This is the
    /// source that still changes for friends in the same instance when the
    /// friend-list endpoint keeps serving their old avatar.
    /// </summary>
    public void ApplyLoggedAvatarChange(VrcUser friend, string avatarName)
        => ApplyLoggedAvatarChange(friend, avatarName, null);

    public void ApplyLoggedAvatarChange(
        VrcUser friend,
        string avatarName,
        string? avatarAuthor)
    {
        if (friend is null ||
            string.IsNullOrWhiteSpace(friend.Id) ||
            string.IsNullOrWhiteSpace(avatarName))
        {
            return;
        }

        // A timestamp keeps a later switch back to the same avatar as a new
        // event instead of being swallowed by the old row's id.
        var entryId = $"avatar-{friend.Id}-log-{avatarName}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        var entry = new ActivityEntry
        {
            Id = entryId,
            Type = ActivityType.AvatarChanged,
            UserId = friend.Id,
            UserName = friend.Username,
            DisplayName = friend.DisplayName,
            Tags = friend.Tags,
            DeveloperType = friend.DeveloperType,
            Location = friend.Location ?? string.Empty,
            Timestamp = DateTimeOffset.UtcNow,
            IsOnline = true,
            IsSelf = IsSelfUser(friend.Id),
            AvatarAuthor = avatarAuthor,
            Details = "changed avatar"
        };
        entry.SetAvatarName(avatarName);

        // VRChat's log does not include the avatar id or picture. Reuse local
        // history first, then resolve the public record by name in the
        // background so the row has a real image and a working View avatar
        // button instead of an empty circle.
        ApplyKnownLoggedAvatar(entry, friend, avatarName);
        _allEntries = MergeEntries(_allEntries, new[] { entry });
        ApplyFilterAndSearch();
        HasLoaded = true;
        SaveHistory();
        if (!entry.HasAvatar || !entry.HasAvatarImage)
        {
            QueueLoggedAvatarLookup(entry, avatarName, avatarAuthor);
        }
    }

    /// <summary>
    /// Applies a self switch read from VRChat's log and advances the self
    /// baseline, so a later /auth/user refresh for the same switch cannot add a
    /// second avatar row.
    /// </summary>
    public void ApplySelfLoggedAvatarChange(
        VrcUser self,
        string avatarName,
        string? avatarId,
        string? imageUrl,
        string? avatarAuthor)
    {
        ApplyLoggedAvatarChange(self, avatarName, avatarAuthor);
        if (_previousSelfUser is null ||
            !string.Equals(_previousSelfUser.Id, self.Id, StringComparison.Ordinal))
        {
            return;
        }

        _previousSelfUser.CurrentAvatarName = avatarName;
        _previousSelfUser.CurrentAvatar = avatarId ?? string.Empty;
        _previousSelfUser.CurrentAvatarThumbnailImageUrl = imageUrl ?? string.Empty;
        _previousSelfUser.CurrentAvatarImageUrl = imageUrl ?? string.Empty;
        _previousSelfUser.Location = self.Location;
    }

    private void ApplyKnownLoggedAvatar(ActivityEntry entry, VrcUser friend, string avatarName)
    {
        if (string.Equals(friend.CurrentAvatarName, avatarName, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(friend.CurrentAvatar))
        {
            entry.AvatarId = friend.CurrentAvatar;
            entry.AvatarUrl = ImageUrlHelper.PickAvatarThumbnailUrl(
                friend.CurrentAvatarThumbnailImageUrl,
                friend.CurrentAvatarImageUrl,
                null,
                null,
                256) ?? string.Empty;
            return;
        }

        if (_accountId is null)
        {
            return;
        }

        var known = _friendAvatars
            .Load(_accountId, friend.Id)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.AvatarId))
            .OrderByDescending(candidate => candidate.IsCurrent)
            .ThenByDescending(candidate => candidate.LastSeen)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, avatarName, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            return;
        }

        entry.AvatarId = known.AvatarId;
        entry.AvatarUrl = !string.IsNullOrWhiteSpace(known.ThumbnailUrl)
            ? known.ThumbnailUrl
            : known.ImageUrl;
    }

    private void QueueLoggedAvatarLookup(
        ActivityEntry entry,
        string avatarName,
        string? avatarAuthor)
    {
        if (_accountId is null ||
            string.IsNullOrWhiteSpace(avatarName) ||
            !_loggedAvatarLookups.Add(entry.Id))
        {
            return;
        }

        _ = ResolveLoggedAvatarAsync(entry, avatarName, avatarAuthor);
    }

    private async Task ResolveLoggedAvatarAsync(
        ActivityEntry entry,
        string avatarName,
        string? avatarAuthor)
    {
        try
        {
            if (entry.HasAvatar)
            {
                var direct = await _api.GetAvatarAsync(entry.AvatarId);
                if (direct is not null)
                {
                    // A private same-name duplicate is not proof that this is the
                    // avatar the friend switched to. Keep looking for the public
                    // record by name before accepting it.
                    if (!direct.IsPrivate && !direct.IsUnavailable)
                    {
                        AvatarLookupCache.Remember(direct);
                        var directImage = !string.IsNullOrWhiteSpace(direct.ThumbnailImageUrl)
                            ? direct.ThumbnailImageUrl
                            : direct.ImageUrl;
                        if (!string.IsNullOrWhiteSpace(directImage))
                        {
                            entry.AvatarUrl = ImageUrlHelper.ToThumbnailUrl(directImage, 256) ?? directImage;
                            entry.SetAvatarName(string.IsNullOrWhiteSpace(direct.Name) ? avatarName : direct.Name);
                            SaveHistory();
                            return;
                        }
                    }
                }
            }

            Avatar? match = null;
            // The public avatar index is the source of truth for a public
            // avatar name. VRChat's marketplace search can return a private
            // same-name duplicate, which used to become this row's record and
            // made a public avatar look private.
            try
            {
                var primary = await _api.SearchAllCommunityAvatarsAsync(
                    avatarName,
                    limit: 25,
                    providerLimit: 25,
                    providers: new[] { "avtrDB" });
                match = PickMatchingAvatar(primary, avatarName, avatarAuthor);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"log avatar primary index {avatarName}: {ex.Message}");
            }

            if (match is null)
            {
                try
                {
                    var result = await _api.SearchAvatarsAsync(new AvatarFilter
                    {
                        SearchTerm = avatarName,
                        PageSize = 50,
                        ReleaseStatus = "all"
                    });
                    match = PickMatchingAvatar(result.Avatars, avatarName, avatarAuthor);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"log avatar VRChat search {avatarName}: {ex.Message}");
                }
            }

            if (match is null)
            {
                try
                {
                    var community = await _api.SearchAllCommunityAvatarsAsync(
                        avatarName,
                        limit: 50,
                        providerLimit: 50);
                    match = PickMatchingAvatar(community, avatarName, avatarAuthor);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"log avatar index search {avatarName}: {ex.Message}");
                }
            }

            if (match is null || string.IsNullOrWhiteSpace(match.Id))
            {
                return;
            }

            AvatarLookupCache.Remember(match);
            entry.AvatarId = match.Id;
            var image = !string.IsNullOrWhiteSpace(match.ThumbnailImageUrl)
                ? match.ThumbnailImageUrl
                : match.ImageUrl;
            entry.AvatarUrl = ImageUrlHelper.ToThumbnailUrl(image, 256) ?? image;
            entry.SetAvatarName(string.IsNullOrWhiteSpace(match.Name) ? avatarName : match.Name);

            if (_accountId is not null && !string.IsNullOrWhiteSpace(entry.UserId))
            {
                _friendAvatars.RememberDetails(_accountId, entry.UserId, match, isCurrent: false);
            }

            SaveHistory();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"log avatar resolution {avatarName}: {ex.Message}");
        }
        finally
        {
            _loggedAvatarLookups.Remove(entry.Id);
        }
    }

    private static Avatar? PickMatchingAvatar(
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

    /// <summary>
    /// Applies the fast online-friend refresh without pretending that the
    /// offline friends in the previous complete snapshot disappeared. This lets
    /// avatar changes show up from the background refresh instead of waiting
    /// for a full offline download.
    /// </summary>
    public void ApplyOnlineFriends(
        IReadOnlyList<VrcUser> friends,
        IReadOnlySet<string>? knownOfflineFriendIds = null)
    {
        var online = friends.ToList();
        if (_previousFriends is null)
        {
            // Establish the baseline from this online set. The complete list is
            // merged on the next full load, but until then the next online poll
            // can already detect avatar/profile changes instead of treating
            // every poll as another first load.
            _previousFriends = online;
            var seededBioEntries = ObserveFriendBios(online, DateTimeOffset.UtcNow);
            RenderFeed(online, previous: null);
            if (seededBioEntries.Count > 0)
            {
                _allEntries = MergeEntries(_allEntries, seededBioEntries);
                ApplyFilterAndSearch();
                HasLoaded = true;
                SaveHistory();
            }

            return;
        }

        var profileChanges = FindProfileAvatarChanges(online, _previousFriends);
        var onlineById = online
            .Where(friend => !string.IsNullOrWhiteSpace(friend.Id))
            .GroupBy(friend => friend.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var current = _previousFriends
            .Select(friend => onlineById.TryGetValue(friend.Id, out var updated) ? updated : friend)
            .ToList();
        var knownIds = new HashSet<string>(current.Select(friend => friend.Id), StringComparer.Ordinal);
        current.AddRange(online.Where(friend => knownIds.Add(friend.Id)));

        // The online endpoint is a complete online set. A friend who was online
        // in the previous snapshot and is absent now went offline. Without this
        // demotion a 30-second online poll could never produce Offline rows, and
        // the friend would keep a stale world forever. When /auth/user supplies
        // the offline bucket, use it as confirmation instead of guessing from a
        // possibly truncated page.
        for (var index = 0; index < current.Count; index++)
        {
            var friend = current[index];
            if (onlineById.ContainsKey(friend.Id) || IsOfflineUser(friend))
            {
                continue;
            }

            if (knownOfflineFriendIds is not null &&
                !knownOfflineFriendIds.Contains(friend.Id))
            {
                continue;
            }

            current[index] = AsOffline(friend);
        }

        var bioEntries = ObserveFriendBios(online, DateTimeOffset.UtcNow);
        SetFeed(current);
        if (bioEntries.Count > 0)
        {
            _allEntries = MergeEntries(_allEntries, bioEntries);
            ApplyFilterAndSearch();
            HasLoaded = true;
            SaveHistory();
        }

        QueueProfileAvatarChanges(profileChanges);
    }

    /// <summary>
    /// Records the signed-in account as a feed subject too. VRChat's pipeline
    /// sends user-location/user-update for this account, but the friend-only
    /// feed path never saw it, so the account's own online/offline, world moves,
    /// avatar and bio changes were missing.
    /// </summary>
    public void ApplyCurrentUser(VrcUser user)
    {
        if (user is null || string.IsNullOrWhiteSpace(user.Id))
        {
            return;
        }

        var previous = _previousSelfUser;
        _previousSelfUser = CloneUser(user);

        var entries = previous is null
            ? _feedService.BuildFeedFromFriends(new[] { user })
            : _feedService.BuildFeedFromFriends(new[] { user }, new[] { previous });
        MarkSelfEntries(entries);
        var bioEntry = ObserveBioValueCore(
            user,
            user.Bio,
            DateTimeOffset.UtcNow,
            addEntry: true,
            updateCheckedAt: true);
        if (bioEntry is not null)
        {
            // The shared feed builder words rows for friends. Replace its self
            // bio duplicate with the version that says "your bio".
            entries.RemoveAll(entry =>
                entry.Type == ActivityType.BioChanged &&
                string.Equals(entry.UserId, user.Id, StringComparison.Ordinal));
            entries.Add(bioEntry);
        }

        // A self switch first seen in VRChat's log has the new name but no id.
        // When /auth/user catches up with the same name, it is the same switch,
        // not a second one; let the existing log row keep the event.
        if (previous is not null &&
            string.IsNullOrWhiteSpace(previous.CurrentAvatar) &&
            !string.IsNullOrWhiteSpace(user.CurrentAvatar) &&
            string.Equals(
                previous.CurrentAvatarName,
                user.CurrentAvatarName,
                StringComparison.OrdinalIgnoreCase))
        {
            entries.RemoveAll(entry =>
                entry.Type == ActivityType.AvatarChanged &&
                string.Equals(entry.UserId, user.Id, StringComparison.Ordinal));
        }

        SaveBioBaselines();
        if (entries.Count == 0)
        {
            return;
        }

        _allEntries = MergeEntries(_allEntries, entries);
        ApplyFilterAndSearch();
        HasLoaded = true;
        SaveHistory();
        QueueAvatarHydration();
    }

    /// <summary>
    /// Records a profile bio fetched outside the friend-list payload. VRCX keeps
    /// a persisted bio baseline and checks one stale friend at a time because
    /// the realtime friend-update payload usually carries no bio at all.
    /// </summary>
    public bool ObserveProfileBio(VrcUser friend, string? bio, DateTimeOffset checkedAt)
    {
        if (friend is null || string.IsNullOrWhiteSpace(friend.Id) || bio is null)
        {
            return false;
        }

        var normalized = bio.Trim();
        var entry = ObserveBioValueCore(
            friend,
            normalized,
            checkedAt,
            addEntry: true,
            updateCheckedAt: true);
        SaveBioBaselines();

        if (_previousSelfUser is { } self &&
            string.Equals(friend.Id, self.Id, StringComparison.Ordinal))
        {
            self.Bio = normalized;
        }

        var knownFriend = _previousFriends?.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, friend.Id, StringComparison.Ordinal));
        if (knownFriend is not null)
        {
            knownFriend.Bio = normalized;
        }

        if (entry is null)
        {
            return false;
        }

        _allEntries = MergeEntries(_allEntries, new[] { entry });
        ApplyFilterAndSearch();
        HasLoaded = true;
        SaveHistory();
        return true;
    }

    /// <summary>
    /// Returns the first friend whose bio has never been checked or is older
    /// than <paramref name="minimumAge"/>. The caller supplies the priority
    /// order (self, then online friends, then the rest).
    /// </summary>
    /// <summary>
    /// Marks a profile as checked even when VRChat omitted the bio field, so
    /// the scanner does not hammer the same profile every few seconds.
    /// </summary>
    public void MarkBioChecked(string userId, DateTimeOffset checkedAt)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        if (_bioBaselines.TryGetValue(userId, out var baseline))
        {
            baseline.CheckedAt = checkedAt;
        }
        else
        {
            _bioBaselines[userId] = new BioBaseline
            {
                Bio = null,
                CheckedAt = checkedAt
            };
        }

        SaveBioBaselines();
    }

    public bool IsBioCheckDue(
        string userId,
        DateTimeOffset now,
        TimeSpan minimumAge)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        return !_bioBaselines.TryGetValue(userId, out var baseline) ||
               now - baseline.CheckedAt >= minimumAge;
    }

    public string? NextBioCandidateId(
        IEnumerable<string> orderedUserIds,
        DateTimeOffset now,
        TimeSpan minimumAge)
    {
        foreach (var userId in orderedUserIds)
        {
            if (IsBioCheckDue(userId, now, minimumAge))
            {
                return userId;
            }
        }

        return null;
    }

    private List<ActivityEntry> ObserveFriendBios(
        IEnumerable<VrcUser> friends,
        DateTimeOffset observedAt)
    {
        var entries = new List<ActivityEntry>();
        var changed = false;
        foreach (var friend in friends)
        {
            if (friend.Bio is null || string.IsNullOrWhiteSpace(friend.Id))
            {
                continue;
            }

            var normalized = friend.Bio.Trim();
            if (!_bioBaselines.TryGetValue(friend.Id, out var known) ||
                !string.Equals(known.Bio, normalized, StringComparison.Ordinal))
            {
                changed = true;
            }

            var entry = ObserveBioValueCore(
                friend,
                normalized,
                observedAt,
                addEntry: true,
                updateCheckedAt: false);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        if (changed)
        {
            SaveBioBaselines();
        }

        return entries;
    }

    private ActivityEntry? ObserveBioValueCore(
        VrcUser friend,
        string? bio,
        DateTimeOffset observedAt,
        bool addEntry,
        bool updateCheckedAt)
    {
        if (bio is null || string.IsNullOrWhiteSpace(friend.Id))
        {
            return null;
        }

        var normalized = bio.Trim();
        ActivityEntry? entry = null;
        if (_bioBaselines.TryGetValue(friend.Id, out var baseline))
        {
            if (addEntry &&
                baseline.Bio is not null &&
                !string.Equals(baseline.Bio, normalized, StringComparison.Ordinal))
            {
                entry = CreateBioEntry(friend, normalized, baseline.Bio, observedAt);
            }

            if (string.Equals(baseline.Bio, normalized, StringComparison.Ordinal))
            {
                if (updateCheckedAt)
                {
                    baseline.CheckedAt = observedAt;
                }

                return entry;
            }
        }

        _bioBaselines[friend.Id] = new BioBaseline
        {
            Bio = normalized,
            CheckedAt = observedAt
        };
        return entry;
    }

    private ActivityEntry CreateBioEntry(
        VrcUser friend,
        string bio,
        string previousBio,
        DateTimeOffset timestamp)
    {
        var isSelf = IsSelfUser(friend.Id);
        return new ActivityEntry
        {
            Id = $"bio-{friend.Id}-{ActivityFeedService.ContentKey(previousBio)}-{ActivityFeedService.ContentKey(bio)}",
            Type = ActivityType.BioChanged,
            UserId = friend.Id,
            UserName = friend.Username,
            DisplayName = friend.DisplayName,
            IsSelf = isSelf,
            Tags = friend.Tags,
            DeveloperType = friend.DeveloperType,
            AvatarUrl = AvatarThumbnailUrl(friend),
            Location = string.Equals(friend.Location, "offline", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : friend.Location ?? string.Empty,
            Timestamp = timestamp,
            IsOnline = !IsOfflineUser(friend),
            Details = bio.Length == 0
                ? (isSelf ? "cleared your bio" : "cleared their bio")
                : $"changed {(isSelf ? "your" : "their")} bio · {(bio.Length > 80 ? bio[..80].TrimEnd() + "…" : bio)}"
        };
    }

    private bool IsSelfUser(string? userId)
        => !string.IsNullOrWhiteSpace(_accountId) &&
           string.Equals(_accountId, userId, StringComparison.Ordinal);

    private void MarkSelfEntries(IEnumerable<ActivityEntry> entries)
    {
        if (_accountId is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (!string.Equals(entry.UserId, _accountId, StringComparison.Ordinal))
            {
                continue;
            }

            entry.IsSelf = true;
            if (entry.Type != ActivityType.BioChanged)
            {
                continue;
            }

            if (string.Equals(entry.Details, "cleared their bio", StringComparison.Ordinal))
            {
                entry.Details = "cleared your bio";
            }
            else if (entry.Details.StartsWith("changed their bio · ", StringComparison.Ordinal))
            {
                entry.Details = "changed your bio · " + entry.Details["changed their bio · ".Length..];
            }
        }
    }

    private void SaveBioBaselines()
    {
        if (_accountId is not null)
        {
            _store.SaveBioBaselines(_accountId, _bioBaselines);
        }
    }

    private static bool IsOfflineUser(VrcUser friend)
        => string.Equals(friend.Status, "offline", StringComparison.OrdinalIgnoreCase) ||
           string.IsNullOrWhiteSpace(friend.Location) ||
           string.Equals(friend.Location, "offline", StringComparison.OrdinalIgnoreCase);

    private static VrcUser AsOffline(VrcUser friend)
    {
        var offline = CloneUser(friend);
        offline.Location = "offline";
        offline.Status = "offline";
        offline.State = "offline";
        return offline;
    }

    private static VrcUser CloneUser(VrcUser user)
        => JsonSerializer.Deserialize<VrcUser>(
               JsonSerializer.Serialize(user)) ?? new VrcUser();

    /// <summary>
    /// VRChat moved friend avatar changes onto the profile image fields. A
    /// changed currentAvatar is still handled by the normal feed builder; this
    /// finds the image-only changes and resolves whether the new picture is an
    /// avatar (not a custom profile icon), matching VRCX's feed behavior.
    /// </summary>
    private static List<ProfileAvatarCandidate> FindProfileAvatarChanges(
        IReadOnlyList<VrcUser> current,
        IReadOnlyList<VrcUser>? previous)
    {
        var changes = new List<ProfileAvatarCandidate>();
        if (previous is null || previous.Count == 0)
        {
            return changes;
        }

        var previousById = previous
            .Where(friend => !string.IsNullOrWhiteSpace(friend.Id))
            .GroupBy(friend => friend.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var friend in current)
        {
            if (string.IsNullOrWhiteSpace(friend.Id) ||
                !previousById.TryGetValue(friend.Id, out var before))
            {
                continue;
            }

            var currentImage = DetectProfileAvatarChange(friend, before);
            if (string.IsNullOrWhiteSpace(currentImage))
            {
                continue;
            }

            var fileId = ImageUrlHelper.ExtractFileId(currentImage);
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                var previousImage = ProfileAvatarImage(before);
                var previousFileId = ImageUrlHelper.ExtractFileId(previousImage);
                changes.Add(new ProfileAvatarCandidate(friend, currentImage, fileId, previousFileId));
            }
        }

        return changes;
    }

    private void QueueProfileAvatarChanges(IEnumerable<ProfileAvatarCandidate> changes)
    {
        if (_accountId is null)
        {
            return;
        }

        foreach (var change in changes)
        {
            var key = change.Friend.Id + "|" + change.PreviousFileId + "|" + change.FileId;
            if (!_profileAvatarLookups.Add(key))
            {
                continue;
            }

            _ = ResolveProfileAvatarChangeAsync(change, key);
        }
    }

    private async Task ResolveProfileAvatarChangeAsync(ProfileAvatarCandidate change, string key)
    {
        try
        {
            var file = await _api.GetFileAsync(change.FileId);
            if (file is null || !file.IsAvatarImage)
            {
                return;
            }

            // Ignore a lookup that finished after the friend moved on again.
            var latest = _previousFriends?.FirstOrDefault(friend =>
                string.Equals(friend.Id, change.Friend.Id, StringComparison.Ordinal));
            if (latest is null ||
                !string.Equals(ProfileAvatarImage(latest), change.ImageUrl, StringComparison.Ordinal))
            {
                return;
            }

            AddProfileAvatarChange(change, file.AvatarName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"avatar profile image lookup {change.FileId}: {ex.Message}");
        }
        finally
        {
            _profileAvatarLookups.Remove(key);
        }
    }

    private void AddProfileAvatarChange(ProfileAvatarCandidate change, string? avatarName)
    {
        var friend = change.Friend;
        var imageUrl = change.ImageUrl;
        var entryId = $"avatar-{friend.Id}-profile-{change.PreviousFileId ?? "none"}-{change.FileId}";
        if (_allEntries.Any(entry => string.Equals(entry.Id, entryId, StringComparison.Ordinal)))
        {
            return;
        }

        var entry = new ActivityEntry
        {
            Id = entryId,
            Type = ActivityType.AvatarChanged,
            UserId = friend.Id,
            UserName = friend.Username,
            DisplayName = friend.DisplayName,
            Tags = friend.Tags,
            DeveloperType = friend.DeveloperType,
            AvatarUrl = ImageUrlHelper.ToThumbnailUrl(imageUrl, 256) ?? imageUrl,
            Location = friend.Location ?? string.Empty,
            Timestamp = DateTimeOffset.UtcNow,
            IsOnline = !string.Equals(friend.Location, "offline", StringComparison.OrdinalIgnoreCase),
            Details = "changed avatar"
        };
        entry.SetAvatarName(avatarName);

        _allEntries = MergeEntries(_allEntries, new[] { entry });
        ApplyFilterAndSearch();
        HasLoaded = true;
        SaveHistory();
        QueueAvatarHydration();
    }

    internal static string? DetectProfileAvatarChange(VrcUser current, VrcUser previous)
    {
        var previousAvatar = AvatarIdentity(previous);
        var currentAvatar = AvatarIdentity(current);
        if (!string.IsNullOrWhiteSpace(currentAvatar) &&
            !string.Equals(previousAvatar, currentAvatar, StringComparison.Ordinal))
        {
            // The normal avatar-id/image comparison already creates this row.
            return null;
        }

        var previousImage = ProfileAvatarImage(previous);
        var currentImage = ProfileAvatarImage(current);
        return string.IsNullOrWhiteSpace(currentImage) ||
               string.Equals(previousImage, currentImage, StringComparison.Ordinal)
            ? null
            : currentImage;
    }

    private static string? AvatarIdentity(VrcUser friend)
    {
        if (!string.IsNullOrWhiteSpace(friend.CurrentAvatar))
        {
            return friend.CurrentAvatar;
        }

        if (!string.IsNullOrWhiteSpace(friend.CurrentAvatarThumbnailImageUrl))
        {
            return friend.CurrentAvatarThumbnailImageUrl;
        }

        return friend.CurrentAvatarImageUrl;
    }

    private static string? ProfileAvatarImage(VrcUser friend)
    {
        if (string.Equals(friend.BannerType, "avatarBanner", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(friend.BannerUrl))
        {
            return friend.BannerUrl;
        }

        // VRChat's user payload now carries the profile/avatar picture in
        // iconUrl. The remaining fields are legacy fallbacks from older builds.
        if (!string.IsNullOrWhiteSpace(friend.IconUrl))
        {
            return friend.IconUrl;
        }

        if (!string.IsNullOrWhiteSpace(friend.ProfilePicOverrideThumbnail))
        {
            return friend.ProfilePicOverrideThumbnail;
        }

        return string.IsNullOrWhiteSpace(friend.ProfilePicOverride)
            ? null
            : friend.ProfilePicOverride;
    }

    /// <summary>
    /// Shows the online portion of the friend list while the complete list is
    /// loading. This deliberately does not advance the snapshot, so a partial
    /// list can never be mistaken for friends who were removed.
    /// </summary>
    public void ShowOnlineFriends(IReadOnlyList<VrcUser> friends)
    {
        var current = friends.ToList();
        RenderFeed(current, previous: null);
    }

    private void SetFeed(IReadOnlyList<VrcUser> friends)
    {
        var current = friends.ToList();
        var previous = _previousFriends;
        _previousFriends = current;
        RenderFeed(current, previous);
    }

    /// <summary>
    /// Records a friendship change that was confirmed against VRChat's
    /// friendStatus endpoint. List diffs never call this directly.
    /// </summary>
    public void ReportFriendChange(
        VrcUser friend,
        bool added,
        DateTimeOffset? timestamp = null,
        bool detectedAfterRelaunch = false)
    {
        if (string.IsNullOrWhiteSpace(friend.Id))
        {
            return;
        }

        var type = added ? ActivityType.FriendAdded : ActivityType.FriendRemoved;
        var lastRelationshipEvent = _allEntries
            .Where(entry =>
                (entry.Type is ActivityType.FriendAdded or ActivityType.FriendRemoved) &&
                string.Equals(entry.UserId, friend.Id, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.Timestamp)
            .FirstOrDefault();
        if (lastRelationshipEvent?.Type == type)
        {
            // A relaunch can re-detect the same state against an older snapshot.
            // The relationship state has not changed since the last event.
            return;
        }

        var at = timestamp ?? DateTimeOffset.UtcNow;

        var entry = new ActivityEntry
        {
            Id = $"friend-{(added ? "added" : "removed")}-{friend.Id}-{Guid.NewGuid():N}",
            Type = type,
            UserId = friend.Id,
            UserName = friend.Username,
            DisplayName = friend.DisplayName,
            Tags = friend.Tags,
            DeveloperType = friend.DeveloperType,
            AvatarUrl = AvatarThumbnailUrl(friend),
            Location = friend.Location ?? string.Empty,
            Timestamp = at,
            IsOnline = added,
            DetectedAfterRelaunch = detectedAfterRelaunch,
            Details = added ? "friended you" : "unfriended you"
        };

        _allEntries.Insert(0, entry);
        if (_allEntries.Count > MaxRetainedEntries)
        {
            _allEntries.RemoveRange(MaxRetainedEntries, _allEntries.Count - MaxRetainedEntries);
        }

        ApplyFilterAndSearch();
        HasLoaded = true;
        SaveHistory();
    }

    private static string AvatarThumbnailUrl(VrcUser friend)
        => ImageUrlHelper.PickAvatarThumbnailUrl(
               friend.UserIcon,
               friend.ProfilePicOverrideThumbnail ?? friend.IconUrl,
               friend.CurrentAvatarThumbnailImageUrl,
               friend.CurrentAvatarImageUrl,
               128) ?? string.Empty;

    private void RenderFeed(IReadOnlyList<VrcUser> friends, IReadOnlyList<VrcUser>? previous)
    {
        var freshEntries = _feedService.BuildFeedFromFriends(friends, previous);
        var knownIds = new HashSet<string>(_allEntries.Select(entry => entry.Id), StringComparer.Ordinal);
        var hasNewEntries = freshEntries.Any(entry => knownIds.Add(entry.Id));
        HasLoaded = true;
        ErrorMessage = null;
        if (!hasNewEntries)
        {
            // The five-second online poll usually finds nothing new. Rebuilding
            // the visible collection every time made a 2,000-friend timeline
            // stutter; leave it untouched when the feed did not change.
            return;
        }

        _allEntries = MergeEntries(_allEntries, freshEntries);
        ApplyFilterAndSearch();
        SaveHistory();
        QueueAvatarHydration();
    }

    /// <summary>
    /// The store keeps the new rows in memory immediately and writes the ~14 MB
    /// file on a worker thread, so this call is cheap on the UI thread.
    /// </summary>
    private void SaveHistory()
    {
        if (_accountId is not null)
        {
            _store.Save(_accountId, _allEntries);
        }
    }

    /// <summary>Waits for queued feed writes; used by tests and shutdown.</summary>
    internal Task FlushSaveAsync() => _store.FlushAsync();

    private const int MaxRetainedEntries = 20_000;

    /// <summary>
    /// Keeps the activity timeline stable between friend-list refreshes. A
    /// refresh only adds newly observed events; it must not erase an avatar or
    /// bio change that was detected on the previous snapshot.
    /// </summary>
    private static List<ActivityEntry> MergeEntries(
        IReadOnlyList<ActivityEntry> existing,
        IReadOnlyList<ActivityEntry> fresh)
    {
        var byId = new Dictionary<string, ActivityEntry>(StringComparer.Ordinal);
        foreach (var entry in existing)
        {
            byId[entry.Id] = entry;
        }

        // Only avatar-change rows can be duplicates, so index those by user
        // instead of scanning the whole 20k timeline for every fresh row.
        var avatarByUser = new Dictionary<string, List<ActivityEntry>>(StringComparer.Ordinal);
        void IndexAvatar(ActivityEntry entry)
        {
            if (entry.Type != ActivityType.AvatarChanged || string.IsNullOrWhiteSpace(entry.UserId))
            {
                return;
            }

            if (!avatarByUser.TryGetValue(entry.UserId, out var list))
            {
                list = new List<ActivityEntry>();
                avatarByUser[entry.UserId] = list;
            }

            list.Add(entry);
        }

        foreach (var entry in byId.Values)
        {
            IndexAvatar(entry);
        }

        foreach (var entry in fresh)
        {
            ActivityEntry? duplicate = null;
            if (entry.Type == ActivityType.AvatarChanged &&
                !string.IsNullOrWhiteSpace(entry.UserId) &&
                avatarByUser.TryGetValue(entry.UserId, out var candidates))
            {
                duplicate = candidates.FirstOrDefault(existing => IsSameAvatarSwitch(existing, entry));
            }
            if (duplicate is not null)
            {
                // Prefer the row that already has the real avatar id/picture.
                if (string.IsNullOrWhiteSpace(duplicate.AvatarId) &&
                    !string.IsNullOrWhiteSpace(entry.AvatarId))
                {
                    byId.Remove(duplicate.Id);
                    byId[entry.Id] = entry;
                }

                continue;
            }

            if (byId.TryAdd(entry.Id, entry))
            {
                IndexAvatar(entry);
            }
        }

        return byId.Values
            .OrderByDescending(entry => entry.Timestamp)
            .Take(MaxRetainedEntries)
            .ToList();
    }

    /// <summary>
    /// A log-only avatar row and the API row for the same switch can arrive
    /// seconds apart. Treat them as one event only while one side still lacks
    /// an id, so two deliberate switches back to the same avatar stay separate.
    /// </summary>
    private static bool IsSameAvatarSwitch(ActivityEntry left, ActivityEntry right)
    {
        if (left.Id == right.Id ||
            left.Type != ActivityType.AvatarChanged ||
            right.Type != ActivityType.AvatarChanged ||
            !string.Equals(left.UserId, right.UserId, StringComparison.Ordinal) ||
            !string.Equals(
                left.EffectiveAvatarName,
                right.EffectiveAvatarName,
                StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(left.AvatarId) &&
             !string.IsNullOrWhiteSpace(right.AvatarId)) ||
            Math.Abs((left.Timestamp - right.Timestamp).TotalSeconds) > 8)
        {
            return false;
        }

        return true;
    }

    private void QueueAvatarHydration()
    {
        if (!_avatarHydration.IsCompleted || _allEntries.Count == 0)
        {
            return;
        }

        _avatarHydration = HydrateAvatarNamesSafelyAsync(_allEntries);
    }

    private async Task HydrateAvatarNamesSafelyAsync(IReadOnlyList<ActivityEntry> entries)
    {
        try
        {
            await HydrateAvatarNamesAsync(entries);
        }
        catch (Exception ex)
        {
            // Activity history should still be usable when an avatar index is down.
            Debug.WriteLine(ex);
        }
    }

    /// <summary>
    /// Friend payloads often omit currentAvatar while keeping the avatar picture.
    /// Recover the id from this account's remembered resolutions, retained friend
    /// history, or the public avatar indexes — the same path a profile uses.
    /// </summary>
    private async Task HydrateAvatarNamesAsync(IReadOnlyList<ActivityEntry> entries)
    {
        var avatarEntries = entries
            .Where(entry => entry.Type == ActivityType.AvatarChanged)
            .ToList();
        if (avatarEntries.Count == 0)
        {
            return;
        }

        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        var changed = ResolveAvatarIdsFromLocalHistory(avatarEntries, names);
        changed |= await ResolveAvatarIdsFromImagesAsync(avatarEntries, names);

        // Backfill the handful of most recent log-only rows on startup. The
        // full history is left alone so a large Activity list does not burst the
        // community indexes.
        foreach (var unresolved in avatarEntries
                     .Where(entry => entry.HasAvatarName)
                     .OrderByDescending(entry => entry.Timestamp)
                     .Take(3))
        {
            QueueLoggedAvatarLookup(
                unresolved,
                unresolved.EffectiveAvatarName,
                unresolved.AvatarAuthor);
        }

        // Ask the API only for the avatars this feed is still missing a name
        // for. This loop used to call GetAvatarAsync once per avatar row on
        // every refresh, so a deep timeline fired hundreds of requests for names
        // it already had. Group by id, remember the answers, and spend at most a
        // bounded number of lookups per pass.
        var pendingNames = avatarEntries
            .Where(entry => entry.HasAvatar &&
                            !entry.HasAvatarName &&
                            !_resolvedAvatarNames.ContainsKey(entry.AvatarId))
            .GroupBy(entry => entry.AvatarId, StringComparer.Ordinal)
            .OrderByDescending(group => group.Max(entry => entry.Timestamp))
            .Take(MaxAvatarNameLookupsPerPass)
            .ToList();

        foreach (var group in pendingNames)
        {
            if (!_resolvedAvatarNames.TryGetValue(group.Key, out var name) ||
                string.IsNullOrWhiteSpace(name))
            {
                if (!names.TryGetValue(group.Key, out name) || string.IsNullOrWhiteSpace(name))
                {
                    name = null;
                    try
                    {
                        name = (await _api.GetAvatarAsync(group.Key))?.Name;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                }

                _resolvedAvatarNames[group.Key] = name;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            foreach (var entry in group)
            {
                var before = entry.Details;
                entry.SetAvatarName(name);
                changed |= !string.Equals(before, entry.Details, StringComparison.Ordinal);
            }
        }

        if (changed)
        {
            SaveHistory();
        }
    }

    private bool ResolveAvatarIdsFromLocalHistory(
        IReadOnlyList<ActivityEntry> entries,
        Dictionary<string, string?> names)
    {
        if (_accountId is null)
        {
            return false;
        }

        var changed = false;
        var histories = new Dictionary<string, IReadOnlyList<FriendAvatar>>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(entry =>
                     !entry.HasAvatar && (entry.HasAvatarImage || entry.HasAvatarName)))
        {
            if (entry.HasAvatarImage)
            {
                var known = _friendAvatars.GetImageResolution(_accountId, entry.AvatarUrl);
                if (!string.IsNullOrWhiteSpace(known?.AvatarId))
                {
                    entry.AvatarId = known.AvatarId;
                    if (!string.IsNullOrWhiteSpace(known.ReleaseStatus) &&
                        !string.Equals(known.ReleaseStatus, "private", StringComparison.OrdinalIgnoreCase))
                    {
                        names.TryAdd(entry.AvatarId, null);
                    }

                    changed = true;
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(entry.UserId))
            {
                continue;
            }

            if (!histories.TryGetValue(entry.UserId, out var history))
            {
                history = _friendAvatars.Load(_accountId, entry.UserId);
                histories[entry.UserId] = history;
            }

            FriendAvatar? match;
            if (entry.HasAvatarImage)
            {
                var fileId = ImageUrlHelper.ExtractFileId(entry.AvatarUrl);
                match = history.FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.AvatarId) &&
                    (string.Equals(candidate.ThumbnailUrl, entry.AvatarUrl, StringComparison.Ordinal) ||
                     string.Equals(candidate.ImageUrl, entry.AvatarUrl, StringComparison.Ordinal) ||
                     (fileId is not null &&
                      (string.Equals(ImageUrlHelper.ExtractFileId(candidate.ThumbnailUrl), fileId, StringComparison.Ordinal) ||
                       string.Equals(ImageUrlHelper.ExtractFileId(candidate.ImageUrl), fileId, StringComparison.Ordinal)))));
            }
            else
            {
                match = history
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate.AvatarId))
                    .OrderByDescending(candidate => candidate.IsCurrent)
                    .ThenByDescending(candidate => candidate.LastSeen)
                    .FirstOrDefault(candidate =>
                        string.Equals(
                            candidate.Name,
                            entry.EffectiveAvatarName,
                            StringComparison.OrdinalIgnoreCase));
            }

            if (match is null)
            {
                continue;
            }

            entry.AvatarId = match.AvatarId;
            if (!entry.HasAvatarImage)
            {
                entry.AvatarUrl = !string.IsNullOrWhiteSpace(match.ThumbnailUrl)
                    ? match.ThumbnailUrl
                    : match.ImageUrl;
            }

            names.TryAdd(match.AvatarId, match.Name);
            changed = true;
        }

        return changed;
    }

    private async Task<bool> ResolveAvatarIdsFromImagesAsync(
        IReadOnlyList<ActivityEntry> entries,
        Dictionary<string, string?> names)
    {
        var pending = entries
            .Where(entry => !entry.HasAvatar && entry.HasAvatarImage)
            .Where(entry =>
            {
                var fileId = ImageUrlHelper.ExtractFileId(entry.AvatarUrl);
                return string.IsNullOrWhiteSpace(fileId) || !_unresolvedAvatarImages.Contains(fileId);
            })
            .ToList();
        if (pending.Count == 0)
        {
            return false;
        }

        var lookups = new Dictionary<string, (string Url, string UserId)>(StringComparer.Ordinal);
        foreach (var entry in pending)
        {
            var fileId = ImageUrlHelper.ExtractFileId(entry.AvatarUrl);
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                lookups.TryAdd(fileId, (entry.AvatarUrl, entry.UserId));
            }
        }

        if (lookups.Count == 0)
        {
            return false;
        }

        using var gate = new SemaphoreSlim(4);
        var tasks = lookups.Select(async lookup =>
        {
            await gate.WaitAsync();
            try
            {
                var avatar = await _api.ResolveCommunityAvatarByImageAsync(
                    lookup.Value.Url,
                    lookup.Value.UserId);
                return (lookup.Key, Avatar: avatar, Failed: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return (lookup.Key, Avatar: (Avatar?)null, Failed: true);
            }
            finally
            {
                gate.Release();
            }
        });

        var resolved = await Task.WhenAll(tasks);
        var byFileId = resolved
            .Where(result => result.Avatar is not null &&
                             !string.IsNullOrWhiteSpace(result.Avatar.Id))
            .ToDictionary(result => result.Key, result => result.Avatar!, StringComparer.Ordinal);

        // A clean "no match" is final for this session; a failed request is not,
        // so it is retried on the next refresh.
        if (_unresolvedAvatarImages.Count < MaxUnresolvedAvatarImages)
        {
            foreach (var result in resolved)
            {
                if (!result.Failed && result.Avatar is null)
                {
                    _unresolvedAvatarImages.Add(result.Key);
                }
            }
        }

        var changed = false;
        foreach (var entry in pending)
        {
            var fileId = ImageUrlHelper.ExtractFileId(entry.AvatarUrl);
            if (fileId is null ||
                !byFileId.TryGetValue(fileId, out var avatar))
            {
                continue;
            }

            entry.AvatarId = avatar.Id;
            names.TryAdd(avatar.Id, avatar.Name);
            _friendAvatars.RememberImageResolution(
                _accountId,
                entry.AvatarUrl,
                avatar.Id,
                avatar.ReleaseStatus);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Rebuilds the visible list from the full feed, so changing the filter or
    /// the search never drops entries from the underlying set.
    /// </summary>
    private void ApplyFilterAndSearch()
    {
        var filtered = _feedService.FilterByType(_allEntries, SelectedFilter);
        if (!ShowOfflineFriends)
        {
            filtered = filtered.Where(entry => entry.Type != ActivityType.FriendOffline).ToList();
        }

        var query = SearchQuery?.Trim() ?? string.Empty;
        filtered = query.Length == 0 ? filtered : SearchEntries(filtered, query);

        // A new filter or search is a new result set, so it always starts at the
        // first page. A refresh keeps the page the reader is already on.
        var pageKey = $"{SelectedFilter?.ToString() ?? "all"}|{ShowOfflineFriends}|{query}";
        if (!string.Equals(pageKey, _pageKey, StringComparison.Ordinal))
        {
            _pageKey = pageKey;
            _pageIndex = 0;
        }

        _filteredEntries = filtered;
        if (_pageIndex >= PageCount)
        {
            _pageIndex = Math.Max(0, PageCount - 1);
        }

        PublishPage();
        SearchPerson = query.Length == 0 ? null : FindSearchPerson(query);
        OnPropertyChanged(nameof(NoEntriesText));
    }

    private void PublishPage()
    {
        Entries = new ObservableCollection<ActivityEntry>(
            _filteredEntries.Skip(_pageIndex * PageSize).Take(PageSize));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(PageSummaryText));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(HasPages));
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
    }

    private void PreviousPage()
    {
        if (!HasPreviousPage)
        {
            return;
        }

        _pageIndex--;
        BumpPageToken();
        PublishPage();
    }

    private void NextPage()
    {
        if (!HasNextPage)
        {
            return;
        }

        _pageIndex++;
        BumpPageToken();
        PublishPage();
    }

    private void BumpPageToken()
    {
        _pageToken++;
        OnPropertyChanged(nameof(PageToken));
    }

    /// <summary>
    /// Searching by id, or by the name someone uses now, must still return the
    /// rows they generated under an older name. Every name seen for an account
    /// is treated as an alias of that account id.
    /// </summary>
    private List<ActivityEntry> SearchEntries(List<ActivityEntry> entries, string query)
    {
        var matched = _feedService.Search(entries, query);
        var ids = new HashSet<string>(
            matched.Select(entry => entry.UserId).Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.Ordinal);

        foreach (var person in KnownPeople())
        {
            if (ActivityFeedService.MatchesUserId(person.Id, query) ||
                person.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                person.Username.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(person.Id);
            }
        }

        if (ids.Count == 0)
        {
            return matched;
        }

        var direct = new HashSet<ActivityEntry>(matched);
        return entries
            .Where(entry => direct.Contains(entry) || ids.Contains(entry.UserId))
            .ToList();
    }

    /// <summary>
    /// Everyone the feed has seen, with the newest known name and picture per
    /// account. Entries supply the history, then the live roster and signed-in
    /// account overwrite them so renames resolve to the same user id.
    /// </summary>
    private List<KnownPerson> KnownPeople()
    {
        var people = new Dictionary<string, KnownPerson>(StringComparer.Ordinal);

        void Remember(
            string id,
            string? displayName,
            string? username,
            string? avatarUrl,
            List<string>? tags = null,
            string? developerType = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            people[id] = new KnownPerson(
                id,
                displayName ?? string.Empty,
                username ?? string.Empty,
                avatarUrl ?? string.Empty,
                tags,
                developerType);
        }

        foreach (var entry in _allEntries)
        {
            Remember(entry.UserId, entry.DisplayName, entry.UserName, entry.AvatarUrl);
        }

        foreach (var friend in _previousFriends ?? Array.Empty<VrcUser>())
        {
            Remember(
                friend.Id,
                friend.DisplayName,
                friend.Username,
                AvatarThumbnailUrl(friend),
                friend.Tags,
                friend.DeveloperType);
        }

        var self = _previousSelfUser;
        if (self is not null)
        {
            Remember(
                self.Id,
                self.DisplayName,
                self.Username,
                AvatarThumbnailUrl(self),
                self.Tags,
                self.DeveloperType);
        }

        return people.Values.ToList();
    }

    private ActivityEntry? FindSearchPerson(string query)
    {
        var matches = KnownPeople()
            .Where(person =>
                ActivityFeedService.MatchesUserId(person.Id, query) ||
                person.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                person.Username.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A world or activity search should not pretend it found one person.
        if (matches.Count != 1)
        {
            return null;
        }

        var person = matches[0];
        return new ActivityEntry
        {
            Id = $"search-person-{person.Id}",
            UserId = person.Id,
            DisplayName = string.IsNullOrWhiteSpace(person.DisplayName)
                ? person.Username
                : person.DisplayName,
            UserName = person.Username,
            AvatarUrl = person.AvatarUrl,
            Tags = person.Tags,
            DeveloperType = person.DeveloperType,
            IsSelf = string.Equals(person.Id, _accountId, StringComparison.Ordinal)
        };
    }

    private sealed record KnownPerson(
        string Id,
        string DisplayName,
        string Username,
        string AvatarUrl,
        List<string>? Tags,
        string? DeveloperType);

    public void SetDefaultFilter(string? filter)
    {
        var option = Filters.FirstOrDefault(candidate =>
            candidate.Type is { } type &&
            (string.Equals(filter, type.ToString(), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(filter, candidate.Label, StringComparison.OrdinalIgnoreCase)));
        _suppressFilterChanged = true;
        try
        {
            SelectedFilterOption = option ?? Filters[0];
        }
        finally
        {
            _suppressFilterChanged = false;
        }
    }

    private void SyncSelectedFilterOption()
    {
        var option = Filters.FirstOrDefault(candidate => candidate.Type == _selectedFilter) ?? Filters[0];
        if (!ReferenceEquals(option, _selectedFilterOption))
        {
            _selectedFilterOption = option;
            OnPropertyChanged(nameof(SelectedFilterOption));
        }
    }

    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        ApplyFilterAndSearch();
    }
}

public sealed record ActivityFilterOption(string Label, ActivityType? Type);
