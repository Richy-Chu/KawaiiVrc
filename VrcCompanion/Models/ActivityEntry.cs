using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>
/// Represents an activity feed entry (friend activity, instance history, etc.).
/// </summary>
public sealed class ActivityEntry : INotifyPropertyChanged
{
    private string _details = string.Empty;

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

    [JsonPropertyName("avatarUrl")]
    public string AvatarUrl { get; set; } = string.Empty;

    /// <summary>The avatar the friend changed to, when this is an avatar change.</summary>
    [JsonPropertyName("avatarId")]
    public string AvatarId { get; set; } = string.Empty;

    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarId);

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
            }
        }
    }

    public void SetAvatarName(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            Details = $"changed avatar · {name}";
        }
    }

    [JsonPropertyName("isOnline")]
    public bool IsOnline { get; set; }

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