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
        Assert.Equal(3, vm.Roles.Count);
        Assert.True(vm.CanManageRoles);
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
        Assert.False(vm.CanManageRoles);
        vm.StartNewRole();
        Assert.False(vm.IsRoleEditorOpen);
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

    [Fact]
    public async Task KickDropsTheMemberCountAndPager()
    {
        var (vm, _) = await OpenAsync(handler =>
        {
            handler.MemberTotal = 201;
            handler.RosterSize = 100;
        });
        Assert.Equal(100, vm.Members.Count);
        Assert.Equal(3, vm.MemberPageCount);
        Assert.Equal("Page 1 of 3", vm.MembersPageText);
        Assert.Contains("201", vm.MemberTotalText);

        vm.RequestKick(vm.Members[0]);
        await vm.ConfirmAsync();

        Assert.Equal(200, vm.MemberTotal);
        Assert.Equal(2, vm.MemberPageCount);
        Assert.Equal(99, vm.Members.Count);
        Assert.Equal("Members (99)", vm.MembersTabLabel);
    }

    [Fact]
    public async Task CreatingARoleSendsItsNameAndPermissions()
    {
        var (vm, handler) = await OpenAsync();
        vm.SelectTabCommand.Execute(GroupModerationViewModel.RolesTab);
        await vm.LoadTabAsync(GroupModerationViewModel.RolesTab);

        vm.StartNewRole();
        Assert.True(vm.IsRoleEditorOpen);
        vm.RoleEditorName = "Greeter";
        vm.RoleEditorDescription = "Welcomes new members";
        vm.RolePermissions.Single(option => option.Key == "group-members-manage").IsChecked = true;

        await vm.SaveRoleAsync();

        var sent = Assert.Single(handler.CreatedRoles);
        Assert.Contains("Greeter", sent);
        Assert.Contains("group-members-manage", sent);
        Assert.False(vm.IsRoleEditorOpen);
        Assert.Contains("created", vm.StatusMessage);
    }

    [Fact]
    public async Task EditingARoleSendsTheEditedValues()
    {
        var (vm, handler) = await OpenAsync();
        var vip = vm.Roles.Single(role => role.RoleId == GroupModerationTestHandler.RoleVipId);

        vm.EditRole(vip);
        Assert.True(vm.IsRoleEditorOpen);
        Assert.Equal("VIP", vm.RoleEditorName);

        vm.RoleEditorName = "VIP Plus";
        await vm.SaveRoleAsync();

        var sent = Assert.Single(handler.UpdatedRoles);
        Assert.Contains(GroupModerationTestHandler.RoleVipId, sent);
        Assert.Contains("VIP Plus", sent);
    }

    [Fact]
    public async Task DeletingARoleWaitsForConfirmation()
    {
        var (vm, handler) = await OpenAsync();
        var vip = vm.Roles.Single(role => role.RoleId == GroupModerationTestHandler.RoleVipId);

        vm.RequestDeleteRole(vip);
        Assert.True(vm.IsConfirmOpen);
        Assert.Empty(handler.DeletedRoles);

        await vm.ConfirmAsync();

        Assert.Equal(GroupModerationTestHandler.RoleVipId, Assert.Single(handler.DeletedRoles));
    }

    [Fact]
    public async Task TheEveryoneRoleCannotBeDeleted()
    {
        var (vm, handler) = await OpenAsync();
        var everyone = vm.Roles.Single(role => role.RoleId == GroupModerationTestHandler.RoleEveryoneId);
        Assert.False(everyone.CanDeleteRole);

        vm.RequestDeleteRole(everyone);

        Assert.False(vm.IsConfirmOpen);
        Assert.Empty(handler.DeletedRoles);
        Assert.Contains("Everyone", vm.StatusMessage);
    }

    [Fact]
    public async Task RoleEditorUsesTheVrchatPermissionCatalogue()
    {
        var (vm, _) = await OpenAsync();
        vm.SelectTabCommand.Execute(GroupModerationViewModel.RolesTab);
        await vm.LoadTabAsync(GroupModerationViewModel.RolesTab);

        Assert.Equal(4, vm.RolePermissions.Count);
        Assert.Contains(vm.RolePermissions, option => option.Label == "Manage bans");
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
