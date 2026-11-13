using System;
using System.Collections.ObjectModel;
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

public class GroupSearchTests
{
    [Fact]
    public void JoinedGroupFilterNarrowsByNameAndClears()
    {
        using var api = new VrcApiClient();
        var vm = new GroupViewModel(api)
        {
            Groups = new ObservableCollection<Group>
            {
                new() { Id = "gmem_1", GroupId = "grp_1", Name = "After Dark" },
                new() { Id = "grp_2", Name = "Age Gated" }
            }
        };

        Assert.Equal(2, vm.VisibleGroups.Count);

        vm.GroupFilterText = "after";

        var match = Assert.Single(vm.VisibleGroups);
        Assert.Equal("After Dark", match.Name);

        vm.GroupFilterText = string.Empty;
        Assert.Equal(2, vm.VisibleGroups.Count);
    }

    [Fact]
    public async Task GroupSearchUsesNameQueryAndDirectIdLookup()
    {
        var handler = new SearchHandler();
        using var api = new VrcApiClient(null, handler);

        var byName = await api.SearchGroupsAsync("After Dark");
        var byId = await api.SearchGroupsAsync("grp_1");

        Assert.Equal("After Dark", Assert.Single(byName).Name);
        Assert.Equal("grp_1", Assert.Single(byId).Id);
        Assert.Equal(1, handler.NameSearches);
        Assert.Equal(1, handler.IdGets);
        Assert.Contains("query=After%20Dark", handler.LastQuery, StringComparison.Ordinal);
    }

    private sealed class SearchHandler : HttpMessageHandler
    {
        public int NameSearches { get; private set; }

        public int IdGets { get; private set; }

        public string LastQuery { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/groups/grp_1", StringComparison.Ordinal))
            {
                IdGets++;
                return Json("""
                    { "id": "grp_1", "name": "After Dark", "joinState": "open" }
                    """);
            }

            if (path.EndsWith("/groups", StringComparison.Ordinal))
            {
                NameSearches++;
                LastQuery = request.RequestUri?.Query ?? string.Empty;
                return Json("""
                    [ { "id": "grp_1", "name": "After Dark", "joinState": "open" } ]
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
}
