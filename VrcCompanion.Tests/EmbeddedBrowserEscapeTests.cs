using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using VrcCompanion.Controls;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class EmbeddedBrowserEscapeTests
{
    [Fact]
    public void PageNavigationShortcutMovesBrowserHistory()
    {
        WpfTestHost.RunAsync(async () =>
        {
            var host = new BraveBrowserViewModel();
            host.ShowBrowserForLayoutTest();
            host.IsFocusMode = true;
            var browser = new EmbeddedBrowser { DataContext = host };
            var window = new Window
            {
                Content = browser,
                Width = 800,
                Height = 600,
                ShowActivated = false
            };

            try
            {
                window.Show();
                await browser.InitializeForTestAsync();

                var core = browser.Browser.CoreWebView2
                    ?? throw new InvalidOperationException("WebView2 did not initialize.");

                async Task NavigateAsync(string html)
                {
                    var loaded = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnNavigationCompleted(
                        object? sender,
                        CoreWebView2NavigationCompletedEventArgs e)
                    {
                        core.NavigationCompleted -= OnNavigationCompleted;
                        loaded.TrySetResult(e);
                    }

                    core.NavigationCompleted += OnNavigationCompleted;
                    core.NavigateToString(html);
                    var navigation = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.True(
                        navigation.IsSuccess,
                        $"The test page failed to load: {navigation.WebErrorStatus}.");
                }

                await NavigateAsync("<html><body>First page</body></html>");
                await NavigateAsync("<html><body>Second page</body></html>");
                Assert.True(core.CanGoBack, "The second page should have history to go back to.");
                Assert.True(browser.CanGoBack, "The focus toolbar should see that Back is available.");

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline &&
                       !string.Equals(
                           await core.ExecuteScriptAsync("window.__vrcFocusMode === true"),
                           "true",
                           StringComparison.Ordinal))
                {
                    await Task.Delay(25);
                }

                var wentBack = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                void OnBackCompleted(
                    object? sender,
                    CoreWebView2NavigationCompletedEventArgs e)
                {
                    core.NavigationCompleted -= OnBackCompleted;
                    wentBack.TrySetResult(e);
                }

                core.NavigationCompleted += OnBackCompleted;
                await core.ExecuteScriptAsync(
                    "window.dispatchEvent(new KeyboardEvent('keydown', " +
                    "{ key: 'ArrowLeft', altKey: true, bubbles: true, cancelable: true }));");
                var back = await wentBack.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(back.IsSuccess, $"The Back navigation failed: {back.WebErrorStatus}.");
                Assert.True(core.CanGoForward, "Back should leave a forward history entry.");
                Assert.True(browser.CanGoForward, "The focus toolbar should see that Forward is available.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeFromPageContentExitsFocusMode()
    {
        WpfTestHost.RunAsync(async () =>
        {
            var host = new BraveBrowserViewModel();
            host.ShowBrowserForLayoutTest();
            host.IsFocusMode = true;
            var browser = new EmbeddedBrowser { DataContext = host };
            var window = new Window
            {
                Content = browser,
                Width = 800,
                Height = 600,
                ShowActivated = false
            };

            try
            {
                window.Show();
                await browser.InitializeForTestAsync();

                var core = browser.Browser.CoreWebView2
                    ?? throw new InvalidOperationException("WebView2 did not initialize.");
                var loaded = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                void OnNavigationCompleted(
                    object? sender,
                    CoreWebView2NavigationCompletedEventArgs e)
                {
                    core.NavigationCompleted -= OnNavigationCompleted;
                    loaded.TrySetResult(e);
                }

                core.NavigationCompleted += OnNavigationCompleted;
                core.NavigateToString("<html><body><button>Page content</button></body></html>");
                var navigation = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(
                    navigation.IsSuccess,
                    $"The test page failed to load: {navigation.WebErrorStatus}.");

                await core.ExecuteScriptAsync(
                    "window.dispatchEvent(new KeyboardEvent('keydown', " +
                    "{ key: 'Escape', bubbles: true, cancelable: true }));");

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (host.IsFocusMode && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(25);
                }

                Assert.False(host.IsFocusMode, "Escape inside the page should exit focus mode.");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
