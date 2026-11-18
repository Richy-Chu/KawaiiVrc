using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VrcCompanion.Tests;

/// <summary>
/// Stands in for VRChat's group moderation endpoints: the group with myMember permissions,
/// roles, members, bans, join requests, invites and the audit log, recording every action.
/// </summary>
internal sealed class GroupModerationTestHandler : HttpMessageHandler
{
    public const string GroupId = "grp_1";
    public const string MemberUserId = "usr_member";
    public const string OtherUserId = "usr_other";
    public const string BannedUserId = "usr_banned";
    public const string RequestUserId = "usr_request";
    public const string InviteUserId = "usr_invited";
    public const string RoleAdminId = "grol_admin";
    public const string RoleVipId = "grol_vip";

    /// <summary>Permissions reported in the group's myMember object.</summary>
    public List<string> Permissions { get; } = new()
    {
        "group-members-remove",
        "group-bans-manage",
        "group-roles-assign",
        "group-members-manage",
        "group-invites-manage",
        "group-audit-view"
    };

    /// <summary>Roles attached to the signed-in account; their permissions count too.</summary>
    public List<string> MemberRoleIds { get; } = new() { RoleAdminId };

    public List<string> PostedBans { get; } = new();

    public List<string> Unbanned { get; } = new();

    public List<string> Kicked { get; } = new();

    public List<string> RolesAdded { get; } = new();

    public List<string> RolesRemoved { get; } = new();

    public List<string> Responses { get; } = new();

    public List<string> InvitesSent { get; } = new();

    public List<string> InvitesDeleted { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var method = request.Method;
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        if (path.Contains("/members/", StringComparison.Ordinal) &&
            path.Contains("/roles/", StringComparison.Ordinal))
        {
            var roles = path[(path.IndexOf("/roles/", StringComparison.Ordinal) + "/roles/".Length)..];
            if (method == HttpMethod.Put)
            {
                RolesAdded.Add(roles);
            }
            else
            {
                RolesRemoved.Add(roles);
            }

            return Empty();
        }

        if (path.EndsWith("/roles", StringComparison.Ordinal))
        {
            return Json(new[]
            {
                new { id = RoleAdminId, name = "Admin", description = "Runs the group", memberCount = 3, permissions = new[] { "*" } },
                new { id = RoleVipId, name = "VIP", description = "Friends of the owner", memberCount = 12, permissions = Array.Empty<string>() }
            });
        }

        if (path.EndsWith("/bans", StringComparison.Ordinal))
        {
            if (method == HttpMethod.Post)
            {
                PostedBans.Add(Read(body, "userId"));
                return Empty();
            }

            return Json(new[] { Entry(BannedUserId, "Banned Person", "spam") });
        }

        if (path.Contains("/bans/", StringComparison.Ordinal))
        {
            Unbanned.Add(path[(path.LastIndexOf('/') + 1)..]);
            return Empty();
        }

        if (path.EndsWith("/requests", StringComparison.Ordinal))
        {
            return Json(new[] { Entry(RequestUserId, "Waiting Person", string.Empty) });
        }

        if (path.Contains("/requests/", StringComparison.Ordinal))
        {
            Responses.Add($"{path[(path.LastIndexOf('/') + 1)..]}:{Read(body, "action")}:{body.Contains("\"block\":true")}");
            return Empty();
        }

        if (path.EndsWith("/invites", StringComparison.Ordinal))
        {
            if (method == HttpMethod.Post)
            {
                InvitesSent.Add(Read(body, "userId"));
                return Empty();
            }

            return Json(new[] { Entry(InviteUserId, "Invited Person", string.Empty) });
        }

        if (path.Contains("/invites/", StringComparison.Ordinal))
        {
            InvitesDeleted.Add(path[(path.LastIndexOf('/') + 1)..]);
            return Empty();
        }

        if (path.EndsWith("/auditLogs", StringComparison.Ordinal))
        {
            return Json(new
            {
                results = new[]
                {
                    new
                    {
                        id = "gaud_1",
                        created_at = "2026-01-02T03:04:05Z",
                        actorDisplayName = "Mod Person",
                        eventType = "group.member.ban",
                        description = "Group member banned"
                    }
                }
            });
        }

        if (path.EndsWith("/members", StringComparison.Ordinal))
        {
            return Json(new[]
            {
                new
                {
                    id = "gmem_1",
                    groupId = GroupId,
                    userId = MemberUserId,
                    membershipStatus = "member",
                    roleIds = new[] { RoleVipId },
                    user = new
                    {
                        id = MemberUserId,
                        displayName = "Member Person",
                        currentAvatarThumbnailImageUrl = "https://example.com/member.png"
                    }
                },
                new
                {
                    id = "gmem_2",
                    groupId = GroupId,
                    userId = OtherUserId,
                    membershipStatus = "member",
                    roleIds = Array.Empty<string>(),
                    user = new
                    {
                        id = OtherUserId,
                        displayName = "Other Person",
                        currentAvatarThumbnailImageUrl = "https://example.com/other.png"
                    }
                }
            });
        }

        if (path.Contains("/members/", StringComparison.Ordinal))
        {
            Kicked.Add(path[(path.LastIndexOf('/') + 1)..]);
            return Empty();
        }

        // The group itself, with myMember and roles.
        return Json(new
        {
            id = GroupId,
            name = "Eden",
            memberCount = 121,
            myMember = new
            {
                id = "gmem_me",
                userId = "usr_me",
                membershipStatus = "member",
                permissions = Permissions,
                roleIds = MemberRoleIds
            },
            roles = new[]
            {
                new { id = RoleAdminId, name = "Admin", memberCount = 3, permissions = new[] { "*" } },
                new { id = RoleVipId, name = "VIP", memberCount = 12, permissions = Array.Empty<string>() }
            }
        });
    }

    private static object Entry(string userId, string displayName, string reason) => new
    {
        id = $"gmem_{userId}",
        groupId = GroupId,
        userId,
        reason,
        createdAt = "2026-01-01T00:00:00Z",
        requestCreatedAt = "2026-01-01T00:00:00Z",
        user = new
        {
            id = userId,
            displayName,
            currentAvatarThumbnailImageUrl = $"https://example.com/{userId}.png"
        }
    };

    private static string Read(string body, string property)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static HttpResponseMessage Empty()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage Json(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
}
