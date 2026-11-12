using System.Collections.Generic;
using System.Reflection;
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
    public void LeaveGroupNeedsASecondConfirmationStep()
    {
        using var api = new VrcApiClient();
        var vm = new GroupViewModel(api);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(GroupViewModel).GetField("_groupInfo", flags)!.SetValue(vm,
            new Group { Id = "grp_test", Name = "Test group", JoinState = "open" });
        typeof(GroupViewModel).GetField("_isMemberOfGroupInfo", flags)!.SetValue(vm, true);

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
