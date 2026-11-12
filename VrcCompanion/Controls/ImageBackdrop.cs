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
            Source = null;
            return;
        }

        try
        {
            var isGif = string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);

            if (isGif && Animate)
            {
                // WPF's BitmapImage alone renders only the first GIF frame.
                // ImageBehavior drives the frames, so the setting really animates.
                ImageBehavior.SetAnimatedSource(this, new BitmapImage(new Uri(path, UriKind.Absolute)));
                return;
            }

            ImageBehavior.SetAnimatedSource(this, null);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();

            // A frozen bitmap only ever shows the first frame.
            image.Freeze();
            Source = image;
        }
        catch
        {
            Source = null;
        }
    }
}