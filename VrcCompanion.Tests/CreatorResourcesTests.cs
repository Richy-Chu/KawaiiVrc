using System;
using System.IO;
using System.Linq;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class CreatorResourcesTests
{
    [Fact]
    public void CreatorSitesExposeTheirSearchLinks()
    {
        var vm = new CreatorResourcesViewModel();

        Assert.Equal(9, vm.Resources.Count);
        Assert.Equal(
            "https://vrmodels.store/?do=search&subaction=search&story=cat%20ears",
            Resource(vm, "VRModels").BuildSearchUrl("cat ears"));
        Assert.Equal(
            "https://forum.ripper.store/search?q=cat%20ears",
            Resource(vm, "Ripper Store").BuildSearchUrl("cat ears"));
        Assert.Equal(
            "https://vrcmods.com/search?q=cat%20ears",
            Resource(vm, "VRCMods").BuildSearchUrl("cat ears"));
        Assert.Equal(
            "https://booth.pm/en/search/cat%20ears",
            Resource(vm, "BOOTH").BuildSearchUrl("cat ears"));
        Assert.Equal(
            "https://jinxxy.com/market/browse?search=cat%20ears",
            Resource(vm, "Jinxxy").BuildSearchUrl("cat ears"));
        Assert.Equal(
            "https://gumroad.com/discover?query=cat%20ears",
            Resource(vm, "Gumroad").BuildSearchUrl("cat ears"));
    }

    [Fact]
    public void OpenOnlyToolsHaveNoSearchPage()
    {
        var vm = new CreatorResourcesViewModel();
        var kawaii = Resource(vm, "Kawaii Studio");
        var vcc = Resource(vm, "VRChat Creator Companion");
        var alcom = Resource(vm, "ALCOM");

        Assert.False(kawaii.CanSearch);
        Assert.False(vcc.CanSearch);
        Assert.False(alcom.CanSearch);
        Assert.Null(kawaii.BuildSearchUrl("avatar"));
        Assert.Null(vcc.BuildSearchUrl("avatar"));
        Assert.Null(alcom.BuildSearchUrl("avatar"));
        Assert.Equal("https://archive.kawaiistudio.org/login", kawaii.SiteUrl);
        Assert.StartsWith("https://", vcc.SiteUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://", alcom.SiteUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchNavigatesInTheEmbeddedBrowser()
    {
        var vm = new CreatorResourcesViewModel { SearchQuery = "cat ears" };
        string? navigatedUrl = null;
        vm.BrowserNavigationRequested += url => navigatedUrl = url;

        vm.SearchCommand.Execute(Resource(vm, "VRModels"));

        Assert.True(vm.IsBrowserOpen);
        Assert.Equal("VRModels", vm.BrowserTitle);
        Assert.Equal(
            "https://vrmodels.store/?do=search&subaction=search&story=cat%20ears",
            vm.BrowserUrl);
        Assert.Equal(vm.BrowserUrl, navigatedUrl);
    }

    [Fact]
    public void BrowseOpensTheToolPageInTheEmbeddedBrowser()
    {
        var vm = new CreatorResourcesViewModel();
        string? navigatedUrl = null;
        vm.BrowserNavigationRequested += url => navigatedUrl = url;

        vm.BrowseCommand.Execute(Resource(vm, "VRChat Creator Companion"));

        Assert.True(vm.IsBrowserOpen);
        Assert.Equal("https://vcc.docs.vrchat.com/", vm.BrowserUrl);
        Assert.Equal(vm.BrowserUrl, navigatedUrl);
    }

    [Fact]
    public void KawaiiStudioLoginOpensInTheEmbeddedBrowser()
    {
        var vm = new CreatorResourcesViewModel();
        string? navigatedUrl = null;
        vm.BrowserNavigationRequested += url => navigatedUrl = url;

        vm.BrowseCommand.Execute(Resource(vm, "Kawaii Studio"));

        Assert.True(vm.IsBrowserOpen);
        Assert.Equal("Kawaii Studio", vm.BrowserTitle);
        Assert.Equal("https://archive.kawaiistudio.org/login", vm.BrowserUrl);
        Assert.Equal(vm.BrowserUrl, navigatedUrl);
    }

    [Fact]
    public void ClosingTheBrowserKeepsResourcesAvailable()
    {
        var vm = new CreatorResourcesViewModel();
        vm.BrowseCommand.Execute(Resource(vm, "ALCOM"));

        vm.CloseBrowserCommand.Execute(null);

        Assert.False(vm.IsBrowserOpen);
        Assert.Empty(vm.BrowserError);
        Assert.Equal(9, vm.Resources.Count);
    }

    [Fact]
    public void CompletedDownloadExposesItsFolderAction()
    {
        var vm = new CreatorResourcesViewModel();
        var path = @"D:\Downloads\avatar.zip";

        vm.SetDownloadStatus($"Downloaded to {path}", downloadedPath: path);

        Assert.True(vm.CanOpenDownloadFolder);

        vm.SetDownloadStatus("Download failed: access was denied.", true);

        Assert.False(vm.CanOpenDownloadFolder);
    }

    [Fact]
    public void OpenFolderReportsWhenTheDownloadAndItsFolderAreGone()
    {
        var vm = new CreatorResourcesViewModel();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "avatar.zip");

        vm.SetDownloadStatus($"Downloaded to {path}", downloadedPath: path);
        vm.OpenDownloadFolderCommand.Execute(null);

        Assert.False(vm.CanOpenDownloadFolder);
        Assert.True(vm.DownloadStatusIsError);
        Assert.Equal("The downloaded file and its folder are no longer available.", vm.DownloadStatus);
    }

    private static CreatorResource Resource(CreatorResourcesViewModel vm, string name)
        => vm.Resources.Single(resource => resource.Name == name);
}
