using System;
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

        Assert.Equal(6, vm.Resources.Count);
        Assert.Equal(
            "https://vrmodels.store/?do=search&subaction=search&story=cat%20ears",
            Resource(vm, "VRModels").BuildSearchUrl("cat ears"));
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
    public void CreatorCompanionHasNoSearchPage()
    {
        var vm = new CreatorResourcesViewModel();
        var vcc = Resource(vm, "VRChat Creator Companion");

        Assert.False(vcc.CanSearch);
        Assert.Null(vcc.BuildSearchUrl("avatar"));
        Assert.StartsWith("https://", vcc.SiteUrl, StringComparison.Ordinal);
    }

    private static CreatorResource Resource(CreatorResourcesViewModel vm, string name)
        => vm.Resources.Single(resource => resource.Name == name);
}
