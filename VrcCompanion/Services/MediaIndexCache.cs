using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcCompanion.Services;

/// <summary>
/// A small on-disk index of the last Media scan so the tab can show the library
/// immediately (one fast JSON read) and refresh from disk afterwards. Slow
/// OneDrive folders no longer mean a blank tab on every visit.
/// </summary>
public static class MediaIndexCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcCompanion",
        "media-index.json");

    public static IReadOnlyList<MediaGroup> Load(string root, string? cachePath = null)
    {
        try
        {
            var path = cachePath ?? DefaultPath;
            if (!File.Exists(path))
            {
                return Array.Empty<MediaGroup>();
            }

            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), JsonOptions);
            if (cache?.Entries is null ||
                !string.Equals(cache.Root, root, StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<MediaGroup>();
            }

            var items = cache.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                .Select(entry => new MediaItem(entry.Path!, entry.World ?? "VRChat", entry.TakenAt));
            return MediaLibraryScanner.BuildGroups(items);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"media cache load {cachePath ?? DefaultPath}: {ex.Message}");
            return Array.Empty<MediaGroup>();
        }
    }

    public static void Save(string root, IEnumerable<MediaGroup> groups, string? cachePath = null)
    {
        try
        {
            var path = cachePath ?? DefaultPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var file = new CacheFile
            {
                Root = root,
                Entries = groups
                    .SelectMany(group => group.Items)
                    .Select(item => new CacheEntry
                    {
                        Path = item.Path,
                        World = item.WorldName,
                        TakenAt = item.TakenAt
                    })
                    .ToList()
            };

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // The cache is an optimisation; failing to write it must not break Media.
            Debug.WriteLine($"media cache save {cachePath ?? DefaultPath}: {ex.Message}");
        }
    }

    private sealed class CacheFile
    {
        [JsonPropertyName("root")]
        public string? Root { get; set; }

        [JsonPropertyName("entries")]
        public List<CacheEntry>? Entries { get; set; }
    }

    private sealed class CacheEntry
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("world")]
        public string? World { get; set; }

        [JsonPropertyName("takenAt")]
        public DateTime TakenAt { get; set; }
    }
}
