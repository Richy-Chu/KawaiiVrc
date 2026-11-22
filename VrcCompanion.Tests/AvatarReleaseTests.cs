using System.Collections.Generic;
using System.Linq;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

[Collection(Seams.Collection)]
[ResetSeams]
public class AvatarReleaseTests
{
    [Fact]
    public void ReleasingFavouritesDropsRowsButKeepsTheStarsEverywhereElse()
    {
        using var api = new VrcApiClient();
        var vm = new AvatarViewModel(api);
        var favourite = new Avatar { Id = "avtr_fav", Name = "Favourite" };
        vm.SeedLibraryForTests(new[] { favourite });
        vm.SeedFavoritesForTests(new[] { favourite });

        Assert.Single(vm.FavoriteAvatars);
        Assert.True(favourite.IsFavorite);
        Assert.True(vm.HasFavorites);

        vm.ReleaseFavoritesData();

        // The rows go with the page...
        Assert.Empty(vm.FavoriteAvatars);
        Assert.Empty(vm.FavoriteSlots);

        // ...but the record set stays, so stars in the library, the details panel
        // and the slot picker still see this avatar as favourited.
        Assert.True(vm.HasFavorites);
        Assert.True(favourite.IsFavorite);
    }

    [Fact]
    public void SlotPickerStillOffersEverySlotAfterFavouritesAreReleased()
    {
        using var api = new VrcApiClient();
        var vm = new AvatarViewModel(api);
        var favourite = new Avatar { Id = "avtr_fav", Name = "Favourite" };
        vm.SeedLibraryForTests(new[] { favourite });
        vm.SeedFavoritesForTests(new[] { favourite });

        vm.ReleaseFavoritesData();
        vm.OpenSlotPickerForTests("avtr_fav");

        // The picker used to come up as an empty dialog when its rows were
        // cleared while it was still on screen.
        Assert.True(vm.IsSlotPickerOpen);
        Assert.Equal(5, vm.FavoriteSlotChoices.Count);
        Assert.Equal("avatars1", vm.FavoriteSlotChoices[0].Tag);
    }

    [Fact]
    public void ReleasingTheLibraryKeepsTheRowsTheFavouritesPageStillShows()
    {
        using var api = new VrcApiClient();
        var vm = new AvatarViewModel(api);
        var favourite = new Avatar { Id = "avtr_fav", Name = "Favourite" };
        var other = new Avatar { Id = "avtr_other", Name = "Other" };
        vm.SeedLibraryForTests(new[] { favourite, other });
        vm.SeedFavoritesForTests(new[] { favourite });

        vm.ReleaseLibraryData(keepFavoriteRows: true);

        Assert.False(vm.HasLoaded);
        Assert.Empty(vm.Avatars);
        // The favourite was only reachable through the library; it must survive
        // so the Favourites page does not lose a row it is still displaying.
        Assert.Single(vm.FavoriteAvatars);
        Assert.Equal("avtr_fav", vm.FavoriteAvatars[0].Id);
    }

    [Fact]
    public void ReleasingTheLibraryReloadsOnTheNextVisit()
    {
        using var api = new VrcApiClient();
        var vm = new AvatarViewModel(api);
        var avatar = new Avatar { Id = "avtr_lib", Name = "Library" };
        vm.SeedLibraryForTests(new[] { avatar });

        vm.ReleaseLibraryData(keepFavoriteRows: false);

        Assert.False(vm.HasLoaded);
        Assert.Empty(vm.Avatars);
        Assert.Empty(vm.FavoriteAvatars);
        Assert.Equal("avtr_lib", avatar.Id);
    }
}
