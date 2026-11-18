using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class LyricsTests
{
    [Fact]
    public void ParsesTimedLrcLinesInOrder()
    {
        var lyrics = LyricsDocument.Parse(
            "[00:12.50] First line\n[00:15.00][00:20.00] Repeated\n[00:25.00] Last\n",
            null);

        Assert.NotNull(lyrics);
        Assert.True(lyrics.IsSynced);
        Assert.Equal(4, lyrics.SyncedLines.Count);
        Assert.Equal("First line", lyrics.LineAt(TimeSpan.FromSeconds(13)));
        Assert.Equal("Repeated", lyrics.LineAt(TimeSpan.FromSeconds(16)));
        Assert.Equal("Last", lyrics.LineAt(TimeSpan.FromSeconds(40)));
    }

    [Fact]
    public void FallsBackToPlainLyrics()
    {
        var lyrics = LyricsDocument.Parse(null, "Line one\n\nLine two\n");

        Assert.NotNull(lyrics);
        Assert.False(lyrics.IsSynced);
        Assert.Equal(2, lyrics.PlainLines.Count);
        Assert.Equal("Line one", lyrics.PreviewLine(TimeSpan.Zero));
    }

    [Fact]
    public void IgnoresMetadataAndEmptyLyrics()
    {
        Assert.Null(LyricsDocument.Parse("[ar:Artist]\n[ti:Title]\n", null));
        Assert.Null(LyricsDocument.Parse(null, "  \n\n"));
    }

    [Fact]
    public void ChatboxLineIncludesSongEvenWithoutLyrics()
    {
        Assert.Equal("♪ Song — Artist · Spotify", ChatboxLyrics.Compose("♪ Song — Artist · Spotify", null));
    }

    [Fact]
    public void ChatboxLineAppendsAvailableLyric()
    {
        Assert.Equal(
            "♪ Song — Artist · Spotify | Current lyric line",
            ChatboxLyrics.Compose("♪ Song — Artist · Spotify", "Current lyric line"));
    }

    [Fact]
    public void ChatboxLineNeverExceedsTheOscLimit()
    {
        var message = ChatboxLyrics.Compose(new string('S', 100), new string('L', 100));
        Assert.Equal(OscMessage.ChatboxLimit, message.Length);
        Assert.StartsWith(new string('S', 100) + " | ", message);
    }

    [Fact]
    public void MediaTimelineWithZeroPositionAdvancesFromLastUpdated()
    {
        var lastUpdated = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var position = NowPlayingService.ResolveTimelinePosition(
            TimeSpan.Zero,
            lastUpdated,
            lastUpdated.AddSeconds(75),
            TimeSpan.FromMinutes(3));

        Assert.Equal(75, position.TotalSeconds, 3);
    }

    [Fact]
    public void MediaTimelineIsClampedToTheTrackEnd()
    {
        var lastUpdated = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var position = NowPlayingService.ResolveTimelinePosition(
            TimeSpan.FromSeconds(10),
            lastUpdated,
            lastUpdated.AddSeconds(500),
            TimeSpan.FromMinutes(3));

        Assert.Equal(TimeSpan.FromMinutes(3), position);
    }

    [Fact]
    public void BrowserTitleWithChannelSuffixProducesArtistAndTrackCandidates()
    {
        var candidates = LyricsService.BuildCandidates(
            "Zara Larsson - Ain't My Fault (Official Video) - ZaraLarssonMusicVEVO",
            "");

        Assert.Contains(candidates, candidate =>
            candidate.Title == "Ain't My Fault" && candidate.Artist == "Zara Larsson");
    }

    [Theory]
    [InlineData("Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster)", "Rick Astley", "Never Gonna Give You Up")]
    [InlineData("Song Name (Official Audio)", "Artist", "Song Name")]
    [InlineData("Artist - Track [Lyrics]", "Artist", "Track")]
    [InlineData("Track - YouTube", "", "Track")]
    public void CleansYouTubeStyleTitlesForLookup(string title, string artist, string expected)
    {
        Assert.Equal(expected, LyricsService.CleanTrackTitle(title, artist));
    }
}
