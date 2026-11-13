using System.Diagnostics;
using System.Windows.Automation;
using Windows.Media.Control;

namespace VrcCompanion.Services;

/// <summary>
/// One track reported by a Windows media session (Spotify, a browser tab, etc.).
/// </summary>
public sealed record NowPlayingInfo(
    string Title,
    string Artist,
    string Source,
    TimeSpan Position = default,
    TimeSpan Duration = default)
{
    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);

    public bool IsBrowserSource => Source is "Chrome" or "Edge" or "Firefox" or "Brave" or "Opera" or "Browser";

    public string DisplayText => HasArtist ? $"{Artist} — {Title}" : Title;

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
        else if (lowerApp.Contains("freebuff"))
        {
            source = "Browser";
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

        if (isBrowser || source is "YouTube" or "Browser" || lowerApp.Contains("freebuff"))
        {
            cleanTitle = StripYouTubeSuffix(cleanTitle);
            var normalised = NormaliseBrowserTitle(cleanTitle, artist, isBrowser || lowerApp.Contains("freebuff"));
            cleanTitle = normalised.Title;
            artist = normalised.Artist;
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

    private static (string Title, string Artist) NormaliseBrowserTitle(
        string title,
        string? artist,
        bool isBrowser)
    {
        if (!isBrowser)
        {
            return (title, artist ?? string.Empty);
        }

        var clean = LyricsService.CleanTrackTitle(title, artist ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(artist))
        {
            var artistKey = NormaliseForMatch(artist);
            var remainder = clean
                .Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => NormaliseForMatch(part) != artistKey)
                .ToArray();

            if (remainder.Length == 0)
            {
                return (clean, artist);
            }

            if (remainder.Length == 1)
            {
                return (remainder[0], artist);
            }

            var last = remainder[^1];
            var isVersion = last.Contains("live", StringComparison.OrdinalIgnoreCase) ||
                            last.Contains("remix", StringComparison.OrdinalIgnoreCase) ||
                            last.Contains("acoustic", StringComparison.OrdinalIgnoreCase) ||
                            last.Contains("version", StringComparison.OrdinalIgnoreCase) ||
                            last.Contains("mix", StringComparison.OrdinalIgnoreCase) ||
                            last.Contains("instrumental", StringComparison.OrdinalIgnoreCase) ||
                            last.Contains("extended", StringComparison.OrdinalIgnoreCase) ||
                            last.Contains("demo", StringComparison.OrdinalIgnoreCase) ||
                            last.Contains("sped up", StringComparison.OrdinalIgnoreCase) ||
                            last.Contains("slowed", StringComparison.OrdinalIgnoreCase) ||
                            last.Contains("nightcore", StringComparison.OrdinalIgnoreCase);
            return isVersion
                ? (string.Join(" - ", remainder), artist)
                : (remainder[0], artist);
        }

        var parts = clean.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            >= 3 => (string.Join(" - ", parts[1..^1]), parts[0]),
            2 => (parts[1], parts[0]),
            _ => (clean, string.Empty)
        };
    }

    private static string NormaliseForMatch(string value) =>
        new string(value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

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
            if (session is not null && IsIgnoredSource(session.SourceAppUserModelId))
            {
                session = null;
            }

            if (session is null ||
                session.GetPlaybackInfo().PlaybackStatus !=
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                session = manager
                    .GetSessions()
                    .FirstOrDefault(candidate =>
                        !IsIgnoredSource(candidate.SourceAppUserModelId) &&
                        candidate.GetPlaybackInfo().PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            }

            if (session is null)
            {
                return null;
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            var info = NowPlayingInfo.FromSession(
                session.SourceAppUserModelId,
                properties?.Title,
                properties?.Artist);

            // Browsers report the page's raw media title, which often has no
            // "YouTube"/"Spotify" marker. Their open tabs do, so use those to
            // label the service instead of showing just "Chrome"/"Brave".
            if (info is { IsBrowserSource: true })
            {
                var service = BrowserTabDetector.FindService(
                    session.SourceAppUserModelId,
                    info.Title);

                if (service is not null)
                {
                    info = info with { Source = service };
                }
            }

            var timeline = session.GetTimelineProperties();
            if (info is not null)
            {
                info = info with
                {
                    Position = ResolveTimelinePosition(
                        timeline.Position,
                        timeline.LastUpdatedTime,
                        DateTimeOffset.UtcNow,
                        timeline.EndTime),
                    Duration = timeline.EndTime > timeline.StartTime
                        ? timeline.EndTime - timeline.StartTime
                        : TimeSpan.Zero
                };
            }

            return info;
        }
        catch
        {
            // Media session APIs are best effort: they are unavailable on older
            // Windows builds and can fail while a player is starting.
            return null;
        }
    }

    private static bool IsIgnoredSource(string? appId) =>
        appId?.Contains("freebuff", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Some players (notably Chromium) report a timeline only once and leave
    /// Position at zero. LastUpdatedTime lets us advance to the real position.
    /// </summary>
    internal static TimeSpan ResolveTimelinePosition(
        TimeSpan reportedPosition,
        DateTimeOffset lastUpdated,
        DateTimeOffset now,
        TimeSpan end)
    {
        var elapsed = now - lastUpdated;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var position = reportedPosition + elapsed;
        if (end > TimeSpan.Zero && position > end)
        {
            position = end;
        }

        return position;
    }
}

/// <summary>
/// Finds the browser tab that is playing a track and reads its site label.
/// </summary>
internal static class BrowserTabDetector
{
    public static string? FindService(string? appId, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        foreach (var processName in ProcessNamesFor(appId))
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    var service = FindInWindow(process.MainWindowHandle, title);
                    if (service is not null)
                    {
                        return service;
                    }
                }
            }
        }

        return null;
    }

    private static string? FindInWindow(IntPtr handle, string title)
    {
        try
        {
            var root = AutomationElement.FromHandle(handle);
            var tabCondition = new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.TabItem);
            var tabs = root.FindAll(TreeScope.Descendants, tabCondition);

            foreach (AutomationElement tab in tabs)
            {
                var tabName = tab.Current.Name;
                if (string.IsNullOrWhiteSpace(tabName) ||
                    !tabName.Contains(title, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (tabName.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
                {
                    return "YouTube";
                }

                if (tabName.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                {
                    return "Spotify";
                }
            }
        }
        catch
        {
            // The browser may be closing or using a UI that does not expose tabs.
        }

        return null;
    }

    private static IEnumerable<string> ProcessNamesFor(string appId)
    {
        var app = appId.ToLowerInvariant();
        if (app.Contains("brave"))
        {
            yield return "brave";
        }
        else if (app.Contains("msedge"))
        {
            yield return "msedge";
        }
        else if (app.Contains("chrome"))
        {
            yield return "chrome";
        }
        else if (app.Contains("firefox"))
        {
            yield return "firefox";
        }
        else if (app.Contains("opera"))
        {
            yield return "opera";
        }
    }
}
