using System.Text.RegularExpressions;

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
