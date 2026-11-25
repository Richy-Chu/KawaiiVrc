using VrcCompanion.Controls;
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
}
