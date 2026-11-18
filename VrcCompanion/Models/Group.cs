using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using VrcCompanion.Services;

namespace VrcCompanion.Models;

/// <summary>
/// Represents a VRChat group with roles and members.
/// </summary>
public sealed class Group
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Set when the group arrived from a membership list, where `id` is
    /// the membership (gmem_…) and the group itself is `groupId` (grp_…).</summary>
    [JsonPropertyName("groupId")]
    public string GroupId { get; set; } = string.Empty;

    [JsonIgnore]
    public string EffectiveId => string.IsNullOrWhiteSpace(GroupId) ? Id : GroupId;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string ImageUrl { get; set; } = string.Empty;

    [JsonPropertyName("iconUrl")]
    public string IconUrl { get; set; } = string.Empty;

    [JsonPropertyName("ownerId")]
    public string OwnerId { get; set; } = string.Empty;

    [JsonPropertyName("memberCount")]
    public int MemberCount { get; set; }

    /// <summary>open, request, invite or closed — only open groups can be joined directly.</summary>
    [JsonPropertyName("joinState")]
    public string JoinState { get; set; } = string.Empty;

    /// <summary>member, requested, invited, banned, inactive or userblocked.</summary>
    [JsonPropertyName("membershipStatus")]
    public string MembershipStatus { get; set; } = string.Empty;

    [JsonPropertyName("myMember")]
    public GroupMember? MyMember { get; set; }

    [JsonPropertyName("roles")]
    public List<GroupRole> Roles { get; set; } = new();

    [JsonPropertyName("members")]
    public List<GroupMember> Members { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public bool IsMember => string.Equals(MembershipStatus, "member", StringComparison.OrdinalIgnoreCase)
        || (MyMember is not null && string.Equals(MyMember.MembershipStatus, "member", StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public string MembershipLabel => MembershipStatus?.ToLowerInvariant() switch
    {
        "member" => "Joined",
        "requested" => "Request pending",
        "invited" => "Invited",
        "banned" => "Banned",
        "userblocked" => "Blocked",
        _ => string.Empty
    };

    /// <summary>Resized group icon URL, loaded through the shared image cache.</summary>
    [JsonIgnore]
    public string ThumbnailUrl
        => ImageUrlHelper.ToThumbnailUrl(
               string.IsNullOrWhiteSpace(IconUrl) ? ImageUrl : IconUrl,
               128) ?? string.Empty;
}

/// <summary>
/// Represents a role within a VRChat group.
/// </summary>
public sealed class GroupRole
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = new();

    [JsonPropertyName("memberCount")]
    public int MemberCount { get; set; }

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#FFFFFF";

    [JsonPropertyName("isAddedOnJoin")]
    public bool IsAddedOnJoin { get; set; }

    [JsonPropertyName("isSelfAssignable")]
    public bool IsSelfAssignable { get; set; }

    [JsonPropertyName("requiresTwoFactor")]
    public bool RequiresTwoFactor { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }
}

/// <summary>
/// Represents a member of a VRChat group.
/// </summary>
public sealed class GroupMember
{
    /// <summary>
    /// VRChat only sends the member's name, avatar and id inside this nested object on the
    /// members endpoints, which is why reading userId alone gave a roster of blank rows.
    /// </summary>
    [JsonPropertyName("user")]
    public GroupMemberUser? User { get; set; }

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("roleId")]
    public string RoleId { get; set; } = string.Empty;

    [JsonPropertyName("joinedAt")]
    public DateTimeOffset JoinedAt { get; set; }

    [JsonPropertyName("avatarUrl")]
    public string AvatarUrl { get; set; } = string.Empty;

    [JsonPropertyName("membershipStatus")]
    public string MembershipStatus { get; set; } = string.Empty;

    /// <summary>The account's permissions when this is the group's myMember object.</summary>
    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = new();

    /// <summary>Roles attached to the member, used to assign and remove roles.</summary>
    [JsonPropertyName("roleIds")]
    public List<string> RoleIds { get; set; } = new();

    [JsonPropertyName("requestCreatedAt")]
    public DateTimeOffset RequestCreatedAt { get; set; }

    public string EffectiveUserId => !string.IsNullOrWhiteSpace(UserId)
        ? UserId
        : User?.Id ?? string.Empty;

    public string EffectiveUserName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(User?.DisplayName))
            {
                return User!.DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(UserName))
            {
                return UserName;
            }

            if (!string.IsNullOrWhiteSpace(User?.Username))
            {
                return User!.Username;
            }

            return DisplayName ?? string.Empty;
        }
    }

    public string EffectiveAvatarUrl
    {
        get
        {
            foreach (var url in new[]
                     {
                         User?.CurrentAvatarThumbnailImageUrl,
                         User?.ThumbnailUrl,
                         User?.IconUrl,
                         AvatarUrl
                     })
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url!;
                }
            }

            return string.Empty;
        }
    }

    /// <summary>The limited user object VRChat nests inside a group member.</summary>
    public sealed class GroupMemberUser
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("iconUrl")]
        public string IconUrl { get; set; } = string.Empty;

        [JsonPropertyName("thumbnailUrl")]
        public string ThumbnailUrl { get; set; } = string.Empty;

        [JsonPropertyName("currentAvatarThumbnailImageUrl")]
        public string CurrentAvatarThumbnailImageUrl { get; set; } = string.Empty;
    }

    /// <summary>Search Group Members wraps its array in a results object.</summary>
    public sealed class SearchResponse
    {
        [JsonPropertyName("results")]
        public List<GroupMember> Results { get; set; } = new();
    }
}

/// <summary>
/// A banned account, a join request or a sent invite. VRChat reuses the member shape for
/// all three, so one model covers the moderation lists.
/// </summary>
public sealed class GroupRosterEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("groupId")]
    public string GroupId { get; set; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public GroupMember.GroupMemberUser? User { get; set; }

    [JsonPropertyName("membershipStatus")]
    public string MembershipStatus { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("requestCreatedAt")]
    public DateTimeOffset RequestCreatedAt { get; set; }

    [JsonPropertyName("actorDisplayName")]
    public string ActorDisplayName { get; set; } = string.Empty;

    public string EffectiveUserId => !string.IsNullOrWhiteSpace(UserId)
        ? UserId
        : User?.Id ?? string.Empty;

    public string EffectiveUserName
    {
        get
        {
            foreach (var name in new[] { User?.DisplayName, User?.Username })
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name!;
                }
            }

            return string.Empty;
        }
    }

    public string EffectiveAvatarUrl
    {
        get
        {
            foreach (var url in new[] { User?.CurrentAvatarThumbnailImageUrl, User?.ThumbnailUrl, User?.IconUrl })
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url!;
                }
            }

            return string.Empty;
        }
    }
}

/// <summary>One entry of a group's audit log.</summary>
public sealed class GroupAuditEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("actorId")]
    public string ActorId { get; set; } = string.Empty;

    [JsonPropertyName("actorDisplayName")]
    public string ActorDisplayName { get; set; } = string.Empty;

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = string.Empty;

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Audit logs wrap their array in a results object, unlike most group endpoints.</summary>
    public sealed class AuditLogResponse
    {
        [JsonPropertyName("results")]
        public List<GroupAuditEntry> Results { get; set; } = new();
    }
}