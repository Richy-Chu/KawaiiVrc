using System.IO;
using System.Net.WebSockets;
using Google.FlatBuffers;
using solarxr_protocol;

namespace VrcCompanion.Services;

/// <summary>
/// Battery information for one physical SlimeVR tracker.
/// </summary>
internal sealed record SlimeVrTrackerBattery(
    string Name,
    double? Percent,
    bool IsController = false,
    bool IsHmd = false);

/// <summary>
/// Reads the physical tracker battery levels from the SlimeVR server's local
/// SolarXR WebSocket feed. SteamVR only sees SlimeVR's merged virtual trackers,
/// so it cannot provide these values itself.
/// </summary>
internal sealed class SlimeVrBatteryService
{
    private const string Endpoint = "ws://127.0.0.1:21110/";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan SuccessCache = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FailureCache = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaleLimit = TimeSpan.FromSeconds(20);

    private readonly object _sync = new();
    private IReadOnlyList<SlimeVrTrackerBattery> _last =
        Array.Empty<SlimeVrTrackerBattery>();
    private DateTime _lastAttempt = DateTime.MinValue;
    private DateTime _lastSuccess = DateTime.MinValue;

    public IReadOnlyList<SlimeVrTrackerBattery> Read()
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            var cache = _last.Count > 0 ? SuccessCache : FailureCache;
            if (now - _lastAttempt < cache)
            {
                return Current(now);
            }

            _lastAttempt = now;
        }

        var result = Task.Run(ReadAsync).GetAwaiter().GetResult();
        lock (_sync)
        {
            if (result.Count > 0)
            {
                _last = result;
                _lastSuccess = DateTime.UtcNow;
            }

            return Current(DateTime.UtcNow);
        }
    }

    private IReadOnlyList<SlimeVrTrackerBattery> Current(DateTime now)
    {
        if (_last.Count > 0 && now - _lastSuccess <= StaleLimit)
        {
            return _last;
        }

        return Array.Empty<SlimeVrTrackerBattery>();
    }

    private static async Task<IReadOnlyList<SlimeVrTrackerBattery>> ReadAsync()
    {
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        try
        {
            await socket.ConnectAsync(new Uri(Endpoint), timeout.Token).ConfigureAwait(false);
            var request = BuildStartFeedRequest();
            await socket.SendAsync(
                new ArraySegment<byte>(request),
                WebSocketMessageType.Binary,
                true,
                timeout.Token).ConfigureAwait(false);

            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (!timeout.IsCancellationRequested)
            {
                var frame = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    timeout.Token).ConfigureAwait(false);
                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (frame.MessageType != WebSocketMessageType.Binary)
                {
                    continue;
                }

                message.Write(buffer, 0, frame.Count);
                if (!frame.EndOfMessage)
                {
                    continue;
                }

                var trackers = ParseBundle(message.ToArray());
                message.SetLength(0);
                if (trackers.Count > 0)
                {
                    return trackers;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // SlimeVR is not running or did not answer in time.
        }
        catch (WebSocketException)
        {
            // The local SlimeVR server is unavailable.
        }
        catch (Exception)
        {
            // A malformed or changing feed must never break the chatbox.
        }

        return Array.Empty<SlimeVrTrackerBattery>();
    }

    internal static byte[] BuildStartFeedRequest()
    {
        var builder = new FlatBufferBuilder(256);
        var trackerMask = TrackerDataMask.CreateTrackerDataMask(
            builder,
            info: true,
            status: true);
        var deviceMask = DeviceDataMask.CreateDeviceDataMask(
            builder,
            trackerMask,
            true);
        var config = DataFeedConfig.CreateDataFeedConfig(
            builder,
            100,
            deviceMask);
        var configs = StartDataFeed.CreateDataFeedsVector(builder, new[] { config });
        var start = StartDataFeed.CreateStartDataFeed(builder, configs);
        var header = DataFeedMessageHeader.CreateDataFeedMessageHeader(
            builder,
            DataFeedMessage.StartDataFeed,
            start.Value);
        var headers = MessageBundle.CreateDataFeedMsgsVector(builder, new[] { header });
        var bundle = MessageBundle.CreateMessageBundle(builder, headers);
        builder.Finish(bundle.Value);
        return builder.SizedByteArray();
    }

    internal static IReadOnlyList<SlimeVrTrackerBattery> ParseBundle(byte[] payload)
    {
        if (payload.Length < 4)
        {
            return Array.Empty<SlimeVrTrackerBattery>();
        }

        var bundle = MessageBundle.GetRootAsMessageBundle(new ByteBuffer(payload));
        var trackers = new List<SlimeVrTrackerBattery>();
        for (var messageIndex = 0; messageIndex < bundle.DataFeedMsgsLength; messageIndex++)
        {
            var header = bundle.DataFeedMsgs(messageIndex);
            if (header is null || header.Value.MessageType != DataFeedMessage.DataFeedUpdate)
            {
                continue;
            }

            var update = header.Value.MessageAsDataFeedUpdate();
            for (var deviceIndex = 0; deviceIndex < update.DevicesLength; deviceIndex++)
            {
                try
                {
                    var device = update.Devices(deviceIndex);
                    if (device is null)
                    {
                        continue;
                    }

                    double? percent = null;
                    try
                    {
                        if (device.Value.HardwareStatus?.BatteryPctEstimate is { } battery)
                        {
                            percent = Math.Clamp((int)battery, 0, 100);
                        }
                    }
                    catch
                    {
                        // A newer SolarXR schema may change this table. The
                        // rest of the device record is still useful.
                    }

                    var deviceName = string.Empty;
                    try
                    {
                        deviceName = FirstNonEmpty(
                            device.Value.CustomName,
                            device.Value.HardwareInfo?.DisplayName);
                    }
                    catch
                    {
                        // Nested hardware info is optional and may use a newer
                        // layout than the bundled schema.
                    }

                    var trackerCount = 0;
                    try
                    {
                        trackerCount = device.Value.TrackersLength;
                    }
                    catch
                    {
                        continue;
                    }

                    for (var trackerIndex = 0; trackerIndex < trackerCount; trackerIndex++)
                    {
                        try
                        {
                            var tracker = device.Value.Trackers(trackerIndex);
                            if (tracker is null)
                            {
                                continue;
                            }

                            var info = tracker.Value.Info;
                            var trackerName = string.Empty;
                            try
                            {
                                if (info is { } trackerInfo)
                                {
                                    trackerName = FirstNonEmpty(
                                        trackerInfo.CustomName,
                                        trackerInfo.DisplayName,
                                        BodyPartName(trackerInfo.BodyPart));
                                }
                            }
                            catch
                            {
                                // Fall back to the device name below.
                            }

                            trackerName = FirstNonEmpty(trackerName, deviceName);
                            if (string.IsNullOrWhiteSpace(trackerName))
                            {
                                trackerName = $"Tracker {trackers.Count + 1}";
                            }

                            var isController = trackerName.Contains(
                                "controller",
                                StringComparison.OrdinalIgnoreCase);
                            var isHmd = !isController &&
                                        (trackerName.Contains("hmd", StringComparison.OrdinalIgnoreCase) ||
                                         trackerName.Contains("quest", StringComparison.OrdinalIgnoreCase));
                            trackers.Add(new SlimeVrTrackerBattery(
                                trackerName,
                                percent,
                                isController,
                                isHmd));
                        }
                        catch
                        {
                            // Skip a malformed tracker rather than losing the
                            // complete device and its battery reading.
                        }
                    }
                }
                catch
                {
                    // Skip a malformed device rather than failing the read.
                }
            }
        }

        return trackers
            .Take(12)
            .ToList();
    }

    private static string BodyPartName(byte bodyPart) => bodyPart switch
    {
        1 => "Head",
        2 => "Neck",
        3 => "Chest",
        4 => "Waist",
        5 => "Hip",
        6 => "Left Upper Leg",
        7 => "Right Upper Leg",
        8 => "Left Lower Leg",
        9 => "Right Lower Leg",
        10 => "Left Foot",
        11 => "Right Foot",
        14 => "Left Lower Arm",
        15 => "Right Lower Arm",
        16 => "Left Upper Arm",
        17 => "Right Upper Arm",
        18 => "Left Hand",
        19 => "Right Hand",
        20 => "Left Shoulder",
        21 => "Right Shoulder",
        22 => "Upper Chest",
        23 => "Left Hip",
        24 => "Right Hip",
        _ => string.Empty
    };

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

}
