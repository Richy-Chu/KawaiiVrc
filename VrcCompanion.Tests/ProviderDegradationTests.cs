using System.IO;
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
    public void VrcdbAvatarNameFieldIsUsed()
    {
        const string body =
            "[{\"id\":\"avtr_fb13f3f0-eb9e-469f-9701-659f6a9b78e0\",\"avatarName\":\"test 1\"," +
            "\"authorId\":\"usr_5f00a689-3016-445c-b8f0-069e82ebcdc1\",\"releaseStatus\":\"public\"}]";

        var rows = VrcApiClient.ParseVrcxProviderBody(body, "VRCDB", 80);

        var avatar = Assert.Single(rows);
        Assert.Equal("test 1", avatar.Name);
        Assert.Equal("avtr_fb13f3f0-eb9e-469f-9701-659f6a9b78e0", avatar.Id);
    }

    [Fact]
    public void RealHtmlProviderResponse_ParsesToEmptyList()
    {
        // Captured live from avtrdb.com's API path: HTTP 200 with text/html and an
        // SPA shell. This is what a non-JSON provider answer really looks like.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "avtrdb-html-response.html");
        var body = File.ReadAllText(path);

        Assert.Contains("<html", body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(VrcApiClient.ParseVrcxProviderBody(body, "avtrDB", 80));
    }

    [Fact]
    public void HtmlBody_IsRejectedByTheSharedIndexGuard()
    {
        Assert.Null(VrcApiClient.ParseJsonBodyOrNull(HtmlBody, "VRCNDb"));
        Assert.Null(VrcApiClient.ParseJsonBodyOrNull(HtmlBody, "avtr.icu"));
        Assert.Null(VrcApiClient.ParseJsonBodyOrNull(string.Empty, "VRCNDb"));

        using var valid = VrcApiClient.ParseJsonBodyOrNull("{\"results\":[]}", "VRCNDb");
        Assert.NotNull(valid);
        Assert.True(valid!.RootElement.TryGetProperty("results", out _));
    }

    [Fact]
    public void EmptyBody_ParsesToEmptyList()
    {
        Assert.Empty(VrcApiClient.ParseVrcxProviderBody(string.Empty, "PAW", 80));
        Assert.Empty(VrcApiClient.ParseVrcxProviderBody("   ", "PAW", 80));
    }
}
