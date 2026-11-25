using VrcCompanion;
using Xunit;

namespace VrcCompanion.Tests;

public class SectionCacheTests
{
    [Theory]
    // Discord is kept whenever its page is open, because evicting the view
    // disposes the WebView and reloading the page drops a voice call.
    [InlineData("discord", true, false, false, true)]
    [InlineData("discord", false, false, false, false)]
    // A browser page is kept while it is being read in focus mode or is busy.
    [InlineData("brave", false, true, false, true)]
    [InlineData("brave", false, false, true, true)]
    [InlineData("brave", false, false, false, false)]
    [InlineData("resources", false, false, true, true)]
    [InlineData("resources", false, false, false, false)]
    // No other section is pinned, so the cache limit still applies to them.
    [InlineData("friends", true, true, true, false)]
    [InlineData("worlds", true, true, true, false)]
    public void OnlyABrowserSectionIsPinnedInTheSectionCache(
        string key,
        bool discordOpen,
        bool browserFocused,
        bool browserActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ShouldKeepSectionView(key, discordOpen, browserFocused, browserActive));
    }
}
