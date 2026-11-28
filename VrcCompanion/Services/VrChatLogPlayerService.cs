using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VrcCompanion.Services;

public sealed record VrChatLogPlayer(string UserId, string DisplayName);

/// <summary>What VRChat's log reported happening in the instance the reader is in.</summary>
public enum VrChatLogActivityKind
{
    Joined,
    Left,
    Udon,

    /// <summary>A player changed avatar while in the instance.</summary>
    AvatarChanged
}

/// <summary>
/// One join, leave or Udon event read from VRChat's own log. The timestamp is the
/// line's own local time, so the row shows when it happened rather than when the
/// app noticed it. <see cref="RepeatCount"/> folds a run of identical Udon lines
/// into one row: a world that writes the same line every frame must not fill the
/// list with one message.
/// </summary>
public sealed record VrChatLogActivity(
    VrChatLogActivityKind Kind,
    string DisplayName,
    string UserId,
    string Detail,
    DateTimeOffset At,
    int RepeatCount = 1,
    string AvatarId = "",
    string AvatarName = "",
    string AvatarAuthor = "");

public sealed record VrChatLogSnapshot(
    string Location,
    IReadOnlyList<VrChatLogPlayer> Players,
    IReadOnlySet<string>? BlockedPlayerNames = null,
    IReadOnlySet<string>? UnblockedPlayerNames = null,
    IReadOnlyDictionary<string, LoggedAvatar>? Avatars = null,
    IReadOnlyDictionary<string, LoggedAvatar>? AvatarSwitches = null,
    IReadOnlyList<VrChatLogActivity>? Activity = null,
    int ActivityRevision = 0);

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

    // The client tags its own Udon messages this way. World Debug.Log output is not
    // matched here: this box reports events, not a chat log.
    private static readonly Regex UdonEvent = new(
        @"\[(?:UdonBehaviour|UdonSharp|Udon)\]\s*(?<message>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 2026.11.27 12:00:05 Log        -  [Behaviour] OnPlayerJoined Someone (usr_...)
    private static readonly Regex LineStart = new(
        @"^(?<stamp>\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const int ActivityLimit = 100;

    private readonly Func<string?> _findLatestLog;
    private readonly Func<bool> _isGameRunning;
    private readonly Dictionary<string, VrChatLogPlayer> _players = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _avatarNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _avatarAuthors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blockedNames = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly HashSet<string> _unblockedNames = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly List<VrChatLogActivity> _activity = new(ActivityLimit);
    private IReadOnlyList<VrChatLogActivity>? _activitySnapshot;
    private int _activityRevision;
    private string _logPath = string.Empty;
    private long _position;
    private string _location = string.Empty;
    private DateTime _lastLogScan = DateTime.MinValue;
    private DateTime _lastGameCheck = DateTime.MinValue;
    private bool _lastGameRunning;

    // Poll is called from a background thread now, by two timers that share this instance: the
    // main window's history tick and the Instance popup. They share one position, one roster and
    // one set of dictionaries, so a poll is serialised whole rather than split. The game check is
    // a separate lock because it is also asked from the UI thread, and it must not wait behind a
    // read of the log.
    private readonly object _gate = new();
    private readonly object _gameGate = new();
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
            lock (_gameGate)
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
    }

    /// <summary>
    /// Reads any new log lines and returns the current location plus the players
    /// seen in it. Returns null until a valid world instance has been joined.
    /// </summary>
    public VrChatLogSnapshot? Poll()
    {
        lock (_gate)
        {
            return PollCore();
        }
    }

    private VrChatLogSnapshot? PollCore()
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
                ClearActivity();
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
                ClearActivity();
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
        lock (_gate)
        {
            ResetCore();
        }
    }

    private void ResetCore()
    {
        _players.Clear();
        _avatarNames.Clear();
        _avatarAuthors.Clear();
        _blockedNames.Clear();
        _unblockedNames.Clear();
        ClearActivity();
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
            switches,
            // Published once per change: a poll that finds no new line returns the
            // same list, so a bound page never re-walks it for nothing.
            _activitySnapshot ??= _activity.ToArray(),
            _activityRevision);
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
                    // Which avatar a player wears is not the room's business: VRChat only
                    // writes "Switching … to avatar …" when it loads an avatar, and a
                    // rejoin usually reuses the avatars already in memory, so keeping
                    // these is what lets the box show everyone's avatar again.
                    _blockedNames.Clear();
                    _unblockedNames.Clear();
                    // Joins and leaves belong to the room they happened in, so a new
                    // instance starts its own list instead of inheriting the last one.
                    ClearActivity();
                }

                _location = location;
            }

            return;
        }

        // One cheap substring test before the regex: almost every line is not Udon.
        if (line.Contains("Udon", StringComparison.Ordinal) && TryRecordUdon(line))
        {
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
                RecordAvatarChange(player, avatar, line);
            }

            return;
        }

        var joined = Joined.Match(line);
        if (joined.Success)
        {
            var name = joined.Groups["name"].Value.Trim();
            var userId = joined.Groups["id"].Value;
            Add(name, userId);
            RecordActivity(VrChatLogActivityKind.Joined, name, userId, "Joined the instance", line);
            // A rejoin does not make VRChat re-log an avatar it already has loaded, so
            // the Avatar filter would only show the few players who happened to switch.
            // Show the avatar this player was last seen wearing, at the moment they
            // joined; RecordAvatarChange folds it into a real switch line if one follows.
            if (_avatarNames.TryGetValue(name, out var worn) && !string.IsNullOrWhiteSpace(worn))
            {
                RecordAvatarChange(name, worn, line);
            }

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
            // The avatars stay known across a leave; see the Joining branch.
            _blockedNames.Clear();
            _unblockedNames.Clear();
            ClearActivity();
            _location = string.Empty;
            return;
        }

        var left = Left.Match(line);
        if (left.Success)
        {
            var name = left.Groups["name"].Value.Trim();
            var userId = left.Groups["id"].Value;
            Remove(name, userId);
            RecordActivity(VrChatLogActivityKind.Left, name, userId, "Left the instance", line);
        }
    }

    /// <summary>
    /// Records the Udon lines VRChat writes itself. "Configuring Udon" is the moment a
    /// world's Udon starts after a join; the tagged lines carry exceptions and halts.
    /// </summary>
    private bool TryRecordUdon(string line)
    {
        if (line.Contains("] Configuring Udon", StringComparison.Ordinal))
        {
            RecordActivity(VrChatLogActivityKind.Udon, string.Empty, string.Empty, "Udon configured for this world", line);
            return true;
        }

        var match = UdonEvent.Match(line);
        if (!match.Success)
        {
            return false;
        }

        var message = match.Groups["message"].Value.Trim();
        if (message.Length == 0)
        {
            return false;
        }

        RecordActivity(VrChatLogActivityKind.Udon, string.Empty, string.Empty, Shorten(message), line);
        return true;
    }

    /// <summary>
    /// Records a player changing avatar. VRChat's log names the avatar but not its id, so
    /// the row resolves it by name exactly like the Activity page's own switch rows; the
    /// author is filled in when that avatar's unpacking line has already been read.
    /// </summary>
    private void RecordAvatarChange(string player, string avatarName, string line)
    {
        // A join re-shows the avatar we already knew, and VRChat sometimes repeats a
        // switch line; either way the newest row for this player already says it.
        var previous = _activity.LastOrDefault(entry =>
            entry.Kind == VrChatLogActivityKind.AvatarChanged &&
            string.Equals(entry.DisplayName, player, StringComparison.CurrentCultureIgnoreCase));
        if (previous is not null &&
            string.Equals(previous.AvatarName, avatarName, StringComparison.Ordinal))
        {
            return;
        }

        var userId = _players.Values
            .FirstOrDefault(candidate =>
                string.Equals(candidate.DisplayName, player, StringComparison.CurrentCultureIgnoreCase))
            ?.UserId ?? string.Empty;
        _avatarAuthors.TryGetValue(avatarName, out var author);

        RecordActivity(
            VrChatLogActivityKind.AvatarChanged,
            player,
            userId,
            avatarName,
            line,
            avatarName,
            author ?? string.Empty);
    }

    private void RecordActivity(
        VrChatLogActivityKind kind,
        string displayName,
        string userId,
        string detail,
        string line,
        string avatarName = "",
        string avatarAuthor = "")
    {
        var at = LineTimestamp(line);
        if (kind == VrChatLogActivityKind.Udon &&
            _activity.Count > 0 &&
            _activity[^1] is { Kind: VrChatLogActivityKind.Udon } previous &&
            string.Equals(previous.Detail, detail, StringComparison.Ordinal))
        {
            // The line right above says the same thing, so the world is repeating it.
            // Folding it in keeps the newest 100 *events* instead of one world's loop,
            // and the row keeps the time of the last time it happened.
            _activity[^1] = previous with
            {
                RepeatCount = previous.RepeatCount + 1,
                At = at
            };
        }
        else
        {
            _activity.Add(new VrChatLogActivity(
                kind,
                displayName ?? string.Empty,
                userId ?? string.Empty,
                detail ?? string.Empty,
                at,
                RepeatCount: 1,
                AvatarName: avatarName ?? string.Empty,
                AvatarAuthor: avatarAuthor ?? string.Empty));
            if (_activity.Count > ActivityLimit)
            {
                // A busy instance can log joins faster than anyone reads them; the box
                // shows the tail, so the oldest rows are the ones to drop.
                _activity.RemoveRange(0, _activity.Count - ActivityLimit);
            }
        }

        _activitySnapshot = null;
        _activityRevision++;
    }

    private void ClearActivity()
    {
        if (_activity.Count == 0)
        {
            return;
        }

        _activity.Clear();
        _activitySnapshot = null;
        _activityRevision++;
    }

    /// <summary>The line's own local time; VRChat writes `yyyy.MM.dd HH:mm:ss` without a zone.</summary>
    private static DateTimeOffset LineTimestamp(string line)
    {
        var match = LineStart.Match(line ?? string.Empty);
        if (match.Success &&
            DateTime.TryParseExact(
                match.Groups["stamp"].Value,
                "yyyy.MM.dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }

        return DateTimeOffset.Now;
    }

    private static string Shorten(string value)
        => value.Length <= 160 ? value : value[..159] + "\u2026";

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
