using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
/// View model for avatar management: the whole avatar library, a detail view,
/// favourites, and wearing an avatar on the signed-in account.
/// </summary>
public sealed class AvatarViewModel : ViewModelBase
{
    private readonly VrcApiClient _api;

    private ObservableCollection<Avatar> _avatars = new();
    private string _searchTerm = string.Empty;
    private bool _isBusy;
    private string? _errorMessage;
    private string? _statusMessage;
    private int _totalCount;
    private Avatar? _selectedAvatar;
    private bool _hasLoaded;
    private readonly Dictionary<string, string> _favoriteRecords = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _favoriteTags = new(StringComparer.Ordinal);
    private readonly List<string> _unresolvedFavoriteIds = new();
    private readonly Dictionary<string, Avatar> _favoriteAvatars = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedFavoriteIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _localFavoriteIds = new(StringComparer.Ordinal);
    private bool _favoritesLoaded;
    private bool _favoritesLoading;
    private const int FavoriteFetchBatch = 6;

    // The account's complete avatar library; the grid shows a filtered view of it.
    private List<Avatar> _library = new();
    private int _publishedLibraryCount;
    private string? _currentAvatarId;
    private string? _originalAvatarId;
    private string? _previousAvatarId;
    private string? _activeSlotTag;
    private bool _favoritesOnly;

    private readonly List<IRelayCommand> _commands = new();

    public AvatarViewModel(VrcApiClient api)
    {
        _api = api;

        SearchCommand = Track(new RelayCommand(ApplyFilter));
        RefreshCommand = Track(new AsyncRelayCommand(LoadAsync, () => !IsBusy));
        ToggleFavoriteCommand = Track(new AsyncRelayCommand<string>(ToggleFavoriteAsync, _ => !IsBusy));
        WearAvatarCommand = Track(new AsyncRelayCommand<string>(WearAvatarAsync, _ => !IsBusy));
        OpenAvatarDetailsCommand = Track(new RelayCommand<string>(OpenDetails));
        CloseAvatarDetailsCommand = Track(new RelayCommand(() => SelectedAvatar = null));
        ClearSearchCommand = Track(new RelayCommand(ClearSearch));
        ToggleFavoritesFilterCommand = Track(new RelayCommand(ToggleFavoritesFilter));
        OpenAvatarOnWebCommand = Track(new RelayCommand<string>(OpenAvatarOnWeb, id => !string.IsNullOrWhiteSpace(id)));
        SelectSlotCommand = Track(new RelayCommand<FavoriteSlot>(ToggleSlot, slot => slot is not null));
        ChooseFavoriteSlotCommand = Track(new AsyncRelayCommand<string>(ChooseFavoriteSlotAsync, tag => !string.IsNullOrWhiteSpace(tag)));
        CancelFavoriteSlotCommand = Track(new RelayCommand(() => IsSlotPickerOpen = false));
        RevertPreviousAvatarCommand = Track(new AsyncRelayCommand(
            () => WearAvatarAsync(_previousAvatarId),
            () => !IsBusy && CanRevertPrevious));
        WearOriginalAvatarCommand = Track(new AsyncRelayCommand(
            () => WearAvatarAsync(_originalAvatarId),
            () => !IsBusy && CanRevertAvatar));
        ToggleLocalFavoriteCommand = Track(new RelayCommand<string>(ToggleLocalFavorite, id => !string.IsNullOrWhiteSpace(id)));
        GlobalSearchCommand = Track(new RelayCommand(
            () =>
            {
                // Ignore clicks while a search is already in flight: overlapping
                // searches would share the page counters and the result list.
                if (IsGlobalBusy)
                {
                    return;
                }

                // An empty box is a browse, not a search: keep the public community list.
                if (string.IsNullOrWhiteSpace(GlobalSearchTerm))
                {
                    _ = BrowseCommunityAsync();
                    return;
                }

                _ = StartSearchAsync();
            },
            () => !IsGlobalBusy));
        NextGlobalPageCommand = Track(new RelayCommand(
            () => _ = NextPageAsync(),
            () => HasNextGlobal));
        PreviousGlobalPageCommand = Track(new RelayCommand(
            () => PreviousPage(),
            () => HasPreviousGlobal));
        ClearGlobalSearchCommand = Track(new RelayCommand(ClearGlobalSearch));
        RefreshBrowseCommand = Track(new RelayCommand(RefreshBrowse, () => !IsGlobalBusy));

        LoadLocalFavorites();
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

    public ObservableCollection<Avatar> Avatars
    {
        get => _avatars;
        set => SetProperty(ref _avatars, value);
    }

    public string SearchTerm
    {
        get => _searchTerm;
        set
        {
            if (SetProperty(ref _searchTerm, value))
            {
                // Filtering the already-loaded library keeps search on your own
                // avatars and makes it instant.
                ApplyFilter();
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
                // Cards are created mid-load, so every command that depends on
                // IsBusy has to be re-evaluated when the flag changes.
                RaiseCommandStates();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Shows only the avatars favourited in VRChat, not a local list.</summary>
    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (SetProperty(ref _favoritesOnly, value))
            {
                OnPropertyChanged(nameof(FavoritesFilterLabel));
                ApplyFilter();
            }
        }
    }

    public string FavoritesFilterLabel => FavoritesOnly ? "All avatars" : "Favourites";

    /// <summary>Avatars favourited on the VRChat account, resolved against your library.</summary>
    public ObservableCollection<Avatar> FavoriteAvatars { get; } = new();

    /// <summary>One line per VRChat favourite slot (avatars1..avatars4).</summary>
    public ObservableCollection<FavoriteSlot> FavoriteSlots { get; } = new();

    public string FavoritesSummaryText => _favoriteRecords.Count == 0
        ? HasLoaded ? "No favourite avatars yet. Star an avatar to save it to VRChat." : string.Empty
        : $"{_favoriteRecords.Count} favourite{(_favoriteRecords.Count == 1 ? string.Empty : "s")} on VRChat · {FavoriteAvatars.Count} shown";

    public string VrcPlusFavoritesText => IsVrcPlusAccount
        ? "VRC+ is active: you get the extra favourite slots (2–4)."
        : "VRC+ unlocks favourite slots 2–4. Slot 1 is available to everyone.";

    public bool IsVrcPlusAccount
        => _api.CurrentUser?.Tags?.Any(tag => string.Equals(tag, "system_supporter", StringComparison.OrdinalIgnoreCase)) == true;

    public bool HasUnresolvedFavorites => _unresolvedFavoriteIds.Count > 0;

    private bool _isLoadingFavorites;

    /// <summary>True while favourite-avatar details are being fetched by id.</summary>
    public bool IsLoadingFavorites
    {
        get => _isLoadingFavorites;
        private set => SetProperty(ref _isLoadingFavorites, value);
    }

    public bool HasFavorites => _favoriteRecords.Count > 0 || _localFavoriteIds.Count > 0;

    /// <summary>Shown on the Favourites tab: its own requests, before/after the library.</summary>
    public string FavoritesLoadingText
    {
        get
        {
            if (_favoritesLoading)
            {
                return "Loading your VRChat favourites…";
            }

            if (IsLoadingFavorites)
            {
                var total = _favoriteRecords.Count + _localFavoriteIds.Count;
                return $"Loading favourite avatar details ({_favoriteAvatars.Count} of {total})…";
            }

            return string.Empty;
        }
    }

    // --- Avatar search across every avatar VRChat knows about ---
    private string _globalSearchTerm = string.Empty;
    private string _lastGlobalQuery = string.Empty;
    private int _globalPage;
    private bool _isGlobalBusy;

    public ObservableCollection<Avatar> GlobalResults { get; } = new();

    public string GlobalSearchTerm
    {
        get => _globalSearchTerm;
        set => SetProperty(ref _globalSearchTerm, value);
    }

    public bool IsGlobalBusy
    {
        get => _isGlobalBusy;
        private set
        {
            if (SetProperty(ref _isGlobalBusy, value))
            {
                OnPropertyChanged(nameof(GlobalSummaryText));
                GlobalSearchCommand.RaiseCanExecuteChanged();
                NextGlobalPageCommand.RaiseCanExecuteChanged();
                PreviousGlobalPageCommand.RaiseCanExecuteChanged();
                RefreshBrowseCommand.RaiseCanExecuteChanged();
            }
        }
    }


    /// <summary>Fetched result pages for the current query (page 1 is index 0).</summary>
    private readonly List<List<Avatar>> _searchPages = new();

    private int _searchPage;

    public bool HasNextGlobal => !IsGlobalBusy &&
        (_lastGlobalQuery.Length == 0
            ? _communityPageFull
            : _searchPage < _searchPages.Count || _lastVrchatPageFull || _communitySearchPageFull);

    public bool HasPreviousGlobal => !IsGlobalBusy && _searchPage > 1;

    /// <summary>Short-lived cache so repeating a query does not re-hit every index.</summary>
    private sealed class CachedSearch
    {
        public List<List<Avatar>> Pages { get; init; } = new();
        public DateTime FetchedAt { get; init; }
        public int VrchatRows { get; init; }
        public bool VrchatMore { get; init; }
        public bool CommunityMore { get; init; }
        public string AuthorName { get; init; } = string.Empty;

    }

    private readonly Dictionary<string, CachedSearch> _searchCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(4);

    private string SearchCacheKey => $"{GlobalSearchTerm.Trim().ToLowerInvariant()}|{PublicOnly}";

    private async Task StartSearchAsync()
    {
        if (IsGlobalBusy)
        {
            return;
        }

        _searchPages.Clear();
        _indexCounts.Clear();
        _searchPage = 0;

        if (_searchCache.TryGetValue(SearchCacheKey, out var cached) &&
            DateTime.UtcNow - cached.FetchedAt < SearchCacheTtl)
        {
            _lastGlobalQuery = GlobalSearchTerm.Trim();
            _authorFilterName = cached.AuthorName;
            _vrchatRowsInResults = cached.VrchatRows;
            _lastVrchatPageFull = cached.VrchatMore;
            _communitySearchPageFull = cached.CommunityMore;
            _searchPages.AddRange(cached.Pages);
            _searchPage = 1;
            PublishStoredPage();
            OnPropertyChanged(nameof(GlobalSummaryText));
            OnPropertyChanged(nameof(HasNextGlobal));
            OnPropertyChanged(nameof(HasPreviousGlobal));
            return;
        }

        await SearchGlobalAsync(append: false);
        if (IsGlobalBusy)
        {
            return;
        }

        if (GlobalResults.Count > 0)
        {
            AddResultChunks(GlobalResults.ToList());
            _searchPage = 1;
            PublishStoredPage();
            StoreSearchCache();
        }
    }

    private const int GlobalPageSize = 50;

    /// <summary>Splits fetched rows into 50-card pages for Back/Next.</summary>
    private void AddResultChunks(List<Avatar> avatars)
    {
        for (var start = 0; start < avatars.Count; start += GlobalPageSize)
        {
            _searchPages.Add(avatars
                .Skip(start)
                .Take(GlobalPageSize)
                .ToList());
        }

    }

    /// <summary>
    /// Unique avatars per source on one page. VRChat rows carry no index name, so
    /// they are counted as "VRChat" — that keeps the numbers summing to the card
    /// count instead of silently leaving VRChat's rows out.
    /// </summary>
    private static Dictionary<string, int> CountBySource(IEnumerable<Avatar> avatars)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var avatar in avatars)
        {
            var source = string.IsNullOrWhiteSpace(avatar.SourceName) ? "VRChat" : avatar.SourceName;
            counts[source] = counts.TryGetValue(source, out var current)
                ? current + 1
                : 1;
        }

        return counts;
    }

    private void StoreSearchCache()
    {
        _searchCache[SearchCacheKey] = new CachedSearch
        {
            Pages = _searchPages.Select(page => page.ToList()).ToList(),
            FetchedAt = DateTime.UtcNow,
            VrchatRows = _vrchatRowsInResults,
            VrchatMore = _lastVrchatPageFull,
            CommunityMore = _communitySearchPageFull,
            AuthorName = _authorFilterName
        };

        // Keep the cache tiny: one query per term, oldest dropped past 20 entries.
        while (_searchCache.Count > 20)
        {
            var oldest = _searchCache.OrderBy(entry => entry.Value.FetchedAt).First().Key;
            _searchCache.Remove(oldest);
        }
    }

    private async Task NextPageAsync()
    {
        if (IsGlobalBusy)
        {
            return;
        }

        if (_lastGlobalQuery.Length == 0)
        {
            await LoadMoreCommunityAsync();
            return;
        }

        if (_searchPage > 0 && _searchPage < _searchPages.Count)
        {
            _searchPage++;
            PublishStoredPage();
            return;
        }

        var before = GlobalResults.Select(avatar => avatar.Id).ToHashSet(StringComparer.Ordinal);
        await SearchGlobalAsync(append: true);

        var added = GlobalResults
            .Where(avatar => !before.Contains(avatar.Id))
            .ToList();

        if (added.Count == 0)
        {
            return;
        }

        AddResultChunks(added);
        _searchPage = Math.Min(_searchPage + 1, _searchPages.Count);
        PublishStoredPage();
        StoreSearchCache();
    }

    private void PreviousPage()
    {
        if (IsGlobalBusy || _searchPage <= 1)
        {
            return;
        }

        _searchPage--;
        PublishStoredPage();
    }

    private void PublishStoredPage()
    {
        GlobalResults.Clear();
        if (_searchPage >= 1 && _searchPage <= _searchPages.Count)
        {
            foreach (var avatar in _searchPages[_searchPage - 1])
            {
                GlobalResults.Add(avatar);
            }
        }

        // Status counts unique avatars actually shown on this page, per index, so
        // an index can never claim rows that de-duplication removed.
        _indexCounts.Clear();
        if (_searchPage >= 1 && _searchPage <= _searchPages.Count)
        {
            foreach (var entry in CountBySource(_searchPages[_searchPage - 1]))
            {
                _indexCounts[entry.Key] = entry.Value;
            }
        }

        // "from N community indexes" must describe this page too, not the last fetch.
        // VRChat is a source in the status line but not a community index, so it is
        // excluded from the summary's index count.
        _communitySourceCount = _indexCounts
            .Count(entry => !entry.Key.Equals("VRChat", StringComparison.OrdinalIgnoreCase));
        _pageVrchatRows = _indexCounts.TryGetValue("VRChat", out var pageVrchat) ? pageVrchat : 0;
        OnPropertyChanged(nameof(CommunityStatusText));
        OnPropertyChanged(nameof(GlobalSummaryText));
        OnPropertyChanged(nameof(HasNextGlobal));
        OnPropertyChanged(nameof(HasPreviousGlobal));
        NextGlobalPageCommand.RaiseCanExecuteChanged();
        PreviousGlobalPageCommand.RaiseCanExecuteChanged();
    }

    private bool _lastVrchatPageFull;
    private bool _communitySearchPageFull;
    private int _communitySearchOffset;
    private int _communitySourceCount;
    private int _vrchatRowsInResults;
    private int _pageVrchatRows;
    private int _vrcndbPage;

    private readonly SortedDictionary<string, int> _indexCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which indexes answered the page you are looking at, how many rows each gave,
    /// and which page that was. Indexes not queried for this page are absent.
    /// </summary>
    public string CommunityStatusText
    {
        get
        {
            if (_indexCounts.Count == 0)
            {
                return string.Empty;
            }

            var parts = string.Join("   ", _indexCounts.Select(entry => $"{entry.Key} {entry.Value}"));
            var page = _lastGlobalQuery.Length == 0 ? _browsePage : _searchPage;
            return page > 1 ? $"{parts}   ·   page {page}" : parts;
        }
    }

    private int _browsePage = 1;

    private static readonly TimeSpan VrchatSearchSpacing = TimeSpan.FromSeconds(2.5);
    private DateTime _lastVrchatSearchAt = DateTime.MinValue;
    private DateTime _vrchatCooldownUntil = DateTime.MinValue;

    private bool _vrchatThrottled;

    /// <summary>True when VRChat is rate-limiting, so results are community-only.</summary>
    public bool VrchatThrottled
    {
        get => _vrchatThrottled;
        private set
        {
            if (SetProperty(ref _vrchatThrottled, value))
            {
                OnPropertyChanged(nameof(GlobalSummaryText));
            }
        }
    }

    /// <summary>Start a 90 second VRChat cooldown after a 429.</summary>
    private void StartVrchatCooldown(Exception ex)
    {
        Debug.WriteLine(ex);
        _vrchatCooldownUntil = DateTime.UtcNow.AddSeconds(90);
        VrchatThrottled = true;
        OnPropertyChanged(nameof(GlobalSummaryText));
    }

    public string GlobalSummaryText
    {
        get
        {
            if (IsGlobalBusy)
            {
                return "Searching VRChat and community indexes…";
            }

            if (GlobalResults.Count == 0)
            {
                if (_lastGlobalQuery.Length == 0)
                {
                    return "Browse all public VRChat avatars, or search by name, usr_ id or @username.";
                }

                return VrchatThrottled
                    ? "VRChat is throttling requests, so this is the community index only — and it found nothing for that term."
                    : "No avatars matched that search.";
            }

            // VRChat's avatar endpoint reports no total, so never invent one, and
            // never pass community-only counts off as if VRChat had answered.
            var count = GlobalResults.Count;
            var sources = _communitySourceCount > 0
                ? $" from {_communitySourceCount} community index{(_communitySourceCount == 1 ? string.Empty : "es")}"
                : string.Empty;
            var pagePrefix = _lastGlobalQuery.Length > 0 && _searchPage > 1
                ? $"Page {_searchPage} · "
                : string.Empty;

            if (_authorFilterName.Length > 0)
            {
                return VrchatThrottled
                    ? $"{count} avatars by {_authorFilterName} — VRChat is throttled"
                    : $"{count} avatars by {_authorFilterName}";
            }

            if (_lastGlobalQuery.Length == 0)
            {
                return $"Browsing {count} public avatars (community index)";
            }

            // Never let a community-only result set read like a normal VRChat
            // search: say plainly when VRChat supplied nothing.
            if (_vrchatRowsInResults == 0)
            {
                return pagePrefix + (VrchatThrottled
                    ? $"{count} avatars found{sources} — VRChat is throttling, so no VRChat results are included"
                    : $"{count} avatars found{sources} — VRChat returned no results (it may be throttling)");
            }

            // Per-page VRChat count, so the summary agrees with the status line.
            return _pageVrchatRows > 0
                ? pagePrefix + $"{count} avatars found ({_pageVrchatRows} from VRChat){sources}"
                : pagePrefix + $"{count} avatars found{sources}";
        }
    }

    private string _authorFilterName = string.Empty;

    private string _globalError = string.Empty;

    /// <summary>Visible on the search tab so a failed request is never silent.</summary>
    public string GlobalError
    {
        get => _globalError;
        private set => SetProperty(ref _globalError, value);
    }

    // --- "Where should this go?" picker shown when starring an avatar ---
    public ObservableCollection<FavoriteSlotChoice> FavoriteSlotChoices { get; } = new();

    private bool _isSlotPickerOpen;
    private string? _pendingFavoriteAvatarId;
    private string _pendingFavoriteAvatarName = string.Empty;

    public bool IsSlotPickerOpen
    {
        get => _isSlotPickerOpen;
        private set => SetProperty(ref _isSlotPickerOpen, value);
    }

    public string PendingFavoriteAvatarName
    {
        get => _pendingFavoriteAvatarName;
        private set => SetProperty(ref _pendingFavoriteAvatarName, value);
    }

    private string _slotPickerMessage = string.Empty;

    /// <summary>Explains why a slot cannot be used (e.g. Slots 2–4 without VRC+).</summary>
    public string SlotPickerMessage
    {
        get => _slotPickerMessage;
        private set => SetProperty(ref _slotPickerMessage, value);
    }

    public AsyncRelayCommand<string> ChooseFavoriteSlotCommand { get; }
    public RelayCommand CancelFavoriteSlotCommand { get; }

    private void BeginFavorite(string avatarId)
    {
        var avatar = FindAvatar(avatarId);
        _pendingFavoriteAvatarId = avatarId;
        PendingFavoriteAvatarName = avatar?.DisplayName ?? "this avatar";

        // Rebuilt every time so the list reflects VRC+ status right now (the
        // account can be upgraded mid-session).
        {
            FavoriteSlotChoices.Clear();
            for (var slot = 1; slot <= 4; slot++)
            {
                var locked = slot > 1 && !IsVrcPlusAccount;
                FavoriteSlotChoices.Add(new FavoriteSlotChoice(
                    $"Slot {slot}",
                    $"avatars{slot}",
                    slot == 1
                        ? "Always available"
                        : locked ? "Needs VRC+ — not available on this account" : "VRC+ slot",
                    locked));
            }

            FavoriteSlotChoices.Add(new FavoriteSlotChoice("Local (unlimited)", "local", "Kept by the app, no VRChat limit", false));
        }

        SlotPickerMessage = IsVrcPlusAccount
            ? string.Empty
            : "Slots 2–4 need VRC+. Without it they cannot be saved to VRChat — use Slot 1 or Local.";
        IsSlotPickerOpen = true;
    }

    private async Task ChooseFavoriteSlotAsync(string? tag)
    {
        var avatarId = _pendingFavoriteAvatarId;
        IsSlotPickerOpen = false;
        if (string.IsNullOrWhiteSpace(avatarId) || string.IsNullOrWhiteSpace(tag) || IsBusy)
        {
            return;
        }

        // VRChat only honours slots 2–4 for VRC+ accounts, so refuse up front
        // instead of losing the favourite silently.
        var slotNumber = tag.StartsWith("avatars", StringComparison.Ordinal) && int.TryParse(tag[7..], out var parsed)
            ? parsed
            : 0;
        if (slotNumber is > 1 and <= 4 && !IsVrcPlusAccount)
        {
            IsSlotPickerOpen = true;
            SlotPickerMessage = $"Slot {slotNumber} needs VRC+. Nothing was saved — pick Slot 1 or Local (unlimited).";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            if (string.Equals(tag, "local", StringComparison.Ordinal))
            {
                _localFavoriteIds.Add(avatarId);
                SaveLocalFavorites();
                if (FindAvatar(avatarId) is { } chosen)
                {
                    chosen.IsLocalFavorite = true;
                }
                StatusMessage = $"Saved {PendingFavoriteAvatarName} to your Local slot.";
            }
            else
            {
                // VRChat has no "move" call: drop the old record, then add the new tag.
                if (_favoriteRecords.TryGetValue(avatarId, out var existingRecord))
                {
                    await _api.RemoveFavoriteAsync(existingRecord);
                    _favoriteRecords.Remove(avatarId);
                }

                var recordId = await _api.AddAvatarFavoriteAsync(avatarId, tag);
                if (!string.IsNullOrWhiteSpace(recordId))
                {
                    _favoriteRecords[avatarId] = recordId;
                    _favoriteTags[avatarId] = new List<string> { tag };
                }

                StatusMessage = $"Saved {PendingFavoriteAvatarName} to VRChat favourite {tag[^1]}.";
            }

            var avatar = FindAvatar(avatarId);
            if (avatar is not null)
            {
                avatar.IsFavorite = _favoriteRecords.ContainsKey(avatarId);
            }

            BuildFavorites();
            OnPropertyChanged(nameof(HasFavorites));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not save that favourite slot.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public RelayCommand<string> ToggleLocalFavoriteCommand { get; }
    public RelayCommand GlobalSearchCommand { get; }
    public RelayCommand NextGlobalPageCommand { get; }
    public RelayCommand PreviousGlobalPageCommand { get; }
    public RelayCommand ClearGlobalSearchCommand { get; }
    public RelayCommand RefreshBrowseCommand { get; }

    private bool _publicOnly = true;

    /// <summary>Restrict searches and browse to public avatars (default).</summary>
    public bool PublicOnly
    {
        get => _publicOnly;
        set
        {
            if (SetProperty(ref _publicOnly, value))
            {
                if (!string.IsNullOrWhiteSpace(GlobalSearchTerm))
                {
                    _ = SearchGlobalAsync(append: false);
                }
                else if (_communityLoaded)
                {
                    PublishCommunityBrowse();
                }
            }
        }
    }

    private void RefreshBrowse()
    {
        _communityLoaded = false;
        _browsePage = 1;
        _communityCache.Clear();
        _communityOffset = 0;
        _communityPageFull = false;
        // Refreshing the browse cache must not wipe an active search.
        _ = BrowseCommunityAsync(publish: string.IsNullOrWhiteSpace(GlobalSearchTerm));
    }

    /// <summary>Single entry point so an avatar id can never appear twice.</summary>
    private bool AddGlobalResult(Avatar avatar)
    {
        if (GlobalResults.Any(existing => string.Equals(existing.Id, avatar.Id, StringComparison.Ordinal)))
        {
            return false;
        }

        ApplyState(avatar, _currentAvatarId);
        GlobalResults.Add(avatar);
        return true;
    }

    private bool MatchesPrivacy(Avatar avatar)
        => !PublicOnly || !avatar.IsPrivate;

    public string UnresolvedFavoritesText => _unresolvedFavoriteIds.Count == 1
        ? "1 favourite is not in your avatar library (public avatars you don't own)."
        : $"{_unresolvedFavoriteIds.Count} favourites are not in your avatar library (public avatars you don't own).";

    public ObservableCollection<string> UnresolvedFavoriteIds { get; } = new();

    public RelayCommand<string> OpenAvatarOnWebCommand { get; }

    /// <summary>The avatar the account was wearing when the app started.</summary>
    public Avatar? OriginalAvatar => _originalAvatarId is null
        ? null
        : _library.FirstOrDefault(avatar => string.Equals(avatar.Id, _originalAvatarId, StringComparison.Ordinal));

    public bool CanRevertAvatar => _originalAvatarId is not null &&
                                   OriginalAvatar is not null &&
                                   !string.Equals(_currentAvatarId, _originalAvatarId, StringComparison.Ordinal);

    public string OriginalAvatarName => OriginalAvatar?.DisplayName ?? "your original avatar";

    /// <summary>The avatar you were wearing before the last change.</summary>
    public Avatar? PreviousAvatar => _previousAvatarId is null
        ? null
        : _library.FirstOrDefault(avatar => string.Equals(avatar.Id, _previousAvatarId, StringComparison.Ordinal));

    public bool CanRevertPrevious => _previousAvatarId is not null &&
                                     PreviousAvatar is not null &&
                                     !string.Equals(_currentAvatarId, _previousAvatarId, StringComparison.Ordinal);

    public string PreviousAvatarName => PreviousAvatar?.DisplayName ?? "the previous avatar";

    public string ActiveSlotText => _activeSlotTag is null
        ? "All favourite slots"
        : FavoriteSlots.FirstOrDefault(slot => string.Equals(slot.Tag, _activeSlotTag, StringComparison.Ordinal))?.Label
          ?? "Favourite slot";

    public RelayCommand<FavoriteSlot> SelectSlotCommand { get; }

    public AsyncRelayCommand RevertPreviousAvatarCommand { get; }

    private void ToggleSlot(FavoriteSlot? slot)
    {
        if (slot is null)
        {
            return;
        }

        _activeSlotTag = string.Equals(_activeSlotTag, slot.Tag, StringComparison.Ordinal) ? null : slot.Tag;
        BuildFavorites();
        OnPropertyChanged(nameof(ActiveSlotText));
    }

    public int TotalCount
    {
        get => _totalCount;
        set
        {
            if (SetProperty(ref _totalCount, value))
            {
                OnPropertyChanged(nameof(SummaryText));
            }
        }
    }

    public string SummaryText
    {
        get
        {
            if (_library.Count == 0)
            {
                return HasLoaded ? "No avatars yet" : string.Empty;
            }

            if (FavoritesOnly)
            {
                return Avatars.Count == 1 ? "1 favourite avatar" : $"{Avatars.Count} favourite avatars";
            }

            if (string.IsNullOrWhiteSpace(_searchTerm))
            {
                return _library.Count == 1 ? "1 avatar" : $"{_library.Count} avatars";
            }

            return $"{Avatars.Count} of {_library.Count} avatars";
        }
    }

    /// <summary>The avatar shown in the detail view, or null for the grid.</summary>
    public Avatar? SelectedAvatar
    {
        get => _selectedAvatar;
        set
        {
            if (SetProperty(ref _selectedAvatar, value))
            {
                OnPropertyChanged(nameof(IsAvatarDetailsOpen));
            }
        }
    }

    public bool IsAvatarDetailsOpen => SelectedAvatar is not null;

    /// <summary>True once the library has loaded, so navigation does not refetch.</summary>
    public bool HasLoaded
    {
        get => _hasLoaded;
        private set => SetProperty(ref _hasLoaded, value);
    }

    public ICommand SearchCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand WearAvatarCommand { get; }
    public ICommand OpenAvatarDetailsCommand { get; }
    public ICommand CloseAvatarDetailsCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ToggleFavoritesFilterCommand { get; }
    public ICommand WearOriginalAvatarCommand { get; }

    /// <summary>Starts the library load unless it is already running or done.</summary>
    public Task EnsureLoadedAsync()
    {
        if (HasLoaded || IsBusy)
        {
            return Task.CompletedTask;
        }

        return LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            // Favourites first (one request), so stars are already correct when
            // the first page of avatars appears.
            // Favourites have their own request so the Favourites tab can fill in
            // even while the (much slower) library is still streaming.
            await EnsureFavoritesAsync();

            _currentAvatarId = _api.CurrentUser?.CurrentAvatar;
            // Remember the avatar the session started with so it can be restored.
            _originalAvatarId ??= _currentAvatarId;
            _library = new List<Avatar>();
            _publishedLibraryCount = 0;
            Avatars = new ObservableCollection<Avatar>();
            TotalCount = 0;

            // Publish every page as soon as it lands instead of waiting for the
            // whole library, so large accounts fill in progressively.
            await _api.GetAllAvatarsAsync(onPartial: PublishLibrary);

            HasLoaded = true;
            NotifySummaryChanged();
            BuildFavorites();

            // Any favourite that the library did not cover still needs details.
            _ = HydrateFavoriteAvatarsAsync();

            // Keep the detail view bound to the refreshed instance.
            if (SelectedAvatar is not null)
            {
                SelectedAvatar = _library.FirstOrDefault(avatar => avatar.Id == SelectedAvatar.Id);
            }
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
            ErrorMessage = "The avatar request timed out. Try again.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not load avatars.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenDetails(string? avatarId)
    {
        if (string.IsNullOrWhiteSpace(avatarId))
        {
            return;
        }

        StatusMessage = null;
        SelectedAvatar = FindAvatar(avatarId);
    }

    private async Task WearAvatarAsync(string? avatarId)
    {
        if (string.IsNullOrWhiteSpace(avatarId))
        {
            return;
        }

        var name = FindAvatar(avatarId)?.DisplayName ?? "that avatar";

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            _previousAvatarId = _currentAvatarId;
            await _api.SetCurrentAvatarAsync(avatarId);
            _currentAvatarId = avatarId;
            StatusMessage = $"Now wearing {name}. It is your avatar in VRChat now.";
            OnPropertyChanged(nameof(CanRevertPrevious));
            OnPropertyChanged(nameof(PreviousAvatarName));
            OnPropertyChanged(nameof(CanRevertAvatar));

            // Release the busy flag so the reload can run and mark the new avatar.
            IsBusy = false;
            await LoadAsync();
        }
                    catch (VrcApiException ex) when (ex.StatusCode == 403)
        {
            ErrorMessage = "VRChat blocked that avatar: it is private or not yours. Only the owner can wear a private avatar — use the profile link to view it instead.";
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat. Check your internet connection.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not wear that avatar.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Appends newly fetched avatars that match the current search.</summary>
    private void PublishLibrary(IReadOnlyList<Avatar> library)
    {
        _library = library.ToList();

        for (var index = _publishedLibraryCount; index < _library.Count; index++)
        {
            var avatar = _library[index];
            ApplyState(avatar, _currentAvatarId);
            if (Matches(avatar))
            {
                Avatars.Add(avatar);
            }
        }

        _publishedLibraryCount = _library.Count;
        UpdateSummary();
    }

    /// <summary>Rebuilds the visible grid from the library for the current query.</summary>
    private void ApplyFilter()
    {
        Avatars = new ObservableCollection<Avatar>(_library.Where(Matches));
        UpdateSummary();
    }

    private bool Matches(Avatar avatar)
    {
        if (FavoritesOnly && !avatar.IsFavorite)
        {
            return false;
        }

        var term = _searchTerm.Trim();
        if (term.Length == 0)
        {
            return true;
        }

        return avatar.DisplayName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
               || avatar.AuthorName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
               || avatar.Tags.Any(tag => tag.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }

    private void UpdateSummary()
    {
        TotalCount = Avatars.Count;
        NotifySummaryChanged();
    }

    private void NotifySummaryChanged() => OnPropertyChanged(nameof(SummaryText));

    private void ApplyState(Avatar avatar, string? currentAvatarId)
    {
        avatar.IsWearing = !string.IsNullOrWhiteSpace(currentAvatarId) &&
                           string.Equals(avatar.Id, currentAvatarId, StringComparison.Ordinal);
        avatar.IsFavorite = _favoriteRecords.ContainsKey(avatar.Id);
    }

    /// <summary>Adds or removes the avatar favourite, depending on its current state.</summary>
    private async Task ToggleFavoriteAsync(string? avatarId)
    {
        if (string.IsNullOrWhiteSpace(avatarId))
        {
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            if (_favoriteRecords.TryGetValue(avatarId, out var recordId))
            {
                await _api.RemoveFavoriteAsync(recordId);
                _favoriteRecords.Remove(avatarId);
                _favoriteTags.Remove(avatarId);
                StatusMessage = "Removed from favourites.";

                var favorite = _favoriteRecords.ContainsKey(avatarId);
                foreach (var item in _library.Where(item => string.Equals(item.Id, avatarId, StringComparison.Ordinal)))
                {
                    item.IsFavorite = favorite;
                }

                UpdateSummary();
                BuildFavorites();
                if (FavoritesOnly)
                {
                    ApplyFilter();
                }

                return;
            }

            // Not favourited yet: ask which slot it should live in.
            BeginFavorite(avatarId);
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat. Check your internet connection.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not update that avatar's favorite state.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Rebuilds the favourites tab: library matches, slot counts, unresolved ids.</summary>
    private void BuildFavorites()
    {
        FavoriteAvatars.Clear();
        var tag = _activeSlotTag;
        var shown = new List<Avatar>();

        var favoriteIds = new List<string>(_favoriteRecords.Keys);
        foreach (var localId in _localFavoriteIds.Where(id => !_favoriteRecords.ContainsKey(id)))
        {
            favoriteIds.Add(localId);
        }

        foreach (var favoriteId in favoriteIds)
        {
            var isLocal = _localFavoriteIds.Contains(favoriteId);
            if (tag == "local" ? !isLocal : tag is not null && !SlotTags(favoriteId).Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // Favourites are often public avatars from the catalog, not your own
            // library, so fall back to the details fetched for the favourite.
            var avatar = _library.FirstOrDefault(item => string.Equals(item.Id, favoriteId, StringComparison.Ordinal))
                         ?? (_favoriteAvatars.TryGetValue(favoriteId, out var fetched) ? fetched : null);
            if (avatar is null)
            {
                continue;
            }

            ApplyState(avatar, _currentAvatarId);
            avatar.IsLocalFavorite = _localFavoriteIds.Contains(avatar.Id);
            shown.Add(avatar);
        }

        foreach (var avatar in shown.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            FavoriteAvatars.Add(avatar);
        }

        UnresolvedFavoriteIds.Clear();
        var libraryIds = new HashSet<string>(_library.Select(avatar => avatar.Id), StringComparer.Ordinal);
        foreach (var id in _favoriteRecords.Keys
                     .Where(id => !libraryIds.Contains(id) && !_favoriteAvatars.ContainsKey(id))
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            UnresolvedFavoriteIds.Add(id);
        }

        FavoriteSlots.Clear();
        for (var slot = 1; slot <= 4; slot++)
        {
            var slotTag = $"avatars{slot}";
            var count = _favoriteTags.Values.Count(tags => tags.Any(value => string.Equals(value, slotTag, StringComparison.OrdinalIgnoreCase)));
            var isActive = string.Equals(_activeSlotTag, slotTag, StringComparison.Ordinal);
            FavoriteSlots.Add(new FavoriteSlot($"Slot {slot}", count, slotTag, isActive));
        }

        FavoriteSlots.Add(new FavoriteSlot(
            "Local (unlimited)",
            _localFavoriteIds.Count,
            "local",
            string.Equals(_activeSlotTag, "local", StringComparison.Ordinal)));

        OnPropertyChanged(nameof(FavoritesSummaryText));
        OnPropertyChanged(nameof(VrcPlusFavoritesText));
        OnPropertyChanged(nameof(IsVrcPlusAccount));
        OnPropertyChanged(nameof(HasUnresolvedFavorites));
        OnPropertyChanged(nameof(UnresolvedFavoritesText));
        OnPropertyChanged(nameof(ActiveSlotText));
    }

    /// <summary>
    /// Fetches details for favourites that are not in the avatar library, in small
    /// parallel batches (VRChat's own details endpoint caches per id, so a second
    /// visit to the tab is instant).
    /// </summary>
    private async Task HydrateFavoriteAvatarsAsync()
    {
        var libraryIds = new HashSet<string>(_library.Select(avatar => avatar.Id), StringComparer.Ordinal);
        var ids = new HashSet<string>(_favoriteRecords.Keys, StringComparer.Ordinal);
        ids.UnionWith(_localFavoriteIds);

        var missing = ids
            .Where(id => !libraryIds.Contains(id) && !_favoriteAvatars.ContainsKey(id) && !_failedFavoriteIds.Contains(id))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        IsLoadingFavorites = true;
        OnPropertyChanged(nameof(FavoritesLoadingText));
        try
        {
            for (var start = 0; start < missing.Count; start += FavoriteFetchBatch)
            {
                var batch = missing.Skip(start).Take(FavoriteFetchBatch).ToList();
                var fetched = await Task.WhenAll(batch.Select(async id =>
                {
                    try
                    {
                        return (Id: id, Avatar: await _api.GetAvatarAsync(id));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                        return (Id: id, Avatar: (Avatar?)null);
                    }
                }));

                foreach (var (id, avatar) in fetched)
                {
                    if (avatar is null)
                    {
                        _failedFavoriteIds.Add(id);
                    }
                    else
                    {
                        _favoriteAvatars[id] = avatar;
                    }
                }

                BuildFavorites();
                OnPropertyChanged(nameof(FavoritesLoadingText));
            }
        }
        finally
        {
            IsLoadingFavorites = false;
            OnPropertyChanged(nameof(FavoritesLoadingText));
            BuildFavorites();
        }
    }

    /// <summary>Loads the account's favourites without waiting for the avatar library.</summary>
    public async Task EnsureFavoritesAsync()
    {
        if (_favoritesLoaded || _favoritesLoading)
        {
            return;
        }

        _favoritesLoading = true;
        OnPropertyChanged(nameof(FavoritesLoadingText));
        try
        {
            var favorites = await _api.GetAvatarFavoritesAsync();
            _favoriteRecords.Clear();
            _favoriteTags.Clear();
            foreach (var favorite in favorites)
            {
                _favoriteRecords[favorite.FavoriteId] = favorite.Id;
                _favoriteTags[favorite.FavoriteId] = favorite.Tags;
            }

            _favoritesLoaded = true;
            BuildFavorites();
            _ = HydrateFavoriteAvatarsAsync();
        }
        catch (VrcApiException ex)
        {
            FavoritesError = ex.Message;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            FavoritesError = "Could not load your avatar favourites.";
        }
        finally
        {
            _favoritesLoading = false;
            OnPropertyChanged(nameof(FavoritesLoadingText));
            OnPropertyChanged(nameof(HasFavorites));
        }
    }

    private string? _favoritesError;

    public string? FavoritesError
    {
        get => _favoritesError;
        private set => SetProperty(ref _favoritesError, value);
    }

    // --- Local favourites: an unlimited, app-side slot VRChat does not cap ---
    private static string LocalFavoritesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcCompanion",
        "local-favorites.json");

    private void LoadLocalFavorites()
    {
        try
        {
            var path = LocalFavoritesPath;
            if (!File.Exists(path))
            {
                return;
            }

            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            if (ids is not null)
            {
                foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)))
                {
                    _localFavoriteIds.Add(id);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void SaveLocalFavorites()
    {
        try
        {
            var path = LocalFavoritesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_localFavoriteIds.OrderBy(id => id).ToList()));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void ToggleLocalFavorite(string? avatarId)
    {
        if (string.IsNullOrWhiteSpace(avatarId))
        {
            return;
        }

        if (!_localFavoriteIds.Remove(avatarId))
        {
            _localFavoriteIds.Add(avatarId);
        }

        SaveLocalFavorites();
        var savedLocal = _localFavoriteIds.Contains(avatarId);
        StatusMessage = savedLocal
            ? "Saved to your Local slot. It is kept by the app, not VRChat."
            : "Removed from your Local slot.";

        // Update every live list (Search results, favourites) so the pin glyph
        // flips immediately instead of waiting for a re-search.
        FindAvatar(avatarId)!.IsLocalFavorite = savedLocal;
        foreach (var item in GlobalResults.Where(item => string.Equals(item.Id, avatarId, StringComparison.Ordinal)))
        {
            item.IsLocalFavorite = savedLocal;
        }

        foreach (var item in FavoriteAvatars.Where(item => string.Equals(item.Id, avatarId, StringComparison.Ordinal)))
        {
            item.IsLocalFavorite = savedLocal;
        }

        BuildFavorites();
        _ = HydrateFavoriteAvatarsAsync();
        OnPropertyChanged(nameof(HasFavorites));
    }

    private async Task SearchGlobalAsync(bool append)
    {
        if (IsGlobalBusy)
        {
            return;
        }

        // Claim the tab before the first await, so a second click (or an @name
        // lookup that takes a moment) cannot start a parallel search.
        IsGlobalBusy = true;
        try
        {
        if (!append)
        {
            _globalPage = 1;
            GlobalResults.Clear();
            _communitySearchOffset = 0;
            _communitySearchPageFull = false;
            _vrchatRowsInResults = 0;
        }
        else
        {
            _globalPage++;
        }

        GlobalError = string.Empty;
        _lastGlobalQuery = GlobalSearchTerm.Trim();
        if (_lastGlobalQuery.Length == 0)
        {
            return;
        }

        var term = _lastGlobalQuery;
        var authorId = string.Empty;
        _authorFilterName = string.Empty;

        if (!append && term.Length > 0)
        {
            // "usr_..." searches that person's avatars; "@name" resolves the user.
            if (term.StartsWith("usr_", StringComparison.OrdinalIgnoreCase))
            {
                authorId = term;
                _authorFilterName = term;
            }
            else if (term.StartsWith('@') && term.Length > 1)
            {
                var user = await _api.SearchUsersAsync(term[1..]);
                if (user is not null)
                {
                    authorId = user.Id;
                    _authorFilterName = user.DisplayName;
                }
            }
        }

            // Stay inside VRChat's rate limit: never fire avatar searches back to
            // back, and do not call it at all while it is cooling down after a 429.
            if (DateTime.UtcNow < _vrchatCooldownUntil)
            {
                VrchatThrottled = true;
            }
            else
            {
                var gap = _lastVrchatSearchAt + VrchatSearchSpacing - DateTime.UtcNow;
                if (gap > TimeSpan.Zero)
                {
                    await Task.Delay(gap);
                }

                _lastVrchatSearchAt = DateTime.UtcNow;
                try
                {
                    var result = await _api.SearchAvatarsAsync(new AvatarFilter
                    {
                        SearchTerm = authorId.Length > 0 ? string.Empty : _lastGlobalQuery,
                        AuthorId = authorId,
                        Page = _globalPage,
                        PageSize = 50,
                        // Ask VRChat for everything and filter locally: releaseStatus=public
                        // makes its marketplace search return far fewer rows than the same
                        // query unfiltered, which silently emptied results.
                        ReleaseStatus = "all"
                    });

                    foreach (var avatar in result.Avatars.Where(MatchesPrivacy))
                    {
                        if (AddGlobalResult(avatar))
                        {
                            _vrchatRowsInResults++;
                        }
                    }

                    _lastVrchatPageFull = result.Avatars.Count >= 50;
                    if (result.Avatars.Count == 0 && append)
                    {
                        _globalPage--;
                    }

                    if (result.Avatars.Count > 0)
                    {
                        VrchatThrottled = false;
                    }
                }
                catch (VrcApiException ex) when (ex.StatusCode == 429)
                {
                    StartVrchatCooldown(ex);
                }
            }

            // One combined search: VRChat's own results plus the community public
            // index, which covers far more avatars than the API exposes.
            if (_lastGlobalQuery.Length > 0)
            {
                if (!append)
                {
                    _communitySearchOffset = 0;
                }

                // @name / usr_ searches must not stop at VRChat: ask the community
                // providers by author id as well.
                if (authorId.Length > 0)
                {
                    var byAuthor = await _api.SearchCommunityByAuthorAsync(authorId);
                    _indexCounts.Clear();
                    foreach (var group in byAuthor.GroupBy(a => a.SourceName, StringComparer.OrdinalIgnoreCase))
                    {
                        _indexCounts[group.Key] = group.Count();
                    }

                    OnPropertyChanged(nameof(CommunityStatusText));
                    foreach (var avatar in byAuthor.Where(MatchesPrivacy))
                    {
                        AddGlobalResult(avatar);
                    }

                    return;
                }

                List<Avatar> community;
                if (append)
                {
                    // VRCNDb has its own page counter: keep its big index contributing
                    // past page one instead of repeating page 1.
                    _vrcndbPage++;
                    community = await _api.SearchCommunityNextPageAsync(
                        _lastGlobalQuery,
                        _vrcndbPage,
                        _communitySearchOffset);
                    _communitySearchOffset += 25;
                }
                else
                {
                    _indexCounts.Clear();
                    _vrcndbPage = 1;
                    // VRCX providers cannot page, so ask for a wider first batch;
                    // only VRCNDb and avtr.icu can continue on later pages.
                    community = await _api.SearchAllCommunityAvatarsAsync(
                        _lastGlobalQuery,
                        providerLimit: 80);
                    _communitySearchOffset = 25;
                }

                OnPropertyChanged(nameof(CommunityStatusText));
                _communitySearchPageFull = community.Count >= 25;
                foreach (var avatar in community.Where(MatchesPrivacy))
                {
                    AddGlobalResult(avatar);
                }

            }
        }
        catch (VrcApiException ex)
        {
            Debug.WriteLine(ex);
            GlobalError = $"VRChat refused the search ({ex.StatusCode}): {ex.Message}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            GlobalError = "Could not search VRChat avatars right now.";
        }
        finally
        {
            IsGlobalBusy = false;
            OnPropertyChanged(nameof(GlobalSummaryText));
            OnPropertyChanged(nameof(HasNextGlobal));
            OnPropertyChanged(nameof(HasPreviousGlobal));
        }
    }

    /// <summary>
    /// Fills the tab with real public avatars on open. VRChat's own endpoint only
    /// returns your library/marketplace results for an ordinary account, so the
    /// community index (avtr.icu) is the browse source.
    /// </summary>
    // Cached community browse pages, so tab switches and paging never refetch
    // what is already on screen.
    private readonly List<Avatar> _communityCache = new();
    private bool _communityLoaded;
    private bool _communityPageFull;
    private int _communityOffset;

    public void BrowseCatalog()
    {
        if (IsGlobalBusy)
        {
            return;
        }

        // An active search owns the tab: come back to the same results, never the
        // browse list. Only refetch when there is nothing to show.
        if (!string.IsNullOrWhiteSpace(GlobalSearchTerm))
        {
            if (GlobalResults.Count == 0)
            {
                _ = SearchGlobalAsync(append: false);
            }

            return;
        }

        if (_communityLoaded)
        {
            // Reuse the cached pages: no request on tab switch.
            PublishCommunityBrowse();
            return;
        }

        _ = BrowseCommunityAsync();
    }

    private async Task BrowseCommunityAsync(bool publish = true)
    {
        IsGlobalBusy = true;
        try
        {
            var page = await _api.GetCommunityLatestAsync(50, 0);
            _communityCache.Clear();
            _communityCache.AddRange(page);
            _communityOffset = page.Count;
            _communityPageFull = page.Count >= 50;
            _communityLoaded = true;
            _browsePage = 1;
            _indexCounts.Clear();
            _indexCounts["avtr.icu"] = page.Count;
            OnPropertyChanged(nameof(CommunityStatusText));
            if (publish)
            {
                PublishCommunityBrowse();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            if (publish)
            {
                GlobalError = "Could not reach the community avatar index.";
            }
        }
        finally
        {
            IsGlobalBusy = false;
            OnPropertyChanged(nameof(GlobalSummaryText));
            OnPropertyChanged(nameof(HasNextGlobal));
            OnPropertyChanged(nameof(HasPreviousGlobal));
        }
    }

    private async Task LoadMoreCommunityAsync()
    {
        if (IsGlobalBusy || !_communityLoaded)
        {
            return;
        }

        IsGlobalBusy = true;
        try
        {
            var page = await _api.GetCommunityLatestAsync(50, _communityOffset);
            _communityOffset += page.Count;
            _communityPageFull = page.Count >= 50;
            var added = 0;
            foreach (var avatar in page)
            {
                if (!_communityCache.Any(cached => string.Equals(cached.Id, avatar.Id, StringComparison.Ordinal)))
                {
                    _communityCache.Add(avatar);
                    added++;
                }
            }

            if (page.Count == 0)
            {
                GlobalError = "No more public avatars in the community index.";
            }

            // Keep the index status in step with the browse page just fetched, and
            // report only the avatars that were actually new on screen.
            _browsePage++;
            _indexCounts.Clear();
            _indexCounts["avtr.icu"] = added;
            OnPropertyChanged(nameof(CommunityStatusText));
            PublishCommunityBrowse();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            GlobalError = "Could not load more community avatars.";
        }
        finally
        {
            IsGlobalBusy = false;
            OnPropertyChanged(nameof(GlobalSummaryText));
            OnPropertyChanged(nameof(HasNextGlobal));
            OnPropertyChanged(nameof(HasPreviousGlobal));
        }
    }

        private void PublishCommunityBrowse()
    {
        GlobalResults.Clear();
        foreach (var avatar in _communityCache.Where(MatchesPrivacy))
        {
            AddGlobalResult(avatar);
        }

        _lastGlobalQuery = string.Empty;
        _authorFilterName = string.Empty;
        _lastVrchatPageFull = _communityPageFull;
        GlobalError = string.Empty;
        OnPropertyChanged(nameof(GlobalSummaryText));
    }

    private void ClearGlobalSearch()
    {
        GlobalSearchTerm = string.Empty;
        _lastGlobalQuery = string.Empty;
        _authorFilterName = string.Empty;
        _globalPage = 0;
        _lastVrchatPageFull = false;
        GlobalResults.Clear();

        if (_communityLoaded)
        {
            PublishCommunityBrowse();
        }
        OnPropertyChanged(nameof(GlobalSummaryText));
    }

    private Avatar? FindAvatar(string avatarId)
        => _library.FirstOrDefault(avatar => string.Equals(avatar.Id, avatarId, StringComparison.Ordinal))
           ?? FavoriteAvatars.FirstOrDefault(avatar => string.Equals(avatar.Id, avatarId, StringComparison.Ordinal))
           ?? GlobalResults.FirstOrDefault(avatar => string.Equals(avatar.Id, avatarId, StringComparison.Ordinal))
           ?? (_favoriteAvatars.TryGetValue(avatarId, out var fetched) ? fetched : null);

    private IReadOnlyList<string> SlotTags(string avatarId)
        => _favoriteTags.TryGetValue(avatarId, out var tags) ? tags : Array.Empty<string>();

    private void OpenAvatarOnWeb(string? avatarId)
    {
        if (string.IsNullOrWhiteSpace(avatarId))
        {
            return;
        }

        try
        {
            LaunchService.OpenUrl($"https://vrchat.com/home/avatar/{avatarId}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Couldn't open that avatar in your browser.";
        }
    }

    private void ToggleFavoritesFilter()
    {
        FavoritesOnly = !FavoritesOnly;
    }

    private void ClearSearch()
    {
        if (string.IsNullOrEmpty(_searchTerm))
        {
            ApplyFilter();
            return;
        }

        _searchTerm = string.Empty;
        OnPropertyChanged(nameof(SearchTerm));
        ApplyFilter();
    }

    /// <summary>
    /// Clears everything that belongs to the previous account, including the
    /// favourites cache, so nothing leaks across an account switch.
    /// </summary>
    public void Reset()
    {
        _favoriteRecords.Clear();
        _favoriteTags.Clear();
        _favoriteAvatars.Clear();
        _failedFavoriteIds.Clear();
        _favoritesLoaded = false;
        _unresolvedFavoriteIds.Clear();
        FavoriteAvatars.Clear();
        FavoriteSlots.Clear();
        UnresolvedFavoriteIds.Clear();
        _library = new List<Avatar>();
        _publishedLibraryCount = 0;
        _currentAvatarId = null;
        _searchTerm = string.Empty;
        OnPropertyChanged(nameof(SearchTerm));
        SelectedAvatar = null;
        Avatars = new ObservableCollection<Avatar>();
        TotalCount = 0;
        HasLoaded = false;
        _originalAvatarId = null;
        _previousAvatarId = null;
        _activeSlotTag = null;
        FavoritesOnly = false;
        ErrorMessage = null;
        StatusMessage = null;
        NotifySummaryChanged();
        BuildFavorites();
    }
}

/// <summary>One VRChat avatar favourite slot (avatars1..avatars4).</summary>
/// <summary>One option in the "which slot?" picker shown when favouriting.</summary>
public sealed class FavoriteSlotChoice
{
    public FavoriteSlotChoice(string label, string tag, string hint, bool isLocked)
    {
        Label = label;
        Tag = tag;
        Hint = hint;
        IsLocked = isLocked;
    }

    public string Label { get; }

    public string Tag { get; }

    public string Hint { get; }

    /// <summary>Not usable on this account (VRC+ slots without VRC+).</summary>
    public bool IsLocked { get; }
}

public sealed class FavoriteSlot
{
    public FavoriteSlot(string label, int count, string tag, bool isActive)
    {
        Label = label;
        Count = count;
        Tag = tag;
        IsActive = isActive;
    }

    public string Label { get; }

    public int Count { get; }

    /// <summary>The VRChat favourite group this slot maps to (avatars1..avatars4).</summary>
    public string Tag { get; }

    public bool IsActive { get; }

    public string Summary => Count == 1 ? "1 avatar" : $"{Count} avatars";
}
