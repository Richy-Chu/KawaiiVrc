using System;
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

    public GroupViewModel(VrcApiClient api)
    {
        _api = api;

        LoadGroupsCommand = Track(new AsyncRelayCommand(LoadGroupsAsync, () => !IsBusy));
        CreateGroupCommand = Track(new AsyncRelayCommand(CreateGroupAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(CreateName)));
        RefreshCommand = Track(new AsyncRelayCommand(LoadGroupsAsync, () => !IsBusy));
        CloseCreateDialogCommand = Track(new RelayCommand(() => ShowCreateDialog = false));
        CloseGroupDetailsCommand = Track(new RelayCommand(() => SelectedGroup = null));
        RefreshMembersCommand = Track(new AsyncRelayCommand(LoadGroupMembersAsync, () => SelectedGroup is not null && !IsBusy));
        OpenSelectedGroupCommand = Track(new RelayCommand(OpenSelectedGroupInBrowser, () => SelectedGroup is not null));
        OpenGroupCommand = Track(new RelayCommand<Group>(group => SelectedGroup = group, group => group is not null));
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
                _ = LoadGroupMembersAsync();
            }
        }
    }

    public ObservableCollection<GroupMember> Members
    {
        get => _members;
        set => SetProperty(ref _members, value);
    }

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
        HasLoaded = false;
        ErrorMessage = null;
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
