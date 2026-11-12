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

    public GroupViewModel(VrcApiClient api, Func<LaunchMode>? launchMode = null)
    {
        _api = api;
        _launchMode = launchMode ?? (() => LaunchMode.Vr);

        LoadGroupsCommand = Track(new AsyncRelayCommand(LoadGroupsAsync, () => !IsBusy));
        CreateGroupCommand = Track(new AsyncRelayCommand(CreateGroupAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(CreateName)));
        RefreshCommand = Track(new AsyncRelayCommand(LoadGroupsAsync, () => !IsBusy));
        CloseCreateDialogCommand = Track(new RelayCommand(() => ShowCreateDialog = false));
        CloseGroupDetailsCommand = Track(new RelayCommand(() => SelectedGroup = null));
        RefreshMembersCommand = Track(new AsyncRelayCommand(LoadGroupMembersAsync, () => SelectedGroup is not null && !IsBusy));
        OpenSelectedGroupCommand = Track(new RelayCommand(OpenSelectedGroupInBrowser, () => SelectedGroup is not null));
        OpenGroupCommand = Track(new RelayCommand<Group>(group => SelectedGroup = group, group => group is not null));
        RefreshInstancesCommand = Track(new AsyncRelayCommand(LoadGroupInstancesAsync, () => SelectedGroup is not null && !IsBusy));
        JoinGroupInstanceCommand = Track(new RelayCommand<GroupInstance>(JoinGroupInstance, instance => instance?.CanJoin == true));
        CloseGroupInfoCommand = Track(new RelayCommand(() => IsGroupInfoOpen = false));
        JoinGroupInfoCommand = Track(new AsyncRelayCommand(JoinGroupInfoAsync, () => GroupInfo is not null && !IsGroupInfoBusy && !IsMemberOfGroupInfo));
        LeaveGroupInfoCommand = Track(new AsyncRelayCommand(LeaveGroupInfoAsync, () => GroupInfo is not null && !IsGroupInfoBusy && IsMemberOfGroupInfo));
        OpenGroupInfoOnWebCommand = Track(new RelayCommand(OpenGroupInfoOnWeb, () => GroupInfo is not null));
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
        private set
        {
            if (SetProperty(ref _groupInfo, value))
            {
                OnPropertyChanged(nameof(GroupInfoMemberText));
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
        private set
        {
            if (SetProperty(ref _isMemberOfGroupInfo, value))
            {
                OnPropertyChanged(nameof(GroupInfoMemberText));
                RaiseCommandStates();
            }
        }
    }

    public string GroupInfoMemberText => IsMemberOfGroupInfo ? "You are a member" : "You are not a member";

    public RelayCommand CloseGroupInfoCommand { get; }

    public AsyncRelayCommand JoinGroupInfoCommand { get; }

    public AsyncRelayCommand LeaveGroupInfoCommand { get; }

    public RelayCommand OpenGroupInfoOnWebCommand { get; }

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
        : $"https://vrchat.com/home/group/{SelectedGroup.Id}";

    public string MembersSummary => Members.Count == 1 ? "1 member loaded" : $"{Members.Count} members loaded";

    public RelayCommand CloseGroupDetailsCommand { get; }

    public AsyncRelayCommand RefreshMembersCommand { get; }

    public RelayCommand OpenSelectedGroupCommand { get; }

    public RelayCommand<Group> OpenGroupCommand { get; }

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

    private async Task LoadGroupsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var groups = await _api.GetMyGroupsAsync();
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
        HasLoaded = false;
        ErrorMessage = null;
    }

    private async Task LoadGroupInstancesAsync()
    {
        if (SelectedGroup is null)
        {
            return;
        }

        var groupId = SelectedGroup.Id;
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

        try
        {
            GroupInfo = await _api.GetGroupAsync(groupId);

            var mine = Groups.Count > 0 ? (IReadOnlyList<Group>)Groups.ToList() : await _api.GetMyGroupsAsync();
            IsMemberOfGroupInfo = mine.Any(group =>
                string.Equals(group.Id, groupId, StringComparison.Ordinal));
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

    private void OpenGroupInfoOnWeb()
    {
        if (GroupInfo is null)
        {
            return;
        }

        LaunchService.OpenUrl($"https://vrchat.com/home/group/{GroupInfo.Id}");
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
            await _api.JoinGroupAsync(GroupInfo.Id);
            IsMemberOfGroupInfo = true;
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
            await _api.LeaveGroupAsync(GroupInfo.Id);
            IsMemberOfGroupInfo = false;
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
            var members = await _api.GetGroupMembersAsync(SelectedGroup.Id);
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
