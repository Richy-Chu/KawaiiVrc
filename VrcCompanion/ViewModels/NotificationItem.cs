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

    public string SenderName => string.IsNullOrWhiteSpace(_notification.SenderUsername)
        ? "VRChat"
        : _notification.SenderUsername!;

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

    public DateTimeOffset? CreatedAt => _notification.CreatedAt;

    public string ReceivedText => _notification.CreatedAt is null
        ? string.Empty
        : _notification.CreatedAt.Value.ToLocalTime().ToString("MMM d, h:mm tt");

    public string Category => _notification.Type.ToLowerInvariant() switch
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

    public bool IsInvite => string.Equals(Type, "invite", StringComparison.OrdinalIgnoreCase);

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
                worldId.StartsWith("wrld_", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(instanceId))
            {
                return $"{worldId}:{instanceId}";
            }

            return null;
        }
    }

    public bool CanAcceptFriendRequest => IsFriendRequest && !string.IsNullOrWhiteSpace(SenderUserId);

    public bool CanJoinInvite => InviteLocation is not null;

    public string ActionHint => IsFriendRequest
        ? "Friend request"
        : IsInvite
            ? "World invite"
            : string.Empty;

    public bool Seen => _notification.Seen;

    public bool IsUnseen => !Seen;

    public string TypeLabel => _notification.Type.ToLowerInvariant() switch
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

    public Brush TypeBrush => _notification.Type.ToLowerInvariant() switch
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
            var time = TimeText.Relative(_notification.CreatedAt);
            if (!string.IsNullOrWhiteSpace(_notification.SenderUsername))
            {
                return string.IsNullOrWhiteSpace(time)
                    ? $"From {_notification.SenderUsername}"
                    : $"From {_notification.SenderUsername} · {time}";
            }

            return time;
        }
    }

    public string DetailsText => ExtractDetails(_notification.Details);

    public Brush RowBackground => Seen ? ReadRowBrush : UnreadRowBrush;

    public void Update(VrcNotification notification)
    {
        _notification = notification;
        OnAllPropertiesChanged();
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
