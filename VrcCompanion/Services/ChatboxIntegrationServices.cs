using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VrcCompanion.Services;

public sealed record NetworkStatsSnapshot(double DownBitsPerSecond, double UpBitsPerSecond)
{
    public string Text =>
        $"↓ {FormatRate(DownBitsPerSecond)} ↑ {FormatRate(UpBitsPerSecond)}";

    public static string FormatRate(double bitsPerSecond)
    {
        var value = Math.Max(0, bitsPerSecond);
        if (value >= 1_000_000_000)
        {
            return $"{value / 1_000_000_000:0.##} Gbps";
        }

        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000:0.##} Mbps";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000:0.#} Kbps";
        }

        return $"{value:0} bps";
    }
}

/// <summary>Live rates from the machine's physical network interfaces.</summary>
public sealed class NetworkStatsService
{
    private long _lastReceived;
    private long _lastSent;
    private long _lastTimestamp;
    private bool _initialized;

    public NetworkStatsSnapshot? Poll()
    {
        try
        {
            long received = 0;
            long sent = 0;
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var stats = network.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }

            var now = Stopwatch.GetTimestamp();
            if (!_initialized)
            {
                _initialized = true;
                _lastReceived = received;
                _lastSent = sent;
                _lastTimestamp = now;
                return new NetworkStatsSnapshot(0, 0);
            }

            var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
            if (elapsed <= 0)
            {
                return null;
            }

            var down = Math.Max(0, received - _lastReceived) * 8 / elapsed;
            var up = Math.Max(0, sent - _lastSent) * 8 / elapsed;
            _lastReceived = received;
            _lastSent = sent;
            _lastTimestamp = now;
            return new NetworkStatsSnapshot(down, up);
        }
        catch
        {
            return null;
        }
    }
}

public sealed record SystemStatsSnapshot(
    double CpuPercent,
    double MemoryPercent,
    double? GpuPercent = null,
    double? VramUsedGb = null,
    double MemoryUsedGb = 0,
    double MemoryTotalGb = 0)
{
    public string Text
    {
        get
        {
            var parts = new List<string> { $"CPU {CpuPercent:0}%" };
            if (GpuPercent is { } gpu)
            {
                parts.Add($"GPU {gpu:0}%");
            }

            if (VramUsedGb is { } vram)
            {
                parts.Add($"VRAM {vram:0.0} GB");
            }

            parts.Add($"RAM {MemoryPercent:0}%");
            return string.Join(" ¦ ", parts);
        }
    }
}

/// <summary>
/// CPU and physical memory load, plus GPU utilization and dedicated VRAM from
/// Windows performance counters where the driver exposes them.
/// </summary>
public sealed class SystemStatsService : IDisposable
{
    private readonly object _gpuSync = new();
    private readonly object _gpuWorkSync = new();
    private readonly List<(string Key, PerformanceCounter Counter)> _gpuCounters = new();
    private readonly List<(string Key, PerformanceCounter Counter)> _vramCounters = new();
    private readonly CancellationTokenSource _gpuCancellation = new();
    private Task? _gpuPump;
    private DateTime _nextGpuScan = DateTime.MinValue;
    private double? _gpuPercent;
    private double? _vramUsedGb;
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private bool _initialized;

    public SystemStatsSnapshot? Poll()
    {
        try
        {
            EnsureGpuPump();

            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                return null;
            }

            var idleValue = idle.ToUInt64();
            var kernelValue = kernel.ToUInt64();
            var userValue = user.ToUInt64();

            var memory = new MemoryStatusEx();
            var hasMemory = GlobalMemoryStatusEx(memory);
            var memoryPercent = hasMemory ? memory.dwMemoryLoad : 0;
            var totalGb = hasMemory ? memory.ullTotalPhys / 1073741824d : 0;
            var usedGb = hasMemory
                ? (memory.ullTotalPhys - memory.ullAvailPhys) / 1073741824d
                : 0;

            double? gpu;
            double? vram;
            lock (_gpuSync)
            {
                gpu = _gpuPercent;
                vram = _vramUsedGb;
            }

            if (!_initialized)
            {
                _initialized = true;
                _lastIdle = idleValue;
                _lastKernel = kernelValue;
                _lastUser = userValue;
                return new SystemStatsSnapshot(0, memoryPercent, gpu, vram, usedGb, totalGb);
            }

            var idleDelta = idleValue - _lastIdle;
            var totalDelta = (kernelValue - _lastKernel) + (userValue - _lastUser);
            _lastIdle = idleValue;
            _lastKernel = kernelValue;
            _lastUser = userValue;

            var cpu = totalDelta == 0 ? 0 : (1d - (double)idleDelta / totalDelta) * 100d;
            return new SystemStatsSnapshot(
                Math.Clamp(cpu, 0, 100),
                memoryPercent,
                gpu,
                vram,
                usedGb,
                totalGb);
        }
        catch
        {
            return null;
        }
    }

    private void EnsureGpuPump()
    {
        lock (_gpuSync)
        {
            _gpuPump ??= Task.Run(GpuPumpAsync);
        }
    }

    private async Task GpuPumpAsync()
    {
        while (!_gpuCancellation.IsCancellationRequested)
        {
            try
            {
                double? gpu;
                double? vram;
                lock (_gpuWorkSync)
                {
                    RefreshGpuCounters();
                    gpu = ReadGpuPercentLocked();
                    vram = ReadVramLocked();
                }
                lock (_gpuSync)
                {
                    _gpuPercent = gpu;
                    _vramUsedGb = vram;
                }
            }
            catch
            {
                // Performance counters can disappear with a driver/process.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _gpuCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private double? ReadGpuPercentLocked()
    {
        if (_gpuCounters.Count == 0)
        {
            return null;
        }

        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, counter) in _gpuCounters)
        {
            try
            {
                var value = counter.NextValue();
                if (value < 0)
                {
                    continue;
                }

                engines.TryGetValue(key, out var current);
                // A single process cannot use more than 100% of an engine.
                engines[key] = Math.Min(100, current + Math.Clamp(value, 0, 100));
            }
            catch
            {
                // A process can disappear between scans; ignore it.
            }
        }

        return engines.Count == 0
            ? null
            : Math.Clamp(engines.Values.Max(), 0, 100);
    }

    private double? ReadVramLocked()
    {
        if (_vramCounters.Count == 0)
        {
            return null;
        }

        var adapters = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, counter) in _vramCounters)
        {
            try
            {
                var value = counter.NextValue();
                if (value <= 0)
                {
                    continue;
                }

                adapters.TryGetValue(key, out var current);
                adapters[key] = Math.Max(current, value);
            }
            catch
            {
                // Adapter instances can change; the next scan rebuilds them.
            }
        }

        // Use the busiest single adapter rather than summing adapters, which
        // would overstate VRAM on hybrid/multi-GPU systems.
        return adapters.Count == 0
            ? null
            : adapters.Values.Max() / 1073741824d;
    }

    private void RefreshGpuCounters()
    {
        if (DateTime.UtcNow < _nextGpuScan)
        {
            return;
        }

        _nextGpuScan = DateTime.UtcNow.AddSeconds(15);
        foreach (var (_, counter) in _gpuCounters)
        {
            counter.Dispose();
        }
        foreach (var (_, counter) in _vramCounters)
        {
            counter.Dispose();
        }
        _gpuCounters.Clear();
        _vramCounters.Clear();

        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instances = category.GetInstanceNames()
                .Where(instance => instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                .Take(32);
            foreach (var instance in instances)
            {
                try
                {
                    _gpuCounters.Add((
                        GpuEngineKey(instance),
                        new PerformanceCounter(
                            "GPU Engine",
                            "Utilization Percentage",
                            instance,
                            readOnly: true)));
                }
                catch
                {
                    // Instance disappeared while it was being opened.
                }
            }
        }
        catch
        {
            // GPU Engine counters are unavailable on some drivers/VMs.
        }

        try
        {
            var category = new PerformanceCounterCategory("GPU Adapter Memory");
            foreach (var instance in category.GetInstanceNames())
            {
                try
                {
                    _vramCounters.Add((
                        GpuAdapterKey(instance),
                        new PerformanceCounter(
                            "GPU Adapter Memory",
                            "Dedicated Usage",
                            instance,
                            readOnly: true)));
                }
                catch
                {
                    // Instance disappeared while it was being opened.
                }
            }
        }
        catch
        {
            // The driver does not expose dedicated GPU memory counters.
        }
    }

    public void Dispose()
    {
        _gpuCancellation.Cancel();
        try
        {
            _gpuPump?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The pump may already have stopped after cancellation.
        }

        lock (_gpuWorkSync)
        {
            foreach (var (_, counter) in _gpuCounters)
            {
                counter.Dispose();
            }
            foreach (var (_, counter) in _vramCounters)
            {
                counter.Dispose();
            }
            _gpuCounters.Clear();
            _vramCounters.Clear();
        }
        lock (_gpuSync)
        {
            _gpuPercent = null;
            _vramUsedGb = null;
        }
        _gpuCancellation.Dispose();
    }

    private static string GpuEngineKey(string instance)
    {
        var luid = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        var engineType = instance.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
        return luid >= 0 && engineType > luid
            ? instance[luid..engineType]
            : instance;
    }

    private static string GpuAdapterKey(string instance)
    {
        var luid = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        return luid >= 0 ? instance[luid..] : instance;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}

public sealed record WeatherSnapshot(
    string City,
    double TemperatureC,
    double FeelsLikeC,
    int Humidity,
    double WindKph,
    string Description,
    string Emoji)
{
    public string CompactText =>
        $"{Emoji} {TemperatureC:0}°C";

    public string DetailedText =>
        $"{Emoji} {TemperatureC:0}°C · {Description} · feels {FeelsLikeC:0}°C · {Humidity}% · {WindKph:0} km/h";
}

/// <summary>Open-Meteo weather. No API key and no account.</summary>
public sealed class WeatherService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<int, (string Description, string Emoji)> Codes = new()
    {
        [0] = ("clear", "☀️"),
        [1] = ("mostly clear", "🌤️"),
        [2] = ("partly cloudy", "⛅"),
        [3] = ("overcast", "☁️"),
        [45] = ("fog", "🌫️"),
        [48] = ("rime fog", "🌫️"),
        [51] = ("light drizzle", "🌦️"),
        [53] = ("drizzle", "🌦️"),
        [55] = ("heavy drizzle", "🌧️"),
        [56] = ("freezing drizzle", "🌧️"),
        [57] = ("freezing drizzle", "🌧️"),
        [61] = ("light rain", "🌦️"),
        [63] = ("rain", "🌧️"),
        [65] = ("heavy rain", "🌧️"),
        [66] = ("freezing rain", "🌧️"),
        [67] = ("freezing rain", "🌧️"),
        [71] = ("light snow", "🌨️"),
        [73] = ("snow", "🌨️"),
        [75] = ("heavy snow", "❄️"),
        [77] = ("snow grains", "🌨️"),
        [80] = ("rain showers", "🌦️"),
        [81] = ("rain showers", "🌧️"),
        [82] = ("violent showers", "⛈️"),
        [85] = ("snow showers", "🌨️"),
        [86] = ("snow showers", "❄️"),
        [95] = ("thunderstorm", "⛈️"),
        [96] = ("thunderstorm with hail", "⛈️"),
        [99] = ("thunderstorm with hail", "⛈️")
    };

    private string _cachedCity = string.Empty;
    private double _latitude;
    private double _longitude;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public async Task<WeatherSnapshot?> RefreshAsync(string city, CancellationToken cancellationToken = default)
    {
        city = string.IsNullOrWhiteSpace(city) ? "London" : city.Trim();
        if (city != _cachedCity || _latitude == 0 && _longitude == 0)
        {
            var location = await GeocodeAsync(city, cancellationToken).ConfigureAwait(false);
            if (location is null)
            {
                return null;
            }

            _cachedCity = city;
            _latitude = location.Value.Latitude;
            _longitude = location.Value.Longitude;
        }

        try
        {
            var url = "https://api.open-meteo.com/v1/forecast" +
                      $"?latitude={_latitude.ToString(CultureInfo.InvariantCulture)}" +
                      $"&longitude={_longitude.ToString(CultureInfo.InvariantCulture)}" +
                      "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m" +
                      "&timezone=auto";
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false));
            var current = document.RootElement.GetProperty("current");
            var code = current.GetProperty("weather_code").GetInt32();
            (string Description, string Emoji) condition = Codes.TryGetValue(code, out var mapped)
                ? mapped
                : ("weather", "🌡️");

            _lastRefresh = DateTimeOffset.Now;
            return new WeatherSnapshot(
                city,
                current.GetProperty("temperature_2m").GetDouble(),
                current.GetProperty("apparent_temperature").GetDouble(),
                current.GetProperty("relative_humidity_2m").GetInt32(),
                current.GetProperty("wind_speed_10m").GetDouble(),
                condition.Description,
                condition.Emoji);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(double Latitude, double Longitude)?> GeocodeAsync(
        string city,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name=" +
                      Uri.EscapeDataString(city);
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
            {
                return null;
            }

            var first = results[0];
            return (first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VrcCompanion/1.0");
        return client;
    }
}

public sealed record VrChatRadarSnapshot(string WorldName, string InstanceType, string Region, int Players)
{
    public string Text =>
        $"🌎 {WorldName} | 👥 {Players}" +
        (string.IsNullOrWhiteSpace(InstanceType) ? string.Empty : $" | {InstanceType}") +
        (string.IsNullOrWhiteSpace(Region) ? string.Empty : $" {Region}");
}

/// <summary>
/// Reads VRChat's own local output log. No network traffic and no VRChat API
/// account are involved.
/// </summary>
public sealed class VrChatRadarService
{
    private static readonly Regex EnteringRoom = new(
        @"\[Behaviour\] Entering Room: (?<world>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex Joining = new(
        @"Joining (?<location>wrld_[^\s]+)",
        RegexOptions.Compiled);

    private static readonly Regex Joined = new(
        @"\[Behaviour\] OnPlayerJoined (?<name>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex Left = new(
        @"\[Behaviour\] OnPlayerLeft (?<name>.+)$",
        RegexOptions.Compiled);

    private string _logPath = string.Empty;
    private long _position;
    private int _players;
    private string _world = string.Empty;
    private string _instance = string.Empty;
    private string _region = string.Empty;

    public VrChatRadarSnapshot? Poll()
    {
        try
        {
            var path = FindLatestLog();
            if (path is null)
            {
                return Current();
            }

            if (!string.Equals(path, _logPath, StringComparison.OrdinalIgnoreCase))
            {
                _logPath = path;
                _position = Math.Max(0, new FileInfo(path).Length - 256 * 1024);
                _players = 0;
                _world = string.Empty;
                _instance = string.Empty;
                _region = string.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < _position)
            {
                _position = 0;
            }

            stream.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            while (reader.ReadLine() is { } line)
            {
                Parse(line);
            }

            _position = stream.Position;
            return Current();
        }
        catch
        {
            return Current();
        }
    }

    private VrChatRadarSnapshot? Current()
        => string.IsNullOrWhiteSpace(_world)
            ? null
            : new VrChatRadarSnapshot(_world, _instance, _region, _players);

    private void Parse(string line)
    {
        var room = EnteringRoom.Match(line);
        if (room.Success)
        {
            _world = room.Groups["world"].Value.Trim();
            _players = 0;
            return;
        }

        var joining = Joining.Match(line);
        if (joining.Success)
        {
            var location = joining.Groups["location"].Value;
            var parts = location.Split(':');
            if (parts.Length > 1)
            {
                var instance = parts[1];
                _instance = instance.Split('~')[0];
                _region = instance.Contains("region(", StringComparison.OrdinalIgnoreCase)
                    ? instance[(instance.IndexOf("region(", StringComparison.OrdinalIgnoreCase) + 7)..]
                        .Split(')')[0]
                    : string.Empty;
            }

            return;
        }

        if (Joined.IsMatch(line))
        {
            _players++;
        }
        else if (Left.IsMatch(line))
        {
            _players = Math.Max(0, _players - 1);
        }
    }

    private static string? FindLatestLog()
    {
        var directory = VrcLogPaths.Directory;
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "output_log_*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }
}

/// <summary>Minimal Pulsoid REST client. Requires the user's own access token.</summary>
public sealed class PulsoidService
{
    private static readonly HttpClient Http = CreateClient();
    private int? _lastHeartRate;

    public int? LastHeartRate => _lastHeartRate;

    public async Task<int?> PollAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://dev.pulsoid.net/api/v1/data/heart_rate/latest");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                token.Trim());
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return _lastHeartRate;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false));
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("heart_rate", out var heartRate))
            {
                _lastHeartRate = heartRate.GetInt32();
            }

            return _lastHeartRate;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return _lastHeartRate;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VrcCompanion/1.0");
        return client;
    }
}

public sealed record TikTokProfileSnapshot(string Username, long Followers)
{
    public string FollowersText => Followers switch
    {
        >= 1_000_000 => $"{Followers / 1_000_000d:0.#}M",
        >= 1_000 => $"{Followers / 1_000d:0.#}K",
        _ => Followers.ToString()
    };
}

/// <summary>
/// Reads the follower count from a public TikTok profile page. No login and
/// no private API. TikTok can change or rate-limit the page, so failures keep
/// the previous value rather than inventing one.
/// </summary>
public sealed class TikTokProfileService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex FollowerCount = new(
        "\"followerCount\"\\s*:\\s*(\\d+)",
        RegexOptions.Compiled);

    private TikTokProfileSnapshot? _last;

    public async Task<TikTokProfileSnapshot?> PollAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var name = NormalizeUsername(username);
        if (name.Length == 0)
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://www.tiktok.com/@{Uri.EscapeDataString(name)}");
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return _last;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var match = FollowerCount.Match(html);
            if (!match.Success || !long.TryParse(match.Groups[1].Value, out var followers))
            {
                return _last;
            }

            _last = new TikTokProfileSnapshot(name, followers);
            return _last;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return _last;
        }
    }

    private static string NormalizeUsername(string value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.StartsWith('@'))
        {
            name = name[1..];
        }

        if (name.Contains("tiktok.com/", StringComparison.OrdinalIgnoreCase))
        {
            var uri = Uri.TryCreate(name, UriKind.Absolute, out var parsed)
                ? parsed
                : null;
            var path = uri?.AbsolutePath.Trim('/') ?? name;
            name = path.StartsWith('@') ? path[1..] : path;
        }

        return name.Split('/', '?', '#')[0].Trim();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        return client;
    }
}

public sealed record TwitchSnapshot(string Channel, bool IsLive, int Viewers, string Game, string Title)
{
    public string Text => IsLive
        ? $"🔴 LIVE {Viewers} viewers" +
          (string.IsNullOrWhiteSpace(Game) ? string.Empty : $" · {Game}") +
          (string.IsNullOrWhiteSpace(Title) ? string.Empty : $" · {Title}")
        : $"⚫ {Channel} offline";
}

/// <summary>Twitch Helix stream lookup using a user-supplied token.</summary>
public sealed class TwitchService
{
    private static readonly HttpClient Http = CreateClient();

    public async Task<TwitchSnapshot?> PollAsync(
        string clientId,
        string accessToken,
        string channel,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(channel))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.twitch.tv/helix/streams?user_login={Uri.EscapeDataString(channel.Trim())}");
            request.Headers.Add("Client-Id", clientId.Trim());
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                accessToken.Trim());
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false));
            var data = document.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0)
            {
                return new TwitchSnapshot(channel.Trim(), false, 0, string.Empty, string.Empty);
            }

            var stream = data[0];
            return new TwitchSnapshot(
                stream.GetProperty("user_login").GetString() ?? channel.Trim(),
                true,
                stream.GetProperty("viewer_count").GetInt32(),
                stream.GetProperty("game_name").GetString() ?? string.Empty,
                stream.GetProperty("title").GetString() ?? string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VrcCompanion/1.0");
        return client;
    }
}

/// <summary>Soundpad remote-control pipe. Read-only polling of the last clip.</summary>
public sealed class SoundpadService : IDisposable
{
    private const string PipeName = "sp_remote_control";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private DateTime _lastPoll = DateTime.MinValue;

    public async Task<string?> PollAsync(CancellationToken cancellationToken = default)
    {
        if (DateTime.UtcNow - _lastPoll < TimeSpan.FromSeconds(2))
        {
            return null;
        }

        _lastPoll = DateTime.UtcNow;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pipe is null || !_pipe.IsConnected)
            {
                _pipe?.Dispose();
                _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await _pipe.ConnectAsync(300, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        _pipe.ReadMode = PipeTransmissionMode.Message;
                    }
                    catch (IOException)
                    {
                        // Byte mode works too; the read timeout bounds it.
                    }
                }
                catch
                {
                    _pipe.Dispose();
                    _pipe = null;
                    return null;
                }
            }

            var status = await SendAsync("GetPlayStatus()", cancellationToken).ConfigureAwait(false);
            if (!string.Equals(status?.Trim(), "PLAYING", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status?.Trim(), "PAUSED", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var title = await SendAsync("GetTitleText()", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(title) || title.StartsWith("R-", StringComparison.Ordinal))
            {
                return null;
            }

            var clean = Regex.Replace(title, @"\s*\[.*?\]\s*", " ").Trim();
            clean = Regex.Replace(clean, @"^(?:II\s+)?Soundpad\s*-\s*", string.Empty, RegexOptions.IgnoreCase);
            return clean.Length == 0 ? "Soundpad clip" : clean;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> SendAsync(string command, CancellationToken cancellationToken)
    {
        if (_pipe is null)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(1500);
            await _pipe.WriteAsync(Encoding.UTF8.GetBytes(command), timeout.Token).ConfigureAwait(false);
            await _pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            using var memory = new MemoryStream();
            while (true)
            {
                var read = await _pipe.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                memory.Write(buffer, 0, read);
                if (read < buffer.Length)
                {
                    break;
                }
            }

            return memory.Length == 0
                ? null
                : Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length).TrimEnd('\0');
        }
        catch
        {
            _pipe.Dispose();
            _pipe = null;
            return null;
        }
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _gate.Dispose();
    }
}
