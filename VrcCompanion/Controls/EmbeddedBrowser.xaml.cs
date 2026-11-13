using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using VrcCompanion.ViewModels;

namespace VrcCompanion.Controls;

/// <summary>
/// Hosts WebView2 lazily. The owner keeps one cached instance per tab, so the
/// page and any active call stay alive while the tab is hidden.
/// </summary>
public partial class EmbeddedBrowser : UserControl, IEmbeddedBrowserAutomation
{
    public static readonly DependencyProperty CanGoBackProperty = DependencyProperty.Register(
        nameof(CanGoBack),
        typeof(bool),
        typeof(EmbeddedBrowser),
        new PropertyMetadata(false));

    public static readonly DependencyProperty CanGoForwardProperty = DependencyProperty.Register(
        nameof(CanGoForward),
        typeof(bool),
        typeof(EmbeddedBrowser),
        new PropertyMetadata(false));

    private const string FocusShortcutScript = """
        window.__vrcFocusMode = false;
        window.addEventListener('keydown', (event) => {
          if (event.key === 'Escape') {
            window.chrome.webview.postMessage('browser-focus-escape');
            return;
          }

          if (window.__vrcFocusMode !== true) return;

          if (event.altKey && event.key === 'ArrowLeft') {
            event.preventDefault();
            window.chrome.webview.postMessage('browser-focus-back');
          } else if (event.altKey && event.key === 'ArrowRight') {
            event.preventDefault();
            window.chrome.webview.postMessage('browser-focus-forward');
          } else if (event.key === 'F5' || (event.ctrlKey && event.key.toLowerCase() === 'r')) {
            event.preventDefault();
            window.chrome.webview.postMessage('browser-focus-refresh');
          }
        }, true);
        """;

    private IEmbeddedBrowserHost? _viewModel;
    private Task? _initialization;
    private string? _pendingUrl;
    private Window? _window;
    private bool _disposed;

    public EmbeddedBrowser()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public bool CanGoBack
    {
        get => (bool)GetValue(CanGoBackProperty);
        private set => SetValue(CanGoBackProperty, value);
    }

    public bool CanGoForward
    {
        get => (bool)GetValue(CanGoForwardProperty);
        private set => SetValue(CanGoForwardProperty, value);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => UpdateMemoryTargetLevel();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _viewModel = DataContext as IEmbeddedBrowserHost;
        if (_viewModel is not null)
        {
            _viewModel.BrowserNavigationRequested += OnBrowserNavigationRequested;
            if (_viewModel is INotifyPropertyChanged notifier)
            {
                notifier.PropertyChanged += OnViewModelPropertyChanged;
            }
            if (_viewModel is IEmbeddedBrowserAutomationHost automationHost)
            {
                automationHost.Automation = this;
            }

            if (_viewModel.IsBrowserOpen)
            {
                _pendingUrl = _viewModel.BrowserUrl;
            }
        }

        if (_window is null)
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
            {
                _window.Closed += OnWindowClosed;
                _window.StateChanged += OnWindowStateChanged;
            }
        }

        _ = NavigatePendingAsync();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window.StateChanged -= OnWindowStateChanged;
            _window = null;
        }

        DisposeBrowser();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
        => UpdateMemoryTargetLevel();

    private void OnBrowserNavigationRequested(string url)
    {
        _pendingUrl = url;
        if (IsLoaded && !_disposed)
        {
            _ = NavigatePendingAsync();
        }
    }

    private async Task NavigatePendingAsync()
    {
        if (_pendingUrl is not { } url ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            await EnsureBrowserAsync();
            if (_disposed || !IsLoaded ||
                !string.Equals(_pendingUrl, url, StringComparison.Ordinal))
            {
                return;
            }

            Browser.Source = uri;
            _pendingUrl = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _viewModel?.SetBrowserError(
                "The in-app browser could not start. Install the Microsoft Edge WebView2 Runtime, or use Open site instead.");
        }
    }

    internal Task InitializeForTestAsync()
        => EnsureBrowserAsync();

    private Task EnsureBrowserAsync()
        => _initialization ??= InitializeBrowserAsync();

    private async Task<CoreWebView2?> GetCoreAsync()
    {
        if (_disposed)
        {
            return null;
        }

        await EnsureBrowserAsync();
        return _disposed ? null : Browser.CoreWebView2;
    }

    public async Task SendKeyAsync(
        string key,
        string code,
        int virtualKeyCode,
        bool control = false,
        bool shift = false)
    {
        if (await GetCoreAsync() is not { } core)
        {
            return;
        }

        try
        {
            Browser.Focus();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        var modifiers = (control ? 2 : 0) | (shift ? 8 : 0);
        await core.CallDevToolsProtocolMethodAsync(
            "Input.dispatchKeyEvent",
            BuildKeyEvent("keyDown", key, code, virtualKeyCode, modifiers));
        await core.CallDevToolsProtocolMethodAsync(
            "Input.dispatchKeyEvent",
            BuildKeyEvent("keyUp", key, code, virtualKeyCode, modifiers));
    }

    public async Task InsertTextAsync(string text)
    {
        if (await GetCoreAsync() is not { } core)
        {
            return;
        }

        await core.CallDevToolsProtocolMethodAsync(
            "Input.insertText",
            JsonSerializer.Serialize(new { text }));
    }

    public async Task<string> ExecuteScriptAsync(string script)
    {
        if (await GetCoreAsync() is not { } core)
        {
            return string.Empty;
        }

        return await core.ExecuteScriptAsync(script);
    }

    private static string BuildKeyEvent(
        string type,
        string key,
        string code,
        int virtualKeyCode,
        int modifiers)
        => JsonSerializer.Serialize(new
        {
            type,
            modifiers,
            key,
            code,
            windowsVirtualKeyCode = virtualKeyCode,
            nativeVirtualKeyCode = virtualKeyCode
        });

    private void DisposeBrowser()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initialization = null;

        if (_viewModel is not null)
        {
            _viewModel.BrowserNavigationRequested -= OnBrowserNavigationRequested;
            if (_viewModel is INotifyPropertyChanged notifier)
            {
                notifier.PropertyChanged -= OnViewModelPropertyChanged;
            }
            if (_viewModel is IEmbeddedBrowserAutomationHost automationHost)
            {
                automationHost.Automation = null;
            }

            _viewModel = null;
        }

        try
        {
            if (Browser.CoreWebView2 is { } core)
            {
                core.NewWindowRequested -= OnNewWindowRequested;
                core.DownloadStarting -= OnDownloadStarting;
                core.SourceChanged -= OnSourceChanged;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.DocumentTitleChanged -= OnDocumentTitleChanged;
                core.WebMessageReceived -= OnWebMessageReceived;
            }

            Browser.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

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
        core.SourceChanged += OnSourceChanged;
        core.NavigationCompleted += OnNavigationCompleted;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.WebMessageReceived += OnWebMessageReceived;
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(FocusShortcutScript);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        UpdateNavigationState();
        await SetPageFocusModeAsync(_viewModel?.IsFocusMode == true);
        UpdateMemoryTargetLevel();
    }

    // Collapsed tabs and a minimized window keep their page and call alive while
    // Chromium releases memory. TrySuspendAsync would stop the call.
    private void UpdateMemoryTargetLevel()
    {
        if (_disposed || Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            core.MemoryUsageTargetLevel =
                !IsVisible || _window?.WindowState == WindowState.Minimized
                    ? CoreWebView2MemoryUsageTargetLevel.Low
                    : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        RememberCurrentLocation();
        UpdateNavigationState();
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        UpdateNavigationState();
        _ = SetPageFocusModeAsync(_viewModel?.IsFocusMode == true);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IEmbeddedBrowserHost.IsFocusMode))
        {
            _ = SetPageFocusModeAsync(_viewModel?.IsFocusMode == true);
        }
    }

    private async Task SetPageFocusModeAsync(bool focused)
    {
        if (Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            await core.ExecuteScriptAsync(
                $"window.__vrcFocusMode = {focused.ToString().ToLowerInvariant()};");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void UpdateNavigationState()
    {
        var core = Browser.CoreWebView2;
        CanGoBack = core?.CanGoBack == true;
        CanGoForward = core?.CanGoForward == true;
    }

    private void OnDocumentTitleChanged(object? sender, object e)
        => RememberCurrentLocation();

    private void RememberCurrentLocation()
    {
        if (Browser.CoreWebView2 is { Source: { Length: > 0 } source } core)
        {
            _viewModel?.SetBrowserLocation(source, core.DocumentTitle);
        }
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

    private async void FocusAddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (!TryBuildAddressUri(FocusAddressBox.Text, out var uri))
        {
            _viewModel?.SetBrowserError("Enter a valid http or https address.");
            return;
        }

        try
        {
            if (await GetCoreAsync() is not { } core)
            {
                _viewModel?.SetBrowserError("The in-app browser could not start.");
                return;
            }

            _viewModel?.SetBrowserError(string.Empty);
            core.Navigate(uri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _viewModel?.SetBrowserError("The in-app browser could not navigate to that address.");
        }
    }

    internal static bool TryBuildAddressUri(string? input, out Uri uri)
    {
        uri = null!;
        var text = input?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var candidate) ||
            (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    public void GoBack()
    {
        if (Browser.CoreWebView2?.CanGoBack == true)
        {
            Browser.CoreWebView2.GoBack();
        }
    }

    public void GoForward()
    {
        if (Browser.CoreWebView2?.CanGoForward == true)
        {
            Browser.CoreWebView2.GoForward();
        }
    }

    public void Reload()
    {
        Browser.CoreWebView2?.Reload();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
        => GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e)
        => GoForward();

    private void Refresh_Click(object sender, RoutedEventArgs e)
        => Reload();

    private void ExitFocus_Click(object sender, RoutedEventArgs e)
        => ExitFocus();

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_viewModel?.IsFocusMode != true)
        {
            return;
        }

        try
        {
            switch (e.TryGetWebMessageAsString())
            {
                case "browser-focus-escape":
                    ExitFocus();
                    break;
                case "browser-focus-back":
                    GoBack();
                    break;
                case "browser-focus-forward":
                    GoForward();
                    break;
                case "browser-focus-refresh":
                    Reload();
                    break;
            }
        }
        catch (InvalidOperationException)
        {
            // The page posted a non-string message; it is not one of our shortcuts.
        }
    }

    private void ExitFocus()
    {
        if (_viewModel?.IsFocusMode == true)
        {
            _viewModel.IsFocusMode = false;
        }
    }
}
