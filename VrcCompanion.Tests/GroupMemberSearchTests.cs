using System;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// Members can be searched by display name through VRChat's member search endpoint and by
/// usr_ id through the single-member lookup, and clearing goes back to the paged roster.
/// </summary>
public class GroupMemberSearchTests
{
    [Fact]
    public async Task SearchByDisplayNameShowsMatches()
    {
        using var handler = new GroupMemberTestHandler(total: 250);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };
        await SettleAsync(vm);

        vm.MemberSearchText = "Member 011";
        Assert.True(vm.SearchMembersCommand.CanExecute(null));
        vm.SearchMembersCommand.Execute(null);
        await SettleAsync(vm);

        Assert.True(vm.IsMemberSearchActive);
        Assert.Equal(10, vm.Members.Count);
        Assert.Equal("10 members match “Member 011”", vm.MembersSummary);
        Assert.False(vm.CanGoToNextMembersPage);
        Assert.All(vm.Members, member => Assert.Contains("011", member.DisplayName));
        Assert.Contains("Member 011", handler.Searches[0]);
    }

    [Fact]
    public async Task SearchByUserIdFindsThatMember()
    {
        using var handler = new GroupMemberTestHandler(total: 250);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };
        await SettleAsync(vm);

        vm.MemberSearchText = "usr_42";
        vm.SearchMembersCommand.Execute(null);
        await SettleAsync(vm);

        Assert.Single(vm.Members);
        Assert.Equal("usr_42", vm.Members[0].UserId);
        Assert.Equal("Member 0042", vm.Members[0].DisplayName);
    }

    [Fact]
    public async Task UnknownUserIdReportsNoMatch()
    {
        using var handler = new GroupMemberTestHandler(total: 10);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 10 }
        };
        await SettleAsync(vm);

        vm.MemberSearchText = "usr_not_a_member";
        vm.SearchMembersCommand.Execute(null);
        await SettleAsync(vm);

        Assert.Empty(vm.Members);
        Assert.Equal(
            "No members match “usr_not_a_member” in this group.",
            vm.MembersSummary);
    }

    [Fact]
    public async Task ShortQueriesCannotRun()
    {
        using var api = new VrcApiClient(null, new GroupMemberTestHandler(total: 10));
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 10 }
        };
        await SettleAsync(vm);

        vm.MemberSearchText = "ab";

        Assert.False(vm.SearchMembersCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClearingGoesBackToTheRoster()
    {
        using var handler = new GroupMemberTestHandler(total: 250);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };
        await SettleAsync(vm);
        vm.MemberSearchText = "Member 011";
        vm.SearchMembersCommand.Execute(null);
        await SettleAsync(vm);

        vm.ClearMemberSearchCommand.Execute(null);
        await SettleAsync(vm);

        Assert.False(vm.IsMemberSearchActive);
        Assert.Equal(100, vm.Members.Count);
        Assert.Equal("Showing 1–100 of 250 members", vm.MembersSummary);
        Assert.Equal(string.Empty, vm.MemberSearchText);
    }

    private static async Task SettleAsync(GroupViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.IsBusy && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.False(vm.IsBusy);
    }
}
