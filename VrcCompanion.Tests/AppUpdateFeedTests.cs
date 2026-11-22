using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The routine update check reads the feed vpk writes instead of listing every
/// release through the GitHub API, so these pin down what it reports.
/// </summary>
public sealed class AppUpdateFeedTests
{
    private const string Feed = """
    {"Assets":[
    {"PackageId":"KawaiiVrc","Version":"1.0.47","Type":"Full","FileName":"KawaiiVrc-1.0.47-full.nupkg","NotesMarkdown":"## KawaiiVrc 1.0.47\r\n\r\n### Fixes\r\n\r\n- **Faster update checks.** The app reads one small feed instead of listing every release.","NotesHTML":"<h2>old</h2>"},
    {"PackageId":"KawaiiVrc","Version":"1.0.47","Type":"Delta","FileName":"KawaiiVrc-1.0.47-delta.nupkg","NotesMarkdown":""}
    ]}
    """;

    [Fact]
    public void ANewerVersionIsAvailableWithItsNotes()
    {
        var result = AppUpdateService.InterpretFeed(Feed, "1.0.46");

        Assert.NotNull(result);
        Assert.Equal(AppUpdateCheckState.Available, result!.State);
        Assert.Equal("1.0.47", result.Version);
        Assert.Contains("Faster update checks.", result.Notes);
        Assert.DoesNotContain("##", result.Notes);
        Assert.DoesNotContain("**", result.Notes);
    }

    [Fact]
    public void TheRunningVersionIsUpToDate()
    {
        var result = AppUpdateService.InterpretFeed(Feed, "1.0.47");

        Assert.NotNull(result);
        Assert.Equal(AppUpdateCheckState.UpToDate, result!.State);
        Assert.Null(result.Version);
    }

    [Fact]
    public void ANewerInstalledBuildIsNotTreatedAsOutOfDate()
    {
        var result = AppUpdateService.InterpretFeed(Feed, "1.1.0");

        Assert.Equal(AppUpdateCheckState.UpToDate, result!.State);
    }

    [Fact]
    public void TheHighestVersionWinsAcrossAssets()
    {
        const string mixed = """
        {"Assets":[
        {"Version":"1.0.48","NotesMarkdown":"latest"},
        {"Version":"1.0.47","NotesMarkdown":"older"},
        {"Version":"not-a-version","NotesMarkdown":"ignored"}
        ]}
        """;

        var result = AppUpdateService.InterpretFeed(mixed, "1.0.46");

        Assert.Equal("1.0.48", result!.Version);
        Assert.Equal("latest", result.Notes);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Assets\":[]}")]
    [InlineData("{\"Something\":1}")]
    public void AFeedItCannotReadIsReportedAsUnknown(string json)
    {
        Assert.Null(AppUpdateService.InterpretFeed(json, "1.0.46"));
    }
}
