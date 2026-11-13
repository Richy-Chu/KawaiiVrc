using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace VrcCompanion.Services;

/// <summary>A single timed lyric line (LRC).</summary>
public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>Lyrics fetched from the free LRCLIB database.</summary>
public sealed class LyricsDocument
{
    private LyricsDocument(IReadOnlyList<LyricLine> synced, IReadOnlyList<string> plain)
    {
        SyncedLines = synced;
        PlainLines = plain;
    }

    public IReadOnlyList<LyricLine> SyncedLines { get; }

    public IReadOnlyList<string> PlainLines { get; }

    public bool IsSynced => SyncedLines.Count > 0;

    public bool HasLyrics => IsSynced || PlainLines.Count > 0;

    public string? PreviewLine(TimeSpan position)
    {
        if (IsSynced)
        {
            return LineAt(position);
        }

        return PlainLines.Count > 0 ? PlainLines[0] : null;
    }

    public string? LineAt(TimeSpan position)
    {
        if (SyncedLines.Count == 0)
        {
            return null;
        }

        var current = SyncedLines[0].Text;
        foreach (var line in SyncedLines)
        {
            if (line.Time > position + TimeSpan.FromMilliseconds(350))
            {
                break;
            }

            current = line.Text;
        }

        return current;
    }

    public static LyricsDocument? Parse(string? syncedLyrics, string? plainLyrics)
    {
        var synced = ParseLrc(syncedLyrics);
        var plain = (plainLyrics ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (synced.Count == 0 && plain.Count == 0)
        {
            return null;
        }

        return new LyricsDocument(synced, plain);
    }

    private static List<LyricLine> ParseLrc(string? lrc)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return lines;
        }

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();
            var times = new List<TimeSpan>();
            while (line.StartsWith('['))
            {
                var close = line.IndexOf(']');
                if (close < 0)
                {
                    break;
                }

                var tag = line[1..close];
                if (TryParseTimestamp(tag, out var time))
                {
                    times.Add(time);
                }

                line = line[(close + 1)..].TrimStart();
            }

            if (times.Count == 0 || line.Length == 0)
            {
                continue;
            }

            foreach (var time in times)
            {
                lines.Add(new LyricLine(time, line));
            }
        }

        return lines.OrderBy(line => line.Time).ToList();
    }

    private static bool TryParseTimestamp(string tag, out TimeSpan time)
    {
        time = default;
        var colon = tag.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        if (!int.TryParse(tag[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(tag[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        if (minutes < 0 || seconds < 0 || seconds >= 60)
        {
            return false;
        }

        time = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return true;
    }
}

public sealed record LyricsLookupResult(
    LyricsDocument? Lyrics,
    string Status,
    bool TemporaryFailure = false,
    string? Match = null);

/// <summary>Formats a VRChat chatbox line containing the song and, if present, a lyric.</summary>
public static class ChatboxLyrics
{
    public static string Compose(string song, string? lyric)
    {
        var header = song.Length > OscMessage.ChatboxLimit
            ? song[..OscMessage.ChatboxLimit]
            : song;

        if (string.IsNullOrWhiteSpace(lyric))
        {
            return header;
        }

        const string separator = " | ";
        var available = OscMessage.ChatboxLimit - header.Length - separator.Length;
        if (available <= 3)
        {
            return header;
        }

        var line = lyric.Trim();
        if (line.Length > available)
        {
            line = line[..available].TrimEnd();
        }

        return $"{header}{separator}{line}";
    }
}

/// <summary>
/// Fetches lyrics from LRCLIB (https://lrclib.net), a free, keyless, open
/// lyrics database. Lookups are best effort and never block the UI.
/// </summary>
public sealed class LyricsService
{
    private static readonly HttpClient SharedHttp = new()
    {
        BaseAddress = new Uri("https://lrclib.net"),
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly HttpClient _http;
    private readonly Dictionary<string, LyricsLookupResult> _cache = new(StringComparer.OrdinalIgnoreCase);

    static LyricsService()
    {
        SharedHttp.DefaultRequestHeaders.UserAgent.ParseAdd("VrcCompanion/1.0");
        SharedHttp.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public LyricsService()
        : this(SharedHttp)
    {
    }

    internal LyricsService(HttpClient http)
    {
        _http = http;
    }

    public async Task<LyricsLookupResult> GetForTrackAsync(
        string title,
        string artist,
        CancellationToken cancellationToken = default,
        TimeSpan duration = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new LyricsLookupResult(null, "No track to look up.");
        }

        var cleanTitle = CleanTrackTitle(title, artist);
        var key = $"{cleanTitle}\u001f{artist}\u001f{(int)duration.TotalSeconds}";
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var result = await FetchAsync(cleanTitle, artist, duration, cancellationToken);
        if (!result.TemporaryFailure)
        {
            lock (_cache)
            {
                if (_cache.Count > 100)
                {
                    _cache.Clear();
                }

                _cache[key] = result;
            }
        }

        return result;
    }

    private async Task<LyricsLookupResult> FetchAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = BuildCandidates(title, artist).ToList();
            LrcLibRecord? record = null;

            foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate.Artist)))
            {
                var url = $"/api/get?track_name={Uri.EscapeDataString(candidate.Title)}&artist_name={Uri.EscapeDataString(candidate.Artist)}";
                if (duration > TimeSpan.Zero)
                {
                    url += $"&duration={(int)Math.Round(duration.TotalSeconds)}";
                }

                using var response = await _http.GetAsync(url, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    record = await response.Content.ReadFromJsonAsync<LrcLibRecord>(cancellationToken: cancellationToken);
                    if (record is not null && DurationMatches(record, duration))
                    {
                        break;
                    }

                    record = null;
                }
                else if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    return new LyricsLookupResult(null, "The free lyrics service is busy. Try again shortly.", true);
                }
            }

            if (record is null)
            {
                foreach (var candidate in candidates)
                {
                    record = await SearchAsync(candidate.Title, candidate.Artist, duration, cancellationToken);
                    if (record is not null)
                    {
                        break;
                    }
                }
            }

            if (record is null)
            {
                return new LyricsLookupResult(null, "No free lyrics found for this track.");
            }

            if (record.Instrumental)
            {
                return new LyricsLookupResult(null, "This track is instrumental.");
            }

            var lyrics = LyricsDocument.Parse(record.SyncedLyrics, record.PlainLyrics);
            return lyrics is null
                ? new LyricsLookupResult(null, "No free lyrics found for this track.")
                : new LyricsLookupResult(
                    lyrics,
                    lyrics.IsSynced ? "Synced lyrics · LRCLIB" : "Plain lyrics · LRCLIB",
                    Match: DescribeMatch(record, title, artist));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new LyricsLookupResult(null, "Could not reach the free lyrics service.", true);
        }
    }

    public void ClearCache()
    {
        lock (_cache)
        {
            _cache.Clear();
        }
    }

    private static bool DurationMatches(LrcLibRecord record, TimeSpan duration) =>
        duration <= TimeSpan.Zero ||
        (record.Duration > 0 && Math.Abs(record.Duration - duration.TotalSeconds) <= 15);

    private static string DescribeMatch(LrcLibRecord record, string title, string artist)
    {
        var matchTitle = string.IsNullOrWhiteSpace(record.TrackName) ? title : record.TrackName.Trim();
        var matchArtist = string.IsNullOrWhiteSpace(record.ArtistName) ? artist.Trim() : record.ArtistName.Trim();
        var name = string.IsNullOrWhiteSpace(matchArtist) ? matchTitle : $"{matchArtist} — {matchTitle}";
        if (record.Duration > 0)
        {
            name += $" ({TimeSpan.FromSeconds(record.Duration):m\\:ss})";
        }

        return name;
    }

    internal static IEnumerable<(string Title, string Artist)> BuildCandidates(string title, string artist)
    {
        var clean = CleanTrackTitle(title, artist);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (seen.Add($"{clean}\u001f{artist.Trim()}"))
        {
            yield return (clean, artist.Trim());
        }

        var parts = clean.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            if (!string.IsNullOrWhiteSpace(artist) && seen.Add($"{parts[0]}\u001f{artist}"))
            {
                // Browser titles often append the channel: "Song - Channel".
                yield return (parts[0], artist.Trim());
            }
            else if (string.IsNullOrWhiteSpace(artist))
            {
                var inferredArtist = parts[0];
                var inferredTitle = parts.Length >= 3
                    ? string.Join(" - ", parts[1..^1])
                    : parts[1];

                if (seen.Add($"{inferredTitle}\u001f{inferredArtist}"))
                {
                    yield return (inferredTitle, inferredArtist);
                }
            }
        }
    }

    private async Task<LrcLibRecord?> SearchAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        var url = $"/api/search?q={Uri.EscapeDataString(query)}";
        using var response = await _http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var results = await response.Content.ReadFromJsonAsync<List<LrcLibRecord>>(cancellationToken: cancellationToken);
        return results?
            .Where(result =>
                (!string.IsNullOrWhiteSpace(result.SyncedLyrics) ||
                 !string.IsNullOrWhiteSpace(result.PlainLyrics)) &&
                DurationMatches(result, duration) &&
                MatchesIdentity(result, title, artist))
            .OrderBy(result =>
                duration > TimeSpan.Zero && result.Duration > 0
                    ? Math.Abs(result.Duration - duration.TotalSeconds)
                    : 0)
            .ThenByDescending(result => !string.IsNullOrWhiteSpace(result.SyncedLyrics))
            .FirstOrDefault();
    }

    private static bool MatchesIdentity(LrcLibRecord record, string title, string artist)
    {
        var wantedTitle = NormaliseForMatch(title);
        var foundTitle = NormaliseForMatch(record.TrackName);
        if (wantedTitle.Length == 0 || foundTitle.Length == 0 ||
            !(foundTitle.Contains(wantedTitle, StringComparison.Ordinal) ||
              wantedTitle.Contains(foundTitle, StringComparison.Ordinal)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(artist))
        {
            return true;
        }

        var wantedArtist = NormaliseForMatch(artist);
        var foundArtist = NormaliseForMatch(record.ArtistName);
        return wantedArtist.Length == 0 || foundArtist.Length == 0 ||
               foundArtist.Contains(wantedArtist, StringComparison.Ordinal) ||
               wantedArtist.Contains(foundArtist, StringComparison.Ordinal);
    }

    private static string NormaliseForMatch(string? value) =>
        new string((value ?? string.Empty)
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    /// <summary>
    /// YouTube titles often look like "Artist - Song (Official Video) (4K)".
    /// Strip those decorations so LRCLIB can find the real track title.
    /// </summary>
    public static string CleanTrackTitle(string title, string artist)
    {
        var clean = title.Trim();
        foreach (var suffix in new[] { " - YouTube Music", " - YouTube", " | YouTube Music", " | YouTube" })
        {
            if (clean.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[..^suffix.Length].Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(artist) &&
            clean.StartsWith(artist.Trim() + " - ", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[(artist.Trim().Length + 3)..].Trim();
        }

        clean = RemoveDecorations(clean).Trim();
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return clean.Length > 0 ? clean : title.Trim();
    }

    private static string RemoveDecorations(string text)
    {
        var decorations = new[]
        {
            "official", "video", "audio", "lyric", "remaster", "4k", "hd", "visualizer"
        };
        var result = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '(' or '[')
            {
                var close = text.IndexOf(text[i] == '(' ? ')' : ']', i + 1);
                if (close > i)
                {
                    var inside = text[(i + 1)..close];
                    if (decorations.Any(decoration => inside.Contains(decoration, StringComparison.OrdinalIgnoreCase)))
                    {
                        i = close;
                        continue;
                    }
                }
            }

            result.Append(text[i]);
        }

        return result.ToString();
    }

    private sealed class LrcLibRecord
    {
        [JsonPropertyName("trackName")]
        public string? TrackName { get; set; }

        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("instrumental")]
        public bool Instrumental { get; set; }

        [JsonPropertyName("plainLyrics")]
        public string? PlainLyrics { get; set; }

        [JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; set; }
    }
}
