using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;

namespace VrcCompanion.Services;

/// <summary>A world VRCX-0 remembered for one picture.</summary>
public sealed record CachedPhotoWorld(string WorldId, string WorldName);

/// <summary>
/// Reads VRCX-0's screenshot cache, which stores the world id and name next to
/// every picture it has scanned. VRChat only started writing the world into the
/// PNG itself recently, so for older pictures this is the only local source of
/// the world — without it they showed up as "VRChat" with no world at all.
/// The database is opened read-only and never modified.
/// </summary>
public static class Vrcx0PhotoCache
{
    private static readonly object Gate = new();
    private static Dictionary<string, CachedPhotoWorld>? _pictures;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCX-0",
        "metadataCache.db");

    public static CachedPhotoWorld? Lookup(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        Dictionary<string, CachedPhotoWorld> pictures;
        lock (Gate)
        {
            pictures = _pictures ??= Load();
        }

        return pictures.TryGetValue(path, out var world) ? world : null;
    }

    private static Dictionary<string, CachedPhotoWorld> Load()
    {
        var rows = new Dictionary<string, CachedPhotoWorld>(StringComparer.OrdinalIgnoreCase);
        var databasePath = DefaultPath;
        if (!File.Exists(databasePath))
        {
            return rows;
        }

        try
        {
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Shared,
                    Pooling = true
                }.ToString());
            connection.Open();
            using (connection)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT path, world_id, world_name
                    FROM screenshot_files
                    WHERE world_id <> ''
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var path = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var worldId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var worldName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    if (path.Length > 0 && worldId.Length > 0)
                    {
                        rows[path] = new CachedPhotoWorld(worldId, worldName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // A missing or locked VRCX-0 database is not an error for Media.
            Debug.WriteLine($"VRCX-0 photo cache: {ex.Message}");
        }

        return rows;
    }
}
