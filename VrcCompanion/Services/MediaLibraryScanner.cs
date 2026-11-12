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

    /// <summary>Folder under the pictures root, or "VRChat" for the default layout.</summary>
    public string WorldName { get; }

    public DateTime TakenAt { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Small decode for the grid; loaded lazily and frozen for reuse.</summary>
    public BitmapImage? Thumbnail => _thumbnail ??= LoadBitmap(320);

    /// <summary>Decodes this file at the requested width (frozen, safe across threads).</summary>
    public BitmapImage? LoadBitmap(int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(Path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"media bitmap {Path}: {ex.Message}");
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
/// itself does not record the world in the image, so its own date folders
/// group as "VRChat" with dates only.
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

    // VRChat writes date folders (2023-08); those are not world names.
    private static readonly Regex DateFolder = new(
        @"^\d{4}([-_.]\d{1,2}){0,2}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "VRChat");

    /// <summary>
    /// Groups every picture under <paramref name="root"/>, newest first, and
    /// reports how many have been found so a slow network folder can show
    /// progress instead of looking stuck.
    /// </summary>
    public static IReadOnlyList<MediaGroup> Scan(string? root = null, IProgress<int>? progress = null)
    {
        root = string.IsNullOrWhiteSpace(root) ? DefaultRoot : root;
        if (!Directory.Exists(root))
        {
            progress?.Report(0);
            return Array.Empty<MediaGroup>();
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };

        var items = new List<MediaItem>();
        var reported = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            if (!ImageExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            items.Add(new MediaItem(file, WorldOf(root, file), TakenAtOf(file)));

            // Throttled so a 100k folder does not flood the UI thread.
            if (progress is not null && items.Count - reported >= 25)
            {
                reported = items.Count;
                progress.Report(reported);
            }
        }

        progress?.Report(items.Count);
        return BuildGroups(items);
    }

    /// <summary>Builds the world/month groups from a flat list (also used for the cache).</summary>
    public static IReadOnlyList<MediaGroup> BuildGroups(IEnumerable<MediaItem> items) => items
        .GroupBy(item => (item.WorldName, item.TakenAt.Year, item.TakenAt.Month))
        .Select(group => new MediaGroup(
            $"{group.Key.WorldName} · {new DateTime(group.Key.Year, group.Key.Month, 1):MMMM yyyy}",
            group.Key.WorldName,
            new DateTime(group.Key.Year, group.Key.Month, 1),
            group.OrderByDescending(item => item.TakenAt).ToList()))
        .OrderByDescending(group => group.Month)
        .ThenBy(group => group.WorldName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// First folder under the root, so pictures/VRChat/Japan/x.png is "Japan".
    /// Date folders (VRChat's own layout) count as the default library instead.
    /// </summary>
    private static string WorldOf(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        var separator = relative.IndexOf(Path.DirectorySeparatorChar);
        if (separator <= 0)
        {
            return "VRChat";
        }

        var folder = relative[..separator];
        return DateFolder.IsMatch(folder) ? "VRChat" : folder;
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
