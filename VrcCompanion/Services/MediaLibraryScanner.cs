using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace VrcCompanion.Services;

/// <summary>One picture found in the VRChat pictures folder.</summary>
public sealed class MediaItem
{
    private BitmapImage? _thumbnail;

    public MediaItem(string path, string worldName, DateTime takenAt)
    {
        Path = path;
        WorldName = worldName;
        TakenAt = takenAt;
    }

    public string Path { get; }

    /// <summary>Folder under the pictures root, or "VRChat" for the flat layout.</summary>
    public string WorldName { get; }

    public DateTime TakenAt { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Decoded once, small enough for a grid; read lazily by the view.</summary>
    public BitmapImage? Thumbnail => _thumbnail ??= LoadThumbnail();

    private BitmapImage? LoadThumbnail()
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 320;
            image.UriSource = new Uri(Path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"media thumbnail {Path}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>Pictures from one world in one month.</summary>
public sealed record MediaGroup(
    string Title,
    string WorldName,
    DateTime Month,
    IReadOnlyList<MediaItem> Items);

/// <summary>
/// Reads the pictures VRChat writes to disk. Grouping uses the folder layout
/// (a subfolder per world/collection) and the date in the file name; VRChat
/// itself does not record the world in the image, so a flat folder groups as
/// "VRChat" with dates only.
/// </summary>
public static class MediaLibraryScanner
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp"
    };

    private static readonly Regex NameDate = new(
        @"VRChat_(?<date>\d{4}-\d{2}-\d{2})_(?<time>\d{2}-\d{2}-\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "VRChat");

    /// <summary>Groups every picture under <paramref name="root"/>, newest first.</summary>
    public static IReadOnlyList<MediaGroup> Scan(string? root = null)
    {
        root = string.IsNullOrWhiteSpace(root) ? DefaultRoot : root;
        if (!Directory.Exists(root))
        {
            return Array.Empty<MediaGroup>();
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };

        var items = new List<MediaItem>();
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            if (!ImageExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            items.Add(new MediaItem(file, WorldOf(root, file), TakenAtOf(file)));
        }

        return items
            .GroupBy(item => (item.WorldName, item.TakenAt.Year, item.TakenAt.Month))
            .Select(group => new MediaGroup(
                $"{group.Key.WorldName} · {new DateTime(group.Key.Year, group.Key.Month, 1):MMMM yyyy}",
                group.Key.WorldName,
                new DateTime(group.Key.Year, group.Key.Month, 1),
                group.OrderByDescending(item => item.TakenAt).ToList()))
            .OrderByDescending(group => group.Month)
            .ThenBy(group => group.WorldName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>First folder under the root, so pictures/VRChat/Japan/x.png is "Japan".</summary>
    private static string WorldOf(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        var separator = relative.IndexOf(Path.DirectorySeparatorChar);
        return separator > 0 ? relative[..separator] : "VRChat";
    }

    /// <summary>Uses VRChat's file-name timestamp, falling back to last write time.</summary>
    internal static DateTime TakenAtOf(string file)
    {
        var match = NameDate.Match(Path.GetFileName(file));
        if (match.Success &&
            DateTime.TryParseExact(
                $"{match.Groups["date"].Value}_{match.Groups["time"].Value}",
                "yyyy-MM-dd_HH-mm-ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var taken))
        {
            return taken;
        }

        try
        {
            return File.GetLastWriteTime(file);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }
}
