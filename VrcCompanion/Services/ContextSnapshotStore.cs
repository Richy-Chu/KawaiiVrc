using System;
using System.IO;
using System.Text.Json;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Writes a small, read-only JSON snapshot for local context consumers. The
/// snapshot is intentionally separate from the history database: it only holds
/// what is currently visible, and it is replaced atomically after each refresh.
/// </summary>
public sealed class ContextSnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ContextSnapshotStore()
        : this(DefaultPath)
    {
    }

    internal ContextSnapshotStore(string path)
    {
        FilePath = path;
    }

    public string FilePath { get; }

    public static string DefaultPath => UserDataPaths.Combine("context-snapshot.json");

    public void Save(ContextSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot, Options));
    }

    public ContextSnapshot? Read()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ContextSnapshot>(
                File.ReadAllText(FilePath),
                Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public DateTimeOffset? LastWriteTime
    {
        get
        {
            try
            {
                return File.Exists(FilePath)
                    ? new DateTimeOffset(File.GetLastWriteTimeUtc(FilePath), TimeSpan.Zero)
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
