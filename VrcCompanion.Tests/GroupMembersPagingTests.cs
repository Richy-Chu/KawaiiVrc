using System;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The roster pages 100 members at a time with Previous and Next, asking VRChat for the offset
/// that matches the page, rather than growing one list with a Load more button.
/// </summary>
public class GroupMembersPagingTests
{
    [Fact]
    public async Task FirstPageOffersNextButNotPrevious()
    {
        using var handler = new GroupMemberTestHandler(total: 250);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };

        await SettleAsync(vm);

        Assert.Equal(100, vm.Members.Count);
        Assert.Equal("Showing 1–100 of 250 members", vm.MembersSummary);
        Assert.Equal("Page 1 of 3", vm.MembersPageText);
        Assert.False(vm.CanGoToPreviousMembersPage);
        Assert.True(vm.CanGoToNextMembersPage);
        Assert.Equal(new[] { 0 }, handler.Offsets);
        Assert.Equal("Member 0000", vm.Members[0].DisplayName);
    }

    [Fact]
    public async Task NextMovesForwardAndPreviousComesBack()
    {
        using var handler = new GroupMemberTestHandler(total: 250);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };
        await SettleAsync(vm);

        vm.NextMembersPageCommand.Execute(null);
        await SettleAsync(vm);
        Assert.Equal("Showing 101–200 of 250 members", vm.MembersSummary);
        Assert.Equal("Page 2 of 3", vm.MembersPageText);
        Assert.True(vm.CanGoToPreviousMembersPage);
        Assert.Equal(new[] { 0, 100 }, handler.Offsets);

        vm.NextMembersPageCommand.Execute(null);
        await SettleAsync(vm);
        Assert.Equal(50, vm.Members.Count);
        Assert.Equal("Showing 201–250 of 250 members", vm.MembersSummary);
        Assert.False(vm.CanGoToNextMembersPage);

        vm.PreviousMembersPageCommand.Execute(null);
        await SettleAsync(vm);
        Assert.Equal(100, vm.Members.Count);
        Assert.Equal("Page 2 of 3", vm.MembersPageText);
        Assert.Equal(new[] { 0, 100, 200, 100 }, handler.Offsets);
    }

    [Fact]
    public async Task RefreshingReturnsToTheFirstPage()
    {
        using var handler = new GroupMemberTestHandler(total: 250);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };
        await SettleAsync(vm);
        vm.NextMembersPageCommand.Execute(null);
        await SettleAsync(vm);

        vm.RefreshMembersCommand.Execute(null);
        await SettleAsync(vm);

        Assert.Equal("Page 1 of 3", vm.MembersPageText);
        Assert.Equal(new[] { 0, 100, 0 }, handler.Offsets);
    }

    [Fact]
    public async Task MemberRowsCarryTheNestedUserNameAndAvatar()
    {
        using var handler = new GroupMemberTestHandler(total: 3);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 3 }
        };

        await SettleAsync(vm);

        Assert.Equal(3, vm.Members.Count);
        Assert.All(vm.Members, member => Assert.StartsWith("Member 000", member.DisplayName));
        Assert.All(vm.Members, member => Assert.EndsWith(".png", member.AvatarUrl));
        Assert.Equal("Showing 1–3 of 3 members", vm.MembersSummary);
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
