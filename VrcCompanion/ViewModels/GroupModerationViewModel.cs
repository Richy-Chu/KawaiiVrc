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
    public const string RolesTab = "Roles";
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
    private int _memberTotal;
    private string? _editingRoleId;
    private string _roleEditorName = string.Empty;
    private string _roleEditorDescription = string.Empty;
    private bool _roleEditorAddedOnJoin;
    private bool _roleEditorSelfAssignable;
    private bool _isRoleEditorOpen;
    private bool _permissionsLoaded;
    private ObservableCollection<RolePermissionOption> _rolePermissions = new();
    private bool _isMemberCardOpen;
    private string _cardManagerNotes = string.Empty;
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
        StartNewRoleCommand = new RelayCommand(StartNewRole, () => CanManageRoles);
        SaveRoleCommand = new AsyncRelayCommand(
            SaveRoleAsync,
            () => CanManageRoles && !IsBusy && RoleEditorName.Trim().Length > 0);
        CloseRoleEditorCommand = new RelayCommand(CloseRoleEditor);
        CloseMemberCardCommand = new RelayCommand(CloseMemberCard, () => IsMemberCardOpen);
        SaveMemberSettingsCommand = new AsyncRelayCommand(
            SaveMemberSettingsAsync,
            () => CanManageMemberData && !IsBusy && IsMemberCardOpen);
    }

    /// <summary>Raised with (userId, displayName, avatarUrl) so a row can open a profile.</summary>
    public Action<string, string, string>? OpenProfileRequested { get; set; }

    /// <summary>Raised after a kick or ban so the group popup can drop its member count too.</summary>
    public event Action? MemberCountChanged;

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
                OnPropertyChanged(nameof(CardVisibilityText));
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

    public bool ShowRoles => IsTab(RolesTab);

    public bool ShowLog => IsTab(LogTab);

    public bool ShowDenied => IsOpen && !HasModerationPermission;

    private bool IsTab(string tab) => IsOpen && string.Equals(_selectedTab, tab, StringComparison.Ordinal);

    public string MembersTabLabel => $"{MembersTab} ({Members.Count:N0})";

    public string BansTabLabel => $"Bans ({Bans.Count:N0})";

    public string RequestsTabLabel => $"Requests ({Requests.Count:N0})";

    public string InvitesTabLabel => $"Invites ({Invites.Count:N0})";

    public string RolesTabLabel => $"Roles ({Roles.Count:N0})";

    public string MemberTotalText => string.Create(CultureInfo.InvariantCulture, $"{MemberTotal:N0} members");

    // ------------------------------------------------------- permissions

    /// <summary>True when the account has any permission this panel can use.</summary>
    public bool HasModerationPermission => CanKick || CanBan || CanAssignRoles
        || CanReviewRequests || CanManageInvites || CanViewLog || CanManageRoles;

    public bool CanManageRoles => HasPermission("group-roles-manage");

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

    /// <summary>Members VRChat reports, kept in step with kicks and bans done here.</summary>
    public int MemberTotal
    {
        get => _memberTotal;
        private set
        {
            if (SetProperty(ref _memberTotal, value))
            {
                OnPropertyChanged(nameof(MemberTotalText));
                OnPropertyChanged(nameof(MemberPageCount));
                OnPropertyChanged(nameof(MembersPageText));
                OnPropertyChanged(nameof(CanGoToNextMembersPage));
            }
        }
    }

    public int MemberPageCount
    {
        get => _memberTotal <= 0 ? 1 : (_memberTotal + PageSize - 1) / PageSize;
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
        MemberTotal = group.MemberCount;
        _permissionsLoaded = false;
        _editingRoleId = null;
        IsRoleEditorOpen = false;
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
        else if (string.Equals(tab, RolesTab, StringComparison.Ordinal))
        {
            await LoadPermissionOptionsAsync();
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

        // Rows show their role names, so they have to be told once the roles exist.
        foreach (var row in Members)
        {
            row.NotifyRoleText();
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

    /// <summary>
    /// Opens the member card — the per-member moderation popup, the way VRCX opens its member
    /// moderation dialog instead of pouring the tools into the roster list.
    /// </summary>
    public void OpenMemberCard(ModerationMemberRow row)
    {
        SelectedMember = row;
        CardManagerNotes = row.ManagerNotes;
        IsMemberCardOpen = true;
        if (Roles.Count == 0)
        {
            _ = LoadRolesAsync();
        }

        if (!_permissionsLoaded)
        {
            _ = LoadPermissionOptionsAsync();
        }
    }

    public void CloseMemberCard()
    {
        IsMemberCardOpen = false;
        SelectedMember = null;
    }

    // ------------------------------------------------------- member card

    public bool IsMemberCardOpen
    {
        get => _isMemberCardOpen;
        private set
        {
            if (SetProperty(ref _isMemberCardOpen, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string CardManagerNotes
    {
        get => _cardManagerNotes;
        set => SetProperty(ref _cardManagerNotes, value);
    }

    public string CardVisibilityText => SelectedMember switch
    {
        null => string.Empty,
        { Visibility: { Length: > 0 } visibility } => $"Profile visibility: {visibility}",
        _ => string.Empty
    };

    public bool CanManageMemberData => HasPermission("group-members-manage");

    public RelayCommand CloseMemberCardCommand { get; }

    public AsyncRelayCommand SaveMemberSettingsCommand { get; }

    internal async Task SaveMemberSettingsAsync()
    {
        if (_group is not { } group || SelectedMember is not { } member)
        {
            return;
        }

        if (!CanManageMemberData)
        {
            StatusMessage = "This account cannot manage member data on this group.";
            return;
        }

        IsBusy = true;
        try
        {
            var notes = CardManagerNotes.Trim();
            await _api.UpdateGroupMemberAsync(group.EffectiveId, member.UserId, notes);
            member.SetSettings(notes);
            StatusMessage = $"Saved notes for {member.DisplayName}.";
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
            CloseMemberCard();
            AfterMemberRemoved();
            await BackToLastPageIfEmptyAsync();
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
            CloseMemberCard();
            AfterMemberRemoved();
            await BackToLastPageIfEmptyAsync();
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

    // ------------------------------------------------------- role editing

    /// <summary>Every permission a role can carry, with a readable label.</summary>
    public ObservableCollection<RolePermissionOption> RolePermissions
    {
        get => _rolePermissions;
        private set => SetProperty(ref _rolePermissions, value);
    }

    public bool IsRoleEditorOpen
    {
        get => _isRoleEditorOpen;
        private set
        {
            if (SetProperty(ref _isRoleEditorOpen, value))
            {
                OnPropertyChanged(nameof(RoleEditorTitle));
                OnPropertyChanged(nameof(RoleEditorActionText));
            }
        }
    }

    public string RoleEditorTitle => _editingRoleId is null ? "New role" : "Edit role";

    public string RoleEditorActionText => _editingRoleId is null ? "Create role" : "Save role";

    public string RoleEditorName
    {
        get => _roleEditorName;
        set
        {
            if (SetProperty(ref _roleEditorName, value))
            {
                SaveRoleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RoleEditorDescription
    {
        get => _roleEditorDescription;
        set => SetProperty(ref _roleEditorDescription, value);
    }

    public bool RoleEditorAddedOnJoin
    {
        get => _roleEditorAddedOnJoin;
        set => SetProperty(ref _roleEditorAddedOnJoin, value);
    }

    public bool RoleEditorSelfAssignable
    {
        get => _roleEditorSelfAssignable;
        set => SetProperty(ref _roleEditorSelfAssignable, value);
    }

    public RelayCommand StartNewRoleCommand { get; }

    public AsyncRelayCommand SaveRoleCommand { get; }

    public RelayCommand CloseRoleEditorCommand { get; }

    public void StartNewRole()
    {
        if (!CanManageRoles)
        {
            StatusMessage = "This account cannot manage roles on this group.";
            return;
        }

        _editingRoleId = null;
        RoleEditorName = string.Empty;
        RoleEditorDescription = string.Empty;
        RoleEditorAddedOnJoin = false;
        RoleEditorSelfAssignable = false;
        ResetPermissionOptions(null);
        IsRoleEditorOpen = true;
        OnPropertyChanged(nameof(RoleEditorTitle));
        OnPropertyChanged(nameof(RoleEditorActionText));
    }

    public void EditRole(ModerationRoleToggle toggle)
    {
        if (!CanManageRoles)
        {
            StatusMessage = "This account cannot manage roles on this group.";
            return;
        }

        _editingRoleId = toggle.RoleId;
        RoleEditorName = toggle.Name;
        RoleEditorDescription = toggle.Description;
        RoleEditorAddedOnJoin = toggle.Role.IsAddedOnJoin;
        RoleEditorSelfAssignable = toggle.Role.IsSelfAssignable;
        ResetPermissionOptions(toggle.Role.Permissions);
        IsRoleEditorOpen = true;
        OnPropertyChanged(nameof(RoleEditorTitle));
        OnPropertyChanged(nameof(RoleEditorActionText));
        SaveRoleCommand.RaiseCanExecuteChanged();
    }

    private void CloseRoleEditor()
    {
        IsRoleEditorOpen = false;
        _editingRoleId = null;
    }

    /// <summary>Creates or updates the role being edited, then reloads the role list.</summary>
    internal async Task SaveRoleAsync()
    {
        if (_group is not { } group)
        {
            return;
        }

        if (!CanManageRoles)
        {
            StatusMessage = "This account cannot manage roles on this group.";
            return;
        }

        var name = RoleEditorName.Trim();
        if (name.Length == 0)
        {
            StatusMessage = "A role needs a name.";
            return;
        }

        var permissions = RolePermissions
            .Where(option => option.IsChecked)
            .Select(option => option.Key)
            .ToList();
        var description = RoleEditorDescription.Trim();
        var editing = _editingRoleId;
        IsBusy = true;
        try
        {
            if (editing is null)
            {
                await _api.CreateGroupRoleAsync(
                    group.EffectiveId,
                    name,
                    description,
                    permissions,
                    RoleEditorAddedOnJoin,
                    RoleEditorSelfAssignable);
            }
            else
            {
                await _api.UpdateGroupRoleAsync(
                    group.EffectiveId,
                    editing,
                    name,
                    description,
                    permissions,
                    RoleEditorAddedOnJoin,
                    RoleEditorSelfAssignable);
            }

            IsRoleEditorOpen = false;
            _editingRoleId = null;
            StatusMessage = editing is null ? $"Role “{name}” created." : $"Role “{name}” saved.";
            await LoadRolesAsync();
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

    public void RequestDeleteRole(ModerationRoleToggle toggle)
    {
        if (_group is not { } group || !CanManageRoles)
        {
            StatusMessage = "This account cannot manage roles on this group.";
            return;
        }

        if (!toggle.CanDeleteRole)
        {
            StatusMessage = "The Everyone role exists in every group and cannot be deleted.";
            return;
        }

        RequestConfirm($"Delete the role “{toggle.Name}”?", async () =>
        {
            IsBusy = true;
            try
            {
                await _api.DeleteGroupRoleAsync(group.EffectiveId, toggle.RoleId);
                StatusMessage = $"Role “{toggle.Name}” deleted.";
                await LoadRolesAsync();
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
        });
    }

    /// <summary>Reads VRChat's permission catalogue once, so role toggles match the group.</summary>
    private async Task LoadPermissionOptionsAsync()
    {
        if (_permissionsLoaded || _group is null)
        {
            return;
        }

        IReadOnlyList<string> keys;
        try
        {
            keys = await _api.GetGroupPermissionsAsync(_group.EffectiveId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            keys = Array.Empty<string>();
        }

        if (keys.Count == 0)
        {
            keys = KnownPermissions.Select(permission => permission.Key).ToList();
        }

        var checkedKeys = RolePermissions
            .Where(option => option.IsChecked)
            .Select(option => option.Key)
            .ToHashSet(StringComparer.Ordinal);
        RolePermissions = new ObservableCollection<RolePermissionOption>(
            keys.Where(key => key != "*")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(PermissionLabel, StringComparer.OrdinalIgnoreCase)
                .Select(key => new RolePermissionOption(key, PermissionLabel(key))));
        foreach (var option in RolePermissions)
        {
            option.IsChecked = checkedKeys.Contains(option.Key);
        }

        _permissionsLoaded = true;
    }

    private void ResetPermissionOptions(IEnumerable<string>? selected)
    {
        var keys = (selected ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        if (RolePermissions.Count == 0)
        {
            RolePermissions = new ObservableCollection<RolePermissionOption>(
                KnownPermissions
                    .OrderBy(permission => permission.Label, StringComparer.OrdinalIgnoreCase)
                    .Select(permission => new RolePermissionOption(permission.Key, permission.Label)));
        }

        foreach (var option in RolePermissions)
        {
            option.IsChecked = keys.Contains(option.Key);
        }
    }

    private static string PermissionLabel(string key)
    {
        foreach (var permission in KnownPermissions)
        {
            if (string.Equals(permission.Key, key, StringComparison.Ordinal))
            {
                return permission.Label;
            }
        }

        return key.Replace("group-", string.Empty, StringComparison.Ordinal).Replace('-', ' ');
    }

    /// <summary>The permission set VRChat documents for groups, used when the catalogue fails.</summary>
    private static readonly (string Key, string Label)[] KnownPermissions =
    {
        ("group-members-manage", "Manage member data"),
        ("group-members-remove", "Remove members"),
        ("group-members-viewall", "View all members"),
        ("group-bans-manage", "Manage bans"),
        ("group-roles-assign", "Assign roles"),
        ("group-roles-manage", "Manage roles"),
        ("group-default-role-manage", "Manage default role"),
        ("group-invites-manage", "Manage invites"),
        ("group-data-manage", "Manage group data"),
        ("group-audit-view", "View audit log"),
        ("group-announcement-manage", "Manage announcements"),
        ("group-galleries-manage", "Manage galleries"),
        ("group-calendar-manage", "Manage calendar"),
        ("group-instance-manage", "Manage instances"),
        ("group-instance-moderate", "Moderate instances"),
        ("group-instance-announcement-create", "Create instance announcements"),
        ("group-instance-join", "Join group instances"),
        ("group-instance-public-create", "Create public instances"),
        ("group-instance-plus-create", "Create Group+ instances"),
        ("group-instance-restricted-create", "Create Group+ members instances"),
        ("group-instance-open-create", "Create open instances"),
        ("group-instance-age-gated-create", "Create age-gated instances"),
        ("group-instance-plus-portal", "Create Group+ portals"),
        ("group-instance-plus-portal-unlocked", "Create unlocked Group+ portals"),
        ("group-instance-queue-priority", "Instance queue priority"),
        ("group-instance-bypass-avatar-performance", "Bypass avatar performance limits"),
        ("group-instance-calendar-link", "Link instances to the calendar")
    };

    /// <summary>Called after a kick or ban: the group now has one member fewer.</summary>
    private void AfterMemberRemoved()
    {
        MemberTotal = Math.Max(0, MemberTotal - 1);
        MemberCountChanged?.Invoke();
        RaiseMemberFlags();
    }

    /// <summary>An emptied page falls back to the page before it instead of showing nothing.</summary>
    private async Task BackToLastPageIfEmptyAsync()
    {
        if (Members.Count == 0 && _memberPage > 0)
        {
            await JumpToMembersPageAsync(_memberPage - 1);
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
        OnPropertyChanged(nameof(ShowRoles));
        OnPropertyChanged(nameof(ShowLog));
        OnPropertyChanged(nameof(MembersTabLabel));
        OnPropertyChanged(nameof(BansTabLabel));
        OnPropertyChanged(nameof(RequestsTabLabel));
        OnPropertyChanged(nameof(InvitesTabLabel));
        OnPropertyChanged(nameof(RolesTabLabel));
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
        StartNewRoleCommand.RaiseCanExecuteChanged();
        SaveRoleCommand.RaiseCanExecuteChanged();
        CloseMemberCardCommand.RaiseCanExecuteChanged();
        SaveMemberSettingsCommand.RaiseCanExecuteChanged();
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
        ManagerNotes = member.ManagerNotes;
        Visibility = member.Visibility;
        JoinedText = member.JoinedAt == default
            ? string.Empty
            : "joined " + member.JoinedAt.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        SelectCommand = new RelayCommand(() => _owner.OpenMemberCard(this));
        KickCommand = new RelayCommand(() => _owner.RequestKick(this), () => owner.CanKick);
        BanCommand = new RelayCommand(() => _owner.RequestBan(this), () => owner.CanBan);
        OpenProfileCommand = new RelayCommand(() => _owner.OpenProfileOf(this));
    }

    public string UserId { get; }

    public string DisplayName { get; }

    public string AvatarUrl { get; }

    public string IdText => UserId;

    public string ManagerNotes { get; private set; }

    public string Visibility { get; private set; }

    /// <summary>"joined 12 Mar 2025", blank when VRChat does not report the join date.</summary>
    public string JoinedText { get; }

    public string NoteText => string.IsNullOrWhiteSpace(ManagerNotes) ? string.Empty : ManagerNotes;

    public bool HasNote => !string.IsNullOrWhiteSpace(ManagerNotes);

    /// <summary>The roles this member holds, resolved to names once the role list is loaded.</summary>
    public string RoleText
    {
        get
        {
            var names = new List<string>();
            foreach (var role in _owner.Roles)
            {
                if (string.Equals(role.Name, "Everyone", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (RoleIds.Contains(role.RoleId, StringComparer.Ordinal))
                {
                    names.Add(role.Name);
                }
            }

            return string.Join(" · ", names);
        }
    }

    internal void NotifyRoleText()
    {
        OnPropertyChanged(nameof(RoleText));
    }

    internal void SetSettings(string notes)
    {
        ManagerNotes = notes;
        OnPropertyChanged(nameof(ManagerNotes));
        OnPropertyChanged(nameof(NoteText));
        OnPropertyChanged(nameof(HasNote));
    }

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

    public string IdText => UserId;

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
    private readonly GroupModerationViewModel _owner;
    private bool _isAssigned;

    public ModerationRoleToggle(GroupRole role, GroupModerationViewModel owner)
    {
        Role = role;
        _owner = owner;
        ToggleCommand = new RelayCommand(() => _ = owner.ToggleRoleAsync(this));
        EditCommand = new RelayCommand(() => owner.EditRole(this), () => owner.CanManageRoles);
        DeleteCommand = new RelayCommand(() => owner.RequestDeleteRole(this), () => owner.CanManageRoles);
    }

    public GroupRole Role { get; }

    public string RoleId => Role.Id;

    public string Name => Role.Name;

    public string Description => string.IsNullOrWhiteSpace(Role.Description)
        ? "No description"
        : Role.Description;

    public string MemberCountText => $"{Role.MemberCount:N0} members";

    public string FlagsText
    {
        get
        {
            var flags = new List<string>();
            if (Role.IsAddedOnJoin) flags.Add("added on join");
            if (Role.IsSelfAssignable) flags.Add("self-assignable");
            if (Role.RequiresTwoFactor) flags.Add("needs 2FA");
            return flags.Count == 0 ? string.Empty : string.Join(" · ", flags);
        }
    }

    /// <summary>The Everyone role is fixed, so it can never be deleted.</summary>
    public bool CanDeleteRole => _owner.CanManageRoles
        && !string.Equals(Role.Name, "Everyone", StringComparison.OrdinalIgnoreCase);

    public RelayCommand EditCommand { get; }

    public RelayCommand DeleteCommand { get; }

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

/// <summary>One permission a role can be given, shown as a checkbox in the role editor.</summary>
public sealed class RolePermissionOption : ViewModelBase
{
    private bool _isChecked;

    public RolePermissionOption(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }

    public string Label { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
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

    /// <summary>Actor and time on one line; Run bindings are avoided because they default to TwoWay.</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Actor)
        ? When
        : string.IsNullOrWhiteSpace(When) ? Actor : $"{Actor} · {When}";
}
