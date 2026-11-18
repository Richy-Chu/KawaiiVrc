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
    public const string RoleEveryoneId = "grol_everyone";

    /// <summary>Members the group reports, and how many the roster endpoint lists.</summary>
    public int MemberTotal { get; set; } = 121;

    public int RosterSize { get; set; } = 2;

    public List<string> CreatedRoles { get; } = new();

    public List<string> UpdatedRoles { get; } = new();

    public List<string> DeletedRoles { get; } = new();

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

    /// <summary>PUT bodies for a member, prefixed with the user id.</summary>
    public List<string> MemberUpdates { get; } = new();

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
            if (method == HttpMethod.Post)
            {
                CreatedRoles.Add(body);
                return Json(new { id = "grol_new", name = Read(body, "name"), memberCount = 0 });
            }

            return Json(new[]
            {
                new { id = RoleAdminId, name = "Admin", description = "Runs the group", memberCount = 3, permissions = new[] { "*" } },
                new { id = RoleVipId, name = "VIP", description = "Friends of the owner", memberCount = 12, permissions = Array.Empty<string>() },
                new { id = RoleEveryoneId, name = "Everyone", description = "Every member", memberCount = 121, permissions = Array.Empty<string>() }
            });
        }

        if (path.Contains("/roles/", StringComparison.Ordinal))
        {
            var roleId = path[(path.LastIndexOf('/') + 1)..];
            if (method == HttpMethod.Delete)
            {
                DeletedRoles.Add(roleId);
            }
            else
            {
                UpdatedRoles.Add($"{roleId}|{body}");
            }

            return Empty();
        }

        if (path.EndsWith("/permissions", StringComparison.Ordinal))
        {
            return Json(new[]
            {
                "group-members-manage",
                "group-bans-manage",
                "group-roles-manage",
                "group-instance-join"
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
            var offset = ReadInt(request.RequestUri?.Query ?? string.Empty, "offset");
            var rows = new List<object>();
            for (var index = offset; index < RosterSize && rows.Count < 100; index++)
            {
                var userId = index switch
                {
                    0 => MemberUserId,
                    1 => OtherUserId,
                    _ => $"usr_roster_{index}"
                };
                var name = index switch
                {
                    0 => "Member Person",
                    1 => "Other Person",
                    _ => $"Roster Person {index}"
                };
                rows.Add(new
                {
                    id = $"gmem_{index}",
                    groupId = GroupId,
                    userId,
                    membershipStatus = "member",
                    joinedAt = new DateTimeOffset(2025, 3, 12, 10, 0, 0, TimeSpan.Zero),
                    visibility = "visible",
                    managerNotes = index == 0 ? "Regular" : string.Empty,
                    roleIds = index == 0 ? new[] { RoleVipId } : Array.Empty<string>(),
                    user = new
                    {
                        id = userId,
                        displayName = name,
                        currentAvatarThumbnailImageUrl = $"https://example.com/{userId}.png"
                    }
                });
            }

            return Json(rows);
        }

        if (path.Contains("/members/", StringComparison.Ordinal))
        {
            var userId = path[(path.LastIndexOf('/') + 1)..];
            if (method == HttpMethod.Delete)
            {
                Kicked.Add(userId);
            }
            else
            {
                MemberUpdates.Add($"{userId}|{body}");
            }

            return Empty();
        }

        // The group itself, with myMember and roles.
        return Json(new
        {
            id = GroupId,
            name = "Eden",
            memberCount = MemberTotal,
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

    private static int ReadInt(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == key && int.TryParse(parts[1], out var value))
            {
                return value;
            }
        }

        return 0;
    }

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
