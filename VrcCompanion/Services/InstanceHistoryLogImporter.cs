using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Rebuilds real instance rows from VRChat's own output logs. This is the same
/// source VRCX uses for historical rosters; it is only used to seed the local
/// history, not to invent entries. New activity is recorded live by
/// <see cref="InstanceHistoryRecorder"/>.
/// </summary>
public sealed class InstanceHistoryLogImporter
{
    private static readonly Regex LineTimestamp = new(
        @"^(?<time>\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

    public IReadOnlyList<InstanceHistoryEntry> Import(
        string accountUserId,
        string accountDisplayName,
        string? logDirectory = null)
    {
        var results = new List<InstanceHistoryEntry>();
        if (string.IsNullOrWhiteSpace(accountUserId))
        {
            return results;
        }

        var directory = string.IsNullOrWhiteSpace(logDirectory)
            ? VrcLogPaths.Directory
            : logDirectory;

        if (!Directory.Exists(directory))
        {
            return results;
        }

        string[] files;
        try
        {
            files = Directory
                .GetFiles(directory, "output_log_*.txt")
                .OrderBy(File.GetLastWriteTimeUtc)
                .TakeLast(80)
                .ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return results;
        }

        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 16384);
                ParseFile(reader, accountUserId, accountDisplayName, results);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        return results
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(entry => entry.JoinedAt)
            .ToList();
    }

    private static void ParseFile(
        TextReader reader,
        string accountUserId,
        string accountDisplayName,
        List<InstanceHistoryEntry> results)
    {
        Session? session = null;

        while (reader.ReadLine() is { } line)
        {
            var timeMatch = LineTimestamp.Match(line);
            if (!timeMatch.Success ||
                !DateTime.TryParseExact(
                    timeMatch.Groups["time"].Value,
                    "yyyy.MM.dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var localTime))
            {
                continue;
            }

            var timestamp = new DateTimeOffset(DateTime.SpecifyKind(localTime, DateTimeKind.Local));
            var joining = Joining.Match(line);
            if (joining.Success)
            {
                CloseSession(session, timestamp, results);
                var location = joining.Groups["location"].Value.Trim();
                session = Session.Create(accountUserId, location, timestamp);
                if (session is not null && !string.IsNullOrWhiteSpace(accountDisplayName))
                {
                    var self = session.Entry.GetOrAddPlayer(accountUserId, accountDisplayName);
                    if (!self.HasOpenVisit)
                    {
                        self.StartVisit(timestamp);
                    }
                }
                continue;
            }

            if (session is null)
            {
                continue;
            }

            var joined = Joined.Match(line);
            if (joined.Success)
            {
                var player = session.Entry.GetOrAddPlayer(
                    joined.Groups["id"].Value,
                    joined.Groups["name"].Value);
                if (!player.HasOpenVisit)
                {
                    player.StartVisit(timestamp);
                }
                session.Entry.LastSeenAt = timestamp;
                continue;
            }

            var left = Left.Match(line);
            if (left.Success)
            {
                var userId = left.Groups["id"].Value;
                var name = left.Groups["name"].Value.Trim();
                var player = FindPlayer(session.Entry, userId, name);
                CloseLastVisit(player, timestamp);
                session.Entry.LastSeenAt = timestamp;
                continue;
            }

            if (LeftRoom.IsMatch(line))
            {
                CloseSession(session, timestamp, results);
                session = null;
            }
        }

        if (session is not null)
        {
            session.Entry.LastSeenAt = session.Entry.Players
                .SelectMany(player => player.Visits)
                .Select(visit => visit.LastSeenAt)
                .DefaultIfEmpty(session.Entry.LastSeenAt)
                .Max();
            results.Add(session.Entry);
        }
    }

    private static InstanceHistoryPlayer? FindPlayer(
        InstanceHistoryEntry entry,
        string userId,
        string name)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var byId = entry.Players.FirstOrDefault(player =>
                string.Equals(player.UserId, userId, StringComparison.Ordinal));
            if (byId is not null)
            {
                return byId;
            }
        }

        return entry.Players.FirstOrDefault(player =>
            string.IsNullOrWhiteSpace(player.UserId) &&
            string.Equals(player.DisplayName, name, StringComparison.OrdinalIgnoreCase));
    }

    private static void CloseLastVisit(InstanceHistoryPlayer? player, DateTimeOffset at)
    {
        if (player is null)
        {
            return;
        }

        for (var i = player.Visits.Count - 1; i >= 0; i--)
        {
            if (player.Visits[i].LeftAt is not null)
            {
                continue;
            }

            var visit = player.Visits[i];
            visit.LastSeenAt = at;
            visit.LeftAt = at < visit.JoinedAt ? visit.JoinedAt : at;
            return;
        }
    }

    private static void CloseSession(
        Session? session,
        DateTimeOffset at,
        List<InstanceHistoryEntry> results)
    {
        if (session is null)
        {
            return;
        }

        session.Entry.Close(at);
        results.Add(session.Entry);
    }

    private sealed class Session
    {
        private Session(InstanceHistoryEntry entry)
        {
            Entry = entry;
        }

        public InstanceHistoryEntry Entry { get; }

        public static Session? Create(
            string accountUserId,
            string location,
            DateTimeOffset timestamp)
        {
            if (!LaunchService.IsLocation(location))
            {
                return null;
            }

            var entry = InstanceHistoryStore.NewFromLog(accountUserId, location, timestamp);
            return entry is null ? null : new Session(entry);
        }
    }
}
