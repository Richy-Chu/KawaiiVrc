using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// The Media tab: real pictures from the VRChat pictures folder, grouped by
/// world folder and month, with an in-app viewer, progress while scanning and
/// a cached index so the grid appears immediately on later visits.
/// </summary>
public sealed class MediaLibraryViewModel : ViewModelBase
{
    private readonly Func<string, IProgress<int>?, IReadOnlyList<MediaGroup>> _scan;
    private readonly Action<string> _openPath;
    private readonly string _cachePath;
    private const int PageSize = 48;

    private bool _loaded;
    private bool _isBusy;
    private int _pageIndex;
    private int _foundCount;
    private string? _errorMessage;
    private MediaItem? _selectedMedia;
    private ImageSource? _viewerImage;

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
    }

    public ObservableCollection<MediaGroup> Groups { get; } = new();

    /// <summary>
    /// The fixed 48 card slots used by the page. Cards are reused across pages
    /// so a page flip never builds or abandons a visual tree of decoded images.
    /// </summary>
    public ObservableCollection<MediaCard> PageCards { get; } = new(
        Enumerable.Range(0, PageSize).Select(_ => new MediaCard()));

    public RelayCommand PreviousPageCommand { get; }

    public RelayCommand NextPageCommand { get; }

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

    public bool HasPictures => PictureCount > 0;

    /// <summary>
    /// Only true after a completed scan found nothing, so the empty message never
    /// flashes while the first scan is still running.
    /// </summary>
    public bool IsEmpty => _loaded && !IsBusy && !HasError && !HasPictures;

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
                ? "No VRChat pictures yet"
                : $"{PictureCount} picture{(PictureCount == 1 ? string.Empty : "s")} in {Groups.Count} group{(Groups.Count == 1 ? string.Empty : "s")}";

    public string EmptyMessage => Directory.Exists(RootPath)
        ? "No pictures in the VRChat folder yet. Take a photo in-game and press Refresh."
        : $"No VRChat pictures folder at {RootPath}. Open folder will create it.";

    // ------------------------------------------------------------- viewer

    public MediaItem? SelectedMedia
    {
        get => _selectedMedia;
        private set
        {
            if (SetProperty(ref _selectedMedia, value))
            {
                OnPropertyChanged(nameof(IsViewerOpen));
                OnPropertyChanged(nameof(ViewerTitle));
                OnPropertyChanged(nameof(ViewerImage));
                OpenSelectedInShellCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsViewerOpen => _selectedMedia is not null;

    public string ViewerTitle => _selectedMedia?.FileName ?? string.Empty;

    public ImageSource? ViewerImage
    {
        get => _viewerImage;
        private set => SetProperty(ref _viewerImage, value);
    }

    public void OpenViewer(MediaItem? item)
    {
        if (item is null)
        {
            return;
        }

        ViewerImage = item.LoadBitmap(1400);
        SelectedMedia = item;
    }

    public void CloseViewer()
    {
        SelectedMedia = null;
        ViewerImage = null;
    }

    /// <summary>Moves to the next/previous picture in the flattened library.</summary>
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
        Groups.Clear();
        foreach (var group in groups)
        {
            Groups.Add(group);
        }

        _loaded = true;
        OnPropertyChanged(nameof(PictureCount));
        OnPropertyChanged(nameof(HasPictures));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(EmptyMessage));
        _pageIndex = 0;
        RebuildPage();
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
            }
        }
    }

    public string? Path => _item?.Path;

    public string FileName => _item?.FileName ?? string.Empty;

    public bool HasItem => _item is not null;

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
