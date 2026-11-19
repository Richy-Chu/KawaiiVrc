using System;
using System.Collections.Generic;
using System.Linq;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>A world resolved from the API for a picture's session.</summary>
public sealed record MediaWorldInfo(string Name, string ThumbnailUrl, string AuthorName);

/// <summary>A player's current name and avatar thumbnail, resolved from the API.</summary>
public sealed record MediaPersonInfo(string DisplayName, string AvatarUrl);

/// <summary>One person who was in the world when a picture was taken.</summary>
public sealed class MediaPersonItem : ViewModels.ViewModelBase
{
    private string _displayName;
    private string _avatarUrl;

    public MediaPersonItem(
        string userId,
        string displayName,
        string avatarUrl,
        bool isSelf,
        DateTimeOffset? joinedAt = null,
        DateTimeOffset? leftAt = null)
    {
        UserId = userId ?? string.Empty;
        _displayName = string.IsNullOrWhiteSpace(displayName) ? "Unknown player" : displayName.Trim();
        _avatarUrl = avatarUrl ?? string.Empty;
        IsSelf = isSelf;
        JoinedAt = joinedAt;
        LeftAt = leftAt;
    }

    public string UserId { get; }

    public string DisplayName => _displayName;

    public string AvatarUrl => _avatarUrl;

    public bool IsSelf { get; }

    /// <summary>When this person was seen in the session the picture belongs to.</summary>
    public DateTimeOffset? JoinedAt { get; }

    public DateTimeOffset? LeftAt { get; }

    public bool HasUserId => !string.IsNullOrWhiteSpace(UserId);

    public bool HasAvatar => !string.IsNullOrWhiteSpace(_avatarUrl);

    public string Initial => _displayName.Length == 0
        ? "?"
        : _displayName[..1].ToUpperInvariant();

    public string DisplayNameText => IsSelf ? $"{_displayName} (you)" : _displayName;

    /// <summary>
    /// The log only knows a joined player's name, so the viewer fills the icon
    /// in afterwards from the user/profile endpoints.
    /// </summary>
    public void ApplyProfile(string? displayName, string? avatarUrl)
    {
        if (!string.IsNullOrWhiteSpace(displayName) && displayName.Trim() != _displayName)
        {
            _displayName = displayName.Trim();
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(DisplayNameText));
            OnPropertyChanged(nameof(Initial));
        }

        if (!string.IsNullOrWhiteSpace(avatarUrl) && avatarUrl.Trim() != _avatarUrl)
        {
            _avatarUrl = avatarUrl.Trim();
            OnPropertyChanged(nameof(AvatarUrl));
            OnPropertyChanged(nameof(HasAvatar));
        }
    }
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
        IReadOnlyList<MediaPersonItem> people,
        string accessLabel,
        string groupId,
        string groupName,
        string groupIconUrl)
    {
        AccessLabel = accessLabel ?? string.Empty;
        GroupId = groupId ?? string.Empty;
        GroupName = groupName ?? string.Empty;
        GroupIconUrl = groupIconUrl ?? string.Empty;
        SessionId = session.Id;
        Location = session.Location ?? string.Empty;
        WorldId = session.WorldId ?? string.Empty;
        WorldName = session.WorldName ?? string.Empty;
        WorldThumbnailUrl = session.WorldThumbnailUrl ?? string.Empty;
        WorldAuthorName = session.WorldAuthorName ?? string.Empty;
        JoinedAt = session.JoinedAt;
        LeftAt = session.LeftAt;
        LastSeenAt = session.LastSeenAt;
        _people = people.ToList();
        SourceEntryIds = session.SourceEntryIds.ToList();
    }

    public string SessionId { get; }

    public string Location { get; }

    public string WorldId { get; }

    public string WorldName { get; private set; }

    public string WorldThumbnailUrl { get; private set; }

    public string WorldAuthorName { get; private set; }

    public DateTimeOffset JoinedAt { get; private set; }

    public DateTimeOffset? LeftAt { get; }

    public DateTimeOffset LastSeenAt { get; }

    private readonly List<MediaPersonItem> _people;

    public IReadOnlyList<MediaPersonItem> People => _people;

    /// <summary>
    /// True when the picture itself supplied people the session log did not
    /// have. VRCX writes the roster next to the shot even when the log only
    /// recorded the world, so the viewer prefers the fuller list.
    /// </summary>
    public bool HasPhotoRoster { get; private set; }

    /// <summary>
    /// Adds the people saved inside the picture to a session's roster without
    /// losing the join/leave times the log already knows for shared names.
    /// </summary>
    /// <returns>True when at least one player was added or upgraded.</returns>
    public bool ApplyPhotoRoster(IEnumerable<VrcPhotoPlayer> roster, DateTimeOffset takenAt)
    {
        var changed = false;
        foreach (var player in roster)
        {
            var name = player.DisplayName?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            var userId = player.UserId?.Trim() ?? string.Empty;
            // Display names are unique in VRChat, and older rows can be
            // name-only, so match on either the id or the name.
            var index = _people.FindIndex(person =>
                (userId.Length > 0 && string.Equals(person.UserId, userId, StringComparison.Ordinal)) ||
                string.Equals(person.DisplayName, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var existing = _people[index];

                // A name-only log row can be upgraded once the picture supplies
                // the account id, keeping the times it already had.
                if (existing.UserId.Length == 0 && userId.Length > 0)
                {
                    _people[index] = new MediaPersonItem(
                        userId,
                        name,
                        existing.AvatarUrl,
                        existing.IsSelf,
                        existing.JoinedAt,
                        existing.LeftAt);
                    changed = true;
                }
                continue;
            }

            _people.Add(new MediaPersonItem(userId, name, string.Empty, isSelf: false, takenAt, takenAt));
            changed = true;
        }

        if (changed)
        {
            HasPhotoRoster = true;
        }

        return changed;
    }

    /// <summary>Every stored session id this row represents (merged visits).</summary>
    public IReadOnlyList<string> SourceEntryIds { get; }

    /// <summary>Public, Friends, Friends+, Invite, Invite+, Group, Group+ or Group public.</summary>
    public string AccessLabel { get; }

    /// <summary>The group the instance belonged to, empty for a normal public session.</summary>
    public string GroupId { get; }

    public string GroupName { get; private set; }

    public string GroupIconUrl { get; private set; }

    public bool HasGroup => !string.IsNullOrWhiteSpace(GroupId);

    public bool CanOpenGroup => HasGroup;

    /// <summary>Fills world name/thumbnail/creator once the API answers, so old
    /// log rows do not stay as "Unknown world".</summary>
    public void ApplyWorldInfo(string? name, string? thumbnailUrl, string? authorName)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            WorldName = name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(thumbnailUrl))
        {
            WorldThumbnailUrl = thumbnailUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(authorName))
        {
            WorldAuthorName = authorName.Trim();
        }
    }

    public string GroupDisplayName => !HasGroup
        ? string.Empty
        : string.IsNullOrWhiteSpace(GroupName)
            ? "Group instance"
            : GroupName;

    /// <summary>Fills in the group name/icon after a lookup, so an old session
    /// still names the group once the API answers.</summary>
    public void ApplyGroupInfo(string? name, string? iconUrl)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            GroupName = name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(iconUrl))
        {
            GroupIconUrl = iconUrl.Trim();
        }
    }

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

    /// <summary>True when this context was rebuilt from the picture's own
    /// metadata instead of a recorded instance session.</summary>
    public bool IsPhotoMetadata { get; private set; }

    /// <summary>Marks a context that came from the file, not the session log.</summary>
    public void MarkPhotoMetadata(DateTimeOffset? takenAt)
    {
        IsPhotoMetadata = true;
        if (takenAt is { } at)
        {
            JoinedAt = at;
        }
    }

    public string SessionText => IsPhotoMetadata
        ? $"Taken {JoinedAt.ToLocalTime():MMM d, yyyy · HH:mm}"
        : LeftAt is null
            ? $"Session still open · since {SessionDateText}"
            : $"Session · {SessionDateText} · {DurationText}";

    /// <summary>The panel header: a recorded session, or the picture itself.</summary>
    public string SessionTitle => IsPhotoMetadata ? "PHOTO" : "SESSION";

    /// <summary>One line on where the panel's world and players came from.</summary>
    public string PhotoNote => IsPhotoMetadata
        ? HasPeople
            ? "Read from the picture itself: VRChat saved the world, VRCX saved the players."
            : "Read from the picture itself: VRChat saved the world and author, but VRCX saved no player list."
        : HasPhotoRoster
            ? "Players read from the picture itself; the session times come from your local history."
            : string.Empty;

    /// <summary>
    /// A logged session can be reopened in the history popup, and so can a
    /// picture's own metadata: the photo supplies the world, time and roster.
    /// </summary>
    public bool CanOpenSessionView => HasSession || (IsPhotoMetadata && HasWorld);

    /// <summary>Packages the context as a history row the popup can display.</summary>
    public InstanceHistoryEntry ToHistoryEntry()
    {
        var instanceId = string.IsNullOrWhiteSpace(Location)
            ? string.Empty
            : LaunchService.GetInstanceIdFromLocation(Location) ?? string.Empty;

        return new InstanceHistoryEntry
        {
            Id = SessionId,
            Location = Location,
            WorldId = WorldId,
            InstanceId = instanceId,
            WorldName = WorldName,
            WorldThumbnailUrl = WorldThumbnailUrl,
            WorldAuthorName = WorldAuthorName,
            JoinedAt = JoinedAt,
            LeftAt = LeftAt ?? LastSeenAt,
            LastSeenAt = LastSeenAt,
            OwnerId = string.Empty,
            OwnerName = WorldAuthorName,
            Players = People
                .Select(person =>
                {
                    var player = new InstanceHistoryPlayer
                    {
                        UserId = person.UserId,
                        DisplayName = person.DisplayName,
                        AvatarUrl = person.AvatarUrl
                    };
                    if (person.JoinedAt is { } joined)
                    {
                        player.Visits.Add(new InstanceHistoryVisit
                        {
                            JoinedAt = joined,
                            LeftAt = person.LeftAt,
                            LastSeenAt = person.LeftAt ?? joined
                        });
                    }
                    return player;
                })
                .ToList()
        };
    }

    public bool HasPeople => People.Count > 0;

    public string PeopleCountText => People.Count switch
    {
        0 => "No player list",
        1 => "1 player recorded",
        _ => $"{People.Count} players recorded"
    };

    /// <summary>Shown under the player header when the roster is empty.</summary>
    public string NoPlayersText => IsPhotoMetadata || HasPhotoRoster
        ? "VRCX didn't save who was there for this picture."
        : "No players were recorded for this session.";
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
        string? accountUserId = null,
        Func<string, (string? Name, string? IconUrl)>? groupLookup = null)
    {
        var result = new Dictionary<string, MediaContext>(StringComparer.Ordinal);
        var orderedSessions = sessions
            .Where(session => !string.IsNullOrWhiteSpace(session.WorldId) ||
                              LaunchService.IsLocation(session.Location))
            .OrderBy(session => session.JoinedAt)
            .ToList();
        if (orderedSessions.Count == 0)
        {
            return result;
        }

        foreach (var item in items)
        {
            var context = FindContext(item.TakenAt, orderedSessions, accountUserId, groupLookup);
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
        string? accountUserId = null,
        Func<string, (string? Name, string? IconUrl)>? groupLookup = null)
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

        return match is null ? null : Describe(match, accountUserId, groupLookup, takenAtLocal);
    }

    public static MediaContext Describe(
        InstanceHistoryEntry session,
        string? accountUserId,
        Func<string, (string? Name, string? IconUrl)>? groupLookup = null,
        DateTime? atLocal = null)
    {
        // VRCX shows the people who were in the instance when the picture was
        // taken, not everyone who visited during a long session.
        var roster = session.Players;
        if (atLocal is { } at)
        {
            var present = session.Players
                .Where(player =>
                    // No join/leave rows means the log never saw them move, so
                    // they stay (this is how the account owner usually appears).
                    player.Visits.Count == 0 ||
                    player.Visits.Any(visit =>
                        visit.JoinedAt.ToLocalTime().DateTime <= at &&
                        (visit.LeftAt is null || visit.LeftAt.Value.ToLocalTime().DateTime >= at)))
                .ToList();
            if (present.Count > 0)
            {
                roster = present;
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var people = new List<MediaPersonItem>();
        foreach (var player in roster
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
                string.Equals(player.UserId, accountUserId, StringComparison.Ordinal),
                player.FirstJoinedAt == DateTimeOffset.MinValue ? null : player.FirstJoinedAt,
                player.LastLeftAt));
        }

        var (accessLabel, groupId) = InstanceAccessOf(session);
        var groupName = string.Empty;
        var groupIconUrl = string.Empty;
        if (!string.IsNullOrWhiteSpace(groupId) && groupLookup is not null)
        {
            try
            {
                var info = groupLookup(groupId);
                groupName = info.Name ?? string.Empty;
                groupIconUrl = info.IconUrl ?? string.Empty;
            }
            catch (Exception)
            {
                // A missing group must not cost the picture its world/session.
            }
        }

        return new MediaContext(session, people, accessLabel, groupId, groupName, groupIconUrl);
    }

    /// <summary>Reads the instance access and group straight from the location.</summary>
    private static (string AccessLabel, string GroupId) InstanceAccessOf(InstanceHistoryEntry session)
    {
        // Older rows keep the whole location in InstanceId, newer ones only the
        // instance part; both carry the ~group(...) markers, so search both.
        var value = $"{session.Location}|{session.InstanceId}";
        return (
            ViewModels.InstancePresentation.AccessLabel(null, value),
            ViewModels.InstancePresentation.GroupIdFrom(value) ?? string.Empty);
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
