using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Valve.VR;

namespace VrcCompanion.Services;

/// <summary>
/// Voice state read from the Discord web app that is already running in this
/// app's embedded browser. No Discord token or separate RPC client is needed.
/// </summary>
public sealed record DiscordVoiceSnapshot(
    string ChannelName,
    int MemberCount,
    IReadOnlyList<string> Speaking)
{
    public const string DefaultFormat = "🔊 {channel} ({count}) | 🎙 {speaking}";

    public string ToText(string? template = null)
    {
        var channel = string.IsNullOrWhiteSpace(ChannelName) ? "Voice" : ChannelName.Trim();
        var count = Math.Max(0, MemberCount);
        var names = Speaking
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToList();
        var speakers = names
            .Take(2)
            .ToList();
        var speakingText = speakers.Count == 0 ? "Quiet..." : string.Join(", ", speakers);
        var format = string.IsNullOrWhiteSpace(template) ? DefaultFormat : template.Trim();
        return format
            .Replace("{channel}", channel)
            .Replace("{count}", count.ToString(CultureInfo.InvariantCulture))
            .Replace("{speaking}", speakingText)
            .Replace("{speaking_count}", names.Count.ToString(CultureInfo.InvariantCulture))
            .Replace("\\n", "\n")
            .Replace("/n", "\n");
    }

    internal static DiscordVoiceSnapshot? TryParse(string? payload)
    {
        var text = (payload ?? string.Empty).Trim();
        if (text.Length == 0 || text is "null" or "not-connected")
        {
            return null;
        }

        // WebView2 returns a JavaScript string as a JSON-quoted string.
        if (text.StartsWith('"'))
        {
            try
            {
                text = JsonSerializer.Deserialize<string>(text) ?? string.Empty;
            }
            catch
            {
                return null;
            }
        }

        if (text.Length == 0 || text is "null" or "not-connected")
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var channel = root.TryGetProperty("channel", out var channelElement)
                ? channelElement.GetString() ?? string.Empty
                : string.Empty;
            var count = root.TryGetProperty("count", out var countElement) &&
                        countElement.TryGetInt32(out var parsedCount)
                ? parsedCount
                : 0;
            var speaking = new List<string>();
            if (root.TryGetProperty("speaking", out var speakingElement) &&
                speakingElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var speaker in speakingElement.EnumerateArray())
                {
                    var name = speaker.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        speaking.Add(name.Trim());
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(channel) && count == 0 && speaking.Count == 0)
            {
                return null;
            }

            return new DiscordVoiceSnapshot(channel, Math.Max(count, speaking.Count), speaking);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// One connected VR device. Percent is null when the driver (for example a
/// SlimeVR virtual tracker) reports the device but not its battery property.
/// </summary>
public sealed record OpenVrDeviceState(
    string Name,
    double? Percent,
    bool Charging,
    string Source = "SteamVR",
    bool IsTracker = false,
    bool IsController = false);

/// <summary>
/// Raw device information used by the in-app SteamVR battery diagnostic. It
/// deliberately includes disconnected devices and property errors so it is
/// clear why a controller or tracker did not produce a battery entry.
/// </summary>
public sealed record OpenVrDeviceDiagnostic(
    uint Index,
    string DeviceClass,
    string Name,
    string ModelNumber,
    bool Connected,
    bool? ProvidesBatteryStatus,
    string BatteryPropertyStatus,
    double? BatteryPercent,
    bool? Charging);

public sealed record OpenVrSnapshot(
    IReadOnlyList<OpenVrDeviceState> Devices,
    double Fps,
    int DroppedFrames,
    double FrameMs,
    IReadOnlyList<OpenVrDeviceDiagnostic>? Diagnostics = null)
{
    public const string DefaultTrackerFormat = "{icon} {name} {batt}%";
    public const string DefaultPerformanceFormat = "🎮 {fps} FPS";

    public string BuildTrackerText(string? template = null)
    {
        if (Devices.Count == 0)
        {
            return string.Empty;
        }

        var format = string.IsNullOrWhiteSpace(template) ? DefaultTrackerFormat : template.Trim();
        // HMD + up to six physical SlimeVR trackers + two controllers. The
        // chatbox limit still trims the combined line if other integrations
        // leave less room.
        var trackerNumber = 0;
        var controllerNumber = 0;
        string CompactName(OpenVrDeviceState device)
        {
            var name = device.Name.Trim();
            if (name.Equals("HMD", StringComparison.OrdinalIgnoreCase))
            {
                return "HMD";
            }

            if (name is "L" or "R")
            {
                return name;
            }

            if (device.IsTracker ||
                name.Contains("tracker", StringComparison.OrdinalIgnoreCase))
            {
                return $"T{++trackerNumber}";
            }

            if (name.Contains("controller", StringComparison.OrdinalIgnoreCase))
            {
                return $"C{++controllerNumber}";
            }

            if (name.Equals("Valve Index", StringComparison.OrdinalIgnoreCase))
            {
                return "Index";
            }

            return name.Length <= 10 ? name : name[..10].TrimEnd();
        }

        // A connected controller without a battery value adds only noise to the
        // chatbox. Keep it in the snapshot for diagnostics, but omit it from the
        // line until its driver starts reporting a percentage.
        var visibleDevices = Devices
            .Where(device => !device.IsController || device.Percent is not null)
            .Take(9);
        var parts = visibleDevices.Select(device =>
        {
            var known = device.Percent is not null;
            var battery = device.Percent?.ToString("0", CultureInfo.InvariantCulture) ?? "--";
            var status = !known
                ? "battery unknown"
                : device.Charging ? "charging" : "battery";
            return format
                .Replace("{icon}", device.Charging ? "⚡" : "🔋")
                .Replace("{name}", CompactName(device))
                .Replace("{fullName}", device.Name)
                .Replace("{batt}", battery)
                .Replace("{status}", status)
                .Replace("\\n", "\n");
        });
        return string.Join(" · ", parts);
    }

    public string BuildBatteryDiagnosticText()
    {
        if (Diagnostics is null || Diagnostics.Count == 0)
        {
            return "SteamVR did not expose any tracked device records.";
        }

        var lines = Diagnostics
            .Where(device => device.Connected ||
                             device.BatteryPercent is not null ||
                             device.DeviceClass is "HMD" or "Controller" or "GenericTracker")
            .OrderBy(device => device.Index)
            .Take(16)
            .Select(device =>
            {
                var battery = device.BatteryPercent is { } percent
                    ? $"battery {percent:0}%"
                    : $"battery unavailable ({device.BatteryPropertyStatus})";
                var provides = device.ProvidesBatteryStatus is { } advertised
                    ? $"advertises={advertised.ToString().ToLowerInvariant()}"
                    : "advertises=unknown";
                var charging = device.Charging is { } isCharging
                    ? $"charging={isCharging.ToString().ToLowerInvariant()}"
                    : "charging=unknown";
                var model = string.IsNullOrWhiteSpace(device.ModelNumber) ||
                            string.Equals(device.ModelNumber, device.Name, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : $" · {device.ModelNumber}";
                return $"{device.Index}: {device.Name} [{device.DeviceClass}] " +
                       $"{(device.Connected ? "connected" : "not connected")} · {battery} · {provides} · {charging}{model}";
            });

        var text = string.Join(Environment.NewLine, lines);
        return string.IsNullOrWhiteSpace(text)
            ? "No connected SteamVR or SlimeVR devices are currently reporting battery data."
            : text;
    }

    public string BuildPerformanceText(string? template = null)
    {
        if (Fps <= 0)
        {
            return string.Empty;
        }

        var format = string.IsNullOrWhiteSpace(template) ? DefaultPerformanceFormat : template.Trim();
        return format
            .Replace("{fps}", Fps.ToString("0", CultureInfo.InvariantCulture))
            .Replace("{frame_ms}", FrameMs.ToString("0.0", CultureInfo.InvariantCulture))
            .Replace("{dropped}", DroppedFrames.ToString(CultureInfo.InvariantCulture))
            .Replace("\\n", "\n");
    }
}

/// <summary>
/// Reads headset/controller/tracker battery and compositor frame timing from
/// SteamVR through OpenVR. It is quiet until SteamVR is actually running.
/// </summary>
public sealed class OpenVrService : IDisposable
{
    // Present in the OpenVR runtime but missing from the older managed enum
    // shipped by the package; the value is stable in the OpenVR API.
    private const ETrackedDeviceProperty ControllerRoleHint =
        (ETrackedDeviceProperty)3003;

    private readonly object _sync = new();
    private readonly SlimeVrBatteryService _slimeVrBattery = new();
    private CVRSystem? _system;
    private DateTime _retryAfter = DateTime.MinValue;
    private Task<OpenVrSnapshot?>? _inFlight;
    private CancellationTokenSource? _eventPumpCancellation;
    private Task? _eventPump;
    private bool _disposed;

    public Task<OpenVrSnapshot?> PollAsync()
    {
        lock (_sync)
        {
            if (_inFlight is { IsCompleted: false })
            {
                return _inFlight;
            }

            _inFlight = Task.Run(PollCore);
            return _inFlight;
        }
    }

    private OpenVrSnapshot? PollCore()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return null;
            }

            if (_system is null)
            {
                if (DateTime.UtcNow < _retryAfter)
                {
                    return null;
                }

                _retryAfter = DateTime.UtcNow.AddSeconds(15);
                try
                {
                    if (!OpenVR.IsRuntimeInstalled() || !OpenVR.IsHmdPresent())
                    {
                        return null;
                    }

                    var error = EVRInitError.None;
                    var system = OpenVR.Init(ref error, EVRApplicationType.VRApplication_Background);
                    if (error != EVRInitError.None || system is null)
                    {
                        return null;
                    }

                    _system = system;
                    StartEventPumpLocked();
                }
                catch
                {
                    _system = null;
                    return null;
                }
            }

            // SteamVR can disappear between the event pump's checks. Do not call
            // into the old system interface after the runtime has gone away.
            if (_system is not null &&
                (!OpenVR.IsRuntimeInstalled() || !OpenVR.IsHmdPresent()))
            {
                ShutdownRuntimeLocked();
                return null;
            }

            return _system is null ? null : Read(_system);
        }
    }

    /// <summary>
    /// OpenVR applications are registered with SteamVR. When SteamVR exits it
    /// sends VREvent_Quit and then terminates processes that do not acknowledge
    /// it. Polling only when the chatbox refreshes (every few seconds) was too
    /// slow, so SteamVR closed this whole app. This lightweight pump drains the
    /// event queue and releases OpenVR immediately, leaving the WPF app running.
    /// </summary>
    private void StartEventPumpLocked()
    {
        if (_eventPump is { IsCompleted: false })
        {
            return;
        }

        _eventPumpCancellation?.Dispose();
        _eventPumpCancellation = new CancellationTokenSource();
        var token = _eventPumpCancellation.Token;
        _eventPump = Task.Run(() => PumpEventsAsync(token), token);
    }

    private async Task PumpEventsAsync(CancellationToken cancellationToken)
    {
        var eventSize = (uint)Marshal.SizeOf<VREvent_t>();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            lock (_sync)
            {
                if (_disposed || _system is null)
                {
                    return;
                }

                try
                {
                    var notification = default(VREvent_t);
                    while (_system.PollNextEvent(ref notification, eventSize))
                    {
                        if (notification.eventType != (uint)EVREventType.VREvent_Quit)
                        {
                            continue;
                        }

                        // Acknowledge before SteamVR's force-close timer fires,
                        // then drop our interface before the runtime unloads it.
                        LifecycleLog.Write("OpenVR quit received; acknowledged and released");
                        try
                        {
                            _system.AcknowledgeQuit_Exiting();
                        }
                        catch
                        {
                            // The runtime may already be tearing down.
                        }

                        ShutdownRuntimeLocked();
                        return;
                    }
                }
                catch
                {
                    // A broken runtime pointer must never take the desktop app
                    // down with it; retry when SteamVR is available again.
                    ShutdownRuntimeLocked();
                    return;
                }
            }
        }
    }

    private void ShutdownRuntimeLocked()
    {
        _system = null;
        _retryAfter = DateTime.UtcNow.AddSeconds(3);
        try
        {
            OpenVR.Shutdown();
        }
        catch
        {
            // Nothing left to release.
        }
    }

    private OpenVrSnapshot Read(CVRSystem system)
    {
        var batteries = new List<OpenVrDeviceState>();
        var diagnostics = new List<OpenVrDeviceDiagnostic>();
        for (uint index = 0; index < OpenVR.k_unMaxTrackedDeviceCount; index++)
        {
            var connected = false;
            var deviceClass = ETrackedDeviceClass.Invalid;
            try
            {
                connected = system.IsTrackedDeviceConnected(index);
                deviceClass = system.GetTrackedDeviceClass(index);
            }
            catch
            {
                // Keep the diagnostic row even if the runtime rejects the index.
            }

            // Some controller/tracker drivers expose a valid battery percentage
            // without advertising Prop_DeviceProvidesBatteryStatus_Bool. Read the
            // percentage directly so those devices are not silently skipped.
            var batteryError = ETrackedPropertyError.TrackedProp_Success;
            var battery = 0f;
            try
            {
                battery = system.GetFloatTrackedDeviceProperty(
                    index,
                    ETrackedDeviceProperty.Prop_DeviceBatteryPercentage_Float,
                    ref batteryError);
            }
            catch
            {
                batteryError = ETrackedPropertyError.TrackedProp_InvalidDevice;
            }

            var charging = ReadBool(
                system,
                index,
                ETrackedDeviceProperty.Prop_DeviceIsCharging_Bool);
            var providesBattery = ReadBool(
                system,
                index,
                ETrackedDeviceProperty.Prop_DeviceProvidesBatteryStatus_Bool);
            var name = DeviceName(system, index);
            var model = ReadString(
                system,
                index,
                ETrackedDeviceProperty.Prop_ModelNumber_String);
            var percent = batteryError == ETrackedPropertyError.TrackedProp_Success
                ? Math.Clamp(battery * 100d, 0d, 100d)
                : (double?)null;

            diagnostics.Add(new OpenVrDeviceDiagnostic(
                index,
                deviceClass.ToString(),
                name,
                model,
                connected,
                providesBattery,
                batteryError.ToString(),
                percent,
                charging));

            // A connected controller or tracker still belongs in the chatbox even
            // when its driver does not expose battery (common for virtual trackers).
            // Skipping it made the line look like only the headset existed.
            if (connected &&
                deviceClass is ETrackedDeviceClass.HMD or
                    ETrackedDeviceClass.Controller or
                    ETrackedDeviceClass.GenericTracker)
            {
                batteries.Add(new OpenVrDeviceState(
                    name,
                    percent,
                    charging == true,
                    IsTracker: deviceClass == ETrackedDeviceClass.GenericTracker,
                    IsController: deviceClass == ETrackedDeviceClass.Controller));
            }
        }

        // SteamVR exposes only SlimeVR's merged virtual trackers (for example
        // hip+waist as one device), and those trackers never carry a battery
        // property. Read the physical device list from SlimeVR itself and use
        // its percentages instead.
        var slimeVrTrackers = _slimeVrBattery.Read();
        if (slimeVrTrackers.Count > 0)
        {
            var slimeVrHmd = slimeVrTrackers.FirstOrDefault(tracker => tracker.IsHmd);
            var slimeVrControllers = slimeVrTrackers
                .Where(tracker => tracker.IsController)
                .ToList();
            var physicalTrackers = slimeVrTrackers
                .Where(tracker => !tracker.IsHmd && !tracker.IsController)
                .ToList();

            var hmd = batteries
                .Where(device => !device.IsTracker &&
                                 device.Name.Equals("HMD", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var controllers = batteries
                .Where(device => !device.IsTracker &&
                                 !device.Name.Equals("HMD", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (slimeVrHmd?.Percent is { } hmdPercent)
            {
                if (hmd.Count == 0)
                {
                    hmd.Add(new OpenVrDeviceState(
                        "HMD",
                        hmdPercent,
                        false,
                        Source: "SlimeVR"));
                }
                else
                {
                    hmd[0] = hmd[0] with { Percent = hmdPercent };
                }
            }

            // Quest controllers are reported by SlimeVR with their left/right
            // names, but SteamVR exposes them only as two generic Controllers.
            // Match by side where possible, then pair the remaining entries in
            // order so C1/C2 still line up with the physical controllers.
            var usedControllers = new HashSet<int>();
            foreach (var tracker in slimeVrControllers)
            {
                var side = tracker.Name.Contains("left", StringComparison.OrdinalIgnoreCase)
                    ? "L"
                    : tracker.Name.Contains("right", StringComparison.OrdinalIgnoreCase)
                        ? "R"
                        : null;
                var controllerIndex = side is null
                    ? -1
                    : controllers.FindIndex(device => device.Name == side);
                if (controllerIndex < 0)
                {
                    for (var index = 0; index < controllers.Count; index++)
                    {
                        if (!usedControllers.Contains(index) &&
                            (controllers[index].Name.Equals("Controller", StringComparison.OrdinalIgnoreCase) ||
                             controllers[index].Name.StartsWith("C", StringComparison.OrdinalIgnoreCase)))
                        {
                            controllerIndex = index;
                            break;
                        }
                    }
                }

                if (controllerIndex < 0)
                {
                    for (var index = 0; index < controllers.Count; index++)
                    {
                        if (!usedControllers.Contains(index))
                        {
                            controllerIndex = index;
                            break;
                        }
                    }
                }

                if (controllerIndex >= 0)
                {
                    usedControllers.Add(controllerIndex);
                    var current = controllers[controllerIndex];
                    controllers[controllerIndex] = current with
                    {
                        Name = tracker.Percent is not null ? side ?? current.Name : current.Name,
                        Percent = tracker.Percent ?? current.Percent
                    };
                }
                else if (tracker.Percent is not null)
                {
                    controllers.Add(new OpenVrDeviceState(
                        side ?? "Controller",
                        tracker.Percent,
                        false,
                        Source: "SlimeVR",
                        IsController: true));
                }
            }

            if (physicalTrackers.Count > 0 || hmd.Count > 0 || controllers.Count > 0)
            {
                batteries.Clear();
                batteries.AddRange(hmd);
                batteries.AddRange(physicalTrackers.Select(tracker => new OpenVrDeviceState(
                    tracker.Name,
                    tracker.Percent,
                    false,
                    Source: "SlimeVR",
                    IsTracker: true)));
                batteries.AddRange(controllers);
            }

            var diagnosticIndex = 900u;
            foreach (var tracker in slimeVrTrackers)
            {
                diagnostics.Add(new OpenVrDeviceDiagnostic(
                    diagnosticIndex++,
                    tracker.IsHmd
                        ? "SlimeVrHmd"
                        : tracker.IsController
                            ? "SlimeVrController"
                            : "SlimeVrTracker",
                    tracker.Name,
                    "SlimeVR",
                    true,
                    true,
                    tracker.Percent is null ? "SlimeVrFeedNoBattery" : "SlimeVrFeed",
                    tracker.Percent,
                    false));
            }
        }

        var fps = 0d;
        var droppedFrames = 0;
        var frameMs = 0d;
        try
        {
            var timing = new Compositor_FrameTiming
            {
                m_nSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Compositor_FrameTiming>()
            };
            if (OpenVR.Compositor.GetFrameTiming(ref timing, 0))
            {
                var interval = timing.m_flClientFrameIntervalMs;
                if (interval <= 0)
                {
                    interval = timing.m_flTotalRenderGpuMs;
                }

                if (interval > 0)
                {
                    fps = 1000d / interval;
                    droppedFrames = (int)timing.m_nNumDroppedFrames;
                    frameMs = interval;
                }
            }
        }
        catch
        {
            fps = 0;
            droppedFrames = 0;
            frameMs = 0;
        }

        return new OpenVrSnapshot(batteries, fps, droppedFrames, frameMs, diagnostics);
    }

    private static bool? ReadBool(
        CVRSystem system,
        uint index,
        ETrackedDeviceProperty property)
    {
        try
        {
            var error = ETrackedPropertyError.TrackedProp_Success;
            var value = system.GetBoolTrackedDeviceProperty(index, property, ref error);
            return error == ETrackedPropertyError.TrackedProp_Success ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string DeviceName(CVRSystem system, uint index)
    {
        try
        {
            var deviceClass = system.GetTrackedDeviceClass(index);
            if (deviceClass == ETrackedDeviceClass.HMD)
            {
                return "HMD";
            }

            if (deviceClass == ETrackedDeviceClass.Controller)
            {
                var role = ReadString(
                    system,
                    index,
                    ControllerRoleHint);
                if (role.Equals("left", StringComparison.OrdinalIgnoreCase) || role == "1")
                {
                    return "L";
                }

                if (role.Equals("right", StringComparison.OrdinalIgnoreCase) || role == "2")
                {
                    return "R";
                }

                return "Controller";
            }

            var error = ETrackedPropertyError.TrackedProp_Success;
            var buffer = new StringBuilder(128);
            system.GetStringTrackedDeviceProperty(
                index,
                ETrackedDeviceProperty.Prop_ModelNumber_String,
                buffer,
                (uint)buffer.Capacity,
                ref error);
            var model = buffer.ToString().Trim();
            if (model.Length > 0)
            {
                return model.Length > 22 ? model[..22] : model;
            }

            return deviceClass switch
            {
                ETrackedDeviceClass.GenericTracker => "Tracker",
                _ => "Device"
            };
        }
        catch
        {
            return "Device";
        }
    }

    private static string ReadString(
        CVRSystem system,
        uint index,
        ETrackedDeviceProperty property)
    {
        try
        {
            var error = ETrackedPropertyError.TrackedProp_Success;
            var buffer = new StringBuilder(64);
            system.GetStringTrackedDeviceProperty(
                index,
                property,
                buffer,
                (uint)buffer.Capacity,
                ref error);
            return error == ETrackedPropertyError.TrackedProp_Success
                ? buffer.ToString().Trim()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _system = null;
            _eventPumpCancellation?.Cancel();
            _eventPumpCancellation?.Dispose();
            _eventPumpCancellation = null;
            _eventPump = null;
            try
            {
                OpenVR.Shutdown();
            }
            catch
            {
                // The runtime may already be gone; there is nothing to clean up.
            }
        }
    }
}
