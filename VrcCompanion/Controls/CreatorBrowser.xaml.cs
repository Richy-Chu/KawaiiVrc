using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using VrcCompanion.ViewModels;

namespace VrcCompanion.Controls;

/// <summary>
/// Hosts WebView2 lazily. Downloads open a Save As dialog so the user chooses
/// each destination, while sign-in stays on the site.
/// </summary>
public partial class CreatorBrowser : UserControl
{
    private CreatorResourcesViewModel? _viewModel;
    private Task? _initialization;
    private bool _isLoaded;

    public CreatorBrowser()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;

        _viewModel = DataContext as CreatorResourcesViewModel;
        if (_viewModel is not null)
        {
            _viewModel.BrowserNavigationRequested += OnBrowserNavigationRequested;
        }

        if (_viewModel?.IsBrowserOpen == true)
        {
            _ = NavigateAsync(_viewModel.BrowserUrl);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        if (_viewModel is not null)
        {
            _viewModel.BrowserNavigationRequested -= OnBrowserNavigationRequested;
        }

        _viewModel = null;
    }

    private void OnBrowserNavigationRequested(string url)
    {
        if (_isLoaded)
        {
            _ = NavigateAsync(url);
        }
    }

    private async Task NavigateAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            await EnsureBrowserAsync();
            if (_isLoaded)
            {
                Browser.Source = uri;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _viewModel?.SetBrowserError(
                "The in-app browser could not start. Install the Microsoft Edge WebView2 Runtime, or use Open site instead.");
        }
    }

    private Task EnsureBrowserAsync()
        => _initialization ??= InitializeBrowserAsync();

    private async Task InitializeBrowserAsync()
    {
        await Browser.EnsureCoreWebView2Async();

        if (Browser.CoreWebView2 is not { } core)
        {
            throw new InvalidOperationException("WebView2 did not initialize.");
        }

        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Browser.CoreWebView2 is not null &&
            Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            e.Handled = true;

            string? selectedPath = null;
            Dispatcher.Invoke(() =>
            {
                var suggestedName = string.IsNullOrWhiteSpace(e.ResultFilePath)
                    ? "download"
                    : Path.GetFileName(e.ResultFilePath);

                var dialog = new SaveFileDialog
                {
                    Title = "Save download",
                    FileName = suggestedName,
                    Filter = "All files (*.*)|*.*",
                    AddExtension = false,
                    OverwritePrompt = true
                };

                var owner = Window.GetWindow(this);
                var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                if (accepted == true)
                {
                    selectedPath = dialog.FileName;
                }
            });

            if (selectedPath is null)
            {
                e.Cancel = true;
                SetDownloadStatus("Download cancelled.");
                return;
            }

            e.ResultFilePath = selectedPath;
            _viewModel?.SetBrowserError(string.Empty);
            SetDownloadStatus($"Downloading {Path.GetFileName(selectedPath)}...");

            var operation = e.DownloadOperation;
            operation.StateChanged += (_, _) => ReportDownloadState(operation);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            e.Cancel = true;
            _viewModel?.SetBrowserError("Could not open the Save As dialog. The download was cancelled.");
            SetDownloadStatus("Download failed: could not open Save As.", true);
        }
    }

    private void ReportDownloadState(CoreWebView2DownloadOperation operation)
    {
        switch (operation.State)
        {
            case CoreWebView2DownloadState.Completed:
                SetDownloadStatus(
                    $"Downloaded to {operation.ResultFilePath}",
                    downloadedPath: operation.ResultFilePath);
                break;
            case CoreWebView2DownloadState.Interrupted
                when operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled:
                SetDownloadStatus("Download cancelled.");
                break;
            case CoreWebView2DownloadState.Interrupted:
                SetDownloadStatus($"Download failed: {Describe(operation.InterruptReason)}.", true);
                break;
        }
    }

    private void SetDownloadStatus(string message, bool isError = false, string? downloadedPath = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => _viewModel?.SetDownloadStatus(message, isError, downloadedPath));
            return;
        }

        _viewModel?.SetDownloadStatus(message, isError, downloadedPath);
    }

    private static string Describe(CoreWebView2DownloadInterruptReason reason)
        => reason switch
        {
            CoreWebView2DownloadInterruptReason.FileAccessDenied => "access was denied",
            CoreWebView2DownloadInterruptReason.FileNoSpace => "the disk is full",
            CoreWebView2DownloadInterruptReason.FileNameTooLong => "the file name is too long",
            CoreWebView2DownloadInterruptReason.FileTooLarge => "the file is too large",
            CoreWebView2DownloadInterruptReason.FileMalicious => "security blocked the file",
            _ => reason.ToString()
        };

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoBack == true)
        {
            Browser.CoreWebView2.GoBack();
        }
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoForward == true)
        {
            Browser.CoreWebView2.GoForward();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Browser.CoreWebView2?.Reload();
    }
}
