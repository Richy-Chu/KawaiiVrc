using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class VrChatWebSocketTests
{
    [Fact]
    public void EncodesAPingAsAMaskedControlFrame()
    {
        var frame = VrChatWebSocketFraming.EncodeClientFrame(
            0x9,
            ReadOnlySpan<byte>.Empty,
            new byte[] { 1, 2, 3, 4 });

        Assert.Equal(6, frame.Length);
        Assert.Equal(0x89, frame[0]);
        Assert.Equal(0x80, frame[1]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, frame[2..6]);
    }

    [Fact]
    public void EncodesAndDecodesAMaskedPayload()
    {
        var payload = Encoding.UTF8.GetBytes("ping payload");
        var frame = VrChatWebSocketFraming.EncodeClientFrame(
            0x9,
            payload,
            new byte[] { 9, 8, 7, 6 });

        Assert.True(VrChatWebSocketFraming.TryDecodeFrame(frame, out var decoded, out var consumed));
        Assert.Equal(frame.Length, consumed);
        Assert.True(decoded.Fin);
        Assert.Equal(0x9, decoded.Opcode);
        Assert.Equal(payload, decoded.Payload);
    }

    [Fact]
    public void DecodesExtendedPayloadLengthAndFragments()
    {
        var first = new byte[130];
        first[0] = 0x01;
        first[1] = 0x7E;
        first[2] = 0x00;
        first[3] = 0x7E;
        for (var i = 0; i < 126; i++)
        {
            first[4 + i] = (byte)'a';
        }

        Assert.True(VrChatWebSocketFraming.TryDecodeFrame(first, out var firstFrame, out var firstConsumed));
        Assert.False(firstFrame.Fin);
        Assert.Equal(0x1, firstFrame.Opcode);
        Assert.Equal(130, firstConsumed);
        Assert.Equal(126, firstFrame.Payload.Length);

        var last = new byte[] { 0x80, 0x02, (byte)'b', (byte)'c' };
        Assert.True(VrChatWebSocketFraming.TryDecodeFrame(last, out var lastFrame, out var lastConsumed));
        Assert.True(lastFrame.Fin);
        Assert.Equal(0x0, lastFrame.Opcode);
        Assert.Equal("bc", Encoding.UTF8.GetString(lastFrame.Payload));
        Assert.Equal(last.Length, lastConsumed);
    }

    [Fact]
    public void RejectsAPartialFrameAndAnOversizedLength()
    {
        Assert.False(VrChatWebSocketFraming.TryDecodeFrame(
            new byte[] { 0x81, 0x05, (byte)'a' },
            out _,
            out _));

        var oversized = new byte[] { 0x81, 0x7F, 0, 0, 0, 0, 0x40, 0x00, 0x01, 0x00 };
        Assert.Throws<VrChatWebSocketException>(() =>
            VrChatWebSocketFraming.TryDecodeFrame(oversized, out _, out _));
    }

    [Fact]
    public void ResolvesTheSystemProxyAndHonoursBypassRules()
    {
        var target = new Uri("wss://pipeline.vrchat.cloud/");
        var proxy = new WebProxy("http://127.0.0.1:8888");

        Assert.Equal(
            new Uri("http://127.0.0.1:8888/"),
            VrChatProxyResolver.Resolve(target, proxy));

        var bypass = new WebProxy("http://127.0.0.1:8888")
        {
            BypassList = new[] { "pipeline\\.vrchat\\.cloud" }
        };
        Assert.Null(VrChatProxyResolver.Resolve(target, bypass));
    }

    [Fact]
    public async Task UsesTheHttpProxyTunnelInsteadOfConnectingDirectly()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var request = new StringBuilder();
            var buffer = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }
                request.Append((char)buffer[0]);
            }

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 407 Proxy Authentication Required\r\n\r\n");
            await stream.WriteAsync(response);
            await stream.FlushAsync();
            return request.ToString();
        });

        try
        {
            var proxy = new WebProxy($"http://127.0.0.1:{port}");
            var error = await Assert.ThrowsAsync<VrChatWebSocketException>(() =>
                VrChatPipelineSocket.ConnectAsync(
                    "wss://example.test/",
                    "https://vrchat.com",
                    CancellationToken.None,
                    proxy));

            Assert.Equal(407, error.StatusCode);
            var request = await accepted;
            Assert.StartsWith("CONNECT example.test:443 HTTP/1.1", request, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task QuietSocketStaysConnectedWhenHeartbeatRepliesArrive()
    {
        using var api = new VrcApiClient();
        var sockets = new ConcurrentBag<FakeSocket>();
        using var service = new VrChatRealtimeService(
            api,
            (_, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var socket = new FakeSocket(replyToPings: true);
                sockets.Add(socket);
                return Task.FromResult<IVrChatPipelineSocket>(socket);
            },
            _ => Task.FromResult<string?>("token"),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromSeconds(5));

        service.Start();
        await WaitUntilAsync(() => service.Status.State == VrChatRealtimeState.Connected);

        // The heartbeat is a 20 ms timer, so wait for it to tick instead of
        // assuming the pool fires three of them inside one fixed 400 ms sleep.
        await WaitUntilAsync(() => sockets.Any(candidate => candidate.PingCount >= 3), 5000);
        await service.StopAsync();

        var socket = Assert.Single(sockets);
        Assert.True(socket.PingCount >= 3);
        Assert.True(socket.Disposed);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task SilentSocketTriggersAReconnect()
    {
        using var api = new VrcApiClient();
        var sockets = new ConcurrentBag<FakeSocket>();
        using var service = new VrChatRealtimeService(
            api,
            (_, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var socket = new FakeSocket(replyToPings: false);
                sockets.Add(socket);
                return Task.FromResult<IVrChatPipelineSocket>(socket);
            },
            _ => Task.FromResult<string?>("token"),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(5));

        service.Start();

        // The service reconnects after ~25 ms of silence, so this wait is headroom for a loaded
        // runner only: the 1500 ms it used to have missed once on CI while this passed locally in
        // 85 ms. A broken reconnect never gets here at all, so it still fails the run in ten
        // seconds rather than holding the suite.
        await WaitUntilAsync(() => sockets.Count >= 2, 10_000);
        await service.StopAsync();

        Assert.True(sockets.Count >= 2);
        Assert.Contains(sockets, socket => socket.Disposed);
    }

    [Fact]
    public async Task StartingAgainDisposesThePreviousSocketAndStopWaitsForTheLoop()
    {
        using var api = new VrcApiClient();
        var sockets = new ConcurrentBag<FakeSocket>();
        using var service = new VrChatRealtimeService(
            api,
            (_, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var socket = new FakeSocket(replyToPings: true);
                sockets.Add(socket);
                return Task.FromResult<IVrChatPipelineSocket>(socket);
            },
            _ => Task.FromResult<string?>("token"),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(60),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromSeconds(5));

        service.Start();
        await WaitUntilAsync(() => sockets.Count == 1);
        service.Start();
        await WaitUntilAsync(() => sockets.Count >= 2);
        await service.StopAsync();

        Assert.True(sockets.Count >= 2);
        Assert.True(sockets.All(socket => socket.Disposed));
        Assert.False(service.IsRunning);
    }

    // The service's own timings are milliseconds, so this bound is headroom for a loaded runner
    // and nothing else: a broken wait still fails the run in ten seconds.
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("The realtime service did not reach the expected state.");
            }
            await Task.Delay(10);
        }
    }

    private sealed class FakeSocket : IVrChatPipelineSocket
    {
        private readonly bool _replyToPings;
        private int _pingCount;

        public FakeSocket(bool replyToPings)
        {
            _replyToPings = replyToPings;
            LastInboundAt = DateTimeOffset.UtcNow;
        }

        public DateTimeOffset LastInboundAt { get; private set; }

        public int PingCount => Volatile.Read(ref _pingCount);

        public bool Disposed { get; private set; }

        public Task SendPingAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _pingCount);
            if (_replyToPings)
            {
                LastInboundAt = DateTimeOffset.UtcNow;
            }
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
