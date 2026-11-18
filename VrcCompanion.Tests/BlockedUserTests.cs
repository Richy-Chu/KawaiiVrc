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

public class BlockedUserTests
{
    [Fact]
    public async Task PlayerModerationsReturnOnlyActiveBlocks()
    {
        using var api = new VrcApiClient(null, new BlockedUsersHandler());

        var blocked = await api.GetBlockedUserIdsAsync();

        Assert.Contains("usr_blocked", blocked);
        Assert.DoesNotContain("usr_unblocked", blocked);
        Assert.DoesNotContain("usr_muted", blocked);
        Assert.DoesNotContain("usr_other", blocked);
        Assert.Single(blocked);
    }

    [Fact]
    public async Task ForcedBlockRefreshBypassesCache()
    {
        using var api = new VrcApiClient(null, new ChangingBlockedUsersHandler());

        var first = await api.GetBlockedUserIdsAsync();
        var second = await api.GetBlockedUserIdsAsync(forceRefresh: true);

        Assert.Contains("usr_first", first);
        Assert.DoesNotContain("usr_second", first);
        Assert.Contains("usr_second", second);
        Assert.DoesNotContain("usr_first", second);
    }

    [Fact]
    public void InstanceRowShowsTheBlockedMarkerForAUserYouBlocked()
    {
        var player = new InstancePlayerItem(
            new VrcUser
            {
                Id = "usr_blocked",
                DisplayName = "Blocked Player"
            },
            isFriend: false,
            isSelf: false,
            isBlocked: true);

        Assert.True(player.IsBlocked);
        Assert.True(player.HasBlockedMarker);
        Assert.Equal("(B)", player.BlockedMarker);
        Assert.Contains("You have blocked", player.BlockedToolTip);
    }

    [Fact]
    public void InstanceRowDoesNotShowTheBlockedMarkerForOtherPlayers()
    {
        var player = new InstancePlayerItem(
            new VrcUser { Id = "usr_other", DisplayName = "Other Player" },
            isFriend: false,
            isSelf: false);

        Assert.False(player.IsBlocked);
        Assert.False(player.HasBlockedMarker);
        Assert.Equal(string.Empty, player.BlockedMarker);
        Assert.Equal(string.Empty, player.BlockedToolTip);
    }

    private sealed class ChangingBlockedUsersHandler : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var target = call == 1 ? "usr_first" : "usr_second";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    [
                      {
                        "id": "pmod_changing",
                        "type": "block",
                        "sourceUserId": "usr_me",
                        "targetUserId": "{{target}}"
                      }
                    ]
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class BlockedUsersHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (!path.Contains("/auth/user/playermoderations", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    [
                      {
                        "id": "pmod_block",
                        "type": "block",
                        "sourceUserId": "usr_me",
                        "targetUserId": "usr_blocked",
                        "targetDisplayName": "Blocked Player"
                      },
                      {
                        "id": "pmod_unblocked_block",
                        "type": "block",
                        "sourceUserId": "usr_me",
                        "targetUserId": "usr_unblocked"
                      },
                      {
                        "id": "pmod_unblocked",
                        "type": "unblock",
                        "sourceUserId": "usr_me",
                        "targetUserId": "usr_unblocked"
                      },
                      {
                        "id": "pmod_mute",
                        "type": "mute",
                        "sourceUserId": "usr_me",
                        "targetUserId": "usr_muted"
                      },
                      {
                        "id": "pmod_unblock",
                        "type": "unblock",
                        "targetUserId": "usr_other"
                      }
                    ]
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
