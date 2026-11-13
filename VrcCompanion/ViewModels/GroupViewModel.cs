using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private Group? _selectedGroup;
    private ObservableCollection<GroupMember> _members = new();
    private bool _isBusy;
    private string? _errorMessage;
    private string _createName = string.Empty;
    private string _createDescription = string.Empty;
    private readonly List<IRelayCommand> _commands = new();
    private bool _showCreateDialog;
    private bool _hasLoaded;
    private readonly Func<LaunchMode> _launchMode;
    private ObservableCollection<GroupInstance> _instances = new();
    private bool _isLoadingInstances;
    private Group? _groupInfo;
    private bool _isGroupInfoOpen;
    private bool _isGroupInfoBusy;
    private string? _groupInfoError;
    private bool _isMemberOfGroupInfo;
    private bool _isLeaveGroupConfirmOpen;
    private ObservableCollection<GroupInstance> _groupInfoInstances = new();
    private bool _isLoadingGroupInfoInstances;
    private bool _groupInstancesHidden;

    /// <summary>Raised after a successful join or leave, with the affected group.</summary>
    public event Action<Group, bool>? MembershipChanged;

    /// <summary>Runs the same notification a successful join or leave sends.</summary>
    internal void PublishMembershipChanged(Group group, bool isMember)
        => MembershipChanged?.Invoke(group, isMember);

    public GroupViewModel(VrcApiClient api, Func<LaunchMode>? launchMode = null)
    {
        _api = api;
        _launchMode = launchMode ?? (() => LaunchMode.Vr);

        LoadGroupsCommand = Track(new AsyncRelayCommand(() => LoadGroupsAsync(), () => !IsBusy));
        CreateGroupCommand = Track(new AsyncRelayCommand(CreateGroupAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(CreateName)));
        RefreshCommand = Track(new AsyncRelayCommand(() => LoadGroupsAsync(forceRefresh: true), () => !IsBusy));
        CloseCreateDialogCommand = Track(new RelayCommand(() => ShowCreateDialog = false));
        CloseGroupDetailsCommand = Track(new RelayCommand(() => SelectedGroup = null));
        RefreshMembersCommand = Track(new AsyncRelayCommand(LoadGroupMembersAsync, () => SelectedGroup is not null && !IsBusy));
        OpenSelectedGroupCommand = Track(new RelayCommand(OpenSelectedGroupInBrowser, () => SelectedGroup is not null));
        OpenGroupCommand = Track(new RelayCommand<Group>(group => SelectedGroup = group, group => group is not null));
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
        CloseGroupInfoCommand = Track(new RelayCommand(() => IsGroupInfoOpen = false));
        JoinGroupInfoCommand = Track(new AsyncRelayCommand(JoinGroupInfoAsync, () => GroupInfo is not null && IsGroupJoinOpen && !IsGroupInfoBusy && !IsMemberOfGroupInfo));
        LeaveGroupInfoCommand = Track(new AsyncRelayCommand(LeaveGroupInfoAsync, () => GroupInfo is not null && !IsGroupInfoBusy && IsMemberOfGroupInfo));
        OpenGroupInfoOnWebCommand = Track(new RelayCommand(OpenGroupInfoOnWeb, () => GroupInfo is not null));
        RequestLeaveGroupCommand = Track(new RelayCommand(() => IsLeaveGroupConfirmOpen = true, () => GroupInfo is not null && IsMemberOfGroupInfo && !IsGroupInfoBusy));
        CancelLeaveGroupCommand = Track(new RelayCommand(() => IsLeaveGroupConfirmOpen = false));
    }

    public ObservableCollection<Group> Groups
    {
        get => _groups;
        set => SetProperty(ref _groups, value);
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

    public ObservableCollection<GroupMember> Members
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

    public bool IsGroupJoinOpen => string.Equals(GroupInfo?.JoinState, "open", StringComparison.OrdinalIgnoreCase);

    public string GroupJoinHint => GroupInfo is null || IsMemberOfGroupInfo
        ? string.Empty
        : GroupInfo.JoinState?.ToLowerInvariant() switch
        {
            "open" => "This group is open — Join group adds you right away.",
            "request" => "This group requires a join request, which VRChat only accepts on vrchat.com.",
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

    public string MembersSummary => Members.Count == 1 ? "1 member loaded" : $"{Members.Count} members loaded";

    public RelayCommand CloseGroupDetailsCommand { get; }

    public AsyncRelayCommand RefreshMembersCommand { get; }

    public RelayCommand OpenSelectedGroupCommand { get; }

    public RelayCommand<Group> OpenGroupCommand { get; }

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
        Groups = new ObservableCollection<Group>();
        Members = new ObservableCollection<GroupMember>();
        Instances = new ObservableCollection<GroupInstance>();
        GroupInfo = null;
        IsGroupInfoOpen = false;
        IsLeaveGroupConfirmOpen = false;
        GroupInfoInstances = new ObservableCollection<GroupInstance>();
        GroupInstancesHidden = false;
        HasLoaded = false;
        ErrorMessage = null;
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
            if (SelectedGroup?.Id != groupId)
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

        try
        {
            LaunchService.Launch(instance.Location, _launchMode());
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not launch VRChat for that instance.";
        }
    }

    /// <summary>
    /// Opens a group from anywhere in the app (a friend's profile chip, the
    /// signed-in account's own groups) with real group data and join/leave.
    /// </summary>
    public async Task OpenGroupInfoAsync(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return;
        }

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

        LaunchService.OpenUrl($"https://vrchat.com/home/group/{GroupInfo.EffectiveId}");
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
            await _api.JoinGroupAsync(GroupInfo.EffectiveId);
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

    private async Task LoadGroupMembersAsync()
    {
        if (SelectedGroup is null)
            return;

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var members = await _api.GetGroupMembersAsync(SelectedGroup.EffectiveId);
            Members = new ObservableCollection<GroupMember>(members.OrderBy(m => m.UserName));
            OnPropertyChanged(nameof(MembersSummary));
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
            IsBusy = false;
        }
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
