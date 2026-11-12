using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// Providers can answer with an HTML page (error page, Cloudflare challenge, SPA
/// shell) instead of JSON. That must degrade to "this index contributed nothing"
/// rather than throwing and taking the other indexes down with it.
/// </summary>
public sealed class ProviderDegradationTests
{
    private const string HtmlBody =
        "<!doctype html><html class=\"dark\"><head><title>Just a moment...</title></head>" +
        "<body><div id=\"challenge\">Checking your browser { not json }</div></body></html>";

    [Fact]
    public void HtmlBody_ParsesToEmptyListInsteadOfThrowing()
    {
        var parsed = VrcApiClient.ParseVrcxProviderBody(HtmlBody, "VRCDB", 80);

        Assert.Empty(parsed);
    }

    [Fact]
    public void HtmlBody_LeavesOtherIndexesInTheMergedResult()
    {
        var aviseek = new List<Avatar>
        {
            new() { Id = "avtr_44444444-4444-4444-4444-444444444444", Name = "still here" }
        };

        var merged = VrcApiClient.MergeCommunityResults(new[]
        {
            ("AviSeek", (IReadOnlyList<Avatar>)aviseek),
            ("VRCDB", (IReadOnlyList<Avatar>)VrcApiClient.ParseVrcxProviderBody(HtmlBody, "VRCDB", 80))
        });

        var only = Assert.Single(merged);
        Assert.Equal("AviSeek", only.SourceName);
        Assert.Equal("still here", only.Name);
    }

    [Fact]
    public void EmptyBody_ParsesToEmptyList()
    {
        Assert.Empty(VrcApiClient.ParseVrcxProviderBody(string.Empty, "PAW", 80));
        Assert.Empty(VrcApiClient.ParseVrcxProviderBody("   ", "PAW", 80));
    }
}
