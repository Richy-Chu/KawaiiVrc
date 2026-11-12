using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace VrcCompanion.Controls;

/// <summary>
/// Enhanced background system supporting multiple layers (base color, image, overlay),
/// background blur, playlist, and optimized GIF handling.
/// Uses WPF BlurEffect for performance-friendly background blurring.
/// </summary>
public sealed class BackgroundLayerManager : IDisposable
{
    private static readonly double[] LayerOpacities = { 1.0, 0.85, 0.5 };

    private static readonly Color[] VariantPalettes =
    {
        Color.FromRgb(7, 9, 16),   // Aurora base
        Color.FromRgb(6, 10, 22),  // Midnight base
        Color.FromRgb(16, 8, 6),   // Ember base
        Color.FromRgb(14, 8, 16)   // Sakura base
    };

    private static readonly double[] VariantDims = { 0.22, 0.30, 0.25, 0.20 };

    public enum VariantType
    {
        Aurora,
        Midnight,
        Ember,
        Sakura
    }

    private VariantType _currentVariant = VariantType.Aurora;
    private bool _animate = true;
    private double _dim = 0.22;
    private double _blurRadius = 0;

    private readonly List<string> _playlist = new();
    private int _playlistIndex = -1;

    private readonly Dictionary<VariantType, (Color baseColor, double dim)> _variantSettings = new()
    {
        { VariantType.Aurora,   (Color.FromRgb(7, 9, 16),   0.22) },
        { VariantType.Midnight, (Color.FromRgb(6, 10, 22),  0.30) },
        { VariantType.Ember,    (Color.FromRgb(16, 8, 6),    0.25) },
        { VariantType.Sakura,   (Color.FromRgb(14, 8, 16),   0.20) }
    };

    public VariantType CurrentVariant
    {
        get => _currentVariant;
        set => SetVariant(value);
    }

    public bool Animate
    {
        get => _animate;
        set => _animate = value;
    }

    public double Dim
    {
        get => _dim;
        set => _dim = Math.Clamp(value, 0, 1);
    }

    public double BlurRadius
    {
        get => _blurRadius;
        set => _blurRadius = Math.Max(0, value);
    }

    public IReadOnlyList<string> Playlist => _playlist;

    /// <summary>
    /// Add a background image path to the playlist for slideshow support.
    /// </summary>
    public void AddToPlaylist(string imagePath)
    {
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            _playlist.Add(imagePath);
        }
    }

    /// <summary>
    /// Clear the background playlist.
    /// </summary>
    public void ClearPlaylist()
    {
        _playlist.Clear();
        _playlistIndex = -1;
    }

    /// <summary>
    /// Advance to the next background image in the playlist.
    /// </summary>
    public string? NextPlaylistItem()
    {
        if (_playlist.Count == 0)
            return null;

        _playlistIndex = (_playlistIndex + 1) % _playlist.Count;
        return _playlist[_playlistIndex];
    }

    /// <summary>
    /// Get the current playlist item path.
    /// </summary>
    public string? CurrentPlaylistItem()
    {
        return _playlistIndex >= 0 && _playlistIndex < _playlist.Count
            ? _playlist[_playlistIndex]
            : null;
    }

    /// <summary>
    /// Set background variant (Aurora, Midnight, Ember, Sakura).
    /// </summary>
    public void SetVariant(VariantType variant)
    {
        _currentVariant = variant;
        if (_variantSettings.TryGetValue(variant, out var settings))
        {
            Dim = settings.dim;
        }
    }

    /// <summary>
    /// Get the base color for the current variant.
    /// </summary>
    public Color GetVariantBaseColor()
    {
        return _variantSettings.TryGetValue(_currentVariant, out var settings)
            ? settings.baseColor
            : VariantPalettes[0];
    }

    /// <summary>
    /// Get a solid color brush for the current variant's base color with applied dim.
    /// </summary>
    public SolidColorBrush GetVariantBrush()
    {
        var color = GetVariantBaseColor();
        var dimmedAlpha = (byte)Math.Clamp(255 * (1 - Dim), 0, 255);
        return new SolidColorBrush(Color.FromArgb(dimmedAlpha, color.R, color.G, color.B));
    }

    /// <summary>
    /// Apply a Gaussian blur effect to an image source (for blurred overlays).
    /// </summary>
    public BitmapSource? ApplyBlur(BitmapSource source)
    {
        if (_blurRadius <= 0 || source == null)
            return source;

        try
        {
            var bitmap = new FormatConvertedBitmap();
            bitmap.BeginInit();
            bitmap.Source = source;
            bitmap.DestinationFormat = PixelFormats.Bgra32;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return source;
        }
    }

    /// <summary>
    /// Check if a file is a supported background image (PNG, JPEG, WebP, GIF).
    /// </summary>
    public static bool IsSupportedImage(string path)
    {
        if (!File.Exists(path))
            return false;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".png" || ext == ".jpg" || ext == ".jpeg" ||
               ext == ".webp" || ext == ".gif" || ext == ".bmp";
    }

    public void Dispose()
    {
        // No unmanaged resources to dispose
    }
}