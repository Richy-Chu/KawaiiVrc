using System;
using System.Collections.Generic;
using System.Linq;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>One person who was in the world when a picture was taken.</summary>
public sealed class MediaPersonItem
{
    public MediaPersonItem(string userId, string displayName, string avatarUrl, bool isSelf)
    {
        UserId = userId ?? string.Empty;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Unknown player" : displayName.Trim();
        AvatarUrl = avatarUrl ?? string.Empty;
        IsSelf = isSelf;
    }

    public string UserId { get; }

    public string DisplayName { get; }

    public string AvatarUrl { get; }

    public bool IsSelf { get; }

    public bool HasUserId => !string.IsNullOrWhiteSpace(UserId);

    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);

    public string Initial => DisplayName.Length == 0
        ? "?"
        : DisplayName[..1].ToUpperInvariant();

    public string DisplayNameText => IsSelf ? $"{DisplayName} (you)" : DisplayName;
}

/// <summary>
/// The world, session and players behind one picture, resolved from the local
/// instance history. VRChat itself stores no metadata in its screenshots, so
/// this is the same log-derived source VRCX and VRCNext read.
/// </summary>
public sealed class MediaContext
{
    public MediaContext(
        InstanceHistoryEntry session,
        IReadOnlyList<MediaPersonItem> people)
    {
        SessionId = session.Id;
        Location = session.Location ?? string.Empty;
        WorldId = session.WorldId ?? string.Empty;
        WorldName = session.WorldName ?? string.Empty;
        WorldThumbnailUrl = session.WorldThumbnailUrl ?? string.Empty;
        WorldAuthorName = session.WorldAuthorName ?? string.Empty;
        JoinedAt = session.JoinedAt;
        LeftAt = session.LeftAt;
        LastSeenAt = session.LastSeenAt;
        People = people;
        SourceEntryIds = session.SourceEntryIds.ToList();
    }

    public string SessionId { get; }

    public string Location { get; }

    public string WorldId { get; }

    public string WorldName { get; }

    public string WorldThumbnailUrl { get; }

    public string WorldAuthorName { get; }

    public DateTimeOffset JoinedAt { get; }

    public DateTimeOffset? LeftAt { get; }

    public DateTimeOffset LastSeenAt { get; }

    public IReadOnlyList<MediaPersonItem> People { get; }

    /// <summary>Every stored session id this row represents (merged visits).</summary>
    public IReadOnlyList<string> SourceEntryIds { get; }

    public bool HasWorld => !string.IsNullOrWhiteSpace(WorldId);

    public bool HasWorldName => !string.IsNullOrWhiteSpace(WorldName);

    public string WorldNameText => HasWorldName ? WorldName : "Unknown world";

    public bool HasWorldThumbnail => !string.IsNullOrWhiteSpace(WorldThumbnailUrl);

    public string WorldAuthorText => string.IsNullOrWhiteSpace(WorldAuthorName)
        ? string.Empty
        : $"by {WorldAuthorName}";

    public bool HasSession => !string.IsNullOrWhiteSpace(SessionId);

    public string SessionDateText => JoinedAt.ToLocalTime().ToString("MMM d, yyyy · HH:mm");

    public TimeSpan Duration => (LeftAt ?? LastSeenAt) - JoinedAt;

    public string DurationText
    {
        get
        {
            var duration = Duration;
            if (duration <= TimeSpan.Zero)
            {
                return "a moment";
            }

            if (duration.TotalMinutes < 1)
            {
                return "under a minute";
            }

            if (duration.TotalHours < 1)
            {
                return $"{(int)duration.TotalMinutes}m";
            }

            return duration.Minutes == 0
                ? $"{(int)duration.TotalHours}h"
                : $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }
    }

    public string SessionText => LeftAt is null
        ? $"Session still open · since {SessionDateText}"
        : $"Session · {SessionDateText} · {DurationText}";

    public bool HasPeople => People.Count > 0;

    public string PeopleCountText => People.Count switch
    {
        0 => "No players recorded for this session",
        1 => "1 player recorded",
        _ => $"{People.Count} players recorded"
    };
}

/// <summary>
/// Matches a picture's file timestamp to the locally recorded instance session
/// that was open at that moment. The log is the only place VRChat leaves this
/// information, and the Companion already keeps it for the instance history.
/// </summary>
public static class MediaMetadataResolver
{
    /// <summary>
    /// Builds a context per picture path. Pictures taken outside any recorded
    /// session simply get no entry, so the viewer can say so instead of
    /// guessing a world.
    /// </summary>
    public static IReadOnlyDictionary<string, MediaContext> Build(
        IEnumerable<MediaItem> items,
        IReadOnlyList<InstanceHistoryEntry> sessions,
        string? accountUserId = null)
    {
        var result = new Dictionary<string, MediaContext>(StringComparer.Ordinal);
        var orderedSessions = sessions
            .Where(session => LaunchService.IsLocation(session.Location))
            .OrderBy(session => session.JoinedAt)
            .ToList();
        if (orderedSessions.Count == 0)
        {
            return result;
        }

        foreach (var item in items)
        {
            var context = FindContext(item.TakenAt, orderedSessions, accountUserId);
            if (context is not null)
            {
                result[item.Path] = context;
            }
        }

        return result;
    }

    /// <summary>The session that contained a local timestamp, if any.</summary>
    public static MediaContext? FindContext(
        DateTime takenAtLocal,
        IReadOnlyList<InstanceHistoryEntry> orderedSessions,
        string? accountUserId = null)
    {
        InstanceHistoryEntry? match = null;
        foreach (var session in orderedSessions)
        {
            var joined = session.JoinedAt.ToLocalTime().DateTime;
            if (takenAtLocal < joined)
            {
                break;
            }

            var end = (session.LeftAt ?? session.LastSeenAt).ToLocalTime().DateTime;
            if (takenAtLocal <= end)
            {
                // Sessions are ordered oldest first, so the last hit is the most
                // recent one that still covers the picture.
                match = session;
            }
        }

        return match is null ? null : Describe(match, accountUserId);
    }

    public static MediaContext Describe(InstanceHistoryEntry session, string? accountUserId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var people = new List<MediaPersonItem>();
        foreach (var player in session.Players
                     .OrderByDescending(player => string.Equals(
                         player.UserId,
                         accountUserId,
                         StringComparison.Ordinal))
                     .ThenBy(player => player.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!seen.Add(player.Key))
            {
                continue;
            }

            people.Add(new MediaPersonItem(
                player.UserId,
                player.DisplayName,
                player.AvatarUrl,
                string.Equals(player.UserId, accountUserId, StringComparison.Ordinal)));
        }

        return new MediaContext(session, people);
    }
}

/// <summary>A selectable world or player chip on the media page.</summary>
public sealed class MediaFilterOption : ViewModels.ViewModelBase
{
    private bool _isSelected;

    public MediaFilterOption(string key, string label, string imageUrl, int count)
    {
        Key = key;
        Label = label;
        ImageUrl = imageUrl;
        Count = count;
    }

    public string Key { get; }

    public string Label { get; }

    public string ImageUrl { get; }

    public int Count { get; }

    public bool HasImage => !string.IsNullOrWhiteSpace(ImageUrl);

    public bool HasAvatar => HasImage;

    public string Initial => Label.Length == 0 ? "?" : Label[..1].ToUpperInvariant();

    public string CountText => Count == 1 ? "1" : Count.ToString("N0");

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
