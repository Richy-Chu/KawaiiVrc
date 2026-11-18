using VrcCompanion.Models;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The world popup lists group instances by their group instead of calling every row
/// "Group instance", so the row reads the group out of the instance id and reports
/// whether the viewer is already in that group.
/// </summary>
public class WorldInstanceGroupTests
{
    private const string WorldId = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";

    private static WorldInstanceItem Item(string instanceId)
        => new(new VrcInstance
        {
            InstanceId = instanceId,
            Location = $"{WorldId}:{instanceId}",
            UserCount = 38,
            Capacity = 80,
            Region = "use"
        }, WorldId);

    [Fact]
    public void GroupInstanceReadsItsGroupFromTheInstanceId()
    {
        var item = Item("12345~group(grp_abc)~groupAccessType(public)");

        Assert.Equal("grp_abc", item.GroupId);
        Assert.True(item.HasGroup);
        Assert.Equal("Group · loading…", item.GroupChipText);
    }

    [Fact]
    public void PublicInstanceHasNoGroupChip()
    {
        var item = Item("12345");

        Assert.False(item.HasGroup);
        Assert.Empty(item.GroupChipText);
        Assert.Equal("Public instance", item.Title);
    }

    [Theory]
    [InlineData("member", "open", "Group · Joined")]
    [InlineData(null, "open", "Group · Open to join")]
    [InlineData(null, "request", "Group · Request to join")]
    [InlineData(null, "invite", "Group · Invite only")]
    [InlineData(null, "closed", "Group · Invite only")]
    public void ResolvedGroupNamesTheRowAndItsJoinState(string? membership, string joinState, string chip)
    {
        var item = Item("12345~group(grp_abc)");
        item.ApplyGroup(new Group
        {
            Id = "grp_abc",
            Name = "Popcorn Palace",
            JoinState = joinState,
            MembershipStatus = membership ?? string.Empty
        });

        Assert.Equal("Popcorn Palace", item.Title);
        Assert.Equal(chip, item.GroupChipText);
    }

    [Fact]
    public void FailedGroupLookupStillOffersTheGroup()
    {
        var item = Item("12345~group(grp_abc)");
        item.ApplyGroup(null);

        Assert.Equal("Group · View group", item.GroupChipText);
        Assert.Equal("Group instance", item.Title);
    }
}
