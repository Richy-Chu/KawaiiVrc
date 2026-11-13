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
    public void EscapeFromPageContentExitsFocusMode()
    {
        WpfTestHost.RunAsync(async () =>
        {
            var host = new TestBrowserHost { IsFocusMode = true };
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

    private sealed class TestBrowserHost : IEmbeddedBrowserHost
    {
        public event Action<string>? BrowserNavigationRequested
        {
            add { }
            remove { }
        }

        public bool IsBrowserOpen => false;

        public bool IsFocusMode { get; set; }

        public string BrowserUrl => "about:blank";

        public void SetBrowserLocation(string url, string title)
        {
        }

        public void SetBrowserError(string message)
        {
        }

        public void SetDownloadStatus(string message, bool isError = false, string? downloadedPath = null)
        {
        }
    }
}
