using System;
using System.Collections.Generic;

namespace VrcCompanion.Models;

/// <summary>
/// One real visit to a VRChat world, normalised from a local history source.
/// The source key is retained so repeated imports update the same visit instead
/// of creating duplicate rows.
/// </summary>
public sealed class ContinuitySession
{
    public string Id { get; set; } = string.Empty;

    public string AccountUserId { get; set; } = string.Empty;

    /// <summary>vrcx-0, vrcx, vrcnext, or vrc-companion.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Stable key inside the source database.</summary>
    public string SourceKey { get; set; } = string.Empty;

    public string Location { get; set; } = string.Empty;

    public string WorldId { get; set; } = string.Empty;

    public string WorldName { get; set; } = string.Empty;

    public string WorldThumbnailUrl { get; set; } = string.Empty;

    public string WorldAuthorName { get; set; } = string.Empty;

    public DateTimeOffset JoinedAt { get; set; }

    public DateTimeOffset? LeftAt { get; set; }

    public long DurationSeconds { get; set; }

    public int PlayerCount { get; set; }

    public List<ContinuitySessionPerson> People { get; set; } = new();

    public static string MakeId(string source, string sourceKey)
    {
        var input = string.Concat(source, "|", sourceKey);
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }
}

/// <summary>One person's observed presence inside a session.</summary>
public sealed class ContinuitySessionPerson
{
    public string PersonKey { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string AvatarUrl { get; set; } = string.Empty;

    public DateTimeOffset JoinedAt { get; set; }

    public DateTimeOffset? LeftAt { get; set; }

    public long DurationSeconds { get; set; }
}

/// <summary>A person known from history, friends, notes, or VRCX profile caches.</summary>
public sealed class ContinuityPerson
{
    public string PersonKey { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string AvatarUrl { get; set; } = string.Empty;

    public string Bio { get; set; } = string.Empty;

    public string LocalNote { get; set; } = string.Empty;

    public string ImportedNote { get; set; } = string.Empty;

    public string TrustLevel { get; set; } = string.Empty;

    public bool IsFriend { get; set; }

    public string Source { get; set; } = string.Empty;

    public DateTimeOffset? FirstSeenAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public long TotalSeconds { get; set; }

    public int SessionCount { get; set; }
}

/// <summary>World metadata used to resolve history rows without inventing names.</summary>
public sealed class ContinuityWorld
{
    public string WorldId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string AuthorName { get; set; } = string.Empty;

    public string ThumbnailUrl { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public DateTimeOffset? FirstSeenAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public long TotalSeconds { get; set; }

    public int VisitCount { get; set; }
}

/// <summary>Summary counts read from the local Continuity database.</summary>
public sealed class ContinuitySummary
{
    public int SessionCount { get; set; }

    public int PeopleCount { get; set; }

    public int WorldCount { get; set; }

    public long TotalSeconds { get; set; }

    public DateTimeOffset? FirstSeenAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
}

/// <summary>One import attempt/source state shown in the Sources tab.</summary>
public sealed class ContinuityImportSource
{
    public string Source { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool IsAvailable { get; set; }

    public DateTimeOffset? ImportedAt { get; set; }

    public int Sessions { get; set; }

    public int People { get; set; }

    public int Worlds { get; set; }

    public string Message { get; set; } = string.Empty;
}

/// <summary>Result returned by a source importer.</summary>
public sealed class ContinuityImportResult
{
    public string Source { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool IsAvailable { get; set; }

    public int Sessions { get; set; }

    public int People { get; set; }

    public int Worlds { get; set; }

    public string Message { get; set; } = string.Empty;

    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;

    public Exception? Error { get; set; }
}
