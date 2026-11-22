using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace VrcCompanion.Services;

public enum AppUpdateCheckState
{
    /// <summary>The running build is not a Velopack install (dev run, portable copy).</summary>
    Unsupported,
    UpToDate,
    Available,
    Failed
}

/// <summary>One update check result. A failure carries text for the manual check only.</summary>
public sealed record AppUpdateCheckResult(
    AppUpdateCheckState State,
    string? Version = null,
    string Notes = "",
    string? Error = null);

/// <summary>The seam the popup view model talks to; tests replace it with a fake.</summary>
public interface IAppUpdateService
{
    bool IsSupported { get; }

    string CurrentVersion { get; }

    Task<AppUpdateCheckResult> CheckAsync();

    Task<bool> DownloadAsync(Action<int>? progress, CancellationToken cancellationToken);

    bool ApplyAndRestart();

    void OpenReleasesPage();
}

/// <summary>
/// Thin wrapper over Velopack. Everything that can go wrong (no install, offline,
/// GitHub rate limit, checksum failure) is turned into a result instead of an
/// exception, so callers never crash and automatic checks stay silent.
/// </summary>
public sealed class AppUpdateService : IAppUpdateService
{
    private const string FallbackRepositoryUrl = "https://github.com/Richy-Chu/KawaiiVrc";
    private const string RepositoryMetadataKey = "UpdateRepository";
    private const string FeedFileName = "releases.win.json";

    /// <summary>
    /// One request for the release feed answers "is there a newer version?" in a
    /// fraction of a second. Velopack's own check lists every release through the
    /// GitHub API first, which is what made an update take a minute to show up.
    /// </summary>
    private static readonly HttpClient FeedClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly UpdateManager? _manager;
    private readonly string _repositoryUrl;
    private readonly HttpClient _feedClient;
    private readonly object _sync = new();
    private UpdateInfo? _pending;

    public AppUpdateService()
        : this(ReadRepositoryUrl())
    {
    }

    public AppUpdateService(string repositoryUrl)
        : this(repositoryUrl, FeedClient)
    {
    }

    internal AppUpdateService(string repositoryUrl, HttpClient feedClient)
    {
        _repositoryUrl = repositoryUrl;
        _feedClient = feedClient;
        try
        {
            var manager = new UpdateManager(new GithubSource(repositoryUrl, null, false));
            if (manager.IsInstalled)
            {
                _manager = manager;
                LifecycleLog.Write($"updater: installed {manager.CurrentVersion}, feed {repositoryUrl}");
            }
            else
            {
                LifecycleLog.Write("updater: not an installed build, checks disabled");
            }
        }
        catch (Exception ex)
        {
            // Running from bin\Debug, a portable copy, or with a broken install.
            // Updates are simply unavailable; startup continues normally.
            LifecycleLog.Write($"updater: unavailable ({ex.GetType().Name}: {ex.Message})");
            Debug.WriteLine($"Updater unavailable: {ex}");
        }
    }

    /// <summary>False for dev/portable runs, where Velopack refuses to check.</summary>
    public bool IsSupported => _manager is not null;

    public string CurrentVersion
    {
        get
        {
            var installed = _manager?.CurrentVersion?.ToString();
            return string.IsNullOrWhiteSpace(installed)
                ? typeof(AppUpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"
                : installed;
        }
    }

    public async Task<AppUpdateCheckResult> CheckAsync()
    {
        var manager = _manager;
        if (manager is null)
        {
            return new AppUpdateCheckResult(AppUpdateCheckState.Unsupported);
        }

        // The feed answers the common "nothing new" case without waking Velopack.
        var fast = await ReadFeedAsync().ConfigureAwait(false);
        if (fast is not null)
        {
            return fast;
        }

        return await CheckWithVelopackAsync(manager).ConfigureAwait(false);
    }

    /// <summary>Reads the feed vpk publishes with each release; null when it cannot.</summary>
    private async Task<AppUpdateCheckResult?> ReadFeedAsync()
    {
        try
        {
            var url = $"{_repositoryUrl.TrimEnd('/')}/releases/latest/download/{FeedFileName}";
            using var response = await _feedClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return InterpretFeed(json, CurrentVersion);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update feed read failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// The feed lists every asset of the latest release with its version and notes,
    /// so a newer version and what changed arrive together. Null means the feed
    /// was not something this build understands.
    /// </summary>
    internal static AppUpdateCheckResult? InterpretFeed(string json, string currentVersion)
    {
        if (!SemanticVersion.TryParse(currentVersion, out var current))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // A captive portal or an error page answering for the feed.
            return null;
        }

        using (document)
        {
            return InterpretAssets(document.RootElement, current);
        }
    }

    private static AppUpdateCheckResult? InterpretAssets(JsonElement root, SemanticVersion current)
    {
        if (!root.TryGetProperty("Assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        SemanticVersion? newest = null;
        var newestVersion = string.Empty;
        var notes = string.Empty;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("Version", out var versionElement))
            {
                continue;
            }

            var text = versionElement.GetString();
            if (text is null || !SemanticVersion.TryParse(text, out var version))
            {
                continue;
            }

            if (newest is not null && version.CompareTo(newest) <= 0)
            {
                continue;
            }

            newest = version;
            newestVersion = text;
            var markdown = asset.TryGetProperty("NotesMarkdown", out var markdownElement)
                ? markdownElement.GetString()
                : null;
            var html = asset.TryGetProperty("NotesHTML", out var htmlElement)
                ? htmlElement.GetString()
                : null;
            notes = ReleaseNotes.ToPlainText(markdown, html);
        }

        if (newest is null)
        {
            return null;
        }

        return newest.CompareTo(current) > 0
            ? new AppUpdateCheckResult(AppUpdateCheckState.Available, newestVersion, notes)
            : new AppUpdateCheckResult(AppUpdateCheckState.UpToDate);
    }

    /// <summary>
    /// The full check, used when the feed could not be read. It is slower and it
    /// also fills in the update Velopack needs before a download can start.
    /// </summary>
    private async Task<AppUpdateCheckResult> CheckWithVelopackAsync(UpdateManager manager)
    {
        try
        {
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                return new AppUpdateCheckResult(AppUpdateCheckState.UpToDate);
            }

            lock (_sync)
            {
                _pending = update;
            }

            var release = update.TargetFullRelease;
            var notes = ReleaseNotes.ToPlainText(release.NotesMarkdown, release.NotesHTML);
            return new AppUpdateCheckResult(
                AppUpdateCheckState.Available,
                release.Version?.ToString(),
                notes);
        }
        catch (NotInstalledException)
        {
            return new AppUpdateCheckResult(AppUpdateCheckState.Unsupported);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed: {ex}");
            return new AppUpdateCheckResult(
                AppUpdateCheckState.Failed,
                Error: "Couldn't check for updates. Check your connection and try again.");
        }
    }

    /// <summary>Downloads the pending update. Returns false when it could not finish.</summary>
    public async Task<bool> DownloadAsync(Action<int>? progress, CancellationToken cancellationToken)
    {
        var manager = _manager;
        if (manager is null)
        {
            return false;
        }

        try
        {
            // A check answered from the feed has no Velopack update object yet;
            // resolve it now, once, instead of doing it on every check.
            var update = _pending;
            if (update is null)
            {
                update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
                lock (_sync)
                {
                    _pending = update;
                }
            }

            if (update is null)
            {
                return false;
            }

            await manager
                .DownloadUpdatesAsync(update, progress, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update download failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Hands the downloaded update to Velopack's Update.exe, which waits for this
    /// process to exit, swaps the app folder and relaunches the new version.
    /// </summary>
    public bool ApplyAndRestart()
    {
        var manager = _manager;
        UpdateInfo? update;
        lock (_sync)
        {
            update = _pending;
        }

        if (manager is null || update is null)
        {
            return false;
        }

        try
        {
            manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update install failed: {ex}");
            return false;
        }
    }

    public void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"{ReadRepositoryUrl()}/releases/latest")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not open the releases page: {ex}");
        }
    }

    private static string ReadRepositoryUrl()
    {
        var metadata = typeof(AppUpdateService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == RepositoryMetadataKey);
        var url = metadata?.Value?.Trim();
        return string.IsNullOrWhiteSpace(url) ? FallbackRepositoryUrl : url;
    }
}

/// <summary>Turns release-note markdown/HTML into short plain text for the dialog.</summary>
public static class ReleaseNotes
{
    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex MarkdownLink = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex ExtraBlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    public static string ToPlainText(string? markdown, string? html)
    {
        var text = !string.IsNullOrWhiteSpace(markdown)
            ? markdown
            : WebUtility.HtmlDecode(HtmlTag.Replace(html ?? string.Empty, "\n"));

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            var heading = Regex.Match(trimmed, "^#{1,6}\\s+(.*)$");
            if (heading.Success)
            {
                lines.Add(Inline(heading.Groups[1].Value));
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                lines.Add("• " + Inline(trimmed[2..]));
                continue;
            }

            lines.Add(Inline(trimmed));
        }

        var joined = string.Join('\n', lines).Trim();
        return ExtraBlankLines.Replace(joined, "\n\n");
    }

    private static string Inline(string text)
    {
        var cleaned = MarkdownLink.Replace(text, "$1");
        cleaned = Bold.Replace(cleaned, "$1");
        return cleaned.Replace("`", string.Empty).Trim();
    }
}
