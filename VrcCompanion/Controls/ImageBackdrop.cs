using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using VrcCompanion.Services;
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

            if (isGif && Animate && !AppPerformance.LiteMode)
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
                animation.DecodePixelWidth = AnimatedBackdropDecodeWidth;
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
            // custom backgrounds while staying above normal window widths.
            image.DecodePixelWidth = BackdropDecodeWidth;
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

    private static int AnimatedBackdropDecodeWidth
        => Math.Min(BackdropDecodeWidth, MaxAnimatedBackdropDecodeWidth);

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