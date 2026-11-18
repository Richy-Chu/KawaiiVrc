using System.IO;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class CosmeticAnimationTests
{
    // Three 1x1 frames (red, green, blue) with a 100 ms delay, from Flutter's
    // public animated-placeholder decoder fixture.
    private const string AnimatedGif =
        "R0lGODlhAQABAKEDAAAA//8AAAD/AP///yH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgD/ACwAAAAAAQABAAACAkwBACH5BAAKAP8ALAAAAAABAAEAAAICVAEAIfkEAAoA/wAsAAAAAAEAAQAAAgJEAQA7";

    [Fact]
    public async Task AnimatedGifDecodesEveryFrameWithItsDelay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vrc-cosmetic-{Guid.NewGuid():N}.gif");
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(AnimatedGif));

        try
        {
            var animation = await ImageLoader.Shared.LoadAnimationAsync(path, 64);

            Assert.NotNull(animation);
            Assert.Equal(3, animation.Frames.Count);
            Assert.Equal(3, animation.Durations.Count);
            Assert.All(animation.Durations, delay => Assert.Equal(100, delay));
            Assert.All(animation.Frames, frame => Assert.Equal(1, frame.PixelWidth));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
