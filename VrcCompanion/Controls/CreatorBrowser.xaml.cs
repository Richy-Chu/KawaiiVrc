using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using VrcCompanion.ViewModels;

namespace VrcCompanion.Controls;

/// <summary>
/// Hosts WebView2 lazily. Downloads use WebView2's normal download UI, so the
/// user still chooses each file and signs in on the site.
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
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        AttachViewModel();

        if (_viewModel?.IsBrowserOpen == true)
        {
            _ = NavigateAsync(_viewModel.BrowserUrl);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        DetachViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_isLoaded)
        {
            AttachViewModel();
        }
    }

    private void AttachViewModel()
    {
        var viewModel = DataContext as CreatorResourcesViewModel;
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        DetachViewModel();
        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.BrowserNavigationRequested += OnBrowserNavigationRequested;
        }
    }

    private void DetachViewModel()
    {
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
        core.NavigationCompleted += OnNavigationCompleted;
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

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _viewModel?.SetBrowserError(string.Empty);
        }
        else
        {
            _viewModel?.SetBrowserError("This page could not be loaded. You can try again or use Open site.");
        }
    }

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
