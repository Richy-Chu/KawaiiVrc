using System;
using System.Collections.ObjectModel;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Curated creator sites. Open site stays in the default browser; search and
/// tool pages can optionally use the embedded browser. Sign-in and downloads
/// stay on the site itself.
/// </summary>
public sealed class CreatorResourcesViewModel : ViewModelBase, IEmbeddedBrowserHost
{
    private string _searchQuery = string.Empty;
    private bool _isBrowserOpen;
    private string _browserUrl = string.Empty;
    private string _browserTitle = string.Empty;
    private string _browserError = string.Empty;
    private string _downloadStatus = string.Empty;
    private bool _downloadStatusIsError;
    private string? _downloadedFilePath;
    private bool _isFocusMode;

    public CreatorResourcesViewModel()
    {
        Resources = new ObservableCollection<CreatorResource>
        {
            new(
                "VRModels",
                "Models & avatars",
                "VR",
                "Community model library for VRChat avatars, props, and other assets.",
                "https://vrmodels.store/",
                "https://vrmodels.store/?do=search&subaction=search&story={0}"),
            new(
                "Ripper Store",
                "Community asset forum",
                "R",
                "Community forum for sharing and requesting VRChat avatars, assets, and tools. Search requires a Ripper Store account.",
                "https://forum.ripper.store/",
                "https://forum.ripper.store/search?q={0}"),
            new(
                "VRCMods",
                "Avatars & Unity assets",
                "VM",
                "Free community avatars, props, animations, shaders, and Unity assets.",
                "https://vrcmods.com/",
                "https://vrcmods.com/search?q={0}"),
            new(
                "BOOTH",
                "Japanese creator marketplace",
                "B",
                "Avatars, outfits, accessories, and creator tools from Japanese shops.",
                "https://booth.pm/en/",
                "https://booth.pm/en/search/{0}"),
            new(
                "Jinxxy",
                "Creator marketplace",
                "J",
                "VR avatars, props, worlds, materials, shaders, and creator tools.",
                "https://jinxxy.com/",
                "https://jinxxy.com/market/browse?search={0}"),
            new(
                "Gumroad",
                "Creator storefronts",
                "G",
                "Paid and free VRChat avatars, assets, and tools from individual creators.",
                "https://gumroad.com/",
                "https://gumroad.com/discover?query={0}"),
            new(
                "VRChat Creator Companion",
                "Official VRChat tool",
                "VCC",
                "The official tool for managing VRChat Unity projects, SDKs, and packages.",
                "https://vcc.docs.vrchat.com/"),
            new(
                "ALCOM",
                "Community package manager",
                "A",
                "Open-source alternative to VCC for managing VRChat packages and Unity projects.",
                "https://vrc-get.anatawa12.com/en/alcom")
        };

        OpenCommand = new RelayCommand<CreatorResource>(Open);
        SearchCommand = new RelayCommand<CreatorResource>(Search);
        BrowseCommand = new RelayCommand<CreatorResource>(Browse);
        CloseBrowserCommand = new RelayCommand(CloseBrowser);
        OpenDownloadFolderCommand = new RelayCommand(OpenDownloadFolder);
    }

    /// <summary>Raised when a page should be loaded in the embedded WebView2.</summary>
    public event Action<string>? BrowserNavigationRequested;

    public ObservableCollection<CreatorResource> Resources { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public bool IsBrowserOpen
    {
        get => _isBrowserOpen;
        private set => SetProperty(ref _isBrowserOpen, value);
    }

    public bool IsFocusMode
    {
        get => _isFocusMode;
        set => SetProperty(ref _isFocusMode, value);
    }

    public string BrowserUrl
    {
        get => _browserUrl;
        private set => SetProperty(ref _browserUrl, value);
    }

    public string BrowserTitle
    {
        get => _browserTitle;
        private set => SetProperty(ref _browserTitle, value);
    }

    public string BrowserError
    {
        get => _browserError;
        private set => SetProperty(ref _browserError, value);
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => SetProperty(ref _downloadStatus, value);
    }

    public bool DownloadStatusIsError
    {
        get => _downloadStatusIsError;
        private set => SetProperty(ref _downloadStatusIsError, value);
    }

    public bool CanOpenDownloadFolder => !string.IsNullOrWhiteSpace(_downloadedFilePath);

    public RelayCommand<CreatorResource> OpenCommand { get; }

    public RelayCommand<CreatorResource> SearchCommand { get; }

    public RelayCommand<CreatorResource> BrowseCommand { get; }

    public RelayCommand CloseBrowserCommand { get; }

    public RelayCommand OpenDownloadFolderCommand { get; }

    public void SetBrowserLocation(string url, string title)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            BrowserUrl = url;
        }
    }

    public void SetBrowserError(string message)
        => BrowserError = message;

    public void SetDownloadStatus(string message, bool isError = false, string? downloadedPath = null)
    {
        DownloadStatus = message;
        DownloadStatusIsError = isError;
        SetDownloadedFilePath(downloadedPath);
    }

    /// <summary>Shows the browser pane without starting a navigation for layout tests.</summary>
    internal void ShowBrowserForLayoutTest() => IsBrowserOpen = true;

    private void SetDownloadedFilePath(string? path)
    {
        if (string.Equals(_downloadedFilePath, path, StringComparison.Ordinal))
        {
            return;
        }

        _downloadedFilePath = path;
        OnPropertyChanged(nameof(CanOpenDownloadFolder));
    }

    private void Open(CreatorResource? resource)
    {
        if (resource is not null)
        {
            LaunchService.OpenUrl(resource.SiteUrl);
        }
    }

    private void Search(CreatorResource? resource)
    {
        if (resource is null)
        {
            return;
        }

        OpenBrowser(resource, resource.BuildSearchUrl(SearchQuery) ?? resource.SiteUrl);
    }

    private void Browse(CreatorResource? resource)
    {
        if (resource is not null)
        {
            OpenBrowser(resource, resource.SiteUrl);
        }
    }

    private void OpenBrowser(CreatorResource resource, string url)
    {
        BrowserTitle = resource.Name;
        BrowserUrl = url;
        BrowserError = string.Empty;
        SetDownloadStatus(string.Empty);
        IsFocusMode = false;
        IsBrowserOpen = true;
        BrowserNavigationRequested?.Invoke(url);
    }

    private void CloseBrowser()
    {
        IsBrowserOpen = false;
        IsFocusMode = false;
        BrowserError = string.Empty;
        SetDownloadStatus(string.Empty);
    }

    private void OpenDownloadFolder()
    {
        if (!LaunchService.OpenFolder(_downloadedFilePath))
        {
            SetDownloadStatus("The downloaded file and its folder are no longer available.", true);
        }
    }
}
