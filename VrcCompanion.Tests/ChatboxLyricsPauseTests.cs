using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class ChatboxProviderEdgeCaseTests
{
    [Fact]
    public void StatusIconCanBeDisabledWithoutChangingTheSavedStatus()
    {
        var provider = new StatusChatboxProvider();
        var context = new ChatboxProviderContext
        {
            IncludeStatus = true,
            ActiveStatus = "back in 5",
            ShowStatusIcon = false,
            StatusIcon = "💭"
        };

        Assert.Equal("back in 5", provider.Build(context));
        Assert.Equal(
            "💭 back in 5",
            provider.Build(context with { ShowStatusIcon = true }));
    }

    [Fact]
    public void LyricsProviderOnlyAddsALineWhenOneIsDisplaying()
    {
        var provider = new LyricsChatboxProvider();
        var context = new ChatboxProviderContext
        {
            IncludeLyrics = true,
            Lyric = string.Empty,
            LyricsFormat = "♪ {lyric}"
        };

        Assert.Null(provider.Build(context));
        Assert.Equal(
            "♪ 世界中の誰よりきっと",
            provider.Build(context with { Lyric = "世界中の誰よりきっと" }));
    }

    [Fact]
    public void WindowProviderUsesTheDesktopHeadingAndFormatsTheFocusedApp()
    {
        var provider = new WindowChatboxProvider();
        var context = new ChatboxProviderContext
        {
            IncludeWindow = true,
            IsVR = false,
            Window = new WindowActivityInfo("Blender", "scene.blend"),
            WindowFormat = "{heading} · {app}"
        };

        Assert.Equal("On desktop · Blender", provider.Build(context));
    }

}
