using System.Windows.Media.Imaging;

namespace VrcCompanion.Services;

/// <summary>Decoded frames and per-frame delays for one decoration animation.</summary>
public sealed class AnimatedImageData
{
    public AnimatedImageData(IReadOnlyList<BitmapSource> frames, IReadOnlyList<int> durations)
    {
        Frames = frames;
        Durations = durations;
    }

    public IReadOnlyList<BitmapSource> Frames { get; }

    public IReadOnlyList<int> Durations { get; }

    public long EstimatedBytes => Frames.Sum(
        frame => (long)frame.PixelWidth * frame.PixelHeight * 4);
}
