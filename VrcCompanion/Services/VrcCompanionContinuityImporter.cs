using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Imports the companion's own VRChat-log history. This source is local and
/// independent, but it often observes the same visit as VRCX-0/VRCNext; rows
/// already covered by an external source are skipped instead of counted twice.
/// </summary>
public sealed class VrcCompanionContinuityImporter
{
    public const string SourceName = "vrc-companion";

    public static string DefaultPath => InstanceHistoryStore.DefaultPath;

    public ContinuityImportResult Import(
        ContinuityStore store,
        string? accountId,
        string? accountDisplayName)
    {
        var result = new ContinuityImportResult
        {
            Source = SourceName,
            Path = DefaultPath,
            ImportedAt = DateTimeOffset.UtcNow
        };

        if (string.IsNullOrWhiteSpace(accountId))
        {
            result.Message = "Sign in to VRChat before importing the companion's own log history.";
            return result;
        }

        if (!File.Exists(DefaultPath))
        {
            result.Message = "The companion has no instance-history database yet.";
            return result;
        }

        try
        {
            var source = new InstanceHistoryStore(DefaultPath);
            var entries = source.ForAccount(accountId);
            var taken = store.GetSessions(accountId, null, 5000).ToList();
            var sessions = new List<ContinuitySession>();
            var people = new Dictionary<string, ContinuityPerson>(StringComparer.Ordinal);
            var worlds = new Dictionary<string, ContinuityWorld>(StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Location))
                {
                    continue;
                }

                // The same VRChat log is commonly captured by VRCX-0 and the
                // companion. Treat a start within five minutes in the same
                // location as the same visit.
                if (taken.Any(candidate =>
                        string.Equals(candidate.AccountUserId, accountId, StringComparison.Ordinal) &&
                        string.Equals(candidate.Location, entry.Location, StringComparison.Ordinal) &&
                        Math.Abs((candidate.JoinedAt - entry.JoinedAt).TotalMinutes) <= 5))
                {
                    continue;
                }

                var start = entry.JoinedAt;
                var end = entry.LeftAt ??
                          (entry.LastSeenAt > start ? entry.LastSeenAt : start.AddSeconds(1));
                if (end <= start)
                {
                    end = start.AddSeconds(1);
                }

                var sessionPeople = new Dictionary<string, ContinuitySessionPerson>(StringComparer.Ordinal);
                AddPerson(
                    sessionPeople,
                    accountId,
                    accountDisplayName,
                    start,
                    end);

                foreach (var player in entry.Players)
                {
                    var key = PersonKey(player.UserId, player.DisplayName);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var first = player.Visits.Count == 0
                        ? start
                        : player.Visits.Min(visit => visit.JoinedAt);
                    if (first < start)
                    {
                        first = start;
                    }

                    var last = player.Visits
                        .Select(visit => visit.LeftAt)
                        .Where(left => left is not null)
                        .Select(left => left!.Value)
                        .DefaultIfEmpty(end)
                        .Max();
                    if (last > end)
                    {
                        last = end;
                    }

                    AddPerson(
                        sessionPeople,
                        player.UserId,
                        player.DisplayName,
                        first > end ? end : first,
                        last,
                        player.AvatarUrl);

                    if (!people.ContainsKey(key))
                    {
                        people[key] = new ContinuityPerson
                        {
                            PersonKey = key,
                            UserId = player.UserId,
                            DisplayName = player.DisplayName,
                            AvatarUrl = player.AvatarUrl,
                            Source = SourceName
                        };
                    }
                }

                var sourceKey = accountId + "|" + entry.Id;
                var session = new ContinuitySession
                {
                    Id = ContinuitySession.MakeId(SourceName, sourceKey),
                    AccountUserId = accountId,
                    Source = SourceName,
                    SourceKey = sourceKey,
                    Location = entry.Location,
                    WorldId = entry.WorldId,
                    WorldName = entry.WorldName,
                    JoinedAt = start,
                    LeftAt = end,
                    DurationSeconds = Math.Max(1, (long)(end - start).TotalSeconds),
                    PlayerCount = sessionPeople.Count,
                    People = sessionPeople.Values
                        .OrderBy(person => person.JoinedAt)
                        .ThenBy(person => person.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
                sessions.Add(session);
                taken.Add(session);

                if (!string.IsNullOrWhiteSpace(entry.WorldId))
                {
                    worlds[entry.WorldId] = new ContinuityWorld
                    {
                        WorldId = entry.WorldId,
                        Name = entry.WorldName,
                        AuthorName = entry.WorldAuthorName,
                        ThumbnailUrl = entry.WorldThumbnailUrl,
                        Source = SourceName
                    };
                }
            }

            store.MergeSessions(sessions);
            store.UpsertPersonMetadata(people.Values);
            store.UpsertWorldMetadata(worlds.Values);
            result.Sessions = sessions.Count;
            result.People = people.Count;
            result.Worlds = worlds.Count;
            result.Message = sessions.Count == 0
                ? entries.Count == 0
                    ? "The companion has no logged visits for this account."
                    : "The companion's own visits are already covered by another source."
                : "Imported " + sessions.Count.ToString("N0", CultureInfo.InvariantCulture) +
                  " additional real session(s) from the companion.";
            store.RecordImport(result);
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex;
            result.Message = "Companion history import failed: " + ex.Message;
            return result;
        }
    }

    private static void AddPerson(
        IDictionary<string, ContinuitySessionPerson> people,
        string? userId,
        string? displayName,
        DateTimeOffset joinedAt,
        DateTimeOffset end,
        string? avatarUrl = null)
    {
        var key = PersonKey(userId, displayName);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!people.TryGetValue(key, out var person))
        {
            person = new ContinuitySessionPerson
            {
                PersonKey = key,
                UserId = userId ?? string.Empty,
                DisplayName = displayName ?? string.Empty,
                AvatarUrl = avatarUrl ?? string.Empty,
                JoinedAt = joinedAt
            };
            people[key] = person;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                person.DisplayName = displayName;
            }

            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                person.AvatarUrl = avatarUrl;
            }
        }

        var left = end < joinedAt ? joinedAt : end;
        person.LeftAt = left;
        person.DurationSeconds = Math.Max(1, (long)(left - person.JoinedAt).TotalSeconds);
    }

    private static string PersonKey(string? userId, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            return userId;
        }

        return string.IsNullOrWhiteSpace(displayName)
            ? string.Empty
            : "name:" + displayName.Trim();
    }
}
