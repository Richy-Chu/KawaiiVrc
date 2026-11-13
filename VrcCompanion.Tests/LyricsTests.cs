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
    public void PlaybackPositionKeepsAdvancingWhenMediaSessionIsStale()
    {
        var estimator = new PlaybackPositionEstimator();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var track = new NowPlayingInfo("Song", "Artist", "Spotify", TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(3));

        var position = estimator.Update(track, now);
        Assert.Equal(10, position.TotalSeconds, 3);

        Assert.Equal(20, estimator.Estimate(track, now.AddSeconds(10)).TotalSeconds, 3);
    }

    [Fact]
    public void EstimatedPositionMovesSyncedLyricsForward()
    {
        var lyrics = LyricsDocument.Parse("[00:00.00] First line\n[00:10.00] Second line\n", null);
        var estimator = new PlaybackPositionEstimator();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var track = new NowPlayingInfo("Song", "Artist", "Spotify", TimeSpan.Zero, TimeSpan.FromMinutes(3));

        estimator.Update(track, now);

        Assert.NotNull(lyrics);
        Assert.Equal("First line", lyrics.LineAt(estimator.Estimate(track, now)));
        Assert.Equal("Second line", lyrics.LineAt(estimator.Estimate(track, now.AddSeconds(12))));
    }

    [Fact]
    public void PlaybackPositionResyncsToTheMediaSession()
    {
        var estimator = new PlaybackPositionEstimator();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var track = new NowPlayingInfo("Song", "Artist", "Spotify", TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(3));

        estimator.Update(track, now);
        estimator.Update(track with { Position = TimeSpan.FromSeconds(12) }, now.AddSeconds(5));

        Assert.Equal(17, estimator.Estimate(track, now.AddSeconds(10)).TotalSeconds, 3);
    }

    [Fact]
    public void ManualChatboxMessagePausesLyrics()
    {
        var policy = new LyricsPausePolicy();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        policy.OnMessageSent(ChatboxMessageKind.Manual, 30, now);

        Assert.True(policy.IsPaused(now.AddSeconds(25)));
        Assert.False(policy.IsPaused(now.AddSeconds(31)));
    }

    [Fact]
    public void RotationMessageDoesNotPauseLyrics()
    {
        var policy = new LyricsPausePolicy();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        policy.OnMessageSent(ChatboxMessageKind.Rotation, 30, now);

        Assert.False(policy.IsPaused(now.AddSeconds(1)));
        Assert.False(policy.IsPaused(now.AddSeconds(10)));
    }

    [Fact]
    public void ManualPauseCanBeResumedImmediately()
    {
        var policy = new LyricsPausePolicy();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        policy.OnMessageSent(ChatboxMessageKind.Manual, 30, now);
        policy.Resume();

        Assert.False(policy.IsPaused(now.AddSeconds(1)));
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
