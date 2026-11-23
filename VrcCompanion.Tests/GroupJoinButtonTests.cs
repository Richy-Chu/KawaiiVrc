using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The popup joins and requests through VRChat's own endpoint: POST groups/{id}/join answers
/// with the member object, and its membershipStatus says whether the account is now a member
/// or has a request pending.
/// </summary>
public class GroupJoinButtonTests
{
    [Theory]
    [InlineData("open", "Join group", true)]
    [InlineData("request", "Request to join", true)]
    [InlineData("invite", "Join group", false)]
    [InlineData("closed", "Join group", false)]
    public void JoinButtonMatchesTheGroupJoinState(string joinState, string text, bool enabled)
    {
        using var api = new VrcApiClient();
        var vm = new GroupViewModel(api)
        {
            GroupInfo = new Group { Id = "grp_1", JoinState = joinState }
        };

        Assert.Equal(text, vm.GroupJoinButtonText);
        Assert.Equal(enabled, vm.CanUseGroupJoinButton);
        Assert.Equal(enabled, vm.JoinGroupInfoCommand.CanExecute(null));
        Assert.False(vm.IsJoinRequestPending);
    }

    [Fact]
    public void MemberCannotUseTheJoinButton()
    {
        using var api = new VrcApiClient();
        var vm = new GroupViewModel(api)
        {
            GroupInfo = new Group { Id = "grp_1", JoinState = "open" },
            IsMemberOfGroupInfo = true
        };

        Assert.False(vm.CanUseGroupJoinButton);
    }

    [Fact]
    public async Task OpenGroupJoinsFromTheApp()
    {
        using var handler = new JoinHandler("member");
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            GroupInfo = new Group { Id = "grp_open", JoinState = "open" }
        };

        await JoinAsync(vm);

        Assert.Equal(1, handler.JoinPosts);
        Assert.True(vm.IsMemberOfGroupInfo);
        Assert.Equal("You are a member", vm.GroupInfoMemberText);
        Assert.Equal("Joined", vm.GroupInfoMembershipLabel);
        Assert.Equal(string.Empty, vm.GroupJoinHint);
        Assert.False(vm.CanUseGroupJoinButton);
    }

    [Fact]
    public async Task RequestOnlyGroupSendsTheRequestFromTheAppAndShowsPending()
    {
        using var handler = new JoinHandler("requested");
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            GroupInfo = new Group { Id = "grp_req", JoinState = "request", Name = "KushCOOKIES" }
        };

        await JoinAsync(vm);

        Assert.Equal(1, handler.JoinPosts);
        Assert.False(vm.IsMemberOfGroupInfo);
        Assert.True(vm.IsJoinRequestPending);
        Assert.Equal("Your join request is pending", vm.GroupInfoMemberText);
        Assert.Equal("Request pending", vm.GroupInfoMembershipLabel);
        Assert.Equal("Request pending", vm.GroupJoinButtonText);
        Assert.False(vm.CanUseGroupJoinButton);
        Assert.Contains("pending", vm.GroupJoinHint, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task JoinAsync(GroupViewModel vm)
    {
        vm.JoinGroupInfoCommand.Execute(null);
        // The fake handler answers at once; ten seconds is scheduler headroom.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (vm.IsGroupInfoBusy && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    private sealed class JoinHandler : HttpMessageHandler
    {
        private readonly string _status;

        public JoinHandler(string status) => _status = status;

        public int JoinPosts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Post && path.EndsWith("/join", StringComparison.Ordinal))
            {
                JoinPosts++;
                return Json($$"""
                    {
                      "id": "gmem_1",
                      "groupId": "grp_1",
                      "userId": "usr_1",
                      "membershipStatus": "{{_status}}"
                    }
                    """);
            }

            return Json("[]");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
