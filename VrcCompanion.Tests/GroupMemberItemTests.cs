using System.Text.Json;
using VrcCompanion.Models;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// VRChat's member objects carry the name inside a nested user object; reading only userId is
/// what produced a list of blank rows. The row also has to be a way into the profile.
/// </summary>
public class GroupMemberItemTests
{
    [Fact]
    public void NameAndAvatarComeFromTheNestedUser()
    {
        var member = JsonSerializer.Deserialize<GroupMember>("""
            {
              "id": "gmem_1",
              "groupId": "grp_1",
              "userId": "usr_1",
              "membershipStatus": "member",
              "user": {
                "id": "usr_1",
                "displayName": "Night Fox",
                "username": "nightfox",
                "iconUrl": "https://example.com/icon.png",
                "currentAvatarThumbnailImageUrl": "https://example.com/thumb.png"
              }
            }
            """)!;

        var item = new GroupMemberItem(member);

        Assert.Equal("Night Fox", item.DisplayName);
        Assert.Equal("usr_1", item.UserId);
        Assert.Equal("https://example.com/thumb.png", item.AvatarUrl);
        Assert.Equal("N", item.Initial);
        Assert.True(item.HasAvatar);
        Assert.True(item.HasId);
    }

    [Fact]
    public void MissingUserObjectStillGivesAReadableRow()
    {
        var member = new GroupMember { UserId = "usr_2" };

        var item = new GroupMemberItem(member);

        Assert.Equal("Member without a visible name", item.DisplayName);
        Assert.Equal("usr_2", item.IdText);
        Assert.True(item.CanOpenProfile);
        Assert.False(item.HasAvatar);
    }

    [Fact]
    public void ClickingARowOpensThatProfile()
    {
        GroupMemberItem? opened = null;
        var item = new GroupMemberItem(
            new GroupMember { UserId = "usr_3", User = new GroupMember.GroupMemberUser { DisplayName = "Kai" } },
            member => opened = member);

        item.OpenProfileCommand.Execute(null);

        Assert.NotNull(opened);
        Assert.Equal("usr_3", opened!.UserId);
    }

    [Fact]
    public void MemberWithoutAnIdCannotOpenAProfile()
    {
        var item = new GroupMemberItem(new GroupMember());

        Assert.False(item.CanOpenProfile);
        Assert.False(item.OpenProfileCommand.CanExecute(null));
    }
}
