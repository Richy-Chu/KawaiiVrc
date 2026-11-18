using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VrcCompanion.Services;

/// <summary>
/// VRChat's API no longer exposes another player's current avatar on every
/// profile. VRChat's own output log still records the switch:
/// <c>[Behaviour] Switching REFOXE to avatar AC-Moe-Green</c>. VRCNext uses
/// this local source; keeping it here lets a profile resolve the avatar name
/// without relying on a public index having seen the wearer.
/// </summary>
public sealed record LoggedAvatar(string Name, string? AuthorName);

public sealed class VrChatLogAvatarService
{
    public static VrChatLogAvatarService Shared { get; } = new();

    private static readonly Regex Joined = new(
        @"\[Behaviour\] OnPlayerJoined (?<name>.+?)(?: \((?<id>usr_[0-9a-fA-F-]{36})\))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Switching = new(
        @"\[Behaviour\] Switching (?<name>.+?) to avatar (?<avatar>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Unpacking = new(
        @"\[AssetBundleDownloadManager\] \[\d+\] Unpacking Avatar \((?<name>.+) by (?<author>.+?)\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _namesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _avatarsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _avatarsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _authorsByAvatarName = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRefresh = DateTime.MinValue;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    // Tests use deterministic rows instead of reading the player's local logs.
    private static readonly Dictionary<string, LoggedAvatar> TestAvatarsById = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LoggedAvatar> TestAvatarsByName =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the last avatar VRChat logged for this player.</summary>
    public LoggedAvatar? GetAvatar(string? userId, string? displayName)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(userId) &&
                TestAvatarsById.TryGetValue(userId, out var testById))
            {
                return testById;
            }

            if (!string.IsNullOrWhiteSpace(displayName) &&
                TestAvatarsByName.TryGetValue(displayName, out var testByName))
            {
                return testByName;
            }

            RefreshIfNeeded();
            string? avatar = null;
            if (!string.IsNullOrWhiteSpace(userId))
            {
                _avatarsById.TryGetValue(userId, out avatar);
            }

            if (string.IsNullOrWhiteSpace(avatar) && !string.IsNullOrWhiteSpace(displayName))
            {
                _avatarsByName.TryGetValue(displayName, out avatar);
            }

            if (string.IsNullOrWhiteSpace(avatar))
            {
                return null;
            }

            _authorsByAvatarName.TryGetValue(avatar, out var author);
            return new LoggedAvatar(avatar, author);
        }
    }

    private void RefreshIfNeeded()
    {
        if (DateTime.UtcNow - _lastRefresh < RefreshInterval)
        {
            return;
        }

        _lastRefresh = DateTime.UtcNow;
        try
        {
            var directory = LogDirectory();
            if (!Directory.Exists(directory))
            {
                return;
            }

            // Oldest to newest so the latest switch wins. Only the tail of the
            // newest logs matters for a current avatar; full logs can be huge.
            var files = Directory.GetFiles(directory, "output_log_*.txt")
                .OrderBy(File.GetLastWriteTimeUtc)
                .TakeLast(3)
                .ToArray();
            foreach (var file in files)
            {
                ParseFile(file);
            }
        }
        catch
        {
            // Logs are best-effort; a locked or missing file is not fatal.
        }
    }

    private void ParseFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            const long tailBytes = 32 * 1024 * 1024;
            var startsMidFile = stream.Length > tailBytes;
            if (startsMidFile)
            {
                stream.Seek(-tailBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream);
            if (startsMidFile)
            {
                // The first line is a fragment of a line cut by the tail seek.
                reader.ReadLine();
            }

            while (reader.ReadLine() is { } line)
            {
                var joined = Joined.Match(line);
                if (joined.Success)
                {
                    var name = joined.Groups["name"].Value.Trim();
                    var userId = joined.Groups["id"].Value.Trim();
                    if (name.Length > 0)
                    {
                        _avatarsByName.TryAdd(name, string.Empty);
                    }

                    if (name.Length > 0 && userId.Length > 0)
                    {
                        _namesById[userId] = name;
                    }

                    continue;
                }

                var unpacking = Unpacking.Match(line);
                if (unpacking.Success)
                {
                    var avatarName = unpacking.Groups["name"].Value.Trim();
                    var author = unpacking.Groups["author"].Value.Trim();
                    if (avatarName.Length > 0 && author.Length > 0)
                    {
                        _authorsByAvatarName[avatarName] = author;
                    }

                    continue;
                }

                var switching = Switching.Match(line);
                if (!switching.Success)
                {
                    continue;
                }

                var player = switching.Groups["name"].Value.Trim();
                var avatar = switching.Groups["avatar"].Value.Trim();
                if (player.Length == 0 || avatar.Length == 0)
                {
                    continue;
                }

                _avatarsByName[player] = avatar;
                var playerId = _namesById
                    .FirstOrDefault(pair => string.Equals(pair.Value, player, StringComparison.Ordinal))
                    .Key;
                if (!string.IsNullOrWhiteSpace(playerId))
                {
                    _avatarsById[playerId] = avatar;
                }
            }
        }
        catch
        {
            // Ignore a log being rotated while it is read.
        }
    }

    private static string LogDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.GetFullPath(Path.Combine(local, "..", "LocalLow", "VRChat", "VRChat"));
    }

    internal static void SetForTests(
        string userId,
        string displayName,
        string avatarName,
        string? authorName = null)
    {
        var avatar = new LoggedAvatar(avatarName, authorName);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            TestAvatarsById[userId] = avatar;
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            TestAvatarsByName[displayName] = avatar;
        }
    }

    internal static void ClearForTests()
    {
        TestAvatarsById.Clear();
        TestAvatarsByName.Clear();
    }
}
