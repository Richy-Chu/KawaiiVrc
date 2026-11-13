using System.Collections.ObjectModel;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Curated external creator sites. This app only opens their public pages;
/// sign-in and downloads stay on the site itself.
/// </summary>
public sealed class CreatorResourcesViewModel : ViewModelBase
{
    private string _searchQuery = string.Empty;

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
                "https://vcc.docs.vrchat.com/")
        };

        OpenCommand = new RelayCommand<CreatorResource>(Open);
        SearchCommand = new RelayCommand<CreatorResource>(Search);
    }

    public ObservableCollection<CreatorResource> Resources { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public RelayCommand<CreatorResource> OpenCommand { get; }

    public RelayCommand<CreatorResource> SearchCommand { get; }

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

        LaunchService.OpenUrl(resource.BuildSearchUrl(SearchQuery) ?? resource.SiteUrl);
    }
}
