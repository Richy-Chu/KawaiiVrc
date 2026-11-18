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
        Assert.Equal("▶ Chill Channel — Lo-fi Beats · YouTube", info.ChatboxText);
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
        Assert.Equal("▶ The Weeknd — Starboy · Spotify", info.ChatboxText);
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
        Assert.Equal("▶ Artist — Some Song · Brave", info.ChatboxText);
    }

    [Fact]
    public void BrowserChannelSuffixIsRemovedFromArtistAndSong()
    {
        var info = NowPlayingInfo.FromSession(
            "chrome.exe",
            "Zara Larsson - Ain't My Fault (Official Video) - ZaraLarssonMusicVEVO",
            null);

        Assert.NotNull(info);
        Assert.Equal("Zara Larsson", info.Artist);
        Assert.Equal("Ain't My Fault", info.Title);
        Assert.Equal("Zara Larsson — Ain't My Fault", info.DisplayText);
        Assert.DoesNotContain("VEVO", info.DisplayText);
        Assert.DoesNotContain("Official", info.DisplayText);
    }

    [Fact]
    public void FreebuffBrowserIsNotShownAsASourceName()
    {
        var info = NowPlayingInfo.FromSession(
            "com.freebuff.desktop",
            "Zara Larsson - Ain't My Fault (Official Video) - ZaraLarssonMusicVEVO",
            null);

        Assert.NotNull(info);
        Assert.Equal("Browser", info.Source);
        Assert.DoesNotContain("freebuff", info.ChatboxText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Zara Larsson - Ain't My Fault (Official Video) - ZaraLarssonMusicVEVO")]
    [InlineData("Ain't My Fault - Zara Larsson")]
    [InlineData("Ain't My Fault - ZaraLarssonMusicVEVO")]
    [InlineData("Ain't My Fault - Zara Larsson - ZaraLarssonMusicVEVO")]
    public void SuppliedArtistIsNotRepeatedAndChannelsAreDropped(string title)
    {
        var info = NowPlayingInfo.FromSession("chrome.exe", title, "Zara Larsson");

        Assert.NotNull(info);
        Assert.Equal("Zara Larsson", info.Artist);
        Assert.Equal("Ain't My Fault", info.Title);
        Assert.Equal("Zara Larsson — Ain't My Fault", info.DisplayText);
    }

    [Fact]
    public void BrowserVersionSuffixIsKeptWhenTheArtistIsSupplied()
    {
        var info = NowPlayingInfo.FromSession(
            "chrome.exe",
            "Ain't My Fault - Live",
            "Zara Larsson");

        Assert.NotNull(info);
        Assert.Equal("Ain't My Fault - Live", info.Title);
        Assert.Equal("Zara Larsson — Ain't My Fault - Live", info.DisplayText);
    }

    [Fact]
    public void OtherPlayersKeepTheirRealName()
    {
        var info = NowPlayingInfo.FromSession("foobar2000.exe", "Track", "Artist");

        Assert.NotNull(info);
        Assert.Equal("foobar2000", info.Source);
    }
}
