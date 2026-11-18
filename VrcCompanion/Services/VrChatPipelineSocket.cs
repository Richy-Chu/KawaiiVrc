using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VrcCompanion.Services;

public sealed class VrChatWebSocketException : Exception
{
    public int? StatusCode { get; }

    public VrChatWebSocketException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

internal interface IVrChatPipelineSocket : IAsyncDisposable
{
    DateTimeOffset LastInboundAt { get; }

    Task SendPingAsync(CancellationToken cancellationToken);

    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);
}

internal readonly record struct VrChatWebSocketFrame(bool Fin, byte Opcode, byte[] Payload);

/// <summary>
/// RFC 6455 framing kept separate from the network stream so frame and
/// ping/pong behavior can be tested without a live VRChat connection.
/// </summary>
internal static class VrChatWebSocketFraming
{
    public const int MaximumMessageBytes = 4 * 1024 * 1024;

    public static byte[] EncodeClientFrame(
        byte opcode,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> mask = default)
    {
        if (payload.Length > MaximumMessageBytes)
        {
            throw new VrChatWebSocketException("VRChat realtime payload is too large.");
        }

        var headerLength = payload.Length < 126 ? 2 : payload.Length <= ushort.MaxValue ? 4 : 10;
        var frame = new byte[headerLength + 4 + payload.Length];
        frame[0] = (byte)(0x80 | opcode);
        if (payload.Length < 126)
        {
            frame[1] = (byte)(0x80 | payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            frame[1] = 0x80 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)payload.Length);
        }
        else
        {
            frame[1] = 0x80 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)payload.Length);
        }

        var maskOffset = headerLength;
        if (mask.Length == 0)
        {
            RandomNumberGenerator.Fill(frame.AsSpan(maskOffset, 4));
        }
        else
        {
            if (mask.Length != 4)
            {
                throw new ArgumentException("A websocket mask must contain four bytes.", nameof(mask));
            }
            mask.CopyTo(frame.AsSpan(maskOffset, 4));
        }

        for (var index = 0; index < payload.Length; index++)
        {
            frame[maskOffset + 4 + index] =
                (byte)(payload[index] ^ frame[maskOffset + (index % 4)]);
        }

        return frame;
    }

    public static bool TryDecodeFrame(
        ReadOnlySpan<byte> data,
        out VrChatWebSocketFrame frame,
        out int consumed)
    {
        frame = default;
        consumed = 0;
        if (data.Length < 2)
        {
            return false;
        }

        var fin = (data[0] & 0x80) != 0;
        var opcode = (byte)(data[0] & 0x0F);
        var masked = (data[1] & 0x80) != 0;
        long length = data[1] & 0x7F;
        var offset = 2;

        if (length == 126)
        {
            if (data.Length < offset + 2)
            {
                return false;
            }
            length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
        }
        else if (length == 127)
        {
            if (data.Length < offset + 8)
            {
                return false;
            }
            var wide = BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);
            if (wide > MaximumMessageBytes)
            {
                throw new VrChatWebSocketException("VRChat realtime frame is too large.");
            }
            length = (long)wide;
            offset += 8;
        }

        if (length < 0 || length > MaximumMessageBytes)
        {
            throw new VrChatWebSocketException("VRChat realtime frame has an invalid length.");
        }

        var mask = new byte[4];
        if (masked)
        {
            if (data.Length < offset + 4)
            {
                return false;
            }
            data.Slice(offset, 4).CopyTo(mask);
            offset += 4;
        }

        var total = offset + (int)length;
        if (data.Length < total)
        {
            return false;
        }

        var payload = data.Slice(offset, (int)length).ToArray();
        if (masked)
        {
            for (var index = 0; index < payload.Length; index++)
            {
                payload[index] ^= mask[index % 4];
            }
        }

        frame = new VrChatWebSocketFrame(fin, opcode, payload);
        consumed = total;
        return true;
    }
}

/// <summary>
/// Resolves the same HTTP proxy the app's HttpClient uses. The raw websocket
/// transport must not silently bypass a system proxy.
/// </summary>
internal static class VrChatProxyResolver
{
    public static Uri? Resolve(Uri target, IWebProxy? proxy)
    {
        if (proxy is null || proxy.IsBypassed(target))
        {
            return null;
        }

        Uri? resolved;
        try
        {
            resolved = proxy.GetProxy(target);
        }
        catch (Exception)
        {
            return null;
        }

        if (resolved is null ||
            (resolved.Scheme == target.Scheme &&
             resolved.Host == target.Host &&
             resolved.Port == target.Port))
        {
            return null;
        }

        return resolved;
    }
}

internal static class VrChatProxyTunnel
{
    public static async Task ConnectAsync(
        Stream stream,
        Uri proxy,
        Uri target,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(proxy.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new VrChatWebSocketException(
                $"The configured proxy uses unsupported scheme {proxy.Scheme}; only HTTP proxies are supported.");
        }

        var authority = $"{target.Host}:{(target.IsDefaultPort ? 443 : target.Port)}";
        var request = new StringBuilder()
            .Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(authority).Append("\r\n")
            .Append("Proxy-Connection: Keep-Alive\r\n");

        var credentials = proxy.UserInfo.Length > 0
            ? proxy.UserInfo
            : string.Empty;
        if (credentials.Length > 0)
        {
            request.Append("Proxy-Authorization: Basic ")
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    Uri.UnescapeDataString(credentials))))
                .Append("\r\n");
        }

        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), cancellationToken)
            .ConfigureAwait(false);

        var headers = await VrChatPipelineSocket.ReadHeadersAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        var status = VrChatPipelineSocket.ParseStatusCode(headers);
        if (status != 200)
        {
            throw new VrChatWebSocketException(
                $"Proxy CONNECT failed ({status}).",
                status);
        }
    }
}

/// <summary>
/// Minimal RFC 6455 client used for the VRChat pipeline. The built-in
/// ClientWebSocket hides ping/pong frames, which makes it impossible to prove a
/// quiet connection is still alive. This transport exposes those control frames
/// so the service can keep a real liveness deadline.
/// </summary>
internal sealed class VrChatPipelineSocket : IVrChatPipelineSocket
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpClient _tcp;
    private readonly SslStream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    private VrChatPipelineSocket(TcpClient tcp, SslStream stream)
    {
        _tcp = tcp;
        _stream = stream;
        LastInboundAt = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset LastInboundAt { get; private set; }

    internal static async Task<VrChatPipelineSocket> ConnectAsync(
        string url,
        string origin,
        CancellationToken cancellationToken,
        IWebProxy? proxy = null)
    {
        var uri = new Uri(url);
        if (!string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            throw new VrChatWebSocketException("VRChat realtime requires a wss:// URL.");
        }

        var proxyUri = VrChatProxyResolver.Resolve(
            uri,
            proxy ?? HttpClient.DefaultProxy);
        var tcp = new TcpClient();
        try
        {
            if (proxyUri is null)
            {
                await tcp.ConnectAsync(
                    uri.Host,
                    uri.IsDefaultPort ? 443 : uri.Port,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await tcp.ConnectAsync(
                    proxyUri.Host,
                    proxyUri.IsDefaultPort ? 80 : proxyUri.Port,
                    cancellationToken).ConfigureAwait(false);
            }

            Stream network = tcp.GetStream();
            if (proxyUri is not null)
            {
                await VrChatProxyTunnel.ConnectAsync(network, proxyUri, uri, cancellationToken)
                    .ConfigureAwait(false);
            }

            var ssl = new SslStream(network, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = uri.Host,
                    ApplicationProtocols = new List<SslApplicationProtocol>
                    {
                        SslApplicationProtocol.Http11
                    }
                },
                cancellationToken).ConfigureAwait(false);

            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var request =
                $"GET {uri.PathAndQuery} HTTP/1.1\r\n" +
                $"Host: {uri.Host}\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {key}\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                $"Origin: {origin}\r\n" +
                "User-Agent: VrcCompanion/1.0.0 (unofficial Windows companion)\r\n" +
                "\r\n";
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken).ConfigureAwait(false);

            var headers = await ReadHeadersAsync(ssl, cancellationToken).ConfigureAwait(false);
            var status = ParseStatusCode(headers);
            if (status != 101)
            {
                throw new VrChatWebSocketException(
                    $"VRChat realtime upgrade failed ({status}).",
                    status);
            }

            var expectedAccept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
            var accept = HeaderValue(headers, "Sec-WebSocket-Accept");
            if (!string.Equals(accept, expectedAccept, StringComparison.Ordinal))
            {
                throw new VrChatWebSocketException("VRChat realtime returned an invalid websocket handshake.");
            }

            return new VrChatPipelineSocket(tcp, ssl);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public async Task SendPingAsync(CancellationToken cancellationToken)
        => await SendFrameAsync(0x9, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);

    /// <summary>Returns one complete text message, or null when the peer closes.</summary>
    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var isText = false;
        while (true)
        {
            var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            LastInboundAt = DateTimeOffset.UtcNow;

            switch (frame.Opcode)
            {
                case 0x8:
                    return null;
                case 0x9:
                    await SendFrameAsync(0xA, frame.Payload, cancellationToken).ConfigureAwait(false);
                    continue;
                case 0xA:
                    continue;
                case 0x1:
                case 0x2:
                    isText = frame.Opcode == 0x1;
                    message.SetLength(0);
                    message.Write(frame.Payload);
                    break;
                case 0x0:
                    message.Write(frame.Payload);
                    break;
                default:
                    continue;
            }

            if (!frame.Fin)
            {
                continue;
            }

            if (!isText)
            {
                message.SetLength(0);
                continue;
            }

            return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    private async Task SendFrameAsync(
        byte opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VrChatPipelineSocket));
        }

        var frame = VrChatWebSocketFraming.EncodeClientFrame(opcode, payload.Span);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<VrChatWebSocketFrame> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var first = new byte[2];
        await ReadExactlyAsync(first, cancellationToken).ConfigureAwait(false);
        var fin = (first[0] & 0x80) != 0;
        var opcode = (byte)(first[0] & 0x0F);
        var masked = (first[1] & 0x80) != 0;
        long length = first[1] & 0x7F;

        if (length == 126)
        {
            var extended = new byte[2];
            await ReadExactlyAsync(extended, cancellationToken).ConfigureAwait(false);
            length = BinaryPrimitives.ReadUInt16BigEndian(extended);
        }
        else if (length == 127)
        {
            var extended = new byte[8];
            await ReadExactlyAsync(extended, cancellationToken).ConfigureAwait(false);
            var value = BinaryPrimitives.ReadUInt64BigEndian(extended);
            if (value > VrChatWebSocketFraming.MaximumMessageBytes)
            {
                throw new VrChatWebSocketException("VRChat realtime frame is too large.");
            }
            length = (long)value;
        }

        if (length < 0 || length > VrChatWebSocketFraming.MaximumMessageBytes)
        {
            throw new VrChatWebSocketException("VRChat realtime frame has an invalid length.");
        }

        var mask = new byte[4];
        if (masked)
        {
            await ReadExactlyAsync(mask, cancellationToken).ConfigureAwait(false);
        }

        var payload = new byte[(int)length];
        await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        if (masked)
        {
            for (var index = 0; index < payload.Length; index++)
            {
                payload[index] ^= mask[index % 4];
            }
        }

        return new VrChatWebSocketFrame(fin, opcode, payload);
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new VrChatWebSocketException("VRChat realtime connection ended.");
            }
            offset += read;
        }
    }

    internal static async Task<string> ReadHeadersAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];
        while (builder.Length < 32 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new VrChatWebSocketException("VRChat realtime handshake ended unexpectedly.");
            }
            builder.Append((char)buffer[0]);
            if (builder.Length >= 4 &&
                builder[^4] == '\r' &&
                builder[^3] == '\n' &&
                builder[^2] == '\r' &&
                builder[^1] == '\n')
            {
                return builder.ToString();
            }
        }

        throw new VrChatWebSocketException("VRChat realtime handshake headers are too large.");
    }

    internal static int ParseStatusCode(string headers)
    {
        var lineEnd = headers.IndexOf("\r\n", StringComparison.Ordinal);
        var statusLine = lineEnd < 0 ? headers : headers[..lineEnd];
        var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out var status) ? status : 0;
    }

    private static string? HeaderValue(string headers, string name)
    {
        foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 ||
                !line[..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(separator + 1)..].Trim();
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        _tcp.Dispose();
        _writeGate.Dispose();
    }
}
