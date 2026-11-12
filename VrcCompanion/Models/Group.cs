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

    [JsonPropertyName("roles")]
    public List<GroupRole> Roles { get; set; } = new();

    [JsonPropertyName("members")]
    public List<GroupMember> Members { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

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
}

/// <summary>
/// Represents a member of a VRChat group.
/// </summary>
public sealed class GroupMember
{
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
}