using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using VrcCompanion.Services;

namespace VrcCompanion.Controls;

/// <summary>
/// Full-window custom background from a PNG, JPEG, WebP, or animated GIF.
///
/// A still is a frozen <see cref="BitmapImage"/>. An animation is decoded by Skia into frozen
/// frames at the width the wallpaper is drawn at, and a timer shows one frame at a time.
/// WpfAnimatedGif used to drive the animation, but it keeps a writeable copy of every frame on
/// top of the decoded frames - measured at 148 writeable bitmaps (about 45 MB) for the shipped
/// 66-frame wallpaper - while WPF's own decoder was asked for every frame at once.
/// </summary>
public sealed class ImageBackdrop : Image
{
    private const int MaxBackdropDecodeWidth = 2048;
    // Animated GIFs keep every frame in memory. Decode them at a smaller size
    // than a still wallpaper; motion hides the difference and the saving is large.
    private const int MaxAnimatedBackdropDecodeWidth = 1280;
    // A decorative wallpaper is drawn inside the window, so asking for more pixels than the file
    // has only upscales it: WPF decoded a 280x275 GIF at 1280x1257 when told 1280, which was
    // about 405 MB of frames for that one 0.9 MB file. The decode stops at the source width now,
    // and a long animation is held to a frame budget as well.
    private const int MinAnimatedBackdropDecodeWidth = 320;
    internal const long AnimatedBackdropFrameBudgetBytes = 96L * 1024 * 1024;
    // Long animations give up frames rather than resolution: the budget already shrinks the
    // decode width, and past this many frames the picture would be a blur before it fits.
    internal const int MaxAnimatedBackdropFrames = 96;
    private const double AnimationSpeedRatio = 0.75;
    private const int DefaultFrameMilliseconds = 100;
    private const int MinFrameDelayMilliseconds = 20;
    private const int MaxFrameDelayMilliseconds = 10_000;

    private Window? _window;
    private bool _isActive = true;
    private string? _loadedPath;
    private bool _loadedAnimate;
    private bool _isLoaded;
    private readonly DispatcherTimer _timer;
    private AnimatedImageData? _animation;
    private int _frameIndex;

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath),
        typeof(string),
        typeof(ImageBackdrop),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate),
        typeof(bool),
        typeof(ImageBackdrop),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    static ImageBackdrop()
    {
        // Cover the whole area while preserving aspect ratio.
        StretchProperty.OverrideMetadata(
            typeof(ImageBackdrop),
            new FrameworkPropertyMetadata(System.Windows.Media.Stretch.UniformToFill));
    }

    public ImageBackdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        // Render priority matches the decoration animations: a background a frame behind is
        // invisible, and it keeps playback from holding up layout.
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += OnFrameTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ImageBackdrop)d).LoadImage();

    private void LoadImage()
    {
        var path = SourcePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ReleaseImage();
            return;
        }

        // The profile backdrop lives in a collapsed popup. Do not hold a second
        // full-size image (or every GIF frame) while that popup is closed.
        if (!IsVisible || !_isActive)
        {
            return;
        }

        // Restore may raise both StateChanged and Activated. Decode once.
        if (_isLoaded &&
            string.Equals(_loadedPath, path, StringComparison.Ordinal) &&
            _loadedAnimate == Animate)
        {
            return;
        }

        try
        {
            var isGif = string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);
            var info = ReadImageInfo(path);

            // Only the frames that are actually kept count against the budget, so a very long
            // animation is thinned out instead of being shrunk to an unreadable strip.
            var frames = info is { } known
                ? Math.Min(known.Frames, MaxAnimatedBackdropFrames)
                : MaxAnimatedBackdropFrames;
            var animationWidth = isGif && Animate
                ? info is { } gif
                    ? AnimatedBackdropWidth(
                        MaxAnimatedBackdropDecodeWidth,
                        gif.Width,
                        gif.Height,
                        frames)
                    : MaxAnimatedBackdropDecodeWidth
                : 0;

            // A GIF too long to hold inside the budget is drawn as its first frame instead;
            // animating it would cost hundreds of megabytes for a background.
            if (isGif && Animate && animationWidth > 0)
            {
                // Decoding the whole animation is a one-time cost at load, the same work WPF's
                // decoder did here before, and it holds only the frames that are kept.
                var animation = DecodeAnimation(path, animationWidth, frames);
                if (animation is { Frames.Count: > 0 })
                {
                    ShowAnimation(animation, path);
                    return;
                }
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path, UriKind.Absolute);
            // Do not decode a 4K wallpaper at full size just to draw it behind
            // a 1080p/1440p window. This is the largest single VRAM saving for
            // custom backgrounds while staying above normal window widths, and
            // the source width keeps a small picture from being upscaled to it.
            image.DecodePixelWidth = info is { } still
                ? ClampBackdropWidth(BackdropDecodeWidth, still.Width)
                : BackdropDecodeWidth;
            image.EndInit();

            // A frozen bitmap only ever shows the first frame.
            image.Freeze();
            _timer.Stop();
            _animation = null;
            _frameIndex = 0;
            Source = image;
            _loadedPath = path;
            _loadedAnimate = Animate;
            _isLoaded = true;
            UpdateAnimationState();
        }
        catch
        {
            ReleaseImage();
        }
    }

    private void ShowAnimation(AnimatedImageData animation, string path)
    {
        _timer.Stop();
        _animation = animation;
        _frameIndex = 0;
        _loadedPath = path;
        _loadedAnimate = Animate;
        _isLoaded = true;
        ShowCurrentFrame();
        UpdateAnimationState();
    }

    private void ShowCurrentFrame()
    {
        if (_animation is not { Frames.Count: > 0 } animation)
        {
            return;
        }

        _frameIndex %= animation.Frames.Count;
        Source = animation.Frames[_frameIndex];

        var duration = _frameIndex < animation.Durations.Count
            ? animation.Durations[_frameIndex]
            : DefaultFrameMilliseconds;
        // WpfAnimatedGif played this at 75% speed; keep the motion people already saw.
        _timer.Interval = TimeSpan.FromMilliseconds(
            Math.Clamp(
                duration / AnimationSpeedRatio,
                MinFrameDelayMilliseconds,
                MaxFrameDelayMilliseconds));
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        if (_animation is not { Frames.Count: > 1 } animation)
        {
            _timer.Stop();
            return;
        }

        _frameIndex = (_frameIndex + 1) % animation.Frames.Count;
        ShowCurrentFrame();
    }

    private void ReleaseImage()
    {
        _timer.Stop();
        _animation = null;
        _frameIndex = 0;
        Source = null;
        _loadedPath = null;
        _isLoaded = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.Activated += OnWindowActivated;
            _window.Deactivated += OnWindowDeactivated;
            _window.StateChanged += OnWindowStateChanged;
            _isActive = _window.IsActive;
        }

        LoadImage();
        UpdateAnimationState();
    }

    private static int BackdropDecodeWidth
        => Math.Clamp(
            (int)Math.Round(SystemParameters.PrimaryScreenWidth * 1.25),
            1280,
            MaxBackdropDecodeWidth);

    /// <summary>
    /// A wallpaper is never drawn wider than the window, so asking for more pixels than the file
    /// has only upscales it into memory; WPF's stretch does that scaling at paint time for free.
    /// </summary>
    internal static int ClampBackdropWidth(int requestedWidth, int sourceWidth)
        => sourceWidth > 0 ? Math.Min(requestedWidth, sourceWidth) : requestedWidth;

    /// <summary>Resident bytes of one decoded animation, at the size it is decoded to.</summary>
    internal static long FrameMemoryBytes(
        int width,
        int sourceWidth,
        int sourceHeight,
        int frameCount)
    {
        if (width <= 0 || sourceWidth <= 0 || sourceHeight <= 0 || frameCount <= 0)
        {
            return 0;
        }

        var height = Math.Max(1, (int)Math.Floor(sourceHeight * (width / (double)sourceWidth)));
        return (long)frameCount * width * height * 4;
    }

    /// <summary>
    /// The width an animated backdrop is decoded at, or 0 when the animation cannot fit the frame
    /// budget even at the smallest useful width - such a GIF is shown as a still. What an animation
    /// costs grows with its frame count and its decoded size, not with the size it is drawn at.
    /// </summary>
    internal static int AnimatedBackdropWidth(
        int requestedWidth,
        int sourceWidth,
        int sourceHeight,
        int frameCount)
    {
        var width = ClampBackdropWidth(requestedWidth, sourceWidth);
        if (frameCount <= 1 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return width;
        }

        if (Fits(width))
        {
            return width;
        }

        // Solve for the width whose frame set fits the budget, then step down for the rounding.
        var area = AnimatedBackdropFrameBudgetBytes / (4.0 * frameCount);
        var budgetWidth = (int)Math.Floor(Math.Sqrt(area * sourceWidth / sourceHeight));
        width = Math.Max(MinAnimatedBackdropDecodeWidth, Math.Min(width, budgetWidth));

        while (width > MinAnimatedBackdropDecodeWidth && !Fits(width))
        {
            width--;
        }

        return Fits(width) ? width : 0;

        bool Fits(int candidate) =>
            FrameMemoryBytes(candidate, sourceWidth, sourceHeight, frameCount)
                <= AnimatedBackdropFrameBudgetBytes;
    }

    /// <summary>Every how many source frames one kept frame stands for; 1 keeps them all.</summary>
    internal static int AnimationFrameStride(int frameCount, int maxFrames)
        => frameCount <= 0 || maxFrames <= 0
            ? 1
            : Math.Max(1, (frameCount + maxFrames - 1) / maxFrames);

    /// <summary>
    /// Decodes an animated file's frames at the width the wallpaper is drawn at, keeping at most
    /// <paramref name="maxFrames"/> of them. Each kept frame stands in for the frames that were
    /// dropped after it, so the loop plays for as long as the file asked.
    /// </summary>
    internal static AnimatedImageData? DecodeAnimation(string path, int decodeWidth, int maxFrames)
    {
        try
        {
            return DecodeAnimation(File.ReadAllBytes(path), decodeWidth, maxFrames);
        }
        catch
        {
            return null;
        }
    }

    internal static AnimatedImageData? DecodeAnimation(byte[] bytes, int decodeWidth, int maxFrames)
    {
        try
        {
            using var stream = new SKMemoryStream(bytes);
            using var codec = SKCodec.Create(stream);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            {
                return null;
            }

            var sourceWidth = codec.Info.Width;
            var sourceHeight = codec.Info.Height;
            var targetWidth = ClampBackdropWidth(decodeWidth, sourceWidth);
            var targetHeight = Math.Max(
                1,
                (int)Math.Round(sourceHeight * (targetWidth / (double)sourceWidth)));
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
            var frameCount = Math.Max(1, codec.FrameCount);
            var stride = AnimationFrameStride(frameCount, maxFrames);
            var frameInfo = codec.FrameInfo;

            using var framePixels = new SKBitmap(sourceInfo);
            framePixels.Erase(SKColors.Transparent);
            var frames = new List<BitmapSource>();
            var durations = new List<int>();

            for (var index = 0; index < frameCount; index++)
            {
                // Passing the previous frame lets the codec composite disposal and
                // partial-frame updates instead of returning isolated rectangles.
                var options = new SKCodecOptions(index, index > 0 ? index - 1 : -1);
                if (codec.GetPixels(sourceInfo, framePixels.GetPixels(), options) != SKCodecResult.Success)
                {
                    continue;
                }

                var duration = frameInfo is not null && index < frameInfo.Length
                    ? Math.Clamp(frameInfo[index].Duration, MinFrameDelayMilliseconds, MaxFrameDelayMilliseconds)
                    : DefaultFrameMilliseconds;

                if (index % stride != 0 || frames.Count >= maxFrames)
                {
                    // A dropped frame still takes time: the kept frame before it stays on screen,
                    // so the loop keeps the file's length instead of speeding up.
                    if (durations.Count > 0)
                    {
                        durations[^1] = Math.Min(MaxFrameDelayMilliseconds, durations[^1] + duration);
                    }

                    continue;
                }

                BitmapSource? frame;
                if (targetWidth == sourceWidth && targetHeight == sourceHeight)
                {
                    frame = ImageLoader.ToBitmapSource(framePixels);
                }
                else
                {
                    using var scaled = framePixels.Resize(
                        targetInfo,
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    frame = scaled is null ? null : ImageLoader.ToBitmapSource(scaled);
                }

                if (frame is null)
                {
                    continue;
                }

                frames.Add(frame);
                durations.Add(duration);
            }

            return frames.Count == 0 ? null : new AnimatedImageData(frames, durations);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The frame count and source size from the file's header, without decoding pixels. Null when
    /// the format cannot be read that way, in which case the old requested width still applies.
    /// </summary>
    private static (int Width, int Height, int Frames)? ReadImageInfo(string path)
    {
        try
        {
            var decoder = BitmapDecoder.Create(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var frame = decoder.Frames[0];
            return frame.PixelWidth > 0 && frame.PixelHeight > 0
                ? (frame.PixelWidth, frame.PixelHeight, decoder.Frames.Count)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
            _window.StateChanged -= OnWindowStateChanged;
            _window = null;
        }

        ReleaseImage();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            LoadImage();
        }
        else
        {
            ReleaseImage();
        }

        UpdateAnimationState();
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _isActive = true;
        // Drop the backdrop while the app is in the background; its native
        // surfaces are one of the largest optional allocations in the process.
        LoadImage();
        UpdateAnimationState();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _isActive = false;
        ReleaseImage();
        UpdateAnimationState();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window?.WindowState == WindowState.Minimized)
        {
            ReleaseImage();
        }
        else if (_isActive)
        {
            LoadImage();
        }

        UpdateAnimationState();
    }

    private void UpdateAnimationState()
    {
        var playing = Animate
            && _isActive
            && IsVisible
            && _window?.WindowState != WindowState.Minimized
            && _animation is { Frames.Count: > 1 };

        if (!playing)
        {
            _timer.Stop();
            return;
        }

        ShowCurrentFrame();
        _timer.Start();
    }
}
