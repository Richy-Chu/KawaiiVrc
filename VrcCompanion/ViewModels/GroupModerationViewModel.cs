using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Group moderation for groups the signed-in account helps run. The same tools VRCX, VRCNext
/// and the VRChat website offer, wired straight to the API the client uses: kick and ban
/// members, unban, hand out or take away roles, answer join requests, send and cancel
/// invites, and read the audit log. Every action is gated on the permissions VRChat reports
/// in the group's myMember object (plus the permissions of the account's roles).
/// </summary>
public sealed class GroupModerationViewModel : ViewModelBase
{
    public const string MembersTab = "Members";
    public const string BansTab = "Bans";
    public const string RequestsTab = "Requests";
    public const string InvitesTab = "Invites";
    public const string LogTab = "Audit log";

    private const int PageSize = 100;

    private readonly VrcApiClient _api;
    private Group? _group;
    private string _selectedTab = MembersTab;
    private ObservableCollection<ModerationMemberRow> _members = new();
    private ObservableCollection<ModerationEntryRow> _bans = new();
    private ObservableCollection<ModerationEntryRow> _requests = new();
    private ObservableCollection<ModerationEntryRow> _invites = new();
    private ObservableCollection<ModerationRoleToggle> _roles = new();
    private ObservableCollection<ModerationLogRow> _log = new();
    private ModerationMemberRow? _selectedMember;
    private bool _isOpen;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private string _inviteUserId = string.Empty;
    private string _pendingAction = string.Empty;
    private Func<Task>? _pendingWork;
    private int _memberPage;
    private bool _bansLoaded;
    private bool _requestsLoaded;
    private bool _invitesLoaded;
    private bool _logLoaded;

    public GroupModerationViewModel(VrcApiClient api)
    {
        _api = api;
        SelectTabCommand = new RelayCommand<string>(SelectTab, tab => !string.IsNullOrWhiteSpace(tab));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsOpen && !IsBusy);
        PreviousMembersPageCommand = new AsyncRelayCommand(PreviousMembersPageAsync, () => CanGoToPreviousMembersPage);
        NextMembersPageCommand = new AsyncRelayCommand(NextMembersPageAsync, () => CanGoToNextMembersPage);
        InviteCommand = new AsyncRelayCommand(InviteAsync, () => CanManageInvites && !IsBusy && LooksLikeUserId(InviteUserId));
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => IsConfirmOpen && !IsBusy);
        CancelConfirmCommand = new RelayCommand(CancelConfirm);
        CloseCommand = new RelayCommand(Close);
    }

    /// <summary>Raised with (userId, displayName, avatarUrl) so a row can open a profile.</summary>
    public Action<string, string, string>? OpenProfileRequested { get; set; }

    // ------------------------------------------------------------- state

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetProperty(ref _isOpen, value))
            {
                RaiseAll();
            }
        }
    }

    public string GroupName => _group?.Name ?? string.Empty;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => _statusMessage.Length > 0;

    public ObservableCollection<ModerationMemberRow> Members
    {
        get => _members;
        private set => SetProperty(ref _members, value);
    }

    public ObservableCollection<ModerationEntryRow> Bans
    {
        get => _bans;
        private set => SetProperty(ref _bans, value);
    }

    public ObservableCollection<ModerationEntryRow> Requests
    {
        get => _requests;
        private set => SetProperty(ref _requests, value);
    }

    public ObservableCollection<ModerationEntryRow> Invites
    {
        get => _invites;
        private set => SetProperty(ref _invites, value);
    }

    public ObservableCollection<ModerationRoleToggle> Roles
    {
        get => _roles;
        private set => SetProperty(ref _roles, value);
    }

    public ObservableCollection<ModerationLogRow> Log
    {
        get => _log;
        private set => SetProperty(ref _log, value);
    }

    public ModerationMemberRow? SelectedMember
    {
        get => _selectedMember;
        private set
        {
            if (SetProperty(ref _selectedMember, value))
            {
                OnPropertyChanged(nameof(HasSelectedMember));
                OnPropertyChanged(nameof(SelectedMemberName));
                OnPropertyChanged(nameof(SelectedMemberText));
                RefreshRoleAssignment();
            }
        }
    }

    public bool HasSelectedMember => _selectedMember is not null;

    public string SelectedMemberName => _selectedMember?.DisplayName ?? string.Empty;

    public string SelectedMemberText => _selectedMember is null
        ? "Pick a member to kick, ban or change their roles."
        : _selectedMember.IdText;

    public string InviteUserId
    {
        get => _inviteUserId;
        set
        {
            if (SetProperty(ref _inviteUserId, value))
            {
                InviteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // ------------------------------------------------------------- tabs

    public string SelectedTab => _selectedTab;

    public bool ShowMembers => IsTab(MembersTab);

    public bool ShowBans => IsTab(BansTab);

    public bool ShowRequests => IsTab(RequestsTab);

    public bool ShowInvites => IsTab(InvitesTab);

    public bool ShowLog => IsTab(LogTab);

    public bool ShowDenied => IsOpen && !HasModerationPermission;

    private bool IsTab(string tab) => IsOpen && string.Equals(_selectedTab, tab, StringComparison.Ordinal);

    public string MembersTabLabel => $"{MembersTab} ({Members.Count:N0})";

    public string BansTabLabel => $"Bans ({Bans.Count:N0})";

    public string RequestsTabLabel => $"Requests ({Requests.Count:N0})";

    public string InvitesTabLabel => $"Invites ({Invites.Count:N0})";

    // ------------------------------------------------------- permissions

    /// <summary>True when the account has any permission this panel can use.</summary>
    public bool HasModerationPermission => CanKick || CanBan || CanAssignRoles
        || CanReviewRequests || CanManageInvites || CanViewLog;

    public bool CanKick => HasPermission("group-members-remove");

    public bool CanBan => HasPermission("group-bans-manage");

    public bool CanAssignRoles => HasPermission("group-roles-assign");

    public bool CanReviewRequests => HasPermission("group-members-manage")
        || HasPermission("group-members-remove");

    public bool CanManageInvites => HasPermission("group-invites-manage");

    public bool CanViewLog => HasPermission("group-audit-view");

    public string PermissionText
    {
        get
        {
            var labels = new List<string>();
            if (CanKick) labels.Add("kick");
            if (CanBan) labels.Add("ban");
            if (CanAssignRoles) labels.Add("roles");
            if (CanReviewRequests) labels.Add("requests");
            if (CanManageInvites) labels.Add("invites");
            if (CanViewLog) labels.Add("audit log");
            return labels.Count == 0
                ? "VRChat lists no moderation permissions for this account on this group."
                : $"This account can {string.Join(", ", labels)} here.";
        }
    }

    /// <summary>
    /// Reads myMember.permissions, falling back to the permissions of the account's roles —
    /// the same two sources VRCX-0 uses because VRChat sometimes only fills one.
    /// </summary>
    private bool HasPermission(string permission)
    {
        if (_group?.MyMember is not { } member)
        {
            return false;
        }

        if (member.Permissions is { Count: > 0 })
        {
            if (member.Permissions.Contains("*") || member.Permissions.Contains(permission))
            {
                return true;
            }
        }

        if (member.RoleIds is { Count: > 0 })
        {
            foreach (var roleId in member.RoleIds)
            {
                var role = _group.Roles.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, roleId, StringComparison.Ordinal));
                if (role is not null &&
                    (role.Permissions.Contains("*") || role.Permissions.Contains(permission)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // -------------------------------------------------------- confirmation

    public bool IsConfirmOpen => _pendingAction.Length > 0;

    public string PendingAction => _pendingAction;

    public void CancelConfirm()
    {
        _pendingAction = string.Empty;
        _pendingWork = null;
        OnPropertyChanged(nameof(PendingAction));
        OnPropertyChanged(nameof(IsConfirmOpen));
        RaiseCommandStates();
    }

    private void RequestConfirm(string description, Func<Task> work)
    {
        _pendingAction = description;
        _pendingWork = work;
        OnPropertyChanged(nameof(PendingAction));
        OnPropertyChanged(nameof(IsConfirmOpen));
        RaiseCommandStates();
    }

    internal async Task ConfirmAsync()
    {
        var work = _pendingWork;
        CancelConfirm();
        if (work is not null)
        {
            await work();
        }
    }

    // ------------------------------------------------------------- paging

    public int MemberPageCount
    {
        get
        {
            var total = _group?.MemberCount ?? 0;
            return total <= 0 ? 1 : (total + PageSize - 1) / PageSize;
        }
    }

    public string MembersPageText => IsBusy && Members.Count == 0
        ? "Loading members…"
        : string.Create(CultureInfo.InvariantCulture, $"Page {_memberPage + 1:N0} of {MemberPageCount:N0}");

    public bool CanGoToPreviousMembersPage => IsOpen && !IsBusy && _memberPage > 0;

    public bool CanGoToNextMembersPage => IsOpen && !IsBusy
        && Members.Count > 0
        && _memberPage + 1 < MemberPageCount;

    // ----------------------------------------------------------- commands

    public RelayCommand<string> SelectTabCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand PreviousMembersPageCommand { get; }

    public AsyncRelayCommand NextMembersPageCommand { get; }

    public AsyncRelayCommand InviteCommand { get; }

    public AsyncRelayCommand ConfirmCommand { get; }

    public RelayCommand CancelConfirmCommand { get; }

    public RelayCommand CloseCommand { get; }

    // ------------------------------------------------------------ opening

    public void Open(Group group)
    {
        _group = group;
        _selectedTab = MembersTab;
        _memberPage = 0;
        _bansLoaded = false;
        _requestsLoaded = false;
        _invitesLoaded = false;
        _logLoaded = false;
        Members = new ObservableCollection<ModerationMemberRow>();
        Bans = new ObservableCollection<ModerationEntryRow>();
        Requests = new ObservableCollection<ModerationEntryRow>();
        Invites = new ObservableCollection<ModerationEntryRow>();
        Roles = new ObservableCollection<ModerationRoleToggle>();
        Log = new ObservableCollection<ModerationLogRow>();
        SelectedMember = null;
        InviteUserId = string.Empty;
        StatusMessage = string.Empty;
        CancelConfirm();
        IsOpen = true;
        RaiseAll();

        if (HasModerationPermission)
        {
            LoadingTask = LoadInitialAsync();
        }
        else
        {
            StatusMessage = PermissionText;
        }
    }

    /// <summary>The loads <see cref="Open"/> starts; awaiting it settles the panel.</summary>
    internal Task LoadingTask { get; private set; } = Task.CompletedTask;

    /// <summary>Lets tests wait for a lazily loaded tab.</summary>
    internal Task LoadTabAsync(string tab) => EnsureTabLoadedAsync(tab);

    private async Task LoadInitialAsync()
    {
        await LoadRolesAsync();
        await LoadMembersAsync();
    }

    public void Close()
    {
        CancelConfirm();
        IsOpen = false;
        _group = null;
        SelectedMember = null;
        StatusMessage = string.Empty;
        RaiseAll();
    }

    private async Task RefreshAsync()
    {
        _bansLoaded = false;
        _requestsLoaded = false;
        _invitesLoaded = false;
        _logLoaded = false;
        await LoadRolesAsync();
        await LoadMembersAsync();
        await EnsureTabLoadedAsync(_selectedTab);
    }

    private void SelectTab(string? tab)
    {
        if (string.IsNullOrWhiteSpace(tab) ||
            string.Equals(tab, _selectedTab, StringComparison.Ordinal))
        {
            return;
        }

        _selectedTab = tab;
        OnPropertyChanged(nameof(SelectedTab));
        RaiseTabFlags();
        _ = EnsureTabLoadedAsync(tab);
    }

    private async Task EnsureTabLoadedAsync(string tab)
    {
        if (!IsOpen || !HasModerationPermission)
        {
            return;
        }

        if (string.Equals(tab, BansTab, StringComparison.Ordinal) && !_bansLoaded)
        {
            await LoadBansAsync();
        }
        else if (string.Equals(tab, RequestsTab, StringComparison.Ordinal) && !_requestsLoaded)
        {
            await LoadRequestsAsync();
        }
        else if (string.Equals(tab, InvitesTab, StringComparison.Ordinal) && !_invitesLoaded)
        {
            await LoadInvitesAsync();
        }
        else if (string.Equals(tab, LogTab, StringComparison.Ordinal) && !_logLoaded)
        {
            await LoadLogAsync();
        }
    }

    // -------------------------------------------------------------- loads

    private async Task LoadMembersAsync()
    {
        if (_group is not { } group)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var members = await _api.GetGroupMembersAsync(group.EffectiveId, _memberPage * PageSize);
            if (!IsOpen || _group?.EffectiveId != group.EffectiveId)
            {
                return;
            }

            Members = new ObservableCollection<ModerationMemberRow>(
                members.Select(member => new ModerationMemberRow(member, this)));
            StatusMessage = Members.Count == 0 ? "VRChat returned no members for this page." : string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
            RaiseMemberFlags();
        }
    }

    private async Task LoadRolesAsync()
    {
        if (_group is not { } group)
        {
            return;
        }

        try
        {
            var roles = await _api.GetGroupRolesAsync(group.EffectiveId);
            if (!IsOpen)
            {
                return;
            }

            Roles = new ObservableCollection<ModerationRoleToggle>(
                roles.Select(role => new ModerationRoleToggle(role, this)));
            RefreshRoleAssignment();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            if (_group?.Roles is { Count: > 0 } groupRoles)
            {
                Roles = new ObservableCollection<ModerationRoleToggle>(
                    groupRoles.Select(role => new ModerationRoleToggle(role, this)));
                RefreshRoleAssignment();
            }
        }
    }

    private async Task LoadBansAsync()
    {
        if (_group is not { } group)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var bans = await _api.GetGroupBansAsync(group.EffectiveId);
            if (!IsOpen)
            {
                return;
            }

            Bans = new ObservableCollection<ModerationEntryRow>(
                bans.Select(entry => new ModerationEntryRow(entry, ModerationEntryKind.Ban, this)));
            _bansLoaded = true;
            RaiseTabFlags();
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadRequestsAsync()
    {
        if (_group is not { } group)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var requests = await _api.GetGroupJoinRequestsAsync(group.EffectiveId);
            if (!IsOpen)
            {
                return;
            }

            Requests = new ObservableCollection<ModerationEntryRow>(
                requests.Select(entry => new ModerationEntryRow(entry, ModerationEntryKind.JoinRequest, this)));
            _requestsLoaded = true;
            StatusMessage = Requests.Count == 0 ? "No join requests are waiting." : string.Empty;
            RaiseTabFlags();
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadInvitesAsync()
    {
        if (_group is not { } group)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var invites = await _api.GetGroupInvitesAsync(group.EffectiveId);
            if (!IsOpen)
            {
                return;
            }

            Invites = new ObservableCollection<ModerationEntryRow>(
                invites.Select(entry => new ModerationEntryRow(entry, ModerationEntryKind.Invite, this)));
            _invitesLoaded = true;
            RaiseTabFlags();
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadLogAsync()
    {
        if (_group is not { } group)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var entries = await _api.GetGroupAuditLogsAsync(group.EffectiveId);
            if (!IsOpen)
            {
                return;
            }

            Log = new ObservableCollection<ModerationLogRow>(entries.Select(entry => new ModerationLogRow(entry)));
            _logLoaded = true;
            StatusMessage = Log.Count == 0 ? "The audit log is empty." : string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task PreviousMembersPageAsync() => CanGoToPreviousMembersPage
        ? JumpToMembersPageAsync(_memberPage - 1)
        : Task.CompletedTask;

    private Task NextMembersPageAsync() => CanGoToNextMembersPage
        ? JumpToMembersPageAsync(_memberPage + 1)
        : Task.CompletedTask;

    private async Task JumpToMembersPageAsync(int page)
    {
        _memberPage = Math.Max(0, page);
        RaiseMemberFlags();
        await LoadMembersAsync();
    }

    // ------------------------------------------------------------ actions

    public void SelectMember(ModerationMemberRow row) => SelectedMember = row;

    public void OpenProfileOf(ModerationMemberRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.UserId))
        {
            OpenProfileRequested?.Invoke(row.UserId, row.DisplayName, row.AvatarUrl);
        }
    }

    public void OpenProfileOf(ModerationEntryRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.UserId))
        {
            OpenProfileRequested?.Invoke(row.UserId, row.DisplayName, row.AvatarUrl);
        }
    }

    public void RequestKick(ModerationMemberRow row)
    {
        if (!CanKick || _group is null)
        {
            StatusMessage = "This account cannot remove members from this group.";
            return;
        }

        RequestConfirm($"Kick {row.DisplayName} from this group?", () => KickAsync(row));
    }

    public void RequestBan(ModerationMemberRow row)
    {
        if (!CanBan || _group is null)
        {
            StatusMessage = "This account cannot ban members from this group.";
            return;
        }

        RequestConfirm($"Ban {row.DisplayName} from this group and its instances?", () => BanAsync(row));
    }

    public void RequestUnban(ModerationEntryRow row)
    {
        if (!CanBan || _group is null)
        {
            StatusMessage = "This account cannot manage bans on this group.";
            return;
        }

        RequestConfirm($"Unban {row.DisplayName}?", () => UnbanAsync(row));
    }

    public void RequestJoinResponse(ModerationEntryRow row, bool accept, bool block)
    {
        if (!CanReviewRequests || _group is null)
        {
            StatusMessage = "This account cannot answer join requests for this group.";
            return;
        }

        var question = accept
            ? $"Accept {row.DisplayName} into the group?"
            : block
                ? $"Reject and block {row.DisplayName}?"
                : $"Reject {row.DisplayName}'s request?";
        RequestConfirm(question, () => RespondAsync(row, accept, block));
    }

    public void RequestCancelInvite(ModerationEntryRow row)
    {
        if (!CanManageInvites || _group is null)
        {
            StatusMessage = "This account cannot manage invites for this group.";
            return;
        }

        RequestConfirm($"Cancel the invite for {row.DisplayName}?", () => CancelInviteAsync(row));
    }

    private async Task KickAsync(ModerationMemberRow row)
    {
        if (_group is not { } group)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _api.KickGroupMemberAsync(group.EffectiveId, row.UserId);
            Members.Remove(row);
            SelectedMember = null;
            StatusMessage = $"{row.DisplayName} was removed from the group.";
            RaiseTabFlags();
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BanAsync(ModerationMemberRow row)
    {
        if (_group is not { } group)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _api.BanGroupMemberAsync(group.EffectiveId, row.UserId);
            Members.Remove(row);
            SelectedMember = null;
            _bansLoaded = false;
            StatusMessage = $"{row.DisplayName} was banned from the group.";
            RaiseTabFlags();
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UnbanAsync(ModerationEntryRow row)
    {
        if (_group is not { } group)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _api.UnbanGroupMemberAsync(group.EffectiveId, row.UserId);
            Bans.Remove(row);
            StatusMessage = $"{row.DisplayName} was unbanned.";
            RaiseTabFlags();
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RespondAsync(ModerationEntryRow row, bool accept, bool block)
    {
        if (_group is not { } group)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _api.RespondGroupJoinRequestAsync(group.EffectiveId, row.UserId, accept, block);
            Requests.Remove(row);
            StatusMessage = accept
                ? $"{row.DisplayName} joined the group."
                : $"{row.DisplayName}'s request was {(block ? "rejected and blocked" : "rejected")}.";
            RaiseTabFlags();
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CancelInviteAsync(ModerationEntryRow row)
    {
        if (_group is not { } group)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _api.DeleteGroupInviteAsync(group.EffectiveId, row.UserId);
            Invites.Remove(row);
            StatusMessage = $"The invite for {row.DisplayName} was cancelled.";
            RaiseTabFlags();
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task InviteAsync()
    {
        if (_group is not { } group)
        {
            return;
        }

        var userId = InviteUserId.Trim();
        if (!LooksLikeUserId(userId))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _api.InviteToGroupAsync(group.EffectiveId, userId);
            StatusMessage = "Invite sent.";
            InviteUserId = string.Empty;
            _invitesLoaded = false;
            await LoadInvitesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ToggleRoleAsync(ModerationRoleToggle toggle)
    {
        if (_group is not { } group || SelectedMember is not { } member)
        {
            StatusMessage = "Pick a member before changing roles.";
            return;
        }

        if (!CanAssignRoles)
        {
            StatusMessage = "This account cannot assign roles on this group.";
            return;
        }

        IsBusy = true;
        try
        {
            if (toggle.IsAssigned)
            {
                await _api.RemoveGroupMemberRoleAsync(group.EffectiveId, member.UserId, toggle.RoleId);
                member.RemoveRole(toggle.RoleId);
                toggle.IsAssigned = false;
                StatusMessage = $"{toggle.Name} removed from {member.DisplayName}.";
            }
            else
            {
                await _api.AddGroupMemberRoleAsync(group.EffectiveId, member.UserId, toggle.RoleId);
                member.AddRole(toggle.RoleId);
                toggle.IsAssigned = true;
                StatusMessage = $"{toggle.Name} added to {member.DisplayName}.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshRoleAssignment()
    {
        var assigned = SelectedMember?.RoleIds ?? new List<string>();
        foreach (var toggle in Roles)
        {
            toggle.IsAssigned = assigned.Contains(toggle.RoleId, StringComparer.Ordinal);
            toggle.RaiseStateChanged();
        }
    }

    // ------------------------------------------------------------ helpers

    private static bool LooksLikeUserId(string? text)
        => !string.IsNullOrWhiteSpace(text) && text.Trim().StartsWith("usr_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns API failures into a sentence a moderator can act on.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        VrcApiException api when api.StatusCode is 401 or 403 =>
            "VRChat does not allow this account to do that on this group.",
        VrcApiException api when api.StatusCode == 404 => "VRChat could not find that member or request.",
        VrcApiException api => api.Message,
        HttpRequestException => "Could not reach VRChat. Check your internet connection.",
        TaskCanceledException => "The request timed out. Try again.",
        _ => "Something went wrong. Try again."
    };

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(HasModerationPermission));
        OnPropertyChanged(nameof(PermissionText));
        OnPropertyChanged(nameof(ShowDenied));
        OnPropertyChanged(nameof(SelectedMember));
        OnPropertyChanged(nameof(HasSelectedMember));
        RaiseTabFlags();
        RaiseMemberFlags();
        RaiseCommandStates();
    }

    private void RaiseTabFlags()
    {
        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(ShowMembers));
        OnPropertyChanged(nameof(ShowBans));
        OnPropertyChanged(nameof(ShowRequests));
        OnPropertyChanged(nameof(ShowInvites));
        OnPropertyChanged(nameof(ShowLog));
        OnPropertyChanged(nameof(MembersTabLabel));
        OnPropertyChanged(nameof(BansTabLabel));
        OnPropertyChanged(nameof(RequestsTabLabel));
        OnPropertyChanged(nameof(InvitesTabLabel));
    }

    private void RaiseMemberFlags()
    {
        OnPropertyChanged(nameof(MembersPageText));
        OnPropertyChanged(nameof(MemberPageCount));
        OnPropertyChanged(nameof(CanGoToPreviousMembersPage));
        OnPropertyChanged(nameof(CanGoToNextMembersPage));
        OnPropertyChanged(nameof(MembersTabLabel));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        PreviousMembersPageCommand.RaiseCanExecuteChanged();
        NextMembersPageCommand.RaiseCanExecuteChanged();
        InviteCommand.RaiseCanExecuteChanged();
        ConfirmCommand.RaiseCanExecuteChanged();
    }
}

public enum ModerationEntryKind
{
    Ban,
    JoinRequest,
    Invite
}

/// <summary>One roster row in the moderation panel: pick it to moderate, or act straight away.</summary>
public sealed class ModerationMemberRow : ViewModelBase
{
    private readonly GroupModerationViewModel _owner;

    public ModerationMemberRow(GroupMember member, GroupModerationViewModel owner)
    {
        _owner = owner;
        UserId = member.EffectiveUserId;
        var name = member.EffectiveUserName.Trim();
        DisplayName = name.Length > 0 ? name : "Member without a visible name";
        AvatarUrl = member.EffectiveAvatarUrl;
        RoleIds = member.RoleIds?.ToList() ?? new List<string>();
        SelectCommand = new RelayCommand(() => _owner.SelectMember(this));
        KickCommand = new RelayCommand(() => _owner.RequestKick(this), () => owner.CanKick);
        BanCommand = new RelayCommand(() => _owner.RequestBan(this), () => owner.CanBan);
        OpenProfileCommand = new RelayCommand(() => _owner.OpenProfileOf(this));
    }

    public string UserId { get; }

    public string DisplayName { get; }

    public string AvatarUrl { get; }

    public string IdText => UserId;

    public List<string> RoleIds { get; }

    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);

    public string Initial
    {
        get
        {
            var name = DisplayName.Trim();
            return name.Length == 0
                ? "?"
                : new StringInfo(name).SubstringByTextElements(0, 1).ToUpperInvariant();
        }
    }

    public RelayCommand SelectCommand { get; }

    public RelayCommand KickCommand { get; }

    public RelayCommand BanCommand { get; }

    public RelayCommand OpenProfileCommand { get; }

    internal void AddRole(string roleId)
    {
        if (!RoleIds.Contains(roleId, StringComparer.Ordinal))
        {
            RoleIds.Add(roleId);
        }
    }

    internal void RemoveRole(string roleId) => RoleIds.RemoveAll(id => string.Equals(id, roleId, StringComparison.Ordinal));
}

/// <summary>A ban, join request or invite row with the action that belongs to it.</summary>
public sealed class ModerationEntryRow : ViewModelBase
{
    public ModerationEntryRow(GroupRosterEntry entry, ModerationEntryKind kind, GroupModerationViewModel owner)
    {
        Kind = kind;
        UserId = entry.EffectiveUserId;
        var name = entry.EffectiveUserName.Trim();
        DisplayName = name.Length > 0 ? name : "Member without a visible name";
        AvatarUrl = entry.EffectiveAvatarUrl;
        Reason = entry.Reason;
        Detail = DescribeEntry(entry, kind);
        OpenProfileCommand = new RelayCommand(() => owner.OpenProfileOf(this));
        UnbanCommand = new RelayCommand(() => owner.RequestUnban(this), () => owner.CanBan);
        AcceptCommand = new RelayCommand(() => owner.RequestJoinResponse(this, true, false), () => owner.CanReviewRequests);
        RejectCommand = new RelayCommand(() => owner.RequestJoinResponse(this, false, false), () => owner.CanReviewRequests);
        BlockCommand = new RelayCommand(() => owner.RequestJoinResponse(this, false, true), () => owner.CanReviewRequests);
        CancelInviteCommand = new RelayCommand(() => owner.RequestCancelInvite(this), () => owner.CanManageInvites);
    }

    public ModerationEntryKind Kind { get; }

    public bool IsBan => Kind == ModerationEntryKind.Ban;

    public bool IsRequest => Kind == ModerationEntryKind.JoinRequest;

    public bool IsInvite => Kind == ModerationEntryKind.Invite;

    public string UserId { get; }

    public string DisplayName { get; }

    public string AvatarUrl { get; }

    public string Reason { get; }

    public string Detail { get; }

    public string Initial
    {
        get
        {
            var name = DisplayName.Trim();
            return name.Length == 0
                ? "?"
                : new StringInfo(name).SubstringByTextElements(0, 1).ToUpperInvariant();
        }
    }

    public RelayCommand OpenProfileCommand { get; }

    public RelayCommand UnbanCommand { get; }

    public RelayCommand AcceptCommand { get; }

    public RelayCommand RejectCommand { get; }

    public RelayCommand BlockCommand { get; }

    public RelayCommand CancelInviteCommand { get; }

    private static string DescribeEntry(GroupRosterEntry entry, ModerationEntryKind kind)
    {
        var when = kind switch
        {
            ModerationEntryKind.Ban => entry.CreatedAt,
            ModerationEntryKind.JoinRequest => entry.RequestCreatedAt == default ? entry.CreatedAt : entry.RequestCreatedAt,
            _ => entry.CreatedAt
        };

        var parts = new List<string>();
        if (when != default)
        {
            parts.Add(when.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(entry.Reason))
        {
            parts.Add($"“{entry.Reason}”");
        }

        if (!string.IsNullOrWhiteSpace(entry.ActorDisplayName))
        {
            parts.Add($"by {entry.ActorDisplayName}");
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>One role the selected member can be given or taken out of.</summary>
public sealed class ModerationRoleToggle : ViewModelBase
{
    private bool _isAssigned;

    public ModerationRoleToggle(GroupRole role, GroupModerationViewModel owner)
    {
        Role = role;
        ToggleCommand = new RelayCommand(() => _ = owner.ToggleRoleAsync(this));
    }

    public GroupRole Role { get; }

    public string RoleId => Role.Id;

    public string Name => Role.Name;

    public string MemberCountText => $"{Role.MemberCount:N0} members";

    public string ActionText => _isAssigned ? "Remove" : "Assign";

    public bool IsAssigned
    {
        get => _isAssigned;
        set
        {
            if (SetProperty(ref _isAssigned, value))
            {
                OnPropertyChanged(nameof(ActionText));
            }
        }
    }

    public RelayCommand ToggleCommand { get; }

    public void RaiseStateChanged() => OnPropertyChanged(nameof(ActionText));
}

/// <summary>One audit-log line.</summary>
public sealed class ModerationLogRow
{
    public ModerationLogRow(GroupAuditEntry entry)
    {
        When = entry.CreatedAt == default
            ? string.Empty
            : entry.CreatedAt.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
        Actor = entry.ActorDisplayName;
        EventType = entry.EventType;
        Description = string.IsNullOrWhiteSpace(entry.Description) ? entry.EventType : entry.Description;
    }

    public string When { get; }

    public string Actor { get; }

    public string EventType { get; }

    public string Description { get; }
}
