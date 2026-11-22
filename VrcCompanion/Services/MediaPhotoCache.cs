using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcCompanion.Services;

/// <summary>
/// Remembers what was read out of each picture, keyed by path and validated by
/// size and write time. VRChat writes the metadata once, when the picture is
/// taken, so re-decoding thousands of PNGs on every visit to Media was wasted
/// work and the reason the tab could freeze. Only new or changed files are read.
/// </summary>
public static class MediaPhotoCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string DefaultPath => UserDataPaths.Combine("media-photo-cache.json");

    public static Dictionary<string, MediaPhotoCacheEntry> Load(string? cachePath = null)
    {
        var rows = new Dictionary<string, MediaPhotoCacheEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = cachePath ?? DefaultPath;
            if (!File.Exists(path))
            {
                return rows;
            }

            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), JsonOptions);
            foreach (var entry in file?.Entries ?? new List<MediaPhotoCacheEntry>())
            {
                if (!string.IsNullOrWhiteSpace(entry.Path))
                {
                    rows[entry.Path] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"media photo cache load: {ex.Message}");
        }

        return rows;
    }

    public static void Save(
        IReadOnlyDictionary<string, MediaPhotoCacheEntry> rows,
        string? cachePath = null)
    {
        try
        {
            var path = cachePath ?? DefaultPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var file = new CacheFile { Entries = rows.Values.ToList() };
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"media photo cache save: {ex.Message}");
        }
    }

    public static bool Matches(this MediaPhotoCacheEntry entry, string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists &&
                   info.Length == entry.Length &&
                   info.LastWriteTimeUtc.Ticks == entry.ModifiedTicks;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A picture that was read and had nothing in it.</summary>
    public static MediaPhotoCacheEntry FromAbsent(string path)
    {
        var entry = new MediaPhotoCacheEntry { Path = path, NoMetadata = true };
        try
        {
            var info = new FileInfo(path);
            entry.Length = info.Length;
            entry.ModifiedTicks = info.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            // Ignore, the entry simply will not match next time.
        }

        return entry;
    }

    public static MediaPhotoCacheEntry FromMetadata(string path, VrcPhotoMetadata metadata)
    {
        var entry = new MediaPhotoCacheEntry { Path = path };
        try
        {
            var info = new FileInfo(path);
            entry.Length = info.Length;
            entry.ModifiedTicks = info.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            // A vanished file just will not match on the next load.
        }

        entry.WorldId = metadata.WorldId;
        entry.WorldName = metadata.WorldName;
        entry.InstanceId = metadata.InstanceId;
        entry.AuthorId = metadata.AuthorId;
        entry.AuthorName = metadata.AuthorName;
        entry.TakenAtTicks = metadata.TakenAt?.UtcTicks;
        entry.Players = metadata.Players
            .Select(player => new MediaPhotoCachePlayer
            {
                UserId = player.UserId,
                DisplayName = player.DisplayName
            })
            .ToList();
        return entry;
    }

    public static VrcPhotoMetadata ToMetadata(this MediaPhotoCacheEntry entry)
        => new()
        {
            WorldId = entry.WorldId ?? string.Empty,
            WorldName = entry.WorldName ?? string.Empty,
            InstanceId = entry.InstanceId ?? string.Empty,
            AuthorId = entry.AuthorId ?? string.Empty,
            AuthorName = entry.AuthorName ?? string.Empty,
            TakenAt = entry.TakenAtTicks is { } ticks
                ? new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc))
                : null,
            Players = (entry.Players ?? new List<MediaPhotoCachePlayer>())
                .Select(player => new VrcPhotoPlayer
                {
                    UserId = player.UserId ?? string.Empty,
                    DisplayName = player.DisplayName ?? string.Empty
                })
                .ToList()
        };

    private sealed class CacheFile
    {
        public List<MediaPhotoCacheEntry> Entries { get; set; } = new();
    }
}

public sealed class MediaPhotoCacheEntry
{
    public string Path { get; set; } = string.Empty;

    /// <summary>The file was read and contained no metadata; do not read it again.</summary>
    public bool NoMetadata { get; set; }

    public long Length { get; set; }

    public long ModifiedTicks { get; set; }

    public string? WorldId { get; set; }

    public string? WorldName { get; set; }

    public string? InstanceId { get; set; }

    public string? AuthorId { get; set; }

    public string? AuthorName { get; set; }

    public long? TakenAtTicks { get; set; }

    public List<MediaPhotoCachePlayer>? Players { get; set; }
}

public sealed class MediaPhotoCachePlayer
{
    public string? UserId { get; set; }

    public string? DisplayName { get; set; }
}
