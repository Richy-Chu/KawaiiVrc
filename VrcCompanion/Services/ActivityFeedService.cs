using System;
using System.Collections.Generic;
using System.Linq;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

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

        var previousLookup = previousFriends?.ToDictionary(f => f.Id, StringComparer.Ordinal) ?? new Dictionary<string, VrcUser>();

        foreach (var friend in friends)
        {
            var previous = previousLookup.TryGetValue(friend.Id, out var prev) ? prev : null;
            var location = WorldLocation(friend.Location);
            var previousLocation = WorldLocation(previous?.Location);

            if (previous is not null)
            {
                var bio = (friend.Bio ?? string.Empty).Trim();
                var previousBio = (previous.Bio ?? string.Empty).Trim();
                if (!string.Equals(bio, previousBio, StringComparison.Ordinal))
                {
                    entries.Add(new ActivityEntry
                    {
                        Id = $"bio-{friend.Id}",
                        Type = ActivityType.BioChanged,
                        UserId = friend.Id,
                        UserName = friend.Username,
                        DisplayName = friend.DisplayName,
                        AvatarUrl = AvatarThumbnailUrl(friend),
                        Timestamp = DateTimeOffset.UtcNow,
                        IsOnline = true,
                        Details = bio.Length == 0
                            ? "cleared their bio"
                            : $"changed their bio · {Shorten(bio)}"
                    });
                }

                if (!string.IsNullOrWhiteSpace(friend.CurrentAvatar) &&
                    !string.Equals(friend.CurrentAvatar, previous.CurrentAvatar, StringComparison.Ordinal))
                {
                    entries.Add(new ActivityEntry
                    {
                        Id = $"avatar-{friend.Id}",
                        Type = ActivityType.AvatarChanged,
                        UserId = friend.Id,
                        UserName = friend.Username,
                        DisplayName = friend.DisplayName,
                        AvatarUrl = AvatarThumbnailUrl(friend),
                        AvatarId = friend.CurrentAvatar!,
                        Timestamp = DateTimeOffset.UtcNow,
                        IsOnline = true,
                        Details = "changed avatar"
                    });
                }
            }

            // Friend came online
            if (previousLocation is null)
            {
                if (location is not null &&
                    string.Equals(friend.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(new ActivityEntry
                    {
                        Id = $"online-{friend.Id}",
                        Type = ActivityType.FriendOnline,
                        UserId = friend.Id,
                        UserName = friend.Username,
                        DisplayName = friend.DisplayName,
                        AvatarUrl = AvatarThumbnailUrl(friend),
                        Location = location,
                        WorldId = LaunchService.GetWorldIdFromLocation(location) ?? string.Empty,
                        InstanceId = LaunchService.GetInstanceIdFromLocation(location) ?? string.Empty,
                        Timestamp = friend.LastActivity ?? DateTimeOffset.UtcNow,
                        IsOnline = true,
                        Details = $"came online in {location}"
                    });
                }
            }
            else
            {
                // Friend changed worlds
                if (location is not null && !string.Equals(previousLocation, location, StringComparison.Ordinal))
                {
                    entries.Add(new ActivityEntry
                    {
                        Id = $"join-{friend.Id}",
                        Type = ActivityType.FriendJoinedWorld,
                        UserId = friend.Id,
                        UserName = friend.Username,
                        DisplayName = friend.DisplayName,
                        AvatarUrl = AvatarThumbnailUrl(friend),
                        Location = location,
                        WorldId = LaunchService.GetWorldIdFromLocation(location) ?? string.Empty,
                        InstanceId = LaunchService.GetInstanceIdFromLocation(location) ?? string.Empty,
                        Timestamp = friend.LastActivity ?? DateTimeOffset.UtcNow,
                        IsOnline = true,
                        Details = $"joined {location}"
                    });
                }

                // Friend went offline
                if (location is null)
                {
                    entries.Add(new ActivityEntry
                    {
                        Id = $"offline-{friend.Id}",
                        Type = ActivityType.FriendOffline,
                        UserId = friend.Id,
                        UserName = friend.Username,
                        DisplayName = friend.DisplayName,
                        AvatarUrl = AvatarThumbnailUrl(friend),
                        Timestamp = friend.LastActivity ?? DateTimeOffset.UtcNow,
                        IsOnline = false,
                        Details = "went offline"
                    });
                }
            }
        }

        if (previousFriends is not null)
        {
            var currentIds = new HashSet<string>(friends.Select(friend => friend.Id), StringComparer.Ordinal);
            var previousIds = new HashSet<string>(previousFriends.Select(friend => friend.Id), StringComparer.Ordinal);
            foreach (var friend in friends.Where(friend => !previousIds.Contains(friend.Id)))
            {
                entries.Add(new ActivityEntry
                {
                    Id = $"added-{friend.Id}",
                    Type = ActivityType.FriendAdded,
                    UserId = friend.Id,
                    UserName = friend.Username,
                    DisplayName = friend.DisplayName,
                    AvatarUrl = AvatarThumbnailUrl(friend),
                    Timestamp = DateTimeOffset.UtcNow,
                    IsOnline = true,
                    Details = "friended you"
                });
            }

            foreach (var friend in previousFriends.Where(friend => !currentIds.Contains(friend.Id)))
            {
                entries.Add(new ActivityEntry
                {
                    Id = $"removed-{friend.Id}",
                    Type = ActivityType.FriendRemoved,
                    UserId = friend.Id,
                    UserName = friend.Username,
                    DisplayName = friend.DisplayName,
                    AvatarUrl = AvatarThumbnailUrl(friend),
                    Timestamp = DateTimeOffset.UtcNow,
                    IsOnline = false,
                    Details = "unfriended you"
                });
            }
        }

        // Sort by timestamp descending (newest first)
        return entries.OrderByDescending(e => e.Timestamp).ToList();
    }

    /// <summary>
    /// Returns the world location for a friend, or null when VRChat reports a
    /// non-world value such as "offline" or "traveling".
    /// </summary>
    private static string? WorldLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        return string.Equals(location, "offline", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(location, "traveling", StringComparison.OrdinalIgnoreCase)
            ? null
            : location;
    }

    private static string AvatarThumbnailUrl(VrcUser friend)
        => ImageUrlHelper.PickAvatarThumbnailUrl(
               null,
               null,
               friend.CurrentAvatarThumbnailImageUrl,
               friend.CurrentAvatarImageUrl,
               128) ?? string.Empty;

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
    /// Search activity entries by user name or details.
    /// </summary>
    public List<ActivityEntry> Search(List<ActivityEntry> entries, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return entries;

        var q = query.Trim().ToLowerInvariant();
        return entries.Where(e =>
            e.DisplayName.ToLowerInvariant().Contains(q) ||
            e.UserName.ToLowerInvariant().Contains(q) ||
            e.Details.ToLowerInvariant().Contains(q) ||
            e.WorldName.ToLowerInvariant().Contains(q)
        ).ToList();
    }
}