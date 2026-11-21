using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
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
public class EmbeddedBrowserSuspensionIntegrationTests
{
    [Fact]
    public void HiddenPageWithAudioIsNotSuspendedAndSuspendsAfterAudioStops()
    {
        WpfTestHost.RunAsync(async () =>
        {
            var browser = new EmbeddedBrowser
            {
                DataContext = new BraveBrowserViewModel()
            };
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
                await browser.InitializeForTestAsync();
                var core = browser.Browser.CoreWebView2
                    ?? throw new InvalidOperationException("WebView2 did not initialize.");

                await NavigateToStringAsync(core, BuildAudioPage());
                await ClickAsync(core, 80, 60);
                await core.ExecuteScriptAsync("document.getElementById('snd').play()");

                var audioStarted = await WaitUntilAsync(
                    () => Task.FromResult(core.IsDocumentPlayingAudio),
                    TimeSpan.FromSeconds(8));
                var audioError = await ReadAsync(core, "window.__audioError || ''");
                Assert.True(audioStarted, $"WebView2 did not report document audio. {audioError}");

                browser.Visibility = Visibility.Collapsed;
                await Task.Delay(TimeSpan.FromMilliseconds(8500));

                Assert.True(core.IsDocumentPlayingAudio, "Audio stopped while the tab was hidden.");
                Assert.False(
                    browser.IsSuspendedForTest,
                    "A hidden page with active audio was suspended.");

                await core.ExecuteScriptAsync(
                    "(() => { const a = document.getElementById('snd');" +
                    " a.pause(); a.removeAttribute('src'); a.load(); return true; })()");
                Assert.True(
                    await WaitUntilAsync(
                        () => Task.FromResult(!core.IsDocumentPlayingAudio),
                        TimeSpan.FromSeconds(3)),
                    "WebView2 still reports audio after the media was stopped.");

                browser.Visibility = Visibility.Visible;
                browser.Visibility = Visibility.Collapsed;
                await Task.Delay(TimeSpan.FromMilliseconds(8500));

                Assert.True(
                    browser.IsSuspendedForTest,
                    "The hidden page did not suspend after audio stopped.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HiddenPageWithActiveDownloadIsNotSuspendedAndSuspendsAfterCancellation()
    {
        WpfTestHost.RunAsync(async () =>
        {
            var tempRoot = Path.Combine(
                Path.GetTempPath(),
                "VrcCompanionTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            using var server = new SlowDownloadServer();
            TestHooks.UseDownloadPathProvider(name =>
                Path.Combine(tempRoot, string.IsNullOrWhiteSpace(name) ? "slow.bin" : name));

            var browser = new EmbeddedBrowser
            {
                DataContext = new BraveBrowserViewModel()
            };
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
                await browser.InitializeForTestAsync();
                var core = browser.Browser.CoreWebView2
                    ?? throw new InvalidOperationException("WebView2 did not initialize.");

                core.Navigate(server.Url);
                Assert.True(
                    await WaitUntilAsync(
                        () => Task.FromResult(browser.HasActiveDownloadsForTest),
                        TimeSpan.FromSeconds(10)),
                    "The WebView2 download did not start.");

                var operation = browser.ActiveDownloadForTest
                    ?? throw new InvalidOperationException("Active download was not tracked.");
                var bytesBeforeHiding = operation.BytesReceived;

                browser.Visibility = Visibility.Collapsed;
                await Task.Delay(TimeSpan.FromMilliseconds(8500));

                Assert.False(
                    browser.IsSuspendedForTest,
                    "A hidden page with an active download was suspended.");
                Assert.Equal(CoreWebView2DownloadState.InProgress, operation.State);
                Assert.True(
                    operation.BytesReceived > bytesBeforeHiding,
                    "The download stopped receiving bytes while the tab was hidden.");

                operation.Cancel();
                Assert.True(
                    await WaitUntilAsync(
                        () => Task.FromResult(!browser.HasActiveDownloadsForTest),
                        TimeSpan.FromSeconds(10)),
                    "The cancelled download was not removed from the active set.");

                browser.Visibility = Visibility.Visible;
                browser.Visibility = Visibility.Collapsed;
                await Task.Delay(TimeSpan.FromMilliseconds(8500));

                Assert.True(
                    browser.IsSuspendedForTest,
                    "The hidden page did not suspend after the download ended.");
            }
            finally
            {
                TestHooks.UseDownloadPathProvider(null);
                window.Close();
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch (IOException)
                {
                    // The WebView may still be releasing the downloaded file.
                }
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

    private static async Task ClickAsync(CoreWebView2 core, int x, int y)
    {
        await core.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new
            {
                type = "mousePressed",
                x,
                y,
                button = "left",
                buttons = 1,
                clickCount = 1
            }));
        await core.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new
            {
                type = "mouseReleased",
                x,
                y,
                button = "left",
                buttons = 0,
                clickCount = 1
            }));
    }

    private static async Task<string> ReadAsync(CoreWebView2 core, string script)
    {
        var result = await core.ExecuteScriptAsync(script);
        return result.Trim('"');
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
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

    private static string BuildAudioPage()
        => $@"<!doctype html>
<html>
<body style='margin:0'>
  <button id='play' style='position:fixed;left:0;top:0;width:220px;height:120px;font-size:24px'>
    Play audio
  </button>
  <audio id='snd' loop src='data:audio/wav;base64,{BuildToneWavBase64()}'></audio>
  <script>
    document.getElementById('play').addEventListener('click', () => {{
      document.getElementById('snd').play()
        .then(() => window.__audioStarted = true)
        .catch(error => window.__audioError = String(error));
    }});
  </script>
</body>
</html>";

    private static string BuildToneWavBase64()
    {
        const int sampleRate = 44100;
        const int seconds = 1;
        const short amplitude = 8000;
        var sampleCount = sampleRate * seconds;
        var dataSize = sampleCount * sizeof(short);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * sizeof(short));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            for (var i = 0; i < sampleCount; i++)
            {
                writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * amplitude));
            }
        }

        return Convert.ToBase64String(stream.ToArray());
    }

    private sealed class SlowDownloadServer : IDisposable
    {
        private const int TotalBytes = 8 * 1024 * 1024;
        private const int ChunkBytes = 64 * 1024;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serverTask;

        public SlowDownloadServer()
        {
            _listener.Start();
            _serverTask = Task.Run(AcceptLoopAsync);
        }

        public string Url
            => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/slow.bin";

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    _ = HandleAsync(client);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var requestBuffer = new byte[4096];
                    await stream.ReadAsync(requestBuffer, _cancellation.Token);

                    var headers =
                        "HTTP/1.1 200 OK\r\n" +
                        $"Content-Length: {TotalBytes}\r\n" +
                        "Content-Type: application/octet-stream\r\n" +
                        "Content-Disposition: attachment; filename=slow.bin\r\n" +
                        "Connection: close\r\n\r\n";
                    await stream.WriteAsync(
                        Encoding.ASCII.GetBytes(headers),
                        _cancellation.Token);

                    var chunk = new byte[ChunkBytes];
                    for (var sent = 0; sent < TotalBytes; sent += ChunkBytes)
                    {
                        var count = Math.Min(ChunkBytes, TotalBytes - sent);
                        await stream.WriteAsync(
                            chunk.AsMemory(0, count),
                            _cancellation.Token);
                        await Task.Delay(100, _cancellation.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                    // The browser cancelled the download.
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                _serverTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
            _cancellation.Dispose();
        }
    }
}
