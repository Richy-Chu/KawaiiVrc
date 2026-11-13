using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VrcCompanion.Services;

namespace VrcCompanion.Controls;

/// <summary>
/// A lightweight rounded image that starts loading only when it is actually
/// visible. Because the friend and notification lists are virtualized, this
/// means avatars are fetched for rows the user can see instead of every friend
/// on the account. Rows recycle cleanly when the source URL changes.
/// </summary>
public sealed class AsyncImage : FrameworkElement
{
    public static readonly DependencyProperty SourceUrlProperty = DependencyProperty.Register(
        nameof(SourceUrl),
        typeof(string),
        typeof(AsyncImage),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceUrlChanged));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius),
        typeof(double),
        typeof(AsyncImage),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.Register(
        nameof(DecodeWidth),
        typeof(int),
        typeof(AsyncImage),
        new PropertyMetadata(96));

    public static readonly DependencyProperty ImageOpacityProperty = DependencyProperty.Register(
        nameof(ImageOpacity),
        typeof(double),
        typeof(AsyncImage),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private CancellationTokenSource? _cancellation;
    private ImageSource? _image;
    private bool _isLoaded;

    public AsyncImage()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public string? SourceUrl
    {
        get => (string?)GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
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

        var image = _image;
        if (image is null || ActualWidth <= 0 || ActualHeight <= 0 || ImageOpacity <= 0)
        {
            return;
        }

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var radius = Math.Min(CornerRadius, Math.Min(ActualWidth, ActualHeight) / 2);
        drawingContext.PushClip(new RectangleGeometry(bounds, radius, radius));
        drawingContext.PushOpacity(ImageOpacity);

        // Uniform-to-fill: scale up to cover, then center-crop with the clip.
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

    private static void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AsyncImage)d).RestartLoad();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        RestartLoad();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        CancelLoad();
        ReleaseImage();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            RestartLoad();
        }
        else
        {
            CancelLoad();
            ReleaseImage();
        }
    }

    private void ReleaseImage()
    {
        if (_image is null)
        {
            return;
        }

        _image = null;
        InvalidateVisual();
    }

    private async void RestartLoad()
    {
        CancelLoad();

        var url = SourceUrl;
        if (!_isLoaded || !IsVisible || string.IsNullOrWhiteSpace(url))
        {
            if (_image is not null)
            {
                _image = null;
                InvalidateVisual();
            }
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        var image = await ImageLoader.Shared.LoadAsync(url, DecodeWidth, cancellation.Token);
        if (cancellation.IsCancellationRequested ||
            !string.Equals(url, SourceUrl, StringComparison.Ordinal))
        {
            return;
        }

        if (image is null)
        {
            return;
        }

        _image = image;
        ImageOpacity = 0;
        InvalidateVisual();
        BeginAnimation(
            ImageOpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void CancelLoad()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }
}
