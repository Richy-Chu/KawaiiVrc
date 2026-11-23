using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    public void SharedProfileGroupIsLabelledMutual()
    {
        var mutual = new VrcProfileGroup { Id = "grp_test", Name = "Shared", IsMine = true, IsMutual = true };
        var mineOnly = new VrcProfileGroup { Id = "grp_other", Name = "Mine", IsMine = true };

        Assert.Equal("Mutual", mutual.MembershipLabel);
        Assert.True(mutual.HasMembershipLabel);
        Assert.Equal("Joined", mineOnly.MembershipLabel);
    }

    [Fact]
    public void RequestedGroupIsNotTreatedAsJoined()
    {
        var group = new Group { Id = "grp_test", MembershipStatus = "requested" };

        Assert.False(group.IsMember);
        Assert.Equal("Request pending", group.MembershipLabel);
    }

    [Fact]
    public void GroupPopupFollowsLiveMembershipAndNotTheStaleStatus()
    {
        using var api = new VrcApiClient();
        var vm = new GroupViewModel(api)
        {
            GroupInfo = new Group { Id = "grp_test", Name = "Test", MembershipStatus = "member" },
            IsMemberOfGroupInfo = true
        };

        Assert.Equal("You are a member", vm.GroupInfoMemberText);
        Assert.Equal("Joined", vm.GroupInfoMembershipLabel);

        // Leaving flips the live flag while the fetched payload still says member.
        vm.IsMemberOfGroupInfo = false;
        Assert.Equal("You are not a member", vm.GroupInfoMemberText);
        Assert.Equal(string.Empty, vm.GroupInfoMembershipLabel);

        // Joining flips it back without waiting for a refetch.
        vm.IsMemberOfGroupInfo = true;
        Assert.Equal("You are a member", vm.GroupInfoMemberText);
        Assert.Equal("Joined", vm.GroupInfoMembershipLabel);
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
    public void GroupInstanceJoinUsesTheSharedLaunchChooser()
    {
        using var api = new VrcApiClient();
        string? chosenLocation = null;
        string? chosenLabel = null;
        var vm = new GroupViewModel(api, (location, label) =>
        {
            chosenLocation = location;
            chosenLabel = label;
        });
        var instance = new GroupInstance
        {
            Location = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345~group(grp_abc)"
        };

        vm.JoinGroupInstanceCommand.Execute(instance);

        Assert.Equal(instance.Location, chosenLocation);
        Assert.Equal("that group instance", chosenLabel);
    }

    [Fact]
    public async Task GroupDetailsLoadsInstancesAndJoinsThroughSharedLaunchChooser()
    {
        using var api = new VrcApiClient(null, new GroupInstanceHandler());
        string? chosenLocation = null;
        string? chosenLabel = null;
        var vm = new GroupViewModel(api, (location, label) =>
        {
            chosenLocation = location;
            chosenLabel = label;
        });

        await vm.OpenGroupInfoAsync("grp_1");

        var instance = Assert.Single(vm.GroupInfoInstances);
        Assert.Equal("The Great Pug", instance.WorldName);
        Assert.Equal("1 open instance", vm.GroupInfoInstancesText);
        Assert.True(instance.CanJoin);

        vm.JoinGroupInstanceCommand.Execute(instance);

        Assert.Equal(instance.Location, chosenLocation);
        Assert.Equal("that group instance", chosenLabel);
    }

    [Fact]
    public async Task MembershipGroupInstancesSurviveGroupIdNormalization()
    {
        var api = new VrcApiClient(null, new GroupInstanceHandler());
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group
            {
                Id = "gmem_1",
                GroupId = "grp_1",
                Name = "Test Group",
                MembershipStatus = "member"
            }
        };

        // The roster comes from an instant fake handler; ten seconds is scheduler headroom.
        var deadline = System.DateTime.UtcNow.AddSeconds(10);
        while (vm.Instances.Count == 0 && System.DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        var instance = Assert.Single(vm.Instances);
        Assert.Equal("The Great Pug", instance.WorldName);
        Assert.Equal("wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345~group(grp_1)", instance.Location);
    }

    [Fact]
    public void InvalidOrMissingLocationsCannotBeJoined()
    {
        var instance = new GroupInstance { Location = "not-a-location" };

        Assert.False(instance.CanJoin);
        Assert.Equal("Unknown world", instance.WorldName);
        Assert.Equal("Public", instance.AccessText);
    }

    private sealed class GroupInstanceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/groups/grp_1", System.StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "id": "grp_1",
                      "name": "Test Group",
                      "membershipStatus": "member",
                      "joinState": "open"
                    }
                    """);
            }

            if (path.Contains("/groups/grp_1/instances", System.StringComparison.Ordinal))
            {
                return Json("""
                    [
                      {
                        "instanceId": "12345~group(grp_1)",
                        "location": "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345~group(grp_1)",
                        "memberCount": 6,
                        "world": { "name": "The Great Pug" }
                      }
                    ]
                    """);
            }

            return Json("[]");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
