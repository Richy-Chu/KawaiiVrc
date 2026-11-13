using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using VrcCompanion.Controls;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class EmbeddedBrowserAddressTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("example.com/path?q=1", "https://example.com/path?q=1")]
    [InlineData("http://example.com/", "http://example.com/")]
    [InlineData("HTTPS://example.com/", "https://example.com/")]
    [InlineData("ftp://example.com/", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void AddressBarAcceptsOnlyHttpAndHttps(string input, string? expected)
    {
        var accepted = EmbeddedBrowser.TryBuildAddressUri(input, out var uri);

        Assert.Equal(expected is not null, accepted);
        if (expected is not null)
        {
            Assert.Equal(expected, uri.AbsoluteUri);
        }
    }

    [Fact]
    public void InvalidAddressLeavesCurrentPageUnchanged()
    {
        WpfTestHost.RunAsync(async () =>
        {
            var host = new BraveBrowserViewModel { IsFocusMode = true };
            var browser = new EmbeddedBrowser { DataContext = host };
            var window = new Window
            {
                Content = browser,
                Width = 900,
                Height = 600,
                ShowActivated = false
            };

            try
            {
                window.Show();
                window.UpdateLayout();
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
                core.NavigateToString(
                    "<html><head><title>Current page</title></head><body>Still here</body></html>");
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));

                var address = browser.FindName("FocusAddressBox") as TextBox
                    ?? throw new InvalidOperationException("Focus address box was not found.");
                var sourceBefore = core.Source;
                var titleBefore = core.DocumentTitle;
                var browserUrlBefore = host.BrowserUrl;

                address.Text = "ftp://example.com/";
                var enter = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!,
                    0,
                    Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                };
                address.RaiseEvent(enter);

                Assert.True(enter.Handled, "Enter should still be handled for invalid input.");
                Assert.Equal("Enter a valid http or https address.", host.BrowserError);
                Assert.Equal(sourceBefore, core.Source);
                Assert.Equal(titleBefore, core.DocumentTitle);
                Assert.Equal(browserUrlBefore, host.BrowserUrl);
                Assert.True(host.IsFocusMode, "Invalid input should not leave focus mode.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FocusAddressBarNavigatesWithoutLeavingFocusMode()
    {
        WpfTestHost.RunAsync(async () =>
        {
            var siteRoot = Path.Combine(
                Path.GetTempPath(),
                "VrcCompanionAddressTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(siteRoot);
            File.WriteAllText(
                Path.Combine(siteRoot, "index.html"),
                "<html><head><title>Address probe</title></head><body>Loaded</body></html>");

            var host = new BraveBrowserViewModel { IsFocusMode = true };
            var browser = new EmbeddedBrowser { DataContext = host };
            var window = new Window
            {
                Content = browser,
                Width = 900,
                Height = 600,
                ShowActivated = false
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                await browser.InitializeForTestAsync();

                var core = browser.Browser.CoreWebView2
                    ?? throw new InvalidOperationException("WebView2 did not initialize.");
                core.SetVirtualHostNameToFolderMapping(
                    "vrc-address.test",
                    siteRoot,
                    CoreWebView2HostResourceAccessKind.Allow);

                var address = browser.FindName("FocusAddressBox") as TextBox
                    ?? throw new InvalidOperationException("Focus address box was not found.");
                Assert.True(address.IsVisible, "The address box should be visible in focus mode.");

                address.Text = "vrc-address.test/index.html";
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
                var enter = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!,
                    0,
                    Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                };
                address.RaiseEvent(enter);

                Assert.True(enter.Handled, "Enter should submit the focus address bar.");
                var navigation = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(
                    navigation.IsSuccess,
                    $"The address-bar navigation failed: {navigation.WebErrorStatus}.");
                Assert.Equal("Address probe", core.DocumentTitle);
                Assert.Equal("https://vrc-address.test/index.html", host.BrowserUrl);
                Assert.True(host.IsFocusMode, "Address navigation should stay in focus mode.");
            }
            finally
            {
                window.Close();
                try
                {
                    Directory.Delete(siteRoot, recursive: true);
                }
                catch (IOException)
                {
                    // WebView may still hold the mapped folder briefly.
                }
            }
        });
    }
}
