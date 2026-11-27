using System;
using System.Collections.Generic;
using System.Linq;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>Friends added to and removed from the full friend list.</summary>
public sealed record FriendListDelta(
    IReadOnlyList<VrcUser> Added,
    IReadOnlyList<VrcUser> Removed)
{
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0;

    /// <summary>
    /// False when at least one candidate could not be confirmed. The persisted
    /// friend baseline must not advance past an unverified change.
    /// </summary>
    public bool IsComplete { get; init; } = true;

    /// <summary>
    /// True when this comparison came from the friend snapshot saved before
    /// the app closed. The exact event time is unknown in that case; the
    /// timestamp shown is when the next launch verified the change.
    /// </summary>
    public bool DetectedAfterRelaunch { get; init; }
}

/// <summary>
/// Builds an activity feed / timeline from friends' data.
/// Analyzes friend locations, status changes, and instance history.
/// </summary>
public sealed class ActivityFeedService
{
    /// <summary>
    /// Generate activity entries from a list of friends with their current status.
    /// </summary>
    public List<ActivityEntry> BuildFeedFromFriends(
        IReadOnlyList<VrcUser> friends,
        IReadOnlyList<VrcUser>? previousFriends = null)
    {
        var entries = new List<ActivityEntry>();

        var previousLookup = previousFriends?
            .Where(friend => !string.IsNullOrWhiteSpace(friend.Id))
            .GroupBy(friend => friend.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
            ?? new Dictionary<string, VrcUser>();
        var trustLookup = friends
            .Where(friend => !string.IsNullOrWhiteSpace(friend.Id))
            .GroupBy(friend => friend.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var detectedAt = DateTimeOffset.UtcNow;

        foreach (var friend in friends)
        {
            var previous = previousLookup.TryGetValue(friend.Id, out var prev) ? prev : null;
            var location = WorldLocation(friend.Location);
            var previousLocation = WorldLocation(previous?.Location);
            var isOffline = IsOffline(friend);
            var wasOffline = IsOffline(previous);

            if (previous is not null)
            {
                // A missing bio means the payload did not carry one; only an
                // explicit string (including an empty one) is an observation.
                // This keeps an omitted field from looking like a cleared bio.
                if (friend.Bio is not null && previous.Bio is not null)
                {
                    var bio = friend.Bio.Trim();
                    var previousBio = previous.Bio.Trim();
                    if (!string.Equals(bio, previousBio, StringComparison.Ordinal))
                    {
                        entries.Add(new ActivityEntry
                        {
                            // Include both sides so changing back to the previous bio
                            // is a new event instead of a duplicate.
                            Id = $"bio-{friend.Id}-{ContentKey(previousBio)}-{ContentKey(bio)}",
                            Type = ActivityType.BioChanged,
                            UserId = friend.Id,
                            UserName = friend.Username,
                            DisplayName = friend.DisplayName,
                            AvatarUrl = AvatarThumbnailUrl(friend),
                            Location = location ?? string.Empty,
                            Timestamp = detectedAt,
                            IsOnline = !isOffline,
                            Details = bio.Length == 0
                                ? "cleared their bio"
                                : $"changed their bio · {Shorten(bio)}"
                        });
                    }
                }

                var avatarKey = AvatarKey(friend);
                var previousAvatarKey = AvatarKey(previous);
                if (!string.IsNullOrWhiteSpace(avatarKey) &&
                    !string.Equals(avatarKey, previousAvatarKey, StringComparison.Ordinal))
                {
                    entries.Add(new ActivityEntry
                    {
                        // A -> B -> A must produce two events. The old id only
                        // contained the destination avatar, so the return switch
                        // was silently swallowed.
                        Id = $"avatar-{friend.Id}-{ContentKey(previousAvatarKey)}-{ContentKey(avatarKey)}",
                        Type = ActivityType.AvatarChanged,
                        UserId = friend.Id,
                        UserName = friend.Username,
                        DisplayName = friend.DisplayName,
                        AvatarUrl = AvatarChangeThumbnailUrl(friend),
                        AvatarId = friend.CurrentAvatar ?? string.Empty,
                        AvatarName = friend.CurrentAvatarName ?? string.Empty,
                        Location = location ?? string.Empty,
                        Timestamp = detectedAt,
                        IsOnline = !isOffline,
                        Details = string.IsNullOrWhiteSpace(friend.CurrentAvatarName)
                            ? "changed avatar"
                            : $"changed avatar · {friend.CurrentAvatarName}"
                    });
                }
            }

            // A friend is online whenever VRChat does not report "offline".
            // "traveling" and "private" are still online states; they must not
            // be turned into an offline event or omitted from the Online filter.
            if (!isOffline && wasOffline)
            {
                entries.Add(new ActivityEntry
                {
                    // The baseline id stays stable so a relaunch does not add a
                    // second "came online" row for everyone already in a world.
                    Id = previous is null ? $"online-{friend.Id}" : EventId("online", friend.Id),
                    Type = ActivityType.FriendOnline,
                    UserId = friend.Id,
                    UserName = friend.Username,
                    DisplayName = friend.DisplayName,
                    AvatarUrl = AvatarThumbnailUrl(friend),
                    Location = location ?? string.Empty,
                    WorldId = LaunchService.GetWorldIdFromLocation(location) ?? string.Empty,
                    InstanceId = LaunchService.GetInstanceIdFromLocation(location) ?? string.Empty,
                    Timestamp = friend.LastActivity ?? detectedAt,
                    IsOnline = true,
                    Details = location is null ? "came online" : $"came online in {location}"
                });
            }
            else if (!isOffline && !wasOffline && location is not null &&
                     !string.Equals(previousLocation, location, StringComparison.Ordinal))
            {
                // Friend changed worlds. A transition from private/traveling to
                // a world also counts; a trip through "traveling" does not create
                // a false offline/online pair.
                entries.Add(new ActivityEntry
                {
                    Id = EventId("join", friend.Id),
                    Type = ActivityType.FriendJoinedWorld,
                    UserId = friend.Id,
                    UserName = friend.Username,
                    DisplayName = friend.DisplayName,
                    AvatarUrl = AvatarThumbnailUrl(friend),
                    Location = location,
                    WorldId = LaunchService.GetWorldIdFromLocation(location) ?? string.Empty,
                    InstanceId = LaunchService.GetInstanceIdFromLocation(location) ?? string.Empty,
                    Timestamp = friend.LastActivity ?? detectedAt,
                    IsOnline = true,
                    Details = $"joined {location}"
                });
            }

            // Friend went offline. This needs a previous online snapshot; an
            // initial offline friend has no transition to record.
            if (previous is not null && isOffline && !wasOffline)
            {
                entries.Add(new ActivityEntry
                {
                    Id = EventId("offline", friend.Id),
                    Type = ActivityType.FriendOffline,
                    UserId = friend.Id,
                    UserName = friend.Username,
                    DisplayName = friend.DisplayName,
                    AvatarUrl = AvatarThumbnailUrl(friend),
                    Timestamp = friend.LastActivity ?? detectedAt,
                    IsOnline = false,
                    Details = "went offline"
                });
            }
        }

        foreach (var entry in entries)
        {
            if (trustLookup.TryGetValue(entry.UserId, out var friend))
            {
                entry.Tags = friend.Tags;
                entry.DeveloperType = friend.DeveloperType;
            }
        }

        // Sort by timestamp descending (newest first)
        return entries.OrderByDescending(e => e.Timestamp).ToList();
    }

    /// <summary>
    /// Compares two complete friend lists. The activity feed is the single
    /// owner of the previous list, so this is also the change signal used by
    /// the friend-changes popup.
    /// </summary>
    public FriendListDelta DiffFriends(
        IReadOnlyList<VrcUser> current,
        IReadOnlyList<VrcUser> previous)
    {
        var currentIds = new HashSet<string>(current.Select(friend => friend.Id), StringComparer.Ordinal);
        var previousIds = new HashSet<string>(previous.Select(friend => friend.Id), StringComparer.Ordinal);
        return new FriendListDelta(
            current.Where(friend => !previousIds.Contains(friend.Id)).ToList(),
            previous.Where(friend => !currentIds.Contains(friend.Id)).ToList());
    }

    /// <summary>
    /// Returns the joinable world location for a friend, or null when VRChat
    /// reports a non-world value such as "offline", "traveling" or "private".
    /// </summary>
    private static string? WorldLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        return string.Equals(location, "offline", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(location, "traveling", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(location, "private", StringComparison.OrdinalIgnoreCase)
            ? null
            : location;
    }

    /// <summary>
    /// Offline is a real presence state, unlike "traveling" or "private", which
    /// still mean the friend is online.
    /// </summary>
    private static bool IsOffline(VrcUser? friend)
        => friend is null ||
           string.Equals(friend.Status, "offline", StringComparison.OrdinalIgnoreCase) ||
           string.IsNullOrWhiteSpace(friend.Location) ||
           string.Equals(friend.Location, "offline", StringComparison.OrdinalIgnoreCase);

    private static string EventId(string prefix, string userId)
        => $"{prefix}-{userId}-{Guid.NewGuid():N}";

    internal static string ContentKey(string? value)
    {
        var text = value ?? string.Empty;
        unchecked
        {
            var hash = 14695981039346656037UL;
            foreach (var character in text)
            {
                hash ^= character;
                hash *= 1099511628211UL;
            }

            return $"{text.Length}-{hash:X16}";
        }
    }

    private static string AvatarThumbnailUrl(VrcUser friend)
        => ImageUrlHelper.PickAvatarThumbnailUrl(
               friend.UserIcon,
               friend.ProfilePicOverrideThumbnail ?? friend.IconUrl,
               friend.CurrentAvatarThumbnailImageUrl,
               friend.CurrentAvatarImageUrl,
               128) ?? string.Empty;

    /// <summary>
    /// Avatar-change rows must show the avatar itself, not the user icon that
    /// normally represents a friend in the timeline. There is deliberately no user-icon
    /// fallback: presenting that icon as the avatar made a private or stale avatar look
    /// like the face the player was wearing.
    /// </summary>
    private static string AvatarChangeThumbnailUrl(VrcUser friend)
        => ImageUrlHelper.PickAvatarThumbnailUrl(
               friend.CurrentAvatarThumbnailImageUrl,
               friend.CurrentAvatarImageUrl,
               null,
               null,
               128) ?? string.Empty;

    private static string? AvatarKey(VrcUser friend)
    {
        if (!string.IsNullOrWhiteSpace(friend.CurrentAvatar))
        {
            return friend.CurrentAvatar;
        }

        if (!string.IsNullOrWhiteSpace(friend.CurrentAvatarThumbnailImageUrl))
        {
            return friend.CurrentAvatarThumbnailImageUrl;
        }

        return friend.CurrentAvatarImageUrl;
    }

    private static string Shorten(string text) =>
        text.Length > 80 ? text[..80].TrimEnd() + "…" : text;

    /// <summary>
    /// Filter activity entries by type.
    /// </summary>
    public List<ActivityEntry> FilterByType(List<ActivityEntry> entries, ActivityType? type)
    {
        if (type is null)
            return entries;
        return entries.Where(e => e.Type == type).ToList();
    }

    /// <summary>
    /// Search activity entries by display name, username, user id, details or
    /// world. User ids match with or without the "usr_" prefix, so an id copied
    /// from a profile keeps finding that person after they rename themselves.
    /// </summary>
    public List<ActivityEntry> Search(List<ActivityEntry> entries, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return entries;

        var q = query.Trim();
        return entries.Where(e =>
            e.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            e.UserName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            MatchesUserId(e.UserId, q) ||
            e.Details.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            e.WorldName.Contains(q, StringComparison.OrdinalIgnoreCase)
        ).ToList();
    }

    /// <summary>Matches a VRChat user id with or without its "usr_" prefix.</summary>
    public static bool MatchesUserId(string? userId, string? query)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(query))
            return false;

        var id = userId.Trim();
        var q = query.Trim();
        if (id.Contains(q, StringComparison.OrdinalIgnoreCase))
            return true;

        var bareId = WithoutUserIdPrefix(id);
        var bareQuery = WithoutUserIdPrefix(q);
        return bareQuery.Length >= 4 &&
               bareId.Contains(bareQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithoutUserIdPrefix(string value) =>
        value.StartsWith("usr_", StringComparison.OrdinalIgnoreCase) ? value[4..] : value;
}