using VrcCompanion.Controls;
using Xunit;

namespace VrcCompanion.Tests;

public class EmbeddedBrowserSuspensionTests
{
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void HiddenPagesSuspendOnlyWhenIdleAndNotDiscord(
        bool isDiscord,
        bool hasActiveDownloads,
        bool isDocumentPlayingAudio,
        bool expected)
    {
        Assert.Equal(
            expected,
            EmbeddedBrowser.ShouldSuspendHiddenPage(
                isDiscord,
                hasActiveDownloads,
                isDocumentPlayingAudio));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void BrowserMemoryIsReleasedOnlyWhenAudioAndDownloadsAreIdle(
        bool hasActiveDownloads,
        bool isDocumentPlayingAudio,
        bool expected)
    {
        Assert.Equal(
            expected,
            EmbeddedBrowser.ShouldReleaseForMemory(
                hasActiveDownloads,
                isDocumentPlayingAudio));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void LowMemoryOnlyAppliesToIdleHiddenPages(
        bool hidden,
        bool hasActiveDownloads,
        bool isDocumentPlayingAudio,
        bool expected)
    {
        Assert.Equal(
            expected,
            EmbeddedBrowser.ShouldUseLowMemoryTarget(
                hidden,
                hasActiveDownloads,
                isDocumentPlayingAudio));
    }
}
