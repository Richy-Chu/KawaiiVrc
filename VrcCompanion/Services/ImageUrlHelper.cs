using System.Text.RegularExpressions;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Builds VRChat avatar / world thumbnail URLs the way VRCX does.
///
/// VRChat serves two kinds of image URLs:
///   * /api/1/file/file_<id>/<version>/file          -- raw file (large)
///   * /api/1/image/file_<id>/<version>/<resolution> -- resized thumbnail
///
/// The thumbnail endpoint is what should be used for small UI chips. VRCX also
/// prefers userIcon &gt; profilePicOverride &gt; currentAvatarThumbnailImageUrl.
/// </summary>
public static class ImageUrlHelper
{
    private static readonly Regex FileUrlRegex = new(
        @"^/api/1/file/(?<fileId>[^/]+)/(?<version>[^/]+)/file/?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // avtr.icu (and providers that reuse it) serve VRChat files through a
    // proxy. That proxy's backing R2 bucket has expired many older files, so
    // the same file is fetched from VRChat's own image/file endpoint instead.
    private static readonly Regex AvtrIcuProxyRegex = new(
        @"^https?://(?:www\.)?avtr\.icu/proxy/(?<fileId>file_[^/]+)/(?<version>[^/]+)/(?<size>[^/?#]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Extracts the VRChat file_… id from a full or thumbnail image URL.</summary>
    public static string? ExtractFileId(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        var start = imageUrl.IndexOf("file_", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var end = start;
        while (end < imageUrl.Length &&
               (char.IsLetterOrDigit(imageUrl[end]) || imageUrl[end] == '_' || imageUrl[end] == '-'))
        {
            end++;
        }

        return end > start ? imageUrl[start..end] : null;
    }

    /// <summary>
    /// Picks the best avatar URL following VRCX's preference order:
    /// userIcon → profilePicOverrideThumbnail → currentAvatarThumbnailImageUrl → currentAvatarImageUrl.
    /// </summary>
    public static string? PickAvatarUrl(
        string? userIcon,
        string? profilePicOverrideThumbnail,
        string? currentAvatarThumbnailImageUrl,
        string? currentAvatarImageUrl)
    {
        if (!string.IsNullOrWhiteSpace(userIcon))
        {
            return userIcon;
        }

        if (!string.IsNullOrWhiteSpace(profilePicOverrideThumbnail))
        {
            return profilePicOverrideThumbnail;
        }

        if (!string.IsNullOrWhiteSpace(currentAvatarThumbnailImageUrl))
        {
            return currentAvatarThumbnailImageUrl;
        }

        return currentAvatarImageUrl;
    }

    /// <summary>
    /// Rewrites an avtr.icu proxy image URL to VRChat's own file/image endpoint.
    /// All other URLs are returned unchanged.
    /// </summary>
    public static string? NormalizeCommunityImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = AvtrIcuProxyRegex.Match(url);
        if (match.Success)
        {
            var size = match.Groups["size"].Value;
            var kind = string.Equals(size, "file", StringComparison.OrdinalIgnoreCase)
                ? "file"
                : "image";
            return $"https://api.vrchat.cloud/api/1/{kind}/{match.Groups["fileId"].Value}/{match.Groups["version"].Value}/{size}";
        }

        // Some indexes wrap VRChat's image in a proxy query, e.g.
        // https://paw-api.amelia.fun/proxy?url=https://api.vrchat.cloud/...
        // Fetch the VRChat URL directly so a stale third-party proxy cannot
        // leave the card blank.
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            parsed.Query.Length > 0)
        {
            foreach (var part in parsed.Query.TrimStart('?').Split('&'))
            {
                if (!part.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string inner;
                try
                {
                    inner = Uri.UnescapeDataString(part[4..].Replace("+", " "));
                }
                catch (UriFormatException)
                {
                    continue;
                }

                if (inner.StartsWith("https://api.vrchat.cloud/", StringComparison.OrdinalIgnoreCase))
                {
                    return inner;
                }
            }
        }

        return url;
    }

    /// <summary>
    /// Converts a raw /api/1/file/.../file URL into a resized /api/1/image/.../<resolution> thumbnail URL.
    /// URLs that are already on the /image/ path or that aren't VRChat file URLs are returned unchanged.
    /// </summary>
    public static string? ToThumbnailUrl(string? url, int resolution = 256)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (url.Contains("/image/", StringComparison.OrdinalIgnoreCase))
        {
            // Already an image URL; just normalise the trailing resolution if present.
            return url;
        }

        var match = FileUrlRegex.Match(new Uri(url, UriKind.RelativeOrAbsolute).AbsolutePath);
        if (match.Success)
        {
            var fileId = match.Groups["fileId"].Value;
            var version = match.Groups["version"].Value;
            return $"/api/1/image/{fileId}/{version}/{resolution}";
        }

        return url;
    }

    /// <summary>
    /// Picks the best avatar URL and converts it to a thumbnail of the given resolution.
    /// </summary>
    public static string? PickAvatarThumbnailUrl(
        string? userIcon,
        string? profilePicOverrideThumbnail,
        string? currentAvatarThumbnailImageUrl,
        string? currentAvatarImageUrl,
        int resolution = 256)
    {
        var url = PickAvatarUrl(
            userIcon,
            profilePicOverrideThumbnail,
            currentAvatarThumbnailImageUrl,
            currentAvatarImageUrl);
        return ToThumbnailUrl(url, resolution);
    }

    /// <summary>
    /// The round picture an Activity row draws for a person: their profile icon first,
    /// then their profile picture, then the avatar they are wearing. This is the icon the
    /// Activity page shows on every row - not the avatar a switch line happens to name.
    /// </summary>
    public static string? PersonThumbnailUrl(VrcUser? user, int resolution = 128)
        => user is null
            ? null
            : PickAvatarThumbnailUrl(
                user.UserIcon,
                user.ProfilePicOverrideThumbnail ?? user.IconUrl,
                user.CurrentAvatarThumbnailImageUrl,
                user.CurrentAvatarImageUrl,
                resolution);

    /// <summary>
    /// True when an avatar's author does not contradict the author VRChat's log named for a
    /// switch. A different author means a different avatar that only shares the name, so a
    /// lookup must not present it as the avatar this person switched to.
    /// </summary>
    public static bool AuthorFits(string? avatarAuthor, string? loggedAuthor)
    {
        var logged = NormalizeAuthor(loggedAuthor);
        var avatar = NormalizeAuthor(avatarAuthor);
        if (logged.Length == 0 || avatar.Length == 0)
        {
            // Nothing to compare: the log named no author, or this record has none.
            return true;
        }

        // VRChat and the community indexes word one creator differently ("Jamuu" and
        // "_Jamuu"), so containment counts as the same author; a different name does not.
        return string.Equals(avatar, logged, StringComparison.OrdinalIgnoreCase) ||
               avatar.Contains(logged, StringComparison.OrdinalIgnoreCase) ||
               logged.Contains(avatar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAuthor(string? author)
        => (author ?? string.Empty).Trim().TrimStart('@').Trim('_', ' ').Trim();

    /// <summary>
    /// Picks the best world thumbnail URL: thumbnailImageUrl → imageUrl, converted to thumbnail form.
    /// </summary>
    public static string? PickWorldThumbnailUrl(string? thumbnailImageUrl, string? imageUrl, int resolution = 256)
    {
        var url = !string.IsNullOrWhiteSpace(thumbnailImageUrl)
            ? thumbnailImageUrl
            : imageUrl;
        return ToThumbnailUrl(url, resolution);
    }
}
