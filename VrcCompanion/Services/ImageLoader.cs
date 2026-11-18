using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace VrcCompanion.Services;

/// <summary>
/// Shared avatar/world image cache. Images are downloaded outside the API
/// throttle, decoded to the size the UI actually needs, kept in a bounded
/// in-memory cache, and written to a disk cache so reopening the app is instant.
///
/// WPF's decoder is used first. VRChat increasingly serves WebP files, which
/// older Windows installs cannot open, so SkiaSharp (MIT licensed) is used as
/// a fallback.
/// </summary>
public sealed class ImageLoader : IDisposable
{
    public static ImageLoader Shared { get; } = new();

    private static readonly Uri ImageBaseUri = new("https://api.vrchat.cloud/");
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcCompanion",
        "imagecache");

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, MemoryEntry> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AnimationEntry> _animations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<AnimatedImageData?>> _animationInflight = new(StringComparer.Ordinal);
    private readonly object _memoryGate = new();
    private readonly object _animationGate = new();
    // Keep enough visible artwork cached for smooth scrolling without letting
    // a long browsing session grow the WPF and GPU memory footprint forever.
    private const long MaxMemoryBytes = 24L * 1024 * 1024;
    private const int MaxMemoryEntries = 48;
    private const long MaxAnimationBytes = 12L * 1024 * 1024;
    private const int MaxAnimationEntries = 8;
    private const int MaxAnimationFrames = 32;
    private const int MaxAnimationDimension = 512;

    internal static long MemoryCacheLimitBytes => MaxMemoryBytes;

    internal long MemoryCacheBytes
    {
        get
        {
            lock (_memoryGate)
            {
                return _memoryBytes;
            }
        }
    }
    private long _memoryBytes;
    private long _animationBytes;
    private readonly SemaphoreSlim _gate = new(4, 4);
    private readonly SemaphoreSlim _fileGate = new(3, 3);
    private readonly SemaphoreSlim _decodeGate = new(3, 3);
    private readonly string _cacheDirectory;

    public ImageLoader()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = true,
            CookieContainer = _cookies,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate |
                                     System.Net.DecompressionMethods.Brotli,
            // avtr.zip serves valid thumbnails over IPv4, but its IPv6 route
            // stalls after a few KB. Prefer IPv4 and fall back to IPv6 only
            // when no IPv4 address can be reached.
            ConnectCallback = ConnectImageAsync
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VrcCompanion/1.1.0 (unofficial Windows companion)");

        _cacheDirectory = CacheDirectory;
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            _ = Task.Run(PruneDiskCache);
        }
        catch
        {
            // A read-only profile should not stop the app from working.
        }
    }

    private static async ValueTask<Stream> ConnectImageAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var ordered = addresses
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

        Exception? lastError = null;
        foreach (var address in ordered)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    attempt.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                lastError = ex;
                socket.Dispose();
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        throw new HttpRequestException(
            $"Could not connect to {context.DnsEndPoint.Host}.",
            lastError);
    }

    /// <summary>
    /// Keeps the session cookie in sync so avatar, group, and world images served
    /// from the VRChat API can be downloaded while signed in. Passing null clears it.
    /// </summary>
    public void SetAuthCookie(string? authCookie)
    {
        try
        {
            var cookie = string.IsNullOrWhiteSpace(authCookie)
                ? new Cookie("auth", string.Empty, "/", "api.vrchat.cloud")
                {
                    Expires = DateTime.UtcNow.AddDays(-1)
                }
                : new Cookie("auth", authCookie, "/", "api.vrchat.cloud")
                {
                    Secure = true,
                    HttpOnly = true
                };

            _cookies.Add(cookie);
        }
        catch
        {
            // A malformed cookie must not break image loading, which is best effort.
        }
    }

    public async Task<ImageSource?> LoadAsync(string? url, int decodeWidth, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var normalized = url.Trim();
        var key = $"{Math.Max(32, decodeWidth)}|{normalized}";
        if (_memory.TryGetValue(key, out var cached))
        {
            cached.UsedAt = DateTimeOffset.UtcNow;
            return cached.Image;
        }

        // Share a single download/decode between duplicate requests, but let an
        // individual caller stop waiting if its row scrolled away.
        var task = _inflight.GetOrAdd(
            key,
            _ => LoadCoreAsync(normalized, Math.Max(32, decodeWidth), key));

        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes every frame of a decoration animation. WPF's image pipeline only
    /// gives us the first frame of animated WebP/GIF files, so Skia produces the
    /// composited frames and their durations for the cosmetic controls to play.
    /// </summary>
    public async Task<AnimatedImageData?> LoadAnimationAsync(
        string? url,
        int decodeWidth,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var normalized = url.Trim();
        var key = $"anim|{Math.Max(32, decodeWidth)}|{normalized}";
        if (_animations.TryGetValue(key, out var cached))
        {
            cached.UsedAt = DateTimeOffset.UtcNow;
            return cached.Data;
        }

        var task = _animationInflight.GetOrAdd(
            key,
            _ => LoadAnimationCoreAsync(normalized, Math.Max(32, decodeWidth), key));

        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<AnimatedImageData?> LoadAnimationCoreAsync(string url, int decodeWidth, string key)
    {
        try
        {
            var bytes = await ReadBytesAsync(url).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0 || !IsImageBytes(bytes))
            {
                return null;
            }

            await _decodeGate.WaitAsync().ConfigureAwait(false);
            AnimatedImageData? data;
            try
            {
                data = DecodeAnimationSkia(bytes, decodeWidth);
            }
            finally
            {
                _decodeGate.Release();
            }

            if (data is not null)
            {
                AddAnimationToMemory(key, data);
            }

            return data;
        }
        catch
        {
            return null;
        }
        finally
        {
            _animationInflight.TryRemove(key, out _);
        }
    }

    private async Task<ImageSource?> LoadCoreAsync(string url, int decodeWidth, string key)
    {
        try
        {
            var bytes = await ReadBytesAsync(url).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            // GIFs are animated — WPF only animates them when loaded from a file Uri,
            // so save the raw bytes to disk and point a BitmapImage at the cached file.
            if (IsGif(bytes))
            {
                return LoadGifFromCache(url, bytes, decodeWidth);
            }

            // Local libraries can queue hundreds of pictures at once; decode a few
            // at a time so the UI thread and memory stay calm.
            await _decodeGate.WaitAsync().ConfigureAwait(false);
            ImageSource? image;
            try
            {
                image = TryDecodeWpf(bytes, decodeWidth) ?? TryDecodeSkia(bytes, decodeWidth);
            }
            finally
            {
                _decodeGate.Release();
            }

            if (image is null)
            {
                return null;
            }

            AddToMemory(key, image);
            return image;
        }
        catch
        {
            return null;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public static bool IsGifBytes(byte[] bytes)
    {
        return bytes.Length >= 6
            && ((bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38 && bytes[4] == 0x39 && bytes[5] == 0x61) // GIF89a
                || (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38 && bytes[4] == 0x37 && bytes[5] == 0x61)); // GIF87a
    }

    public static bool IsImageBytes(byte[] bytes)
    {
        if (bytes.Length < 4)
        {
            return false;
        }

        // GIF87a / GIF89a
        if (IsGifBytes(bytes))
        {
            return true;
        }

        // PNG: 89 50 4E 47
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return true;
        }

        // JPEG: FF D8 FF
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return true;
        }

        // WEBP: RIFF....WEBP
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return true;
        }

        return false;
    }

    private static bool IsGif(byte[] bytes)
    {
        return IsGifBytes(bytes);
    }

    private ImageSource? LoadGifFromCache(string url, byte[] bytes, int decodeWidth)
    {
        try
        {
            var cachePath = GetCachePath(url, ".gif");
            if (cachePath is null)
            {
                return null;
            }

            // Write raw GIF bytes to disk so WPF can animate from a file Uri.
            var temp = cachePath + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, cachePath, overwrite: true);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(cachePath, UriKind.Absolute);
            // Small animated avatars do not need their 1024px source decoded at
            // full resolution. This mirrors the still-image decode cap.
            image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private async Task<byte[]?> ReadBytesAsync(string url)
    {
        var cachePath = GetCachePath(url);
        if (cachePath is not null && File.Exists(cachePath))
        {
            try
            {
                return await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
            }
            catch
            {
                // Fall through to the network.
            }
        }

        byte[] bytes;
        var isLocalFile = false;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.IsFile)
        {
            isLocalFile = true;
            await _fileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                bytes = await File.ReadAllBytesAsync(absolute.LocalPath).ConfigureAwait(false);
            }
            finally
            {
                _fileGate.Release();
            }
        }
        else if (Path.IsPathRooted(url) && File.Exists(url))
        {
            isLocalFile = true;
            await _fileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                bytes = await File.ReadAllBytesAsync(url).ConfigureAwait(false);
            }
            finally
            {
                _fileGate.Release();
            }
        }
        else
        {
            var uri = Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                ? parsed
                : new Uri(ImageBaseUri, url);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                bytes = await _http.GetByteArrayAsync(uri).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        // Pictures already live on this disk; copying them into the download cache
        // would duplicate the whole library. Only network responses are cached.
        if (!isLocalFile && cachePath is not null && bytes.Length > 0)
        {
            try
            {
                var temp = cachePath + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
                File.Move(temp, cachePath, overwrite: true);
            }
            catch
            {
                // Disk caching is best-effort.
            }
        }

        return bytes;
    }

    private static BitmapImage? TryDecodeWpf(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = decodeWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes WebP (and anything else WPF cannot read) with SkiaSharp, scaling
    /// straight to the size the UI needs so the cache stays small.
    /// </summary>
    private static ImageSource? TryDecodeSkia(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var decoded = SKBitmap.Decode(bytes);
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
            {
                return null;
            }

            var targetWidth = Math.Clamp(decodeWidth, 32, decoded.Width);
            var targetHeight = Math.Max(
                1,
                (int)Math.Round(decoded.Height * (targetWidth / (double)decoded.Width)));

            var info = new SKImageInfo(
                targetWidth,
                targetHeight,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);

            using var scaled = decoded.Resize(
                info,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (scaled is null || scaled.Width <= 0 || scaled.Height <= 0)
            {
                return null;
            }

            // Skia decodes to premultiplied BGRA, which is WPF's Pbgra32 format.
            var pixels = scaled.GetPixelSpan().ToArray();
            var bitmap = BitmapSource.Create(
                scaled.Width,
                scaled.Height,
                96,
                96,
                PixelFormats.Pbgra32,
                null,
                pixels,
                scaled.RowBytes);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void AddToMemory(string key, ImageSource image)
    {
        var bytes = EstimateBytes(image);
        lock (_memoryGate)
        {
            if (_memory.TryRemove(key, out var previous))
            {
                _memoryBytes -= previous.Bytes;
            }

            _memory[key] = new MemoryEntry(image, DateTimeOffset.UtcNow, bytes);
            _memoryBytes += bytes;

            while (_memory.Count > MaxMemoryEntries || _memoryBytes > MaxMemoryBytes)
            {
                var oldest = _memory.OrderBy(pair => pair.Value.UsedAt).FirstOrDefault();
                if (oldest.Key is null || !_memory.TryRemove(oldest.Key, out var removed))
                {
                    break;
                }

                _memoryBytes -= removed.Bytes;
            }
        }
    }

    private static AnimatedImageData? DecodeAnimationSkia(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(bytes));
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            {
                return null;
            }

            var sourceWidth = codec.Info.Width;
            var sourceHeight = codec.Info.Height;
            var targetWidth = Math.Max(
                1,
                Math.Min(
                    Math.Max(32, decodeWidth),
                    Math.Min(sourceWidth, MaxAnimationDimension)));
            var targetHeight = Math.Max(
                1,
                (int)Math.Round(sourceHeight * (targetWidth / (double)sourceWidth)));
            if (targetHeight > MaxAnimationDimension)
            {
                targetWidth = Math.Max(
                    1,
                    (int)Math.Round(targetWidth * (MaxAnimationDimension / (double)targetHeight)));
                targetHeight = MaxAnimationDimension;
            }

            var frameInfo = codec.FrameInfo;
            var frameCount = Math.Clamp(codec.FrameCount, 1, MaxAnimationFrames);
            var sourceInfo = new SKImageInfo(
                sourceWidth,
                sourceHeight,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            var targetInfo = new SKImageInfo(
                targetWidth,
                targetHeight,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);

            using var framePixels = new SKBitmap(sourceInfo);
            framePixels.Erase(SKColors.Transparent);
            var frames = new List<BitmapSource>(frameCount);
            var durations = new List<int>(frameCount);

            for (var index = 0; index < frameCount; index++)
            {
                // Passing the previous frame lets the codec composite disposal and
                // partial-frame updates instead of returning isolated rectangles.
                var options = new SKCodecOptions(index, index > 0 ? index - 1 : -1);
                var result = codec.GetPixels(sourceInfo, framePixels.GetPixels(), options);
                if (result != SKCodecResult.Success)
                {
                    continue;
                }

                BitmapSource? frame;
                if (targetWidth == sourceWidth && targetHeight == sourceHeight)
                {
                    frame = ToBitmapSource(framePixels);
                }
                else
                {
                    using var scaled = framePixels.Resize(
                        targetInfo,
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    frame = scaled is null ? null : ToBitmapSource(scaled);
                }

                if (frame is null)
                {
                    continue;
                }

                frames.Add(frame);
                var duration = frameInfo is not null && index < frameInfo.Length
                    ? frameInfo[index].Duration
                    : 100;
                durations.Add(Math.Clamp(duration, 20, 10_000));
            }

            return frames.Count == 0 ? null : new AnimatedImageData(frames, durations);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource ToBitmapSource(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan().ToArray();
        var image = BitmapSource.Create(
            bitmap.Width,
            bitmap.Height,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            pixels,
            bitmap.RowBytes);
        image.Freeze();
        return image;
    }

    private void AddAnimationToMemory(string key, AnimatedImageData data)
    {
        lock (_animationGate)
        {
            if (_animations.TryRemove(key, out var previous))
            {
                _animationBytes -= previous.Data.EstimatedBytes;
            }

            _animations[key] = new AnimationEntry(data);
            _animationBytes += data.EstimatedBytes;

            while (_animations.Count > MaxAnimationEntries || _animationBytes > MaxAnimationBytes)
            {
                var oldest = _animations.OrderBy(pair => pair.Value.UsedAt).FirstOrDefault();
                if (oldest.Key is null || !_animations.TryRemove(oldest.Key, out var removed))
                {
                    break;
                }

                _animationBytes -= removed.Data.EstimatedBytes;
            }
        }
    }

    private static long EstimateBytes(ImageSource image)
        => image is BitmapSource bitmap
            ? (long)bitmap.PixelWidth * bitmap.PixelHeight * 4
            : 0;

    private string? GetCachePath(string url, string extension = ".img")
    {
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
            return Path.Combine(_cacheDirectory, hash[..40] + extension);
        }
        catch
        {
            return null;
        }
    }

    private void PruneDiskCache()
    {
        try
        {
            const long maxBytes = 64L * 1024 * 1024;
            var files = new DirectoryInfo(_cacheDirectory)
                .EnumerateFiles("*.img")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            long total = 0;
            foreach (var file in files)
            {
                total += file.Length;
                if (total > maxBytes)
                {
                    file.Delete();
                }
                else if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30))
                {
                    file.Delete();
                }
            }
        }
        catch
        {
            // Cache pruning must never take the app down.
        }
    }

    /// <summary>
    /// Drops decoded artwork while the app is in the background. The disk cache
    /// keeps the next visit fast without holding native WPF surfaces resident.
    /// </summary>
    public void TrimMemory()
    {
        lock (_memoryGate)
        {
            _memory.Clear();
            _memoryBytes = 0;
        }

        lock (_animationGate)
        {
            _animations.Clear();
            _animationBytes = 0;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        // Keep _gate alive: background image loads may still be releasing it
        // while the app is shutting down. The process is about to exit anyway.
    }

    private sealed class MemoryEntry
    {
        public MemoryEntry(ImageSource image, DateTimeOffset usedAt, long bytes)
        {
            Image = image;
            UsedAt = usedAt;
            Bytes = bytes;
        }

        public ImageSource Image { get; }
        public DateTimeOffset UsedAt { get; set; }
        public long Bytes { get; }
    }

    private sealed class AnimationEntry
    {
        public AnimationEntry(AnimatedImageData data)
        {
            Data = data;
        }

        public AnimatedImageData Data { get; }
        public DateTimeOffset UsedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
