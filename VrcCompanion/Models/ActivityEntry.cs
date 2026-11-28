using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>
/// Represents an activity feed entry (friend activity, instance history, etc.).
/// </summary>
public sealed class ActivityEntry : INotifyPropertyChanged
{
    private string _details = string.Empty;
    private string _avatarId = string.Empty;
    private string _avatarUrl = string.Empty;
    private string _avatarName = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public ActivityType Type { get; set; }

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// True when this activity belongs to the signed-in account. It is stored
    /// with the row so a self event remains identifiable after a restart.
    /// </summary>
    [JsonPropertyName("isSelf")]
    public bool IsSelf { get; set; }

    /// <summary>Trust tags captured when the activity was recorded.</summary>
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("developerType")]
    public string? DeveloperType { get; set; }

    /// <summary>
    /// The url as stored. A row without an avatar image keeps the property off
    /// the file entirely; it used to write an empty string on every row.
    /// </summary>
    [JsonPropertyName("avatarUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StoredAvatarUrl
    {
        get => string.IsNullOrEmpty(_avatarUrl) ? null : _avatarUrl;
        set => AvatarUrl = value ?? string.Empty;
    }

    [JsonIgnore]
    public string AvatarUrl
    {
        get => _avatarUrl;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_avatarUrl, next, StringComparison.Ordinal))
            {
                return;
            }

            _avatarUrl = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarUrl)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAvatarImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanOpenAvatar)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarActionTooltip)));
        }
    }

    /// <summary>
    /// Avatar name carried by VRChat's local log even when the API has not yet
    /// supplied an id or picture.
    /// </summary>
    [JsonPropertyName("avatarName")]
    public string AvatarName
    {
        get => _avatarName;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_avatarName, next, StringComparison.Ordinal))
            {
                return;
            }

            _avatarName = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveAvatarName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAvatarName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanOpenAvatar)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarActionTooltip)));
        }
    }

    /// <summary>The avatar creator VRChat's log reported for this switch.</summary>
    [JsonPropertyName("avatarAuthor")]
    public string? AvatarAuthor { get; set; }

    /// <summary>
    /// True when <see cref="AvatarId"/> came from the player's own VRChat record - the
    /// avatar their profile is wearing - rather than a name search. The popup then opens
    /// that exact record, even when its name differs from the log line, because a search
    /// can hand back a different avatar that shares the name. Not persisted: the row
    /// resolves it again from the live record.
    /// </summary>
    [JsonIgnore]
    public bool AvatarIdIsAuthoritative { get; set; }

    /// <summary>
    /// Where <see cref="AvatarUrl"/> came from: the player's own record, this account's
    /// saved history, or a public avatar index. The avatar card shows it so a reader can
    /// tell a confirmed picture from one borrowed off a stale record. Not persisted.
    /// </summary>
    [JsonIgnore]
    public string AvatarPictureSource { get; set; } = string.Empty;

    /// <summary>
    /// True when <see cref="AvatarId"/> came from the player's current VRChat record.
    /// Only that may be reported as private when VRChat refuses the record: an id
    /// remembered from older history can belong to another avatar with the same name or to
    /// one that was deleted, and calling that private would be a guess. Not persisted.
    /// </summary>
    [JsonIgnore]
    public bool AvatarIdIsCurrentAvatar { get; set; }

    /// <summary>The avatar the friend changed to, when this is an avatar change.</summary>
    [JsonPropertyName("avatarId")]
    public string AvatarId
    {
        get => _avatarId;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_avatarId, next, StringComparison.Ordinal))
            {
                return;
            }

            _avatarId = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarId)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAvatar)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanOpenAvatar)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsActionTooltip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarActionTooltip)));
        }
    }

    /// <summary>
    /// A copy that shares the tags list and string values. The store writes this
    /// with the per-user data lifted out, so the live row keeps its badges.
    /// </summary>
    internal ActivityEntry ShallowCopy() => (ActivityEntry)MemberwiseClone();

    [JsonIgnore]
    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarId);

    [JsonIgnore]
    public bool HasAvatarImage => !string.IsNullOrWhiteSpace(AvatarUrl);

    /// <summary>Older rows stored the avatar name only inside Details.</summary>
    [JsonIgnore]
    public string EffectiveAvatarName => !string.IsNullOrWhiteSpace(AvatarName)
        ? AvatarName
        : Details.StartsWith("changed avatar · ", StringComparison.Ordinal)
            ? Details["changed avatar · ".Length..].Trim()
            : string.Empty;

    [JsonIgnore]
    public bool HasAvatarName => IsAvatarChange && !string.IsNullOrWhiteSpace(EffectiveAvatarName);

    /// <summary>
    /// VRChat often omits the avatar id but still provides the avatar picture,
    /// and the local log often provides only the name. Any of the three lets the
    /// popup open and resolve the public avatar record.
    /// </summary>
    [JsonIgnore]
    public bool CanOpenAvatar => HasAvatar || HasAvatarImage || HasAvatarName;

    [JsonIgnore]
    public bool IsAvatarChange => Type == ActivityType.AvatarChanged;

    /// <summary>
    /// An avatar-change row opens the avatar itself; other activity rows open
    /// the person who performed the action.
    /// </summary>
    [JsonIgnore]
    public string DetailsActionTooltip => IsAvatarChange
        ? CanOpenAvatar
            ? "View this avatar — wear it if it is public"
            : "Avatar details are unavailable for this activity entry"
        : "Open this person's VRChat profile";

    [JsonIgnore]
    public string AvatarActionTooltip => CanOpenAvatar
        ? "Open this avatar's details — wear it if it is public"
        : "This activity entry has no avatar picture to open.";

    [JsonPropertyName("worldId")]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("worldName")]
    public string WorldName { get; set; } = string.Empty;

    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("details")]
    public string Details
    {
        get => _details;
        set
        {
            if (!string.Equals(_details, value, StringComparison.Ordinal))
            {
                _details = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Details)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveAvatarName)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAvatarName)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanOpenAvatar)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsActionTooltip)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarActionTooltip)));
            }
        }
    }

    public void SetAvatarName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        AvatarName = name.Trim();
        Details = $"changed avatar · {AvatarName}";
    }

    [JsonPropertyName("isOnline")]
    public bool IsOnline { get; set; }

    /// <summary>
    /// A friended/unfriended event found by comparing the snapshot saved before
    /// the app closed. The displayed time is the launch-time verification time.
    /// </summary>
    [JsonPropertyName("detectedAfterRelaunch")]
    public bool DetectedAfterRelaunch { get; set; }

    /// <summary>Absolute local date and time, shown alongside the relative age.</summary>
    [JsonIgnore]
    public string TimestampText
    {
        get
        {
            if (Timestamp == default)
            {
                return string.Empty;
            }

            var text = Timestamp.ToLocalTime().ToString("MMM d, yyyy h:mm tt");
            return DetectedAfterRelaunch
                ? $"{text} · detected on next launch"
                : text;
        }
    }

    [JsonIgnore]
    public string RelativeTime
    {
        get
        {
            var span = DateTimeOffset.UtcNow - Timestamp;
            if (span.TotalSeconds < 60)
                return "just now";
            if (span.TotalMinutes < 60)
                return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24)
                return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 7)
                return $"{(int)span.TotalDays}d ago";
            return Timestamp.ToString("MMM dd");
        }
    }
}

/// <summary>
/// Types of activity feed entries.
/// </summary>
public enum ActivityType
{
    FriendOnline,
    FriendOffline,
    FriendJoinedWorld,
    FriendLeftWorld,
    InstanceJoined,
    InstanceLeft,
    FriendRequest,
    GroupInvite,
    WorldCreated,
    ProfileUpdated,
    FriendAdded,
    FriendRemoved,
    BioChanged,
    AvatarChanged
}