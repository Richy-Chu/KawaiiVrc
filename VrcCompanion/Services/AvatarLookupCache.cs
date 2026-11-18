using System.Collections.Concurrent;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// The avatar-search results already loaded in this session. VRChat frequently
/// hides a user's current avatar id while still exposing its picture, and some
/// community indexes answer a text search but not a file-id lookup. Keeping the
/// search rows here lets a profile resolve that avatar by its picture or exact
/// name instead of reporting that no public avatar exists.
/// </summary>
public static class AvatarLookupCache
{
    private static readonly ConcurrentDictionary<string, Avatar> ByFileId =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Avatar> ByUrl =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Avatar> ByName =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void Clear()
    {
        ByFileId.Clear();
        ByUrl.Clear();
        ByName.Clear();
    }

    public static void Remember(IEnumerable<Avatar>? avatars)
    {
        if (avatars is null)
        {
            return;
        }

        foreach (var avatar in avatars)
        {
            Remember(avatar);
        }
    }

    public static void Remember(Avatar? avatar)
    {
        if (avatar is null ||
            string.IsNullOrWhiteSpace(avatar.Id) ||
            avatar.IsPrivate ||
            avatar.IsUnavailable)
        {
            return;
        }

        foreach (var url in new[] { avatar.ImageUrl, avatar.ThumbnailImageUrl })
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            ByUrl[url.Trim()] = avatar;
            var fileId = ImageUrlHelper.ExtractFileId(url);
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                ByFileId[fileId] = avatar;
            }
        }

        var name = avatar.Name.Trim();
        if (name.Length > 0)
        {
            ByName[name] = avatar;
        }
    }

    public static Avatar? FindByImage(params string?[] urls)
    {
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (ByUrl.TryGetValue(url.Trim(), out var direct))
            {
                return direct;
            }

            var fileId = ImageUrlHelper.ExtractFileId(url);
            if (!string.IsNullOrWhiteSpace(fileId) &&
                ByFileId.TryGetValue(fileId, out var avatar))
            {
                return avatar;
            }
        }

        return null;
    }

    public static Avatar? FindByName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? null
            : ByName.TryGetValue(trimmed, out var avatar)
                ? avatar
                : null;
    }
}
