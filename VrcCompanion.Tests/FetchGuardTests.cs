using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// Feeds a real captured HTML body through the fetch methods themselves (not the
/// parse helper) and checks the status line names indexes that failed.
/// </summary>
[Collection("CommunityHandler")]
public class FetchGuardTests
{
    private sealed class HtmlHandler : HttpMessageHandler
    {
        private readonly string _body;

        public HtmlHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_body, Encoding.UTF8, "text/html")
            });
    }

    private static string CapturedHtml() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "avtrdb-html-response.html"));

    [Fact]
    public async Task VrcndbFetch_HtmlBody_ReturnsNothingAndMarksIndexFailed()
    {
        var html = CapturedHtml();
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);

        TestHooks.UseCommunityHandler(new HtmlHandler(html));
        try
        {
            var api = new VrcApiClient();
            var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var rows = await api.FetchVrcndbAsync("cat", 40, 1, CancellationToken.None, results);

            Assert.Empty(rows);
            Assert.True(results.TryGetValue("VRCNDb", out var outcome));
            Assert.Equal(-1, outcome);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task AvtrIcuFetch_HtmlBody_ReturnsNothingAndMarksIndexFailed()
    {
        var html = CapturedHtml();

        TestHooks.UseCommunityHandler(new HtmlHandler(html));
        try
        {
            var api = new VrcApiClient();
            var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var rows = await api.FetchCommunityAsync(
                "search?search=cat&limit=25",
                CancellationToken.None,
                results);

            Assert.Empty(rows);
            Assert.True(results.TryGetValue("avtr.icu", out var outcome));
            Assert.Equal(-1, outcome);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task BrowseFetch_HtmlBody_ReturnsNothingAndMarksAvtrIcuFailed()
    {
        // Browse (the /latest call) must report the failing index too, not show "0".
        TestHooks.UseCommunityHandler(new HtmlHandler(CapturedHtml()));
        try
        {
            var api = new VrcApiClient();
            var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var rows = await api.GetCommunityLatestAsync(50, 0, results);

            Assert.Empty(rows);
            Assert.True(results.TryGetValue("avtr.icu", out var outcome));
            Assert.Equal(-1, outcome);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public async Task VrcxProviderFetch_HtmlBody_ReturnsNothingAndMarksIndexFailed()
    {
        var html = CapturedHtml();

        TestHooks.UseCommunityHandler(new HtmlHandler(html));
        try
        {
            var api = new VrcApiClient();
            var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var rows = await api.FetchVrcxProviderAsync(
                "PAW",
                "https://paw-api.amelia.fun/vrcx_search?search=cat&n=40",
                40,
                CancellationToken.None,
                results);

            Assert.Empty(rows);
            Assert.True(results.TryGetValue("PAW", out var outcome));
            Assert.Equal(-1, outcome);
        }
        finally
        {
            TestHooks.UseCommunityHandler(null);
        }
    }

    [Fact]
    public void StatusLine_NamesFailedIndexesInsteadOfDroppingThem()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["AviSeek"] = 12,
            ["VRChat"] = 3
        };

        var text = AvatarViewModel.FormatIndexStatus(counts, new[] { "PAW", "VRCNDb" }, 4);

        Assert.Contains("AviSeek 12", text);
        Assert.Contains("VRChat 3", text);
        Assert.Contains("PAW failed", text);
        Assert.Contains("VRCNDb failed", text);
        Assert.Contains("page 4", text);
    }

    [Fact]
    public void CachedProbe_DropsFailuresSoAStaleFailureIsNeverShown()
    {
        var stored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["AviSeek"] = 0,
            ["PAW"] = -1,
            ["VRCNDb"] = -1
        };

        var restored = AvatarViewModel.HealthOnlyProbe(stored);

        Assert.False(restored.ContainsKey("PAW"));
        Assert.False(restored.ContainsKey("VRCNDb"));
        Assert.Equal(0, restored["AviSeek"]);

        var text = AvatarViewModel.FormatIndexStatus(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["AviSeek"] = 12 },
            restored.Where(entry => entry.Value < 0).Select(entry => entry.Key),
            1);
        Assert.DoesNotContain("failed", text);
        Assert.Contains("AviSeek 12", text);
    }

    [Fact]
    public void FailedBrowse_IsNotTreatedAsLoadedSoItRetriesAndClears()
    {
        Assert.False(AvatarViewModel.BrowseFetchSucceeded(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["avtr.icu"] = -1 }));

        Assert.True(AvatarViewModel.BrowseFetchSucceeded(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["avtr.icu"] = 50 }));

        Assert.True(AvatarViewModel.BrowseFetchSucceeded(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["avtr.icu"] = 0 }));

        Assert.True(AvatarViewModel.BrowseFetchSucceeded(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void BrowseRefreshDuringSearch_DoesNotTakeOverTheStatusLine()
    {
        // RefreshBrowse passes publish:false while a query is active, so browse
        // numbers (or "avtr.icu failed") must never replace the search status.
        Assert.False(AvatarViewModel.ShouldReportBrowseIndexStatus("cat", publish: false));
        Assert.False(AvatarViewModel.ShouldReportBrowseIndexStatus("cat", publish: true));

        Assert.True(AvatarViewModel.ShouldReportBrowseIndexStatus(string.Empty, publish: true));
        Assert.True(AvatarViewModel.ShouldReportBrowseIndexStatus("   ", publish: true));
        Assert.False(AvatarViewModel.ShouldReportBrowseIndexStatus(string.Empty, publish: false));
    }

    [Fact]
    public void StatusLine_IsEmptyWhenNothingWasQueried()
    {
        Assert.Equal(
            string.Empty,
            AvatarViewModel.FormatIndexStatus(
                new Dictionary<string, int>(),
                Array.Empty<string>(),
                1));
    }
}
