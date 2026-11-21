using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using VrcCompanion.Controls;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

[Collection(Seams.Collection)]
[ResetSeams]
public class EmbeddedBrowserAdBlockTests
{
    [Theory]
    [InlineData("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js")]
    [InlineData("https://securepubads.g.doubleclick.net/tag/js/gpt.js")]
    [InlineData("https://www.google-analytics.com/collect")]
    [InlineData("https://cdn.taboola.com/libtrc/loader.js")]
    public void KnownAdAndTrackingHostsAreBlocked(string url)
        => Assert.True(AdBlockList.ShouldBlock(new Uri(url)));

    [Theory]
    [InlineData("https://search.brave.com/")]
    [InlineData("https://discord.com/channels/@me")]
    [InlineData("https://booth.pm/en/")]
    [InlineData("https://vrmodels.store/")]
    [InlineData("https://www.youtube.com/")]
    public void NormalSitesAreNotBlocked(string url)
        => Assert.False(AdBlockList.ShouldBlock(new Uri(url)));

    [Fact]
    public void BundledBraveEngineBlocksTrackersAndReturnsCosmeticRules()
    {
        using var engine = BraveAdBlockEngine.CreateBundled();
        Assert.True(engine is not null, BraveAdBlockEngine.LastCreationError);
        Assert.NotNull(engine);

        Assert.True(engine.ShouldBlock(
            new Uri("https://doubleclick.net/pixel"),
            new Uri("https://example.com/"),
            "image"));
        Assert.True(engine.ShouldBlock(
            new Uri("https://www.facebook.com/tr?id=123"),
            new Uri("https://example.com/"),
            "image"));
        Assert.True(engine.ShouldBlock(
            new Uri("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js"),
            null,
            "document"));
        Assert.False(engine.ShouldBlock(
            new Uri("https://example.com/"),
            new Uri("https://example.com/"),
            "document"));

        var cosmetics = engine.GetCosmeticResources(new Uri("https://example.com/"));
        Assert.NotNull(cosmetics);

        Assert.True(BraveAdBlockEngine.ResourcesLoaded);
        var youtube = engine.GetCosmeticResources(new Uri("https://www.youtube.com/"));
        Assert.NotNull(youtube);
        Assert.False(string.IsNullOrWhiteSpace(youtube.InjectedScript));
        Assert.Contains("json-prune-fetch-response", youtube.InjectedScript);
        Assert.Contains("trusted-replace-xhr-response", youtube.InjectedScript);
        Assert.Contains("ytInitialPlayerResponse", youtube.InjectedScript);

        var spotifyAd = engine.Check(
            new Uri("https://audio-ak-spotify-com.akamaized.net/audio/ad.mp3"),
            new Uri("https://open.spotify.com/"),
            "media");
        Assert.False(string.IsNullOrWhiteSpace(spotifyAd.Redirect));
    }

    [Theory]
    [InlineData("https://audio-ak-spotify-com.akamaized.net/audio/ad.mp3")]
    [InlineData("https://audio-fa.scdn.co/audio/ad.mp3")]
    [InlineData("https://spotifycdn.com/audio/ad.mp3")]
    public void SpotifyAudioAdsAreRedirected(string url)
    {
        using var engine = BraveAdBlockEngine.CreateBundled();
        Assert.NotNull(engine);

        var result = engine.Check(
            new Uri(url),
            new Uri("https://open.spotify.com/"),
            "media");
        Assert.False(string.IsNullOrWhiteSpace(result.Redirect), url);
    }

    [Fact]
    public void EmbeddedBrowserInjectsBraveScriptletsAtDocumentStart()
    {
        WpfTestHost.RunAsync(async () =>
        {
            var browser = new EmbeddedBrowser
            {
                DataContext = new BraveBrowserViewModel(),
                AdBlockEnabled = true
            };
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
                await NavigateUrlAsync(core, "https://www.youtube.com/");

                Assert.Equal(
                    "loading",
                    await ReadAsync(core, "window.__vrcBraveDocumentScript"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void YouTubeScriptletRemovesAdPlacementsBeforeThePageReadsThem()
    {
        WpfTestHost.RunAsync(async () =>
        {
            var folder = Path.Combine(
                Path.GetTempPath(),
                "vrc-youtube-scriptlet-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "youtubei", "v1"));
            File.WriteAllText(
                Path.Combine(folder, "index.html"),
                """
                <!doctype html><html><body>youtube fixture</body></html>
                """);
            File.WriteAllText(
                Path.Combine(folder, "youtubei", "v1", "player"),
                "{\"adPlacements\":[{\"ad\":1}],\"adSlots\":[{\"slot\":1}]," +
                "\"adBreakHeartbeatParams\":\"x\",\"videoDetails\":{\"title\":\"ok\"}}");

            var browser = new EmbeddedBrowser
            {
                DataContext = new BraveBrowserViewModel(),
                AdBlockEnabled = true
            };
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
                core.SetVirtualHostNameToFolderMapping(
                    "www.youtube.com",
                    folder,
                    CoreWebView2HostResourceAccessKind.Allow);

                var navigation = await NavigateUrlAsync(core, "https://www.youtube.com/index.html");
                Assert.True(
                    navigation.IsSuccess,
                    $"YouTube fixture navigation failed: {navigation.WebErrorStatus}; " +
                    await ReadAsync(core, "location.href"));

                await core.ExecuteScriptAsync("""
                    fetch('/youtubei/v1/player?key=x')
                      .then(response => response.text())
                      .then(text => {
                        window.__fetchText = text;
                        window.__fetchDone = 'load';
                      })
                      .catch(error => {
                        window.__fetchError = String(error);
                        window.__fetchDone = 'error';
                      });
                    """);
                Assert.True(
                    await WaitUntilAsync(async () =>
                        await ReadAsync(core, "window.__fetchDone || ''") is "load" or "error"),
                    await ReadAsync(core,
                        "JSON.stringify({marker:window.__vrcBraveDocumentScript,error:window.__fetchError,text:window.__fetchText})"));

                var text = await ReadAsync(core, "window.__fetchText");
                Assert.DoesNotContain("adPlacements", text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("no_ads", text);
            }
            finally
            {
                window.Close();
                try { Directory.Delete(folder, recursive: true); } catch { }
            }
        });
    }

    [Fact]
    public void EmbeddedBrowserBlocksAdHostsAndTheSwitchStopsBlocking()
    {
        WpfTestHost.RunAsync(async () =>
        {
            var browser = new EmbeddedBrowser
            {
                DataContext = new BraveBrowserViewModel(),
                AdBlockEnabled = true
            };
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
                Assert.True(browser.IsAdBlockFilterActive, "The ad-block filter should be installed.");

                await NavigateUrlAsync(
                    core,
                    "https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js" +
                    $"?nonce={Guid.NewGuid():N}");
                Assert.True(
                    await WaitUntilAsync(() => Task.FromResult(
                        browser.BlockedAdBlockRequests > 0)),
                    "The built-in filter did not block the ad host.");

                await NavigateToStringAsync(core,
                    "<html><body><div id='google_ads_test'>ad slot</div></body></html>");
                Assert.True(
                    await WaitUntilAsync(async () =>
                        await ReadAsync(core,
                            "getComputedStyle(document.getElementById('google_ads_test')).display") == "none"),
                    "The cosmetic ad slot was not hidden.");

                var blockedWhileOn = browser.BlockedAdBlockRequests;
                var observedWhileOn = browser.ObservedAdBlockRequests;

                browser.AdBlockEnabled = false;
                Assert.False(browser.IsAdBlockFilterActive);

                Assert.True(
                    await WaitUntilAsync(async () =>
                        await ReadAsync(core,
                            "getComputedStyle(document.getElementById('google_ads_test')).display") != "none"),
                    "Turning the blocker off did not restore the page element.");

                core.Navigate($"https://example.com/?nonce={Guid.NewGuid():N}");
                await Task.Delay(750);

                Assert.Equal(blockedWhileOn, browser.BlockedAdBlockRequests);
                Assert.Equal(observedWhileOn, browser.ObservedAdBlockRequests);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static async Task NavigateToStringAsync(CoreWebView2 core, string html)
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
        var result = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsSuccess, $"The test page failed to load: {result.WebErrorStatus}.");
    }

    private static async Task<CoreWebView2NavigationCompletedEventArgs> NavigateUrlAsync(
        CoreWebView2 core,
        string url)
    {
        var loaded = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigationCompleted(
            object? sender,
            CoreWebView2NavigationCompletedEventArgs e)
        {
            // Scriptlet pages are deliberately cancelled once while their
            // document-start script is registered and then navigated again.
            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            {
                return;
            }

            core.NavigationCompleted -= OnNavigationCompleted;
            loaded.TrySetResult(e);
        }

        core.NavigationCompleted += OnNavigationCompleted;
        core.Navigate(url);
        return await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static async Task<string> ReadAsync(CoreWebView2 core, string script)
    {
        var result = await core.ExecuteScriptAsync(script);
        return result.Trim('"');
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return await condition();
    }
}
