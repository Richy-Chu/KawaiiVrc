using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class NowPlayingTests
{
    [Fact]
    public void BrowserYouTubeSessionIsLabelledYouTube()
    {
        var info = NowPlayingInfo.FromSession(
            "chrome.exe",
            "Lo-fi Beats - YouTube",
            "Chill Channel");

        Assert.NotNull(info);
        Assert.Equal("YouTube", info.Source);
        Assert.Equal("Lo-fi Beats", info.Title);
        Assert.Equal("♪ Lo-fi Beats — Chill Channel · YouTube", info.ChatboxText);
    }

    [Fact]
    public void BrowserYouTubeMusicSessionIsLabelledYouTube()
    {
        var info = NowPlayingInfo.FromSession(
            "msedge.exe",
            "Night Drive | YouTube Music",
            "Synthwave");

        Assert.NotNull(info);
        Assert.Equal("YouTube", info.Source);
        Assert.Equal("Night Drive", info.Title);
    }

    [Fact]
    public void SpotifySessionIsLabelledSpotify()
    {
        var info = NowPlayingInfo.FromSession(
            "Spotify.exe",
            "Starboy",
            "The Weeknd");

        Assert.NotNull(info);
        Assert.Equal("Spotify", info.Source);
        Assert.Equal("♪ Starboy — The Weeknd · Spotify", info.ChatboxText);
    }

    [Fact]
    public void SpotifyWebPlayerInABrowserIsLabelledSpotify()
    {
        var info = NowPlayingInfo.FromSession(
            "chrome.exe",
            "One More Time • Spotify",
            "Daft Punk");

        Assert.NotNull(info);
        Assert.Equal("Spotify", info.Source);
    }

    [Fact]
    public void GenericBrowserPlayerPageDoesNotReportATrack()
    {
        Assert.Null(NowPlayingInfo.FromSession("chrome.exe", "Spotify – Web Player", null));
        Assert.Null(NowPlayingInfo.FromSession("chrome.exe", "YouTube", null));
        Assert.Null(NowPlayingInfo.FromSession("chrome.exe", null, null));
    }

    [Fact]
    public void BrowserWithoutAServiceMarkerIsLabelledByBrowserName()
    {
        var info = NowPlayingInfo.FromSession("brave.exe", "Some Song", "Artist");

        Assert.NotNull(info);
        Assert.Equal("Brave", info.Source);
        Assert.Equal("♪ Some Song — Artist · Brave", info.ChatboxText);
    }

    [Fact]
    public void OtherPlayersKeepTheirRealName()
    {
        var info = NowPlayingInfo.FromSession("foobar2000.exe", "Track", "Artist");

        Assert.NotNull(info);
        Assert.Equal("foobar2000", info.Source);
    }
}
