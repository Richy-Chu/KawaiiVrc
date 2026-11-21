using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

[Collection(Seams.Collection)]
[ResetSeams]
public class FriendAvatarProfileTests
{
    private const string UserId = "usr_avatar_profile";

    [Fact]
    public void VrcPlusProfilePictureIsNotShownAsAnAvatar()
    {
        WithStore((store, notes) =>
        {
            var profile = CreateProfile(
                store,
                notes,
                new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Avatar Person",
                    UserIcon = "https://example.test/profile-custom.png",
                    IconUrl = "https://example.test/profile-custom.png",
                    CurrentAvatarImageUrl = "https://example.test/profile-custom.png"
                });

            Assert.Empty(profile.Avatars);
            Assert.False(profile.HasAvatars);
        });
    }

    [Fact]
    public void ProfileKeepsTheLastKnownAvatarIdWhenVrchatOmitsIt()
    {
        var directory = NewDirectory();
        try
        {
            var store = new FriendAvatarStore(Path.Combine(directory, "avatars.json"));
            store.Observe(
                "acct_me",
                UserId,
                "avtr_stored",
                "Stored Avatar",
                "https://api.vrchat.cloud/api/1/file/file_same/1/file",
                "https://api.vrchat.cloud/api/1/file/file_same/1/256");

            using var api = new VrcApiClient();
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Avatar Person",
                    UserIcon = "https://example.test/profile-custom.png",
                    CurrentAvatarImageUrl = "https://api.vrchat.cloud/api/1/file/file_same/1/file"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: store);

            var avatar = Assert.Single(profile.Avatars);
            Assert.Equal("avtr_stored", avatar.AvatarId);
            Assert.Equal("Stored Avatar", avatar.DisplayName);
            Assert.True(avatar.IsCurrent);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingCurrentAvatarShowsProfilePictureInsteadOfAuthoredAvatars()
    {
        var directory = NewDirectory();
        var handler = new AuthorAvatarHandler();
        TestHooks.UseCommunityHandler(handler);
        try
        {
            using var api = new VrcApiClient();
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Avatar Person",
                    IsFriend = false,
                    UserIcon = "https://example.test/profile-custom.png"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));

            // VRChat can return only the profile artwork here. The avatar
            // section must not turn the author's uploads into current avatars.
            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "Avatar Person",
                IconUrl = "https://example.test/profile-custom.png"
            };

            await WaitUntil(() => profile.Avatars.Count > 0);

            var avatar = Assert.Single(profile.Avatars);
            Assert.Equal("Profile picture", avatar.DisplayName);
            Assert.True(avatar.IsProfilePictureFallback);
            Assert.False(avatar.CanOpen);
            Assert.False(profile.HasIndexedAvatars);
            Assert.True(handler.AuthorRequests > 0);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CurrentAvatarIsVisibleBeforeAnyAvatarRequestFinishes()
    {
        WithStore((store, notes) =>
        {
            var profile = CreateProfile(
                store,
                notes,
                new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Avatar Person",
                    CurrentAvatar = "avtr_current",
                    CurrentAvatarImageUrl = "https://example.test/current.png",
                    CurrentAvatarThumbnailImageUrl = "https://example.test/current-thumb.png"
                });

            var avatar = Assert.Single(profile.Avatars);
            Assert.True(profile.HasAvatars);
            Assert.True(avatar.IsCurrent);
            Assert.Equal("avtr_current", avatar.AvatarId);
            Assert.Equal("https://example.test/current-thumb.png", avatar.ThumbnailUrl);
            Assert.True(avatar.CanOpen);
        });
    }

    [Fact]
    public void CurrentAvatarPictureAppearsEvenBeforeTheAvatarIdArrives()
    {
        WithStore((store, notes) =>
        {
            var profile = CreateProfile(
                store,
                notes,
                new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Avatar Person",
                    CurrentAvatarImageUrl = "https://example.test/picture-only.png"
                });

            var avatar = Assert.Single(profile.Avatars);
            Assert.Equal(string.Empty, avatar.AvatarId);
            Assert.True(avatar.HasThumbnail);
            Assert.True(avatar.CanOpen);
            Assert.Equal("https://example.test/picture-only.png", avatar.ThumbnailUrl);

            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "Avatar Person",
                CurrentAvatar = "avtr_now_known",
                CurrentAvatarName = "Now Known",
                CurrentAvatarImageUrl = "https://example.test/picture-only.png"
            };

            var promoted = Assert.Single(profile.Avatars);
            Assert.Equal("avtr_now_known", promoted.AvatarId);
            Assert.Equal("Now Known", promoted.DisplayName);
        });
    }

    [Fact]
    public void ProfileAddsTheCurrentAvatarNameWithoutLosingThePicture()
    {
        WithStore((store, notes) =>
        {
            var profile = CreateProfile(
                store,
                notes,
                new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Avatar Person",
                    CurrentAvatar = "avtr_current",
                    CurrentAvatarThumbnailImageUrl = "https://example.test/current-thumb.png"
                });

            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "Avatar Person",
                CurrentAvatar = "avtr_current",
                CurrentAvatarName = "Cyber Fox",
                CurrentAvatarThumbnailImageUrl = "https://example.test/current-thumb.png"
            };

            var avatar = Assert.Single(profile.Avatars);
            Assert.Equal("Cyber Fox", avatar.DisplayName);
            Assert.Equal("https://example.test/current-thumb.png", avatar.ThumbnailUrl);
            Assert.True(avatar.IsCurrent);
        });
    }

    [Fact]
    public void ClickingAPrivateAvatarOpensTheSharedPopupWithoutWearActions()
    {
        WithStore((store, notes) =>
        {
            var profile = CreateProfile(
                store,
                notes,
                new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Avatar Person",
                    CurrentAvatar = "avtr_private",
                    CurrentAvatarImageUrl = "https://example.test/private.png"
                });
            var item = Assert.Single(profile.Avatars);
            item.ReleaseStatus = "private";

            Avatar? opened = null;
            profile.OpenAvatar = avatar => opened = avatar;
            profile.OpenAvatarCommand.Execute(item);

            Assert.NotNull(opened);
            Assert.Equal("avtr_private", opened!.Id);
            Assert.True(opened.IsUnavailable);
            Assert.Equal("Unavailable", opened.ReleaseStatusLabel);
            Assert.False(opened.CanUse);
            Assert.Equal("https://example.test/private.png", opened.ImageUrl);
        });
    }

    [Fact]
    public async Task NonFriendProfileResolvesPublicAvatarByNameWhenTheIdIsHidden()
    {
        var directory = NewDirectory();
        try
        {
            using var api = new VrcApiClient(null, new NamedAvatarHandler());
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Not A Friend",
                    IsFriend = false,
                    CurrentAvatarImageUrl = "https://example.test/named.png"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));
            // The public profile can expose the avatar name while withholding
            // its id, which is the common non-friend case.
            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "Not A Friend",
                CurrentAvatarName = "Named Public Avatar",
                CurrentAvatarImageUrl = "https://example.test/named.png"
            };

            var item = Assert.Single(profile.Avatars);
            Assert.Equal(string.Empty, item.AvatarId);
            var opened = new TaskCompletionSource<Avatar>(TaskCreationOptions.RunContinuationsAsynchronously);
            profile.OpenAvatar = avatar => opened.TrySetResult(avatar);
            profile.OpenAvatarCommand.Execute(item);

            var avatar = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("public", avatar.ReleaseStatus);
            Assert.Equal("Named Public Avatar", avatar.Name);
            Assert.Equal("Named Author", avatar.AuthorName);
            Assert.Equal("PC · Quest", avatar.PlatformsText);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NonFriendProfileUsesTheSameCurrentAvatarSection()
    {
        WithStore((store, notes) =>
        {
            var profile = CreateProfile(
                store,
                notes,
                new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Not A Friend",
                    IsFriend = false,
                    CurrentAvatar = "avtr_nonfriend",
                    CurrentAvatarImageUrl = "https://example.test/nonfriend.png"
                });

            Assert.False(profile.IsFriend);
            var avatar = Assert.Single(profile.Avatars);
            Assert.Equal("avtr_nonfriend", avatar.AvatarId);
            Assert.True(avatar.HasThumbnail);
        });
    }

    [Fact]
    public void AvatarPopupCanReturnToTheProfileThatOpenedIt()
    {
        var vm = new AvatarViewModel(new VrcApiClient());
        vm.ShowAvatarDetails(
            new Avatar
            {
                Id = "avtr_public",
                Name = "Public Avatar",
                ReleaseStatus = "public"
            },
            returnToProfile: true);

        Assert.True(vm.IsAvatarDetailsOpen);
        Assert.True(vm.CanReturnToProfile);
        Assert.Equal("← Back to profile", vm.AvatarDetailsBackLabel);

        vm.CloseAvatarDetailsCommand.Execute(null);
        Assert.False(vm.IsAvatarDetailsOpen);
        Assert.False(vm.CanReturnToProfile);

        vm.ShowAvatarDetails(new Avatar { Id = "avtr_public", Name = "Public Avatar" });
        Assert.False(vm.CanReturnToProfile);
        Assert.Equal("← Back to all avatars", vm.AvatarDetailsBackLabel);
    }

    [Fact]
    public void ProfileAvatarFavoriteOpensTheSlotPickerWithItsName()
    {
        var vm = new AvatarViewModel(new VrcApiClient());
        vm.ShowAvatarDetails(new Avatar
        {
            Id = "avtr_profile_favorite",
            Name = "Felix",
            ReleaseStatus = "public"
        }, returnToProfile: true);

        vm.ToggleFavoriteCommand.Execute("avtr_profile_favorite");

        Assert.True(vm.IsSlotPickerOpen);
        Assert.Equal("Felix", vm.PendingFavoriteAvatarName);
        Assert.Equal(5, vm.FavoriteSlotChoices.Count);
    }

    [Fact]
    public async Task ProfileAvatarFavoriteCanBeRemovedAfterAdding()
    {
        var handler = new FavoritesHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_favorites", DisplayName = "Me" });
        var vm = new AvatarViewModel(api);
        var avatar = new Avatar
        {
            Id = "avtr_profile_favorite",
            Name = "Felix",
            ReleaseStatus = "public"
        };

        vm.ShowAvatarDetails(avatar, returnToProfile: true);
        await vm.EnsureFavoritesAsync();
        vm.ToggleFavoriteCommand.Execute(avatar.Id);
        Assert.True(vm.IsSlotPickerOpen);

        vm.ChooseFavoriteSlotCommand.Execute("avatars1");
        await WaitUntil(() => avatar.IsFavorite);
        Assert.Equal(1, handler.PostCount);

        vm.ToggleFavoriteCommand.Execute(avatar.Id);
        await WaitUntil(() => !avatar.IsFavorite);
        Assert.Equal(1, handler.DeleteCount);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), "Timed out waiting for the favourite state to change.");
    }

    [Fact]
    public async Task PictureOnlyCardResolvesTheRealAvatarBeforeOpeningThePopup()
    {
        var directory = NewDirectory();
        try
        {
            using var api = new VrcApiClient(null, new LiveAvatarHandler());
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Not A Friend",
                    IsFriend = false,
                    CurrentAvatarImageUrl = "https://example.test/picture-only.png"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));

            var opened = new TaskCompletionSource<Avatar>(TaskCreationOptions.RunContinuationsAsynchronously);
            profile.OpenAvatar = avatar => opened.TrySetResult(avatar);
            profile.OpenAvatarCommand.Execute(Assert.Single(profile.Avatars));

            var avatar = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("avtr_live", avatar.Id);
            Assert.Equal("Live Public Avatar", avatar.Name);
            Assert.Equal("public", avatar.ReleaseStatus);
            Assert.True(avatar.CanUse);
            Assert.Equal(2022, avatar.CreatedAt.Year);
            Assert.Equal("PC · Quest", avatar.PlatformsText);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void KnownImageResolutionRestoresTheAvatarWithoutCheckingAgain()
    {
        var directory = NewDirectory();
        try
        {
            var store = new FriendAvatarStore(Path.Combine(directory, "avatars.json"));
            store.RememberImageResolution(
                "acct_known",
                "https://api.vrchat.cloud/api/1/file/file_known/1/file",
                "avtr_known",
                "public");

            using var api = new VrcApiClient();
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_known", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Picture Only",
                    IsFriend = false,
                    CurrentAvatarImageUrl = "https://api.vrchat.cloud/api/1/file/file_known/1/file"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: store);

            var avatar = Assert.Single(profile.Avatars);
            Assert.Equal("avtr_known", avatar.AvatarId);
            Assert.Equal("public", avatar.ReleaseStatus);
            Assert.True(avatar.IsPublic);
            Assert.Equal("Public", avatar.PrivacyLabel);
            Assert.Equal("\uE785", avatar.PrivacyGlyph);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnresolvedProfileImageFallsBackToTheProfilePictureWithoutClaimingPrivate()
    {
        var directory = NewDirectory();
        var community = new AuthorAvatarHandler();
        TestHooks.UseCommunityHandler(community);
        try
        {
            using var api = new VrcApiClient(null, new AuthorAvatarHandler());
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Avatar Person",
                    IsFriend = false,
                    UserIcon = "https://example.test/profile-custom.png",
                    CurrentAvatarImageUrl = "https://api.vrchat.cloud/api/1/file/file_missing/1/file"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));

            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "Avatar Person",
                IconUrl = "https://example.test/profile-custom.png"
            };

            await WaitUntil(() => profile.Avatars.Count > 0);

            var avatar = Assert.Single(profile.Avatars);
            Assert.True(avatar.IsProfilePictureFallback);
            Assert.False(avatar.IsUnavailable);
            Assert.Equal("Profile picture", avatar.SeenLabel);
            Assert.Equal("https://example.test/profile-custom.png", avatar.ImageUrl);
            Assert.False(profile.HasIndexedAvatars);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CurrentAvatarImageDifferentFromProfileIconResolvesBeforeAuthorLookup()
    {
        var directory = NewDirectory();
        TestHooks.UseCommunityHandler(new FileIdAvatarHandler());
        try
        {
            using var api = new VrcApiClient(null, new CommunityHydrationHandler());
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Avatar Person",
                    IsFriend = false,
                    UserIcon = "https://example.test/profile-custom.png",
                    CurrentAvatarImageUrl = "https://api.vrchat.cloud/api/1/file/file_lookup/1/file"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));

            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "Avatar Person",
                IconUrl = "https://example.test/profile-custom.png"
            };

            await WaitUntil(() => profile.Avatars.Any(avatar =>
                string.Equals(avatar.AvatarId, "avtr_from_file", StringComparison.Ordinal)));

            var avatar = Assert.Single(profile.Avatars);
            Assert.True(avatar.IsCurrent);
            Assert.False(avatar.IsIndexed);
            Assert.False(avatar.IsProfileImageCandidate);
            Assert.Equal("Indexed Avatar", avatar.DisplayName);
            Assert.True(avatar.CanOpen);
            Assert.False(profile.HasIndexedAvatars);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
            // The profile's avatar store saves on a background thread, so the file
            // can still be open for a moment after the assertion that saw it.
            await DeleteDirectoryAsync(directory);
        }
    }

    [Fact]
    public async Task ProfileIconFileMetadataResolvesTheExactIndexedAvatar()
    {
        var directory = NewDirectory();
        var iconUrl = "https://api.vrchat.cloud/api/1/file/file_avatar_icon/1/file";
        TestHooks.UseCommunityHandler(new ProfileIconAvatarIndexHandler());
        try
        {
            using var api = new VrcApiClient(null, new ProfileIconFileHandler());
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "LeviRakun",
                    IsFriend = false,
                    UserIcon = iconUrl
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));

            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "LeviRakun",
                IconUrl = iconUrl
            };

            await WaitUntil(() => profile.Avatars.Count > 0);

            var avatar = Assert.Single(profile.Avatars);
            Assert.Equal("Remi Tan", avatar.DisplayName);
            Assert.Equal("avtr_remi_tan", avatar.AvatarId);
            Assert.Equal(iconUrl, avatar.ImageUrl);
            Assert.True(avatar.IsCurrent);
            Assert.True(avatar.IsPublic);
            Assert.True(avatar.CanOpen);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoggedAvatarNameResolvesAProfileWithAHiddenAvatar()
    {
        var directory = NewDirectory();
        TestHooks.UseCommunityHandler(new LoggedAvatarHandler());
        TestHooks.UseLoggedAvatar(UserId, "REFOXE", "AC-Moe-Green", "AC大喵喵");
        try
        {
            using var api = new VrcApiClient(null, new NoAvatarHandler());
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "REFOXE",
                    IsFriend = true,
                    UserIcon = "https://example.test/custom-icon.png"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));

            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "REFOXE",
                IconUrl = "https://example.test/custom-icon.png"
            };

            await WaitUntil(() => profile.Avatars.Any(avatar =>
                string.Equals(avatar.AvatarId, "avtr_from_log", StringComparison.Ordinal)));

            var avatar = Assert.Single(profile.Avatars);
            Assert.Equal("AC-Moe-Green", avatar.DisplayName);
            Assert.True(avatar.IsCurrent);
            Assert.True(avatar.IsPublic);
            Assert.True(avatar.CanOpen);
            Assert.Equal("https://example.test/ac-moe.png", avatar.ImageUrl);
        }
        finally
        {
            TestHooks.ClearLoggedAvatars();
            TestHooks.UseCommunityHandler(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoggedAvatarSwitchReplacesTheCurrentCard()
    {
        var directory = NewDirectory();
        TestHooks.UseCommunityHandler(new LoggedAvatarHandler());
        TestHooks.UseLoggedAvatar(UserId, "REFOXE", "AC-Moe-Green", "AC大喵喵");
        try
        {
            using var api = new VrcApiClient(null, new NoAvatarHandler());
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "REFOXE",
                    IsFriend = true,
                    UserIcon = "https://example.test/custom-icon.png"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));

            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "REFOXE",
                IconUrl = "https://example.test/custom-icon.png"
            };

            await WaitUntil(() => profile.Avatars.Any(avatar =>
                string.Equals(avatar.AvatarId, "avtr_from_log", StringComparison.Ordinal)));

            profile.ApplyLoggedAvatar(new LoggedAvatar("Second Avatar", "Second Author"));
            await WaitUntil(() => profile.Avatars.Any(avatar =>
                string.Equals(avatar.AvatarId, "avtr_second", StringComparison.Ordinal)));

            Assert.Equal(2, profile.Avatars.Count);
            Assert.Equal("avtr_second", profile.Avatars[0].AvatarId);
            Assert.True(profile.Avatars[0].IsCurrent);
            Assert.False(profile.Avatars[1].IsCurrent);
        }
        finally
        {
            TestHooks.ClearLoggedAvatars();
            TestHooks.UseCommunityHandler(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoggedAvatarAuthorPicksTheRightCommonName()
    {
        var directory = NewDirectory();
        TestHooks.UseCommunityHandler(new AmbiguousJaneHandler());
        TestHooks.UseLoggedAvatar(UserId, "Astryn", "jane", "creator");
        try
        {
            using var api = new VrcApiClient(null, new NoAvatarHandler());
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Astryn",
                    IsFriend = true,
                    UserIcon = "https://example.test/custom-icon.png"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));

            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "Astryn",
                IconUrl = "https://example.test/custom-icon.png"
            };

            await WaitUntil(() => profile.Avatars.Any(avatar =>
                string.Equals(avatar.AvatarId, "avtr_jane_right", StringComparison.Ordinal)));

            var avatar = Assert.Single(profile.Avatars);
            Assert.Equal("jane", avatar.DisplayName);
            Assert.Equal("creator", avatar.Details?.AuthorName);
            Assert.True(avatar.IsCurrent);
            Assert.True(avatar.CanOpen);
        }
        finally
        {
            TestHooks.ClearLoggedAvatars();
            TestHooks.UseCommunityHandler(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OpenProfilePicksUpAvatarSearchResultsAddedLater()
    {
        var directory = NewDirectory();
        var imageUrl = "https://api.vrchat.cloud/api/1/file/file_late_search/1/file";
        AvatarLookupCache.Clear();
        TestHooks.UseCommunityHandler(new NoAvatarHandler());
        try
        {
            using var api = new VrcApiClient(null, new NoAvatarHandler());
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "LeviRakun",
                    IsFriend = false,
                    UserIcon = "https://example.test/profile-custom.png",
                    CurrentAvatarImageUrl = imageUrl
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));

            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "LeviRakun",
                IconUrl = "https://example.test/profile-custom.png",
                CurrentAvatarImageUrl = imageUrl
            };
            await TestHooks.LoadAvatarDetails(profile);
            Assert.True(Assert.Single(profile.Avatars).IsProfilePictureFallback);

            AvatarLookupCache.Remember(new Avatar
            {
                Id = "avtr_late_search",
                Name = "Remi By Kiutie",
                ImageUrl = imageUrl,
                ReleaseStatus = "public"
            });
            profile.RefreshAvatarLookupFromSearch();

            await WaitUntil(() => profile.Avatars.Any(avatar =>
                string.Equals(avatar.AvatarId, "avtr_late_search", StringComparison.Ordinal)));
            Assert.True(Assert.Single(profile.Avatars).IsCurrent);
        }
        finally
        {
            AvatarLookupCache.Clear();
            TestHooks.UseCommunityHandler(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NonFriendProfileRetriesTheIndexLookupAfterTheProfileArrives()
    {
        var directory = NewDirectory();
        AvatarLookupCache.Clear();
        TestHooks.UseCommunityHandler(new AllIndexesAuthorHandler());
        try
        {
            using var api = new VrcApiClient(null, new NoAvatarHandler());
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "LeviRakun",
                    IsFriend = false,
                    UserIcon = "https://example.test/profile-custom.png"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));

            // The live user request runs before /profile; for a non-friend it
            // has no avatar hints, so this first lookup legitimately finds
            // nothing and must not suppress the later profile lookup.
            await TestHooks.LoadAvatarDetails(profile);
            Assert.True(Assert.Single(profile.Avatars).IsProfilePictureFallback);

            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "LeviRakun",
                CurrentAvatarName = "Remi By Kiutie",
                CurrentAvatarAuthorName = "Kiutie",
                IconUrl = "https://example.test/profile-custom.png"
            };

            await WaitUntil(() => profile.Avatars.Any(avatar =>
                string.Equals(avatar.AvatarId, "avtr_from_avtr_icu", StringComparison.Ordinal)));

            var avatar = Assert.Single(profile.Avatars);
            Assert.Equal("Remi By Kiutie", avatar.DisplayName);
            Assert.True(avatar.IsCurrent);
            Assert.False(avatar.IsIndexed);
        }
        finally
        {
            AvatarLookupCache.Clear();
            TestHooks.UseCommunityHandler(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AuthorIndexDoesNotFillTheCurrentAvatarSection()
    {
        var directory = NewDirectory();
        AvatarLookupCache.Clear();
        TestHooks.UseCommunityHandler(new AllIndexesAuthorHandler());
        try
        {
            using var api = new VrcApiClient(null, new NoAvatarHandler());
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "LeviRakun",
                    IsFriend = false,
                    UserIcon = "https://example.test/profile-custom.png"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));

            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "LeviRakun",
                CurrentAvatarAuthorName = "Kiutie",
                IconUrl = "https://example.test/profile-custom.png"
            };

            await WaitUntil(() => profile.Avatars.Count > 0);

            var avatar = Assert.Single(profile.Avatars);
            Assert.True(avatar.IsProfilePictureFallback);
            Assert.DoesNotContain(profile.Avatars, candidate =>
                string.Equals(candidate.AvatarId, "avtr_from_avtr_icu", StringComparison.Ordinal));
            Assert.False(profile.HasIndexedAvatars);
        }
        finally
        {
            AvatarLookupCache.Clear();
            TestHooks.UseCommunityHandler(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AvatarSearchResultResolvesAProfileAvatarWhenTheIndexMissesItsFileId()
    {
        var directory = NewDirectory();
        var imageUrl = "https://api.vrchat.cloud/api/1/file/file_search_cached/1/file";
        AvatarLookupCache.Clear();
        TestHooks.UseCommunityHandler(new NoAvatarHandler());
        try
        {
            AvatarLookupCache.Remember(new Avatar
            {
                Id = "avtr_search_cached",
                Name = "Remi By Kiutie",
                ImageUrl = imageUrl,
                ThumbnailImageUrl = "https://api.vrchat.cloud/api/1/image/file_search_cached/1/256",
                ReleaseStatus = "public"
            });
            using var api = new VrcApiClient(null, new NoAvatarHandler());
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Avatar Person",
                    IsFriend = false,
                    UserIcon = imageUrl,
                    CurrentAvatarImageUrl = imageUrl,
                    CurrentAvatarThumbnailImageUrl = "https://api.vrchat.cloud/api/1/image/file_search_cached/1/256"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));

            // The profile uses the avatar picture as its profile artwork and
            // exposes no avatar id or name, exactly where the normal candidate
            // is suppressed. The search result must still recover it.
            profile.Profile = new VrcProfile
            {
                Id = UserId,
                DisplayName = "Avatar Person",
                IconUrl = imageUrl,
                CurrentAvatarImageUrl = imageUrl,
                CurrentAvatarThumbnailImageUrl = "https://api.vrchat.cloud/api/1/image/file_search_cached/1/256"
            };

            await WaitUntil(() => profile.Avatars.Any(avatar =>
                string.Equals(avatar.AvatarId, "avtr_search_cached", StringComparison.Ordinal)));

            var avatar = Assert.Single(profile.Avatars);
            Assert.Equal("Remi By Kiutie", avatar.DisplayName);
            Assert.True(avatar.IsCurrent);
            Assert.True(avatar.CanOpen);
        }
        finally
        {
            AvatarLookupCache.Clear();
            TestHooks.UseCommunityHandler(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CommunityFileLookupResolvesAnAvatarFromItsPicture()
    {
        TestHooks.UseCommunityHandler(new FileIdAvatarHandler());
        try
        {
            using var api = new VrcApiClient();
            var avatar = await api.ResolveCommunityAvatarByImageAsync(
                "https://api.vrchat.cloud/api/1/file/file_lookup/1/file",
                "usr_author");

            Assert.NotNull(avatar);
            Assert.Equal("avtr_from_file", avatar!.Id);
            Assert.Equal("Indexed Avatar", avatar.Name);
            Assert.Equal("public", avatar.ReleaseStatus);
            Assert.Equal(2021, avatar.CreatedAt.Year);
            Assert.Equal("PC · Quest", avatar.PlatformsText);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task CommunityProfileAvatarLoadsTheFullVrchatRecord()
    {
        var directory = NewDirectory();
        TestHooks.UseCommunityHandler(new FileIdAvatarHandler());
        try
        {
            using var api = new VrcApiClient(null, new CommunityHydrationHandler());
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Not A Friend",
                    IsFriend = false,
                    CurrentAvatarImageUrl = "https://api.vrchat.cloud/api/1/file/file_lookup/1/file"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: new FriendAvatarStore(Path.Combine(directory, "avatars.json")));

            var opened = new TaskCompletionSource<Avatar>(TaskCreationOptions.RunContinuationsAsynchronously);
            profile.OpenAvatar = avatar => opened.TrySetResult(avatar);
            profile.OpenAvatarCommand.Execute(Assert.Single(profile.Avatars));

            var avatar = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("avtr_from_file", avatar.Id);
            Assert.Equal("public", avatar.ReleaseStatus);
            Assert.Equal(2022, avatar.CreatedAt.Year);
            Assert.Equal("PC · Quest · iOS", avatar.PlatformsText);
            Assert.True(avatar.CanUse);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AvtrIcuNativeFileLookupResolvesTheAvatar()
    {
        TestHooks.UseCommunityHandler(new NativeFileIdHandler());
        try
        {
            using var api = new VrcApiClient();
            var avatar = await api.ResolveCommunityAvatarByImageAsync(
                "https://api.vrchat.cloud/api/1/file/file_native/1/file",
                "usr_author");

            Assert.NotNull(avatar);
            Assert.Equal("avtr_native", avatar!.Id);
            Assert.Equal("Native Index Avatar", avatar.Name);
            Assert.Equal("PC · Quest", avatar.PlatformsText);
            Assert.Equal("avtr.icu", avatar.SourceName);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public void StoreKeepsPreviousAvatarsAndMovesTheCurrentOneToTheFront()
    {
        var directory = NewDirectory();
        try
        {
            var store = new FriendAvatarStore(Path.Combine(directory, "avatars.json"));
            store.ObserveAll("acct_one", new[]
            {
                new VrcUser
                {
                    Id = UserId,
                    CurrentAvatar = "avtr_first",
                    CurrentAvatarThumbnailImageUrl = "https://example.test/first.png"
                }
            });

            store.ObserveAll("acct_one", new[]
            {
                new VrcUser
                {
                    Id = UserId,
                    CurrentAvatar = "avtr_second",
                    CurrentAvatarThumbnailImageUrl = "https://example.test/second.png"
                }
            });

            var history = store.Load("acct_one", UserId);
            Assert.Equal(2, history.Count);
            Assert.Equal("avtr_second", history[0].AvatarId);
            Assert.True(history[0].IsCurrent);
            Assert.Equal("avtr_first", history[1].AvatarId);
            Assert.False(history[1].IsCurrent);

            // Account data is kept separate.
            Assert.Empty(store.Load("acct_two", UserId));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RefusedAvatarRecordStaysPicturedAndLocked()
    {
        var directory = NewDirectory();
        try
        {
            var store = new FriendAvatarStore(Path.Combine(directory, "avatars.json"));
            store.Observe(
                "acct_one",
                UserId,
                "avtr_private",
                name: "Secret Avatar",
                imageUrl: "https://example.test/secret.png",
                thumbnailUrl: "https://example.test/secret.png");
            store.MarkUnavailable("acct_one", UserId, "avtr_private");

            var avatar = Assert.Single(store.Load("acct_one", UserId));
            Assert.True(avatar.IsPrivate);
            Assert.True(avatar.IsUnavailable);
            Assert.Equal("Unavailable", avatar.PrivacyLabel);
            Assert.Equal("\uE946", avatar.PrivacyGlyph);
            Assert.Equal("https://example.test/secret.png", avatar.ThumbnailUrl);
            Assert.False(avatar.ToAvatar().CanUse);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StoredPrivateLabelIsRefreshedFromThePublicAvatarRecord()
    {
        var directory = NewDirectory();
        try
        {
            var store = new FriendAvatarStore(Path.Combine(directory, "avatars.json"));
            store.Observe(
                "acct_me",
                UserId,
                "avtr_live",
                name: "Old Name",
                imageUrl: "https://example.test/picture-only.png",
                thumbnailUrl: "https://example.test/picture-only.png");
            store.MarkUnavailable("acct_me", UserId, "avtr_live");

            using var api = new VrcApiClient(null, new LiveAvatarHandler());
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_me", DisplayName = "Me" });
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Not A Friend",
                    CurrentAvatar = "avtr_live"
                }),
                api,
                ImageLoader.Shared,
                new FriendNotesStore(Path.Combine(directory, "notes.json")),
                avatarStore: store);

            Assert.True(Assert.Single(profile.Avatars).IsPrivate);

            var opened = new TaskCompletionSource<Avatar>(TaskCreationOptions.RunContinuationsAsynchronously);
            profile.OpenAvatar = avatar => opened.TrySetResult(avatar);
            profile.OpenAvatarCommand.Execute(Assert.Single(profile.Avatars));

            var avatar = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("public", avatar.ReleaseStatus);
            Assert.Equal("Live Public Avatar", avatar.Name);
            Assert.Equal("Avatar Author", avatar.AuthorName);
            Assert.Equal(2022, avatar.CreatedAt.Year);
            Assert.Equal("PC · Quest", avatar.PlatformsText);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProfileShowsTheCurrentAvatarAndCanExpandTheFullHistory()
    {
        WithStore((store, notes) =>
        {
            var profile = CreateProfile(
                store,
                notes,
                new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Avatar Person",
                    CurrentAvatar = "avtr_current",
                    CurrentAvatarName = "Current Avatar",
                    CurrentAvatarThumbnailImageUrl = "https://example.test/current.png"
                });
            profile.Avatars.Add(new FriendAvatar
            {
                AvatarId = "avtr_older",
                Name = "Older Avatar",
                LastSeen = DateTimeOffset.UtcNow.AddDays(-2)
            });
            profile.Avatars.Add(new FriendAvatar
            {
                AvatarId = "avtr_oldest",
                Name = "Oldest Avatar",
                LastSeen = DateTimeOffset.UtcNow.AddDays(-5)
            });

            var current = Assert.Single(profile.VisibleAvatars);
            Assert.True(current.IsCurrent);
            Assert.Equal("Current Avatar", current.DisplayName);
            Assert.True(profile.CanToggleAvatars);
            Assert.Equal("1 of 3 avatars", profile.AvatarsSummary);
            Assert.Contains("Show all 3", profile.AvatarsToggleText);

            profile.ToggleAvatarsCommand.Execute(null);

            Assert.True(profile.IsAvatarsExpanded);
            Assert.Equal(3, profile.VisibleAvatars.Count);
            Assert.Equal("3 avatars", profile.AvatarsSummary);
            Assert.Equal("Show current only", profile.AvatarsToggleText);

            profile.ToggleAvatarsCommand.Execute(null);

            Assert.False(profile.IsAvatarsExpanded);
            Assert.Single(profile.VisibleAvatars);
            Assert.True(profile.VisibleAvatars[0].IsCurrent);
        });
    }

    private static FriendDetailViewModel CreateProfile(
        FriendAvatarStore store,
        FriendNotesStore notes,
        VrcUser user)
        => new(
            new FriendItem(user),
            new VrcApiClient(),
            ImageLoader.Shared,
            notes,
            avatarStore: store);

    private static void WithStore(Action<FriendAvatarStore, FriendNotesStore> action)
    {
        var directory = NewDirectory();
        try
        {
            action(
                new FriendAvatarStore(Path.Combine(directory, "avatars.json")),
                new FriendNotesStore(Path.Combine(directory, "notes.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Deletes a test folder, waiting out a store that is still finishing a
    /// background save. The folder is disposable state, not an assertion.
    /// </summary>
    private static async Task DeleteDirectoryAsync(string directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(50);
            }
        }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "vrccompanion-avatars-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class AuthorAvatarHandler : HttpMessageHandler
    {
        public int AuthorRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri?.Query.Contains($"authorId={UserId}", StringComparison.Ordinal) == true)
            {
                AuthorRequests++;
                return Json("""
                    [
                      {
                        "id": "avtr_author_public",
                        "name": "Indexed Fox",
                        "authorName": "Avatar Person",
                        "releaseStatus": "public",
                        "imageUrl": "https://example.test/indexed-fox.png",
                        "thumbnailImageUrl": "https://example.test/indexed-fox.png",
                        "created_at": "2024-03-01T12:00:00Z",
                        "updated_at": "2024-03-01T12:00:00Z",
                        "unityPackages": [
                          { "platform": "standalonewindows" },
                          { "platform": "android" }
                        ]
                      }
                    ]
                    """);
            }

            return Json("{}");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class AllIndexesAuthorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (string.Equals(host, "avtr.icu", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith("/search", StringComparison.Ordinal))
            {
                return Json("""
                    [
                      {
                        "id": "avtr_from_avtr_icu",
                        "name": "Remi By Kiutie",
                        "authorId": "usr_kiutie",
                        "authorName": "Kiutie",
                        "releaseStatus": "public",
                        "imageUrl": "https://example.test/remi.png",
                        "thumbnailImageUrl": "https://example.test/remi.png"
                      }
                    ]
                    """);
            }

            if (string.Equals(host, "db.vrcnext.com", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""{ "results": [] }""");
            }

            return Json("[]");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class ProfileIconFileHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/file/file_avatar_icon", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "id": "file_avatar_icon",
                      "name": "file_avatar_icon_blob",
                      "ownerId": "usr_8b8a98ed-2bbb-4517-875e-636cc608727a",
                      "tags": [ "avatarimage" ],
                      "versions": [
                        {
                          "file": {
                            "url": "https://api.vrchat.cloud/api/1/file/file_avatar_icon/1/file"
                          }
                        }
                      ]
                    }
                    """);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class LoggedAvatarHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            var query = uri is null ? string.Empty : Uri.UnescapeDataString(uri.Query);
            if (query.Contains("AC-Moe-Green", StringComparison.OrdinalIgnoreCase) ||
                query.Contains("AC大喵喵", StringComparison.Ordinal))
            {
                return Json("""
                    [
                      {
                        "id": "avtr_from_log",
                        "name": "AC-Moe-Green",
                        "authorId": "usr_8df02380-4cf2-41a9-a97e-a535645dbd51",
                        "authorName": "AC",
                        "imageUrl": "https://example.test/ac-moe.png",
                        "thumbnailImageUrl": "https://example.test/ac-moe-thumb.png",
                        "releaseStatus": "public"
                      }
                    ]
                    """);
            }

            if (query.Contains("Second Avatar", StringComparison.OrdinalIgnoreCase) ||
                query.Contains("Second Author", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""
                    [
                      {
                        "id": "avtr_second",
                        "name": "Second Avatar",
                        "authorId": "usr_second",
                        "authorName": "Second Author",
                        "imageUrl": "https://example.test/second.png",
                        "thumbnailImageUrl": "https://example.test/second-thumb.png",
                        "releaseStatus": "public"
                      }
                    ]
                    """);
            }

            return Json("[]");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class ProfileIconAvatarIndexHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            if (query.Contains("fileId=file_avatar_icon", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "id": "avtr_remi_tan",
                      "name": "Remi Tan",
                      "authorId": "usr_8b8a98ed-2bbb-4517-875e-636cc608727a",
                      "authorName": "goonikorn",
                      "imageUrl": "https://api.vrchat.cloud/api/1/file/file_avatar_icon/1/file",
                      "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_avatar_icon/1/256",
                      "releaseStatus": "public"
                    }
                    """);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class AmbiguousJaneHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            var query = uri is null ? string.Empty : Uri.UnescapeDataString(uri.Query);
            if (query.Contains("creator", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""
                    [
                      {
                        "id": "avtr_jane_right",
                        "name": "jane",
                        "authorId": "usr_creator",
                        "authorName": "creator",
                        "imageUrl": "https://example.test/jane-right.png",
                        "thumbnailImageUrl": "https://example.test/jane-right-thumb.png",
                        "releaseStatus": "public"
                      }
                    ]
                    """);
            }

            if (query.Contains("jane", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""
                    [
                      {
                        "id": "avtr_jane_wrong",
                        "name": "jane",
                        "authorId": "usr_other",
                        "authorName": "other",
                        "imageUrl": "https://example.test/jane-wrong.png",
                        "thumbnailImageUrl": "https://example.test/jane-wrong-thumb.png",
                        "releaseStatus": "public"
                      },
                      {
                        "id": "avtr_jane_right",
                        "name": "jane",
                        "authorId": "usr_creator",
                        "authorName": "creator",
                        "imageUrl": "https://example.test/jane-right.png",
                        "thumbnailImageUrl": "https://example.test/jane-right-thumb.png",
                        "releaseStatus": "public"
                      }
                    ]
                    """);
            }

            return Json("[]");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class NoAvatarHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class LiveAvatarHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains($"/users/{UserId}", StringComparison.Ordinal) &&
                !path.Contains("/groups", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "id": "usr_avatar_profile",
                      "displayName": "Not A Friend",
                      "currentAvatar": "avtr_live",
                      "currentAvatarImageUrl": "https://example.test/picture-only.png",
                      "currentAvatarThumbnailImageUrl": "https://example.test/picture-only.png"
                    }
                    """);
            }

            if (path.Contains("/avatars/avtr_live", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "id": "avtr_live",
                      "name": "Live Public Avatar",
                      "authorName": "Avatar Author",
                      "releaseStatus": "public",
                      "imageUrl": "https://example.test/picture-only.png",
                      "thumbnailImageUrl": "https://example.test/picture-only.png",
                      "created_at": "2022-04-25T12:00:00Z",
                      "updated_at": "2022-04-25T12:00:00Z",
                      "unityPackages": [
                        { "platform": "standalonewindows" },
                        { "platform": "android" }
                      ]
                    }
                    """);
            }

            return Json("{}");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class NamedAvatarHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/avatars", StringComparison.Ordinal))
            {
                return Json("""
                    [
                      {
                        "id": "avtr_named",
                        "name": "Named Public Avatar",
                        "authorName": "Named Author",
                        "releaseStatus": "public",
                        "platforms": ["standalonewindows", "android"],
                        "imageUrl": "https://example.test/named.png",
                        "thumbnailImageUrl": "https://example.test/named.png"
                      }
                    ]
                    """);
            }

            if (path.Contains("/avatars/avtr_named", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "id": "avtr_named",
                      "name": "Named Public Avatar",
                      "authorName": "Named Author",
                      "releaseStatus": "public",
                      "imageUrl": "https://example.test/named.png",
                      "thumbnailImageUrl": "https://example.test/named.png",
                      "created_at": "2023-01-02T12:00:00Z",
                      "updated_at": "2023-01-02T12:00:00Z",
                      "unityPackages": [
                        { "platform": "standalonewindows" },
                        { "platform": "android" }
                      ]
                    }
                    """);
            }

            return Json("{}");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class FileIdAvatarHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            if (query.Contains("fileId=file_lookup", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "id": "avtr_from_file",
                      "name": "Indexed Avatar",
                      "authorId": "usr_author",
                      "authorName": "Index Author",
                      "imageUrl": "https://api.vrchat.cloud/api/1/file/file_lookup/1/file",
                      "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_lookup/1/256",
                      "releaseStatus": "public",
                      "created_at": "2021-06-14T12:00:00Z",
                      "updated_at": "2021-06-14T12:00:00Z",
                      "unityPackages": [
                        { "platform": "standalonewindows" },
                        { "platform": "android" }
                      ]
                    }
                    """);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class CommunityHydrationHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains($"/users/{UserId}", StringComparison.Ordinal) &&
                !path.Contains("/groups", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "id": "usr_avatar_profile",
                      "displayName": "Not A Friend",
                      "currentAvatarImageUrl": "https://api.vrchat.cloud/api/1/file/file_lookup/1/file",
                      "currentAvatarThumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_lookup/1/256"
                    }
                    """);
            }

            if (path.Contains("/avatars/avtr_from_file", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "id": "avtr_from_file",
                      "name": "Indexed Avatar",
                      "authorName": "Index Author",
                      "releaseStatus": "public",
                      "imageUrl": "https://api.vrchat.cloud/api/1/file/file_lookup/1/file",
                      "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_lookup/1/256",
                      "created_at": "2022-04-25T12:00:00Z",
                      "updated_at": "2022-04-25T12:00:00Z",
                      "unityPackages": [
                        { "platform": "standalonewindows" },
                        { "platform": "android" },
                        { "platform": "ios" }
                      ]
                    }
                    """);
            }

            return Json("{}");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class NativeFileIdHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method.Method == "QUERY" &&
                request.RequestUri?.AbsolutePath.EndsWith("/resolve", StringComparison.Ordinal) == true)
            {
                return Json("""
                    {
                      "file_native": {
                        "id": "avtr_native",
                        "name": "Native Index Avatar",
                        "authorId": "usr_author",
                        "authorName": "Native Author",
                        "imageUrl": "https://avtr.icu/proxy/file_native/1/file",
                        "thumbnailImageUrl": "https://avtr.icu/proxy/file_native/1/256",
                        "releaseStatus": "public",
                        "platforms": ["standalonewindows", "android"]
                      }
                    }
                    """);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class FavoritesHandler : HttpMessageHandler
    {
        public int PostCount { get; private set; }
        public int DeleteCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Post && path.EndsWith("/favorites", StringComparison.Ordinal))
            {
                PostCount++;
                return Json("""
                    {
                      "id": "fvrt_profile",
                      "favoriteId": "avtr_profile_favorite",
                      "tags": ["avatars1"],
                      "type": "avatar"
                    }
                    """);
            }

            if (request.Method == HttpMethod.Delete && path.Contains("/favorites/", StringComparison.Ordinal))
            {
                DeleteCount++;
                return Json("{}");
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/favorites", StringComparison.Ordinal))
            {
                return Json("[]");
            }

            return Json("{}");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
