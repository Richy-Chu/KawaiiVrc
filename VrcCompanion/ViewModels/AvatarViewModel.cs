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
using System.Windows.Threading;
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
    private bool _isWearingAvatar;
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
    private int _favoritesGeneration;
    private bool _favoritesLoading;

    // The in-flight favourites load, if any. It replaces every record, so an
    // add or remove has to wait for it before writing.
    private Task? _favoritesLoad;
    // Each favourite needs its own avatar call, so the list used to fill in
    // groups of six while the rest waited their turn. Sixteen at a time keeps
    // well inside the API's limits and cuts the wait for a large list by more
    // than half.
    private const int FavoriteFetchBatch = 16;

    // The account's complete avatar library; the grid shows a filtered view of it.
    private List<Avatar> _library = new();
    private string? _currentAvatarId;
    private string? _originalAvatarId;
    private string? _previousAvatarId;
    private string? _activeSlotTag;
    private bool _favoritesOnly;
    private FilterOption _selectedAvatarSort = null!;
    private FilterOption _selectedFavoriteSort = null!;
    private bool _isAvatarSortOpen;
    private bool _isFavoriteSortOpen;
    private CancellationTokenSource? _avatarOscCancellation;

    private readonly List<IRelayCommand> _commands = new();

    public AvatarViewModel(VrcApiClient api)
    {
        _api = api;

        SearchCommand = Track(new RelayCommand(ApplyFilter));
        ShowMoreAvatarsCommand = Track(new RelayCommand(ShowMoreAvatars, () => HasMoreAvatars));
        RefreshCommand = Track(new AsyncRelayCommand(LoadAsync, () => !IsBusy));
        ToggleFavoriteCommand = Track(new AsyncRelayCommand<string>(ToggleFavoriteAsync, _ => !IsBusy));
        WearAvatarCommand = Track(new AsyncRelayCommand<string>(WearAvatarAsync, _ => !IsWearingAvatar));
        OpenAvatarDetailsCommand = Track(new RelayCommand<string>(OpenDetails));
        CloseAvatarDetailsCommand = Track(new RelayCommand(CloseAvatarDetails));
        ClearSearchCommand = Track(new RelayCommand(ClearSearch));
        ToggleFavoritesFilterCommand = Track(new RelayCommand(ToggleFavoritesFilter));
        OpenAvatarOnWebCommand = Track(new RelayCommand<string>(OpenAvatarOnWeb, id => !string.IsNullOrWhiteSpace(id)));
        SelectSlotCommand = Track(new RelayCommand<FavoriteSlot>(ToggleSlot, slot => slot is not null));
        ChooseFavoriteSlotCommand = Track(new AsyncRelayCommand<string>(ChooseFavoriteSlotAsync, tag => !string.IsNullOrWhiteSpace(tag)));
        CancelFavoriteSlotCommand = Track(new RelayCommand(() => IsSlotPickerOpen = false));
        RevertPreviousAvatarCommand = Track(new AsyncRelayCommand(
            () => WearAvatarAsync(_previousAvatarId),
            () => !IsWearingAvatar && CanRevertPrevious));
        WearOriginalAvatarCommand = Track(new AsyncRelayCommand(
            () => WearAvatarAsync(_originalAvatarId),
            () => !IsWearingAvatar && CanRevertAvatar));
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

        ToggleProviderFilterCommand = Track(new RelayCommand(
            () => IsProviderFilterOpen = !IsProviderFilterOpen));
        SelectAllProvidersCommand = Track(new RelayCommand(() => SetProviderSelection(true)));
        SelectNoProvidersCommand = Track(new RelayCommand(() => SetProviderSelection(false)));
        ToggleAvatarSortCommand = Track(new RelayCommand(
            () => IsAvatarSortOpen = !IsAvatarSortOpen));
        ToggleFavoriteSortCommand = Track(new RelayCommand(
            () => IsFavoriteSortOpen = !IsFavoriteSortOpen));

        AvatarSortOptions = new ObservableCollection<FilterOption>
        {
            new("date-added", "Date Added"),
            new("last-updated", "Last Updated"),
            new("name", "Name"),
            new("recently-used", "Recently Used"),
            new("performance", "Performance")
        };
        _selectedAvatarSort = ResolveAvatarSort(SettingsStore.Shared.Settings.UploadedAvatarSort);
        _selectedFavoriteSort = ResolveAvatarSort(SettingsStore.Shared.Settings.FavoriteAvatarSort);

        foreach (var providerName in VrcApiClient.CommunityProviderNames)
        {
            ProviderFilterOptions.Add(new ProviderFilterOption(providerName, OnProviderFilterChanged));
        }

        SetProviderSelection(true);

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

    /// <summary>True only while an avatar switch is in flight, never during a library load.</summary>
    public bool IsWearingAvatar
    {
        get => _isWearingAvatar;
        private set
        {
            if (SetProperty(ref _isWearingAvatar, value))
            {
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

    /// <summary>The game's five avatar sort modes, in its menu order.</summary>
    public ObservableCollection<FilterOption> AvatarSortOptions { get; }

    public FilterOption SelectedAvatarSort
    {
        get => _selectedAvatarSort;
        set
        {
            if (value is null || !SetProperty(ref _selectedAvatarSort, value))
            {
                return;
            }

            SettingsStore.Shared.Settings.UploadedAvatarSort = value.Key;
            SettingsStore.Shared.Save();
            IsAvatarSortOpen = false;
            ApplyFilter();
        }
    }

    public FilterOption SelectedFavoriteSort
    {
        get => _selectedFavoriteSort;
        set
        {
            if (value is null || !SetProperty(ref _selectedFavoriteSort, value))
            {
                return;
            }

            SettingsStore.Shared.Settings.FavoriteAvatarSort = value.Key;
            SettingsStore.Shared.Save();
            IsFavoriteSortOpen = false;
            BuildFavorites();
        }
    }

    public bool IsAvatarSortOpen
    {
        get => _isAvatarSortOpen;
        set => SetProperty(ref _isAvatarSortOpen, value);
    }

    public bool IsFavoriteSortOpen
    {
        get => _isFavoriteSortOpen;
        set => SetProperty(ref _isFavoriteSortOpen, value);
    }

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

    public RangeObservableCollection<Avatar> GlobalResults { get; } = new();

    /// <summary>Raised after a search batch adds public avatar rows.</summary>
    public event Action? GlobalResultsChanged;

    /// <summary>Raised only after VRChat accepts a new avatar for this account.</summary>
    public event Action<Avatar>? AvatarWorn;

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

    /// <summary>
    /// New avatars each loaded browse page contributed, so stepping back can show
    /// exactly the pages already fetched instead of the whole growing cache.
    /// </summary>
    private readonly List<int> _browsePageNewCounts = new();

    /// <summary>
    /// Per page, what each index answered: rows, or -1 when that index failed.
    /// Kept per page so the status line can name the indexes that did not respond.
    /// </summary>
    private readonly List<Dictionary<string, int>> _pageProbes = new();

    /// <summary>
    /// Probe filled by the fetch currently in flight (see _pageProbes). Parallel
    /// provider fetches merge into it after their awaits, so a plain map is safe.
    /// </summary>
    private Dictionary<string, int> _currentProbe = new(StringComparer.OrdinalIgnoreCase);

    private int _searchPage;

    public bool HasNextGlobal => !IsGlobalBusy &&
        (_lastGlobalQuery.Length == 0
            ? _communityPageFull
            : _searchPage < _searchPages.Count || _lastVrchatPageFull || _communitySearchPageFull);

    /// <summary>
    /// Bumped whenever the visible results page changes (browse or search); the view
    /// scrolls to the top on each change so a new page never opens mid-list.
    /// </summary>
    public int ResultsPageToken => _browsePageToken;

    private int _browsePageToken;

    private void BumpResultsPageToken()
    {
        _browsePageToken++;
        OnPropertyChanged(nameof(ResultsPageToken));
    }

    public bool HasPreviousGlobal => !IsGlobalBusy &&
        (_lastGlobalQuery.Length == 0
            ? _communityLoaded && _browsePage > 1
            : _searchPage > 1);

    /// <summary>Short-lived cache so repeating a query does not re-hit every index.</summary>
    internal sealed class CachedSearch
    {
        public List<List<Avatar>> Pages { get; init; } = new();
        public DateTime FetchedAt { get; init; }
        public int VrchatRows { get; init; }
        public bool VrchatMore { get; init; }
        public bool CommunityMore { get; init; }
        public string AuthorName { get; init; } = string.Empty;
        public List<Dictionary<string, int>> PageProbes { get; init; } = new();
    }

    private readonly Dictionary<string, CachedSearch> _searchCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(4);

    private string ProviderFilterCacheKey => ProviderFilterOptions.All(option => option.IsSelected)
        ? "all"
        : string.Join(",", ProviderFilterOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

    private string SearchCacheKey =>
        $"{GlobalSearchTerm.Trim().ToLowerInvariant()}|{PublicOnly}|{ProviderFilterCacheKey}";

    private async Task StartSearchAsync()
    {
        if (IsGlobalBusy)
        {
            return;
        }

        _searchPages.Clear();
        _pageProbes.Clear();
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
            // Failures are transient facts about one request, not about the page, so
            // a cached restore drops them rather than claiming an index is still down.
            _pageProbes.AddRange(cached.PageProbes.Select(HealthOnlyProbe));
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
            AddResultChunks(GlobalResults.ToList(), _currentProbe);
            _searchPage = 1;
            PublishStoredPage();
            StoreSearchCache();
        }
    }

    /// <summary>
    /// Probe without failure entries: safe to show from cache, where a failure
    /// recorded minutes ago may no longer be true.
    /// </summary>
    internal static Dictionary<string, int> HealthOnlyProbe(IReadOnlyDictionary<string, int> probe)
    {
        var healthy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in probe)
        {
            if (entry.Value >= 0)
            {
                healthy[entry.Key] = entry.Value;
            }
        }

        return healthy;
    }

    private const int GlobalPageSize = 50;

    /// <summary>Splits fetched rows into 50-card pages for Back/Next.</summary>
    private void AddResultChunks(List<Avatar> avatars, IReadOnlyDictionary<string, int>? probe = null)
    {
        for (var start = 0; start < avatars.Count; start += GlobalPageSize)
        {
            _searchPages.Add(avatars
                .Skip(start)
                .Take(GlobalPageSize)
                .ToList());
        }

        // Every chunk from this fetch shares the same "what each index answered".
        while (_pageProbes.Count < _searchPages.Count)
        {
            _pageProbes.Add(probe is null
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(probe, StringComparer.OrdinalIgnoreCase));
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

    /// <summary>Test seam: preloads the cache for the current search key.</summary>
    internal void SeedSearchCache(CachedSearch cached) => _searchCache[SearchCacheKey] = cached;

    /// <summary>
    /// Test seam: replaces the browse fetch (offset in, rows out) for both the
    /// first page and "load more", so their failure branches can be exercised.
    /// Null uses the real community index call.
    /// </summary>
    internal Func<int, CancellationToken, Task<List<Avatar>>>? BrowseFetch { get; set; }

    private void StoreSearchCache()
    {
        _searchCache[SearchCacheKey] = new CachedSearch
        {
            Pages = _searchPages.Select(page => page.ToList()).ToList(),
            FetchedAt = DateTime.UtcNow,
            VrchatRows = _vrchatRowsInResults,
            VrchatMore = _lastVrchatPageFull,
            CommunityMore = _communitySearchPageFull,
            AuthorName = _authorFilterName,
            PageProbes = _pageProbes
                .Select(probe => new Dictionary<string, int>(probe, StringComparer.OrdinalIgnoreCase))
                .ToList()
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
            // Already-loaded pages are shown from the cache without a request.
            if (_communityLoaded && _browsePage < _browsePageNewCounts.Count)
            {
                _browsePage++;
                PublishCommunityBrowse();
                ApplyBrowseStatusForPage();
                return;
            }

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

        AddResultChunks(added, _currentProbe);
        _searchPage = Math.Min(_searchPage + 1, _searchPages.Count);
        PublishStoredPage();
        StoreSearchCache();
    }

    private void PreviousPage()
    {
        if (IsGlobalBusy)
        {
            return;
        }

        if (_lastGlobalQuery.Length == 0)
        {
            if (!_communityLoaded || _browsePage <= 1)
            {
                return;
            }

            _browsePage--;
            PublishCommunityBrowse();
            ApplyBrowseStatusForPage();
            return;
        }

        if (_searchPage <= 1)
        {
            return;
        }

        _searchPage--;
        PublishStoredPage();
    }

    /// <summary>Status line for the browse page being shown (already-loaded pages only).</summary>
    private void ApplyBrowseStatusForPage()
    {
        _indexCounts.Clear();
        _failedIndexes.Clear();
        SetBrowseStatusCounts(_browsePage);

        BumpResultsPageToken();
        OnPropertyChanged(nameof(CommunityStatusText));
        OnPropertyChanged(nameof(GlobalSummaryText));
        OnPropertyChanged(nameof(HasNextGlobal));
        OnPropertyChanged(nameof(HasPreviousGlobal));
        NextGlobalPageCommand.RaiseCanExecuteChanged();
        PreviousGlobalPageCommand.RaiseCanExecuteChanged();
    }

    private void PublishStoredPage()
    {
        // A stored page is always a page change, so start it at the top.
        BumpResultsPageToken();
        GlobalResults.ReplaceAll(
            _searchPage >= 1 && _searchPage <= _searchPages.Count
                ? _searchPages[_searchPage - 1]
                : Array.Empty<Avatar>());

        // Status counts unique avatars actually shown on this page, per index, so
        // an index can never claim rows that de-duplication removed.
        _indexCounts.Clear();
        _failedIndexes.Clear();
        if (_searchPage >= 1 && _searchPage <= _searchPages.Count)
        {
            foreach (var entry in CountBySource(_searchPages[_searchPage - 1]))
            {
                _indexCounts[entry.Key] = entry.Value;
            }
        }

        // Indexes that were asked but did not answer usefully are still named.
        if (_searchPage >= 1 && _searchPage <= _pageProbes.Count)
        {
            foreach (var entry in _pageProbes[_searchPage - 1])
            {
                if (entry.Value < 0)
                {
                    _failedIndexes.Add(entry.Key);
                }
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
    private readonly List<string> _failedIndexes = new();

    /// <summary>
    /// What each index did for the page you are looking at: rows it contributed, or
    /// "failed" when it was queried but did not answer usefully. Indexes that were
    /// not queried for this page are absent.
    /// </summary>
    public string CommunityStatusText
    {
        get
        {
            var text = FormatIndexStatus(
                _indexCounts,
                _failedIndexes,
                _lastGlobalQuery.Length == 0 ? _browsePage : _searchPage);

            // While the browse index is cooling down, say so: a bare "failed" looks
            // like a stuck tab. Search results keep their own status untouched.
            if (!string.IsNullOrWhiteSpace(GlobalSearchTerm))
            {
                return text;
            }

            if (_browseRetryInFlight)
            {
                var inFlight = _browseRetryInFlightPage switch
                {
                    1 => "reloading page 1…",
                    > 1 => $"retrying page {_browseRetryInFlightPage}…",
                    _ => "retrying now…"
                };
                return text.Length == 0 ? inFlight : $"{text}   ·   {inFlight}";
            }

            if (!BrowseBackoffActive)
            {
                return text;
            }

            var retry = $"retrying in {Math.Max(1, BrowseBackoffSecondsRemaining)}s";
            return text.Length == 0 ? retry : $"{text}   ·   {retry}";
        }
    }

    internal static string FormatIndexStatus(
        IReadOnlyDictionary<string, int> counts,
        IEnumerable<string> failed,
        int page)
    {
        var entries = counts
            .Select(entry => (Name: entry.Key, Text: $"{entry.Key} {entry.Value}"))
            .Concat(failed.Select(name => (Name: name, Text: $"{name} failed")))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Text)
            .ToList();

        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var parts = string.Join("   ", entries);
        return page > 1 ? $"{parts}   ·   page {page}" : parts;
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
                return $"Page {_browsePage} · {count} public avatars (community index)";
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
        private set
        {
            if (value && FavoriteSlotChoices.Count == 0)
            {
                // The list is rebuilt on every open so VRC+ status is current;
                // rebuilding here as well means a picker that is shown without
                // them can never come up as an empty dialog.
                BuildFavoriteSlotChoices();
            }

            SetProperty(ref _isSlotPickerOpen, value);
        }
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
        BuildFavoriteSlotChoices();

        SlotPickerMessage = IsVrcPlusAccount
            ? string.Empty
            : "Slots 2–4 need VRC+. Without it they cannot be saved to VRChat — use Slot 1 or Local.";
        IsSlotPickerOpen = true;
    }

    /// <summary>Test seam: opens the slot picker without going through a command.</summary>
    internal void OpenSlotPickerForTests(string avatarId) => BeginFavorite(avatarId);

    private void BuildFavoriteSlotChoices()
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
            // A load that finishes after this add would clear the record again.
            await EnsureFavoritesAsync();

            var isVrchatFavorite = false;
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
                isVrchatFavorite = true;
                if (!string.IsNullOrWhiteSpace(recordId))
                {
                    _favoriteRecords[avatarId] = recordId;
                    _favoriteTags[avatarId] = new List<string> { tag };
                }
                else
                {
                    // Some successful responses omit the favourite record id.
                    // Refresh so the remove action has the record it needs; a
                    // refresh failure must not turn a successful save into an
                    // error message.
                    try
                    {
                        await RefreshFavoriteRecordsAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"favourite refresh after add: {ex.Message}");
                    }
                }

                StatusMessage = $"Saved {PendingFavoriteAvatarName} to VRChat favourite {tag[^1]}.";
            }

            var avatar = FindAvatar(avatarId);
            if (avatar is not null)
            {
                // A profile avatar is not in the account library. Keep the
                // selected record as a favourite detail so the Favourites tab
                // and the Add/Remove button reflect the save immediately.
                _favoriteAvatars[avatarId] = avatar;
                avatar.IsFavorite = isVrchatFavorite || _favoriteRecords.ContainsKey(avatarId);
                avatar.IsLocalFavorite = _localFavoriteIds.Contains(avatarId);
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
    public RelayCommand ToggleProviderFilterCommand { get; }
    public RelayCommand SelectAllProvidersCommand { get; }
    public RelayCommand SelectNoProvidersCommand { get; }

    /// <summary>The community indexes the user can include in a search.</summary>
    public ObservableCollection<ProviderFilterOption> ProviderFilterOptions { get; } = new();

    private bool _isProviderFilterOpen;

    public bool IsProviderFilterOpen
    {
        get => _isProviderFilterOpen;
        set => SetProperty(ref _isProviderFilterOpen, value);
    }

    public string ProviderFilterLabel
    {
        get
        {
            var selected = ProviderFilterOptions.Count(option => option.IsSelected);
            if (selected == 0)
            {
                return "No providers";
            }

            if (selected == ProviderFilterOptions.Count)
            {
                return $"All providers ({selected})";
            }

            return selected == 1
                ? ProviderFilterOptions.First(option => option.IsSelected).Name
                : $"{selected} providers";
        }
    }

    public string ProviderFilterSummary
    {
        get
        {
            var selected = ProviderFilterOptions.Count(option => option.IsSelected);
            var total = ProviderFilterOptions.Count;
            if (selected == 0)
            {
                return "No community indexes selected. Search will show only VRChat results.";
            }

            return selected == total
                ? $"All {total} community indexes selected. VRChat is always searched too."
                : $"{selected} of {total} community indexes selected. VRChat is always searched too.";
        }
    }

    /// <summary>
    /// Null means all providers, so an unchanged search keeps the fast path. An
    /// empty set is a deliberate "no community indexes" selection.
    /// </summary>
    private IReadOnlyCollection<string>? SelectedProviderNames
        => ProviderFilterOptions.All(option => option.IsSelected)
            ? null
            : ProviderFilterOptions
                .Where(option => option.IsSelected)
                .Select(option => option.Name)
                .ToArray();

    private void SetProviderSelection(bool selected)
    {
        foreach (var option in ProviderFilterOptions)
        {
            option.IsSelected = selected;
        }

        OnProviderFilterChanged();
    }

    private void OnProviderFilterChanged()
    {
        OnPropertyChanged(nameof(ProviderFilterLabel));
        OnPropertyChanged(nameof(ProviderFilterSummary));
    }

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

    /// <summary>True while a recent browse failure is still cooling down.</summary>
    private bool BrowseBackoffActive
        => _browseFailureStreak > 0 && TestHooks.UtcNow() < _browseRetryAfterUtc;

    private int BrowseBackoffSecondsRemaining
        => Math.Max(0, (int)Math.Ceiling((_browseRetryAfterUtc - TestHooks.UtcNow()).TotalSeconds));

    private void RecordBrowseFailure(bool loadMore = false)
    {
        _browseRetryIsLoadMore = loadMore;
        _browseFailureStreak = Math.Min(_browseFailureStreak + 1, 8);
        var seconds = Math.Min(30 * Math.Pow(2, _browseFailureStreak - 1), 300);
        _browseRetryAfterUtc = TestHooks.UtcNow().AddSeconds(seconds);
        EnsureBrowseBackoffTicker();
    }

    private void ClearBrowseBackoff()
    {
        _browseFailureStreak = 0;
        _browseRetryAfterUtc = DateTime.MinValue;
        _browseRetryIsLoadMore = false;
        _browsePageRetryCount = 0;
        _browseBackoffTimer?.Stop();
    }

    /// <summary>Repaints the cooldown once a second so the status line counts down.</summary>
    private void EnsureBrowseBackoffTicker()
    {
        if (_browseBackoffTimer is null)
        {
            _browseBackoffTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _browseBackoffTimer.Tick += (_, _) => OnBrowseBackoffTick();
        }

        if (!_browseBackoffTimer.IsEnabled)
        {
            _browseBackoffTimer.Start();
        }
    }

    /// <summary>
    /// One tick of the cooldown countdown. When the window runs out browse retries
    /// by itself, so the tab does not sit on a stale "failed" line until the user
    /// happens to switch tabs. While a search owns the tab there is nothing to
    /// retry into, so the retry waits for the next tick after the search clears.
    /// </summary>
    internal void OnBrowseBackoffTick()
    {
        OnPropertyChanged(nameof(CommunityStatusText));

        if (BrowseBackoffActive)
        {
            return;
        }

        // Nothing to retry into while another tab is showing. The setter restarts
        // the ticker (or runs the retry) when Avatar Search comes back.
        if (!IsBrowseTabActive)
        {
            return;
        }

        if (IsGlobalBusy || !string.IsNullOrWhiteSpace(GlobalSearchTerm))
        {
            return;
        }

        _browseBackoffTimer?.Stop();
        if (!_communityLoaded)
        {
            StartAutomaticBrowseRetry(loadMore: false);
            return;
        }

        if (!_browseRetryIsLoadMore)
        {
            return;
        }

        if (_browsePageRetryCount >= MaxAutoPageRetries)
        {
            // The same page keeps failing: stop chasing it and reload page 1. Mark
            // browse unloaded so a failed reload is retried as page 1, not ignored.
            _browsePageRetryCount = 0;
            _browseRetryIsLoadMore = false;
            _communityLoaded = false;
            StartAutomaticBrowseRetry(loadMore: false, retryPage: 1);
            return;
        }

        // Resume the page that failed rather than throwing the pages away.
        _browsePageRetryCount++;
        StartAutomaticBrowseRetry(loadMore: true, retryPage: _browsePage + 1);
    }

    private void StartAutomaticBrowseRetry(bool loadMore, int retryPage = 0)
    {
        _browseRetryInFlight = true;
        _browseRetryInFlightPage = retryPage;
        OnPropertyChanged(nameof(CommunityStatusText));
        if (loadMore)
        {
            _ = LoadMoreCommunityAsync();
        }
        else
        {
            _ = BrowseCommunityAsync();
        }
    }

    private void EndAutomaticBrowseRetry()
    {
        if (!_browseRetryInFlight)
        {
            return;
        }

        _browseRetryInFlight = false;
        _browseRetryInFlightPage = 0;
        OnPropertyChanged(nameof(CommunityStatusText));
    }

    private void RefreshBrowse()
    {
        _communityLoaded = false;
        _browsePage = 1;
        _communityCache.Clear();
        _communityOffset = 0;
        _browseVrcndbPage = 1;
        _communityPageFull = false;
        // Refreshing the browse cache must not wipe an active search.
        _ = BrowseCommunityAsync(publish: string.IsNullOrWhiteSpace(GlobalSearchTerm));
    }

        /// <summary>
    /// Adds a source's rows as one collection update. Per-row adds made the
    /// result grid re-layout for every avatar while several indexes streamed in.
    /// </summary>
    private int AddGlobalResults(IEnumerable<Avatar> avatars, bool atStart = false)
    {
        var seen = GlobalResults
            .Select(existing => existing.Id)
            .ToHashSet(StringComparer.Ordinal);
        var rows = new List<Avatar>();
        foreach (var avatar in avatars)
        {
            if (!seen.Add(avatar.Id))
            {
                continue;
            }

            ApplyState(avatar, _currentAvatarId);
            rows.Add(avatar);
        }

        if (rows.Count == 0)
        {
            return 0;
        }

        AvatarLookupCache.Remember(rows);

        if (atStart)
        {
            GlobalResults.InsertRange(0, rows);
        }
        else
        {
            GlobalResults.AddRange(rows);
        }

        GlobalResultsChanged?.Invoke();
        return rows.Count;
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
                return HasLoaded ? "No uploaded avatars yet" : string.Empty;
            }

            // The grid builds a chunk at a time, so these counts describe the
            // filtered library, never the cards that happen to be built.
            if (FavoritesOnly)
            {
                return _filteredCount == 1 ? "1 favourite avatar" : $"{_filteredCount} favourite avatars";
            }

            if (string.IsNullOrWhiteSpace(_searchTerm))
            {
                return _library.Count == 1 ? "1 uploaded avatar" : $"{_library.Count} uploaded avatars";
            }

            return $"{_filteredCount} of {_library.Count} uploaded avatars";
        }
    }

    /// <summary>The avatar shown in the detail view, or null for the grid.</summary>
    public Avatar? SelectedAvatar
    {
        get => _selectedAvatar;
        set
        {
            // Ownership affects CanUse, so it must be set before the detail
            // bindings see the new avatar.
            if (value is not null)
            {
                ApplyOwnership(value);
            }

            if (SetProperty(ref _selectedAvatar, value))
            {
                if (value is null)
                {
                    SetReturnToProfile(false);
                }

                OnPropertyChanged(nameof(IsAvatarDetailsOpen));
            }
        }
    }

    public bool IsAvatarDetailsOpen => SelectedAvatar is not null;

    private bool _canReturnToProfile;

    /// <summary>True when this avatar was opened from a profile card.</summary>
    public bool CanReturnToProfile
    {
        get => _canReturnToProfile;
        private set
        {
            if (SetProperty(ref _canReturnToProfile, value))
            {
                OnPropertyChanged(nameof(AvatarDetailsBackLabel));
            }
        }
    }

    public string AvatarDetailsBackLabel => CanReturnToProfile
        ? "← Back to profile"
        : "← Back to all avatars";

    /// <summary>
    /// Opens the shared avatar detail popup. The profile flag is set only by the
    /// profile callback, so the back button can return to the card behind it.
    /// </summary>
    public void ShowAvatarDetails(Avatar avatar, bool returnToProfile = false)
    {
        StatusMessage = null;
        ErrorMessage = null;
        SetReturnToProfile(returnToProfile);
        SelectedAvatar = avatar;

        // A profile card can be opened before the avatar tab has loaded. Fetch
        // the favourite records so the button knows whether this avatar is
        // already favourited and can remove it.
        if (!_favoritesLoaded && !_favoritesLoading)
        {
            _ = EnsureFavoritesAsync();
        }
    }

    private void CloseAvatarDetails()
    {
        SelectedAvatar = null;
        SetReturnToProfile(false);
    }

    private void SetReturnToProfile(bool value)
    {
        if (CanReturnToProfile == value)
        {
            return;
        }

        CanReturnToProfile = value;
    }

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
    public ICommand ToggleAvatarSortCommand { get; }
    public ICommand ToggleFavoriteSortCommand { get; }
    public ICommand WearOriginalAvatarCommand { get; }

    /// <summary>
    /// Drops the owned-avatar library once its page is gone; the Favourites page
    /// binds its own rows and is not touched. The next visit reloads because
    /// HasLoaded is reset.
    /// </summary>
    /// <summary>
    /// Drops the loaded library rows and the images they hold. Returns false
    /// when the library is still arriving, so the caller can retry.
    /// </summary>
    public bool ReleaseLibraryData(bool keepFavoriteRows)
    {
        if (IsBusy)
        {
            return false;
        }

        if (keepFavoriteRows)
        {
            // The Favourites page can still be on screen and resolves its rows
            // from the library first, so hand it just those avatars before the
            // rest of the library goes; otherwise its list would shrink the next
            // time it rebuilds.
            var byId = new Dictionary<string, Avatar>(StringComparer.Ordinal);
            foreach (var avatar in _library)
            {
                byId[avatar.Id] = avatar;
            }

            foreach (var favoriteId in _favoriteRecords.Keys.Concat(_localFavoriteIds))
            {
                if (byId.TryGetValue(favoriteId, out var avatar) &&
                    !_favoriteAvatars.ContainsKey(favoriteId))
                {
                    _favoriteAvatars[favoriteId] = avatar;
                }
            }
        }

        SelectedAvatar = null;
        _library = new List<Avatar>();
        Avatars = new ObservableCollection<Avatar>();
        TotalCount = 0;
        _filteredCount = 0;
        _visibleLimit = AvatarChunk;
        HasLoaded = false;
        OnPropertyChanged(nameof(HasMoreAvatars));
        OnPropertyChanged(nameof(HiddenAvatarCount));
        return true;
    }

    /// <summary>Drops browse results, which are the heaviest rows this tab keeps.</summary>
    public void ReleaseBrowseData()
    {
        if (IsBusy)
        {
            return;
        }

        GlobalResults.ReplaceAll(Array.Empty<Avatar>());
    }

    /// <summary>
    /// Drops the favourite rows, slot counts and fetched details, but keeps the
    /// record set itself: stars in the library, the avatar details panel and the
    /// slot picker all read <see cref="_favoriteRecords"/>, and clearing it here
    /// would show unfavourited stars everywhere until the next reload.
    /// </summary>
    public void ReleaseFavoritesData()
    {
        if (_favoritesLoading)
        {
            return;
        }

        _favoritesGeneration++;
        FavoriteAvatars.Clear();
        FavoriteSlots.Clear();
        UnresolvedFavoriteIds.Clear();
        _favoriteAvatars.Clear();
        _failedFavoriteIds.Clear();
        _favoritesLoaded = false;
    }

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
            RememberCurrentAvatar();
            // Remember the avatar the session started with so it can be restored.
            _originalAvatarId ??= _currentAvatarId;
            _library = new List<Avatar>();
            Avatars = new ObservableCollection<Avatar>();
            TotalCount = 0;
            // A reload (refresh, account switch) starts a new grid, so it must
            // not inherit however many chunks the previous one had revealed.
            _visibleLimit = AvatarChunk;

            // Publish every page as soon as it lands instead of waiting for the
            // whole library, so large accounts fill in progressively.
            await _api.GetAllAvatarsAsync(onPartial: PublishLibrary);

            HasLoaded = true;
            NotifySummaryChanged();
            BuildFavorites();

            // Any favourite that the library did not cover still needs details.
            _ = HydrateFavoriteAvatarsAsync();

            // VRChat sometimes omits tags from private/own list rows. Fill them
            // from a content-tag filtered query so every card shows the same
            // readable content chips.
            _ = HydrateMissingContentTagsAsync();

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
        SetReturnToProfile(false);
        var avatar = FindAvatar(avatarId);
        AvatarLookupCache.Remember(avatar);
        SelectedAvatar = avatar;

        // Community rows and own rows whose list response omitted tags still
        // need VRChat's authoritative record. Fetch it when the popup opens so
        // the detail panel always has the same tags as the cards.
        if (avatar is not null &&
            (!string.IsNullOrWhiteSpace(avatar.SourceName) || avatar.Tags.Count == 0))
        {
            _ = HydrateCommunityAvatarAsync(avatar);
        }
    }

    private bool _contentTagsHydrating;

    private async Task HydrateMissingContentTagsAsync()
    {
        if (_contentTagsHydrating || _library.Count == 0)
        {
            return;
        }

        _contentTagsHydrating = true;
        try
        {
            var tagged = await _api.GetOwnContentTaggedAvatarsAsync();
            if (tagged.Count == 0)
            {
                return;
            }

            var targets = _library.ToDictionary(avatar => avatar.Id, StringComparer.Ordinal);
            var changed = false;
            foreach (var source in tagged)
            {
                if (source.Tags.Count > 0 &&
                    targets.TryGetValue(source.Id, out var target))
                {
                    changed |= target.MergeTags(source.Tags);
                }
            }

            if (!changed)
            {
                return;
            }

            ApplyFilter();
            BuildFavorites();
        }
        catch (Exception ex)
        {
            // Tags are supplementary visual metadata; keep the library usable if
            // the filtered query is unavailable.
            Debug.WriteLine($"avatar content tags: {ex.Message}");
        }
        finally
        {
            _contentTagsHydrating = false;
        }
    }

    private async Task HydrateCommunityAvatarAsync(Avatar avatar)
    {
        try
        {
            var details = await _api.GetAvatarAsync(avatar.Id);
            if (details is not null)
            {
                avatar.MergeDetailsFrom(details);
                // The merged record may carry the author id that proves this is
                // the signed-in user's own private avatar.
                ApplyOwnership(avatar);
            }
        }
        catch (Exception ex)
        {
            // The community row is still usable when VRChat briefly refuses or
            // cannot find the public record; keep it instead of closing the popup.
            Debug.WriteLine($"avatar search detail hydration {avatar.Id}: {ex.Message}");
        }
    }

    private async Task WearAvatarAsync(string? avatarId)
    {
        if (string.IsNullOrWhiteSpace(avatarId))
        {
            return;
        }

        var avatar = FindAvatar(avatarId);
        if (avatar?.HasSecurityCheckFailed == true)
        {
            ErrorMessage = "VRChat's security checks failed on that avatar, so it cannot be loaded, worn, or favourited.";
            return;
        }

        var name = avatar?.DisplayName ?? "that avatar";

        IsWearingAvatar = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            _previousAvatarId = _currentAvatarId;

            // VRChat's REST select can take a long time (or never) to reach the
            // running client. Send OSC directly to VRChat's local input port as
            // well, then keep nudging it after the account update succeeds.
            var settings = SettingsStore.Shared.Settings;
            _avatarOscCancellation?.Cancel();
            _avatarOscCancellation?.Dispose();
            _avatarOscCancellation = new CancellationTokenSource();
            var oscToken = _avatarOscCancellation.Token;

            // Start the OSC retry loop before the REST response so VRChat can
            // load the avatar as soon as its account update becomes visible,
            // without waiting for our own HTTP round trip first.
            _ = NudgeAvatarOverOscAsync(avatarId, settings, oscToken);
            await _api.SetCurrentAvatarAsync(avatarId, oscToken);
            _currentAvatarId = avatarId;
            RememberAvatarUse(avatarId);
            ApplyCurrentAvatarLocally(avatarId);
            AvatarWorn?.Invoke(avatar ?? new Avatar
            {
                Id = avatarId,
                Name = name
            });
            if (string.Equals(SelectedAvatarSort.Key, "recently-used", StringComparison.Ordinal))
            {
                ApplyFilter();
            }
            if (string.Equals(SelectedFavoriteSort.Key, "recently-used", StringComparison.Ordinal))
            {
                BuildFavorites();
            }
            OscAvatarSwitch.TrySend(avatarId, settings.OscHost, settings.OscPort);
            StatusMessage = $"Now wearing {name}. It is your avatar in VRChat now.";
            OnPropertyChanged(nameof(CanRevertPrevious));
            OnPropertyChanged(nameof(PreviousAvatarName));
            OnPropertyChanged(nameof(CanRevertAvatar));
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
            if (ErrorMessage is not null)
            {
                _avatarOscCancellation?.Cancel();
            }

            IsWearingAvatar = false;
        }
    }

    /// <summary>Republishes the progressively fetched library in the selected order.</summary>
    private void PublishLibrary(IReadOnlyList<Avatar> library)
    {
        _library = library.ToList();
        ApplyFilter();
    }

    /// <summary>Test seam: seeds the uploaded library without making an API request.</summary>
    internal void SeedLibraryForTests(IEnumerable<Avatar> avatars)
    {
        _library = avatars.ToList();
        ApplyFilter();
    }

    /// <summary>Test seam: seeds VRChat favourites without making an API request.</summary>
    internal void SeedFavoritesForTests(IEnumerable<Avatar> avatars)
    {
        _favoriteRecords.Clear();
        _favoriteTags.Clear();
        _favoriteAvatars.Clear();
        foreach (var avatar in avatars)
        {
            _favoriteRecords[avatar.Id] = $"fav_{avatar.Id}";
            _favoriteTags[avatar.Id] = new List<string> { "avatars1" };
            _favoriteAvatars[avatar.Id] = avatar;
        }

        _favoritesLoaded = true;
        BuildFavorites();
    }

    /// <summary>Rebuilds the visible grid from the library for the current query and sort.</summary>
    private void ApplyFilter()
    {
        var rows = SortAvatars(_library.Where(Matches), SelectedAvatarSort).ToList();
        _filteredCount = rows.Count;

        // The grid's virtualizing panel keeps only the cards near the viewport
        // built, but every published avatar is still a row the grid can scroll
        // to, so the page also publishes a chunk at a time and lets the panel
        // keep the height of the rest without any visuals.
        _visibleLimit = Math.Max(AvatarChunk, Math.Min(_visibleLimit, _filteredCount));
        Avatars = new ObservableCollection<Avatar>(rows.Take(_visibleLimit));
        UpdateSummary();
        OnPropertyChanged(nameof(HasMoreAvatars));
        OnPropertyChanged(nameof(HiddenAvatarCount));
        ShowMoreAvatarsCommand.RaiseCanExecuteChanged();
    }

    /// <summary>How many more cards to add when the user asks for them.</summary>
    private const int AvatarChunk = 48;

    private int _visibleLimit = AvatarChunk;
    private int _filteredCount;

    /// <summary>True while the filtered library still has cards that are not built.</summary>
    public bool HasMoreAvatars => Avatars.Count < _filteredCount;

    public int HiddenAvatarCount => Math.Max(0, _filteredCount - Avatars.Count);

    public IRelayCommand ShowMoreAvatarsCommand { get; }

    public void ShowMoreAvatars()
    {
        if (!HasMoreAvatars)
        {
            return;
        }

        _visibleLimit = Math.Min(_filteredCount, _visibleLimit + (AvatarChunk * 2));
        ApplyFilter();
    }

    private FilterOption ResolveAvatarSort(string? key)
    {
        return AvatarSortOptions.FirstOrDefault(option =>
                   string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase))
               ?? AvatarSortOptions[0];
    }

    private IEnumerable<Avatar> SortAvatars(IEnumerable<Avatar> avatars, FilterOption sort)
    {
        var rows = avatars.ToList();
        foreach (var avatar in rows)
        {
            ApplyState(avatar, _currentAvatarId);
        }

        var recent = SettingsStore.Shared.Settings.RecentAvatarIds ?? new List<string>();
        var recentRank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < recent.Count; index++)
        {
            recentRank.TryAdd(recent[index], index);
        }

        IOrderedEnumerable<Avatar> ordered = sort.Key switch
        {
            "last-updated" => rows
                .OrderByDescending(avatar => avatar.UpdatedAt)
                .ThenByDescending(avatar => avatar.CreatedAt),
            "name" => rows
                .OrderBy(avatar => avatar.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            "recently-used" => rows
                .OrderBy(avatar => recentRank.TryGetValue(avatar.Id, out var index) ? index : int.MaxValue)
                .ThenByDescending(avatar => avatar.CreatedAt),
            "performance" => rows
                .OrderBy(PerformanceSortRank)
                .ThenByDescending(avatar => avatar.CreatedAt),
            _ => rows.OrderByDescending(avatar => avatar.CreatedAt)
        };

        return ordered
            .ThenBy(avatar => avatar.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(avatar => avatar.Id, StringComparer.Ordinal);
    }

    private static int PerformanceSortRank(Avatar avatar)
    {
        var ratings = avatar.UnityPackages
            .Select(package => PerformanceRank(package.PerformanceRating))
            .Where(rank => rank < 5)
            .ToList();
        return ratings.Count == 0 ? 5 : ratings.Min();
    }

    private static int PerformanceRank(string? rating)
    {
        return rating?.Trim().ToLowerInvariant() switch
        {
            "excellent" => 0,
            "good" => 1,
            "medium" => 2,
            "poor" => 3,
            "very poor" or "verypoor" => 4,
            _ => 5
        };
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
        // The grid publishes a chunk at a time, so the headline count must come
        // from the filtered library, not from the cards that happen to be built.
        TotalCount = _filteredCount;
        NotifySummaryChanged();
    }

    private void NotifySummaryChanged() => OnPropertyChanged(nameof(SummaryText));

    private void ApplyState(Avatar avatar, string? currentAvatarId)
    {
        ApplyOwnership(avatar);
        avatar.IsWearing = !string.IsNullOrWhiteSpace(currentAvatarId) &&
                           string.Equals(avatar.Id, currentAvatarId, StringComparison.Ordinal);
        avatar.IsFavorite = _favoriteRecords.ContainsKey(avatar.Id);
        avatar.IsLocalFavorite = _localFavoriteIds.Contains(avatar.Id);
    }

    /// <summary>
    /// VRChat allows private avatars to be worn only by their creator. Mark the
    /// record when it belongs to the signed-in account so the UI can enable it
    /// without opening up someone else's private avatar.
    /// </summary>
    private void ApplyOwnership(Avatar avatar)
    {
        var currentUserId = _api.CurrentUser?.Id;
        avatar.IsOwnedByCurrentUser =
            !string.IsNullOrWhiteSpace(currentUserId) &&
            !string.IsNullOrWhiteSpace(avatar.AuthorId) &&
            string.Equals(avatar.AuthorId, currentUserId, StringComparison.Ordinal);
    }

    private static async Task NudgeAvatarOverOscAsync(
        string avatarId,
        CompanionSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            // Sparse retries across VRChat's propagation window. The later
            // packets matter because VRChat can take several seconds to make
            // the selected avatar eligible locally. Keep this sparse: flooding
            // the same switch delays the avatar load instead of helping it.
            foreach (var delay in new[] { 0, 100, 250, 500, 1000, 1800, 2800 })
            {
                await Task.Delay(delay, cancellationToken);
                OscAvatarSwitch.TrySend(avatarId, settings.OscHost, settings.OscPort);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer Wear or Revert owns the avatar now.
        }
    }

    /// <summary>
    /// Reflects a completed avatar switch in every visible list immediately. A full
    /// library reload is unnecessary here and would keep the user waiting on network
    /// pages after VRChat has already accepted the change.
    /// </summary>
    private void ApplyCurrentAvatarLocally(string avatarId)
    {
        foreach (var avatar in _library)
        {
            ApplyState(avatar, avatarId);
        }

        foreach (var avatar in GlobalResults)
        {
            ApplyState(avatar, avatarId);
        }

        foreach (var avatar in FavoriteAvatars)
        {
            ApplyState(avatar, avatarId);
        }

        if (SelectedAvatar is { } selected)
        {
            ApplyState(selected, avatarId);
        }
    }

    /// <summary>Marks a successfully worn avatar as the newest entry in Recently Used.</summary>
    private void RememberAvatarUse(string avatarId)
    {
        if (string.IsNullOrWhiteSpace(avatarId))
        {
            return;
        }

        var recent = SettingsStore.Shared.Settings.RecentAvatarIds ??= new List<string>();
        recent.RemoveAll(id => string.Equals(id, avatarId, StringComparison.Ordinal));
        recent.Insert(0, avatarId);
        if (recent.Count > 250)
        {
            recent.RemoveRange(250, recent.Count - 250);
        }

        SettingsStore.Shared.Save();
    }

    /// <summary>The avatar already selected in VRChat counts as used when the app starts.</summary>
    private void RememberCurrentAvatar()
    {
        if (string.IsNullOrWhiteSpace(_currentAvatarId))
        {
            return;
        }

        var recent = SettingsStore.Shared.Settings.RecentAvatarIds ??= new List<string>();
        if (!recent.Contains(_currentAvatarId, StringComparer.Ordinal))
        {
            recent.Insert(0, _currentAvatarId);
            SettingsStore.Shared.Save();
        }
    }

    /// <summary>Adds or removes the avatar favourite, depending on its current state.</summary>
    private async Task ToggleFavoriteAsync(string? avatarId)
    {
        if (string.IsNullOrWhiteSpace(avatarId))
        {
            return;
        }

        if (FindAvatar(avatarId)?.HasSecurityCheckFailed == true)
        {
            ErrorMessage = "VRChat's security checks failed on that avatar, so it cannot be favourited.";
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
            // Opening the slot picker must not wait on the network, so the load
            // is only joined here when nothing is in flight; a load that is
            // already running cannot be extended without delaying the picker.
            if (!_favoritesLoaded && !_favoritesLoading)
            {
                await EnsureFavoritesAsync();
            }

            var isFavorite = _favoriteRecords.ContainsKey(avatarId) ||
                             (SelectedAvatar is { } current &&
                              string.Equals(current.Id, avatarId, StringComparison.Ordinal) &&
                              current.IsFavorite);
            var isLocalFavorite = _localFavoriteIds.Contains(avatarId) ||
                                  (SelectedAvatar is { } localCurrent &&
                                   string.Equals(localCurrent.Id, avatarId, StringComparison.Ordinal) &&
                                   localCurrent.IsLocalFavorite);
            if (isFavorite)
            {
                if (!_favoriteRecords.TryGetValue(avatarId, out var recordId))
                {
                    await RefreshFavoriteRecordsAsync();
                    _favoriteRecords.TryGetValue(avatarId, out recordId);
                }

                if (string.IsNullOrWhiteSpace(recordId))
                {
                    if (SelectedAvatar is { } stale &&
                        string.Equals(stale.Id, avatarId, StringComparison.Ordinal))
                    {
                        stale.IsFavorite = false;
                    }

                    ErrorMessage = "VRChat did not return a favourite record to remove.";
                    return;
                }

                await _api.RemoveFavoriteAsync(recordId);
                _favoriteRecords.Remove(avatarId);
                _favoriteTags.Remove(avatarId);
                if (_localFavoriteIds.Remove(avatarId))
                {
                    SaveLocalFavorites();
                }
                StatusMessage = "Removed from favourites.";

                var favorite = _favoriteRecords.ContainsKey(avatarId);
                foreach (var item in _library.Where(item => string.Equals(item.Id, avatarId, StringComparison.Ordinal)))
                {
                    item.IsFavorite = favorite;
                    item.IsLocalFavorite = false;
                }

                if (SelectedAvatar is { } selected &&
                    string.Equals(selected.Id, avatarId, StringComparison.Ordinal))
                {
                    selected.IsFavorite = favorite;
                    selected.IsLocalFavorite = false;
                }

                UpdateSummary();
                BuildFavorites();
                if (FavoritesOnly)
                {
                    ApplyFilter();
                }

                return;
            }

            if (isLocalFavorite)
            {
                _localFavoriteIds.Remove(avatarId);
                SaveLocalFavorites();
                if (SelectedAvatar is { } selectedLocal &&
                    string.Equals(selectedLocal.Id, avatarId, StringComparison.Ordinal))
                {
                    selectedLocal.IsLocalFavorite = false;
                }

                StatusMessage = "Removed from your Local favourites.";
                BuildFavorites();
                OnPropertyChanged(nameof(HasFavorites));
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

        foreach (var avatar in SortAvatars(shown, SelectedFavoriteSort))
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
        var generation = _favoritesGeneration;
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

                if (generation != _favoritesGeneration)
                {
                    // The rows were released while this batch was in flight.
                    return;
                }

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
    public Task EnsureFavoritesAsync()
    {
        if (_favoritesLoaded)
        {
            return Task.CompletedTask;
        }

        // Callers that are already loading share the same task instead of
        // returning early: a caller that is about to change a favourite needs
        // to know when the record set has been replaced, otherwise the load
        // can land afterwards and wipe the record the change just wrote.
        return _favoritesLoad ??= LoadFavoritesCoreAsync();
    }

    private async Task LoadFavoritesCoreAsync()
    {
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
            if (SelectedAvatar is { } selected)
            {
                ApplyState(selected, _currentAvatarId);
            }

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
            _favoritesLoad = null;
            _favoritesLoading = false;
            OnPropertyChanged(nameof(FavoritesLoadingText));
            OnPropertyChanged(nameof(HasFavorites));
        }
    }

    /// <summary>Re-reads VRChat favourites when a mutation response omitted its record id.</summary>
    private async Task RefreshFavoriteRecordsAsync()
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
        if (SelectedAvatar is { } selected)
        {
            ApplyState(selected, _currentAvatarId);
        }

        BuildFavorites();
        OnPropertyChanged(nameof(HasFavorites));
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

        if (FindAvatar(avatarId)?.HasSecurityCheckFailed == true)
        {
            ErrorMessage = "VRChat's security checks failed on that avatar, so it cannot be favourited.";
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

            // Run both sides together. VRChat can spend 2.5 seconds waiting for
            // its own search spacing; the community indexes must not wait behind
            // that, or the grid stays empty for no reason.
            async Task SearchVrchatApiAsync()
            {
                // Stay inside VRChat's rate limit: never fire avatar searches back
                // to back, and do not call it at all while it is cooling down.
                if (DateTime.UtcNow < _vrchatCooldownUntil)
                {
                    VrchatThrottled = true;
                    return;
                }

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

                    _vrchatRowsInResults += AddGlobalResults(result.Avatars.Where(MatchesPrivacy));

                    _lastVrchatPageFull = result.Avatars.Count >= 50;
                    if (result.Avatars.Count == 0 && append)
                    {
                        _globalPage--;
                    }

                    if (result.Avatars.Count > 0)
                    {
                        VrchatThrottled = false;
                    }

                    OnPropertyChanged(nameof(GlobalSummaryText));
                }
                catch (VrcApiException ex) when (ex.StatusCode == 429)
                {
                    StartVrchatCooldown(ex);
                }
            }

            async Task SearchCommunityIndexesAsync()
            {
                // One combined search: VRChat's own results plus the community
                // public indexes, which cover far more avatars than the API.
                if (_lastGlobalQuery.Length == 0)
                {
                    return;
                }

                if (!append)
                {
                    _communitySearchOffset = 0;
                }

                // @name / usr_ searches must not stop at VRChat: ask the community
                // providers by author id as well.
                _currentProbe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                if (authorId.Length > 0)
                {
                    var byAuthor = await _api.SearchCommunityByAuthorAsync(
                        authorId,
                        indexResults: _currentProbe,
                        providers: SelectedProviderNames);
                    _indexCounts.Clear();
                    foreach (var group in byAuthor.GroupBy(a => a.SourceName, StringComparer.OrdinalIgnoreCase))
                    {
                        _indexCounts[group.Key] = group.Count();
                    }

                    OnPropertyChanged(nameof(CommunityStatusText));
                    AddGlobalResults(byAuthor.Where(MatchesPrivacy));
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
                        _communitySearchOffset,
                        indexResults: _currentProbe,
                        providers: SelectedProviderNames);
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
                        providerLimit: 80,
                        indexResults: _currentProbe,
                        providers: SelectedProviderNames,
                        onPartial: (source, partial) =>
                        {
                            var rows = partial.Where(MatchesPrivacy).ToList();
                            // One collection update per source keeps the UI
                            // responsive while several indexes stream in.
                            AddGlobalResults(
                                rows,
                                atStart: source.Equals("VRCNDb", StringComparison.OrdinalIgnoreCase));

                            OnPropertyChanged(nameof(GlobalSummaryText));
                        });
                    _communitySearchOffset = 25;
                }

                OnPropertyChanged(nameof(CommunityStatusText));
                _communitySearchPageFull = community.Count >= 25;
                AddGlobalResults(community.Where(MatchesPrivacy));
            }

            await Task.WhenAll(SearchVrchatApiAsync(), SearchCommunityIndexesAsync());
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
    private int _browseVrcndbPage = 1;
    // A failing browse index is not retried on every tab activation: each failure
    // doubles the wait (30 s, then 60 s, up to 5 min) until a fetch succeeds.
    private int _browseFailureStreak;
    private DateTime _browseRetryAfterUtc;
    // Ticks the countdown in the status line while a cooldown is running.
    private DispatcherTimer? _browseBackoffTimer;
    // True when the failure came from a load-more page: expiry must retry that
    // page, not restart browse from the first one.
    private bool _browseRetryIsLoadMore;
    // Automatic attempts at the same load-more page before giving up on it.
    private const int MaxAutoPageRetries = 3;
    private int _browsePageRetryCount;
    // Set while the cooldown tick is fetching by itself, so the status can say so.
    private bool _browseRetryInFlight;
    // Page the in-flight retry is fetching: 0 = generic browse retry, 1 = the
    // cap reloading page 1, N = the automatic retry of page N.
    private int _browseRetryInFlightPage;

    private bool _isBrowseTabActive;

    /// <summary>
    /// True while the Avatar Search tab is the one on screen. The shell keeps this
    /// in step. Leaving the tab stops the cooldown ticker; returning restarts the
    /// countdown, or runs the pending retry straight away if the window is over.
    /// </summary>
    public bool IsBrowseTabActive
    {
        get => _isBrowseTabActive;
        set
        {
            if (_isBrowseTabActive == value)
            {
                return;
            }

            _isBrowseTabActive = value;
            if (!value)
            {
                _browseBackoffTimer?.Stop();
                return;
            }

            if (_browseFailureStreak == 0)
            {
                return;
            }

            if (BrowseBackoffActive)
            {
                EnsureBrowseBackoffTicker();
            }
            else
            {
                // Window already ran out while the tab was away: retry now.
                OnBrowseBackoffTick();
            }
        }
    }

    internal bool IsBrowseBackoffTickerRunning => _browseBackoffTimer?.IsEnabled == true;

    public void BrowseCatalog()
    {
        IsBrowseTabActive = true;

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

        // The index failed recently: skip the automatic retry so opening the tab
        // repeatedly does not each wait on a timeout. RefreshBrowse still forces it.
        if (BrowseBackoffActive)
        {
            return;
        }

        _ = BrowseCommunityAsync();
    }

    private async Task BrowseCommunityAsync(bool publish = true)
    {
        // Reloading page 1 ends any series of retries aimed at a later page.
        _browsePageRetryCount = 0;
        IsGlobalBusy = true;
        try
        {
            var probe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var vrcndbProbe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<Avatar> page;
            var avtrRows = 0;
            var vrcndbRows = 0;
            if (BrowseFetch is { } fetchForTests)
            {
                page = await fetchForTests(0, CancellationToken.None);
                foreach (var avatar in page)
                {
                    avatar.SourceName ??= "avtr.icu";
                }

                avtrRows = page.Count;
            }
            else
            {
                // Both browse indexes share one deadline: a dead index must not
                // hold the latest-avatar page for its full HTTP timeout.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var token = deadline.Token;
                var avtrTask = _api.GetCommunityLatestAsync(
                    50, 0, probe, token, SelectedProviderNames);
                var vrcndbTask = _api.GetVrcndbLatestAsync(
                    40, 1, vrcndbProbe, token, SelectedProviderNames);
                await Task.WhenAll(avtrTask, vrcndbTask);

                var avtr = await avtrTask;
                var vrcndb = await vrcndbTask;
                avtrRows = avtr.Count;
                vrcndbRows = vrcndb.Count;
                page = VrcApiClient.MergeCommunityResults(new[]
                {
                    ("avtr.icu", (IReadOnlyList<Avatar>)avtr),
                    ("VRCNDb", (IReadOnlyList<Avatar>)vrcndb)
                });
            }

            var avtrOk = !probe.TryGetValue("avtr.icu", out var avtrOutcome) || avtrOutcome >= 0;
            var vrcndbOk = !vrcndbProbe.TryGetValue("VRCNDb", out var vrcndbOutcome) || vrcndbOutcome >= 0;
            var fetched = avtrOk || vrcndbOk;
            if (fetched)
            {
                ClearBrowseBackoff();
            }
            else
            {
                RecordBrowseFailure();
            }

            _communityCache.Clear();
            _communityCache.AddRange(page);
            _communityOffset = avtrRows;
            _browseVrcndbPage = 2;
            _communityPageFull = avtrRows >= 50 || vrcndbRows >= 40;
            // A failed fetch must not count as "loaded": the next tab switch or
            // refresh then retries, and a success clears the failure line.
            _communityLoaded = fetched;
            _browsePage = 1;
            BumpResultsPageToken();
            _browsePageNewCounts.Clear();
            if (fetched)
            {
                _browsePageNewCounts.Add(page.Count);
            }
            // A refresh during an active search only warms the browse cache; the
            // status line still describes the search results on screen.
            if (ShouldReportBrowseIndexStatus(GlobalSearchTerm, publish))
            {
                _failedIndexes.Clear();
                SetBrowseStatusCounts(1);
                if (!avtrOk)
                {
                    _failedIndexes.Add("avtr.icu");
                }

                if (!vrcndbOk)
                {
                    _failedIndexes.Add("VRCNDb");
                }

                OnPropertyChanged(nameof(CommunityStatusText));
            }
            if (publish)
            {
                PublishCommunityBrowse();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            RecordBrowseFailure();
            if (publish)
            {
                GlobalError = "Could not reach the community avatar index.";
            }
        }
        finally
        {
            IsGlobalBusy = false;
            EndAutomaticBrowseRetry();
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

        // A failed page should not be refetched on every click: the same cooldown
        // as a failed browse applies, and the status line counts it down.
        if (BrowseBackoffActive)
        {
            GlobalError = $"The community index is cooling down. Try again in {Math.Max(1, BrowseBackoffSecondsRemaining)}s.";
            return;
        }

        IsGlobalBusy = true;
        try
        {
            var probe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var vrcndbProbe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<Avatar> page;
            var avtrRows = 0;
            var vrcndbRows = 0;
            var testFetch = BrowseFetch is not null;
            if (BrowseFetch is { } fetchForTests)
            {
                page = await fetchForTests(_communityOffset, CancellationToken.None);
                foreach (var avatar in page)
                {
                    avatar.SourceName ??= "avtr.icu";
                }

                avtrRows = page.Count;
            }
            else
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var token = deadline.Token;
                var avtrTask = _api.GetCommunityLatestAsync(
                    50, _communityOffset, probe, token, SelectedProviderNames);
                var vrcndbTask = _api.GetVrcndbLatestAsync(
                    40, _browseVrcndbPage, vrcndbProbe, token, SelectedProviderNames);
                await Task.WhenAll(avtrTask, vrcndbTask);

                var avtr = await avtrTask;
                var vrcndb = await vrcndbTask;
                avtrRows = avtr.Count;
                vrcndbRows = vrcndb.Count;
                page = VrcApiClient.MergeCommunityResults(new[]
                {
                    ("avtr.icu", (IReadOnlyList<Avatar>)avtr),
                    ("VRCNDb", (IReadOnlyList<Avatar>)vrcndb)
                });
            }

            var avtrOk = !probe.TryGetValue("avtr.icu", out var avtrOutcome) || avtrOutcome >= 0;
            var vrcndbOk = !vrcndbProbe.TryGetValue("VRCNDb", out var vrcndbOutcome) || vrcndbOutcome >= 0;
            var failed = !avtrOk && !vrcndbOk;
            if (failed)
            {
                RecordBrowseFailure(loadMore: true);
            }
            else
            {
                ClearBrowseBackoff();
            }

            // A failed index answers with nothing; that is not "no more avatars",
            // and it must not advance the page counter.
            if (failed)
            {
                _indexCounts.Clear();
                _failedIndexes.Clear();
                _failedIndexes.Add("avtr.icu");
                _failedIndexes.Add("VRCNDb");
                GlobalError = "Could not load more community avatars.";
                OnPropertyChanged(nameof(CommunityStatusText));
                return;
            }

            _communityOffset += avtrRows;
            if (!testFetch)
            {
                _browseVrcndbPage++;
            }

            _failedIndexes.Clear();
            _communityPageFull = avtrRows >= 50 || vrcndbRows >= 40;
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
                return;
            }

            // A page of avatars already on screen adds nothing: do not advance the
            // page counter or claim a zero-row page.
            if (added == 0)
            {
                GlobalError = "No new public avatars on that page.";
                return;
            }

            // Keep the index status in step with the browse page just fetched, and
            // report only the avatars that were actually new on screen.
            _browsePage++;
            _browsePageNewCounts.Add(added);
            BumpResultsPageToken();
            SetBrowseStatusCounts(_browsePage);
            OnPropertyChanged(nameof(CommunityStatusText));
            PublishCommunityBrowse();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            RecordBrowseFailure(loadMore: true);
            GlobalError = "Could not load more community avatars.";
        }
        finally
        {
            IsGlobalBusy = false;
            EndAutomaticBrowseRetry();
            EndAutomaticBrowseRetry();
            OnPropertyChanged(nameof(GlobalSummaryText));
            OnPropertyChanged(nameof(HasNextGlobal));
            OnPropertyChanged(nameof(HasPreviousGlobal));
        }
    }

    /// <summary>
    /// Browse may only rewrite the index status line when it is what the user is
    /// looking at: publishing, with no search query owning the tab.
    /// </summary>
    internal static bool ShouldReportBrowseIndexStatus(string searchTerm, bool publish)
        => publish && string.IsNullOrWhiteSpace(searchTerm);

    /// <summary>
    /// True when the browse index actually answered. A failed browse must stay
    /// retryable so its "failed" status cannot outlive the outage.
    /// </summary>
    internal static bool BrowseFetchSucceeded(IReadOnlyDictionary<string, int> probe)
        => !probe.TryGetValue("avtr.icu", out var outcome) || outcome >= 0;

    /// <summary>
    /// Rows the grid shows for one loaded browse page: the cache slice is counted
    /// after the privacy filter, so the status can never exceed the visible cards.
    /// </summary>
    private void SetBrowseStatusCounts(int page)
    {
        _indexCounts.Clear();
        IEnumerable<Avatar> rows = _communityCache;
        if (_browsePageNewCounts.Count > 0)
        {
            var clamped = Math.Clamp(page, 1, _browsePageNewCounts.Count);
            var start = _browsePageNewCounts.Take(clamped - 1).Sum();
            rows = _communityCache
                .Skip(start)
                .Take(_browsePageNewCounts[clamped - 1]);
        }

        foreach (var entry in CountBySource(rows.Where(MatchesPrivacy)))
        {
            _indexCounts[entry.Key] = entry.Value;
        }
    }

    private int VisibleCountForBrowsePage(int page)
    {
        if (_browsePageNewCounts.Count == 0)
        {
            return _communityCache.Count(MatchesPrivacy);
        }

        var clamped = Math.Clamp(page, 1, _browsePageNewCounts.Count);
        var start = _browsePageNewCounts.Take(clamped - 1).Sum();
        return _communityCache
            .Skip(start)
            .Take(_browsePageNewCounts[clamped - 1])
            .Count(MatchesPrivacy);
    }

    private void PublishCommunityBrowse()
    {
        // Browse is paged, not cumulative: Next replaces the grid with the newly
        // fetched cards so the user does not have to pass every earlier page again.
        IEnumerable<Avatar> visible = _communityCache;
        if (_browsePageNewCounts.Count > 0)
        {
            var page = Math.Clamp(_browsePage, 1, _browsePageNewCounts.Count);
            var start = _browsePageNewCounts.Take(page - 1).Sum();
            visible = _communityCache
                .Skip(start)
                .Take(_browsePageNewCounts[page - 1]);
        }

        var rows = visible.Where(MatchesPrivacy).ToList();
        foreach (var avatar in rows)
        {
            ApplyState(avatar, _currentAvatarId);
        }

        GlobalResults.ReplaceAll(rows);

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
           ?? (SelectedAvatar is { } selected &&
               string.Equals(selected.Id, avatarId, StringComparison.Ordinal)
                   ? selected
                   : null)
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
        // A picker left open across a sign-out would sit on the login screen.
        _pendingFavoriteAvatarId = null;
        IsSlotPickerOpen = false;
        FavoritesOnly = false;
        ErrorMessage = null;
        StatusMessage = null;
        NotifySummaryChanged();
        BuildFavorites();
    }
}

/// <summary>One selectable community index in the Avatar Search provider menu.</summary>
public sealed class ProviderFilterOption : ViewModelBase
{
    private readonly Action? _changed;
    private bool _isSelected;

    public ProviderFilterOption(string name, Action? changed = null)
    {
        Name = name;
        _changed = changed;
    }

    public string Name { get; }

    public string DisplayName => Name;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _changed?.Invoke();
            }
        }
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
