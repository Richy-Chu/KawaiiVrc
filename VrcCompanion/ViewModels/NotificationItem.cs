using System.Globalization;
using System.Text.Json;
using System.Windows.Media;
using VrcCompanion.Models;

namespace VrcCompanion.ViewModels;

public sealed class NotificationItem : ViewModelBase
{
    private static readonly Brush FriendBrush = MakeBrush("#3DDC97");
    private static readonly Brush InviteBrush = MakeBrush("#4EA8FF");
    private static readonly Brush BoopBrush = MakeBrush("#FF7AB6");
    private static readonly Brush GroupBrush = MakeBrush("#A78BFA");
    private static readonly Brush DefaultBrush = MakeBrush("#8B95AA");
    private static readonly Brush UnreadRowBrush = MakeBrush("#171E2D");
    private static readonly Brush ReadRowBrush = MakeBrush("#00000000");

    private VrcNotification _notification;

    public NotificationItem(VrcNotification notification)
    {
        _notification = notification;
    }

    public string Id => _notification.Id;

    public string Type => _notification.Type;

    public string SenderUserId => _notification.SenderUserId ?? string.Empty;

    public string SenderName => string.IsNullOrWhiteSpace(_notification.SenderName)
        ? "VRChat"
        : _notification.SenderName!;

    public string SenderUsername => _notification.SenderUsername ?? string.Empty;

    public string SenderHandle => string.IsNullOrWhiteSpace(_notification.SenderUsername)
        ? string.Empty
        : $"@{_notification.SenderUsername}";

    public string SenderInitial
    {
        get
        {
            var name = SenderName.Trim();
            if (name.Length == 0)
            {
                return "?";
            }

            return new StringInfo(name).SubstringByTextElements(0, 1).ToUpperInvariant();
        }
    }

    public DateTimeOffset? CreatedAt => _notification.Timestamp;

    public bool IsV2 => _notification.IsV2;

    public string ReceivedText => _notification.Timestamp is null
        ? string.Empty
        : _notification.Timestamp.Value.ToLocalTime().ToString("MMM d, h:mm tt");

    public string Category => IsGroup
        ? "group"
        : _notification.Type.ToLowerInvariant() switch
        {
            "friendrequest" => "request",
            "invite" => "invite",
            _ => "other"
        };

    public string TypeGlyph => _notification.Type.ToLowerInvariant() switch
    {
        "friendrequest" => "\uE8FA",
        "invite" => "\uE8AB",
        "inviteresponse" or "requestinviteresponse" => "\uE8F2",
        "requestinvite" => "\uE8F4",
        "boop" => "\uE945",
        "message" => "\uE8BD",
        var type when type.Contains("group", StringComparison.Ordinal) => "\uE902",
        "votetokick" => "\uE7BA",
        _ => "\uEA8F"
    };

    public bool IsFriendRequest => string.Equals(Type, "friendRequest", StringComparison.OrdinalIgnoreCase);

    public bool IsBoop => string.Equals(Type, "boop", StringComparison.OrdinalIgnoreCase);

    public bool IsGroup => _notification.IsGroupNotification;

    /// <summary>A pending invitation to join a VRChat group.</summary>
    public bool IsGroupInvite => string.Equals(Type, "group.invite", StringComparison.OrdinalIgnoreCase);

    public string GroupId => _notification.GroupId ?? string.Empty;

    public bool HasGroupId => !string.IsNullOrWhiteSpace(GroupId);

    public string GroupName => string.IsNullOrWhiteSpace(_notification.GroupName)
        ? "Group"
        : _notification.GroupName!;

    public string GroupTitle => _notification.GroupTitle ?? string.Empty;

    public bool HasGroupTitle => IsGroup &&
        !string.IsNullOrWhiteSpace(GroupTitle) &&
        !string.Equals(GroupTitle, MessageText, StringComparison.CurrentCultureIgnoreCase);

    public string GroupImageUrl => _notification.GroupImageUrl ?? string.Empty;

    public bool HasGroupImage => IsGroup && !string.IsNullOrWhiteSpace(GroupImageUrl);

    public bool IsInvite => string.Equals(Type, "invite", StringComparison.OrdinalIgnoreCase);

    /// <summary>Someone is asking you to invite them to your current instance.</summary>
    public bool IsInviteRequest => string.Equals(Type, "requestInvite", StringComparison.OrdinalIgnoreCase);

    /// <summary>Invites, invite requests, group invites, and boops stay in the feed after they are read.</summary>
    public bool IsPersistent => IsInvite || IsInviteRequest || IsGroupInvite || IsBoop;

    /// <summary>Only a group invitation with a canonical group id can be joined or declined.</summary>
    public bool CanAcceptGroupInvite => IsGroupInvite && HasGroupId;

    public bool CanDeclineGroupInvite => IsGroupInvite && HasGroupId;

    public bool HasBoopAttachment => IsBoop &&
        (_notification.HasBoopAttachment ||
         !string.IsNullOrWhiteSpace(_notification.BoopName) ||
         !string.IsNullOrWhiteSpace(_notification.BoopImageUrl));

    public bool HasBoopImage => IsBoop && !string.IsNullOrWhiteSpace(BoopImageUrl);

    public string BoopImageUrl => IsBoop
        ? _notification.BoopImageUrl ?? _notification.ImageUrl ?? string.Empty
        : string.Empty;

    public string BoopAttachmentTitle => string.IsNullOrWhiteSpace(_notification.BoopName)
        ? "Boop emoji"
        : _notification.BoopName!;

    public string BoopKindText
    {
        get
        {
            if (!IsBoop || !HasBoopAttachment)
            {
                return string.Empty;
            }

            var kind = (_notification.BoopKind ?? string.Empty).ToLowerInvariant() switch
            {
                "custom" => "Custom emoji",
                "inventory" => "Inventory emoji",
                "emoji" => "Default emoji",
                _ => "Emoji"
            };

            return string.IsNullOrWhiteSpace(_notification.BoopName) ||
                   IsGenericBoopName(_notification.BoopName)
                ? kind
                : $"{kind} · {_notification.BoopName}";
        }
    }

    public string? InviteLocation
    {
        get
        {
            if (!IsInvite)
            {
                return null;
            }

            var location = GetDetailString("location");
            if (!string.IsNullOrWhiteSpace(location) &&
                location.StartsWith("wrld_", StringComparison.Ordinal) &&
                location.Contains(':'))
            {
                return location;
            }

            var worldId = GetDetailString("worldId");
            var instanceId = GetDetailString("instanceId");
            if (!string.IsNullOrWhiteSpace(worldId) &&
                worldId.StartsWith("wrld_", StringComparison.Ordinal))
            {
                // The REST notification API returns worldId as a complete
                // location: "wrld_...:instanceId". Some websocket payloads
                // split it into worldId + instanceId instead.
                if (worldId.Contains(':'))
                {
                    return worldId;
                }

                if (!string.IsNullOrWhiteSpace(instanceId))
                {
                    return $"{worldId}:{instanceId}";
                }
            }

            return null;
        }
    }

    public bool CanAcceptFriendRequest => IsFriendRequest && !string.IsNullOrWhiteSpace(SenderUserId);

    /// <summary>Someone asked you to invite them to your current instance.</summary>
    public bool CanAcceptInviteRequest => IsInviteRequest && !string.IsNullOrWhiteSpace(SenderUserId);

    /// <summary>World invites and invite requests can be declined or cleared.</summary>
    public bool CanDecline => IsInvite || IsInviteRequest;

    private bool _isPastReplyWindow;

    /// <summary>
    /// VRChat accepts a reply only for the newest 40 notifications. Invites are kept here
    /// between launches, so a row can still be listed long after VRChat stopped answering
    /// it; the list marks that row before "Decline with message" is pressed.
    /// </summary>
    public bool IsPastReplyWindow => _isPastReplyWindow;

    public string ReplyWindowClosedHint =>
        "VRChat only accepts replies to its newest 40 notifications, so this invite can no " +
        "longer take a message. Decline still removes it.";

    /// <summary>Marks (or clears) the reply-window warning; only world invites and requests can carry it.</summary>
    public void MarkPastReplyWindow(bool pastWindow)
    {
        var value = pastWindow && CanDecline;
        if (_isPastReplyWindow == value)
        {
            return;
        }

        _isPastReplyWindow = value;
        OnPropertyChanged(nameof(IsPastReplyWindow));
    }

    /// <summary>The raw notification is what gets persisted between launches.</summary>
    public VrcNotification Snapshot => _notification;

    public string InviteWorldName => IsInvite
        ? GetDetailString("worldName") ?? string.Empty
        : string.Empty;

    public bool CanJoinInvite => InviteLocation is not null;

    public string ActionHint => IsFriendRequest
        ? "Friend request"
        : IsInvite
            ? "World invite"
            : IsGroupInvite
                ? "Group invite"
                : string.Empty;

    public bool Seen => _notification.Seen;

    public bool IsUnseen => !Seen;

    public string TypeLabel => IsGroupInvite
        ? "INVITE"
        : IsGroup
            ? "GROUP"
            : _notification.Type.ToLowerInvariant() switch
    {
        "friendrequest" => "FRIEND",
        "invite" => "INVITE",
        "inviteresponse" => "REPLY",
        "requestinvite" => "REQUEST",
        "requestinviteresponse" => "REPLY",
        "votetokick" => "VOTE",
        "boop" => "BOOP",
        "message" => "MESSAGE",
        var type when type.Contains("group", StringComparison.Ordinal) => "GROUP",
        var type when string.IsNullOrWhiteSpace(type) => "NOTICE",
        var type => type.ToUpperInvariant()
    };

    public Brush TypeBrush => IsGroup
        ? GroupBrush
        : _notification.Type.ToLowerInvariant() switch
    {
        "friendrequest" => FriendBrush,
        "invite" or "inviteresponse" or "requestinvite" or "requestinviteresponse" => InviteBrush,
        "boop" => BoopBrush,
        var type when type.Contains("group", StringComparison.Ordinal) => GroupBrush,
        _ => DefaultBrush
    };

    public string MessageText
    {
        get
        {
            if (IsBoop)
            {
                if (!HasBoopAttachment)
                {
                    return "Booped you.";
                }

                if (!string.IsNullOrWhiteSpace(_notification.BoopName) &&
                    !IsGenericBoopName(_notification.BoopName))
                {
                    return $"Booped you with {_notification.BoopName}.";
                }

                return (_notification.BoopKind ?? string.Empty).ToLowerInvariant() switch
                {
                    "custom" => "Booped you with a custom emoji.",
                    "inventory" => "Booped you with an inventory emoji.",
                    _ => "Booped you with an emoji."
                };
            }

            if (!string.IsNullOrWhiteSpace(_notification.Message))
            {
                return _notification.Message.Trim();
            }

            return _notification.Type.ToLowerInvariant() switch
            {
                "friendrequest" => "Sent you a friend request.",
                "invite" => "Invited you to a world.",
                "requestinvite" => "Asked you for an invite.",
                "inviteresponse" => "Responded to your invite.",
                "requestinviteresponse" => "Responded to your invite request.",
                "group.invite" => "Invited you to a group.",
                "boop" => "Booped you.",
                "votetokick" => "Started a vote to kick someone.",
                _ => "New VRChat notification."
            };
        }
    }

    public string SenderLine
    {
        get
        {
            var time = TimeText.Relative(_notification.Timestamp);
            if (!string.IsNullOrWhiteSpace(_notification.SenderName))
            {
                return string.IsNullOrWhiteSpace(time)
                    ? $"From {_notification.SenderName}"
                    : $"From {_notification.SenderName} · {time}";
            }

            return time;
        }
    }

    public string DetailsText => IsBoop
        ? BoopKindText
        : ExtractDetails(_notification.Details);

    public Brush RowBackground => Seen ? ReadRowBrush : UnreadRowBrush;

    public void Update(VrcNotification notification)
    {
        _notification = notification;
        OnAllPropertiesChanged();
    }

    /// <summary>Stores the canonical group name and icon resolved by group id.</summary>
    public void ApplyGroupMetadata(string? name, string? iconUrl)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _notification.ResolvedGroupName = name;
        }

        if (!string.IsNullOrWhiteSpace(iconUrl))
        {
            _notification.ResolvedGroupIconUrl = iconUrl;
        }

        RefreshGroupMetadata();
    }

    public void RefreshGroupMetadata()
    {
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(GroupImageUrl));
        OnPropertyChanged(nameof(HasGroupImage));
    }

    /// <summary>Stores resolved boop artwork on the current snapshot.</summary>
    public void ApplyBoopAttachment(string? name, string? imageUrl, string? kind)
    {
        _notification.BoopName = name;
        _notification.BoopImageUrl = imageUrl;
        _notification.BoopKind = kind;
        RefreshBoopAttachment();
    }

    /// <summary>Called after the boop's emoji or inventory item has been resolved.</summary>
    public void RefreshBoopAttachment()
    {
        OnPropertyChanged(nameof(HasBoopAttachment));
        OnPropertyChanged(nameof(HasBoopImage));
        OnPropertyChanged(nameof(BoopImageUrl));
        OnPropertyChanged(nameof(BoopAttachmentTitle));
        OnPropertyChanged(nameof(BoopKindText));
        OnPropertyChanged(nameof(MessageText));
        OnPropertyChanged(nameof(DetailsText));
    }

    public void MarkSeen()
    {
        if (_notification.Seen)
        {
            return;
        }

        _notification.Seen = true;
        OnPropertyChanged(nameof(Seen));
        OnPropertyChanged(nameof(IsUnseen));
        OnPropertyChanged(nameof(RowBackground));
    }

    private static bool IsGenericBoopName(string? name)
        => string.Equals(name, "Emoji", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "Boop emoji", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "Custom emoji", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "Inventory emoji", StringComparison.OrdinalIgnoreCase);

    private string? GetDetailString(string key)
    {
        try
        {
            var details = _notification.Details;
            if (details.ValueKind == JsonValueKind.Undefined || details.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (details.ValueKind == JsonValueKind.String)
            {
                var raw = details.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                using var nested = JsonDocument.Parse(raw);
                return ReadString(nested.RootElement, key);
            }

            return ReadString(details, key);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(key, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    private static string ExtractDetails(JsonElement details)
    {
        try
        {
            if (details.ValueKind == JsonValueKind.Undefined || details.ValueKind == JsonValueKind.Null)
            {
                return string.Empty;
            }

            if (details.ValueKind == JsonValueKind.String)
            {
                var raw = details.GetString();
                if (string.IsNullOrWhiteSpace(raw) || raw.Trim() is "{}" or "[]")
                {
                    return string.Empty;
                }

                using var nested = JsonDocument.Parse(raw);
                return ExtractMessage(nested.RootElement);
            }

            if (details.ValueKind == JsonValueKind.Object)
            {
                return ExtractMessage(details);
            }
        }
        catch (JsonException)
        {
            // Details are allowed to be a plain, non-JSON string.
        }

        return string.Empty;
    }

    private static string ExtractMessage(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var key in new[] { "inviteMessage", "requestMessage", "responseMessage", "message" })
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text) && text != "{}" && !text.StartsWith('{'))
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    private static Brush MakeBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
