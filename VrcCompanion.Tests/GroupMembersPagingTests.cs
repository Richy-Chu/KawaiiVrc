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

        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");

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
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");

        vm.NextMembersPageCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");
        Assert.Equal("Showing 101–200 of 250 members", vm.MembersSummary);
        Assert.Equal("Page 2 of 3", vm.MembersPageText);
        Assert.True(vm.CanGoToPreviousMembersPage);
        Assert.Equal(new[] { 0, 100 }, handler.Offsets);

        vm.NextMembersPageCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");
        Assert.Equal(50, vm.Members.Count);
        Assert.Equal("Showing 201–250 of 250 members", vm.MembersSummary);
        Assert.False(vm.CanGoToNextMembersPage);

        vm.PreviousMembersPageCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");
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
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");
        vm.NextMembersPageCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");

        vm.RefreshMembersCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");

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

        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");

        Assert.Equal(3, vm.Members.Count);
        Assert.All(vm.Members, member => Assert.StartsWith("Member 000", member.DisplayName));
        Assert.All(vm.Members, member => Assert.EndsWith(".png", member.AvatarUrl));
        Assert.Equal("Showing 1–3 of 3 members", vm.MembersSummary);
    }

    [Fact]
    public async Task TypedPageNumberJumpsStraightToThatPage()
    {
        using var handler = new GroupMemberTestHandler(total: 56900);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 56900 }
        };
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");

        vm.MemberPageInput = "500";
        Assert.True(vm.JumpToMembersPageCommand.CanExecute(null));
        vm.JumpToMembersPageCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");

        Assert.Equal("Page 500 of 569", vm.MembersPageText);
        Assert.Equal("500", vm.MemberPageInput);
        Assert.Equal("Showing 49,901–50,000 of 56,900 members", vm.MembersSummary);
        Assert.Equal(new[] { 0, 49900 }, handler.Offsets);
    }

    [Fact]
    public async Task FirstAndLastReachTheEndsOfTheRoster()
    {
        using var handler = new GroupMemberTestHandler(total: 56900);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 56900 }
        };
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");

        vm.LastMembersPageCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");
        Assert.Equal("Page 569 of 569", vm.MembersPageText);
        Assert.False(vm.CanGoToNextMembersPage);

        vm.FirstMembersPageCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");
        Assert.Equal("Page 1 of 569", vm.MembersPageText);
        Assert.False(vm.CanGoToPreviousMembersPage);
        Assert.Equal(new[] { 0, 56800, 0 }, handler.Offsets);
    }

    [Fact]
    public async Task OutOfRangePageNumberClampsToTheLastPage()
    {
        using var handler = new GroupMemberTestHandler(total: 56900);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 56900 }
        };
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");

        vm.MemberPageInput = "9999";
        vm.JumpToMembersPageCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");

        Assert.Equal("Page 569 of 569", vm.MembersPageText);
        Assert.Equal(new[] { 0, 56800 }, handler.Offsets);
    }

    [Fact]
    public async Task NextBumpsThePageTokenSoThePopupScrollsToTheTop()
    {
        using var handler = new GroupMemberTestHandler(total: 250);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");
        var firstPage = vm.Members;
        var firstToken = vm.MembersPageToken;
        var firstPopupToken = vm.PopupPageToken;

        vm.NextMembersPageCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");

        // The popup scrolls to the top whenever this token changes, so a new page is
        // read from its first member instead of from wherever the pager was left.
        Assert.True(vm.MembersPageToken > firstToken);
        Assert.True(vm.PopupPageToken > firstPopupToken);
        Assert.Same(firstPage, vm.Members);
        Assert.Equal(100, vm.Members.Count);
        Assert.Equal("Member 0100", vm.Members[0].DisplayName);
    }

    [Fact]
    public async Task EmptyPagesStopThePagerAtWhatVrchatReturns()
    {
        using var handler = new GroupMemberTestHandler(total: 56789) { Viewable = 300 };
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 56789 }
        };
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");

        vm.NextMembersPageCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");
        vm.NextMembersPageCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");
        Assert.Equal("Page 3 of 568", vm.MembersPageText);

        // VRChat keeps saying there are 56,789 members but sends nothing past 300.
        vm.NextMembersPageCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The members page never settled.");

        Assert.Equal(100, vm.Members.Count);
        Assert.Equal("Page 3 of 3", vm.MembersPageText);
        Assert.False(vm.CanGoToNextMembersPage);
        Assert.Contains("VRChat only returns the first 300 members", vm.MembersSummary);
    }

}
