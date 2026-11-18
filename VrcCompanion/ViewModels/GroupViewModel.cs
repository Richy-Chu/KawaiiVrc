using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Input;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// View model for VRChat group management. Requests go through the signed-in
/// <see cref="VrcApiClient"/>, so group calls share the session.
/// </summary>
public sealed class GroupViewModel : ViewModelBase
{
    private readonly VrcApiClient _api;

    private ObservableCollection<Group> _groups = new();
    private ObservableCollection<Group> _visibleGroups = new();
    private string _groupFilterText = string.Empty;
    private string _groupDiscoveryQuery = string.Empty;
    private ObservableCollection<Group> _groupSearchResults = new();
    private bool _isSearchingGroups;
    private string _groupSearchMessage = string.Empty;
    private Group? _selectedGroup;
    private ObservableCollection<GroupMemberItem> _members = new();
    private bool _isLoadingMembers;
    private int _membersPage;
    private string _memberSearchText = string.Empty;
    private bool _isMemberSearchActive;
    private string? _lastMemberSearch;
    private readonly Action<GroupMemberItem>? _openMemberProfile;
    private bool _isBusy;
    private string? _errorMessage;
    private string _createName = string.Empty;
    private string _createDescription = string.Empty;
    private readonly List<IRelayCommand> _commands = new();
    private bool _showCreateDialog;
    private bool _hasLoaded;
    private readonly Action<string, string>? _openLaunchChooser;
    private ObservableCollection<GroupInstance> _instances = new();
    private bool _isLoadingInstances;
    private Group? _groupInfo;
    private bool _isGroupInfoOpen;
    private bool _isGroupInfoBusy;
    private string? _groupInfoError;
    private bool _isMemberOfGroupInfo;
    private bool _isMutualGroupInfo;
    private bool _isLeaveGroupConfirmOpen;
    private ObservableCollection<GroupInstance> _groupInfoInstances = new();
    private bool _isLoadingGroupInfoInstances;
    private bool _groupInstancesHidden;

    /// <summary>Raised after a successful join or leave, with the affected group.</summary>
    public event Action<Group, bool>? MembershipChanged;

    /// <summary>Runs the same notification a successful join or leave sends.</summary>
    internal void PublishMembershipChanged(Group group, bool isMember)
        => MembershipChanged?.Invoke(group, isMember);

    private readonly Func<string, bool> _openUrl;

    public GroupViewModel(
        VrcApiClient api,
        Action<string, string>? openLaunchChooser = null,
        Func<string, bool>? openUrl = null,
        Action<GroupMemberItem>? openMemberProfile = null)
    {
        _api = api;
        _openMemberProfile = openMemberProfile;
        _openLaunchChooser = openLaunchChooser;
        _openUrl = openUrl ?? (url => LaunchService.OpenUrl(url));

        LoadGroupsCommand = Track(new AsyncRelayCommand(() => LoadGroupsAsync(), () => !IsBusy));
        CreateGroupCommand = Track(new AsyncRelayCommand(CreateGroupAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(CreateName)));
        RefreshCommand = Track(new AsyncRelayCommand(() => LoadGroupsAsync(forceRefresh: true), () => !IsBusy));
        CloseCreateDialogCommand = Track(new RelayCommand(() => ShowCreateDialog = false));
        CloseGroupDetailsCommand = Track(new RelayCommand(() => SelectedGroup = null));
        RefreshMembersCommand = Track(new AsyncRelayCommand(LoadGroupMembersAsync, () => SelectedGroup is not null && !IsBusy));
        PreviousMembersPageCommand = Track(new AsyncRelayCommand(PreviousMembersPageAsync, () => CanGoToPreviousMembersPage));
        NextMembersPageCommand = Track(new AsyncRelayCommand(NextMembersPageAsync, () => CanGoToNextMembersPage));
        SearchMembersCommand = Track(new AsyncRelayCommand(SearchMembersAsync, () => SelectedGroup is not null && !IsBusy && MemberSearchText.Trim().Length >= 3));
        ClearMemberSearchCommand = Track(new RelayCommand(
            () => { _ = ClearMemberSearchAsync(); },
            () => SelectedGroup is not null || IsMemberSearchActive));
        OpenSelectedGroupCommand = Track(new RelayCommand(OpenSelectedGroupInBrowser, () => SelectedGroup is not null));
        OpenGroupCommand = Track(new RelayCommand<Group>(group => SelectedGroup = group, group => group is not null));
        SearchGroupsCommand = Track(new AsyncRelayCommand(
            SearchGroupsAsync,
            () => !IsSearchingGroups && !string.IsNullOrWhiteSpace(GroupDiscoveryQuery)));
        OpenDiscoveredGroupCommand = Track(new AsyncRelayCommand<Group>(
            group => OpenGroupInfoAsync(group.EffectiveId),
            group => group is not null && !IsGroupInfoBusy));
        OpenSelectedGroupInfoCommand = Track(new RelayCommand(
            () =>
            {
                if (SelectedGroup is not null)
                {
                    _ = OpenGroupInfoAsync(SelectedGroup.EffectiveId);
                }
            },
            () => SelectedGroup is not null));
        RefreshInstancesCommand = Track(new AsyncRelayCommand(LoadGroupInstancesAsync, () => SelectedGroup is not null && !IsBusy));
        JoinGroupInstanceCommand = Track(new RelayCommand<GroupInstance>(JoinGroupInstance, instance => instance?.CanJoin == true));
        CloseGroupInfoCommand = Track(new RelayCommand(CloseGroupInfo));
        JoinGroupInfoCommand = Track(new AsyncRelayCommand(JoinGroupInfoAsync, () => CanUseGroupJoinButton));
        LeaveGroupInfoCommand = Track(new AsyncRelayCommand(LeaveGroupInfoAsync, () => GroupInfo is not null && !IsGroupInfoBusy && IsMemberOfGroupInfo));
        OpenGroupInfoOnWebCommand = Track(new RelayCommand(OpenGroupInfoOnWeb, () => GroupInfo is not null));
        RequestLeaveGroupCommand = Track(new RelayCommand(() => IsLeaveGroupConfirmOpen = true, () => GroupInfo is not null && IsMemberOfGroupInfo && !IsGroupInfoBusy));
        CancelLeaveGroupCommand = Track(new RelayCommand(() => IsLeaveGroupConfirmOpen = false));
    }

    private void CloseGroupInfo()
    {
        IsGroupInfoOpen = false;
        GroupInfoIsMutual = false;
    }

    public ObservableCollection<Group> Groups
    {
        get => _groups;
        set
        {
            if (SetProperty(ref _groups, value))
            {
                ApplyGroupFilter();
            }
        }
    }

    /// <summary>The joined groups matching <see cref="GroupFilterText"/>.</summary>
    public ObservableCollection<Group> VisibleGroups
    {
        get => _visibleGroups;
        private set => SetProperty(ref _visibleGroups, value);
    }

    public string GroupFilterText
    {
        get => _groupFilterText;
        set
        {
            if (SetProperty(ref _groupFilterText, value))
            {
                ApplyGroupFilter();
                OnPropertyChanged(nameof(GroupListEmptyText));
                OnPropertyChanged(nameof(GroupListEmptyHint));
            }
        }
    }

    public string GroupListEmptyText => string.IsNullOrWhiteSpace(GroupFilterText)
        ? "No groups found."
        : "No joined groups match that search.";

    public string GroupListEmptyHint => string.IsNullOrWhiteSpace(GroupFilterText)
        ? "Create your first group to get started."
        : "Try another group name.";

    public string GroupDiscoveryQuery
    {
        get => _groupDiscoveryQuery;
        set
        {
            if (SetProperty(ref _groupDiscoveryQuery, value))
            {
                SearchGroupsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<Group> GroupSearchResults
    {
        get => _groupSearchResults;
        private set => SetProperty(ref _groupSearchResults, value);
    }

    public bool IsSearchingGroups
    {
        get => _isSearchingGroups;
        private set
        {
            if (SetProperty(ref _isSearchingGroups, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string GroupSearchMessage
    {
        get => _groupSearchMessage;
        private set => SetProperty(ref _groupSearchMessage, value);
    }

    public Group? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                OnPropertyChanged(nameof(IsGroupDetailsOpen));
                OnPropertyChanged(nameof(SelectedGroupUrl));
                OnPropertyChanged(nameof(MembersSummary));
                RaiseCommandStates();
                Instances = new ObservableCollection<GroupInstance>();
                OnPropertyChanged(nameof(NoInstances));
                _ = LoadGroupMembersAsync();
                _ = LoadGroupInstancesAsync();
            }
        }
    }

    public ObservableCollection<GroupMemberItem> Members
    {
        get => _members;
        set => SetProperty(ref _members, value);
    }

    /// <summary>Live instances hosted by the selected group.</summary>
    public ObservableCollection<GroupInstance> Instances
    {
        get => _instances;
        private set => SetProperty(ref _instances, value);
    }

    public bool IsLoadingInstances
    {
        get => _isLoadingInstances;
        private set
        {
            if (SetProperty(ref _isLoadingInstances, value))
            {
                OnPropertyChanged(nameof(NoInstances));
            }
        }
    }

    public bool NoInstances => !IsLoadingInstances && Instances.Count == 0;

    // ------------------------------------------------------- group info popup

    public Group? GroupInfo
    {
        get => _groupInfo;
        internal set
        {
            if (SetProperty(ref _groupInfo, value))
            {
                OnPropertyChanged(nameof(GroupInfoMemberText));
                OnPropertyChanged(nameof(GroupInfoMembershipLabel));
                OnPropertyChanged(nameof(HasGroupInfoMembershipLabel));
                OnPropertyChanged(nameof(GroupJoinHint));
                OnPropertyChanged(nameof(IsGroupJoinOpen));
                OnPropertyChanged(nameof(IsGroupJoinRequest));
                OnPropertyChanged(nameof(IsJoinRequestPending));
                OnPropertyChanged(nameof(CanUseGroupJoinButton));
                OnPropertyChanged(nameof(GroupJoinButtonText));
                OnPropertyChanged(nameof(GroupJoinButtonToolTip));
                RaiseCommandStates();
            }
        }
    }

    public bool IsGroupInfoOpen
    {
        get => _isGroupInfoOpen;
        private set => SetProperty(ref _isGroupInfoOpen, value);
    }

    public bool IsGroupInfoBusy
    {
        get => _isGroupInfoBusy;
        private set
        {
            if (SetProperty(ref _isGroupInfoBusy, value))
            {
                OnPropertyChanged(nameof(CanUseGroupJoinButton));
                RaiseCommandStates();
            }
        }
    }

    public string? GroupInfoError
    {
        get => _groupInfoError;
        private set => SetProperty(ref _groupInfoError, value);
    }

    public bool IsMemberOfGroupInfo
    {
        get => _isMemberOfGroupInfo;
        internal set
        {
            if (SetProperty(ref _isMemberOfGroupInfo, value))
            {
                OnPropertyChanged(nameof(GroupInfoMemberText));
                OnPropertyChanged(nameof(GroupInfoMembershipLabel));
                OnPropertyChanged(nameof(HasGroupInfoMembershipLabel));
                OnPropertyChanged(nameof(GroupJoinHint));
                OnPropertyChanged(nameof(IsGroupJoinOpen));
                OnPropertyChanged(nameof(IsJoinRequestPending));
                OnPropertyChanged(nameof(CanUseGroupJoinButton));
                OnPropertyChanged(nameof(GroupJoinButtonText));
                OnPropertyChanged(nameof(GroupJoinButtonToolTip));
                RaiseCommandStates();
            }
        }
    }

    public string GroupInfoMemberText
    {
        get
        {
            if (IsMemberOfGroupInfo)
            {
                return "You are a member";
            }

            return GroupInfo?.MembershipStatus?.ToLowerInvariant() switch
            {
                "requested" => "Your join request is pending",
                "invited" => "You have an invite to this group",
                "banned" => "You are banned from this group",
                "userblocked" => "You have blocked this group",
                _ => "You are not a member"
            };
        }
    }

    public string GroupInfoMembershipLabel
    {
        get
        {
            if (IsMemberOfGroupInfo)
            {
                return "Joined";
            }

            return GroupInfo?.MembershipStatus?.ToLowerInvariant() switch
            {
                "requested" => "Request pending",
                "invited" => "Invited",
                "banned" => "Banned",
                "userblocked" => "Blocked",
                _ => string.Empty
            };
        }
    }

    public bool HasGroupInfoMembershipLabel => GroupInfoMembershipLabel.Length > 0;

    /// <summary>True when the group is shared with the profile it was opened from.</summary>
    public bool GroupInfoIsMutual
    {
        get => _isMutualGroupInfo;
        private set => SetProperty(ref _isMutualGroupInfo, value);
    }

    public bool IsGroupJoinOpen => string.Equals(GroupInfo?.JoinState, "open", StringComparison.OrdinalIgnoreCase);

    /// <summary>The group takes join requests rather than open joins.</summary>
    public bool IsGroupJoinRequest => string.Equals(GroupInfo?.JoinState, "request", StringComparison.OrdinalIgnoreCase);

    /// <summary>A request is with the group's staff, so there is nothing left to click.</summary>
    public bool IsJoinRequestPending => !IsMemberOfGroupInfo
        && string.Equals(GroupInfo?.MembershipStatus, "requested", StringComparison.OrdinalIgnoreCase);

    /// <summary>Open groups join straight away, request groups send the request from here.</summary>
    public bool CanUseGroupJoinButton => !IsMemberOfGroupInfo
        && !IsJoinRequestPending
        && !IsGroupInfoBusy
        && (IsGroupJoinOpen || IsGroupJoinRequest);

    public string GroupJoinButtonText => IsJoinRequestPending
        ? "Request pending"
        : IsGroupJoinRequest ? "Request to join" : "Join group";

    public string GroupJoinButtonToolTip => IsJoinRequestPending
        ? "Waiting for the group to accept your request"
        : IsGroupJoinRequest
            ? "Send a join request from here"
            : "Join this open group";

    public string GroupJoinHint => GroupInfo is null || IsMemberOfGroupInfo
        ? string.Empty
        : IsJoinRequestPending
            ? "Your join request is pending. You become a member once the group accepts it."
            : GroupInfo.JoinState?.ToLowerInvariant() switch
        {
            "open" => "This group is open — Join group adds you right away.",
            "request" => "This group takes join requests — Request to join sends one from here.",
            "invite" => "This group is invite-only. Ask a member for an invite on vrchat.com.",
            "closed" => "This group is closed to new members.",
            _ => string.Empty
        };

    public RelayCommand CloseGroupInfoCommand { get; }

    public AsyncRelayCommand JoinGroupInfoCommand { get; }

    public AsyncRelayCommand LeaveGroupInfoCommand { get; }

    public RelayCommand OpenGroupInfoOnWebCommand { get; }

    public RelayCommand RequestLeaveGroupCommand { get; }

    public RelayCommand CancelLeaveGroupCommand { get; }

    /// <summary>Leaving changes real membership, so it always needs a second click.</summary>
    public bool IsLeaveGroupConfirmOpen
    {
        get => _isLeaveGroupConfirmOpen;
        private set => SetProperty(ref _isLeaveGroupConfirmOpen, value);
    }

    public ObservableCollection<GroupInstance> GroupInfoInstances
    {
        get => _groupInfoInstances;
        private set => SetProperty(ref _groupInfoInstances, value);
    }

    public bool IsLoadingGroupInfoInstances
    {
        get => _isLoadingGroupInfoInstances;
        private set => SetProperty(ref _isLoadingGroupInfoInstances, value);
    }

    public bool GroupInstancesHidden
    {
        get => _groupInstancesHidden;
        private set => SetProperty(ref _groupInstancesHidden, value);
    }

    public string GroupInfoInstancesText
    {
        get
        {
            if (IsLoadingGroupInfoInstances)
            {
                return "Checking for open instances…";
            }
            if (GroupInstancesHidden)
            {
                return "Open instances are only visible to members.";
            }

            return GroupInfoInstances.Count == 0
                ? "No open instances right now."
                : $"{GroupInfoInstances.Count} open instance{(GroupInfoInstances.Count == 1 ? string.Empty : "s")}";
        }
    }

    public AsyncRelayCommand RefreshInstancesCommand { get; }

    public RelayCommand<GroupInstance> JoinGroupInstanceCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public string CreateName
    {
        get => _createName;
        set => SetProperty(ref _createName, value);
    }

    public string CreateDescription
    {
        get => _createDescription;
        set => SetProperty(ref _createDescription, value);
    }

    public bool ShowCreateDialog
    {
        get => _showCreateDialog;
        set => SetProperty(ref _showCreateDialog, value);
    }

    public bool HasSelectedGroup => SelectedGroup is not null;

    public bool IsGroupDetailsOpen => SelectedGroup is not null;

    public string SelectedGroupUrl => SelectedGroup is null
        ? string.Empty
        : $"https://vrchat.com/home/group/{SelectedGroup.EffectiveId}";

    public bool IsLoadingMembers
    {
        get => _isLoadingMembers;
        private set
        {
            if (SetProperty(ref _isLoadingMembers, value))
            {
                OnPropertyChanged(nameof(MembersSummary));
                OnPropertyChanged(nameof(CanGoToPreviousMembersPage));
                OnPropertyChanged(nameof(CanGoToNextMembersPage));
                RaiseCommandStates();
            }
        }
    }

    /// <summary>VRChat returns at most 100 members per request, so the popup pages through them.</summary>
    public const int MembersPageSize = 100;

    public string MemberSearchText
    {
        get => _memberSearchText;
        set
        {
            if (SetProperty(ref _memberSearchText, value))
            {
                SearchMembersCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsMemberSearchActive => _isMemberSearchActive;

    public int MemberPageNumber => _membersPage + 1;

    public int MemberPageCount
    {
        get
        {
            var total = SelectedGroup?.MemberCount ?? 0;
            return total <= 0 ? 1 : (total + MembersPageSize - 1) / MembersPageSize;
        }
    }

    public string MembersPageText => IsMemberSearchActive ? string.Empty : $"Page {MemberPageNumber} of {MemberPageCount}";

    public bool CanGoToPreviousMembersPage => !IsMemberSearchActive && !IsLoadingMembers && _membersPage > 0;

    public bool CanGoToNextMembersPage => !IsMemberSearchActive
        && !IsLoadingMembers
        && Members.Count > 0
        && (SelectedGroup?.MemberCount ?? 0) > MemberPageNumber * MembersPageSize;

    public string MembersSummary => DescribeMembers(
        Members.Count,
        SelectedGroup?.MemberCount ?? 0,
        IsLoadingMembers,
        _membersPage,
        _isMemberSearchActive ? _lastMemberSearch : null);

    internal static string DescribeMembers(
        int loaded,
        int total,
        bool isLoading,
        int page = 0,
        string? searchQuery = null)
    {
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            if (isLoading && loaded == 0)
            {
                return $"Searching for “{searchQuery}”…";
            }

            if (loaded == 0)
            {
                return $"No members match “{searchQuery}” in this group.";
            }

            return loaded == 1
                ? $"1 member matches “{searchQuery}”"
                : string.Create(CultureInfo.InvariantCulture, $"{loaded:N0} members match “{searchQuery}”");
        }

        if (isLoading && loaded == 0)
        {
            return "Loading members…";
        }

        if (loaded == 0)
        {
            return "No members loaded.";
        }

        var first = page * MembersPageSize + 1;
        var last = page * MembersPageSize + loaded;
        return total > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Showing {first:N0}–{last:N0} of {total:N0} members")
            : string.Create(CultureInfo.InvariantCulture, $"Showing {first:N0}–{last:N0} members");
    }

    public RelayCommand CloseGroupDetailsCommand { get; }

    public AsyncRelayCommand PreviousMembersPageCommand { get; }

    public AsyncRelayCommand NextMembersPageCommand { get; }

    public AsyncRelayCommand SearchMembersCommand { get; }

    public RelayCommand ClearMemberSearchCommand { get; }

    public AsyncRelayCommand RefreshMembersCommand { get; }

    public RelayCommand OpenSelectedGroupCommand { get; }

    public RelayCommand<Group> OpenGroupCommand { get; }

    public AsyncRelayCommand SearchGroupsCommand { get; }

    public AsyncRelayCommand<Group> OpenDiscoveredGroupCommand { get; }

    public RelayCommand OpenSelectedGroupInfoCommand { get; }

    private void OpenSelectedGroupInBrowser()
    {
        if (SelectedGroup is null)
        {
            return;
        }

        try
        {
            LaunchService.OpenUrl(SelectedGroupUrl);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Couldn't open that group in your browser.";
        }
    }

    /// <summary>True once the group list has loaded, so navigation does not refetch.</summary>
    public bool HasLoaded
    {
        get => _hasLoaded;
        private set => SetProperty(ref _hasLoaded, value);
    }

    public ICommand LoadGroupsCommand { get; }
    public ICommand CreateGroupCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CloseCreateDialogCommand { get; }

    public Task LoadAsync() => LoadGroupsAsync();

    private T Track<T>(T command) where T : IRelayCommand
    {
        _commands.Add(command);
        return command;
    }

    private void RaiseCommandStates()
    {
        foreach (var command in _commands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private async Task LoadGroupsAsync(bool forceRefresh = false)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var groups = await _api.GetMyGroupsAsync(forceRefresh: forceRefresh);
            Groups = new ObservableCollection<Group>(groups.OrderByDescending(g => g.UpdatedAt));
            HasLoaded = true;
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat. Check your internet connection.";
        }
        catch (TaskCanceledException)
        {
            ErrorMessage = "The group request timed out. Try again.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not load groups.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Clears the list so a different account loads its own groups.</summary>
    public void Reset()
    {
        SelectedGroup = null;
        GroupFilterText = string.Empty;
        Groups = new ObservableCollection<Group>();
        Members = new ObservableCollection<GroupMemberItem>();
        _membersPage = 0;
        _isMemberSearchActive = false;
        _lastMemberSearch = null;
        MemberSearchText = string.Empty;
        Instances = new ObservableCollection<GroupInstance>();
        GroupInfo = null;
        IsGroupInfoOpen = false;
        IsLeaveGroupConfirmOpen = false;
        GroupInfoInstances = new ObservableCollection<GroupInstance>();
        GroupInstancesHidden = false;
        GroupDiscoveryQuery = string.Empty;
        GroupSearchResults = new ObservableCollection<Group>();
        GroupSearchMessage = string.Empty;
        HasLoaded = false;
        ErrorMessage = null;
    }

    private void ApplyGroupFilter()
    {
        var query = GroupFilterText.Trim();
        var filtered = query.Length == 0
            ? Groups
            : Groups.Where(group =>
                group.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                group.EffectiveId.Contains(query, StringComparison.OrdinalIgnoreCase));
        VisibleGroups = new ObservableCollection<Group>(filtered);
    }

    private async Task SearchGroupsAsync()
    {
        var query = GroupDiscoveryQuery.Trim();
        if (query.Length == 0)
        {
            return;
        }

        IsSearchingGroups = true;
        GroupSearchMessage = "Searching VRChat…";
        try
        {
            var results = await _api.SearchGroupsAsync(query);
            GroupSearchResults = new ObservableCollection<Group>(
                results.OrderByDescending(group => group.MemberCount));
            GroupSearchMessage = results.Count == 0
                ? "No groups found. Try another name or group ID."
                : $"{results.Count} group{(results.Count == 1 ? string.Empty : "s")} found.";
        }
        catch (VrcApiException ex)
        {
            GroupSearchResults = new ObservableCollection<Group>();
            GroupSearchMessage = query.StartsWith("grp_", StringComparison.OrdinalIgnoreCase)
                ? "No group found with that ID."
                : ex.Message;
        }
        catch (HttpRequestException)
        {
            GroupSearchResults = new ObservableCollection<Group>();
            GroupSearchMessage = "Could not reach VRChat to search groups.";
        }
        catch (TaskCanceledException)
        {
            GroupSearchResults = new ObservableCollection<Group>();
            GroupSearchMessage = "The group search timed out. Try again.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            GroupSearchResults = new ObservableCollection<Group>();
            GroupSearchMessage = "Could not search for groups.";
        }
        finally
        {
            IsSearchingGroups = false;
        }
    }

    private async Task LoadGroupInstancesAsync()
    {
        if (SelectedGroup is null)
        {
            return;
        }

        var groupId = SelectedGroup.EffectiveId;
        IsLoadingInstances = true;
        try
        {
            var instances = await _api.GetGroupInstancesAsync(groupId);
            if (SelectedGroup?.EffectiveId != groupId)
            {
                return; // selection changed while loading
            }

            Instances = new ObservableCollection<GroupInstance>(
                instances.OrderByDescending(instance => instance.MemberCount));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Instances = new ObservableCollection<GroupInstance>();
        }
        finally
        {
            IsLoadingInstances = false;
            OnPropertyChanged(nameof(NoInstances));
        }
    }

    private void JoinGroupInstance(GroupInstance? instance)
    {
        if (instance is null || !instance.CanJoin)
        {
            return;
        }

        if (_openLaunchChooser is null)
        {
            return;
        }

        _openLaunchChooser(instance.Location, "that group instance");
    }

    /// <summary>
    /// Opens a group from anywhere in the app (a friend's profile chip, the
    /// signed-in account's own groups) with real group data and join/leave.
    /// </summary>
    public async Task OpenGroupInfoAsync(string groupId, bool isMutual = false)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return;
        }

        GroupInfoIsMutual = isMutual;
        IsGroupInfoOpen = true;
        IsGroupInfoBusy = true;
        GroupInfoError = null;
        GroupInfo = null;
        IsLeaveGroupConfirmOpen = false;
        GroupInfoInstances = new ObservableCollection<GroupInstance>();
        GroupInstancesHidden = false;

        try
        {
            var info = await _api.GetGroupAsync(groupId)
                ?? throw new VrcApiException(404, "That VRChat group could not be found.");
            GroupInfo = info;

            // The group payload carries the signed-in account's membership, which
            // stays correct even when the cached "my groups" page is stale.
            if (!string.IsNullOrWhiteSpace(info.MembershipStatus))
            {
                IsMemberOfGroupInfo = info.IsMember;
            }
            else
            {
                var mine = Groups.Count > 0 ? (IReadOnlyList<Group>)Groups.ToList() : await _api.GetMyGroupsAsync();
                IsMemberOfGroupInfo = mine.Any(group =>
                    string.Equals(group.EffectiveId, groupId, StringComparison.Ordinal));
            }

            // Instance lists are member-only; non-members get a 403 we must not
            // surface as a scary failure.
            await LoadGroupInfoInstancesAsync(groupId);
        }
        catch (VrcApiException ex)
        {
            GroupInfoError = ex.Message;
        }
        catch (HttpRequestException)
        {
            GroupInfoError = "Could not reach VRChat to load this group.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            GroupInfoError = "Could not load this group.";
        }
        finally
        {
            IsGroupInfoBusy = false;
        }
    }

    private async Task LoadGroupInfoInstancesAsync(string groupId)
    {
        IsLoadingGroupInfoInstances = true;
        GroupInstancesHidden = false;
        OnPropertyChanged(nameof(GroupInfoInstancesText));
        try
        {
            var instances = await _api.GetGroupInstancesAsync(groupId);
            GroupInfoInstances = new ObservableCollection<GroupInstance>(
                instances.OrderByDescending(instance => instance.MemberCount));
        }
        catch (VrcApiException ex) when (ex.StatusCode is 401 or 403 or 404)
        {
            GroupInfoInstances = new ObservableCollection<GroupInstance>();
            GroupInstancesHidden = true;
        }
        finally
        {
            IsLoadingGroupInfoInstances = false;
            OnPropertyChanged(nameof(GroupInfoInstancesText));
        }
    }

    private void OpenGroupInfoOnWeb()
    {
        if (GroupInfo is null)
        {
            return;
        }

        _openUrl($"https://vrchat.com/home/group/{GroupInfo.EffectiveId}");
    }

    private async Task JoinGroupInfoAsync()
    {
        if (GroupInfo is null || IsGroupInfoBusy)
        {
            return;
        }

        IsGroupInfoBusy = true;
        GroupInfoError = null;
        try
        {
            // VRChat answers from the group's join state: an open group makes the account
            // a member, while a request-only group records a join request instead.
            var status = await _api.JoinGroupAsync(GroupInfo.EffectiveId);
            if (string.Equals(status, "requested", StringComparison.OrdinalIgnoreCase))
            {
                GroupInfo.MembershipStatus = "requested";
                OnPropertyChanged(nameof(GroupInfoMemberText));
                OnPropertyChanged(nameof(GroupInfoMembershipLabel));
                OnPropertyChanged(nameof(HasGroupInfoMembershipLabel));
                OnPropertyChanged(nameof(GroupJoinHint));
                OnPropertyChanged(nameof(IsJoinRequestPending));
                OnPropertyChanged(nameof(CanUseGroupJoinButton));
                OnPropertyChanged(nameof(GroupJoinButtonText));
                OnPropertyChanged(nameof(GroupJoinButtonToolTip));
                RaiseCommandStates();
                PublishMembershipChanged(GroupInfo, false);
                return;
            }

            IsMemberOfGroupInfo = true;
            GroupInfo.MembershipStatus = "member";
            OnPropertyChanged(nameof(GroupInfoMemberText));
            OnPropertyChanged(nameof(GroupInfoMembershipLabel));
            PublishMembershipChanged(GroupInfo, true);
            GroupInstancesHidden = false;
            await LoadGroupInfoInstancesAsync(GroupInfo.EffectiveId);
            // Refresh the cached membership list in the background; the popup
            // above must not wait for it or re-derive membership from stale data.
            _ = LoadGroupsAsync();
        }
        catch (VrcApiException ex)
        {
            GroupInfoError = ex.Message;
        }
        catch (HttpRequestException)
        {
            GroupInfoError = "Could not reach VRChat to join this group.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            GroupInfoError = "Could not join this group. It may be invite-only or need a request.";
        }
        finally
        {
            IsGroupInfoBusy = false;
        }
    }

    private async Task LeaveGroupInfoAsync()
    {
        if (GroupInfo is null || IsGroupInfoBusy)
        {
            return;
        }

        IsGroupInfoBusy = true;
        GroupInfoError = null;
        try
        {
            await _api.LeaveGroupAsync(GroupInfo.EffectiveId);
            IsMemberOfGroupInfo = false;
            GroupInfo.MembershipStatus = "inactive";
            OnPropertyChanged(nameof(GroupInfoMemberText));
            OnPropertyChanged(nameof(GroupInfoMembershipLabel));
            PublishMembershipChanged(GroupInfo, false);
            IsLeaveGroupConfirmOpen = false;
            GroupInfoInstances = new ObservableCollection<GroupInstance>();
            GroupInstancesHidden = true;
            OnPropertyChanged(nameof(GroupInfoInstancesText));
            if (SelectedGroup?.Id == GroupInfo.Id)
            {
                SelectedGroup = null;
            }

            _ = LoadGroupsAsync();
        }
        catch (VrcApiException ex)
        {
            GroupInfoError = ex.Message;
        }
        catch (HttpRequestException)
        {
            GroupInfoError = "Could not reach VRChat to leave this group.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            GroupInfoError = "Could not leave this group.";
        }
        finally
        {
            IsGroupInfoBusy = false;
        }
    }

    private Task LoadGroupMembersAsync() => SelectedGroup is null
        ? Task.CompletedTask
        : LoadMembersPageAsync(0);

    private Task PreviousMembersPageAsync() => SelectedGroup is null || !CanGoToPreviousMembersPage
        ? Task.CompletedTask
        : LoadMembersPageAsync(_membersPage - 1);

    private Task NextMembersPageAsync() => SelectedGroup is null || !CanGoToNextMembersPage
        ? Task.CompletedTask
        : LoadMembersPageAsync(_membersPage + 1);

    /// <summary>
    /// Pulls one page of the group's roster. The endpoint pages with n=100 and offset, so a
    /// page is requested at page * 100 and the Previous/Next buttons move between them.
    /// </summary>
    private async Task LoadMembersPageAsync(int page)
    {
        if (SelectedGroup is not { } group)
        {
            return;
        }

        var groupId = group.EffectiveId;
        page = Math.Max(0, page);
        IsBusy = true;
        ErrorMessage = null;
        IsLoadingMembers = true;

        try
        {
            var members = await _api.GetGroupMembersAsync(groupId, page * MembersPageSize);
            if (SelectedGroup?.EffectiveId != groupId)
            {
                return;
            }

            _membersPage = page;
            _isMemberSearchActive = false;
            _lastMemberSearch = null;
            Members = new ObservableCollection<GroupMemberItem>(
                members.Select(member => new GroupMemberItem(member, _openMemberProfile)));
            RaiseMemberListChanged();
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat. Check your internet connection.";
        }
        catch (TaskCanceledException)
        {
            ErrorMessage = "The member request timed out. Try again.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not load members.";
        }
        finally
        {
            IsLoadingMembers = false;
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    /// <summary>
    /// Searches the group's members. VRChat's member search filters display names (three
    /// characters or more); a usr_ id is looked up through the single-member endpoint instead.
    /// </summary>
    private async Task SearchMembersAsync()
    {
        if (SelectedGroup is not { } group)
        {
            return;
        }

        var query = MemberSearchText.Trim();
        if (query.Length < 3)
        {
            return;
        }

        var groupId = group.EffectiveId;
        IsBusy = true;
        ErrorMessage = null;
        IsLoadingMembers = true;

        try
        {
            IReadOnlyList<GroupMember> matches;
            if (query.StartsWith("usr_", StringComparison.OrdinalIgnoreCase))
            {
                var member = await _api.GetGroupMemberAsync(groupId, query);
                matches = member is null ? Array.Empty<GroupMember>() : new[] { member };
            }
            else
            {
                matches = await _api.SearchGroupMembersAsync(groupId, query);
            }

            if (SelectedGroup?.EffectiveId != groupId)
            {
                return;
            }

            _membersPage = 0;
            _isMemberSearchActive = true;
            _lastMemberSearch = query;
            Members = new ObservableCollection<GroupMemberItem>(
                matches.Select(member => new GroupMemberItem(member, _openMemberProfile)));
            RaiseMemberListChanged();
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat. Check your internet connection.";
        }
        catch (TaskCanceledException)
        {
            ErrorMessage = "The member search timed out. Try again.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not search the group's members.";
        }
        finally
        {
            IsLoadingMembers = false;
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private async Task ClearMemberSearchAsync()
    {
        MemberSearchText = string.Empty;
        if (SelectedGroup is null)
        {
            return;
        }

        await LoadMembersPageAsync(_membersPage);
    }

    private void RaiseMemberListChanged()
    {
        OnPropertyChanged(nameof(MembersSummary));
        OnPropertyChanged(nameof(MembersPageText));
        OnPropertyChanged(nameof(MemberPageNumber));
        OnPropertyChanged(nameof(MemberPageCount));
        OnPropertyChanged(nameof(IsMemberSearchActive));
        OnPropertyChanged(nameof(CanGoToPreviousMembersPage));
        OnPropertyChanged(nameof(CanGoToNextMembersPage));
        RaiseCommandStates();
    }

    private async Task CreateGroupAsync()
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var group = await _api.CreateGroupAsync(CreateName, CreateDescription);
            if (group is not null)
            {
                Groups.Add(group);
                CreateName = string.Empty;
                CreateDescription = string.Empty;
                ShowCreateDialog = false;
            }
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat. Check your internet connection.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not create that group.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
