using System;
using System.Linq;
using System.Threading.Tasks;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The moderation panel offers the tools VRCX, VRCNext and the VRChat website give group
/// staff — kick, ban and unban, roles, join requests, invites and the audit log — and hands
/// an ordinary member none of them. Destructive actions only run after a confirmation.
/// </summary>
public class GroupModerationTests
{
    [Fact]
    public async Task ModeratorSeesEveryToolAndTheRoster()
    {
        var (vm, _) = await OpenAsync();

        Assert.True(vm.IsOpen);
        Assert.True(vm.HasModerationPermission);
        Assert.True(vm.CanKick);
        Assert.True(vm.CanBan);
        Assert.True(vm.CanAssignRoles);
        Assert.True(vm.CanReviewRequests);
        Assert.True(vm.CanManageInvites);
        Assert.True(vm.CanViewLog);
        Assert.Equal(2, vm.Members.Count);
        Assert.Equal("Member Person", vm.Members[0].DisplayName);
        Assert.Equal(2, vm.Roles.Count);
        Assert.Contains("kick", vm.PermissionText);
    }

    [Fact]
    public async Task OrdinaryMembersGetNoModerationTools()
    {
        // No permissions on myMember and no role that carries any: a plain member.
        var (vm, _) = await OpenAsync(handler =>
        {
            handler.Permissions.Clear();
            handler.Permissions.Add("group-instance-join");
            handler.MemberRoleIds.Clear();
        });

        Assert.False(vm.HasModerationPermission);
        Assert.False(vm.CanBan);
        Assert.False(vm.CanKick);
        Assert.Empty(vm.Members);
        Assert.Contains("no moderation permissions", vm.PermissionText);
    }

    [Fact]
    public async Task BanWaitsForConfirmationThenBansTheMember()
    {
        var (vm, handler) = await OpenAsync();
        var row = vm.Members[0];

        vm.RequestBan(row);
        Assert.True(vm.IsConfirmOpen);
        Assert.Empty(handler.PostedBans);

        await vm.ConfirmAsync();

        Assert.False(vm.IsConfirmOpen);
        Assert.Equal(new[] { GroupModerationTestHandler.MemberUserId }, handler.PostedBans);
        Assert.Single(vm.Members);
        Assert.Contains("banned", vm.StatusMessage);
    }

    [Fact]
    public async Task KickWaitsForConfirmationThenRemovesTheMember()
    {
        var (vm, handler) = await OpenAsync();

        vm.RequestKick(vm.Members[0]);
        await vm.ConfirmAsync();

        Assert.Equal(GroupModerationTestHandler.MemberUserId, Assert.Single(handler.Kicked));
        Assert.Single(vm.Members);
        Assert.Contains("removed", vm.StatusMessage);
    }

    [Fact]
    public async Task CancellingAConfirmationLeavesTheGroupAlone()
    {
        var (vm, handler) = await OpenAsync();

        vm.RequestBan(vm.Members[0]);
        vm.CancelConfirmCommand.Execute(null);

        Assert.False(vm.IsConfirmOpen);
        Assert.Empty(handler.PostedBans);
        Assert.Equal(2, vm.Members.Count);
    }

    [Fact]
    public async Task BansTabListsBansAndUnbans()
    {
        var (vm, handler) = await OpenAsync();
        Assert.False(vm.ShowBans);

        vm.SelectTabCommand.Execute(GroupModerationViewModel.BansTab);
        Assert.True(vm.ShowBans);
        await vm.LoadTabAsync(GroupModerationViewModel.BansTab);

        var ban = Assert.Single(vm.Bans);
        Assert.Equal("Banned Person", ban.DisplayName);

        vm.RequestUnban(ban);
        await vm.ConfirmAsync();

        Assert.Equal(GroupModerationTestHandler.BannedUserId, Assert.Single(handler.Unbanned));
        Assert.Empty(vm.Bans);
    }

    [Fact]
    public async Task AcceptingAJoinRequestAddsTheUser()
    {
        var (vm, handler) = await OpenAsync();

        vm.SelectTabCommand.Execute(GroupModerationViewModel.RequestsTab);
        await vm.LoadTabAsync(GroupModerationViewModel.RequestsTab);
        var request = Assert.Single(vm.Requests);

        vm.RequestJoinResponse(request, accept: true, block: false);
        await vm.ConfirmAsync();

        Assert.Equal($"{GroupModerationTestHandler.RequestUserId}:accept:False", handler.Responses[0]);
        Assert.Empty(vm.Requests);
    }

    [Fact]
    public async Task RejectAndBlockSendsTheBlockFlag()
    {
        var (vm, handler) = await OpenAsync();

        vm.SelectTabCommand.Execute(GroupModerationViewModel.RequestsTab);
        await vm.LoadTabAsync(GroupModerationViewModel.RequestsTab);

        vm.RequestJoinResponse(vm.Requests[0], accept: false, block: true);
        await vm.ConfirmAsync();

        Assert.Equal($"{GroupModerationTestHandler.RequestUserId}:reject:True", handler.Responses[0]);
    }

    [Fact]
    public async Task RoleChipsAssignAndRemoveForTheSelectedMember()
    {
        var (vm, handler) = await OpenAsync();
        vm.SelectMember(vm.Members[0]);

        var admin = vm.Roles.Single(role => role.RoleId == GroupModerationTestHandler.RoleAdminId);
        var vip = vm.Roles.Single(role => role.RoleId == GroupModerationTestHandler.RoleVipId);
        Assert.False(admin.IsAssigned);
        Assert.True(vip.IsAssigned);

        await vm.ToggleRoleAsync(admin);
        Assert.Equal(GroupModerationTestHandler.RoleAdminId, Assert.Single(handler.RolesAdded));
        Assert.True(admin.IsAssigned);

        await vm.ToggleRoleAsync(vip);
        Assert.Equal(GroupModerationTestHandler.RoleVipId, Assert.Single(handler.RolesRemoved));
        Assert.False(vip.IsAssigned);
    }

    [Fact]
    public async Task AuditLogTabReadsTheWrappedResults()
    {
        var (vm, _) = await OpenAsync();

        vm.SelectTabCommand.Execute(GroupModerationViewModel.LogTab);
        await vm.LoadTabAsync(GroupModerationViewModel.LogTab);

        var entry = Assert.Single(vm.Log);
        Assert.Equal("Group member banned", entry.Description);
        Assert.Equal("Mod Person", entry.Actor);
    }

    [Fact]
    public async Task InvitingNeedsAUserIdAndSendsIt()
    {
        var (vm, handler) = await OpenAsync();
        Assert.False(vm.InviteCommand.CanExecute(null));

        vm.InviteUserId = "usr_invited";
        Assert.True(vm.InviteCommand.CanExecute(null));
        await vm.InviteAsync();

        Assert.Equal("usr_invited", Assert.Single(handler.InvitesSent));
        Assert.Equal(string.Empty, vm.InviteUserId);
    }

    private static async Task<(GroupModerationViewModel Vm, GroupModerationTestHandler Handler)> OpenAsync(
        Action<GroupModerationTestHandler>? configure = null)
    {
        var handler = new GroupModerationTestHandler();
        configure?.Invoke(handler);
        var api = new VrcApiClient(null, handler);
        var group = await api.GetGroupAsync(GroupModerationTestHandler.GroupId);
        var vm = new GroupModerationViewModel(api);
        vm.Open(group!);
        await vm.LoadingTask;
        return (vm, handler);
    }
}
