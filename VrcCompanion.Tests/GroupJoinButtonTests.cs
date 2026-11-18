using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The group popup used to show a greyed-out "Join group" for groups that take join
/// requests. The button now says what it will do and stays usable.
/// </summary>
public class GroupJoinButtonTests
{
    [Theory]
    [InlineData("open", "Join group", true)]
    [InlineData("request", "Request to join", true)]
    [InlineData("invite", "Join group", false)]
    [InlineData("closed", "Join group", false)]
    public void JoinButtonMatchesTheGroupJoinState(string joinState, string text, bool enabled)
    {
        using var api = new VrcApiClient();
        var vm = new GroupViewModel(api)
        {
            GroupInfo = new Group { Id = "grp_1", JoinState = joinState }
        };

        Assert.Equal(text, vm.GroupJoinButtonText);
        Assert.Equal(enabled, vm.CanUseGroupJoinButton);
        Assert.Equal(enabled, vm.JoinGroupInfoCommand.CanExecute(null));
    }

    [Fact]
    public void MemberCannotUseTheJoinButton()
    {
        using var api = new VrcApiClient();
        var vm = new GroupViewModel(api)
        {
            GroupInfo = new Group { Id = "grp_1", JoinState = "open" },
            IsMemberOfGroupInfo = true
        };

        Assert.False(vm.CanUseGroupJoinButton);
    }

    [Fact]
    public void RequestToJoinOpensTheGroupOnTheWeb()
    {
        using var api = new VrcApiClient();
        string? opened = null;
        var vm = new GroupViewModel(api, openUrl: url =>
        {
            opened = url;
            return true;
        });
        vm.GroupInfo = new Group { Id = "grp_req", JoinState = "request", Name = "KushCOOKIES" };

        vm.JoinGroupInfoCommand.Execute(null);

        // VRChat's API only joins open groups, so the request is sent on the website.
        Assert.Equal("https://vrchat.com/home/group/grp_req", opened);
    }
}
