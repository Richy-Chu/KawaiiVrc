using System;
using System.Linq;
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
/// A world refresh rebuilds every instance row, and each group instance needs its group.
/// The dialog must ask VRChat once per group, not once per row and not again per refresh.
/// </summary>
public class WorldDialogGroupCacheTests
{
    private const string WorldId = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";

    [Fact]
    public async Task RefreshReusesTheGroupAlreadyLookedUp()
    {
        using var handler = new GroupCountingHandler();
        using var api = new VrcApiClient(null, handler);
        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared);

        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);
        await (dialog.GroupHydration ?? Task.CompletedTask);

        var groupRows = dialog.Instances.Where(item => item.HasGroup).ToList();
        Assert.Equal(2, groupRows.Count);
        Assert.All(groupRows, item => Assert.Equal("Test Group", item.Title));
        Assert.All(groupRows, item => Assert.Equal("Group · Joined", item.GroupChipText));
        Assert.Equal(1, handler.GroupRequests);

        await dialog.LoadAsync(forceRefresh: true, CancellationToken.None);
        await (dialog.GroupHydration ?? Task.CompletedTask);

        Assert.Equal(2, dialog.Instances.Count(item => item.HasGroup));
        Assert.All(dialog.Instances.Where(item => item.HasGroup), item =>
            Assert.Equal("Test Group", item.Title));
        Assert.Equal(1, handler.GroupRequests);
    }

    [Fact]
    public async Task JoiningTheGroupUpdatesTheOpenRowsAndDropsTheCache()
    {
        using var handler = new GroupCountingHandler(membership: "inactive", joinState: "open");
        using var api = new VrcApiClient(null, handler);
        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared);

        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);
        await (dialog.GroupHydration ?? Task.CompletedTask);

        Assert.All(dialog.Instances.Where(item => item.HasGroup), item =>
            Assert.Equal("Group · Open to join", item.GroupChipText));

        dialog.ApplyGroupMembership("grp_abc", isMember: true);

        Assert.All(dialog.Instances.Where(item => item.HasGroup), item =>
            Assert.Equal("Group · Joined", item.GroupChipText));

        await dialog.LoadAsync(forceRefresh: true, CancellationToken.None);
        await (dialog.GroupHydration ?? Task.CompletedTask);

        // The cached membership was dropped, so the refresh asks again rather than
        // reusing "not a member" from before the join.
        Assert.Equal(2, handler.GroupRequests);
    }

    private sealed class GroupCountingHandler : HttpMessageHandler
    {
        private readonly string _membership;
        private readonly string _joinState;

        public GroupCountingHandler(string membership = "member", string joinState = "open")
        {
            _membership = membership;
            _joinState = joinState;
        }

        public int GroupRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith($"/worlds/{WorldId}", StringComparison.Ordinal))
            {
                return Json($$"""
                    {
                      "id": "{{WorldId}}",
                      "name": "Popcorn Palace",
                      "slimInstances": [
                        {
                          "instanceId": "111~group(grp_abc)~groupAccessType(public)",
                          "location": "{{WorldId}}:111~group(grp_abc)~groupAccessType(public)",
                          "userCount": 38,
                          "capacity": 80,
                          "region": "use"
                        },
                        {
                          "instanceId": "222~group(grp_abc)~groupAccessType(members)",
                          "location": "{{WorldId}}:222~group(grp_abc)~groupAccessType(members)",
                          "userCount": 12,
                          "capacity": 80,
                          "region": "use"
                        },
                        {
                          "instanceId": "333",
                          "location": "{{WorldId}}:333",
                          "userCount": 5,
                          "capacity": 40,
                          "region": "us"
                        }
                      ]
                    }
                    """);
            }

            if (path.EndsWith("/groups/grp_abc", StringComparison.Ordinal))
            {
                GroupRequests++;
                return Json($$"""
                    {
                      "id": "grp_abc",
                      "name": "Test Group",
                      "membershipStatus": "{{_membership}}",
                      "joinState": "{{_joinState}}"
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
