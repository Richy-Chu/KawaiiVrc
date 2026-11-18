using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VrcCompanion.Services;

namespace VrcCompanion.Controls;

/// <summary>
/// Rounded decoration artwork that plays the intro asset once and then loops the
/// main animation. VRChat decorations are animated WebP files; WPF shows only
/// their first frame, so the frames are decoded by Skia and advanced here.
/// Static artwork still works through the same element.
/// </summary>
public sealed class CosmeticImage : FrameworkElement
{
    public static readonly DependencyProperty SourceUrlProperty = DependencyProperty.Register(
        nameof(SourceUrl),
        typeof(string),
        typeof(CosmeticImage),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    public static readonly DependencyProperty IntroUrlProperty = DependencyProperty.Register(
        nameof(IntroUrl),
        typeof(string),
        typeof(CosmeticImage),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius),
        typeof(double),
        typeof(CosmeticImage),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.Register(
        nameof(DecodeWidth),
        typeof(int),
        typeof(CosmeticImage),
        new PropertyMetadata(160, OnSourceChanged));

    public static readonly DependencyProperty ImageOpacityProperty = DependencyProperty.Register(
        nameof(ImageOpacity),
        typeof(double),
        typeof(CosmeticImage),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cancellation;
    private AnimatedImageData? _intro;
    private AnimatedImageData? _main;
    private ImageSource? _staticImage;
    private ImageSource? _frame;
    private int _frameIndex;
    private int _loadVersion;
    private bool _playingIntro;
    private bool _isLoaded;

    public CosmeticImage()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public string? SourceUrl
    {
        get => (string?)GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    public string? IntroUrl
    {
        get => (string?)GetValue(IntroUrlProperty);
        set => SetValue(IntroUrlProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public int DecodeWidth
    {
        get => (int)GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    public double ImageOpacity
    {
        get => (double)GetValue(ImageOpacityProperty);
        set => SetValue(ImageOpacityProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var image = _frame ?? _staticImage;
        if (image is null || ActualWidth <= 0 || ActualHeight <= 0 || ImageOpacity <= 0)
        {
            return;
        }

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var radius = Math.Min(CornerRadius, Math.Min(ActualWidth, ActualHeight) / 2);
        drawingContext.PushClip(new RectangleGeometry(bounds, radius, radius));
        drawingContext.PushOpacity(ImageOpacity);

        var imageWidth = image.Width > 0 ? image.Width : ActualWidth;
        var imageHeight = image.Height > 0 ? image.Height : ActualHeight;
        var scale = Math.Max(ActualWidth / imageWidth, ActualHeight / imageHeight);
        var drawWidth = imageWidth * scale;
        var drawHeight = imageHeight * scale;
        drawingContext.DrawImage(
            image,
            new Rect(
                (ActualWidth - drawWidth) / 2,
                (ActualHeight - drawHeight) / 2,
                drawWidth,
                drawHeight));

        drawingContext.Pop();
        drawingContext.Pop();
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CosmeticImage)d).RestartLoad();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        RestartLoad();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _loadVersion++;
        CancelLoad();
        StopAnimation();
        ReleaseArtwork();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            RestartLoad();
        }
        else
        {
            _loadVersion++;
            CancelLoad();
            StopAnimation();
            ReleaseArtwork();
        }
    }

    private void RestartLoad()
    {
        _loadVersion++;
        CancelLoad();
        StopAnimation();
        ReleaseArtwork();

        var url = SourceUrl;
        if (!_isLoaded || !IsVisible || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _ = LoadAsync(url, IntroUrl, cancellation, _loadVersion);
    }

    private async Task LoadAsync(
        string url,
        string? introUrl,
        CancellationTokenSource cancellation,
        int version)
    {
        var width = DecodeWidth;
        var mainTask = ImageLoader.Shared.LoadAnimationAsync(url, width, cancellation.Token);
        var introTask = string.IsNullOrWhiteSpace(introUrl)
            ? Task.FromResult<AnimatedImageData?>(null)
            : ImageLoader.Shared.LoadAnimationAsync(introUrl, width, cancellation.Token);

        await Task.WhenAll(mainTask, introTask);
        if (cancellation.IsCancellationRequested || version != _loadVersion)
        {
            return;
        }

        var main = await mainTask;
        var intro = await introTask;
        if (main is null && _staticImage is null)
        {
            _staticImage = await ImageLoader.Shared.LoadAsync(url, width, cancellation.Token);
            if (cancellation.IsCancellationRequested || version != _loadVersion)
            {
                return;
            }
        }

        _main = main;
        _intro = intro is { Frames.Count: > 1 } ? intro : null;
        _playingIntro = _intro is not null;
        _frameIndex = 0;
        _frame = null;
        ShowCurrentFrame();
        ImageOpacity = 0;
        InvalidateVisual();
        BeginAnimation(
            ImageOpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void ShowCurrentFrame()
    {
        var frames = _playingIntro ? _intro?.Frames : _main?.Frames;
        var durations = _playingIntro ? _intro?.Durations : _main?.Durations;
        if (frames is null || frames.Count == 0)
        {
            if (!_playingIntro)
            {
                _frame = null;
                StopAnimation();
                InvalidateVisual();
            }
            return;
        }

        _frameIndex = Math.Clamp(_frameIndex, 0, frames.Count - 1);
        _frame = frames[_frameIndex];
        InvalidateVisual();

        if (frames.Count <= 1)
        {
            StopAnimation();
            return;
        }

        var delay = durations is not null && _frameIndex < durations.Count
            ? durations[_frameIndex]
            : 100;
        _timer.Stop();
        // Decorative animations do not need to run faster than 30 fps. Letting
        // a 50 fps source drive WPF at full rate burns CPU for no visible gain.
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(delay, 33, 10_000));
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_playingIntro && _intro is not null)
        {
            if (_frameIndex + 1 < _intro.Frames.Count)
            {
                _frameIndex++;
                ShowCurrentFrame();
                return;
            }

            _playingIntro = false;
            _frameIndex = 0;
            ShowCurrentFrame();
            return;
        }

        if (_main is null || _main.Frames.Count == 0)
        {
            StopAnimation();
            return;
        }

        _frameIndex = (_frameIndex + 1) % _main.Frames.Count;
        ShowCurrentFrame();
    }

    private void StopAnimation()
    {
        _timer.Stop();
    }

    private void ReleaseArtwork()
    {
        if (_frame is null && _staticImage is null)
        {
            return;
        }

        _main = null;
        _intro = null;
        _staticImage = null;
        _frame = null;
        InvalidateVisual();
    }

    private void CancelLoad()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }
}
