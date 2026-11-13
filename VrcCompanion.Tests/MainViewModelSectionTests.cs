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
            TestHooks.SetLoggedIn(shell, true);
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
            WaitUntil(() => handler.ProfileGets == 1 &&
                            shell.SelectedFriendDetail?.Profile is not null);

            var profile = Assert.IsType<FriendDetailViewModel>(shell.SelectedFriendDetail);
            Assert.Equal("usr_activity", profile.Id);
            Assert.Equal("Live Activity", profile.DisplayName);
            Assert.Equal(location, profile.JoinLocation);
            Assert.True(profile.CanJoin);
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
                WaitUntil(() => handler.FriendGroupGets >= 1 && handler.MyGroupGets >= 1);

                Assert.Equal(1, handler.ProfileGets);
                Assert.Equal(1, handler.MyGroupGets);
                Assert.Equal(1, handler.FriendGroupGets);

                var shell = new MainViewModel();
                TestHooks.AttachFriendDetail(shell, profile);
                shell.RefreshFriendProfileCommand.Execute(null);

                WaitUntil(() => handler.FriendGroupGets >= 2 && handler.MyGroupGets >= 2);

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

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }

        Assert.True(condition(), "Timed out waiting for the profile refresh.");
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
