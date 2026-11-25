using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using WpfAnimatedGif;

namespace VrcCompanion.Controls;

/// <summary>
/// Full-window custom background from a PNG, JPEG, WebP, or animated GIF.
///
/// Derives from Image so WPF's own GIF decoder drives the animation. WPF's
/// Image control handles palettes, transparency, disposal methods, and partial
/// frames correctly — far more reliable than manual frame extraction. When
/// Animate is false the BitmapImage is frozen so only the first frame shows.
/// </summary>
public sealed class ImageBackdrop : Image
{
    private const int MaxBackdropDecodeWidth = 2048;
    // Animated GIFs keep every frame in memory. Decode them at a smaller size
    // than a still wallpaper; motion hides the difference and the saving is large.
    private const int MaxAnimatedBackdropDecodeWidth = 1280;
    // A decorative wallpaper is drawn inside the window, so asking for more pixels than the file
    // has only upscales it: WPF decoded a 280x275 GIF at 1280x1257 when told 1280, and
    // WpfAnimatedGif holds every frame of an animation decoded at once, so that one 0.9 MB file
    // was about 405 MB of the process. The decode stops at the source width now, and a long
    // animation is held to a total frame budget as well.
    private const int MinAnimatedBackdropDecodeWidth = 320;
    internal const long AnimatedBackdropFrameBudgetBytes = 96L * 1024 * 1024;
    private const double AnimationSpeedRatio = 0.75;

    private Window? _window;
    private bool _isActive = true;
    private string? _loadedPath;
    private bool _loadedAnimate;
    private bool _isLoaded;

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

            var animationWidth = isGif && Animate
                ? info is { } gif
                    ? AnimatedBackdropWidth(
                        MaxAnimatedBackdropDecodeWidth,
                        gif.Width,
                        gif.Height,
                        gif.Frames)
                    : MaxAnimatedBackdropDecodeWidth
                : 0;

            // A GIF too long to hold inside the budget is drawn as its first frame instead;
            // animating it would cost hundreds of megabytes for a background.
            if (isGif && Animate && animationWidth > 0)
            {
                // WPF's BitmapImage alone renders only the first GIF frame.
                // ImageBehavior drives the frames; 75% keeps the motion close to
                // natural without triggering the much costlier full-rate path.
                ImageBehavior.SetAnimationSpeedRatio(this, AnimationSpeedRatio);
                var animation = new BitmapImage();
                animation.BeginInit();
                animation.CacheOption = BitmapCacheOption.OnLoad;
                animation.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                animation.UriSource = new Uri(path, UriKind.Absolute);
                animation.DecodePixelWidth = animationWidth;
                animation.EndInit();
                ImageBehavior.SetAnimatedSource(this, animation);
                _loadedPath = path;
                _loadedAnimate = Animate;
                _isLoaded = true;
                QueueAnimationStateUpdate();
                return;
            }

            ImageBehavior.SetAnimatedSource(this, null);
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

    private void ReleaseImage()
    {
        ImageBehavior.GetAnimationController(this)?.Pause();
        ImageBehavior.SetAnimatedSource(this, null);
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

    /// <summary>Resident bytes of one decoded animation, at the size WPF will decode it to.</summary>
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
    /// budget even at the smallest useful width - such a GIF is shown as a still. WpfAnimatedGif
    /// keeps every frame decoded at once, so what a long GIF costs grows with its frame count and
    /// its decoded size, not with the size it is drawn at.
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

    private void QueueAnimationStateUpdate()
        => Dispatcher.BeginInvoke(new Action(UpdateAnimationState));

    private void UpdateAnimationState()
    {
        var controller = ImageBehavior.GetAnimationController(this);
        if (controller is null)
        {
            return;
        }

        if (Animate && _isActive && IsVisible && _window?.WindowState != WindowState.Minimized)
        {
            controller.Play();
        }
        else
        {
            controller.Pause();
        }
    }
}