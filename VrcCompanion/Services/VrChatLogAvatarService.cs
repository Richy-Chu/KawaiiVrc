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

    /// <summary>
    /// Only the end of a log can name a current avatar, and VRChat logs grow to tens of megabytes.
    /// Reading a bounded tail of the newest files keeps the refresh milliseconds long; reading
    /// whole files with a regex per line took seconds, on the caller's thread, which is the UI one.
    /// </summary>
    internal const long TailBytes = 4 * 1024 * 1024;

    /// <summary>The newest logs; older ones cannot hold a switch newer than these.</summary>
    internal const int TailFiles = 2;

    private const string BehaviourMarker = "[Behaviour]";
    private const string UnpackingMarker = "Unpacking Avatar";

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _namesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _avatarsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _avatarsById = new(StringComparer.Ordinal);
    // An avatar's name is not unique, so an author is remembered for the player whose own
    // switch line paired with its unpack line, never for every avatar that shares the name.
    private readonly Dictionary<string, string> _authorsByPlayer = new(StringComparer.CurrentCultureIgnoreCase);
    private string _pendingUnpackName = string.Empty;
    private string _pendingUnpackAuthor = string.Empty;
    // A switch whose unpack line has not been read yet. It is what an unpack line directly
    // after the switch pairs with; the list is short because only the nearby lines matter.
    private readonly List<(string Player, string Avatar)> _unpairedSwitches = new();
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
            var playerName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
            if (!string.IsNullOrWhiteSpace(userId))
            {
                _avatarsById.TryGetValue(userId, out avatar);
                if (_namesById.TryGetValue(userId, out var knownName))
                {
                    playerName = knownName;
                }
            }

            if (string.IsNullOrWhiteSpace(avatar) && playerName is not null)
            {
                _avatarsByName.TryGetValue(playerName, out avatar);
            }

            if (string.IsNullOrWhiteSpace(avatar))
            {
                return null;
            }

            _authorsByPlayer.TryGetValue(playerName ?? string.Empty, out var author);
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
                .TakeLast(TailFiles)
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
            var startsMidFile = stream.Length > TailBytes;
            if (startsMidFile)
            {
                stream.Seek(-TailBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream);
            if (startsMidFile)
            {
                // The first line is a fragment of a line cut by the tail seek.
                reader.ReadLine();
            }

            while (reader.ReadLine() is { } line)
            {
                // Almost every line is neither a join, a switch nor an unpacking. The substring
                // check is what keeps three regexes off tens of thousands of other lines.
                if (line.Contains(BehaviourMarker, StringComparison.Ordinal))
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
                            _idsByName[name] = userId;
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
                    // The name-to-id map makes this a lookup; scanning every known id for each
                    // switch line made the tail cost grow with the number of lines in it.
                    if (_idsByName.TryGetValue(player, out var playerId) &&
                        !string.IsNullOrWhiteSpace(playerId))
                    {
                        _avatarsById[playerId] = avatar;
                    }

                    var unpackAuthor = ConsumePendingAuthor(avatar);
                    if (unpackAuthor.Length > 0)
                    {
                        _authorsByPlayer[player] = unpackAuthor;
                    }
                    else
                    {
                        // No unpack line before this switch; VRChat may write it just after.
                        _authorsByPlayer.Remove(player);
                        _unpairedSwitches.Add((player, avatar));
                        if (_unpairedSwitches.Count > 8)
                        {
                            _unpairedSwitches.RemoveAt(0);
                        }
                    }

                    continue;
                }

                if (!line.Contains(UnpackingMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                var unpacking = Unpacking.Match(line);
                if (!unpacking.Success)
                {
                    continue;
                }

                var avatarName = unpacking.Groups["name"].Value.Trim();
                var author = unpacking.Groups["author"].Value.Trim();
                if (avatarName.Length > 0 && author.Length > 0)
                {
                    if (!TryAttachAuthorToRecentSwitch(avatarName, author))
                    {
                        _pendingUnpackName = avatarName;
                        _pendingUnpackAuthor = author;
                    }
                }
            }
        }
        catch
        {
            // Ignore a log being rotated while it is read.
        }
    }

    /// <summary>
    /// Pairs an unpack line with the switch just before it, when that is the same avatar
    /// and the player still wears it. Otherwise the line waits for the switch that follows.
    /// </summary>
    private bool TryAttachAuthorToRecentSwitch(string avatarName, string author)
    {
        for (var index = _unpairedSwitches.Count - 1; index >= 0; index--)
        {
            var (player, avatar) = _unpairedSwitches[index];
            if (!string.Equals(avatar, avatarName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // An older switch by the same player is not the one this load belongs to.
            if (!_avatarsByName.TryGetValue(player, out var latest) ||
                !string.Equals(latest, avatarName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _authorsByPlayer[player] = author;
            _unpairedSwitches.RemoveAt(index);
            return true;
        }

        return false;
    }

    private string ConsumePendingAuthor(string avatarName)
    {
        if (_pendingUnpackName.Length == 0)
        {
            return string.Empty;
        }

        var author = string.Equals(
            _pendingUnpackName,
            avatarName,
            StringComparison.OrdinalIgnoreCase)
            ? _pendingUnpackAuthor
            : string.Empty;
        _pendingUnpackName = string.Empty;
        _pendingUnpackAuthor = string.Empty;
        return author;
    }

    private static string LogDirectory() => VrcLogPaths.Directory;

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
