using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
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
public class MainViewModelSectionTests
{
    [Fact]
    public void ActivityEntryOpensTheSharedProfileWithItsLocation()
    {
        RunOnSta(() =>
        {
            var handler = new ActivityProfileHandler();
            using var api = new VrcApiClient(null, handler);
            var shell = new MainViewModel(api);
            TestHooks.SetLoggedIn(shell);
            const string location =
                "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345~group(grp_test)";
            var entry = new ActivityEntry
            {
                UserId = "usr_activity",
                DisplayName = "Activity Person",
                UserName = "activity",
                Location = location
            };

            shell.OpenActivityUserCommand.Execute(entry);
            Settle.Until(() => handler.ProfileGets == 1 &&
                            shell.SelectedFriendDetail?.Profile is not null);

            var profile = Assert.IsType<FriendDetailViewModel>(shell.SelectedFriendDetail);
            Assert.Equal("usr_activity", profile.Id);
            Assert.Equal("Live Activity", profile.DisplayName);
            Assert.Equal(location, profile.JoinLocation);
            Assert.True(profile.CanJoin);
        });
    }

    [Fact]
    public void AvatarChangedActivityOpensTheSharedAvatarPopup()
    {
        RunOnSta(() =>
        {
            var handler = new ActivityAvatarHandler(isPrivate: false);
            using var api = new VrcApiClient(null, handler);
            var shell = new MainViewModel(api);
            TestHooks.SetLoggedIn(shell);
            var avatars = new AvatarViewModel(api);
            TestHooks.AttachAvatarViewModel(shell, avatars);
            var entry = new ActivityEntry
            {
                Type = ActivityType.AvatarChanged,
                UserId = "usr_activity",
                DisplayName = "Activity Person",
                AvatarId = "avtr_public_change",
                Details = "changed avatar"
            };

            shell.OpenActivityAvatarCommand.Execute(entry);
            Settle.Until(() => avatars.SelectedAvatar?.Id == entry.AvatarId &&
                            avatars.SelectedAvatar.CanUse);

            Assert.False(avatars.SelectedAvatar!.IsPrivate);
            Assert.True(avatars.SelectedAvatar.CanUse);
            Assert.Equal("Public Avatar", avatars.SelectedAvatar.Name);
        });
    }

    [Fact]
    public void OldAvatarChangedActivityCannotOpenTheAvatarPopup()
    {
        RunOnSta(() =>
        {
            using var api = new VrcApiClient();
            var shell = new MainViewModel(api);
            TestHooks.SetLoggedIn(shell);
            var avatars = new AvatarViewModel(api);
            TestHooks.AttachAvatarViewModel(shell, avatars);
            var entry = new ActivityEntry
            {
                Type = ActivityType.AvatarChanged,
                UserId = "usr_activity",
                DisplayName = "Activity Person",
                AvatarId = string.Empty,
                Details = "changed avatar"
            };

            Assert.False(shell.OpenActivityAvatarCommand.CanExecute(entry));
            shell.OpenActivityAvatarCommand.Execute(entry);

            Assert.Null(avatars.SelectedAvatar);
            Assert.Null(shell.SelectedFriendDetail);
        });
    }

    [Fact]
    public void LogAvatarChangeResolvesThePublicAvatarFromItsName()
    {
        RunOnSta(() =>
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "VrcCompanionTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                TestHooks.UseCommunityHandler(new ActivityAvatarCommunityHandler());
                using var api = new VrcApiClient(null, new ActivityAvatarHandler(isPrivate: false));
                var feed = new ActivityFeedViewModel(
                    api,
                    new ActivityStore(Path.Combine(directory, "activity.json")),
                    new FriendAvatarStore(Path.Combine(directory, "avatars.json")));
                feed.SetAccount("acct_log_avatar");
                var friend = new VrcUser
                {
                    Id = "usr_activity",
                    DisplayName = "Activity Person",
                    Location = "wrld_test:1"
                };

                feed.ApplyLoggedAvatarChange(friend, "Mellzie", "Avatar Maker");

                var entry = Assert.Single(feed.Entries);
                Assert.True(entry.CanOpenAvatar);
                Assert.Equal("Mellzie", entry.EffectiveAvatarName);
                Settle.Until(() => entry.AvatarId == "avtr_public_change" &&
                                !string.IsNullOrWhiteSpace(entry.AvatarUrl));
                Assert.True(entry.HasAvatarImage);
                Assert.Contains("file_log_avatar", entry.AvatarUrl);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void AvatarChangedNameOnlyOpensAndResolvesThePublicAvatar()
    {
        RunOnSta(() =>
        {
            TestHooks.UseCommunityHandler(new ActivityAvatarCommunityHandler());
            try
            {
                var handler = new ActivityAvatarHandler(isPrivate: false);
                using var api = new VrcApiClient(null, handler);
                var shell = new MainViewModel(api);
                TestHooks.SetLoggedIn(shell);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);
                // The log wrote the author, so this row can be identified by name and
                // author - the documented case where the public record still resolves.
                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_activity",
                    DisplayName = "Activity Person",
                    AvatarName = "Mellzie",
                    AvatarAuthor = "Avatar Maker",
                    Details = "changed avatar · Mellzie"
                };

                Assert.True(shell.OpenActivityAvatarCommand.CanExecute(entry));
                shell.OpenActivityAvatarCommand.Execute(entry);

                // The name-only row opens immediately as a placeholder, then the
                // public record fills in the id and picture.
                Assert.NotNull(avatars.SelectedAvatar);
                Settle.Until(() => avatars.SelectedAvatar?.Id == "avtr_public_change");
                Assert.True(avatars.SelectedAvatar!.CanUse);
                Assert.Equal("Public Avatar", avatars.SelectedAvatar.Name);
                Assert.Equal("avtr_public_change", entry.AvatarId);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
            }
        });
    }

    [Fact]
    public void ANameOnlyRowWithoutAnAuthorKeepsItsPictureAndLocksTheRecord()
    {
        RunOnSta(() =>
        {
            var probe = new NamesakeProbeHandler();
            TestHooks.UseCommunityHandler(probe);
            try
            {
                using var api = new VrcApiClient(null, probe);
                var shell = new MainViewModel(api);
                TestHooks.SetLoggedIn(shell);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);
                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_activity",
                    DisplayName = "chloe",
                    AvatarName = "DAVINA",
                    AvatarUrl = "https://api.vrchat.cloud/api/1/image/file_row_picture/1/256",
                    Details = "changed avatar · DAVINA"
                };

                shell.OpenActivityAvatarCommand.Execute(entry);

                // The only public DAVINA the search could return is by another author, so
                // no public record is opened at all: the row's picture stays and the record
                // is read-only.
                Settle.Until(() =>
                    avatars.SelectedAvatar?.Description.Contains("without its author") == true);
                Assert.Equal("DAVINA", avatars.SelectedAvatar!.Name);
                Assert.Contains("file_row_picture", avatars.SelectedAvatar.ImageUrl);
                Assert.True(avatars.SelectedAvatar.IsUnavailable);
                Assert.False(avatars.SelectedAvatar.CanUse);
                Assert.Equal(string.Empty, avatars.SelectedAvatar.Id);
                Assert.Contains("without its author", avatars.SelectedAvatar.Description);
                Assert.Equal(0, probe.Searches);
                Assert.Equal(0, probe.AvatarGets);
                Assert.Equal(string.Empty, entry.AvatarId);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
            }
        });
    }

    [Fact]
    public void TheCardNamesWhereItsPictureCameFrom()
    {
        RunOnSta(() =>
        {
            var probe = new NamesakeProbeHandler();
            TestHooks.UseCommunityHandler(probe);
            try
            {
                using var api = new VrcApiClient(null, probe);
                var shell = new MainViewModel(api);
                TestHooks.SetLoggedIn(shell);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);
                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_source",
                    DisplayName = "Source",
                    AvatarName = "Misu",
                    AvatarUrl = "https://api.vrchat.cloud/api/1/image/file_source/1/256",
                    AvatarPictureSource = "Saved avatar history - the same name and creator",
                    Details = "changed avatar · Misu"
                };

                shell.OpenActivityAvatarCommand.Execute(entry);

                Settle.Until(() => avatars.SelectedAvatar is not null);
                Assert.Equal(
                    "Saved avatar history - the same name and creator",
                    avatars.SelectedAvatar!.PictureSource);
                Assert.True(avatars.SelectedAvatar.HasPictureSource);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
            }
        });
    }

    [Fact]
    public void ACardNeverBorrowsAnUnrelatedAvatarFromThePlayersHistory()
    {
        RunOnSta(() =>
        {
            var probe = new NamesakeProbeHandler();
            TestHooks.UseCommunityHandler(probe);
            try
            {
                using var api = new VrcApiClient(null, probe);
                var shell = new MainViewModel(api);
                TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_card", DisplayName = "Me" });
                TestHooks.SetLoggedIn(shell);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);

                // This player wore a room-shaped avatar shortly before the switch, so it is
                // the nearest row in their saved history. The log named FND and no picture
                // of FND is known, so the card must not draw that other avatar.
                FriendAvatarStore.Shared.Observe(
                    "acct_card",
                    "usr_card",
                    "avtr_room",
                    "Room",
                    "https://api.vrchat.cloud/api/1/file/file_room/1/file",
                    "https://api.vrchat.cloud/api/1/image/file_room/1/256",
                    "Room Maker");

                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_card",
                    DisplayName = "hellkitty",
                    AvatarName = "FND",
                    Details = "changed avatar · FND",
                    Timestamp = DateTimeOffset.UtcNow
                };

                shell.OpenActivityAvatarCommand.Execute(entry);

                Settle.Until(() =>
                    avatars.SelectedAvatar?.Description.Contains("without its author") == true);
                Assert.Equal("FND", avatars.SelectedAvatar!.Name);
                Assert.Equal(string.Empty, avatars.SelectedAvatar.ImageUrl);
                Assert.Equal(string.Empty, avatars.SelectedAvatar.ThumbnailImageUrl);
                Assert.True(avatars.SelectedAvatar.IsUnavailable);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
            }
        });
    }

    [Fact]
    public void ACardDrawsTheHistoryAvatarUnderTheNameTheLogWrote()
    {
        RunOnSta(() =>
        {
            var probe = new NamesakeProbeHandler();
            TestHooks.UseCommunityHandler(probe);
            try
            {
                using var api = new VrcApiClient(null, probe);
                var shell = new MainViewModel(api);
                TestHooks.SetCurrentUser(api, new VrcUser { Id = "acct_card_named", DisplayName = "Me" });
                TestHooks.SetLoggedIn(shell);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);

                FriendAvatarStore.Shared.Observe(
                    "acct_card_named",
                    "usr_card_named",
                    "avtr_fnd",
                    "FND",
                    "https://api.vrchat.cloud/api/1/file/file_fnd/1/file",
                    "https://api.vrchat.cloud/api/1/image/file_fnd/1/256",
                    "FND Maker");

                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_card_named",
                    DisplayName = "hellkitty",
                    AvatarName = "FND",
                    AvatarAuthor = "FND Maker",
                    Details = "changed avatar · FND",
                    Timestamp = DateTimeOffset.UtcNow
                };

                shell.OpenActivityAvatarCommand.Execute(entry);

                // No picture of its own, so the card draws the avatar the player was
                // recorded wearing under that exact name and creator.
                Settle.Until(() =>
                    avatars.SelectedAvatar?.ImageUrl.Contains("file_fnd") == true);
                Assert.Equal("FND", avatars.SelectedAvatar!.Name);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
            }
        });
    }

    [Fact]
    public void ANameOnlyRowOpensThePublicAvatarItsPictureBelongsTo()
    {
        RunOnSta(() =>
        {
            var handler = new PicturedNameOnlyHandler();
            TestHooks.UseCommunityHandler(handler);
            try
            {
                using var api = new VrcApiClient(null, handler);
                var shell = new MainViewModel(api);
                TestHooks.SetLoggedIn(shell);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);

                // The log named this avatar without its author and the profile named no id,
                // but the row draws the avatar's own picture and a public index has it. The
                // picture is the identity, so the row opens that record - still with no name
                // search that a namesake could answer.
                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_activity",
                    DisplayName = "chloe",
                    AvatarName = "Hays 2.0 (mixed)",
                    AvatarUrl = "https://api.vrchat.cloud/api/1/image/file_hays_row/1/256",
                    Details = "changed avatar · Hays 2.0 (mixed)"
                };

                shell.OpenActivityAvatarCommand.Execute(entry);

                Settle.Until(() => avatars.SelectedAvatar?.Id == "avtr_hays_public");
                Assert.True(avatars.SelectedAvatar!.CanUse);
                Assert.False(avatars.SelectedAvatar.IsUnavailable);
                Assert.Equal("public", avatars.SelectedAvatar.ReleaseStatus);
                Assert.Equal("Hays 2.0 (mixed)", avatars.SelectedAvatar.Name);
                Assert.Equal("avtr_hays_public", entry.AvatarId);
                Assert.Equal(0, handler.Searches);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
            }
        });
    }

    [Fact]
    public void AvatarRowOpensThePlayersOwnAvatarInsteadOfASameNamedOne()
    {
        RunOnSta(() =>
        {
            TestHooks.UseCommunityHandler(new ActivityAvatarCommunityHandler());
            try
            {
                var handler = new PlayerOwnAvatarHandler();
                using var api = new VrcApiClient(null, handler);
                var shell = new MainViewModel(api);
                TestHooks.SetLoggedIn(shell);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);

                // The row picture came from her profile, so the row carries that avatar's
                // id and marks it authoritative. A name search would answer with
                // avtr_other_davina, which is exactly what must not open here.
                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_activity",
                    DisplayName = "chloe",
                    AvatarId = "avtr_her_davina",
                    AvatarIdIsAuthoritative = true,
                    AvatarName = "DAVINA",
                    AvatarUrl = "https://api.vrchat.cloud/api/1/image/file_her_davina/1/256",
                    Details = "changed avatar · DAVINA"
                };

                Assert.True(shell.OpenActivityAvatarCommand.CanExecute(entry));
                shell.OpenActivityAvatarCommand.Execute(entry);

                Settle.Until(() => avatars.SelectedAvatar?.Id == "avtr_her_davina");
                Assert.Equal("Her DAVINA", avatars.SelectedAvatar!.Name);
                Assert.Equal(1, handler.OwnAvatarGets);
                Assert.Equal(0, handler.SearchRequests);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
            }
        });
    }

    [Fact]
    public void ARowOpensThePublicAvatarByItsIdWhenVrchatRefusesTheRecord()
    {
        RunOnSta(() =>
        {
            var handler = new RefusedAvatarIdHandler();
            TestHooks.UseCommunityHandler(handler);
            try
            {
                using var api = new VrcApiClient(null, handler);
                var shell = new MainViewModel(api);
                TestHooks.SetLoggedIn(shell);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);

                // The profile named the avatar and its picture, but VRChat answers 404
                // for that record. A public index still lists the same id, which is the
                // same avatar - unlike the namesake with the same name and picture.
                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_activity",
                    DisplayName = "chloe",
                    AvatarId = "avtr_refused_frost",
                    AvatarIdIsAuthoritative = true,
                    AvatarName = "Frost",
                    AvatarUrl = "https://api.vrchat.cloud/api/1/image/file_row_frost/1/256",
                    Details = "changed avatar · Frost"
                };

                shell.OpenActivityAvatarCommand.Execute(entry);

                Settle.Until(() => avatars.SelectedAvatar?.Id == "avtr_refused_frost");
                Assert.Equal("Frost", avatars.SelectedAvatar!.Name);
                Assert.Equal("public", avatars.SelectedAvatar.ReleaseStatus);
                Assert.True(avatars.SelectedAvatar.CanUse);
                Assert.False(avatars.SelectedAvatar.IsUnavailable);
                // The record with this exact id, not the namesake that shares its name.
                Assert.Equal("Demon_Edi", avatars.SelectedAvatar.AuthorName);
                Assert.Equal("avtr_refused_frost", entry.AvatarId);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
            }
        });
    }

    [Fact]
    public void APrivateAvatarThePlayerWearsIsNotReplacedByAPublicAvatarWithTheSameName()
    {
        RunOnSta(() =>
        {
            var probe = new PrivateAvatarProbeHandler();
            TestHooks.UseCommunityHandler(probe);
            try
            {
                using var api = new VrcApiClient(null, probe);
                var shell = new MainViewModel(api);
                TestHooks.SetLoggedIn(shell);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);

                // Her own record says she wears this ":3" and draws its cat picture.
                // VRChat will not expose that record. A public ":3" by gavial shares the
                // name but is a different avatar, so it must not open in its place.
                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_activity",
                    DisplayName = "SyKari",
                    AvatarId = "avtr_sykari_private",
                    AvatarIdIsAuthoritative = true,
                    AvatarIdIsCurrentAvatar = true,
                    AvatarName = ":3",
                    AvatarUrl = "https://api.vrchat.cloud/api/1/image/file_sykari_cat/1/256",
                    Details = "changed avatar · :3"
                };

                Assert.True(shell.OpenActivityAvatarCommand.CanExecute(entry));
                shell.OpenActivityAvatarCommand.Execute(entry);

                Settle.Until(() => avatars.SelectedAvatar?.IsPrivate == true);
                Assert.Equal("avtr_sykari_private", avatars.SelectedAvatar!.Id);
                Assert.Equal(":3", avatars.SelectedAvatar.Name);
                Assert.Contains("file_sykari_cat", avatars.SelectedAvatar.ImageUrl);
                Assert.False(avatars.SelectedAvatar.CanUse);
                // Never the public namesake the index offered for this name.
                Assert.NotEqual("avtr_gavial_public", avatars.SelectedAvatar.Id);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
            }
        });
    }

    [Fact]
    public void ACurrentAvatarIdWithoutAPictureStillOpensAsPrivate()
    {
        RunOnSta(() =>
        {
            var probe = new PrivateAvatarProbeHandler();
            TestHooks.UseCommunityHandler(probe);
            try
            {
                using var api = new VrcApiClient(null, probe);
                var shell = new MainViewModel(api);
                TestHooks.SetLoggedIn(shell);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);

                // VRChat's user record can name the avatar without exposing its picture.
                // That id is still the avatar the player wears, so a refused record is
                // private rather than a reason to search the name and open gavial's ":3".
                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_activity",
                    DisplayName = "SyKari",
                    AvatarId = "avtr_sykari_private",
                    AvatarIdIsAuthoritative = true,
                    AvatarIdIsCurrentAvatar = true,
                    AvatarName = ":3",
                    AvatarAuthor = "SyKari",
                    Details = "changed avatar · :3"
                };

                shell.OpenActivityAvatarCommand.Execute(entry);

                Settle.Until(() => avatars.SelectedAvatar?.IsPrivate == true);
                Assert.Equal("avtr_sykari_private", avatars.SelectedAvatar!.Id);
                Assert.Equal(":3", avatars.SelectedAvatar.Name);
                Assert.False(avatars.SelectedAvatar.CanUse);
                Assert.NotEqual("avtr_gavial_public", avatars.SelectedAvatar.Id);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
            }
        });
    }

    [Fact]
    public void ASavedIdThatIsRefusedIsUnavailableRatherThanPrivate()
    {
        RunOnSta(() =>
        {
            var probe = new PrivateAvatarProbeHandler();
            TestHooks.UseCommunityHandler(probe);
            try
            {
                using var api = new VrcApiClient(null, probe);
                var shell = new MainViewModel(api);
                TestHooks.SetLoggedIn(shell);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);

                // The id came from an older row, not the player's current record. VRChat
                // refusing it proves neither that it is private nor that it is the avatar
                // the row is about, so the card must not claim either.
                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_activity",
                    DisplayName = "SyKari",
                    AvatarId = "avtr_sykari_old",
                    AvatarIdIsAuthoritative = true,
                    AvatarName = ":3",
                    AvatarAuthor = "SyKari",
                    AvatarUrl = "https://api.vrchat.cloud/api/1/image/file_sykari_cat/1/256",
                    Details = "changed avatar · :3"
                };

                shell.OpenActivityAvatarCommand.Execute(entry);

                Settle.Until(() => avatars.SelectedAvatar?.IsUnavailable == true);
                Assert.Equal("avtr_sykari_old", avatars.SelectedAvatar!.Id);
                Assert.Equal(":3", avatars.SelectedAvatar.Name);
                Assert.Contains("file_sykari_cat", avatars.SelectedAvatar.ImageUrl);
                Assert.False(avatars.SelectedAvatar.CanUse);
                Assert.Contains("could not be confirmed", avatars.SelectedAvatar.Description);
                Assert.NotEqual("avtr_gavial_public", avatars.SelectedAvatar.Id);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
            }
        });
    }

    [Fact]
    public void AnIdWhoseRecordIsByAnotherAuthorIsNotAccepted()
    {
        RunOnSta(() =>
        {
            var handler = new WrongAuthorAvatarHandler();
            TestHooks.UseCommunityHandler(handler);
            try
            {
                using var api = new VrcApiClient(null, handler);
                var shell = new MainViewModel(api);
                TestHooks.SetLoggedIn(shell);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);

                // The saved id resolves to a public ":3" by Someone Else, while the log
                // named gavial for this switch. The author check is what keeps that public
                // record from being opened as the player's avatar.
                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_activity",
                    DisplayName = "SyKari",
                    AvatarId = "avtr_wrong_author",
                    AvatarIdIsAuthoritative = true,
                    AvatarIdIsCurrentAvatar = true,
                    AvatarName = ":3",
                    AvatarAuthor = "gavial",
                    AvatarUrl = "https://api.vrchat.cloud/api/1/image/file_sykari_cat/1/256",
                    Details = "changed avatar · :3"
                };

                shell.OpenActivityAvatarCommand.Execute(entry);

                Settle.Until(() => avatars.SelectedAvatar?.IsUnavailable == true);
                Assert.Equal("avtr_wrong_author", avatars.SelectedAvatar!.Id);
                Assert.False(avatars.SelectedAvatar.CanUse);
                Assert.Contains("file_sykari_cat", avatars.SelectedAvatar.ImageUrl);
                Assert.NotEqual("Someone Else", avatars.SelectedAvatar.AuthorName);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
            }
        });
    }

    [Fact]
    public void WearingAnAvatarRecordsTheSignedInUserImmediately()
    {
        RunOnSta(() =>
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "VrcCompanionTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using var api = new VrcApiClient(null, new SelfAvatarHandler());
                var me = new VrcUser
                {
                    Id = "usr_self",
                    DisplayName = "Me",
                    Username = "me",
                    CurrentAvatar = "avtr_old",
                    CurrentAvatarName = "Old Look",
                    Location = "wrld_test:1"
                };
                TestHooks.SetCurrentUser(api, me);

                var shell = new MainViewModel(api);
                TestHooks.SetLoggedIn(shell);
                TestHooks.SetCurrentUser(shell, me);
                var feed = new ActivityFeedViewModel(
                    api,
                    new ActivityStore(Path.Combine(directory, "activity.json")),
                    new FriendAvatarStore(Path.Combine(directory, "avatars.json")));
                feed.SetAccount(me.Id);
                feed.ApplyCurrentUser(me);
                TestHooks.AttachActivityFeed(shell, feed);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);

                TestHooks.PublishAvatarWorn(avatars, new Avatar
                {
                    Id = "avtr_new",
                    Name = "New Look",
                    ThumbnailImageUrl = "https://example.test/new-look.png"
                });

                Assert.Equal("avtr_new", me.CurrentAvatar);
                Assert.Equal("New Look", me.CurrentAvatarName);
                Settle.Until(() => feed.Entries.Any(entry =>
                    entry.Type == ActivityType.AvatarChanged &&
                    entry.UserId == me.Id &&
                    entry.AvatarId == "avtr_new"));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void SelfAvatarSwitchFromTheVrchatLogBecomesOneActivityRow()
    {
        RunOnSta(() =>
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "VrcCompanionTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using var api = new VrcApiClient();
                var me = new VrcUser
                {
                    Id = "usr_self",
                    DisplayName = "Me",
                    Username = "me",
                    CurrentAvatar = "avtr_old",
                    CurrentAvatarName = "Old Look",
                    CurrentAvatarThumbnailImageUrl = "https://example.test/old.png",
                    Location = "wrld_test:1"
                };
                TestHooks.SetCurrentUser(api, me);
                var shell = new MainViewModel(api);
                TestHooks.SetLoggedIn(shell);
                TestHooks.SetCurrentUser(shell, me);
                var feed = new ActivityFeedViewModel(
                    api,
                    new ActivityStore(Path.Combine(directory, "activity.json")),
                    new FriendAvatarStore(Path.Combine(directory, "avatars.json")));
                feed.SetAccount(me.Id);
                feed.ApplyCurrentUser(me);
                TestHooks.AttachActivityFeed(shell, feed);
                TestHooks.SeedLoggedAvatarBaseline(shell, me.Id, "Old Look");

                var switches = new Dictionary<string, LoggedAvatar>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Me"] = new("New Log Look", "Avatar Maker")
                };
                TestHooks.ApplyLoggedAvatarChanges(
                    shell,
                    new VrChatLogSnapshot(
                        "wrld_test:1",
                        Array.Empty<VrChatLogPlayer>(),
                        AvatarSwitches: switches));

                Assert.Equal("New Log Look", me.CurrentAvatarName);
                Settle.Until(() => feed.Entries.Any(entry =>
                    entry.Type == ActivityType.AvatarChanged &&
                    entry.UserId == me.Id &&
                    entry.EffectiveAvatarName == "New Log Look"));

                // The account endpoint catching up must resolve the same row,
                // not create a second switch event.
                me.CurrentAvatar = "avtr_log_new";
                me.CurrentAvatarName = "New Log Look";
                me.CurrentAvatarThumbnailImageUrl = "https://example.test/new-log.png";
                feed.ApplyCurrentUser(me);

                Assert.Single(feed.Entries, entry =>
                    entry.Type == ActivityType.AvatarChanged &&
                    entry.UserId == me.Id);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void AvatarChangedActivityProfileActionOpensTheProfilePopup()
    {
        RunOnSta(() =>
        {
            var handler = new ActivityProfileHandler();
            using var api = new VrcApiClient(null, handler);
            var shell = new MainViewModel(api);
            TestHooks.SetLoggedIn(shell);
            var entry = new ActivityEntry
            {
                Type = ActivityType.AvatarChanged,
                UserId = "usr_activity",
                DisplayName = "Activity Person",
                AvatarId = "avtr_public_change",
                Details = "changed avatar"
            };

            shell.OpenActivityUserCommand.Execute(entry);
            Settle.Until(() => shell.SelectedFriendDetail?.Profile is not null);

            Assert.Equal("Live Activity", shell.SelectedFriendDetail!.DisplayName);
        });
    }

    [Fact]
    public void AvatarChangedPictureWithoutAnIdOpensAViewOnlyPopup()
    {
        RunOnSta(() =>
        {
            using var api = new VrcApiClient(null, new ActivityAvatarHandler(isPrivate: true));
            var shell = new MainViewModel(api);
            TestHooks.SetLoggedIn(shell);
            var avatars = new AvatarViewModel(api);
            TestHooks.AttachAvatarViewModel(shell, avatars);
            var entry = new ActivityEntry
            {
                Type = ActivityType.AvatarChanged,
                UserId = "usr_activity",
                DisplayName = "Activity Person",
                AvatarUrl = "https://example.test/profile-picture",
                Details = "changed avatar"
            };

            shell.OpenActivityAvatarCommand.Execute(entry);

            // The picture-only popup opens synchronously, before any lookup.
            Assert.NotNull(avatars.SelectedAvatar);
            Assert.Equal(entry.AvatarUrl, avatars.SelectedAvatar!.ImageUrl);
            Assert.True(avatars.SelectedAvatar.IsUnavailable);
            Assert.False(avatars.SelectedAvatar.CanUse);
            Assert.Contains("unavailable", avatars.SelectedAvatar.UseHint);
            Assert.Equal("Private or unlisted avatar", avatars.SelectedAvatar.Name);
        });
    }

    [Fact]
    public void AvatarChangeHistoryBackfillsAnIdFromTheRememberedPicture()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "VrcCompanionTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var avatarUrl = "https://api.vrchat.cloud/api/1/image/file_remembered/1/128";
            var avatarStore = new FriendAvatarStore(Path.Combine(directory, "avatars.json"));
            avatarStore.RememberImageResolution(
                "acct_activity",
                avatarUrl,
                "avtr_remembered",
                "public");
            var activityStore = new ActivityStore(Path.Combine(directory, "activity.json"));
            activityStore.Save("acct_activity", new[]
            {
                new ActivityEntry
                {
                    Id = "avatar-usr_remembered-file_remembered",
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_remembered",
                    DisplayName = "Remembered Person",
                    AvatarUrl = avatarUrl,
                    Details = "changed avatar",
                    Timestamp = DateTimeOffset.UtcNow
                }
            });

            using var api = new VrcApiClient(null, new ActivityAvatarHandler(isPrivate: false));
            var feed = new ActivityFeedViewModel(api, activityStore, avatarStore);
            feed.SetAccount("acct_activity");
            Settle.Until(() => feed.Entries.FirstOrDefault()?.AvatarId == "avtr_remembered");
            Settle.Until(() => feed.Entries.First().Details.Contains("Public Avatar"));

            var entry = Assert.Single(feed.Entries);
            Assert.True(entry.CanOpenAvatar);
            Assert.True(entry.HasAvatar);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PrivateAvatarChangedActivityOpensAViewOnlyPopup()
    {
        RunOnSta(() =>
        {
            TestHooks.UseCommunityHandler(new ActivityAvatarCommunityHandler());
            try
            {
                var handler = new ActivityAvatarHandler(isPrivate: true);
                using var api = new VrcApiClient(null, handler);
                var shell = new MainViewModel(api);
                TestHooks.SetLoggedIn(shell);
                var avatars = new AvatarViewModel(api);
                TestHooks.AttachAvatarViewModel(shell, avatars);
                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_activity",
                    DisplayName = "Activity Person",
                    AvatarId = "avtr_private_change",
                    Details = "changed avatar · Secret Kitten"
                };

                shell.OpenActivityAvatarCommand.Execute(entry);
                Settle.Until(() => avatars.SelectedAvatar?.IsPrivate == true);

                Assert.True(avatars.SelectedAvatar!.IsPrivate);
                Assert.False(avatars.SelectedAvatar.CanUse);
                Assert.Equal("Secret Kitten", avatars.SelectedAvatar.Name);
                Assert.Contains("owner can wear", avatars.SelectedAvatar.UseHint);
            }
            finally
            {
                TestHooks.UseCommunityHandler(null);
            }
        });
    }

    [Fact]
    public void RefreshFriendProfileBypassesBothGroupCaches()
    {
        RunOnSta(() =>
        {
            var handler = new RefreshHandler();
            using var api = new VrcApiClient(null, handler);
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_cache", DisplayName = "Me", Username = "me" });

            var notesDirectory = Path.Combine(Path.GetTempPath(), "vrccompanion-refresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(notesDirectory);
            try
            {
                var profile = new FriendDetailViewModel(
                    new FriendItem(new VrcUser { Id = "usr_friend", DisplayName = "Friend", Username = "friend" }),
                    api,
                    ImageLoader.Shared,
                    new FriendNotesStore(Path.Combine(notesDirectory, "notes.json")));

                profile.LoadAsync(forceRefresh: false, CancellationToken.None).GetAwaiter().GetResult();
                Settle.Until(() => handler.FriendGroupGets >= 1 && handler.MyGroupGets >= 1);

                Assert.Equal(1, handler.ProfileGets);
                Assert.Equal(1, handler.MyGroupGets);
                Assert.Equal(1, handler.FriendGroupGets);

                var shell = new MainViewModel();
                TestHooks.AttachFriendDetail(shell, profile);
                shell.RefreshFriendProfileCommand.Execute(null);

                Settle.Until(() => handler.FriendGroupGets >= 2 && handler.MyGroupGets >= 2);

                Assert.Equal(2, handler.ProfileGets);
                Assert.Equal(2, handler.MyGroupGets);
                Assert.Equal(2, handler.FriendGroupGets);
            }
            finally
            {
                Directory.Delete(notesDirectory, recursive: true);
            }
        });
    }

    [Fact]
    public void ProfileInviteAndRequestAreAvailableWithoutAKnownCurrentWorld()
    {
        using var shell = new MainViewModel();
        TestHooks.SetLoggedIn(shell);
        var friend = new FriendItem(new VrcUser
        {
            Id = "usr_invite_target",
            DisplayName = "Invite Target",
            Username = "invitetarget",
            Status = "active",
            Location = "private"
        });

        Assert.True(shell.InviteFriendCommand.CanExecute(friend));
        Assert.True(shell.RequestInviteFriendCommand.CanExecute(friend));
    }

    [Fact]
    public void HeaderFriendsCommandFocusesTheFriendsSection()
    {
        RunOnSta(() =>
        {
            var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            shell.SelectedSection = Section(shell, "activity");

            shell.OpenFriendsCommand.Execute(null);

            Assert.Equal("friends", shell.SelectedSection.Key);
            Assert.False(Section(shell, "friends").IsVisible);
        });
    }

    [Fact]
    public void FriendFiltersSeparateInGameActiveAndOfflinePresence()
    {
        RunOnSta(() =>
        {
            var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            shell.Friends.Add(new FriendItem(new VrcUser
            {
                Id = "usr_ingame",
                DisplayName = "In Game",
                State = "online",
                Status = "join me",
                Location = "wrld_11111111-1111-1111-1111-111111111111:12345"
            }));
            shell.Friends.Add(new FriendItem(new VrcUser
            {
                Id = "usr_web",
                DisplayName = "Web Active",
                State = "active",
                Status = "join me",
                Location = "offline"
            }));
            shell.Friends.Add(new FriendItem(new VrcUser
            {
                Id = "usr_offline",
                DisplayName = "Offline",
                State = "offline",
                Status = "offline",
                Location = "offline"
            }));

            string[] Filtered(string key)
            {
                shell.SelectedFriendFilter = shell.FriendFilters.Single(option => option.Key == key);
                return shell.FriendsView.Cast<FriendItem>().Select(friend => friend.Id).ToArray();
            }

            Assert.Equal(new[] { "usr_ingame" }, Filtered("online"));
            Assert.Equal(new[] { "usr_web" }, Filtered("active"));
            Assert.Equal(new[] { "usr_ingame" }, Filtered("joinme"));
            Assert.Equal(new[] { "usr_offline" }, Filtered("offline"));
        });
    }

    [Fact]
    public void ActiveAccountLabelFollowsTheSignedInUserNotStaleStoredState()
    {
        // A scratch store: the view model reads the signed-in picture into the
        // saved rows, and no test may write into the real accounts folder.
        var directory = Path.Combine(Path.GetTempPath(), "vrccompanion-accounts-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var api = new VrcApiClient();
            TestHooks.SetCurrentUser(api, new VrcUser
            {
                Id = "usr_chebe",
                DisplayName = "chebe",
                Username = "chebe"
            });

            var accounts = new MultiAccountViewModel(api, new MultiAccountService(directory));

            Assert.Equal("chebe", accounts.ActiveAccountLabel);
            Assert.Equal("C", accounts.ActiveAccountInitial);
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover scratch folder is not what this case is about.
            }
        }
    }

    [Fact]
    public void MembershipEventFromTheGroupViewModelUpdatesTheOpenProfile()
    {
        RunOnSta(() =>
        {
            var shell = new MainViewModel();
            var groups = new GroupViewModel(new VrcApiClient());
            var notesDirectory = Path.Combine(Path.GetTempPath(), "vrccompanion-event-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(notesDirectory);
            try
            {
                var profile = new FriendDetailViewModel(
                    new FriendItem(new VrcUser { Id = "usr_event", DisplayName = "Event", Username = "event" }),
                    new VrcApiClient(),
                    ImageLoader.Shared,
                    new FriendNotesStore(Path.Combine(notesDirectory, "notes.json")));

                TestHooks.AttachGroupViewModel(shell, groups);
                TestHooks.AttachFriendDetail(shell, profile);

                var group = new Group { Id = "grp_event", Name = "Event Group" };
                groups.PublishMembershipChanged(group, isMember: true);
                Assert.Single(profile.MyGroups);
                Assert.Equal("Event Group", profile.MyGroups[0].Name);

                groups.PublishMembershipChanged(group, isMember: false);
                Assert.Empty(profile.MyGroups);
            }
            finally
            {
                Directory.Delete(notesDirectory, recursive: true);
            }
        });
    }

    [Fact]
    public void SwitchingSectionsTogglesBrowseTabVisibility()
    {
        RunOnSta(() =>
        {
            var shell = new MainViewModel();
            var avatar = new AvatarViewModel(new VrcApiClient());
            TestHooks.AttachAvatarViewModel(shell, avatar);

            // The shell starts on Profile by assigning the field, not the setter.
            Assert.False(avatar.IsBrowseTabActive);

            shell.SelectedSection = Section(shell, "avatarsearch");
            Assert.True(avatar.IsBrowseTabActive);

            shell.SelectedSection = Section(shell, "friends");
            Assert.False(avatar.IsBrowseTabActive);

            shell.SelectedSection = Section(shell, "avatarsearch");
            Assert.True(avatar.IsBrowseTabActive);
        });
    }

    private static NavItem Section(MainViewModel shell, string key)
        => shell.Sections.Single(section => section.Key == key);

    /// <summary>The shell builds WPF collection views and a dispatcher timer.</summary>
    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("MainViewModel section test did not finish.");
        }

        failure?.Throw();
    }

    private sealed class ActivityAvatarHandler : HttpMessageHandler
    {
        private readonly bool _isPrivate;

        public ActivityAvatarHandler(bool isPrivate)
        {
            _isPrivate = isPrivate;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/avatars", StringComparison.Ordinal) &&
                request.RequestUri?.Query.Contains("search=", StringComparison.Ordinal) == true)
            {
                return Json(_isPrivate
                    ? "[]"
                    : """
                      [
                        {
                          "id": "avtr_private_duplicate",
                          "name": "Mellzie",
                          "authorName": "Avatar Maker",
                          "releaseStatus": "private"
                        },
                        {
                          "id": "avtr_public_change",
                          "name": "Mellzie",
                          "authorName": "Avatar Maker",
                          "releaseStatus": "public",
                          "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_log_avatar/1/256"
                        }
                      ]
                      """);
            }

            if (path.Contains("/avatars/", StringComparison.Ordinal))
            {
                if (_isPrivate)
                {
                    return Json("""
                        {
                          "id": "avtr_private_change",
                          "name": "Secret Kitten",
                          "authorName": "Avatar Maker",
                          "releaseStatus": "private"
                        }
                        """);
                }

                return Json("""
                    {
                      "id": "avtr_public_change",
                      "name": "Public Avatar",
                      "authorName": "Avatar Maker",
                      "releaseStatus": "public"
                    }
                    """);
            }

            // The popup may load favourites in the background.
            return Json("[]");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class SelfAvatarHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Json("""
                {
                  "id": "avtr_new",
                  "name": "New Look",
                  "authorName": "Me",
                  "releaseStatus": "private",
                  "thumbnailImageUrl": "https://example.test/new-look.png"
                }
                """);
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class PlayerOwnAvatarHandler : HttpMessageHandler
    {
        private int _ownAvatarGets;
        private int _searchRequests;

        public int OwnAvatarGets => Volatile.Read(ref _ownAvatarGets);

        public int SearchRequests => Volatile.Read(ref _searchRequests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/avatars/avtr_her_davina", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _ownAvatarGets);
                return Json("""
                    {
                      "id": "avtr_her_davina",
                      "name": "Her DAVINA",
                      "authorName": "Jamuu",
                      "releaseStatus": "public",
                      "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_her_davina/1/256"
                    }
                    """);
            }

            if (path.EndsWith("/avatars", StringComparison.Ordinal) &&
                request.RequestUri?.Query.Contains("search=", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref _searchRequests);
                return Json("""
                    [
                      {
                        "id": "avtr_other_davina",
                        "name": "DAVINA",
                        "authorName": "Someone Else",
                        "releaseStatus": "public"
                      }
                    ]
                    """);
            }

            // The popup may load favourites in the background.
            return Json("[]");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class PicturedNameOnlyHandler : HttpMessageHandler
    {
        private const string Record = """
            [
              {
                "id": "avtr_hays_public",
                "name": "Hays 2.0 (mixed)",
                "authorName": "Hays Maker",
                "releaseStatus": "public",
                "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_hays_row/1/256"
              }
            ]
            """;

        private int _searches;

        public int Searches => Volatile.Read(ref _searches);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri is null)
            {
                return Json("[]");
            }

            if (uri.Host.Contains("vrchat", StringComparison.OrdinalIgnoreCase))
            {
                if (uri.AbsolutePath.Contains("/avatars/", StringComparison.Ordinal))
                {
                    return Json("""
                        {
                          "id": "avtr_hays_public",
                          "name": "Hays 2.0 (mixed)",
                          "authorName": "Hays Maker",
                          "releaseStatus": "public",
                          "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_hays_row/1/256"
                        }
                        """);
                }

                if (uri.AbsolutePath.EndsWith("/avatars", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _searches);
                }

                return Json("[]");
            }

            return Json(Record);
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class NamesakeProbeHandler : HttpMessageHandler
    {
        private int _searches;
        private int _avatarGets;

        public int Searches => Volatile.Read(ref _searches);

        public int AvatarGets => Volatile.Read(ref _avatarGets);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            // Only VRChat's avatar endpoint counts: a community provider URL can also
            // contain "/avatars/", and those requests are not avatar reads.
            var isVrChat = request.RequestUri?.Host.Contains("vrchat", StringComparison.OrdinalIgnoreCase) == true;
            if (isVrChat && path.Contains("/avatars/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _avatarGets);
                return Json("""
                    {
                      "id": "avtr_namesake_davina",
                      "name": "DAVINA",
                      "authorName": "Someone Else",
                      "releaseStatus": "public"
                    }
                    """);
            }

            if (path.EndsWith("/avatars", StringComparison.Ordinal) &&
                request.RequestUri?.Query.Contains("search=", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref _searches);
                return Json("""
                    [
                      {
                        "id": "avtr_namesake_davina",
                        "name": "DAVINA",
                        "authorName": "Someone Else",
                        "releaseStatus": "public",
                        "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_namesake/1/256"
                      }
                    ]
                    """);
            }

            // The popup may load favourites in the background.
            return Json("[]");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class RefusedAvatarIdHandler : HttpMessageHandler
    {
        private const string IndexRows = """
            [
              {
                "id": "avtr_namesake_frost",
                "name": "Frost",
                "authorName": "Someone Else",
                "releaseStatus": "public",
                "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_namesake/1/256"
              },
              {
                "id": "avtr_refused_frost",
                "name": "Frost",
                "authorName": "Demon_Edi",
                "releaseStatus": "public",
                "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_row_frost/1/256"
              }
            ]
            """;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri is null)
            {
                return Json("[]");
            }

            if (uri.Host.Contains("vrchat", StringComparison.OrdinalIgnoreCase))
            {
                if (uri.AbsolutePath.Contains("/avatars/", StringComparison.Ordinal))
                {
                    // VRChat will not expose this public avatar's record by id.
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                if (uri.Query.Contains("search=", StringComparison.Ordinal))
                {
                    return Json(IndexRows);
                }

                // The popup may load favourites in the background.
                return Json("[]");
            }

            return Json(IndexRows);
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    /// <summary>
    /// VRChat refuses the avatar record by id and a public namesake answers every name
    /// search. A row whose id came from the player's own record must never reach it.
    /// </summary>
    private sealed class PrivateAvatarProbeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri is null)
            {
                return Json("[]");
            }

            if (uri.Host.Contains("vrchat", StringComparison.OrdinalIgnoreCase))
            {
                if (uri.AbsolutePath.Contains("/avatars/", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                if (uri.Query.Contains("search=", StringComparison.Ordinal))
                {
                    return Json("""
                        [
                          {
                            "id": "avtr_gavial_public",
                            "name": ":3",
                            "authorName": "gavial",
                            "releaseStatus": "public",
                            "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_gavial/1/256"
                          }
                        ]
                        """);
                }

                return Json("[]");
            }

            // The public indexes know no record for this private avatar.
            return Json("[]");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    /// <summary>
    /// The saved id resolves to a public avatar, but by a different creator than the log
    /// named for this switch.
    /// </summary>
    private sealed class WrongAuthorAvatarHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri is null)
            {
                return Json("[]");
            }

            if (uri.Host.Contains("vrchat", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.Contains("/avatars/", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "id": "avtr_wrong_author",
                      "name": ":3",
                      "authorName": "Someone Else",
                      "releaseStatus": "public",
                      "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_other/1/256"
                    }
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

    private sealed class ActivityAvatarCommunityHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Json("""
                [
                  {
                    "id": "avtr_private_duplicate",
                    "name": "Mellzie",
                    "authorName": "Avatar Maker",
                    "releaseStatus": "private"
                  },
                  {
                    "id": "avtr_public_change",
                    "name": "Mellzie",
                    "authorName": "Avatar Maker",
                    "releaseStatus": "public",
                    "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_log_avatar/1/256"
                  }
                ]
                """);
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class ActivityProfileHandler : HttpMessageHandler
    {
        private int _profileGets;

        public int ProfileGets => Volatile.Read(ref _profileGets);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/profile/usr_activity", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _profileGets);
                return Json("""
                    {
                      "id": "usr_activity",
                      "displayName": "Live Activity",
                      "bio": "Fetched from VRChat",
                      "groups": { "count": 0, "list": [] },
                      "representedGroup": null
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

    private sealed class RefreshHandler : HttpMessageHandler
    {
        private int _profileGets;
        private int _myGroupGets;
        private int _friendGroupGets;

        public int ProfileGets => Volatile.Read(ref _profileGets);

        public int MyGroupGets => Volatile.Read(ref _myGroupGets);

        public int FriendGroupGets => Volatile.Read(ref _friendGroupGets);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains("/profile/usr_friend", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _profileGets);
                return Json("""
                    {
                      "id": "usr_friend",
                      "displayName": "Friend",
                      "username": "friend",
                      "groups": {
                        "count": 3,
                        "list": [ { "id": "grp_1", "name": "One" } ]
                      },
                      "representedGroup": { "id": "grp_1", "name": "One" }
                    }
                    """);
            }

            if (path.Contains("/users/usr_cache/groups", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _myGroupGets);
                return Json("[{\"id\":\"gmem_1\",\"groupId\":\"grp_1\",\"name\":\"One\",\"membershipStatus\":\"member\"}]");
            }

            if (path.Contains("/users/usr_friend/groups", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _friendGroupGets);
                return Json("[{\"id\":\"gmem_1\",\"groupId\":\"grp_1\",\"name\":\"One\",\"membershipStatus\":\"member\"},{\"id\":\"gmem_2\",\"groupId\":\"grp_2\",\"name\":\"Two\",\"membershipStatus\":\"member\"},{\"id\":\"gmem_3\",\"groupId\":\"grp_3\",\"name\":\"Three\",\"membershipStatus\":\"member\"}]");
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
