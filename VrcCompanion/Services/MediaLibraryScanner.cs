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
    private VrcPhotoMetadata? _embeddedMetadata;
    private bool _metadataRead;
    private readonly object _metadataLock = new();

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

    /// <summary>
    /// The world, session and players recorded when this picture was taken.
    /// Null when the picture predates the local instance history.
    /// </summary>
    public MediaContext? Context { get; internal set; }

    /// <summary>The real world name from history, falling back to the folder name.</summary>
    public string DisplayWorldName => Context is { HasWorldName: true } context
        ? context.WorldName
        : WorldName;

    public bool HasContext => Context is not null;

    public string FileName => System.IO.Path.GetFileName(Path);

    private static readonly Regex VrChatName = new(
        @"VRChat_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// True for VRChat's own screenshots (named VRChat_… or inside a VRChat
    /// folder). Everything else the library finds — saved stickers, other
    /// games' screenshots — is a plain picture and stays one: it is never
    /// matched to a VRChat session or given a world.
    /// </summary>
    public bool IsVrChatPicture =>
        VrChatName.IsMatch(FileName) ||
        Path.Contains("\\VRChat\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The world/players VRChat or VRCX wrote into the file itself, read on
    /// demand. Null for screenshots without metadata.
    /// </summary>
    public VrcPhotoMetadata? EmbeddedMetadata
    {
        get
        {
            // The background metadata pass and the viewer can ask at the same
            // time; without the lock one of them would see an empty answer.
            lock (_metadataLock)
            {
                if (!_metadataRead)
                {
                    _metadataRead = true;
                    _embeddedMetadata = PhotoMetadataReader.Read(Path);
                }

                return _embeddedMetadata;
            }
        }
    }

    /// <summary>
    /// Supplies metadata already read from this file on a previous visit, so a
    /// warm Media tab never decodes the PNG again.
    /// </summary>
    internal void SeedMetadata(VrcPhotoMetadata metadata)
    {
        lock (_metadataLock)
        {
            _embeddedMetadata = metadata;
            _metadataRead = true;
        }
    }

    /// <summary>
    /// The file was read on an earlier visit and had no VRChat/VRCX metadata.
    /// Mark it read so a warm Media tab does not decode it again.
    /// </summary>
    internal void MarkMetadataAbsent()
    {
        lock (_metadataLock)
        {
            _embeddedMetadata = null;
            _metadataRead = true;
        }
    }

    /// <summary>
    /// Decodes this file at the requested width (frozen, safe across threads).
    /// The grid never calls it: a card decodes through <c>AsyncImage</c> at 160 px,
    /// so a per-item thumbnail cache here only held a second decode of files the
    /// library keeps for the whole session.
    /// </summary>
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
        .GroupBy(item => (item.DisplayWorldName, item.TakenAt.Year, item.TakenAt.Month))
        .Select(group => new MediaGroup(
            $"{group.Key.DisplayWorldName} · {new DateTime(group.Key.Year, group.Key.Month, 1):MMMM yyyy}",
            group.Key.DisplayWorldName,
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
