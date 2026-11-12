using System;
using System.Collections.Generic;
using System.IO;
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
/// Drives AvatarViewModel through its real call paths with the fake community
/// handler installed, and asserts what the status line actually shows.
/// </summary>
[Collection("CommunityHandler")]
public class AvatarViewModelBrowseTests
{
    private sealed class ToggleHandler : HttpMessageHandler
    {
        public bool Html { get; set; } = true;

        public int Requests;

        public string Json { get; set; } = AvatarJson;

        public static string AvatarJson { get; } = """
            [
              {
                "id": "avtr_test-1",
                "name": "Test Avatar One",
                "authorId": "usr_test",
                "authorName": "Tester",
                "releaseStatus": "public"
              },
              {
                "id": "avtr_test-2",
                "name": "Test Avatar Two",
                "authorId": "usr_test",
                "authorName": "Tester",
                "releaseStatus": "public"
              }
            ]
            """;

        public static string CapturedHtml { get; } =
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "avtrdb-html-response.html"));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            var body = Html ? CapturedHtml : Json;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, Html ? "text/html" : "application/json")
            });
        }
    }

    private static async Task WaitUntilIdle(AvatarViewModel vm)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (vm.IsGlobalBusy)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("AvatarViewModel stayed busy.");
            }

            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Browse_FailureThenRecovery_ClearsFailedStatus()
    {
        var handler = new ToggleHandler { Html = true };
        TestHooks.UseCommunityHandler(handler);
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());

            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.Contains("avtr.icu failed", vm.CommunityStatusText);

            // Index recovers, then the refresh must replace the failure, not keep it.
            handler.Html = false;
            vm.RefreshBrowseCommand.Execute(null);
            await WaitUntilIdle(vm);

            Assert.Contains("avtr.icu 2", vm.CommunityStatusText);
            Assert.DoesNotContain("failed", vm.CommunityStatusText);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task Browse_ThrowingFetch_ReportsGlobalErrorAndRetriesOnNextOpen()
    {
        var handler = new ToggleHandler { Html = false };
        TestHooks.UseCommunityHandler(handler);
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            TestHooks.UseBrowseFetch(vm, (_, _) => throw new HttpRequestException("browse blew up"));

            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            Assert.Equal("Could not reach the community avatar index.", vm.GlobalError);
            Assert.Equal(string.Empty, vm.CommunityStatusText);

            // A throw must not mark browse as loaded: the next open retries, and a
            // success then clears both the error and the (empty) failure status.
            TestHooks.UseBrowseFetch(vm, null);
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            Assert.Contains("avtr.icu 2", vm.CommunityStatusText);
            Assert.Equal(string.Empty, vm.GlobalError);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task LoadMore_ThrowingFetch_ReportsGlobalErrorAndKeepsPage()
    {
        var handler = new ToggleHandler { Html = false, Json = IdsJson(Ids(50, "bulk")) };
        TestHooks.UseCommunityHandler(handler);
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.Contains("avtr.icu 50", vm.CommunityStatusText);

            TestHooks.UseBrowseFetch(vm, (_, _) => throw new HttpRequestException("load more blew up"));
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            Assert.Equal("Could not load more community avatars.", vm.GlobalError);
            // The failed page must not advance the browse page counter.
            Assert.Contains("avtr.icu 50", vm.CommunityStatusText);
            Assert.DoesNotContain("page 2", vm.CommunityStatusText);

            // The next attempt with a working index does advance and clears the error.
            handler.Json = IdsJson(Ids(50, "more"));
            TestHooks.UseBrowseFetch(vm, null);
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            Assert.Contains("page 2", vm.CommunityStatusText);
            Assert.Equal(string.Empty, vm.GlobalError);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task LoadMore_HtmlBody_ShowsFailedIndexAndDoesNotAdvancePage()
    {
        var handler = new ToggleHandler { Html = false, Json = IdsJson(Ids(50, "bulk")) };
        TestHooks.UseCommunityHandler(handler);
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.Contains("avtr.icu 50", vm.CommunityStatusText);

            // Real fetch path (no seam): the index now answers with HTML.
            handler.Html = true;
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            Assert.Contains("avtr.icu failed", vm.CommunityStatusText);
            Assert.DoesNotContain("page 2", vm.CommunityStatusText);
            Assert.Equal("Could not load more community avatars.", vm.GlobalError);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task LoadMore_DuplicateOnlyPage_DoesNotAdvanceOrReportZero()
    {
        var handler = new ToggleHandler { Html = false, Json = IdsJson(Ids(50, "bulk")) };
        TestHooks.UseCommunityHandler(handler);
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.Contains("avtr.icu 50", vm.CommunityStatusText);

            // Same 50 avatars again: nothing new, so no page advance and no "0".
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            Assert.Contains("avtr.icu 50", vm.CommunityStatusText);
            Assert.DoesNotContain("avtr.icu 0", vm.CommunityStatusText);
            Assert.DoesNotContain("page 2", vm.CommunityStatusText);
            Assert.Equal("No new public avatars on that page.", vm.GlobalError);

            // A genuinely new page still advances and counts only the new rows.
            handler.Json = IdsJson(Ids(50, "more"));
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            Assert.Contains("avtr.icu 50", vm.CommunityStatusText);
            Assert.Contains("page 2", vm.CommunityStatusText);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task LoadMore_PartialOverlap_CountsOnlyNewAvatarsAndAddsToTotal()
    {
        // Mirrors the real avtr.icu page 2: 50 rows, 21 of them already visible.
        var handler = new ToggleHandler { Html = false, Json = IdsJson(Ids(50, "bulk")) };
        TestHooks.UseCommunityHandler(handler);
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.Contains("avtr.icu 50", vm.CommunityStatusText);
            Assert.Equal(50, vm.GlobalResults.Count);

            handler.Json = IdsJson(Enumerable.Range(1, 50).Select(i => i <= 21 ? $"bulk-{i}" : $"more-{i}"));
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            // Only the 29 genuinely new avatars count, and they extend the list.
            Assert.Contains("avtr.icu 29", vm.CommunityStatusText);
            Assert.Contains("page 2", vm.CommunityStatusText);
            Assert.Equal(79, vm.GlobalResults.Count);
            Assert.Contains("79", vm.GlobalSummaryText);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    /// <summary>JSON array for the given avatar ids: the only page builder the tests use.</summary>
    private static string IdsJson(IEnumerable<string> ids) =>
        "[" + string.Join(",", ids.Select(id =>
            $"{{\"id\":\"avtr_{id}\",\"name\":\"{id}\",\"releaseStatus\":\"public\"}}")) + "]";

    /// <summary>Ids for a contiguous generated page, e.g. bulk-1 … bulk-50.</summary>
    private static List<string> Ids(int count, string prefix) =>
        Enumerable.Range(1, count).Select(i => $"{prefix}-{i}").ToList();

    [Fact]
    public async Task Browse_TwoMorePages_AccumulatesNewCountsPerPageAndReachesPage3()
    {
        var page1 = Enumerable.Range(1, 50).Select(i => $"bulk-{i}").ToList();
        // Page 2 repeats 21 of page 1 and adds 29 new avatars.
        var page2 = page1.Take(21).Concat(Enumerable.Range(1, 29).Select(i => $"second-{i}")).ToList();
        // Page 3 repeats 30 already-visible avatars and adds 20 new ones.
        var page3 = page1.Take(10)
            .Concat(Enumerable.Range(1, 20).Select(i => $"second-{i}"))
            .Concat(Enumerable.Range(1, 20).Select(i => $"third-{i}"))
            .ToList();

        var handler = new ToggleHandler { Html = false, Json = IdsJson(page1) };
        TestHooks.UseCommunityHandler(handler);
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.Equal(50, vm.GlobalResults.Count);

            handler.Json = IdsJson(page2);
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            Assert.Contains("avtr.icu 29", vm.CommunityStatusText);
            Assert.Contains("page 2", vm.CommunityStatusText);
            Assert.Equal(79, vm.GlobalResults.Count);

            handler.Json = IdsJson(page3);
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            // Each page contributes only its new avatars, and the suffix keeps up.
            Assert.Contains("avtr.icu 20", vm.CommunityStatusText);
            Assert.Contains("page 3", vm.CommunityStatusText);
            Assert.Equal(99, vm.GlobalResults.Count);
            Assert.Contains("99", vm.GlobalSummaryText);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task Browse_NextTwiceThenPrevious_StepsBackThroughLoadedPages()
    {
        var page1 = Ids(50, "bulk");
        var page2 = page1.Take(21).Concat(Ids(29, "second")).ToList();
        var page3 = page1.Take(10).Concat(Ids(20, "second")).Concat(Ids(20, "third")).ToList();

        var handler = new ToggleHandler { Html = false, Json = IdsJson(page1) };
        TestHooks.UseCommunityHandler(handler);
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.False(vm.HasPreviousGlobal);

            handler.Json = IdsJson(page2);
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            handler.Json = IdsJson(page3);
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            Assert.True(vm.HasPreviousGlobal);
            Assert.Contains("avtr.icu 20", vm.CommunityStatusText);
            Assert.Contains("page 3", vm.CommunityStatusText);
            Assert.Equal(99, vm.GlobalResults.Count);
            var requestsAfterLoad = handler.Requests;
            var tokenAtPage3 = vm.ResultsPageToken;

            // Previous shows the already-loaded page 2 without fetching again.
            vm.PreviousGlobalPageCommand.Execute(null);
            Assert.Contains("avtr.icu 29", vm.CommunityStatusText);
            Assert.Contains("page 2", vm.CommunityStatusText);
            Assert.Equal(79, vm.GlobalResults.Count);
            Assert.Equal(requestsAfterLoad, handler.Requests);
            Assert.True(vm.ResultsPageToken > tokenAtPage3);

            // Back on page 1, Previous is no longer offered.
            vm.PreviousGlobalPageCommand.Execute(null);
            Assert.Contains("avtr.icu 50", vm.CommunityStatusText);
            Assert.DoesNotContain("page 2", vm.CommunityStatusText);
            Assert.Equal(50, vm.GlobalResults.Count);
            Assert.False(vm.HasPreviousGlobal);

            // Next forward again comes from the cache, not the index.
            var tokenAtPage1 = vm.ResultsPageToken;
            vm.NextGlobalPageCommand.Execute(null);
            Assert.Contains("page 2", vm.CommunityStatusText);
            Assert.Equal(79, vm.GlobalResults.Count);
            Assert.Equal(requestsAfterLoad, handler.Requests);

            // Every page change bumps the token the view scrolls on, browse or search.
            Assert.True(vm.ResultsPageToken > tokenAtPage1);

            // Search-result paging (cache restore) bumps the same token.
            var beforeSearch = vm.ResultsPageToken;
            vm.GlobalSearchTerm = "cat";
            TestHooks.SeedSearchCache(vm, new AvatarViewModel.CachedSearch
            {
                Pages = new List<List<Avatar>> { page1.Take(3).Select(id => new Avatar { Id = $"avtr_{id}", Name = id }).ToList() },
                FetchedAt = DateTime.UtcNow
            });
            vm.GlobalSearchCommand.Execute(null);
            Assert.True(vm.ResultsPageToken > beforeSearch);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task RefreshDuringActiveSearch_LeavesSearchStatusAlone()
    {
        var handler = new ToggleHandler { Html = false };
        TestHooks.UseCommunityHandler(handler);
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());

            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            var browseStatus = vm.CommunityStatusText;
            Assert.Contains("avtr.icu 2", browseStatus);

            // A search query owns the tab: a background browse refresh must not
            // rewrite the visible status, even when the index is failing.
            handler.Html = true;
            vm.GlobalSearchTerm = "cat";
            vm.RefreshBrowseCommand.Execute(null);
            await WaitUntilIdle(vm);
            Assert.Equal(browseStatus, vm.CommunityStatusText);

            // With no query owning the tab the same failure is reported.
            vm.GlobalSearchTerm = string.Empty;
            vm.RefreshBrowseCommand.Execute(null);
            await WaitUntilIdle(vm);
            Assert.Contains("avtr.icu failed", vm.CommunityStatusText);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public void CachedPage_RestoredThroughSearch_ShowsCountsWithoutStaleFailure()
    {
        var vm = new AvatarViewModel(new VrcApiClient());

        // Seed the cache exactly as a completed search would have stored it:
        // one page, with PAW recorded as failed at fetch time.
        var page = new List<Avatar>
        {
            new() { Id = "avtr_cached-1", Name = "Cached One", SourceName = "AviSeek" },
            new() { Id = "avtr_cached-2", Name = "Cached Two", SourceName = "VRChat" }
        };
        var probe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["AviSeek"] = 1,
            ["VRChat"] = 1,
            ["PAW"] = -1
        };

        var cached = new AvatarViewModel.CachedSearch
        {
            Pages = new List<List<Avatar>> { page },
            FetchedAt = DateTime.UtcNow,
            PageProbes = new List<Dictionary<string, int>> { probe }
        };

        vm.GlobalSearchTerm = "cat";
        TestHooks.SeedSearchCache(vm, cached);
        vm.GlobalSearchCommand.Execute(null);

        Assert.Contains("AviSeek 1", vm.CommunityStatusText);
        Assert.Contains("VRChat 1", vm.CommunityStatusText);
        Assert.DoesNotContain("PAW", vm.CommunityStatusText);
        Assert.DoesNotContain("failed", vm.CommunityStatusText);
    }

}
