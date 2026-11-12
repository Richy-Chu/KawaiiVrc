using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace VrcCompanion.Services;

/// <summary>
/// Shared avatar/world image cache. Images are downloaded outside the API
/// throttle, decoded to the size the UI actually needs, kept in memory for the
/// session, and written to a disk cache so reopening the app is instant.
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
    private readonly SemaphoreSlim _gate = new(4, 4);
    private readonly string _cacheDirectory;

    public ImageLoader()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = _cookies,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate |
                                     System.Net.DecompressionMethods.Brotli
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
                return LoadGifFromCache(url, bytes);
            }

            var image = TryDecodeWpf(bytes, decodeWidth) ?? TryDecodeSkia(bytes, decodeWidth);
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

    private ImageSource? LoadGifFromCache(string url, byte[] bytes)
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
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.IsFile)
        {
            bytes = await File.ReadAllBytesAsync(absolute.LocalPath).ConfigureAwait(false);
        }
        else if (Path.IsPathRooted(url) && File.Exists(url))
        {
            bytes = await File.ReadAllBytesAsync(url).ConfigureAwait(false);
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

        if (cachePath is not null && bytes.Length > 0)
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
        _memory[key] = new MemoryEntry(image, DateTimeOffset.UtcNow);
        if (_memory.Count <= 256)
        {
            return;
        }

        foreach (var entry in _memory
                     .OrderBy(pair => pair.Value.UsedAt)
                     .Take(64)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _memory.TryRemove(entry, out _);
        }
    }

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
            const long maxBytes = 96L * 1024 * 1024;
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

    public void Dispose()
    {
        _http.Dispose();
        // Keep _gate alive: background image loads may still be releasing it
        // while the app is shutting down. The process is about to exit anyway.
    }

    private sealed class MemoryEntry
    {
        public MemoryEntry(ImageSource image, DateTimeOffset usedAt)
        {
            Image = image;
            UsedAt = usedAt;
        }

        public ImageSource Image { get; }
        public DateTimeOffset UsedAt { get; set; }
    }
}
