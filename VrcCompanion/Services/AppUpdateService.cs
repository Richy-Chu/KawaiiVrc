using System.Diagnostics;
using System.Net;
using System.Reflection;
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

    private readonly UpdateManager? _manager;
    private readonly object _sync = new();
    private UpdateInfo? _pending;

    public AppUpdateService()
        : this(ReadRepositoryUrl())
    {
    }

    public AppUpdateService(string repositoryUrl)
    {
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
