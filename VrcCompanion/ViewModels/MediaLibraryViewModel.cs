using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// The Media tab: real pictures from the VRChat pictures folder, grouped by
/// world folder and month, with an in-app viewer, progress while scanning and
/// a cached index so the grid appears immediately on later visits.
///
/// Each picture is matched to the locally recorded instance session that was
/// open when it was taken, which is how the viewer can show the real world and
/// the players who were there (VRChat stores no metadata in the image itself).
/// </summary>
public sealed class MediaLibraryViewModel : ViewModelBase
{
    private readonly Func<string, IProgress<int>?, IReadOnlyList<MediaGroup>> _scan;
    private readonly Action<string> _openPath;
    private readonly string _cachePath;
    private const int PageSize = 48;
    private const int MaximumFilterChips = 14;

    private bool _loaded;
    private bool _isBusy;
    private int _pageIndex;
    private int _pageGeneration;
    private CancellationTokenSource? _viewerHydration;
    private int _foundCount;
    private string? _errorMessage;
    private MediaItem? _selectedMedia;
    private ImageSource? _viewerImage;
    private List<MediaItem> _allItems = new();
    private string _searchText = string.Empty;
    private string _worldFilterKey = string.Empty;
    private string _personFilterKey = string.Empty;

    public MediaLibraryViewModel(
        string? root = null,
        Func<string, IProgress<int>?, IReadOnlyList<MediaGroup>>? scan = null,
        Action<string>? openPath = null,
        string? cachePath = null)
    {
        RootPath = string.IsNullOrWhiteSpace(root) ? MediaLibraryScanner.DefaultRoot : root;
        _scan = scan ?? ((path, progress) => MediaLibraryScanner.Scan(path, progress));
        _openPath = openPath ?? DefaultOpenPath;
        _cachePath = string.IsNullOrWhiteSpace(cachePath) ? MediaIndexCache.DefaultPath : cachePath;

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(force: true), () => !IsBusy);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        OpenViewerCommand = new RelayCommand<MediaItem>(OpenViewer, item => item is not null);
        CloseViewerCommand = new RelayCommand(CloseViewer);
        NextImageCommand = new RelayCommand(() => ShowRelative(1));
        PreviousImageCommand = new RelayCommand(() => ShowRelative(-1));
        OpenSelectedInShellCommand = new RelayCommand(
            () => OpenFile(SelectedMedia?.Path),
            () => SelectedMedia is not null);
        PreviousPageCommand = new RelayCommand(PreviousPage, () => HasPreviousPage);
        NextPageCommand = new RelayCommand(NextPage, () => HasNextPage);
        ToggleWorldFilterCommand = new RelayCommand<MediaFilterOption>(ToggleWorldFilter, option => option is not null);
        TogglePersonFilterCommand = new RelayCommand<MediaFilterOption>(TogglePersonFilter, option => option is not null);
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => HasActiveFilter);
        ShowWorldFilterCommand = new RelayCommand<MediaContext>(ShowWorldFilter, context => context is not null);
        ShowPersonFilterCommand = new RelayCommand<MediaPersonItem>(ShowPersonFilter, person => person is not null);
    }

    /// <summary>
    /// Supplies the signed-in account's recorded sessions. Set by the shell
    /// after login because the account id is only known then.
    /// </summary>
    public Func<IReadOnlyList<InstanceHistoryEntry>>? HistoryProvider { get; set; }

    /// <summary>Looks a group up in what the app already knows, so a group
    /// instance shows its name without a request.</summary>
    public Func<string, (string? Name, string? IconUrl)>? GroupLookup { get; set; }

    /// <summary>Fetches an unknown group so old sessions still name it.</summary>
    public Func<string, Task<(string? Name, string? IconUrl)>>? GroupFetcher { get; set; }

    /// <summary>Fills in the world name/thumbnail when the log row only had the id.</summary>
    public Func<string, Task<MediaWorldInfo?>>? WorldFetcher { get; set; }

    /// <summary>Fills in a player's current avatar when the session only has their name.</summary>
    public Func<string, Task<MediaPersonInfo?>>? PersonFetcher { get; set; }

    public ObservableCollection<MediaGroup> Groups { get; } = new();

    public ObservableCollection<MediaFilterOption> WorldFilters { get; } = new();

    public ObservableCollection<MediaFilterOption> PeopleFilters { get; } = new();

    /// <summary>
    /// The fixed 48 card slots used by the page. Cards are reused across pages
    /// so a page flip never builds or abandons a visual tree of decoded images.
    /// </summary>
    public IReadOnlyList<MediaCard> PageCards { get; } =
        Enumerable.Range(0, PageSize).Select(_ => new MediaCard()).ToList();

    public RelayCommand PreviousPageCommand { get; }

    public RelayCommand NextPageCommand { get; }

    public RelayCommand<MediaFilterOption> ToggleWorldFilterCommand { get; }

    public RelayCommand<MediaFilterOption> TogglePersonFilterCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    public RelayCommand<MediaContext> ShowWorldFilterCommand { get; }

    public RelayCommand<MediaPersonItem> ShowPersonFilterCommand { get; }

    public int PageCount => (PictureCount + PageSize - 1) / PageSize;

    public int CurrentPage => PageCount == 0 ? 0 : _pageIndex + 1;

    public string PageText => PageCount == 0
        ? string.Empty
        : $"Page {CurrentPage} of {PageCount}";

    public bool HasPreviousPage => _pageIndex > 0;

    public bool HasNextPage => PageCount > 0 && _pageIndex < PageCount - 1;

    public string RootPath { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand<MediaItem> OpenViewerCommand { get; }

    public RelayCommand CloseViewerCommand { get; }

    public RelayCommand NextImageCommand { get; }

    public RelayCommand PreviousImageCommand { get; }

    public RelayCommand OpenSelectedInShellCommand { get; }

    // ------------------------------------------------------------- filters

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
            {
                return;
            }

            ApplyFilters();
        }
    }

    public bool HasActiveFilter =>
        !string.IsNullOrWhiteSpace(_searchText) ||
        _worldFilterKey.Length > 0 ||
        _personFilterKey.Length > 0;

    public bool HasWorldFilter => _worldFilterKey.Length > 0;

    public bool HasPersonFilter => _personFilterKey.Length > 0;

    public string FilterSummaryText
    {
        get
        {
            var parts = new List<string>();
            if (_worldFilterKey.Length > 0)
            {
                parts.Add(WorldFilters.FirstOrDefault(option =>
                    string.Equals(option.Key, _worldFilterKey, StringComparison.OrdinalIgnoreCase))?.Label
                    ?? "this world");
            }

            if (_personFilterKey.Length > 0)
            {
                parts.Add("with " + (PeopleFilters.FirstOrDefault(option =>
                    string.Equals(option.Key, _personFilterKey, StringComparison.OrdinalIgnoreCase))?.Label
                    ?? "this person"));
            }

            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                parts.Add($"matching “{_searchText.Trim()}”");
            }

            if (parts.Count == 0)
            {
                return string.Empty;
            }

            return "Showing pictures from " + string.Join(" · ", parts);
        }
    }

    public bool HasFilterSummary => HasActiveFilter;

    public bool HasWorldFilters => WorldFilters.Count > 0;

    public bool HasPeopleFilters => PeopleFilters.Count > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(SummaryText));
                OnPropertyChanged(nameof(LoadingText));
            }
        }
    }

    /// <summary>Pictures found so far in the running scan.</summary>
    public int FoundCount
    {
        get => _foundCount;
        private set
        {
            if (SetProperty(ref _foundCount, value))
            {
                OnPropertyChanged(nameof(LoadingText));
                OnPropertyChanged(nameof(SummaryText));
            }
        }
    }

    public string LoadingText => FoundCount == 0
        ? "Scanning pictures…"
        : $"Scanning pictures… {FoundCount} found";

    public int PictureCount => Groups.Sum(group => group.Items.Count);

    public int TotalPictureCount => _allItems.Count;

    public bool HasPictures => PictureCount > 0;

    /// <summary>
    /// Only true after a completed scan found nothing, so the empty message never
    /// flashes while the first scan is still running.
    /// </summary>
    public bool IsEmpty => _loaded && !IsBusy && !HasError && !HasPictures && !HasActiveFilter;

    public bool IsFilteredEmpty => _loaded && !IsBusy && !HasPictures && HasActiveFilter;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(SummaryText));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string SummaryText => IsBusy
        ? LoadingText
        : HasError
            ? "Pictures could not be loaded"
            : PictureCount == 0
                ? HasActiveFilter ? "No pictures match these filters" : "No VRChat pictures yet"
                : HasActiveFilter
                    ? $"{PictureCount:N0} of {_allItems.Count:N0} pictures"
                    : $"{PictureCount} picture{(PictureCount == 1 ? string.Empty : "s")} in {Groups.Count} group{(Groups.Count == 1 ? string.Empty : "s")}";

    public string EmptyMessage => Directory.Exists(RootPath)
        ? "No pictures in the VRChat folder yet. Take a photo in-game and press Refresh."
        : $"No VRChat pictures folder at {RootPath}. Open folder will create it.";

    public string FilteredEmptyMessage => "No picture matches the current world, person or search.";

    // ------------------------------------------------------------- viewer

    public MediaItem? SelectedMedia
    {
        get => _selectedMedia;
        private set
        {
            if (!SetProperty(ref _selectedMedia, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsViewerOpen));
            OnPropertyChanged(nameof(ViewerTitle));
            OnPropertyChanged(nameof(ViewerImage));
            OnPropertyChanged(nameof(ViewerTakenText));
            OnPropertyChanged(nameof(ViewerContext));
            OnPropertyChanged(nameof(HasViewerContext));
            OnPropertyChanged(nameof(ViewerShowsNoContext));
            OnPropertyChanged(nameof(ViewerWorldName));
            OnPropertyChanged(nameof(ViewerWorldAuthorText));
            OnPropertyChanged(nameof(ViewerWorldThumbnailUrl));
            OnPropertyChanged(nameof(HasViewerWorldThumbnail));
            OnPropertyChanged(nameof(ViewerWorldFolderName));
            OnPropertyChanged(nameof(CanOpenViewerWorld));
            OnPropertyChanged(nameof(ViewerAccessLabel));
            OnPropertyChanged(nameof(ViewerHasGroup));
            OnPropertyChanged(nameof(ViewerShowsAccessChip));
            OnPropertyChanged(nameof(CanOpenViewerGroup));
            OnPropertyChanged(nameof(ViewerGroupName));
            OnPropertyChanged(nameof(ViewerGroupIconUrl));
            OnPropertyChanged(nameof(ViewerSessionText));
            OnPropertyChanged(nameof(CanOpenViewerSession));
            OnPropertyChanged(nameof(ViewerPeople));
            OnPropertyChanged(nameof(HasViewerPeople));
            OnPropertyChanged(nameof(ViewerShowsNoPlayers));
            OnPropertyChanged(nameof(ViewerPeopleCountText));
            OnPropertyChanged(nameof(ViewerContextNote));
            OnPropertyChanged(nameof(CanFilterByViewerWorld));
            OpenSelectedInShellCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsViewerOpen => _selectedMedia is not null;

    public string ViewerTitle => _selectedMedia?.FileName ?? string.Empty;

    public string ViewerTakenText => _selectedMedia is null
        ? string.Empty
        : $"Taken {_selectedMedia.TakenAt:MMM d, yyyy · HH:mm}";

    public ImageSource? ViewerImage
    {
        get => _viewerImage;
        private set => SetProperty(ref _viewerImage, value);
    }

    public MediaContext? ViewerContext => _selectedMedia?.Context;

    public bool HasViewerContext => ViewerContext is not null;

    /// <summary>True when the picture predates any recorded session.</summary>
    public bool ViewerShowsNoContext => !HasViewerContext;

    public string ViewerWorldName
    {
        get
        {
            if (ViewerContext is { } context)
            {
                if (!string.IsNullOrWhiteSpace(context.WorldName))
                {
                    return context.WorldName;
                }

                // The log records the world id first; the name arrives from the
                // API a moment later, so say that instead of "Unknown world".
                if (!string.IsNullOrWhiteSpace(context.WorldId))
                {
                    return "Loading world…";
                }
            }

            return _selectedMedia?.WorldName ?? string.Empty;
        }
    }

    public string ViewerWorldAuthorText => ViewerContext?.WorldAuthorText ?? string.Empty;

    public string ViewerWorldThumbnailUrl => ViewerContext?.WorldThumbnailUrl ?? string.Empty;

    public bool HasViewerWorldThumbnail => !string.IsNullOrWhiteSpace(ViewerWorldThumbnailUrl);

    /// <summary>The folder name, shown when there is no API thumbnail to draw.</summary>
    public string ViewerWorldFolderName => _selectedMedia?.WorldName ?? string.Empty;

    public bool CanOpenViewerWorld => ViewerContext?.HasWorld == true;

    /// <summary>Public, Friends, Friends+, Invite, Invite+, Group, Group+ or Group public.</summary>
    public string ViewerAccessLabel => ViewerContext?.AccessLabel ?? string.Empty;

    public bool ViewerHasGroup => ViewerContext?.HasGroup == true;

    /// <summary>The access chip is only needed when there is no group row to carry it.</summary>
    public bool ViewerShowsAccessChip => HasViewerContext && !ViewerHasGroup;

    public bool CanOpenViewerGroup => ViewerContext?.CanOpenGroup == true;

    public string ViewerGroupName => ViewerContext?.GroupDisplayName ?? string.Empty;

    public string ViewerGroupIconUrl => ViewerContext?.GroupIconUrl ?? string.Empty;

    /// <summary>The viewer's world/player lookups; exposed so tests can await them.</summary>
    internal Task ViewerHydration { get; private set; } = Task.CompletedTask;

    public bool CanFilterByViewerWorld => !string.IsNullOrWhiteSpace(ViewerContext?.WorldName);

    public string ViewerSessionText => ViewerContext?.SessionText ?? string.Empty;

    public bool CanOpenViewerSession => ViewerContext?.HasSession == true;

    public IReadOnlyList<MediaPersonItem> ViewerPeople =>
        ViewerContext?.People ?? (IReadOnlyList<MediaPersonItem>)Array.Empty<MediaPersonItem>();

    public bool HasViewerPeople => ViewerPeople.Count > 0;

    public bool ViewerShowsNoPlayers => HasViewerContext && !HasViewerPeople;

    public string ViewerPeopleCountText => ViewerContext?.PeopleCountText ?? string.Empty;

    public string ViewerContextNote =>
        "VRChat does not save the world inside the picture. This one has no recorded session yet — the Companion matches pictures to your local instance history, which starts from the first world you joined with it installed.";

    public void OpenViewer(MediaItem? item)
    {
        if (item is null)
        {
            return;
        }

        ViewerImage = item.LoadBitmap(1400);
        SelectedMedia = item;
        RestartViewerHydration(item.Context);
    }

    /// <summary>
    /// The instance history stores what the log saw: a world id and player
    /// names. Name the world and load the player icons the way the instance
    /// history popup does, cancelling when the viewer moves on.
    /// </summary>
    private void RestartViewerHydration(MediaContext? context)
    {
        _viewerHydration?.Cancel();
        _viewerHydration?.Dispose();
        _viewerHydration = null;
        if (context is null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _viewerHydration = cancellation;
        ViewerHydration = HydrateViewerAsync(context, cancellation.Token);
    }

    private async Task HydrateViewerAsync(MediaContext context, CancellationToken token)
    {
        try
        {
            await HydrateViewerGroupAsync(context, token);
            await HydrateViewerWorldAsync(context, token);
            await HydrateViewerPeopleAsync(context, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HydrateViewerWorldAsync(MediaContext context, CancellationToken token)
    {
        var fetcher = WorldFetcher;
        if (fetcher is null || string.IsNullOrWhiteSpace(context.WorldId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(context.WorldName) &&
            !string.IsNullOrWhiteSpace(context.WorldThumbnailUrl) &&
            !string.IsNullOrWhiteSpace(context.WorldAuthorName))
        {
            return;
        }

        var world = await fetcher(context.WorldId);
        if (world is null || token.IsCancellationRequested)
        {
            return;
        }

        context.ApplyWorldInfo(world.Name, world.ThumbnailUrl, world.AuthorName);
        if (!ReferenceEquals(ViewerContext, context))
        {
            return;
        }

        OnPropertyChanged(nameof(ViewerWorldName));
        OnPropertyChanged(nameof(ViewerWorldAuthorText));
        OnPropertyChanged(nameof(ViewerWorldThumbnailUrl));
        OnPropertyChanged(nameof(HasViewerWorldThumbnail));
        OnPropertyChanged(nameof(CanOpenViewerWorld));
        OnPropertyChanged(nameof(CanFilterByViewerWorld));
    }

    private async Task HydrateViewerPeopleAsync(MediaContext context, CancellationToken token)
    {
        var fetcher = PersonFetcher;
        if (fetcher is null)
        {
            return;
        }

        foreach (var person in context.People)
        {
            if (token.IsCancellationRequested || !ReferenceEquals(ViewerContext, context))
            {
                return;
            }

            if (!person.HasUserId || person.HasAvatar)
            {
                continue;
            }

            MediaPersonInfo? info;
            try
            {
                info = await fetcher(person.UserId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"media avatar {person.UserId}: {ex.Message}");
                continue;
            }

            if (info is not null)
            {
                person.ApplyProfile(info.DisplayName, info.AvatarUrl);
            }
        }
    }

    /// <summary>
    /// Older sessions only store the group id. Ask for its name/icon once and
    /// update the open viewer, so the panel never says "Group instance" for a
    /// group the account can still resolve.
    /// </summary>
    private async Task HydrateViewerGroupAsync(MediaContext? context, CancellationToken token)
    {
        if (context is null || !context.HasGroup || !string.IsNullOrWhiteSpace(context.GroupName))
        {
            return;
        }

        var fetcher = GroupFetcher;
        if (fetcher is null)
        {
            return;
        }

        try
        {
            var (name, iconUrl) = await fetcher(context.GroupId);
            if (string.IsNullOrWhiteSpace(name) || token.IsCancellationRequested)
            {
                return;
            }

            context.ApplyGroupInfo(name, iconUrl);
            if (!ReferenceEquals(ViewerContext, context))
            {
                return;
            }

            OnPropertyChanged(nameof(ViewerGroupName));
            OnPropertyChanged(nameof(ViewerGroupIconUrl));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"media group {context.GroupId}: {ex.Message}");
        }
    }

    public void CloseViewer()
    {
        _viewerHydration?.Cancel();
        _viewerHydration?.Dispose();
        _viewerHydration = null;
        SelectedMedia = null;
        ViewerImage = null;
    }

    /// <summary>Moves to the next/previous picture in the filtered library.</summary>
    public void ShowRelative(int offset)
    {
        var items = Groups.SelectMany(group => group.Items).ToList();
        if (items.Count == 0 || SelectedMedia is null)
        {
            return;
        }

        var index = items.FindIndex(item => string.Equals(item.Path, SelectedMedia.Path, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        var next = items[(index + offset + items.Count) % items.Count];
        OpenViewer(next);
    }

    // ------------------------------------------------------------- filters

    public void ToggleWorldFilter(MediaFilterOption? option)
    {
        if (option is null)
        {
            return;
        }

        _worldFilterKey = string.Equals(_worldFilterKey, option.Key, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : option.Key;
        // A world and a person filter at once is almost always empty; keeping
        // one dimension at a time is what VRCX's gallery does.
        if (_worldFilterKey.Length > 0)
        {
            _personFilterKey = string.Empty;
        }

        ApplyFilters();
    }

    public void TogglePersonFilter(MediaFilterOption? option)
    {
        if (option is null)
        {
            return;
        }

        _personFilterKey = string.Equals(_personFilterKey, option.Key, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : option.Key;
        if (_personFilterKey.Length > 0)
        {
            _worldFilterKey = string.Empty;
        }

        ApplyFilters();
    }

    public void ShowWorldFilter(MediaContext? context)
    {
        if (context is null)
        {
            return;
        }

        var key = WorldKeyFor(context.WorldId, context.WorldName);
        var option = WorldFilters.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            return;
        }

        _worldFilterKey = option.Key;
        _personFilterKey = string.Empty;
        CloseViewer();
        ApplyFilters();
    }

    public void ShowPersonFilter(MediaPersonItem? person)
    {
        if (person is null)
        {
            return;
        }

        var key = PersonKeyFor(person.UserId, person.DisplayName);
        var option = PeopleFilters.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            return;
        }

        _personFilterKey = option.Key;
        _worldFilterKey = string.Empty;
        CloseViewer();
        ApplyFilters();
    }

    public void ClearFilters()
    {
        _searchText = string.Empty;
        _worldFilterKey = string.Empty;
        _personFilterKey = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        ApplyFilters();
    }

    private void ApplyFilters(bool resetPage = false)
    {
        IEnumerable<MediaItem> items = _allItems;
        if (_worldFilterKey.Length > 0)
        {
            items = items.Where(item =>
                string.Equals(
                    WorldKeyFor(item.Context?.WorldId, item.DisplayWorldName),
                    _worldFilterKey,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (_personFilterKey.Length > 0)
        {
            items = items.Where(item =>
                item.Context?.People.Any(person =>
                    string.Equals(
                        PersonKeyFor(person.UserId, person.DisplayName),
                        _personFilterKey,
                        StringComparison.OrdinalIgnoreCase)) == true);
        }

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var term = _searchText.Trim();
            items = items.Where(item => MatchesSearch(item, term));
        }

        Groups.Clear();
        foreach (var group in BuildGroupsInOrder(items))
        {
            Groups.Add(group);
        }

        _pageIndex = resetPage
            ? 0
            : Math.Max(0, Math.Min(_pageIndex, Math.Max(0, PageCount - 1)));
        SyncFilterSelection();
        OnPropertyChanged(nameof(PictureCount));
        OnPropertyChanged(nameof(TotalPictureCount));
        OnPropertyChanged(nameof(HasPictures));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(HasWorldFilter));
        OnPropertyChanged(nameof(HasPersonFilter));
        OnPropertyChanged(nameof(HasFilterSummary));
        OnPropertyChanged(nameof(FilterSummaryText));
        ClearFiltersCommand.RaiseCanExecuteChanged();
        RebuildPage();
    }

    /// <summary>
    /// Groups the filtered pictures without re-sorting them: the scan (and its
    /// cache) already decided the order, so filtering must not shuffle the grid.
    /// </summary>
    private static List<MediaGroup> BuildGroupsInOrder(IEnumerable<MediaItem> items)
    {
        var order = new List<(string World, DateTime Month, List<MediaItem> Items)>();
        var index = new Dictionary<(string World, DateTime Month), List<MediaItem>>();
        foreach (var item in items)
        {
            var month = new DateTime(item.TakenAt.Year, item.TakenAt.Month, 1);
            var key = (item.DisplayWorldName, month);
            if (!index.TryGetValue(key, out var bucket))
            {
                bucket = new List<MediaItem>();
                index[key] = bucket;
                order.Add((item.DisplayWorldName, month, bucket));
            }

            bucket.Add(item);
        }

        return order
            .Select(group => new MediaGroup(
                $"{group.World} · {group.Month:MMMM yyyy}",
                group.World,
                group.Month,
                group.Items))
            .ToList();
    }

    private static bool MatchesSearch(MediaItem item, string term)
    {
        bool Match(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Contains(term, StringComparison.CurrentCultureIgnoreCase);

        return Match(item.FileName) ||
               Match(item.DisplayWorldName) ||
               Match(item.Context?.WorldAuthorName) ||
               Match(item.TakenAt.ToString("MMMM yyyy")) ||
               Match(item.TakenAt.ToString("yyyy-MM-dd")) ||
               item.Context?.People.Any(person => Match(person.DisplayName)) == true;
    }

    private void RebuildFilters()
    {
        var previousWorld = _worldFilterKey;
        var previousPerson = _personFilterKey;
        WorldFilters.Clear();
        PeopleFilters.Clear();

        foreach (var group in _allItems
                     .Where(item => item.Context is not null)
                     .GroupBy(item => WorldKeyFor(item.Context!.WorldId, item.DisplayWorldName))
                     .Select(group => new
                     {
                         Key = group.Key,
                         Label = group.Select(item => item.DisplayWorldName)
                             .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Unknown world",
                         ImageUrl = group.Select(item => item.Context?.WorldThumbnailUrl ?? string.Empty)
                             .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? string.Empty,
                         Count = group.Count()
                     })
                     .OrderByDescending(group => group.Count)
                     .ThenBy(group => group.Label, StringComparer.CurrentCultureIgnoreCase)
                     .Take(MaximumFilterChips))
        {
            WorldFilters.Add(new MediaFilterOption(group.Key, group.Label, group.ImageUrl, group.Count));
        }

        foreach (var group in _allItems
                     .Where(item => item.Context is not null)
                     .SelectMany(item => item.Context!.People)
                     .GroupBy(person => PersonKeyFor(person.UserId, person.DisplayName))
                     .Select(group => new
                     {
                         Key = group.Key,
                         Label = group.Select(person => person.DisplayName)
                             .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Unknown player",
                         ImageUrl = group.Select(person => person.AvatarUrl)
                             .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? string.Empty,
                         Count = group.Count()
                     })
                     .OrderByDescending(group => group.Count)
                     .ThenBy(group => group.Label, StringComparer.CurrentCultureIgnoreCase)
                     .Take(MaximumFilterChips))
        {
            PeopleFilters.Add(new MediaFilterOption(group.Key, group.Label, group.ImageUrl, group.Count));
        }

        if (WorldFilters.All(option => !string.Equals(option.Key, previousWorld, StringComparison.OrdinalIgnoreCase)))
        {
            _worldFilterKey = string.Empty;
        }

        if (PeopleFilters.All(option => !string.Equals(option.Key, previousPerson, StringComparison.OrdinalIgnoreCase)))
        {
            _personFilterKey = string.Empty;
        }

        SyncFilterSelection();
        OnPropertyChanged(nameof(HasWorldFilters));
        OnPropertyChanged(nameof(HasPeopleFilters));
    }

    private void SyncFilterSelection()
    {
        foreach (var option in WorldFilters)
        {
            option.IsSelected = string.Equals(option.Key, _worldFilterKey, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var option in PeopleFilters)
        {
            option.IsSelected = string.Equals(option.Key, _personFilterKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string WorldKeyFor(string? worldId, string? worldName) =>
        !string.IsNullOrWhiteSpace(worldId)
            ? worldId
            : "name:" + (worldName ?? string.Empty).Trim();

    private static string PersonKeyFor(string? userId, string? displayName) =>
        !string.IsNullOrWhiteSpace(userId)
            ? userId
            : "name:" + (displayName ?? string.Empty).Trim();

    // ------------------------------------------------------------- loading

    public async Task EnsureLoadedAsync()
    {
        if (!_loaded && !IsBusy)
        {
            // Instant paint from the last scan; the live scan below corrects it.
            var cached = MediaIndexCache.Load(RootPath, _cachePath);
            if (cached.Count > 0)
            {
                Apply(cached);
            }
        }

        await LoadAsync(force: true);
    }

    public async Task LoadAsync(bool force)
    {
        if (IsBusy || (_loaded && !force))
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        FoundCount = 0;

        try
        {
            var progress = new Progress<int>(count => FoundCount = count);
            var groups = await Task.Run(() => _scan(RootPath, progress));
            Apply(groups);
            if (groups.Count > 0)
            {
                MediaIndexCache.Save(RootPath, groups, _cachePath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not read the VRChat pictures folder. Check the path, then retry.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(IReadOnlyList<MediaGroup> groups)
    {
        // Keep the scanner's order (newest first); filtering re-groups without
        // re-sorting so a page never shuffles under the pointer.
        _allItems = groups.SelectMany(group => group.Items).ToList();
        AttachContexts();
        _loaded = true;
        RebuildFilters();
        ApplyFilters(resetPage: true);
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>
    /// Matches every picture to the recorded session that was live when it was
    /// taken. Runs on the scan result, so the first paint already has metadata.
    /// </summary>
    private void AttachContexts()
    {
        var provider = HistoryProvider;
        if (provider is null)
        {
            return;
        }

        try
        {
            var sessions = provider();
            if (sessions.Count == 0)
            {
                return;
            }

            var byPath = new Dictionary<string, MediaItem>(StringComparer.Ordinal);
            foreach (var item in _allItems)
            {
                byPath[item.Path] = item;
            }

            foreach (var pair in MediaMetadataResolver.Build(_allItems, sessions, groupLookup: GroupLookup))
            {
                if (byPath.TryGetValue(pair.Key, out var item))
                {
                    item.Context = pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            // Metadata is a bonus; a damaged history store must not break Media.
            Debug.WriteLine($"media metadata: {ex}");
        }
    }

    private void PreviousPage()
    {
        if (!HasPreviousPage)
        {
            return;
        }

        _pageIndex--;
        RebuildPage();
    }

    private void NextPage()
    {
        if (!HasNextPage)
        {
            return;
        }

        _pageIndex++;
        RebuildPage();
    }

    private void RebuildPage()
    {
        var pageItems = new List<(MediaItem Item, string GroupTitle)>(PageSize);
        var skip = _pageIndex * PageSize;
        var take = PageSize;

        foreach (var group in Groups)
        {
            if (take <= 0)
            {
                break;
            }

            if (skip >= group.Items.Count)
            {
                skip -= group.Items.Count;
                continue;
            }

            foreach (var item in group.Items.Skip(skip).Take(take))
            {
                pageItems.Add((item, group.Title));
                take--;
            }

            skip = 0;
        }

        for (var i = 0; i < PageCards.Count; i++)
        {
            if (i >= pageItems.Count)
            {
                PageCards[i].Set(null, null);
                continue;
            }

            // A group heading labels the first card in that group on this page;
            // the rest of the row stays visually compact.
            var groupTitle = i == 0 ||
                             !string.Equals(pageItems[i].GroupTitle, pageItems[i - 1].GroupTitle, StringComparison.Ordinal)
                ? pageItems[i].GroupTitle
                : null;
            PageCards[i].Set(pageItems[i].Item, groupTitle);
        }

        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();

        // WPF bitmaps are finalized off the UI thread. Debounce a background
        // collection so rapid page flips do not queue collections, while the
        // UI thread is never blocked by one.
        var generation = Interlocked.Increment(ref _pageGeneration);
        _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            if (Volatile.Read(ref _pageGeneration) != generation)
            {
                return;
            }

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        });
    }

    // ------------------------------------------------------------- actions

    private void OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        _openPath(path);
    }

    private void OpenFolder()
    {
        try
        {
            if (!Directory.Exists(RootPath))
            {
                // VRChat only creates this folder after the first in-game photo.
                Directory.CreateDirectory(RootPath);
            }

            _openPath(RootPath);
            OnPropertyChanged(nameof(EmptyMessage));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = $"Could not open the pictures folder: {ex.Message}";
        }
    }

    private static void DefaultOpenPath(string path)
        => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}

/// <summary>
/// One reusable media card. The same 48 card objects are updated when the page
/// changes, so the visual tree and its AsyncImage controls are not recreated.
/// </summary>
public sealed class MediaCard : ViewModelBase
{
    private MediaItem? _item;
    private string? _groupTitle;

    public MediaItem? Item
    {
        get => _item;
        private set
        {
            if (SetProperty(ref _item, value))
            {
                OnPropertyChanged(nameof(Path));
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(HasItem));
                OnPropertyChanged(nameof(WorldBadgeText));
                OnPropertyChanged(nameof(HasWorldBadge));
            }
        }
    }

    public string? Path => _item?.Path;

    public string FileName => _item?.FileName ?? string.Empty;

    public bool HasItem => _item is not null;

    /// <summary>The recorded world name, shown as a caption over the card.</summary>
    public string WorldBadgeText => _item?.Context is { HasWorldName: true } context
        ? context.WorldName
        : string.Empty;

    public bool HasWorldBadge => !string.IsNullOrWhiteSpace(WorldBadgeText);

    public string? GroupTitle
    {
        get => _groupTitle;
        private set
        {
            if (SetProperty(ref _groupTitle, value))
            {
                OnPropertyChanged(nameof(HasGroupTitle));
            }
        }
    }

    public bool HasGroupTitle => !string.IsNullOrWhiteSpace(GroupTitle);

    internal void Set(MediaItem? item, string? groupTitle)
    {
        Item = item;
        GroupTitle = groupTitle;
    }
}
