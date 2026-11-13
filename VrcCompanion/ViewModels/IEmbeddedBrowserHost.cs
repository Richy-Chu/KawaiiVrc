using System;

namespace VrcCompanion.ViewModels;

/// <summary>
/// The browser-facing state shared by Creator Resources, Brave, and Discord.
/// </summary>
public interface IEmbeddedBrowserHost
{
    event Action<string>? BrowserNavigationRequested;

    bool IsBrowserOpen { get; }

    string BrowserUrl { get; }

    void SetBrowserLocation(string url, string title);

    void SetBrowserError(string message);

    void SetDownloadStatus(string message, bool isError = false, string? downloadedPath = null);
}
