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
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        vm.MemberSearchText = "Member 011";
        Assert.True(vm.SearchMembersCommand.CanExecute(null));
        vm.SearchMembersCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

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
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        vm.MemberSearchText = "usr_42";
        vm.SearchMembersCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        Assert.Single(vm.Members);
        Assert.Equal("usr_42", vm.Members[0].UserId);
        Assert.Equal("Member 0042", vm.Members[0].DisplayName);
    }

    [Fact]
    public async Task UserIdSearchFillsNameAndAvatarFromTheResolvedAccount()
    {
        using var handler = new GroupMemberTestHandler(total: 250) { BareMemberLookups = true };
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        vm.MemberSearchText = "usr_42";
        vm.SearchMembersCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        // The membership record has no user object of its own; the resolved account fills it.
        var member = Assert.Single(vm.Members);
        Assert.Equal("Member 0042", member.DisplayName);
        Assert.EndsWith("user-42.png", member.AvatarUrl);
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
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        vm.MemberSearchText = "usr_not_a_member";
        vm.SearchMembersCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

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
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

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
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");
        vm.MemberSearchText = "Member 011";
        vm.SearchMembersCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        vm.ClearMemberSearchCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        Assert.False(vm.IsMemberSearchActive);
        Assert.Equal(100, vm.Members.Count);
        Assert.Equal("Showing 1–100 of 250 members", vm.MembersSummary);
        Assert.Equal(string.Empty, vm.MemberSearchText);
    }

    [Fact]
    public async Task UsernameFallsBackToVrchatUserSearchAndMembership()
    {
        using var handler = new GroupMemberTestHandler(total: 250);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        // "member42" is the account username: their display name is "Member 0042",
        // which the display-name search cannot match.
        vm.MemberSearchText = "member42";
        vm.SearchMembersCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        Assert.Single(vm.Members);
        Assert.Equal("usr_42", vm.Members[0].UserId);
        Assert.Equal("Member 0042", vm.Members[0].DisplayName);
        Assert.Equal(
            "1 member matches “member42” — matched by username or user ID",
            vm.MembersSummary);
        Assert.Contains("usr_42", handler.MemberLookups);
    }

    [Fact]
    public async Task UsernameOutsideTheGroupSaysTheyAreNotAMember()
    {
        using var handler = new GroupMemberTestHandler(total: 250);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        vm.MemberSearchText = "stranger";
        vm.SearchMembersCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        Assert.Empty(vm.Members);
        Assert.Equal(
            "“Stranger Danger (@stranger)” is not a member of this group.",
            vm.MembersSummary);
    }

    [Fact]
    public async Task PartialUserIdIsTriedAsAUserId()
    {
        using var handler = new GroupMemberTestHandler(total: 250);
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        vm.MemberSearchText = "a1b2c3";
        vm.SearchMembersCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        // The partial id is resolved through the public user endpoint, which the group's
        // membership endpoint would refuse on locked-down groups.
        Assert.Contains("usr_a1b2c3", handler.UserLookups);
        Assert.Empty(vm.Members);
        Assert.Equal("No members match “a1b2c3” in this group.", vm.MembersSummary);
    }

    [Fact]
    public async Task ForbiddenMembershipDetailsExplainsInsteadOfFailing()
    {
        using var handler = new GroupMemberTestHandler(total: 250) { ForbidMembershipDetails = true };
        using var api = new VrcApiClient(null, handler);
        var vm = new GroupViewModel(api)
        {
            SelectedGroup = new Group { Id = "grp_1", MemberCount = 250 }
        };
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        // A usr_ id copied from a friend's profile: VRChat answers the group's member
        // lookup with 403, which must not become "Could not search this group's members".
        vm.MemberSearchText = "usr_99";
        vm.SearchMembersCommand.Execute(null);
        await Settle.UntilAsync(() => !vm.IsBusy, "The member search never settled.");

        Assert.Empty(vm.Members);
        Assert.DoesNotContain("Could not search", vm.MembersSummary);
        Assert.Contains("may not view this group's membership details", vm.MembersSummary);
    }

}
