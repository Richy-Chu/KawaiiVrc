using System;
using System.Globalization;
using VrcCompanion.Models;

namespace VrcCompanion.ViewModels;

/// <summary>
/// One row of a group's member list. The name lives in the member's nested user object, and
/// clicking the row opens that user's profile in the app instead of sending anyone to a
/// browser. Members whose user object VRChat hid still get a readable id so the row is never
/// blank.
/// </summary>
public sealed class GroupMemberItem : ViewModelBase
{
    private readonly Action<GroupMemberItem>? _openProfile;

    public GroupMemberItem(GroupMember member, Action<GroupMemberItem>? openProfile = null)
    {
        Member = member;
        _openProfile = openProfile;
        UserId = member.EffectiveUserId;
        var name = member.EffectiveUserName.Trim();
        DisplayName = name.Length > 0 ? name : "Member without a visible name";
        AvatarUrl = member.EffectiveAvatarUrl;
        OpenProfileCommand = new RelayCommand(() => _openProfile?.Invoke(this), () => CanOpenProfile);
    }

    public GroupMember Member { get; }

    public string UserId { get; }

    public string DisplayName { get; }

    public string AvatarUrl { get; }

    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);

    public bool CanOpenProfile => !string.IsNullOrWhiteSpace(UserId);

    /// <summary>The id, shown under the name so a member is identifiable even when unnamed.</summary>
    public string IdText => UserId;

    public bool HasId => !string.IsNullOrWhiteSpace(UserId);

    public string Initial
    {
        get
        {
            var name = DisplayName.Trim();
            return name.Length == 0
                ? "?"
                : new StringInfo(name).SubstringByTextElements(0, 1).ToUpperInvariant();
        }
    }

    public RelayCommand OpenProfileCommand { get; }
}
