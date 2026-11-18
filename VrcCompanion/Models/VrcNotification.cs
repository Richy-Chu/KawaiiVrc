using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>
/// A VRChat notification from either the legacy /auth/user/notifications
/// feed or the newer /notifications (NotificationV2) feed. Boops are only
/// delivered through NotificationV2.
/// </summary>
public sealed class VrcNotification
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("details")]
    public JsonElement Details { get; set; }

    /// <summary>NotificationV2's per-type payload; boops may put the emoji here.</summary>
    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [JsonPropertyName("seen")]
    public bool Seen { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAtLegacy { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonIgnore]
    public DateTimeOffset? Timestamp => CreatedAt ?? CreatedAtLegacy;

    [JsonPropertyName("senderUserId")]
    public string? SenderUserId { get; set; }

    [JsonPropertyName("senderUsername")]
    public string? SenderUsername { get; set; }

    /// <summary>NotificationV2's preferred display name for the sender.</summary>
    [JsonPropertyName("senderDisplayName")]
    public string? SenderDisplayName { get; set; }

    [JsonIgnore]
    public string? SenderName => string.IsNullOrWhiteSpace(SenderDisplayName)
        ? SenderUsername
        : SenderDisplayName;

    [JsonPropertyName("displayCategory")]
    public string? DisplayCategory { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>True when this record came from the NotificationV2 feed.</summary>
    [JsonIgnore]
    public bool IsV2 { get; set; }

    /// <summary>The built-in emoji constant or custom emoji file id attached to a boop.</summary>
    [JsonIgnore]
    public string? BoopEmojiId => ReadPayloadString("emojiId");

    /// <summary>The inventory emoji item attached to a boop.</summary>
    [JsonIgnore]
    public string? BoopInventoryItemId => ReadPayloadString("inventoryItemId");

    [JsonIgnore]
    public bool HasBoopAttachment =>
        !string.IsNullOrWhiteSpace(BoopEmojiId) ||
        !string.IsNullOrWhiteSpace(BoopInventoryItemId);

    /// <summary>Resolved display name; persisted so relaunches keep the caption.</summary>
    [JsonPropertyName("boopName")]
    public string? BoopName { get; set; }

    /// <summary>Resolved image URL; persisted so relaunches keep the artwork.</summary>
    [JsonPropertyName("boopImageUrl")]
    public string? BoopImageUrl { get; set; }

    /// <summary>emoji, custom, or inventory.</summary>
    [JsonPropertyName("boopKind")]
    public string? BoopKind { get; set; }

    /// <summary>
    /// A refresh returns the raw V2 record again. Keep the resolved attachment
    /// from the existing row so its artwork does not disappear.
    /// </summary>
    public void CopyResolvedBoopAttachmentFrom(VrcNotification source)
    {
        BoopName ??= source.BoopName;
        BoopImageUrl ??= source.BoopImageUrl;
        BoopKind ??= source.BoopKind;
    }

    /// <summary>Group notifications carry their group in NotificationV2 data.</summary>
    [JsonIgnore]
    public bool IsGroupNotification =>
        Type.StartsWith("group.", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Category, "social.group", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(DisplayCategory, "groups", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(GroupId);

    /// <summary>The canonical grp_ id used to open the right group.</summary>
    [JsonIgnore]
    public string? GroupId
    {
        get
        {
            var id = ReadPayloadString("groupId");
            id ??= ExtractGroupId(Link);
            if (string.IsNullOrWhiteSpace(id) &&
                SenderUserId?.StartsWith("grp_", StringComparison.OrdinalIgnoreCase) == true)
            {
                id = SenderUserId;
            }

            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }
    }

    [JsonIgnore]
    public string? GroupName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ResolvedGroupName))
            {
                return ResolvedGroupName;
            }

            var name = ReadPayloadString("groupName");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            var title = Title?.Trim();
            var colon = title?.IndexOf(':') ?? -1;
            return colon > 0 ? title![..colon].Trim() : null;
        }
    }

    [JsonIgnore]
    public string? GroupImageUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ResolvedGroupIconUrl))
            {
                return ResolvedGroupIconUrl;
            }

            return string.IsNullOrWhiteSpace(ImageUrl) ? null : ImageUrl;
        }
    }

    [JsonIgnore]
    public string? GroupTitle
    {
        get
        {
            var announcement = ReadPayloadString("announcementTitle");
            if (!string.IsNullOrWhiteSpace(announcement))
            {
                return announcement;
            }

            var title = Title?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var colon = title.IndexOf(':');
            if (colon > 0)
            {
                var after = title[(colon + 1)..].Trim();
                return after.Length == 0 ? null : after;
            }

            return title;
        }
    }

    [JsonPropertyName("resolvedGroupName")]
    public string? ResolvedGroupName { get; set; }

    [JsonPropertyName("resolvedGroupIconUrl")]
    public string? ResolvedGroupIconUrl { get; set; }

    public void CopyResolvedGroupMetadataFrom(VrcNotification source)
    {
        ResolvedGroupName ??= source.ResolvedGroupName;
        ResolvedGroupIconUrl ??= source.ResolvedGroupIconUrl;
    }

    private string? ReadPayloadString(string key)
    {
        var value = ReadPayloadString(Details, key);
        if (value is not null)
        {
            return value;
        }

        return Data is { } data
            ? ReadPayloadString(data, key)
            : null;
    }

    private static string? ExtractGroupId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var start = value.IndexOf("grp_", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var end = start + 4;
        while (end < value.Length &&
               (char.IsLetterOrDigit(value[end]) || value[end] == '-'))
        {
            end++;
        }

        return end > start + 4 ? value[start..end] : null;
    }

    private static string? ReadPayloadString(JsonElement container, string key)
    {
        if (container.ValueKind == JsonValueKind.Undefined ||
            container.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (container.ValueKind == JsonValueKind.String)
        {
            var raw = container.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(raw);
                return ReadPayloadString(document.RootElement, key);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty(key, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
