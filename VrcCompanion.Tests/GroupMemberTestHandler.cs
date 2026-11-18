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
/// Stands in for VRChat's group member endpoints: paged lists, display-name search wrapped in
/// a results object, and the single-member lookup by usr_ id. Members carry the nested user
/// object the real API sends, which is where names and avatars live.
/// </summary>
internal sealed class GroupMemberTestHandler : HttpMessageHandler
{
    private readonly int _total;

    public GroupMemberTestHandler(int total) => _total = total;

    public List<int> Offsets { get; } = new();

    public List<string> Searches { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var query = request.RequestUri?.Query ?? string.Empty;
        if (path.EndsWith("/members/search", StringComparison.Ordinal))
        {
            var text = ReadString(query, "query");
            Searches.Add(text);
            var matches = Enumerable.Range(0, _total)
                .Where(index => MemberName(index).Contains(text, StringComparison.OrdinalIgnoreCase))
                .Take(100)
                .Select(Member)
                .ToArray();
            return Json(new { results = matches });
        }

        if (path.Contains("/members/", StringComparison.Ordinal))
        {
            var id = path[(path.LastIndexOf('/') + 1)..];
            var index = Enumerable.Range(0, _total)
                .FirstOrDefault(candidate => string.Equals($"usr_{candidate}", id, StringComparison.Ordinal));
            if (index == 0 && !string.Equals(id, "usr_0", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"Not Found\"}}", Encoding.UTF8, "application/json")
                });
            }

            return Json(Member(index));
        }

        if (path.EndsWith("/members", StringComparison.Ordinal))
        {
            var offset = ReadInt(query, "offset");
            var count = ReadInt(query, "n", fallback: 100);
            Offsets.Add(offset);
            var available = Math.Max(0, Math.Min(count, _total - offset));
            return Json(Enumerable.Range(offset, available).Select(Member).ToArray());
        }

        return Json(Array.Empty<object>());
    }

    public static string MemberName(int index) => $"Member {index:D4}";

    public static object Member(int index) => new
    {
        id = $"gmem_{index}",
        groupId = "grp_1",
        userId = $"usr_{index}",
        membershipStatus = "member",
        joinedAt = "2024-01-01T00:00:00Z",
        user = new
        {
            id = $"usr_{index}",
            displayName = MemberName(index),
            username = $"member{index}",
            iconUrl = $"https://example.com/icon-{index}.png",
            currentAvatarThumbnailImageUrl = $"https://example.com/thumb-{index}.png"
        }
    };

    private static Task<HttpResponseMessage> Json(object payload)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        });

    private static int ReadInt(string query, string key, int fallback = 0)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == key && int.TryParse(parts[1], out var value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static string ReadString(string query, string key)
        => Uri.UnescapeDataString(query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .FirstOrDefault(parts => parts.Length == 2 && parts[0] == key)?[1] ?? string.Empty);
}
