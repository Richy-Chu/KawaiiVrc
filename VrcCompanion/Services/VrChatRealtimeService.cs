using System.Diagnostics;
using System.Text.Json;

namespace VrcCompanion.Services;

public enum VrChatRealtimeState
{
    Idle,
    Connecting,
    Connected,
    Disconnected,
    AuthFailure
}

public sealed record VrChatRealtimeStatus(VrChatRealtimeState State, string? Reason = null);

public static class VrChatRealtimeLiveness
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    /// <summary>
    /// A connection is dead only after three heartbeat intervals without any
    /// inbound frame, including pong frames. Quiet pipelines stay connected.
    /// </summary>
    public static bool IsExpired(
        DateTimeOffset lastInboundAt,
        DateTimeOffset now,
        TimeSpan? deadline = null)
        => now - lastInboundAt >= (deadline ?? Deadline);
}

/// <summary>
/// One VRChat pipeline message. The pipeline sends <c>content</c> either as a
/// JSON object or as a JSON-encoded string; both are normalized to an object
/// here, matching VRCX-0's realtime message parser.
/// </summary>
public sealed record VrChatRealtimeEvent(
    string Type,
    JsonElement Content,
    string Raw,
    DateTimeOffset ReceivedAt);

public static class VrChatRealtimeMessage
{
    public static bool TryParse(string raw, DateTimeOffset receivedAt, out VrChatRealtimeEvent? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var type = typeElement.GetString() ?? string.Empty;
            if (type.Length == 0)
            {
                return false;
            }

            JsonElement content;
            if (root.TryGetProperty("content", out var rawContent))
            {
                content = NormalizeContent(rawContent);
            }
            else
            {
                content = JsonSerializer.SerializeToElement(new { });
            }

            result = new VrChatRealtimeEvent(type, content.Clone(), raw, receivedAt);
            return true;
        }
    }

    private static JsonElement NormalizeContent(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.String)
        {
            return content;
        }

        var text = content.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return content;
        }

        try
        {
            using var nested = JsonDocument.Parse(text);
            return nested.RootElement.Clone();
        }
        catch (JsonException)
        {
            return content;
        }
    }
}

/// <summary>
/// VRChat's realtime pipeline. This is an independent C# implementation of the
/// transport VRCX-0 uses: authenticated websocket, JSON event parsing, and a
/// reconnect loop with exponential backoff. It is a presence/event source only;
/// the VRChat log remains the source of truth for instance history.
/// </summary>
public sealed class VrChatRealtimeService : IDisposable
{
    public const string DefaultEndpoint = "wss://pipeline.vrchat.cloud";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StableConnection = TimeSpan.FromSeconds(60);

    private readonly VrcApiClient _api;
    private readonly Func<string, string, CancellationToken, Task<IVrChatPipelineSocket>> _socketFactory;
    private readonly Func<CancellationToken, Task<string?>> _tokenProvider;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _deadline;
    private readonly TimeSpan _initialReconnectDelay;
    private readonly TimeSpan _maximumReconnectDelay;
    private readonly TimeSpan _stableConnection;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private Task? _retired;
    private string _lastRaw = string.Empty;
    private long _generation;
    private bool _disposed;

    public VrChatRealtimeService(VrcApiClient api)
        : this(
            api,
            async (url, origin, cancellationToken) =>
                await VrChatPipelineSocket.ConnectAsync(url, origin, cancellationToken)
                    .ConfigureAwait(false),
            null)
    {
    }

    internal VrChatRealtimeService(
        VrcApiClient api,
        Func<string, string, CancellationToken, Task<IVrChatPipelineSocket>> socketFactory,
        Func<CancellationToken, Task<string?>>? tokenProvider = null,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? deadline = null,
        TimeSpan? initialReconnectDelay = null,
        TimeSpan? maximumReconnectDelay = null,
        TimeSpan? stableConnection = null)
    {
        _api = api;
        _socketFactory = socketFactory;
        _tokenProvider = tokenProvider ?? ResolveTokenFromApiAsync;
        _heartbeatInterval = heartbeatInterval ?? VrChatRealtimeLiveness.HeartbeatInterval;
        _deadline = deadline ?? VrChatRealtimeLiveness.Deadline;
        _initialReconnectDelay = initialReconnectDelay ?? InitialReconnectDelay;
        _maximumReconnectDelay = maximumReconnectDelay ?? MaximumReconnectDelay;
        _stableConnection = stableConnection ?? StableConnection;
    }

    public event EventHandler<VrChatRealtimeEvent>? EventReceived;

    public event EventHandler<VrChatRealtimeStatus>? StatusChanged;

    public VrChatRealtimeStatus Status { get; private set; } = new(VrChatRealtimeState.Idle);

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _loop is { IsCompleted: false };
            }
        }
    }

    /// <summary>Starts a new pipeline session, replacing any previous one.</summary>
    public void Start()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            previous = _cancellation;
            // The session being replaced is cancelled below and then runs its own
            // socket disposal, so keep its loop around for StopAsync to wait on.
            if (_loop is not null)
            {
                _retired = _retired is null ? _loop : Task.WhenAll(_retired, _loop);
            }

            _lastRaw = string.Empty;
            _cancellation = new CancellationTokenSource();
            var owned = _cancellation;
            var token = owned.Token;
            var generation = Interlocked.Increment(ref _generation);
            _loop = Task.Run(
                () => RunAsync(generation, token, owned),
                CancellationToken.None);
        }

        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _cancellation;
            _cancellation = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        Interlocked.Increment(ref _generation);
        Status = new VrChatRealtimeStatus(VrChatRealtimeState.Idle);
    }

    /// <summary>
    /// Cancels the active session and waits until its socket and read loop have
    /// fully unwound. Use this during shutdown or when replacing a session.
    /// </summary>
    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            loop = _loop;
            cancellation = _cancellation;
            _cancellation = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        Interlocked.Increment(ref _generation);
        Status = new VrChatRealtimeStatus(VrChatRealtimeState.Idle);

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task? retired;
        lock (_gate)
        {
            retired = _retired;
            _retired = null;
        }

        if (retired is not null)
        {
            try
            {
                await retired.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public static string BuildUrl(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("A VRChat realtime token is required.", nameof(token));
        }

        return DefaultEndpoint + "/?auth=" + Uri.EscapeDataString(token);
    }

    private async Task RunAsync(
        long generation,
        CancellationToken cancellationToken,
        CancellationTokenSource ownedCancellation)
    {
        try
        {
            await RunLoopAsync(generation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ownedCancellation.Dispose();
        }
    }

    private async Task RunLoopAsync(long generation, CancellationToken cancellationToken)
    {
        var reconnectDelay = _initialReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            PublishStatus(generation, VrChatRealtimeState.Connecting);
            var connectedAt = DateTimeOffset.MinValue;
            IVrChatPipelineSocket? socket = null;
            try
            {
                var token = await _tokenProvider(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token))
                {
                    PublishStatus(generation, VrChatRealtimeState.AuthFailure, "VRChat did not return a realtime token.");
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(ConnectTimeout);
                socket = await _socketFactory(
                    BuildUrl(token),
                    "https://vrchat.com",
                    connectTimeout.Token).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    await socket.DisposeAsync().ConfigureAwait(false);
                    break;
                }

                connectedAt = DateTimeOffset.UtcNow;
                PublishStatus(generation, VrChatRealtimeState.Connected);
                await ReceiveLoopAsync(generation, socket, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (VrcApiException ex) when (ex.StatusCode is 401 or 403)
            {
                PublishStatus(generation, VrChatRealtimeState.AuthFailure, ex.Message);
                return;
            }
            catch (VrChatWebSocketException ex) when (ex.StatusCode is 401 or 403)
            {
                PublishStatus(generation, VrChatRealtimeState.AuthFailure, ex.Message);
                return;
            }
            catch (VrChatWebSocketException ex)
            {
                PublishStatus(generation, VrChatRealtimeState.Disconnected, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PublishStatus(generation, VrChatRealtimeState.Disconnected, ex.Message);
            }
            finally
            {
                if (socket is not null)
                {
                    await socket.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // A healthy session resets backoff. Repeated failures keep doubling
            // the delay up to a minute, like VRCX-0's recorder.
            if (connectedAt != DateTimeOffset.MinValue &&
                DateTimeOffset.UtcNow - connectedAt >= _stableConnection)
            {
                reconnectDelay = _initialReconnectDelay;
            }

            try
            {
                await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            reconnectDelay = TimeSpan.FromSeconds(
                Math.Min(_maximumReconnectDelay.TotalSeconds, reconnectDelay.TotalSeconds * 2));
        }

        PublishStatus(generation, VrChatRealtimeState.Idle);
    }

    private async Task<string?> ResolveTokenFromApiAsync(CancellationToken cancellationToken)
    {
        var cookie = _api.CurrentAuthCookie;
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            return cookie;
        }

        return await _api.GetRealtimeTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(
        long generation,
        IVrChatPipelineSocket socket,
        CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiveTask = ReceiveFramesAsync(generation, socket, session.Token);
        var heartbeatTask = HeartbeatAsync(socket, session.Token);
        var completed = await Task.WhenAny(receiveTask, heartbeatTask).ConfigureAwait(false);

        if (completed == receiveTask)
        {
            session.Cancel();
            try { await heartbeatTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            await receiveTask.ConfigureAwait(false);
            return;
        }

        session.Cancel();
        // Closing the stream is what unblocks a read that is waiting on a
        // silently dead TCP connection.
        await socket.DisposeAsync().ConfigureAwait(false);
        try { await receiveTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await heartbeatTask.ConfigureAwait(false);
    }

    private async Task ReceiveFramesAsync(
        long generation,
        IVrChatPipelineSocket socket,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var raw = await socket.ReceiveTextAsync(cancellationToken).ConfigureAwait(false);
            if (raw is null)
            {
                throw new VrChatWebSocketException("VRChat closed the realtime connection.");
            }

            if (string.IsNullOrWhiteSpace(raw) ||
                string.Equals(raw, _lastRaw, StringComparison.Ordinal))
            {
                continue;
            }

            _lastRaw = raw;
            if (generation != Volatile.Read(ref _generation))
            {
                continue;
            }

            if (VrChatRealtimeMessage.TryParse(raw, DateTimeOffset.Now, out var parsed) && parsed is not null)
            {
                try
                {
                    EventReceived?.Invoke(this, parsed);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"realtime event handler: {ex}");
                }
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task HeartbeatAsync(
        IVrChatPipelineSocket socket,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_heartbeatInterval, cancellationToken)
                .ConfigureAwait(false);

            if (VrChatRealtimeLiveness.IsExpired(socket.LastInboundAt, DateTimeOffset.UtcNow, _deadline))
            {
                throw new TimeoutException(
                    "VRChat realtime stopped responding to heartbeat frames.");
            }

            await socket.SendPingAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void PublishStatus(long generation, VrChatRealtimeState state, string? reason = null)
    {
        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        Status = new VrChatRealtimeStatus(state, reason);
        try
        {
            StatusChanged?.Invoke(this, Status);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"realtime status handler: {ex}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
            _retired?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }
}
