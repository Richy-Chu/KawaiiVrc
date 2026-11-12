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
            // VRCNDb answers with {results:[...]}; every other index with a bare array.
            var isVrcndb = request.RequestUri?.Host.Contains("vrcnext", StringComparison.OrdinalIgnoreCase) == true;
            var body = Html
                ? CapturedHtml
                : isVrcndb ? "{\"results\":" + Json + "}" : Json;
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
    public async Task FailedBrowse_BacksOffInsteadOfRetryingOnEveryTabActivation()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = true };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            Assert.Contains("avtr.icu failed", vm.CommunityStatusText);
            Assert.Equal(1, handler.Requests);

            // Inside the 30 s cooldown, opening the tab again must not hit the index.
            vm.BrowseCatalog();
            vm.BrowseCatalog();
            Assert.Equal(1, handler.Requests);

            // Once the cooldown passes, the next activation retries exactly once.
            now = now.AddSeconds(31);
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.Equal(2, handler.Requests);
            Assert.Contains("avtr.icu failed", vm.CommunityStatusText);

            // A second failure doubles the wait: 31 s is no longer enough, 61 s is.
            now = now.AddSeconds(31);
            vm.BrowseCatalog();
            Assert.Equal(2, handler.Requests);

            now = now.AddSeconds(30);
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.Equal(3, handler.Requests);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task FailedBrowse_StatusShowsRemainingCooldown()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = true };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            Assert.Contains("avtr.icu failed", vm.CommunityStatusText);
            Assert.Contains("retrying in 30s", vm.CommunityStatusText);

            // The status reads the clock, so it counts down instead of freezing.
            now = now.AddSeconds(18);
            Assert.Contains("retrying in 12s", vm.CommunityStatusText);
            Assert.DoesNotContain("retrying in 30s", vm.CommunityStatusText);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task FailedBrowse_CooldownStopsDoublingAtFiveMinuteCap()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = true };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            // 30, 60, 120, 240, then 300 twice: the cap, not 480.
            foreach (var seconds in new[] { 30, 60, 120, 240, 300, 300 })
            {
                Assert.Contains($"retrying in {seconds}s", vm.CommunityStatusText);

                // One second short of the window: still cooling down, no request.
                var before = handler.Requests;
                now = now.AddSeconds(seconds - 1);
                vm.BrowseCatalog();
                Assert.Equal(before, handler.Requests);

                // At the deadline the next activation retries and fails again.
                now = now.AddSeconds(1);
                vm.BrowseCatalog();
                await WaitUntilIdle(vm);
                Assert.Equal(before + 1, handler.Requests);
            }

            // A seventh failure would have been 480 s without the cap.
            Assert.Contains("retrying in 300s", vm.CommunityStatusText);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task FailedBrowse_RetriesItselfWhenCooldownExpires()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = true };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.Equal(1, handler.Requests);
            Assert.Contains("retrying in 30s", vm.CommunityStatusText);

            // No tab switch: the countdown reaching zero is what triggers the retry.
            now = now.AddSeconds(30);
            TestHooks.PumpBrowseBackoff(vm);
            await WaitUntilIdle(vm);

            Assert.Equal(2, handler.Requests);
            Assert.Contains("avtr.icu failed", vm.CommunityStatusText);
            Assert.Contains("retrying in 60s", vm.CommunityStatusText);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task FailedBrowse_CooldownExpiryRetryRestoresTheGrid()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = true };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.Empty(vm.GlobalResults);

            handler.Html = false;
            now = now.AddSeconds(30);
            TestHooks.PumpBrowseBackoff(vm);
            await WaitUntilIdle(vm);

            Assert.Equal(2, vm.GlobalResults.Count);
            Assert.Contains("avtr.icu 2", vm.CommunityStatusText);
            Assert.DoesNotContain("retrying", vm.CommunityStatusText);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task FailedBrowse_CooldownExpiryWaitsWhileSearchOwnsTheTab()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = true };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.Equal(1, handler.Requests);

            // Search owns the tab: a browse retry would replace its results.
            vm.GlobalSearchTerm = "cat";
            now = now.AddSeconds(30);
            TestHooks.PumpBrowseBackoff(vm);
            await WaitUntilIdle(vm);
            Assert.Equal(1, handler.Requests);

            // Once the search is cleared the next tick catches up and retries.
            vm.GlobalSearchTerm = string.Empty;
            TestHooks.PumpBrowseBackoff(vm);
            await WaitUntilIdle(vm);
            Assert.Equal(2, handler.Requests);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task LoadMore_FailureRespectsCooldownInsteadOfRetryingEveryClick()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = false, Json = IdsJson(Ids(50, "bulk")) };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            handler.Html = true;
            var afterFirstPage = handler.Requests;
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            Assert.Equal(afterFirstPage + 1, handler.Requests);
            Assert.Contains("avtr.icu failed", vm.CommunityStatusText);
            Assert.Contains("retrying in 30s", vm.CommunityStatusText);

            // Clicks inside the cooldown explain the wait instead of refetching.
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            Assert.Equal(afterFirstPage + 1, handler.Requests);
            Assert.Contains("cooling down", vm.GlobalError);
            Assert.DoesNotContain("page 2", vm.CommunityStatusText);

            // After the window the next click fetches and advances normally.
            handler.Html = false;
            handler.Json = IdsJson(Ids(50, "more"));
            now = now.AddSeconds(31);
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            Assert.Equal(afterFirstPage + 2, handler.Requests);
            Assert.Contains("page 2", vm.CommunityStatusText);
            Assert.DoesNotContain("cooling down", vm.GlobalError);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task LoadMore_RetriesItsFailedPageWhenCooldownExpires()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = false, Json = IdsJson(Ids(50, "bulk")) };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            handler.Html = true;
            var afterPageOne = handler.Requests;
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);
            Assert.Equal(afterPageOne + 1, handler.Requests);
            Assert.Contains("retrying in 30s", vm.CommunityStatusText);

            // The expiry tick must fetch page 2 itself, with no click.
            handler.Html = false;
            handler.Json = IdsJson(Ids(50, "more"));
            now = now.AddSeconds(30);
            TestHooks.PumpBrowseBackoff(vm);
            await WaitUntilIdle(vm);

            Assert.Equal(afterPageOne + 2, handler.Requests);
            Assert.Contains("page 2", vm.CommunityStatusText);
            Assert.DoesNotContain("retrying", vm.CommunityStatusText);
            Assert.Equal(string.Empty, vm.GlobalError);
            // 50 first-page rows plus the appended page, not a restart.
            Assert.Equal(100, vm.GlobalResults.Count);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task Browse_AutoRetryShowsGenericLabelWhileFetching()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = true };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.Contains("retrying in 30s", vm.CommunityStatusText);

            // Hold the page-1 retry open: with no pages loaded there is no page
            // number to name, so the label must stay the generic one.
            var pending = new TaskCompletionSource<List<Avatar>>(TaskCreationOptions.RunContinuationsAsynchronously);
            TestHooks.UseBrowseFetch(vm, (_, _) => pending.Task);
            now = now.AddSeconds(30);
            TestHooks.PumpBrowseBackoff(vm);

            Assert.True(vm.IsGlobalBusy);
            Assert.Contains("retrying now", vm.CommunityStatusText);
            Assert.DoesNotContain("retrying page", vm.CommunityStatusText);
            Assert.DoesNotContain("reloading page", vm.CommunityStatusText);

            pending.SetResult(Enumerable.Range(1, 50)
                .Select(i => new Avatar { Id = $"avtr_retry-{i}", Name = $"retry-{i}", SourceName = "avtr.icu" })
                .ToList());
            await WaitUntilIdle(vm);

            Assert.Contains("avtr.icu 50", vm.CommunityStatusText);
            Assert.DoesNotContain("retrying now", vm.CommunityStatusText);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task LoadMore_AutoRetryShowsInProgressStateWhileFetching()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = false, Json = IdsJson(Ids(50, "bulk")) };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            handler.Html = true;
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);
            Assert.Contains("retrying in 30s", vm.CommunityStatusText);

            // Hold the retry open so the in-progress status can be observed.
            var pending = new TaskCompletionSource<List<Avatar>>(TaskCreationOptions.RunContinuationsAsynchronously);
            TestHooks.UseBrowseFetch(vm, (_, _) => pending.Task);
            now = now.AddSeconds(30);
            TestHooks.PumpBrowseBackoff(vm);

            Assert.True(vm.IsGlobalBusy);
            Assert.Contains("retrying page 2", vm.CommunityStatusText);
            Assert.DoesNotContain("retrying now", vm.CommunityStatusText);
            Assert.DoesNotContain("retrying in", vm.CommunityStatusText);

            pending.SetResult(Enumerable.Range(1, 50)
                .Select(i => new Avatar { Id = $"avtr_more-{i}", Name = $"more-{i}", SourceName = "avtr.icu" })
                .ToList());
            await WaitUntilIdle(vm);

            Assert.Contains("page 2", vm.CommunityStatusText);
            Assert.DoesNotContain("retrying now", vm.CommunityStatusText);
            Assert.DoesNotContain("retrying page 2", vm.CommunityStatusText);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task LoadMore_AutoRetryNamesThePageAfterTwoLoadedPages()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = false, Json = IdsJson(Ids(50, "bulk")) };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            handler.Json = IdsJson(Ids(50, "more"));
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);
            Assert.Contains("page 2", vm.CommunityStatusText);

            // Page 3 is the one that fails, so its retry must name page 3.
            handler.Html = true;
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);
            Assert.Contains("retrying in 30s", vm.CommunityStatusText);

            var pending = new TaskCompletionSource<List<Avatar>>(TaskCreationOptions.RunContinuationsAsynchronously);
            TestHooks.UseBrowseFetch(vm, (_, _) => pending.Task);
            now = now.AddSeconds(30);
            TestHooks.PumpBrowseBackoff(vm);

            Assert.Contains("retrying page 3", vm.CommunityStatusText);
            Assert.DoesNotContain("retrying page 2", vm.CommunityStatusText);
            Assert.DoesNotContain("retrying now", vm.CommunityStatusText);

            pending.SetResult(Enumerable.Range(1, 50)
                .Select(i => new Avatar { Id = $"avtr_third-{i}", Name = $"third-{i}", SourceName = "avtr.icu" })
                .ToList());
            await WaitUntilIdle(vm);

            Assert.Contains("page 3", vm.CommunityStatusText);
            Assert.DoesNotContain("retrying page 3", vm.CommunityStatusText);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task LoadMore_AutoRetryFallsBackToFirstPageAfterCap()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = false, Json = IdsJson(Ids(50, "bulk")) };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            // Every later page fails; the offsets show what is being retried.
            var offsets = new List<int>();
            TestHooks.UseBrowseFetch(vm, (offset, _) =>
            {
                offsets.Add(offset);
                throw new HttpRequestException("still down");
            });

            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);
            Assert.Equal(new[] { 50 }, offsets);

            // Three automatic attempts at page 2, each after the cooldown.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                now = now.AddSeconds(600);
                TestHooks.PumpBrowseBackoff(vm);
                await WaitUntilIdle(vm);
            }

            Assert.Equal(new[] { 50, 50, 50, 50 }, offsets);

            // The fourth expiry gives up on that page and asks for offset 0.
            // Hold that reload open so its dedicated status can be observed.
            var pending = new TaskCompletionSource<List<Avatar>>(TaskCreationOptions.RunContinuationsAsynchronously);
            TestHooks.UseBrowseFetch(vm, (offset, _) =>
            {
                offsets.Add(offset);
                return pending.Task;
            });
            now = now.AddSeconds(600);
            TestHooks.PumpBrowseBackoff(vm);

            Assert.True(vm.IsGlobalBusy);
            Assert.Contains("reloading page 1", vm.CommunityStatusText);
            Assert.DoesNotContain("retrying now", vm.CommunityStatusText);

            pending.SetException(new HttpRequestException("still down"));
            await WaitUntilIdle(vm);
            Assert.Equal(new[] { 50, 50, 50, 50, 0 }, offsets);
            Assert.DoesNotContain("reloading page 1", vm.CommunityStatusText);

            // When page 1 answers again it replaces the grid and drops the page suffix.
            TestHooks.UseBrowseFetch(vm, null);
            handler.Json = IdsJson(Ids(50, "fresh"));
            now = now.AddSeconds(600);
            TestHooks.PumpBrowseBackoff(vm);
            await WaitUntilIdle(vm);

            Assert.Equal(50, vm.GlobalResults.Count);
            Assert.Contains("avtr.icu 50", vm.CommunityStatusText);
            Assert.DoesNotContain("page 2", vm.CommunityStatusText);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task FailedBrowse_ExplicitRefreshBypassesBackoffAndSuccessClearsIt()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = true };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);
            Assert.Equal(1, handler.Requests);

            // An explicit refresh is the user asking: it ignores the cooldown.
            vm.RefreshBrowseCommand.Execute(null);
            await WaitUntilIdle(vm);
            Assert.Equal(2, handler.Requests);

            handler.Html = false;
            vm.RefreshBrowseCommand.Execute(null);
            await WaitUntilIdle(vm);
            Assert.Contains("avtr.icu 2", vm.CommunityStatusText);

            // Recovery cleared the backoff, so a tab activation serves the cache.
            vm.BrowseCatalog();
            Assert.Equal(3, handler.Requests);
            Assert.Contains("avtr.icu 2", vm.CommunityStatusText);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task Browse_ThrowingFetch_ReportsGlobalErrorAndRetriesAfterCooldown()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = false };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            TestHooks.UseBrowseFetch(vm, (_, _) => throw new HttpRequestException("browse blew up"));

            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            Assert.Equal("Could not reach the community avatar index.", vm.GlobalError);
            // No index answered, so the status is only the cooldown countdown.
            Assert.Equal("retrying in 30s", vm.CommunityStatusText);

            // A throw must not mark browse as loaded, but it also starts the
            // backoff, so the retry only happens once the cooldown has passed.
            TestHooks.UseBrowseFetch(vm, null);
            now = now.AddSeconds(31);
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            Assert.Contains("avtr.icu 2", vm.CommunityStatusText);
            Assert.Equal(string.Empty, vm.GlobalError);
        }
        finally
        {
            TestHooks.ResetClock();
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task LoadMore_ThrowingFetch_ReportsGlobalErrorAndKeepsPage()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ToggleHandler { Html = false, Json = IdsJson(Ids(50, "bulk")) };
        TestHooks.UseCommunityHandler(handler);
        TestHooks.UtcNow = () => now;
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

            // The next attempt with a working index does advance and clears the
            // error, but only once the cooldown it started has passed.
            handler.Json = IdsJson(Ids(50, "more"));
            TestHooks.UseBrowseFetch(vm, null);
            now = now.AddSeconds(31);
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            Assert.Contains("page 2", vm.CommunityStatusText);
            Assert.Equal(string.Empty, vm.GlobalError);
        }
        finally
        {
            TestHooks.ResetClock();
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
    public async Task FederatedSearch_ConcurrentProviderWrites_KeepEveryIndexResult()
    {
        // Six indexes answer concurrently through one results map. Plain Dictionary
        // writes would race here; the probe must end complete and consistent.
        var handler = new ToggleHandler { Html = false };
        TestHooks.UseCommunityHandler(handler);
        try
        {
            var api = new VrcApiClient();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var probe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                var rows = await api.SearchAllCommunityAvatarsAsync("cat", indexResults: probe);

                Assert.NotEmpty(rows);
                Assert.Equal(6, probe.Count); // four VRCX providers + avtr.icu + VRCNDb
                Assert.All(probe.Values, value => Assert.True(value >= 0));
            }
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task Browse_PrivateFilteredPaging_StatusMatchesVisibleCardsPerPage()
    {
        static string Row(string id, bool isPrivate) =>
            $"{{\"id\":\"avtr_{id}\",\"name\":\"{id}\",\"releaseStatus\":\"{(isPrivate ? "private" : "public")}\"}}";

        // Page 1: 50 rows, the last two private, so the grid shows 48.
        var page1 = Enumerable.Range(1, 50)
            .Select(i => Row($"a-{i}", i > 48))
            .ToList();
        // Page 2: 20 repeats, 28 new public, 2 new private: 30 new rows, 28 visible.
        var page2 = Enumerable.Range(1, 20).Select(i => Row($"a-{i}", false))
            .Concat(Enumerable.Range(51, 28).Select(i => Row($"a-{i}", false)))
            .Concat(Enumerable.Range(1, 2).Select(i => Row($"x-{i}", true)))
            .ToList();

        // These rows are already JSON objects, so join them without re-wrapping ids.
        var handler = new ToggleHandler
        {
            Html = false,
            Json = "[" + string.Join(",", page1) + "]"
        };
        TestHooks.UseCommunityHandler(handler);
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            Assert.Equal(48, vm.GlobalResults.Count);
            Assert.Contains("avtr.icu 48", vm.CommunityStatusText);

            handler.Json = "[" + string.Join(",", page2) + "]";
            vm.NextGlobalPageCommand.Execute(null);
            await WaitUntilIdle(vm);

            // 30 new rows arrived but only 28 are public, and the total is visible-only.
            Assert.Equal(76, vm.GlobalResults.Count);
            Assert.Contains("avtr.icu 28", vm.CommunityStatusText);
            Assert.Contains("page 2", vm.CommunityStatusText);

            var cardsOnPage = vm.GlobalResults.Count - 48;
            Assert.Equal(28, cardsOnPage);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task Browse_StatusCountMatchesGridWhenPrivateRowsAreFiltered()
    {
        // Public-only is the default, so one of these three rows never reaches the grid.
        var handler = new ToggleHandler
        {
            Html = false,
            Json = """
                [
                  {"id":"avtr_pub-1","name":"Public One","releaseStatus":"public"},
                  {"id":"avtr_pub-2","name":"Public Two","releaseStatus":"public"},
                  {"id":"avtr_priv-1","name":"Private One","releaseStatus":"private"}
                ]
                """
        };
        TestHooks.UseCommunityHandler(handler);
        try
        {
            var vm = new AvatarViewModel(new VrcApiClient());
            vm.BrowseCatalog();
            await WaitUntilIdle(vm);

            Assert.Equal(2, vm.GlobalResults.Count);
            Assert.Contains("avtr.icu 2", vm.CommunityStatusText);
            Assert.DoesNotContain("avtr.icu 3", vm.CommunityStatusText);
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
