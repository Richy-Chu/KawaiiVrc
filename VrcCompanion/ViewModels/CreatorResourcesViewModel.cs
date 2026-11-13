using System;
using System.Collections.ObjectModel;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Curated creator sites. Open site stays in the default browser; search and
/// tool pages can optionally use the embedded browser. Sign-in and downloads
/// stay on the site itself.
/// </summary>
public sealed class CreatorResourcesViewModel : ViewModelBase
{
    private string _searchQuery = string.Empty;
    private bool _isBrowserOpen;
    private string _browserUrl = string.Empty;
    private string _browserTitle = string.Empty;
    private string _browserError = string.Empty;

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

    public RelayCommand<CreatorResource> OpenCommand { get; }

    public RelayCommand<CreatorResource> SearchCommand { get; }

    public RelayCommand<CreatorResource> BrowseCommand { get; }

    public RelayCommand CloseBrowserCommand { get; }

    public void SetBrowserError(string message)
        => BrowserError = message;

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
        IsBrowserOpen = true;
        BrowserNavigationRequested?.Invoke(url);
    }

    private void CloseBrowser()
    {
        IsBrowserOpen = false;
        BrowserError = string.Empty;
    }
}
