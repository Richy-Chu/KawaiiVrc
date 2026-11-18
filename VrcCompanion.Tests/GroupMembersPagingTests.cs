using System;
using System.Collections.Generic;
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

/// <summary>
/// VRChat's group members endpoint pages with n=100 and offset. Load more continues past the
/// rows already shown, and a short page ends the roster even when the cached count is stale.
/// </summary>
public class GroupMembersPagingTests
{
    [Fact]
    public async Task LoadMoreAppendsTheNextPageUntilTheRosterEnds()
    {
        using var handler = new MemberPageHandler(total: 250);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };

        await SettleAsync(vm);
        Assert.Equal(100, vm.Members.Count);
        Assert.True(vm.HasMoreMembers);
        Assert.True(vm.CanLoadMoreMembers);
        Assert.Equal(
            "Showing 100 of 250 members — Load more fetches the next 100.",
            vm.MembersSummary);

        vm.LoadMoreMembersCommand.Execute(null);
        await SettleAsync(vm);
        Assert.Equal(200, vm.Members.Count);
        Assert.True(vm.CanLoadMoreMembers);

        vm.LoadMoreMembersCommand.Execute(null);
        await SettleAsync(vm);
        Assert.Equal(250, vm.Members.Count);
        Assert.False(vm.HasMoreMembers);
        Assert.False(vm.CanLoadMoreMembers);
        Assert.Equal("250 members loaded", vm.MembersSummary);
        Assert.Equal(new[] { 0, 100, 200 }, handler.Offsets);
    }

    [Fact]
    public async Task ShortPageStopsPagingEvenWhenTheCachedCountIsHigher()
    {
        using var handler = new MemberPageHandler(total: 120);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 5000 }
        };

        await SettleAsync(vm);
        vm.LoadMoreMembersCommand.Execute(null);
        await SettleAsync(vm);

        Assert.Equal(120, vm.Members.Count);
        Assert.False(vm.HasMoreMembers);
        Assert.False(vm.CanLoadMoreMembers);
    }

    [Fact]
    public async Task RefreshingMembersStartsAgainFromTheFirstPage()
    {
        using var handler = new MemberPageHandler(total: 250);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };

        await SettleAsync(vm);
        vm.LoadMoreMembersCommand.Execute(null);
        await SettleAsync(vm);
        Assert.Equal(200, vm.Members.Count);

        vm.RefreshMembersCommand.Execute(null);
        await SettleAsync(vm);

        Assert.Equal(100, vm.Members.Count);
        Assert.True(vm.CanLoadMoreMembers);
        Assert.Equal(new[] { 0, 100, 0 }, handler.Offsets);
    }

    private static async Task SettleAsync(GroupViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.IsBusy && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.False(vm.IsBusy);
    }

    private sealed class MemberPageHandler : HttpMessageHandler
    {
        private readonly int _total;

        public MemberPageHandler(int total) => _total = total;

        public List<int> Offsets { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var query = request.RequestUri?.Query ?? string.Empty;
            if (!path.EndsWith("/members", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                });
            }

            var offset = ReadInt(query, "offset");
            var count = ReadInt(query, "n", fallback: 100);
            Offsets.Add(offset);

            var available = Math.Max(0, Math.Min(count, _total - offset));
            var members = Enumerable.Range(offset, available)
                .Select(index => $$"""
                    {"userId":"usr_{{index}}","userName":"Member {{index:D4}}"}
                    """);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[" + string.Join(",", members) + "]",
                    Encoding.UTF8,
                    "application/json")
            });
        }

        private static int ReadInt(string query, string key, int fallback = 0)
        {
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == key && int.TryParse(parts[1], out var value))
                {
                    return value;
                }
            }

            return fallback;
        }
    }
}
