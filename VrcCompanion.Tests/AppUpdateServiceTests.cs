using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class AppUpdateServiceTests
{
    [Fact]
    public async Task DevBuildsReportUnsupportedInsteadOfThrowing()
    {
        // The test host is not a Velopack install, which is exactly the dev/portable case.
        var service = new AppUpdateService("https://github.com/Richy-Chu/KawaiiVrc");

        Assert.False(service.IsSupported);

        var result = await service.CheckAsync();

        Assert.Equal(AppUpdateCheckState.Unsupported, result.State);
        Assert.False(await service.DownloadAsync(null, CancellationToken.None));
        Assert.False(service.ApplyAndRestart());
    }

    [Fact]
    public void ReleaseNotesAreRenderedAsPlainText()
    {
        var notes = ReleaseNotes.ToPlainText(
            "## What's new\n- **Fixed** the [timeline](https://example.com)\n- Added paging\n\nThanks!",
            null);

        Assert.Contains("What's new", notes);
        Assert.Contains("• Fixed the timeline", notes);
        Assert.Contains("• Added paging", notes);
        Assert.DoesNotContain("**", notes);
        Assert.DoesNotContain("https://example.com", notes);
    }

    [Fact]
    public void ReleaseNotesFallBackToHtml()
    {
        var notes = ReleaseNotes.ToPlainText(null, "<p>Fixes &amp; tweaks</p><ul><li>Paging</li></ul>");

        Assert.Contains("Fixes & tweaks", notes);
        Assert.Contains("Paging", notes);
        Assert.DoesNotContain("<p>", notes);
    }
}
