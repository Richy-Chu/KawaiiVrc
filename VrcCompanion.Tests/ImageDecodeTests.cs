using System;
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
