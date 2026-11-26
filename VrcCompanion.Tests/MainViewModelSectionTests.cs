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
                var entry = new ActivityEntry
                {
                    Type = ActivityType.AvatarChanged,
                    UserId = "usr_activity",
                    DisplayName = "Activity Person",
                    AvatarName = "Mellzie",
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
