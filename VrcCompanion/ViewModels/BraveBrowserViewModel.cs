using System;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// A general-purpose in-app browser backed by Brave Search. Search results and
/// sites open in the same WebView2 pane, so site sign-ins use the normal profile.
/// </summary>
public sealed class BraveBrowserViewModel : ViewModelBase, IEmbeddedBrowserHost
{
    public const string HomeUrl = "https://search.brave.com/";

    private string _searchQuery = string.Empty;
    private bool _isBrowserOpen;
    private string _browserUrl = HomeUrl;
    private string _browserTitle = "Brave Search";
    private string _browserError = string.Empty;
    private string _downloadStatus = string.Empty;
    private bool _downloadStatusIsError;
    private string? _downloadedFilePath;

    public BraveBrowserViewModel()
    {
        SearchCommand = new RelayCommand(Search, () => !string.IsNullOrWhiteSpace(SearchQuery));
        HomeCommand = new RelayCommand(GoHome);
        OpenDownloadFolderCommand = new RelayCommand(OpenDownloadFolder);
    }

    public event Action<string>? BrowserNavigationRequested;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value ?? string.Empty))
            {
                SearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBrowserOpen
    {
        get => _isBrowserOpen;
        private set => SetProperty(ref _isBrowserOpen, value);
    }

    public string BrowserUrl
    {
        get => _browserUrl;
        private set => SetProperty(ref _browserUrl, value);
    }

    public string BrowserTitle
    {
        get => _browserTitle;
        private set => SetProperty(ref _browserTitle, value);
    }

    public string BrowserError
    {
        get => _browserError;
        private set => SetProperty(ref _browserError, value);
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => SetProperty(ref _downloadStatus, value);
    }

    public bool DownloadStatusIsError
    {
        get => _downloadStatusIsError;
        private set => SetProperty(ref _downloadStatusIsError, value);
    }

    public bool CanOpenDownloadFolder => !string.IsNullOrWhiteSpace(_downloadedFilePath);

    public RelayCommand SearchCommand { get; }

    public RelayCommand HomeCommand { get; }

    public RelayCommand OpenDownloadFolderCommand { get; }

    public void SetBrowserLocation(string url, string title)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            BrowserUrl = url;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            BrowserTitle = title;
        }
    }

    public void SetBrowserError(string message)
        => BrowserError = message;

    public void SetDownloadStatus(string message, bool isError = false, string? downloadedPath = null)
    {
        DownloadStatus = message;
        DownloadStatusIsError = isError;
        SetDownloadedFilePath(downloadedPath);
    }

    private void Search()
    {
        var query = SearchQuery.Trim();
        if (query.Length == 0)
        {
            return;
        }

        Navigate(BuildDestination(query), "Brave Browser");
    }

    internal static string BuildDestination(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (!trimmed.Contains(' ') && trimmed.Contains('.') && !trimmed.Contains('\\'))
        {
            return "https://" + trimmed;
        }

        return "https://search.brave.com/search?q=" + Uri.EscapeDataString(trimmed);
    }

    private void GoHome()
    {
        Navigate(HomeUrl, "Brave Search");
    }

    private void Navigate(string url, string title)
    {
        BrowserTitle = title;
        BrowserUrl = url;
        BrowserError = string.Empty;
        SetDownloadStatus(string.Empty);
        IsBrowserOpen = true;
        BrowserNavigationRequested?.Invoke(url);
    }

    private void SetDownloadedFilePath(string? path)
    {
        if (string.Equals(_downloadedFilePath, path, StringComparison.Ordinal))
        {
            return;
        }

        _downloadedFilePath = path;
        OnPropertyChanged(nameof(CanOpenDownloadFolder));
    }

    private void OpenDownloadFolder()
    {
        if (!LaunchService.OpenFolder(_downloadedFilePath))
        {
            SetDownloadStatus("The downloaded file and its folder are no longer available.", true);
        }
    }
}
