using System;
using System.Collections.Generic;

namespace VrcCompanion.Models;

/// <summary>
/// The read-only context that other local tools can consume. It contains only
/// values observed from VRChat or the local Continuity database.
/// </summary>
public sealed class ContextSnapshot
{
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    public string AccountUserId { get; set; } = string.Empty;

    public string AccountDisplayName { get; set; } = string.Empty;

    public string AccountAvatarUrl { get; set; } = string.Empty;

    public string StatusText { get; set; } = string.Empty;

    public string TrustText { get; set; } = string.Empty;

    public string WorldId { get; set; } = string.Empty;

    public string WorldName { get; set; } = string.Empty;

    public string WorldAuthor { get; set; } = string.Empty;

    public string WorldImageUrl { get; set; } = string.Empty;

    public string Location { get; set; } = string.Empty;

    public string AccessText { get; set; } = string.Empty;

    public string RegionText { get; set; } = string.Empty;

    public int PlayerCount { get; set; }

    public List<ContextSnapshotPerson> Players { get; set; } = new();

    public List<ContextSnapshotPerson> RecentPeople { get; set; } = new();

    public List<ContextSnapshotSession> TodaySessions { get; set; } = new();
}

public sealed class ContextSnapshotPerson
{
    public string UserId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string TrustText { get; set; } = string.Empty;

    public bool IsFriend { get; set; }

    /// <summary>True when the signed-in account has blocked this user.</summary>
    public bool IsBlocked { get; set; }

    /// <summary>True for the signed-in account; self is never a friend row.</summary>
    public bool IsSelf { get; set; }

    public string AvatarUrl { get; set; } = string.Empty;

    public DateTimeOffset? LastSeenAt { get; set; }

    public int SessionCount { get; set; }
}

public sealed class ContextSnapshotSession
{
    public string WorldName { get; set; } = string.Empty;

    public string Location { get; set; } = string.Empty;

    public DateTimeOffset JoinedAt { get; set; }

    public long DurationSeconds { get; set; }

    public int PlayerCount { get; set; }
}
