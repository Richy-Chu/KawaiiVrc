using Windows.Media.Control;

namespace VrcCompanion.Services;

/// <summary>
/// One track reported by a Windows media session (Spotify, a browser tab, etc.).
/// </summary>
public sealed record NowPlayingInfo(string Title, string Artist, string Source)
{
    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);

    public string DisplayText => HasArtist ? $"{Title} — {Artist}" : Title;

    public string ChatboxText
    {
        get
        {
            var source = Source.Length > 0 ? $" · {Source}" : string.Empty;
            return $"♪ {DisplayText}{source}";
        }
    }

    /// <summary>
    /// Labels the media session source. Browser sessions are labelled by what
    /// they are playing (YouTube / Spotify) rather than "Chrome".
    /// </summary>
    public static NowPlayingInfo? FromSession(string? appId, string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var cleanTitle = title.Trim();
        var lowerTitle = cleanTitle.ToLowerInvariant();
        var lowerApp = (appId ?? string.Empty).ToLowerInvariant();
        var isBrowser = lowerApp.Contains("chrome") ||
                        lowerApp.Contains("msedge") ||
                        lowerApp.Contains("firefox") ||
                        lowerApp.Contains("brave") ||
                        lowerApp.Contains("opera");

        string source;
        if (lowerApp.Contains("spotify") || lowerTitle.Contains("spotify"))
        {
            source = "Spotify";
        }
        else if (lowerApp.Contains("youtube") ||
                 lowerTitle.Contains("youtube") ||
                 lowerTitle.Contains("youtu.be"))
        {
            source = "YouTube";
        }
        else if (isBrowser)
        {
            source = lowerApp switch
            {
                var app when app.Contains("chrome") => "Chrome",
                var app when app.Contains("msedge") => "Edge",
                var app when app.Contains("firefox") => "Firefox",
                var app when app.Contains("brave") => "Brave",
                var app when app.Contains("opera") => "Opera",
                _ => "Browser",
            };
        }
        else
        {
            source = CleanAppName(appId);
        }

        if (source is "YouTube" or "Browser")
        {
            cleanTitle = StripYouTubeSuffix(cleanTitle);
        }

        // A browser's generic player page title carries no track information.
        if (cleanTitle.Equals("Spotify – Web Player", StringComparison.OrdinalIgnoreCase) ||
            cleanTitle.Equals("YouTube", StringComparison.OrdinalIgnoreCase) ||
            cleanTitle.Equals("YouTube Music", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new NowPlayingInfo(cleanTitle, (artist ?? string.Empty).Trim(), source);
    }

    private static string StripYouTubeSuffix(string title)
    {
        foreach (var suffix in new[] { " - YouTube Music", " - YouTube", " | YouTube Music", " | YouTube" })
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return title[..^suffix.Length].Trim();
            }
        }

        return title;
    }

    private static string CleanAppName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return string.Empty;
        }

        var name = appId;
        var bang = name.LastIndexOf('!');
        if (bang >= 0 && bang + 1 < name.Length)
        {
            name = name[(bang + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name;
    }
}

/// <summary>
/// Reads Windows' current media session. Returns null when nothing is playing.
/// </summary>
public sealed class NowPlayingService
{
    public async Task<NowPlayingInfo?> GetCurrentAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetCurrentSession();
            if (session is null ||
                session.GetPlaybackInfo().PlaybackStatus !=
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                session = manager
                    .GetSessions()
                    .FirstOrDefault(candidate =>
                        candidate.GetPlaybackInfo().PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            }

            if (session is null)
            {
                return null;
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            return NowPlayingInfo.FromSession(
                session.SourceAppUserModelId,
                properties?.Title,
                properties?.Artist);
        }
        catch
        {
            // Media session APIs are best effort: they are unavailable on older
            // Windows builds and can fail while a player is starting.
            return null;
        }
    }
}
