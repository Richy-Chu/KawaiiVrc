using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using VrcCompanion.Services;
using Xunit;
using Xunit.Abstractions;

namespace VrcCompanion.Tests;

public class ImageDecodeTests
{
    private readonly ITestOutputHelper _output;

    public ImageDecodeTests(ITestOutputHelper output) => _output = output;

    /// <summary>A real WebP, which WPF cannot read, so the tests drive the Skia path itself.</summary>
    private static byte[] Webp(int width, int height)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(new SKColor(0x40, 0x80, 0xC0));
        surface.Canvas.DrawRect(
            new SKRect(0, 0, width / 2f, height / 2f),
            new SKPaint { Color = new SKColor(0xFF, 0x80, 0xB0) });
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Webp, 90);
        return data.ToArray();
    }

    /// <summary>
    /// A 1080p screenshot, the format the media grid asks for at 160 px. PNG
    /// cannot scale while decoding, so it is the case that used to materialise
    /// ten megapixels per card.
    /// </summary>
    private static byte[] Screenshot(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)(offset % 251);
            pixels[offset + 1] = (byte)(offset % 241);
            pixels[offset + 2] = (byte)(offset % 239);
            pixels[offset + 3] = 255;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void AScreenshotIsNotDecodedAtItsSourceResolution()
    {
        var bytes = Screenshot(1920, 1080);

        // The codec either scales itself or declines and the fallback resizes;
        // what must never happen is a 1920 px frame handed to a 160 px card.
        var scaled = ImageLoader.DecodeSkiaScaled(bytes, 160) as BitmapSource;
        Assert.True(
            scaled is null || scaled.PixelWidth <= 200,
            $"scaled decode returned {scaled?.PixelWidth}x{scaled?.PixelHeight}");

        var fallback = Assert.IsAssignableFrom<BitmapSource>(ImageLoader.DecodeSkiaFullSize(bytes, 160));
        Assert.Equal(160, fallback.PixelWidth);
        Assert.Equal(90, fallback.PixelHeight);
    }

    [Fact]
    public async Task ALocalScreenshotLoadsAtTheWidthTheMediaGridAsksFor()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "decode-test-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(path, Screenshot(1920, 1080));
        try
        {
            var image = Assert.IsAssignableFrom<BitmapSource>(
                await new ImageLoader().LoadAsync(path, 160));

            Assert.InRange(image.PixelWidth, 32, 200);
            Assert.InRange(image.PixelHeight, 24, 120);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void APictureIsDecodedForItsBoxNotForTheSourceFile()
    {
        var bytes = Screenshot(1920, 1080);

        // The enlarged boop card draws a 160 DIP box and used to decode 640 px
        // wide; 320 is twice the box, which is what 200% scaling draws.
        var before = Assert.IsAssignableFrom<BitmapSource>(ImageLoader.DecodeSkiaFullSize(bytes, 640));
        var after = Assert.IsAssignableFrom<BitmapSource>(ImageLoader.DecodeSkiaFullSize(bytes, 320));

        Assert.Equal(640 * 360 * 4, before.PixelWidth * before.PixelHeight * 4);
        Assert.Equal(320 * 180 * 4, after.PixelWidth * after.PixelHeight * 4);
        _output.WriteLine(
            $"boop card: 640 px {before.PixelWidth * before.PixelHeight * 4:N0} B -> " +
            $"320 px {after.PixelWidth * after.PixelHeight * 4:N0} B");
    }

    [Fact]
    public void AWebpIsNotDecodedAtItsSourceResolution()
    {
        var bytes = Webp(1600, 1200);

        var image = Assert.IsAssignableFrom<BitmapSource>(ImageLoader.DecodeSkiaScaled(bytes, 96));

        // Some codecs only scale by whole factors, so the exact width is theirs to choose; what
        // matters is that a 96 px row never costs a 1600 px decode.
        Assert.InRange(image.PixelWidth, 32, 200);
        Assert.InRange(image.PixelHeight, 24, 150);
        Assert.True(
            Math.Abs(image.PixelWidth / (double)image.PixelHeight - 1600 / 1200.0) < 0.15,
            $"aspect changed: {image.PixelWidth}x{image.PixelHeight}");
    }

    [Fact]
    public void ATinyWebpIsNotUpscaled()
    {
        var image = Assert.IsAssignableFrom<BitmapSource>(
            ImageLoader.DecodeSkiaScaled(Webp(40, 30), 512));

        Assert.Equal(40, image.PixelWidth);
        Assert.Equal(30, image.PixelHeight);
    }

    [Fact]
    public void TheFullSizeDecodeStillWorksAsTheFallback()
    {
        var image = Assert.IsAssignableFrom<BitmapSource>(
            ImageLoader.DecodeSkiaFullSize(Webp(1600, 1200), 96));

        Assert.Equal(96, image.PixelWidth);
        Assert.Equal(72, image.PixelHeight);
    }

    [Fact]
    public void DecodingDoesNotAllocateASourceSizedPixelBuffer()
    {
        var bytes = Webp(1600, 1200);
        // Warm the codec and the WPF bitmap path so the measurement is the decode, not first-use
        // JIT and type loads.
        _ = ImageLoader.DecodeSkiaScaled(bytes, 96);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var image = Assert.IsAssignableFrom<BitmapSource>(ImageLoader.DecodeSkiaScaled(bytes, 96));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _output.WriteLine(
            $"1600x1200 webp at 96 px decoded to {image.PixelWidth}x{image.PixelHeight}, " +
            $"allocated {allocated:N0} managed bytes");
        // 1600x1200 pixels is 7.7 MB; the old path copied a full-size frame into a managed array
        // before scaling, which is a large-object allocation per image.
        Assert.True(allocated < 512 * 1024, $"decode allocated {allocated:N0} bytes");
    }
}
