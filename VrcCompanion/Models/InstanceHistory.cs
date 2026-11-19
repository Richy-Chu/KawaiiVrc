using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>
/// One real visit to a VRChat world, recorded locally from the signed-in
/// account's VRChat log. Player entries hold every visit they made during the
/// instance so the detail view can show joins, leaves and total time.
/// </summary>
public sealed class InstanceHistoryEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("accountUserId")]
    public string AccountUserId { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("worldId")]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = string.Empty;

    [JsonPropertyName("worldName")]
    public string WorldName { get; set; } = string.Empty;

    [JsonPropertyName("worldThumbnailUrl")]
    public string WorldThumbnailUrl { get; set; } = string.Empty;

    [JsonPropertyName("worldAuthorName")]
    public string WorldAuthorName { get; set; } = string.Empty;

    [JsonPropertyName("ownerId")]
    public string OwnerId { get; set; } = string.Empty;

    [JsonPropertyName("ownerName")]
    public string OwnerName { get; set; } = string.Empty;

    [JsonPropertyName("joinedAt")]
    public DateTimeOffset JoinedAt { get; set; }

    [JsonPropertyName("leftAt")]
    public DateTimeOffset? LeftAt { get; set; }

    [JsonPropertyName("lastSeenAt")]
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// Timestamp of the last observed join/leave event. Unlike LastSeenAt this
    /// never advances just because a row is still open, so a stale session is
    /// not stretched to the current time when it is closed.
    /// </summary>
    [JsonPropertyName("lastEventAt")]
    public DateTimeOffset LastEventAt { get; set; }

    [JsonPropertyName("players")]
    public List<InstanceHistoryPlayer> Players { get; set; } = new();

    [JsonIgnore]
    public bool IsOpen => LeftAt is null;

    /// <summary>The SQLite event rows this session was rebuilt from.</summary>
    [JsonIgnore]
    internal List<long> EventIds { get; set; } = new();

    /// <summary>
    /// True for a row built from a picture rather than a recorded session: the
    /// capture time is known, the visit's duration is not, so nothing may claim
    /// a join/leave time or a length.
    /// </summary>
    [JsonIgnore]
    public bool IsPhotoSnapshot { get; internal set; }

    /// <summary>
    /// Stored session ids represented by this row. A view-level merge of
    /// repeated visits to the exact same instance keeps every source id so a
    /// delete removes all of them.
    /// </summary>
    [JsonIgnore]
    internal List<string> SourceEntryIds { get; set; } = new();

    [JsonIgnore]
    public TimeSpan Duration => (LeftAt ?? LastSeenAt) - JoinedAt;

    public InstanceHistoryPlayer GetOrAddPlayer(string userId, string displayName, string avatarUrl = "")
    {
        var key = !string.IsNullOrWhiteSpace(userId) ? userId : "name:" + displayName.Trim();
        var player = Players.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));

        // The log can emit a name-only join before it learns the usr_ id. When
        // the id arrives, keep the visit history and upgrade that same row
        // instead of showing the player twice.
        if (player is null && !string.IsNullOrWhiteSpace(userId))
        {
            player = Players.FirstOrDefault(candidate =>
                string.IsNullOrWhiteSpace(candidate.UserId) &&
                string.Equals(candidate.DisplayName, displayName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (player is not null)
            {
                player.UserId = userId;
            }
        }

        if (player is null)
        {
            player = new InstanceHistoryPlayer
            {
                UserId = userId ?? string.Empty,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Unknown player" : displayName.Trim(),
                AvatarUrl = avatarUrl ?? string.Empty
            };
            Players.Add(player);
        }
        else if (!string.IsNullOrWhiteSpace(displayName))
        {
            player.DisplayName = displayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(avatarUrl) && string.IsNullOrWhiteSpace(player.AvatarUrl))
        {
            player.AvatarUrl = avatarUrl;
        }

        return player;
    }

    public void Close(DateTimeOffset at)
    {
        var closedAt = at < JoinedAt ? JoinedAt : at;
        LeftAt = closedAt;
        LastSeenAt = closedAt < LastSeenAt ? LastSeenAt : closedAt;
        foreach (var player in Players)
        {
            var visit = player.Visits.LastOrDefault();
            if (visit is not null && visit.LeftAt is null)
            {
                visit.LeftAt = closedAt < visit.JoinedAt ? visit.JoinedAt : closedAt;
            }
        }
    }
}

public sealed class InstanceHistoryPlayer
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("avatarUrl")]
    public string AvatarUrl { get; set; } = string.Empty;

    [JsonPropertyName("visits")]
    public List<InstanceHistoryVisit> Visits { get; set; } = new();

    [JsonIgnore]
    public string Key => !string.IsNullOrWhiteSpace(UserId)
        ? UserId
        : "name:" + DisplayName.Trim();

    [JsonIgnore]
    public DateTimeOffset FirstJoinedAt => Visits.Count == 0 ? DateTimeOffset.MinValue : Visits[0].JoinedAt;

    [JsonIgnore]
    public DateTimeOffset? LastLeftAt
    {
        get
        {
            for (var i = Visits.Count - 1; i >= 0; i--)
            {
                if (Visits[i].LeftAt is { } left)
                {
                    return left;
                }
            }
            return null;
        }
    }

    [JsonIgnore]
    public bool HasOpenVisit => Visits.Any(visit => visit.LeftAt is null);

    [JsonIgnore]
    public TimeSpan TotalTime
    {
        get
        {
            var total = TimeSpan.Zero;
            foreach (var visit in Visits)
            {
                var end = visit.LeftAt ?? visit.LastSeenAt;
                if (end > visit.JoinedAt)
                {
                    total += end - visit.JoinedAt;
                }
            }
            return total;
        }
    }

    public InstanceHistoryVisit StartVisit(DateTimeOffset at)
    {
        var visit = new InstanceHistoryVisit
        {
            JoinedAt = at,
            LastSeenAt = at
        };
        Visits.Add(visit);
        return visit;
    }
}

public sealed class InstanceHistoryVisit
{
    [JsonPropertyName("joinedAt")]
    public DateTimeOffset JoinedAt { get; set; }

    [JsonPropertyName("leftAt")]
    public DateTimeOffset? LeftAt { get; set; }

    [JsonPropertyName("lastSeenAt")]
    public DateTimeOffset LastSeenAt { get; set; }
}
