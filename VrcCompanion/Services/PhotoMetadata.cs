using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VrcCompanion.Services;

/// <summary>Metadata VRChat or VRCX wrote into a screenshot.</summary>
public sealed class VrcPhotoMetadata
{
    public string WorldId { get; init; } = string.Empty;

    public string WorldName { get; init; } = string.Empty;

    public string AuthorId { get; init; } = string.Empty;

    public string AuthorName { get; init; } = string.Empty;

    /// <summary>Full location (`wrld_…:12345~group(…)…`) when VRCX wrote the photo.</summary>
    public string InstanceId { get; init; } = string.Empty;

    public DateTimeOffset? TakenAt { get; init; }

    public IReadOnlyList<VrcPhotoPlayer> Players { get; init; } = Array.Empty<VrcPhotoPlayer>();

    public bool HasWorld => !string.IsNullOrWhiteSpace(WorldId);

    public bool HasPlayers => Players.Count > 0;
}

public sealed class VrcPhotoPlayer
{
    public string UserId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>
/// Reads the metadata VRChat (since 2025.3) and VRCX write into screenshots.
/// VRChat stores Adobe XMP in an iTXt PNG chunk; VRCX stores a JSON blob in the
/// same kind of chunk, and that one also lists the players who were there.
/// </summary>
public static class PhotoMetadataReader
{
    private const int MaximumBytes = 256 * 1024;

    private static readonly Regex WorldIdTag = Tag("vrc:WorldID");
    private static readonly Regex WorldNameTag = Tag("vrc:WorldDisplayName");
    private static readonly Regex AuthorIdTag = Tag("vrc:AuthorID");
    private static readonly Regex AuthorTag = Tag("xmp:Author");
    private static readonly Regex CreateDateTag = Tag("xmp:CreateDate");

    /// <summary>Returns null for pictures without metadata (older or stripped files).</summary>
    public static VrcPhotoMetadata? Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bytes = ReadHead(stream);
            if (bytes.Length == 0)
            {
                return null;
            }

            foreach (var text in ReadTextChunks(bytes))
            {
                var fromJson = TryReadJson(text);
                if (fromJson is not null)
                {
                    return fromJson;
                }

                var fromXmp = TryReadXmp(text);
                if (fromXmp is not null)
                {
                    return fromXmp;
                }
            }

            // JPEG (and anything else) keeps the XMP packet as plain text; the
            // PNG chunk loop above does not apply, so look for the packet.
            return TryReadXmp(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] ReadHead(Stream stream)
    {
        var buffer = new byte[MaximumBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
            // The metadata chunks always follow the header, so stop early once
            // the pixel data starts instead of reading whole 4K pictures.
            if (total > 1024 && IsEndOfMetadata(buffer.AsSpan(0, total)))
            {
                break;
            }
        }

        return total == buffer.Length ? buffer : buffer[..total];
    }

    private static bool IsEndOfMetadata(ReadOnlySpan<byte> data)
    {
        // PNG: stop at the first IDAT, which always comes after iTXt/tEXt.
        return data.Length > 64 && data.LastIndexOf("IDAT"u8) > 0;
    }

    /// <summary>Every tEXt/iTXt/zTXt payload in a PNG, decoded to text.</summary>
    private static IEnumerable<string> ReadTextChunks(byte[] bytes)
    {
        if (bytes.Length < 8 ||
            bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
        {
            yield break;
        }

        var offset = 8;
        while (offset + 8 <= bytes.Length)
        {
            var length = (bytes[offset] << 24) | (bytes[offset + 1] << 16) |
                         (bytes[offset + 2] << 8) | bytes[offset + 3];
            if (length < 0 || offset + 12 + length > bytes.Length)
            {
                yield break;
            }

            var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var data = new ReadOnlySpan<byte>(bytes, offset + 8, length);
            if (type is "tEXt" or "iTXt" or "zTXt")
            {
                var text = DecodeTextChunk(type, data);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return text;
                }
            }

            offset += 12 + length;
            if (type == "IEND")
            {
                yield break;
            }
        }
    }

    private static string? DecodeTextChunk(string type, ReadOnlySpan<byte> data)
    {
        // keyword\0[compression flag\0 method\0 language\0 translated\0]text
        var keywordEnd = data.IndexOf((byte)0);
        if (keywordEnd < 0)
        {
            return null;
        }

        if (type == "tEXt")
        {
            return Encoding.UTF8.GetString(data[(keywordEnd + 1)..]);
        }

        var rest = data[(keywordEnd + 1)..];
        if (type == "zTXt")
        {
            if (rest.Length < 2)
            {
                return null;
            }

            return Inflate(rest[1..]);
        }

        // iTXt: compression flag, compression method, language tag, translated
        // keyword (all NUL-terminated), then the text.
        if (rest.Length < 2)
        {
            return null;
        }

        var compressed = rest[0] == 1;
        var cursor = 2;
        for (var fields = 0; fields < 2; fields++)
        {
            var end = rest[cursor..].IndexOf((byte)0);
            if (end < 0)
            {
                return null;
            }

            cursor += end + 1;
        }

        if (cursor >= rest.Length)
        {
            return null;
        }

        return compressed ? Inflate(rest[cursor..]) : Encoding.UTF8.GetString(rest[cursor..]);
    }

    private static string? Inflate(ReadOnlySpan<byte> data)
    {
        try
        {
            using var input = new MemoryStream(data.ToArray());
            using var deflate = new ZLibStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(deflate, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static VrcPhotoMetadata? TryReadJson(string text)
    {
        if (!text.Contains("\"application\"", StringComparison.Ordinal) ||
            !text.Contains("VRCX", StringComparison.Ordinal))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            var root = document.RootElement;
            var world = root.TryGetProperty("world", out var worldElement) ? worldElement : default;
            var author = root.TryGetProperty("author", out var authorElement) ? authorElement : default;

            var players = new List<VrcPhotoPlayer>();
            if (root.TryGetProperty("players", out var playerList) &&
                playerList.ValueKind == JsonValueKind.Array)
            {
                foreach (var player in playerList.EnumerateArray())
                {
                    var userId = player.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty;
                    var name = player.TryGetProperty("displayName", out var displayName)
                        ? displayName.GetString() ?? string.Empty
                        : string.Empty;
                    if (!string.IsNullOrWhiteSpace(userId) || !string.IsNullOrWhiteSpace(name))
                    {
                        players.Add(new VrcPhotoPlayer { UserId = userId, DisplayName = name });
                    }
                }
            }

            return new VrcPhotoMetadata
            {
                WorldId = StringOf(world, "id"),
                WorldName = StringOf(world, "name"),
                InstanceId = StringOf(world, "instanceId"),
                AuthorId = StringOf(author, "id"),
                AuthorName = StringOf(author, "displayName"),
                Players = players
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StringOf(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static VrcPhotoMetadata? TryReadXmp(string text)
    {
        if (!text.Contains("<x:xmpmeta", StringComparison.Ordinal) &&
            !text.Contains("<vrc:WorldID", StringComparison.Ordinal))
        {
            return null;
        }

        var worldId = ValueOf(WorldIdTag, text);
        var worldName = ValueOf(WorldNameTag, text);
        var authorId = ValueOf(AuthorIdTag, text);
        var authorName = ValueOf(AuthorTag, text);
        var createdAt = ValueOf(CreateDateTag, text);

        if (string.IsNullOrWhiteSpace(worldId) &&
            string.IsNullOrWhiteSpace(createdAt) &&
            string.IsNullOrWhiteSpace(authorName))
        {
            return null;
        }

        return new VrcPhotoMetadata
        {
            WorldId = worldId,
            WorldName = worldName,
            AuthorId = authorId,
            AuthorName = authorName,
            TakenAt = DateTimeOffset.TryParse(createdAt, out var taken) ? taken : null
        };
    }

    private static Regex Tag(string name)
        => new(
            $"<{Regex.Escape(name)}(?:\\s[^>]*)?>(?<value>[^<]*)</{Regex.Escape(name)}>",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static string ValueOf(Regex tag, string text)
    {
        var match = tag.Match(text);
        return match.Success ? DecodeEntities(match.Groups["value"].Value.Trim()) : string.Empty;
    }

    private static string DecodeEntities(string value)
        => value
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal);
}
