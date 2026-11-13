using System.Diagnostics;
using System.Windows.Automation;
using Windows.Media.Control;

namespace VrcCompanion.Services;

/// <summary>
/// One track reported by a Windows media session (Spotify, a browser tab, etc.).
/// </summary>
public sealed record NowPlayingInfo(string Title, string Artist, string Source)
{
    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);

    public bool IsBrowserSource => Source is "Chrome" or "Edge" or "Firefox" or "Brave" or "Opera" or "Browser";

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

            return info;
        }
        catch
        {
            // Media session APIs are best effort: they are unavailable on older
            // Windows builds and can fail while a player is starting.
            return null;
        }
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
