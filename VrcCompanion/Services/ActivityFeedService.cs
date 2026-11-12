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