using System.Text.Json;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

public enum VrChatRealtimeRouteAction
{
    None,
    UpdateFriend,
    AddFriend,
    RemoveFriend,
    RefreshFriends,
    RefreshNotifications,
    UpdateCurrentUserLocation
}

public sealed record VrChatRealtimeRouteResult(
    VrChatRealtimeRouteAction Action,
    string? UserId = null,
    VrcUser? Friend = null,
    string? Location = null);

/// <summary>
/// Pure projection of VRChat pipeline events onto the friend/notification
/// state. Keeping this independent of WPF makes the stale-presence rules
/// deterministic and testable.
/// </summary>
public static class VrChatRealtimeRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static VrChatRealtimeRouteResult Route(
        VrChatRealtimeEvent realtimeEvent,
        IReadOnlyDictionary<string, VrcUser> friends)
    {
        switch (realtimeEvent.Type)
        {
            case "friend-add":
                return RouteFriendAdd(realtimeEvent);
            case "friend-delete":
                return RemoveOrIgnore(realtimeEvent, friends);
            case "friend-online":
            case "friend-active":
            case "friend-offline":
            case "friend-location":
            case "friend-update":
                return RouteFriend(realtimeEvent, friends);
            case "notification":
            case "notification-v2":
            case "notification-v2-update":
            case "notification-v2-delete":
            case "see-notification":
            case "hide-notification":
            case "response-notification":
                return new VrChatRealtimeRouteResult(VrChatRealtimeRouteAction.RefreshNotifications);
            case "user-location":
                return RouteCurrentUserLocation(realtimeEvent);
            case "user-update":
                return new VrChatRealtimeRouteResult(VrChatRealtimeRouteAction.RefreshFriends);
            default:
                return new VrChatRealtimeRouteResult(VrChatRealtimeRouteAction.None);
        }
    }

    private static VrChatRealtimeRouteResult RouteCurrentUserLocation(
        VrChatRealtimeEvent realtimeEvent)
    {
        var content = realtimeEvent.Content;
        var location = ReadString(content, "location");
        if (string.IsNullOrWhiteSpace(location))
        {
            var embedded = ReadEmbeddedUser(content);
            location = embedded?.Location;
        }

        return new VrChatRealtimeRouteResult(
            VrChatRealtimeRouteAction.UpdateCurrentUserLocation,
            ReadString(content, "userId"),
            Location: location);
    }

    private static VrChatRealtimeRouteResult RouteFriendAdd(VrChatRealtimeEvent realtimeEvent)
    {
        var content = realtimeEvent.Content;
        var embedded = ReadEmbeddedUser(content);
        var userId = ReadString(content, "userId") ?? embedded?.Id;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new VrChatRealtimeRouteResult(VrChatRealtimeRouteAction.RefreshFriends);
        }

        var added = embedded ?? new VrcUser { Id = userId };
        added.Id = userId;
        added.IsFriend = true;
        if (string.IsNullOrWhiteSpace(added.DisplayName))
        {
            added.DisplayName = ReadString(content, "displayName") ?? userId;
        }

        return new VrChatRealtimeRouteResult(
            VrChatRealtimeRouteAction.AddFriend,
            userId,
            added);
    }

    private static VrChatRealtimeRouteResult RemoveOrIgnore(
        VrChatRealtimeEvent realtimeEvent,
        IReadOnlyDictionary<string, VrcUser> friends)
    {
        var userId = ReadString(realtimeEvent.Content, "userId");
        return !string.IsNullOrWhiteSpace(userId) && friends.ContainsKey(userId)
            ? new VrChatRealtimeRouteResult(VrChatRealtimeRouteAction.RemoveFriend, userId)
            : new VrChatRealtimeRouteResult(VrChatRealtimeRouteAction.None);
    }

    private static VrChatRealtimeRouteResult RouteFriend(
        VrChatRealtimeEvent realtimeEvent,
        IReadOnlyDictionary<string, VrcUser> friends)
    {
        var content = realtimeEvent.Content;
        var embedded = ReadEmbeddedUser(content);
        var userId = ReadString(content, "userId");
        if (string.IsNullOrWhiteSpace(userId) && embedded is not null)
        {
            userId = embedded.Id;
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            return new VrChatRealtimeRouteResult(VrChatRealtimeRouteAction.None);
        }

        if (!friends.TryGetValue(userId, out var current) || current is null)
        {
            // An offline event for an unknown user changes nothing. Any other
            // presence event is proof of a friend the local page had missed;
            // apply the embedded user immediately instead of waiting for a
            // multi-page full refresh. This is what makes a just-came-online
            // alt appear without delay.
            if (realtimeEvent.Type == "friend-offline")
            {
                return new VrChatRealtimeRouteResult(VrChatRealtimeRouteAction.None);
            }

            current = new VrcUser { Id = userId, DisplayName = userId, IsFriend = true };
        }

        var updated = Clone(current);
        var topLevelLocation = ReadString(content, "location");
        var topLevelStatus = ReadString(content, "status");
        var topLevelState = ReadString(content, "state");
        var includeEmbeddedPresence =
            realtimeEvent.Type == "friend-update" ||
            (realtimeEvent.Type == "friend-online" && string.IsNullOrWhiteSpace(topLevelLocation));

        if (embedded is not null)
        {
            MergeProfile(updated, embedded);
            // An explicitly present empty bio is a clear. MergeProfile treats
            // empty as "omitted", so handle the raw property here.
            if (content.TryGetProperty("user", out var embeddedElement) &&
                embeddedElement.ValueKind == JsonValueKind.Object &&
                embeddedElement.TryGetProperty("bio", out var embeddedBio) &&
                embeddedBio.ValueKind == JsonValueKind.String)
            {
                updated.Bio = embeddedBio.GetString() ?? string.Empty;
            }

            if (includeEmbeddedPresence)
            {
                MergePresence(updated, embedded);
            }
        }

        // Most friend-update events nest the User object, but accept flat
        // profile fields too. VRChat has changed pipeline payload shapes before,
        // and a missing avatar field here silently hides the Activity row.
        MergeTopLevelProfile(updated, content);

        // Top-level presence is authoritative. Embedded user records in
        // friend-location/friend-online are often a stale snapshot (including
        // an old "offline" state) and must not overwrite the event itself.
        if (!string.IsNullOrWhiteSpace(topLevelLocation))
        {
            updated.Location = topLevelLocation;
        }
        if (!string.IsNullOrWhiteSpace(topLevelStatus))
        {
            updated.Status = topLevelStatus;
        }
        if (!string.IsNullOrWhiteSpace(topLevelState))
        {
            updated.State = topLevelState;
        }

        var platform = ReadString(content, "platform");
        if (!string.IsNullOrWhiteSpace(platform))
        {
            updated.Platform = platform;
            updated.LastPlatform = platform;
        }

        switch (realtimeEvent.Type)
        {
            case "friend-offline":
                updated.Location = "offline";
                updated.Status = "offline";
                updated.State = "offline";
                break;
            case "friend-active":
                updated.State = "active";
                break;
            case "friend-online":
                updated.State = "online";
                if (string.Equals(updated.Status, "offline", StringComparison.OrdinalIgnoreCase))
                {
                    updated.Status = "active";
                }
                break;
            case "friend-location" when string.Equals(
                topLevelLocation, "traveling", StringComparison.OrdinalIgnoreCase):
                // The destination is not a joinable location yet; keep the
                // presence as Traveling, not the stale embedded world.
                updated.Location = "traveling";
                updated.State = "online";
                break;
            case "friend-location":
                updated.State = string.Equals(updated.Location, "offline", StringComparison.OrdinalIgnoreCase)
                    ? "offline"
                    : "online";
                if (updated.State == "online" &&
                    string.Equals(updated.Status, "offline", StringComparison.OrdinalIgnoreCase))
                {
                    updated.Status = "active";
                }
                break;
        }

        updated.IsFriend = true;
        return new VrChatRealtimeRouteResult(
            VrChatRealtimeRouteAction.UpdateFriend,
            userId,
            updated);
    }

    private static VrcUser? ReadEmbeddedUser(JsonElement content)
    {
        if (!content.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VrcUser>(user.GetRawText(), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void MergeProfile(VrcUser target, VrcUser source)
    {
        if (!string.IsNullOrWhiteSpace(source.Id)) target.Id = source.Id;
        if (!string.IsNullOrWhiteSpace(source.DisplayName)) target.DisplayName = source.DisplayName;
        if (!string.IsNullOrWhiteSpace(source.Username)) target.Username = source.Username;
        if (!string.IsNullOrWhiteSpace(source.Bio)) target.Bio = source.Bio;
        if (!string.IsNullOrWhiteSpace(source.UserIcon)) target.UserIcon = source.UserIcon;
        if (!string.IsNullOrWhiteSpace(source.IconUrl)) target.IconUrl = source.IconUrl;
        if (!string.IsNullOrWhiteSpace(source.ProfilePicOverride)) target.ProfilePicOverride = source.ProfilePicOverride;
        if (!string.IsNullOrWhiteSpace(source.ProfilePicOverrideThumbnail))
            target.ProfilePicOverrideThumbnail = source.ProfilePicOverrideThumbnail;
        if (!string.IsNullOrWhiteSpace(source.CurrentAvatar))
            target.CurrentAvatar = source.CurrentAvatar;
        if (!string.IsNullOrWhiteSpace(source.CurrentAvatarImageUrl))
            target.CurrentAvatarImageUrl = source.CurrentAvatarImageUrl;
        if (!string.IsNullOrWhiteSpace(source.CurrentAvatarThumbnailImageUrl))
            target.CurrentAvatarThumbnailImageUrl = source.CurrentAvatarThumbnailImageUrl;
        if (!string.IsNullOrWhiteSpace(source.BannerUrl)) target.BannerUrl = source.BannerUrl;
        if (!string.IsNullOrWhiteSpace(source.BannerType)) target.BannerType = source.BannerType;
        if (!string.IsNullOrWhiteSpace(source.BannerColor)) target.BannerColor = source.BannerColor;
        if (!string.IsNullOrWhiteSpace(source.DateJoined)) target.DateJoined = source.DateJoined;
        if (!string.IsNullOrWhiteSpace(source.Pronouns)) target.Pronouns = source.Pronouns;
        if (source.BioLinks.Count > 0) target.BioLinks = new List<string>(source.BioLinks);
        if (source.CurrentAvatarTags.Count > 0)
            target.CurrentAvatarTags = new List<string>(source.CurrentAvatarTags);
        if (source.Tags.Count > 0) target.Tags = new List<string>(source.Tags);
    }

    private static void MergeTopLevelProfile(VrcUser target, JsonElement content)
    {
        var currentAvatar = ReadString(content, "currentAvatar");
        var avatarImage = ReadString(content, "currentAvatarImageUrl");
        var avatarThumbnail = ReadString(content, "currentAvatarThumbnailImageUrl");
        if (!string.IsNullOrWhiteSpace(currentAvatar)) target.CurrentAvatar = currentAvatar;
        if (!string.IsNullOrWhiteSpace(avatarImage)) target.CurrentAvatarImageUrl = avatarImage;
        if (!string.IsNullOrWhiteSpace(avatarThumbnail))
            target.CurrentAvatarThumbnailImageUrl = avatarThumbnail;

        var userIcon = ReadString(content, "userIcon");
        var iconUrl = ReadString(content, "iconUrl");
        var profileImage = ReadString(content, "profilePicOverride");
        var profileThumbnail = ReadString(content, "profilePicOverrideThumbnail");
        var bannerUrl = ReadString(content, "bannerUrl");
        var bannerType = ReadString(content, "bannerType");
        var statusDescription = ReadString(content, "statusDescription");
        var displayName = ReadString(content, "displayName");
        if (!string.IsNullOrWhiteSpace(userIcon)) target.UserIcon = userIcon;
        if (!string.IsNullOrWhiteSpace(iconUrl)) target.IconUrl = iconUrl;
        if (!string.IsNullOrWhiteSpace(profileImage)) target.ProfilePicOverride = profileImage;
        if (!string.IsNullOrWhiteSpace(profileThumbnail))
            target.ProfilePicOverrideThumbnail = profileThumbnail;
        if (!string.IsNullOrWhiteSpace(bannerUrl)) target.BannerUrl = bannerUrl;
        if (!string.IsNullOrWhiteSpace(bannerType)) target.BannerType = bannerType;
        if (content.TryGetProperty("bio", out var bioElement) &&
            bioElement.ValueKind == JsonValueKind.String)
        {
            // Present-but-empty means the bio was cleared, not omitted.
            target.Bio = bioElement.GetString() ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(statusDescription))
            target.StatusDescription = statusDescription;
        if (!string.IsNullOrWhiteSpace(displayName)) target.DisplayName = displayName;
    }

    private static void MergePresence(VrcUser target, VrcUser source)
    {
        if (!string.IsNullOrWhiteSpace(source.Status)) target.Status = source.Status;
        if (!string.IsNullOrWhiteSpace(source.StatusDescription))
            target.StatusDescription = source.StatusDescription;
        if (!string.IsNullOrWhiteSpace(source.Location)) target.Location = source.Location;
        if (!string.IsNullOrWhiteSpace(source.State)) target.State = source.State;
        if (!string.IsNullOrWhiteSpace(source.Platform)) target.Platform = source.Platform;
        if (!string.IsNullOrWhiteSpace(source.LastPlatform)) target.LastPlatform = source.LastPlatform;
        if (source.LastActivity is not null) target.LastActivity = source.LastActivity;
        if (source.LastLogin is not null) target.LastLogin = source.LastLogin;
    }

    private static VrcUser Clone(VrcUser user)
        => JsonSerializer.Deserialize<VrcUser>(
               JsonSerializer.Serialize(user, JsonOptions),
               JsonOptions) ?? new VrcUser();

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
