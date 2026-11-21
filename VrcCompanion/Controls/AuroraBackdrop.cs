using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using VrcCompanion.Services;

namespace VrcCompanion.Controls;

/// <summary>
/// Slow-moving aurora blobs and a subtle star field behind the whole app.
/// Rendering is capped at 15 fps and stops completely when the window is
/// minimized or inactive, so it stays decorative instead of expensive.
/// </summary>
public sealed class AuroraBackdrop : FrameworkElement
{
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant),
        typeof(string),
        typeof(AuroraBackdrop),
        new FrameworkPropertyMetadata("Aurora", FrameworkPropertyMetadataOptions.AffectsRender, OnAppearanceChanged));

    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate),
        typeof(bool),
        typeof(AuroraBackdrop),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnAppearanceChanged));

    private static readonly Star[] Stars = CreateStars();

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Window? _window;
    private bool _isHooked;
    private bool _isVisible = true;
    private bool _isActive = true;
    private long _lastFrameTicks;

    private readonly SolidColorBrush _starBrush = FrozenBrush("#B3FFFFFF");
    private readonly SolidColorBrush _accentStarBrush = FrozenBrush("#88FFFFFF");
    private SolidColorBrush _baseBrush = FrozenBrush("#070910");
    private RadialGradientBrush[] _blobBrushes = Array.Empty<RadialGradientBrush>();

    public AuroraBackdrop()
    {
        IsHitTestVisible = false;
        // The backdrop is soft gradients and tiny stars, so a half-resolution
        // cache cuts fill-rate cost without a visible loss at normal sizes.
        CacheMode = new BitmapCache { RenderAtScale = 0.5 };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        ApplyVariant(Variant);
    }

    public string? Variant
    {
        get => (string?)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        dc.DrawRectangle(_baseBrush, null, new Rect(0, 0, width, height));

        var time = Animate ? _clock.Elapsed.TotalSeconds : 0;

        // Large, slow aurora fields. Brushes are frozen and reused.
        var minSide = Math.Min(width, height);
        DrawBlob(dc, 0, time, width, height, minSide, 0.62, 1.10, 0.95);
        DrawBlob(dc, 1, time, width, height, minSide, 0.52, 1.35, 0.80);
        DrawBlob(dc, 2, time, width, height, minSide, 0.72, 0.90, 1.30);
        DrawBlob(dc, 3, time, width, height, minSide, 0.42, 1.20, 1.10);

        // Twinkling stars, mostly still so the motion reads as ambient.
        foreach (var star in Stars)
        {
            var twinkle = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(time * star.Speed + star.Phase));
            var alpha = star.Brightness * twinkle;
            if (alpha <= 0.02)
            {
                continue;
            }

            var brush = star.Accent ? _accentStarBrush : _starBrush;
            dc.PushOpacity(alpha);
            dc.DrawEllipse(brush, null, new Point(star.X * width, star.Y * height), star.Size, star.Size);
            dc.Pop();
        }
    }

    private void DrawBlob(
        DrawingContext dc,
        int index,
        double time,
        double width,
        double height,
        double minSide,
        double opacity,
        double scaleX,
        double scaleY)
    {
        if (_blobBrushes.Length <= index)
        {
            return;
        }

        var center = BlobCenter(index, time, width, height);
        var radius = minSide * (index switch { 1 => 0.26, 2 => 0.16, 3 => 0.12, _ => 0.20 });
        dc.PushOpacity(opacity);
        dc.DrawEllipse(_blobBrushes[index], null, center, radius * scaleX, radius * scaleY);
        dc.Pop();
    }

    private static RadialGradientBrush CreateBlobBrush(Color color)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0.50), 0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0.20), 0.45));
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0), 1));
        brush.Freeze();
        return brush;
    }

    private static Point BlobCenter(int index, double time, double width, double height)
    {
        var (x, y) = index switch
        {
            1 => (0.84 + 0.09 * Math.Cos(time * 0.045 + 1.4), 0.24 + 0.10 * Math.Sin(time * 0.037 + 0.3)),
            2 => (0.55 + 0.24 * Math.Sin(time * 0.031 + 2.2), 0.88 + 0.08 * Math.Cos(time * 0.05 + 1.1)),
            3 => (0.16 + 0.08 * Math.Sin(time * 0.052 + 3.4), 0.72 + 0.12 * Math.Cos(time * 0.034 + 2.0)),
            _ => (0.18 + 0.12 * Math.Sin(time * 0.04 + 0.6), 0.14 + 0.10 * Math.Cos(time * 0.033 + 1.8))
        };

        return new Point(x * width, y * height);
    }

    private static void OnAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var backdrop = (AuroraBackdrop)d;
        if (e.Property == VariantProperty)
        {
            backdrop.ApplyVariant(e.NewValue as string);
        }
        backdrop.UpdateAnimationHook();
        backdrop.InvalidateVisual();
    }

    private void ApplyVariant(string? variant)
    {
        var palette = (variant ?? "Aurora").ToLowerInvariant() switch
        {
            "midnight" => new[]
            {
                Color.FromRgb(59, 130, 246),
                Color.FromRgb(6, 182, 212),
                Color.FromRgb(129, 140, 248),
                Color.FromRgb(29, 78, 216)
            },
            "ember" => new[]
            {
                Color.FromRgb(249, 115, 22),
                Color.FromRgb(244, 63, 94),
                Color.FromRgb(245, 158, 11),
                Color.FromRgb(219, 39, 119)
            },
            "sakura" => new[]
            {
                Color.FromRgb(244, 114, 182),
                Color.FromRgb(167, 139, 250),
                Color.FromRgb(251, 113, 133),
                Color.FromRgb(139, 92, 246)
            },
            _ => new[]
            {
                Color.FromRgb(139, 92, 246),
                Color.FromRgb(34, 211, 238),
                Color.FromRgb(244, 114, 182),
                Color.FromRgb(59, 130, 246)
            }
        };

        _blobBrushes = palette.Select(CreateBlobBrush).ToArray();
        _baseBrush = FrozenBrush(variant?.ToLowerInvariant() switch
        {
            "midnight" => "#060A16",
            "ember" => "#100806",
            "sakura" => "#0E0810",
            _ => "#070910"
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppPerformance.Changed += OnPerformanceChanged;
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.StateChanged += OnWindowStateChanged;
            _window.Activated += OnWindowActivated;
            _window.Deactivated += OnWindowDeactivated;
            _isActive = _window.IsActive;
        }
        UpdateAnimationHook();
        InvalidateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppPerformance.Changed -= OnPerformanceChanged;
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowStateChanged;
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        }
        UnhookAnimation();
    }

    private void OnPerformanceChanged()
    {
        // The performance mode is process-wide, so it can change from any thread
        // (a settings load, another window). Touching this control's composition
        // hook from that thread would throw, so the work always lands on the
        // dispatcher that owns it.
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(OnPerformanceChanged));
            return;
        }

        UpdateAnimationHook();
        InvalidateVisual();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _isVisible = IsVisible;
        UpdateAnimationHook();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        _isVisible = IsVisible && _window?.WindowState != WindowState.Minimized;
        _isActive = _window?.IsActive == true;
        UpdateAnimationHook();
        InvalidateVisual();
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _isActive = true;
        UpdateAnimationHook();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _isActive = false;
        UpdateAnimationHook();
    }

    private void UpdateAnimationHook()
    {
        // Lite mode paints one static frame: a 15 fps full-window repaint is the
        // single largest continuous cost in the app on a weak GPU.
        var shouldAnimate = Animate && !AppPerformance.LiteMode && _isVisible && _isActive && IsLoaded &&
                            _window?.WindowState != WindowState.Minimized;
        if (shouldAnimate && !_isHooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _isHooked = true;
        }
        else if (!shouldAnimate && _isHooked)
        {
            UnhookAnimation();
            InvalidateVisual();
        }
    }

    private void UnhookAnimation()
    {
        if (!_isHooked)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _isHooked = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = now - _lastFrameTicks;
        if (elapsed < Stopwatch.Frequency / 15)
        {
            return;
        }

        _lastFrameTicks = now;
        InvalidateVisual();
    }

    private static SolidColorBrush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Color WithAlpha(Color color, double alpha)
        => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);

    private static Star[] CreateStars()
    {
        var random = new Random(417);
        var stars = new Star[48];
        for (var i = 0; i < stars.Length; i++)
        {
            stars[i] = new Star
            {
                X = random.NextDouble(),
                Y = random.NextDouble(),
                Size = 0.5 + random.NextDouble() * 1.4,
                Phase = random.NextDouble() * Math.PI * 2,
                Speed = 0.25 + random.NextDouble() * 0.85,
                Brightness = 0.18 + random.NextDouble() * 0.55,
                Accent = i % 9 == 0
            };
        }

        return stars;
    }

    private sealed class Star
    {
        public double X { get; init; }
        public double Y { get; init; }
        public double Size { get; init; }
        public double Phase { get; init; }
        public double Speed { get; init; }
        public double Brightness { get; init; }
        public bool Accent { get; init; }
    }
}
