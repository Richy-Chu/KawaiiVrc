using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VrcCompanion.Controls;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class BackdropDecodeTests
{
    [Theory]
    // The wallpaper is stretched to the window at paint time, so decoding above the file's own
    // width only upscales it and holds pixels that were never in the file.
    [InlineData(2048, 280, 280)]
    [InlineData(1280, 480, 480)]
    [InlineData(2048, 3840, 2048)]
    [InlineData(2048, 0, 2048)]
    public void ABackdropIsNeverDecodedAboveItsSource(int requested, int sourceWidth, int expected)
    {
        Assert.Equal(expected, ImageBackdrop.ClampBackdropWidth(requested, sourceWidth));
    }

    [Fact]
    public void ASmallGifIsDecodedAtItsOwnSize()
    {
        // The shipped shape: a 280x275, 66-frame GIF asked for at 1280 px. WPF decoded it at
        // 1280x1257, so the animation held about 405 MB; at its own size it holds 20 MB.
        var width = ImageBackdrop.AnimatedBackdropWidth(1280, 280, 275, 66);

        Assert.Equal(280, width);
        Assert.True(ImageBackdrop.FrameMemoryBytes(width, 280, 275, 66) <= 24L * 1024 * 1024);
    }

    [Fact]
    public void ALongGifIsHeldToTheFrameBudget()
    {
        var width = ImageBackdrop.AnimatedBackdropWidth(1280, 1280, 720, 200);

        Assert.True(width > 0);
        Assert.True(width < 1280);
        Assert.True(
            ImageBackdrop.FrameMemoryBytes(width, 1280, 720, 200)
                <= ImageBackdrop.AnimatedBackdropFrameBudgetBytes);
    }

    [Fact]
    public void AShortGifKeepsTheRequestedWidth()
    {
        Assert.Equal(1280, ImageBackdrop.AnimatedBackdropWidth(1280, 1280, 720, 8));
    }

    [Fact]
    public void AGifTooLongToAnimateBecomesAStill()
    {
        // Five thousand frames cannot fit the budget at any useful width, so the picture is shown
        // as its first frame rather than holding a gigabyte of decoded frames.
        Assert.Equal(0, ImageBackdrop.AnimatedBackdropWidth(1280, 1280, 720, 5000));
    }

    [Fact]
    public void AnAnimationIsDecodedAtItsSourceWidth()
    {
        var bytes = AnimatedGif(6, 64, 48);

        var data = Assert.IsAssignableFrom<AnimatedImageData>(
            ImageBackdrop.DecodeAnimation(bytes, 1280, ImageBackdrop.MaxAnimatedBackdropFrames));

        Assert.Equal(6, data.Frames.Count);
        Assert.Equal(6, data.Durations.Count);
        Assert.All(
            data.Frames,
            frame =>
            {
                Assert.Equal(64, frame.PixelWidth);
                Assert.Equal(48, frame.PixelHeight);
            });
    }

    [Fact]
    public void AnAnimationIsDecodedAtTheRequestedWidthWhenItIsSmallerThanTheFile()
    {
        var bytes = AnimatedGif(4, 160, 120);

        var data = Assert.IsAssignableFrom<AnimatedImageData>(
            ImageBackdrop.DecodeAnimation(bytes, 80, ImageBackdrop.MaxAnimatedBackdropFrames));

        Assert.All(
            data.Frames,
            frame =>
            {
                Assert.Equal(80, frame.PixelWidth);
                Assert.Equal(60, frame.PixelHeight);
            });
    }

    [Fact]
    public void AFrameCapDropsFramesButKeepsTheLoopTime()
    {
        var bytes = AnimatedGif(20, 32, 32);
        var full = Assert.IsAssignableFrom<AnimatedImageData>(
            ImageBackdrop.DecodeAnimation(bytes, 32, ImageBackdrop.MaxAnimatedBackdropFrames));

        var capped = Assert.IsAssignableFrom<AnimatedImageData>(ImageBackdrop.DecodeAnimation(bytes, 32, 5));

        Assert.Equal(20, full.Frames.Count);
        Assert.True(capped.Frames.Count <= 5, $"kept {capped.Frames.Count} frames");
        Assert.Equal(capped.Frames.Count, capped.Durations.Count);
        // A kept frame stands in for the frames dropped after it, so a shortened animation does
        // not play any faster than the file asked for.
        Assert.Equal(full.Durations.Sum(), capped.Durations.Sum());
        Assert.All(capped.Durations, duration => Assert.InRange(duration, 20, 10_000));
    }

    [Fact]
    public void AFrameCapLeavesAnOrdinaryAnimationAlone()
    {
        // The shipped 66-frame wallpaper is under the cap, so its motion is unchanged.
        Assert.Equal(1, ImageBackdrop.AnimationFrameStride(66, ImageBackdrop.MaxAnimatedBackdropFrames));
        Assert.Equal(2, ImageBackdrop.AnimationFrameStride(66, 48));
        Assert.Equal(1, ImageBackdrop.AnimationFrameStride(1, 96));
        Assert.Equal(1, ImageBackdrop.AnimationFrameStride(0, 96));
    }

    [Fact]
    public void AStillGifDecodesToOneFrame()
    {
        var data = Assert.IsAssignableFrom<AnimatedImageData>(
            ImageBackdrop.DecodeAnimation(AnimatedGif(1, 32, 32), 32, ImageBackdrop.MaxAnimatedBackdropFrames));

        Assert.Single(data.Frames);
    }

    /// <summary>A real animated GIF, written by WPF's own encoder so the test does not fake the format.</summary>
    private static byte[] AnimatedGif(int frameCount, int width, int height)
    {
        var encoder = new GifBitmapEncoder();
        for (var index = 0; index < frameCount; index++)
        {
            var pixels = new byte[width * height * 4];
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = (byte)(index * 8);
                pixels[offset + 1] = 0x80;
                pixels[offset + 2] = 0x40;
                pixels[offset + 3] = 0xFF;
            }

            var frame = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                width * 4);
            encoder.Frames.Add(BitmapFrame.Create(frame));
        }

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
