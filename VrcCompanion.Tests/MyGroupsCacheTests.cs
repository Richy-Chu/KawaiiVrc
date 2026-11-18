using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class MyGroupsCacheTests
{
    [Fact]
    public async Task RepeatedReadsReuseTheCacheAndJoinOrLeaveInvalidateIt()
    {
        var handler = new CountingHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser
        {
            Id = "usr_cache",
            DisplayName = "Cache",
            Username = "cache"
        });

        var first = await api.GetMyGroupsAsync();
        var second = await api.GetMyGroupsAsync();

        Assert.Equal(1, handler.GroupGets);
        Assert.Same(first, second);

        await api.JoinGroupAsync("grp_2");
        Assert.Equal(1, handler.GroupGets);

        await api.GetMyGroupsAsync();
        Assert.Equal(2, handler.GroupGets);

        await api.LeaveGroupAsync("grp_2");
        await api.GetMyGroupsAsync();
        Assert.Equal(3, handler.GroupGets);
        Assert.Equal(2, handler.MembershipPosts);
        Assert.Equal("grp_1", Assert.Single(first).EffectiveId);
    }

    [Fact]
    public async Task DecliningAGroupInviteInvalidatesTheMembershipCache()
    {
        var handler = new CountingHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser
        {
            Id = "usr_cache",
            DisplayName = "Cache",
            Username = "cache"
        });

        await api.GetMyGroupsAsync();
        await api.DeclineGroupInviteAsync("grp_2");
        await api.GetMyGroupsAsync();

        Assert.Equal(2, handler.GroupGets);
        Assert.Equal(1, handler.InviteDeclines);
        Assert.Equal(0, handler.MembershipPosts);
    }

    [Fact]
    public async Task FriendGroupListIsCachedUntilRefreshForcesIt()
    {
        var handler = new CountingHandler();
        using var api = new VrcApiClient(null, handler);

        await api.GetUserGroupsAsync("usr_friend");
        await api.GetUserGroupsAsync("usr_friend");
        Assert.Equal(1, handler.FriendGroupGets);

        await api.GetUserGroupsAsync("usr_friend", forceRefresh: true);
        Assert.Equal(2, handler.FriendGroupGets);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int GroupGets { get; private set; }

        public int FriendGroupGets { get; private set; }

        public int MembershipPosts { get; private set; }

        public int InviteDeclines { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Get &&
                path.Contains("/users/usr_cache/groups", StringComparison.Ordinal))
            {
                GroupGets++;
                return Json("[{\"id\":\"gmem_1\",\"groupId\":\"grp_1\",\"name\":\"One\",\"membershipStatus\":\"member\"}]");
            }

            if (request.Method == HttpMethod.Get &&
                path.Contains("/users/usr_friend/groups", StringComparison.Ordinal))
            {
                FriendGroupGets++;
                return Json("[{\"id\":\"gmem_2\",\"groupId\":\"grp_2\",\"name\":\"Friend Group\",\"membershipStatus\":\"member\"}]");
            }

            if (request.Method == HttpMethod.Put &&
                path.EndsWith("/invites", StringComparison.Ordinal))
            {
                InviteDeclines++;
            }

            if (request.Method == HttpMethod.Post &&
                (path.EndsWith("/join", StringComparison.Ordinal) ||
                 path.EndsWith("/leave", StringComparison.Ordinal)))
            {
                MembershipPosts++;
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
