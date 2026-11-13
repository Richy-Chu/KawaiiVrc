using System.Collections.Generic;
using System.Text.Json;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class GroupInstanceTests
{
    [Fact]
    public void DeserializesWorldAndMembersForAGroupInstance()
    {
        const string json = """
        [
          {
            "instanceId": "12345~group(grp_abc)",
            "location": "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345~group(grp_abc)",
            "memberCount": 6,
            "world": {
              "name": "The Great Pug",
              "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_abc/1/256"
            }
          }
        ]
        """;

        var instances = JsonSerializer.Deserialize<List<GroupInstance>>(json);

        var instance = Assert.Single(instances!);
        Assert.Equal("The Great Pug", instance.WorldName);
        Assert.Equal("6 people here", instance.UsersText);
        Assert.Equal("Group", instance.AccessText);
        Assert.True(instance.CanJoin);
        Assert.Contains("file_abc", instance.ThumbnailUrl, System.StringComparison.Ordinal);
    }

    [Fact]
    public void MembershipListGroupsResolveTheRealGroupIdAndMembership()
    {
        const string json = """
        {
          "id": "gmem_05ffa838-fa82-47f9-9033-47e468a1a3e5",
          "groupId": "grp_326c2349-0e87-4e75-81ab-5b6cf8ebb665",
          "name": "After Dark",
          "membershipStatus": "member",
          "joinState": "request"
        }
        """;

        var group = JsonSerializer.Deserialize<Group>(json)!;

        Assert.Equal("grp_326c2349-0e87-4e75-81ab-5b6cf8ebb665", group.EffectiveId);
        Assert.True(group.IsMember);
        Assert.Equal("Joined", group.MembershipLabel);
    }

    [Fact]
    public void RequestedGroupIsNotTreatedAsJoined()
    {
        var group = new Group { Id = "grp_test", MembershipStatus = "requested" };

        Assert.False(group.IsMember);
        Assert.Equal("Request pending", group.MembershipLabel);
    }

    [Fact]
    public void LeaveGroupNeedsASecondConfirmationStep()
    {
        using var api = new VrcApiClient();
        var vm = new GroupViewModel(api)
        {
            GroupInfo = new Group { Id = "grp_test", Name = "Test group", JoinState = "open" },
            IsMemberOfGroupInfo = true
        };

        Assert.False(vm.IsLeaveGroupConfirmOpen);
        vm.RequestLeaveGroupCommand.Execute(null);
        Assert.True(vm.IsLeaveGroupConfirmOpen);

        vm.CancelLeaveGroupCommand.Execute(null);
        Assert.False(vm.IsLeaveGroupConfirmOpen);
    }

    [Fact]
    public void InvalidOrMissingLocationsCannotBeJoined()
    {
        var instance = new GroupInstance { Location = "not-a-location" };

        Assert.False(instance.CanJoin);
        Assert.Equal("Unknown world", instance.WorldName);
        Assert.Equal("Public", instance.AccessText);
    }
}
