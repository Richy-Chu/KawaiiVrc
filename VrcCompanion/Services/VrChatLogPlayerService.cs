using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VrcCompanion.Services;

public sealed record VrChatLogPlayer(string UserId, string DisplayName);

public sealed record VrChatLogSnapshot(
    string Location,
    IReadOnlyList<VrChatLogPlayer> Players,
    IReadOnlySet<string>? BlockedPlayerNames = null,
    IReadOnlySet<string>? UnblockedPlayerNames = null,
    IReadOnlyDictionary<string, LoggedAvatar>? Avatars = null,
    IReadOnlyDictionary<string, LoggedAvatar>? AvatarSwitches = null);

/// <summary>
/// Reads VRChat's own output log to track the current instance and the players
/// who joined or left it. VRChat only returns a complete instance user list for
/// instances created by the requesting account, so this is the same local
/// source VRCX and VRCNext use to fill in everyone else. The log is read-only.
/// </summary>
public sealed class VrChatLogPlayerService
{
    private static readonly Regex Joining = new(
        @"\[Behaviour\] Joining (?<location>wrld_[^\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Joined = new(
        @"\[Behaviour\] OnPlayerJoined (?<name>.+?)(?: \((?<id>usr_[0-9a-fA-F-]{36})\))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Left = new(
        @"\[Behaviour\] OnPlayerLeft (?<name>.+?)(?: \((?<id>usr_[0-9a-fA-F-]{36})\))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LeftRoom = new(
        @"\[Behaviour\] OnLeftRoom\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Switching = new(
        @"\[Behaviour\] Switching (?<name>.+?) to avatar (?<avatar>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Unpacking = new(
        @"\[AssetBundleDownloadManager\] \[\d+\] Unpacking Avatar \((?<name>.+) by (?<author>.+?)\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Blocked = new(
        @"\[ModerationManager\]\s+(?<name>.+?)\s+is now Blocked\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex Unblocked = new(
        @"\[ModerationManager\]\s+(?<name>.+?)\s+(?:is now Unblocked|is no longer Blocked)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly Func<string?> _findLatestLog;
    private readonly Func<bool> _isGameRunning;
    private readonly Dictionary<string, VrChatLogPlayer> _players = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _avatarNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _avatarAuthors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blockedNames = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly HashSet<string> _unblockedNames = new(StringComparer.CurrentCultureIgnoreCase);
    private string _logPath = string.Empty;
    private long _position;
    private string _location = string.Empty;
    private DateTime _lastLogScan = DateTime.MinValue;
    private DateTime _lastGameCheck = DateTime.MinValue;
    private bool _lastGameRunning;
    private static readonly TimeSpan LogScanInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GameCheckInterval = TimeSpan.FromSeconds(5);

    public VrChatLogPlayerService()
        : this(FindLatestLog, IsVrChatRunning)
    {
    }

    internal VrChatLogPlayerService(Func<string?> findLatestLog, Func<bool>? isGameRunning = null)
    {
        _findLatestLog = findLatestLog;
        _isGameRunning = isGameRunning ?? IsVrChatRunning;
    }

    public bool IsGameRunning
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now - _lastGameCheck < GameCheckInterval)
            {
                return _lastGameRunning;
            }

            _lastGameCheck = now;
            try
            {
                _lastGameRunning = _isGameRunning();
            }
            catch
            {
                _lastGameRunning = false;
            }

            return _lastGameRunning;
        }
    }

    /// <summary>
    /// Reads any new log lines and returns the current location plus the players
    /// seen in it. Returns null until a valid world instance has been joined.
    /// </summary>
    public VrChatLogSnapshot? Poll()
    {
        try
        {
            var path = _logPath;
            if (DateTime.UtcNow - _lastLogScan >= LogScanInterval)
            {
                _lastLogScan = DateTime.UtcNow;
                path = _findLatestLog() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Current();
            }

            if (!string.Equals(path, _logPath, StringComparison.OrdinalIgnoreCase))
            {
                _logPath = path;
                _position = 0;
                _players.Clear();
                _avatarNames.Clear();
                _avatarAuthors.Clear();
                _blockedNames.Clear();
                _unblockedNames.Clear();
                _location = string.Empty;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position)
            {
                _position = 0;
                _players.Clear();
                _avatarNames.Clear();
                _avatarAuthors.Clear();
                _location = string.Empty;
            }

            stream.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                Parse(line);
            }

            _position = stream.Position;
            return Current();
        }
        catch
        {
            return Current();
        }
    }

    public void Reset()
    {
        _players.Clear();
        _avatarNames.Clear();
        _avatarAuthors.Clear();
        _blockedNames.Clear();
        _unblockedNames.Clear();
        _location = string.Empty;
        _position = 0;
        _logPath = string.Empty;
        _lastLogScan = DateTime.MinValue;
        _lastGameCheck = DateTime.MinValue;
        _lastGameRunning = false;
    }

    private VrChatLogSnapshot? Current()
    {
        if (!LaunchService.IsLocation(_location))
        {
            return null;
        }

        var avatars = new Dictionary<string, LoggedAvatar>(StringComparer.Ordinal);
        foreach (var player in _players.Values)
        {
            if (!_avatarNames.TryGetValue(player.DisplayName, out var avatarName) ||
                string.IsNullOrWhiteSpace(avatarName))
            {
                continue;
            }

            _avatarAuthors.TryGetValue(avatarName, out var author);
            var key = string.IsNullOrWhiteSpace(player.UserId)
                ? "name:" + player.DisplayName
                : player.UserId;
            avatars[key] = new LoggedAvatar(avatarName, author);
        }

        // The signed-in player is not always present in OnPlayerJoined, but
        // VRChat still logs their own "Switching <name> to avatar ..." line.
        // Expose those switches by display name so self changes are observable.
        var switches = new Dictionary<string, LoggedAvatar>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _avatarNames)
        {
            _avatarAuthors.TryGetValue(pair.Value, out var author);
            switches[pair.Key] = new LoggedAvatar(pair.Value, author);
        }

        return new VrChatLogSnapshot(
            _location,
            _players.Values
                .OrderBy(player => player.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            new HashSet<string>(_blockedNames, StringComparer.CurrentCultureIgnoreCase),
            new HashSet<string>(_unblockedNames, StringComparer.CurrentCultureIgnoreCase),
            avatars,
            switches);
    }

    private void Parse(string line)
    {
        var joining = Joining.Match(line);
        if (joining.Success)
        {
            var location = joining.Groups["location"].Value.Trim();
            if (LaunchService.IsLocation(location))
            {
                if (!string.Equals(InstanceKey(location), InstanceKey(_location), StringComparison.Ordinal))
                {
                    _players.Clear();
                    _avatarNames.Clear();
                    _avatarAuthors.Clear();
                    _blockedNames.Clear();
                    _unblockedNames.Clear();
                }

                _location = location;
            }

            return;
        }

        var unpacking = Unpacking.Match(line);
        if (unpacking.Success)
        {
            var avatarName = unpacking.Groups["name"].Value.Trim();
            var author = unpacking.Groups["author"].Value.Trim();
            if (avatarName.Length > 0 && author.Length > 0)
            {
                _avatarAuthors[avatarName] = author;
            }

            return;
        }

        var switching = Switching.Match(line);
        if (switching.Success)
        {
            var player = switching.Groups["name"].Value.Trim();
            var avatar = switching.Groups["avatar"].Value.Trim();
            if (player.Length > 0 && avatar.Length > 0)
            {
                _avatarNames[player] = avatar;
            }

            return;
        }

        var joined = Joined.Match(line);
        if (joined.Success)
        {
            Add(joined.Groups["name"].Value, joined.Groups["id"].Value);
            return;
        }

        var blocked = Blocked.Match(line);
        if (blocked.Success)
        {
            var name = blocked.Groups["name"].Value.Trim();
            if (name.Length > 0)
            {
                _blockedNames.Add(name);
                _unblockedNames.Remove(name);
            }

            return;
        }

        var unblocked = Unblocked.Match(line);
        if (unblocked.Success)
        {
            var name = unblocked.Groups["name"].Value.Trim();
            if (name.Length > 0)
            {
                _blockedNames.Remove(name);
                _unblockedNames.Add(name);
            }

            return;
        }

        if (LeftRoom.IsMatch(line))
        {
            _players.Clear();
            _avatarNames.Clear();
            _avatarAuthors.Clear();
            _blockedNames.Clear();
            _unblockedNames.Clear();
            _location = string.Empty;
            return;
        }

        var left = Left.Match(line);
        if (left.Success)
        {
            Remove(left.Groups["name"].Value.Trim(), left.Groups["id"].Value);
        }
    }

    private void Add(string rawName, string userId)
    {
        var name = rawName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(userId))
        {
            // A name-only row for the same player may already be present.
            var oldKey = _players
                .FirstOrDefault(pair =>
                    pair.Value.UserId.Length == 0 &&
                    string.Equals(pair.Value.DisplayName, name, StringComparison.Ordinal))
                .Key;
            if (oldKey is not null)
            {
                _players.Remove(oldKey);
            }

            _players[userId] = new VrChatLogPlayer(userId, name);
            return;
        }

        var key = NameKey(name);
        if (!_players.ContainsKey(key))
        {
            _players[key] = new VrChatLogPlayer(string.Empty, name);
        }
    }

    private void Remove(string name, string userId)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            _players.Remove(userId);
            return;
        }

        var key = _players
            .FirstOrDefault(pair =>
                pair.Value.UserId.Length == 0 &&
                string.Equals(pair.Value.DisplayName, name, StringComparison.Ordinal))
            .Key;
        if (key is not null)
        {
            _players.Remove(key);
        }
    }

    private static string NameKey(string name) => "name:" + name;

    private static string InstanceKey(string location)
    {
        var value = location ?? string.Empty;
        var separator = value.IndexOf('~');
        return separator > 0 ? value[..separator] : value;
    }

    internal static string? FindLatestLog()
    {
        var directory = VrcLogPaths.Directory;
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "output_log_*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    internal static bool IsVrChatRunning()
    {
        var processes = Process.GetProcessesByName("VRChat");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
