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
    private string _membersStatus = string.Empty;
    private string _memberPageInput = "1";
    private int _membersPageToken;
    private int? _membersLastPageWithRows;
    private bool _membersTruncated;
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
        Action<GroupMemberItem>? openMemberProfile = null,
        Action<string, string, string>? openUserProfile = null)
    {
        _api = api;
        _openMemberProfile = openMemberProfile;
        Moderation = new GroupModerationViewModel(api)
        {
            // Moderation rows only carry strings, so a profile opens from the ids directly;
            // when no opener was supplied the member-row callback is reused.
            OpenProfileRequested = openUserProfile ?? ((userId, name, avatarUrl) =>
                _openMemberProfile?.Invoke(new GroupMemberItem(
                    new GroupMember
                    {
                        UserId = userId,
                        User = new GroupMember.GroupMemberUser
                        {
                            Id = userId,
                            DisplayName = name,
                            CurrentAvatarThumbnailImageUrl = avatarUrl
                        }
                    },
                    _openMemberProfile)))
        };
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
        FirstMembersPageCommand = Track(new AsyncRelayCommand(FirstMembersPageAsync, () => CanGoToPreviousMembersPage));
        LastMembersPageCommand = Track(new AsyncRelayCommand(LastMembersPageAsync, () => CanGoToNextMembersPage));
        JumpToMembersPageCommand = Track(new AsyncRelayCommand(JumpToMembersPageAsync, () => CanJumpToMembersPage));
        SearchMembersCommand = Track(new AsyncRelayCommand(SearchMembersAsync, () => SelectedGroup is not null && !IsBusy && MemberSearchText.Trim().Length >= 3));
        ClearMemberSearchCommand = Track(new RelayCommand(
            () => { _ = ClearMemberSearchAsync(); },
            () => SelectedGroup is not null || IsMemberSearchActive));
        OpenSelectedGroupCommand = Track(new RelayCommand(OpenSelectedGroupInBrowser, () => SelectedGroup is not null));
        OpenGroupModerationCommand = Track(new AsyncRelayCommand(OpenGroupModerationAsync, () => SelectedGroup is not null && !IsBusy));
        CloseGroupModerationCommand = Track(new RelayCommand(CloseGroupModeration, () => Moderation.IsOpen));
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
                if (Moderation.IsOpen)
                {
                    Moderation.Close();
                }

                OnPropertyChanged(nameof(IsGroupModerationOpen));
                OnPropertyChanged(nameof(IsGroupDetailsOpen));
                OnPropertyChanged(nameof(SelectedGroupUrl));
                OnPropertyChanged(nameof(MembersSummary));
                RaiseCommandStates();
                _membersTruncated = false;
                _membersLastPageWithRows = null;
                _membersStatus = string.Empty;
                _membersPage = 0;
                Members = new ObservableCollection<GroupMemberItem>();
                RaiseMemberListChanged();
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
                OnPropertyChanged(nameof(HasMemberSearchText));
                SearchMembersCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The page number shown in the jump box. Typing a number and pressing Enter (or Jump)
    /// goes straight to that page instead of clicking Next hundreds of times.
    /// </summary>
    public string MemberPageInput
    {
        get => _memberPageInput;
        set
        {
            if (SetProperty(ref _memberPageInput, value))
            {
                JumpToMembersPageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsMemberSearchActive => _isMemberSearchActive;

    /// <summary>False while the search box is empty, so the popup can show its hint text.</summary>
    public bool HasMemberSearchText => !string.IsNullOrWhiteSpace(MemberSearchText);

    public int MemberPageNumber => _membersPage + 1;

    public int MemberPageCount
    {
        get
        {
            var total = SelectedGroup?.MemberCount ?? 0;
            var pages = total <= 0 ? 1 : (total + MembersPageSize - 1) / MembersPageSize;
            // VRChat answers some pages with an empty list even though the group reports
            // more members, so the pager stops at the last page that actually had rows.
            if (_membersTruncated && _membersLastPageWithRows is { } lastPage)
            {
                pages = Math.Min(pages, lastPage + 1);
            }

            return Math.Max(1, pages);
        }
    }

    /// <summary>Bumped on every loaded page; the popup scrolls to the top when it changes.</summary>
    public int MembersPageToken => _membersPageToken;

    public string MembersPageText => IsMemberSearchActive ? string.Empty : $"Page {MemberPageNumber} of {MemberPageCount}";

    /// <summary>The "of 569" half of the pager, with the page number living in the jump box.</summary>
    public string MembersPageCountText => IsMemberSearchActive
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $"of {MemberPageCount:N0}");

    public bool CanJumpToMembersPage => !IsMemberSearchActive
        && !IsLoadingMembers
        && TryParsePageNumber(MemberPageInput, out var page)
        && page >= 1;

    public bool CanGoToPreviousMembersPage => !IsMemberSearchActive && !IsLoadingMembers && _membersPage > 0;

    public bool CanGoToNextMembersPage => !IsMemberSearchActive
        && !IsLoadingMembers
        && Members.Count > 0
        && _membersPage + 1 < MemberPageCount;

    public string MembersSummary => _membersStatus.Length > 0
        ? _membersStatus
        : DescribeMembers(
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
        string? searchQuery = null,
        string? searchNote = null)
    {
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            // A search in flight keeps the previous page on screen, so the wording is
            // what tells the user their query is actually being looked up.
            if (isLoading)
            {
                return $"Searching for “{searchQuery}”…";
            }

            if (loaded == 0)
            {
                return $"No members match “{searchQuery}” in this group.";
            }

            var matches = loaded == 1
                ? $"1 member matches “{searchQuery}”"
                : string.Create(CultureInfo.InvariantCulture, $"{loaded:N0} members match “{searchQuery}”");
            return string.IsNullOrWhiteSpace(searchNote) ? matches : $"{matches} — {searchNote}";
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

    public AsyncRelayCommand FirstMembersPageCommand { get; }

    public AsyncRelayCommand LastMembersPageCommand { get; }

    public AsyncRelayCommand JumpToMembersPageCommand { get; }

    public AsyncRelayCommand SearchMembersCommand { get; }

    public RelayCommand ClearMemberSearchCommand { get; }

    public AsyncRelayCommand RefreshMembersCommand { get; }

    public RelayCommand OpenSelectedGroupCommand { get; }

    /// <summary>Member moderation tools for the selected group (kick, ban, roles, requests…).</summary>
    public GroupModerationViewModel Moderation { get; }

    public bool IsGroupModerationOpen => Moderation.IsOpen;

    public AsyncRelayCommand OpenGroupModerationCommand { get; }

    public RelayCommand CloseGroupModerationCommand { get; }

    public RelayCommand<Group> OpenGroupCommand { get; }

    public AsyncRelayCommand SearchGroupsCommand { get; }

    public AsyncRelayCommand<Group> OpenDiscoveredGroupCommand { get; }

    public RelayCommand OpenSelectedGroupInfoCommand { get; }

    /// <summary>
    /// Opens the moderation tools. The group is re-read with includeRoles so myMember.permissions
    /// and the role definitions are current — those decide which tools are usable.
    /// </summary>
    private async Task OpenGroupModerationAsync()
    {
        if (SelectedGroup is not { } group)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var info = await _api.GetGroupAsync(group.EffectiveId);
            if (info is null)
            {
                ErrorMessage = "That VRChat group could not be found.";
                return;
            }

            Moderation.Open(info);
            // The moderation panel starts at the top of the popup, like a new member page.
            _membersPageToken++;
            OnPropertyChanged(nameof(MembersPageToken));
            OnPropertyChanged(nameof(IsGroupModerationOpen));
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat to load this group.";
        }
        catch (TaskCanceledException)
        {
            ErrorMessage = "The group request timed out. Try again.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not load this group's moderation tools.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CloseGroupModeration()
    {
        Moderation.Close();
        OnPropertyChanged(nameof(IsGroupModerationOpen));
    }

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
        _membersStatus = string.Empty;
        MemberSearchText = string.Empty;
        MemberPageInput = "1";
        Instances = new ObservableCollection<GroupInstance>();
        GroupInfo = null;
        IsGroupInfoOpen = false;
        IsLeaveGroupConfirmOpen = false;
        GroupInfoInstances = new ObservableCollection<GroupInstance>();
        GroupInstancesHidden = false;
        Moderation.Close();
        OnPropertyChanged(nameof(IsGroupModerationOpen));
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

    private Task FirstMembersPageAsync() => SelectedGroup is null || !CanGoToPreviousMembersPage
        ? Task.CompletedTask
        : LoadMembersPageAsync(0);

    private Task LastMembersPageAsync() => SelectedGroup is null || !CanGoToNextMembersPage
        ? Task.CompletedTask
        : LoadMembersPageAsync(MemberPageCount - 1);

    /// <summary>Jumps to the page typed in the pager, clamped to the real page count.</summary>
    private Task JumpToMembersPageAsync()
    {
        if (SelectedGroup is null ||
            IsMemberSearchActive ||
            !TryParsePageNumber(MemberPageInput, out var page))
        {
            return Task.CompletedTask;
        }

        var target = Math.Clamp(page, 1, MemberPageCount) - 1;
        return target == _membersPage ? Task.CompletedTask : LoadMembersPageAsync(target);
    }

    internal static bool TryParsePageNumber(string? text, out int page)
        => int.TryParse(
            (text ?? string.Empty).Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out page);

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
        if (page == 0)
        {
            _membersTruncated = false;
            _membersLastPageWithRows = null;
        }

        try
        {
            var members = await _api.GetGroupMembersAsync(groupId, page * MembersPageSize);
            if (SelectedGroup?.EffectiveId != groupId)
            {
                return;
            }

            var expected = (SelectedGroup?.MemberCount ?? 0) - page * MembersPageSize;
            if (members.Count == 0 && expected > 0)
            {
                // VRChat reports the members are there but sends none back. Large groups
                // only expose their first few hundred members, so keep the page that did
                // load and say why nothing further arrives instead of blanking the list
                // and leaving a pager that still promises hundreds of pages.
                if (_membersLastPageWithRows is { } lastPage && page == lastPage + 1)
                {
                    MarkRosterLimit();
                }
                else
                {
                    _membersStatus = string.Create(
                        CultureInfo.InvariantCulture,
                        $"VRChat returned no members for page {page + 1:N0}.");
                }

                RaiseMemberListChanged();
                return;
            }

            _membersPage = page;
            _membersLastPageWithRows = page;
            _membersPageToken++;
            _isMemberSearchActive = false;
            _lastMemberSearch = null;
            _membersStatus = string.Empty;
            SetMemberRows(members.Select(member => new GroupMemberItem(member, _openMemberProfile)).ToList());
            RaiseMemberListChanged();
        }
        catch (VrcApiException ex) when (ex.StatusCode is 401 or 403 && _membersLastPageWithRows is not null)
        {
            // VRChat answers deeper pages of a big roster with "You're not allowed to view
            // membership details"; that is a limit on the roster, not a broken request.
            MarkRosterLimit();
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
    /// Stops the pager at the last page that had rows and says why nothing further loads:
    /// VRChat reports the full member count but only serves the first few hundred members.
    /// </summary>
    private void MarkRosterLimit()
    {
        if (_membersLastPageWithRows is not { } lastPage)
        {
            return;
        }

        _membersTruncated = true;
        _membersStatus = DescribeMembers(
            Members.Count,
            SelectedGroup?.MemberCount ?? 0,
            false,
            _membersPage) +
            string.Create(
                CultureInfo.InvariantCulture,
                $" — VRChat only returns the first {(lastPage + 1) * MembersPageSize:N0} members of this group to your account.");
    }

    /// <summary>
    /// Searches the group's members. VRChat's member search only filters display names, so a
    /// miss falls back to resolving the query as a username or user id on VRChat and then
    /// checking that account against the group's roster.
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

        // Switch into search mode before the answer arrives. Leaving the previous page
        // and its range summary on screen made a slow or empty search look like nothing
        // had happened at all.
        _membersPage = 0;
        _isMemberSearchActive = true;
        _lastMemberSearch = query;
        _membersStatus = string.Empty;
        RaiseMemberListChanged();

        try
        {
            var result = await FindMembersAsync(groupId, query);
            if (SelectedGroup?.EffectiveId != groupId)
            {
                return;
            }

            SetMemberRows(result.Members.Select(member => new GroupMemberItem(member, _openMemberProfile)).ToList());
            _membersStatus = result.Status;
        }
        catch (VrcApiException ex)
        {
            _membersStatus = $"Could not search this group's members: {ex.Message}";
        }
        catch (HttpRequestException)
        {
            _membersStatus = "Could not reach VRChat to search this group's members.";
        }
        catch (TaskCanceledException)
        {
            _membersStatus = "The member search timed out. Try again.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _membersStatus = "Could not search this group's members.";
        }
        finally
        {
            IsLoadingMembers = false;
            IsBusy = false;
            RaiseMemberListChanged();
        }
    }

    private sealed record MemberSearchResult(IReadOnlyList<GroupMember> Members, string Status);

    private sealed record MemberNameSearch(IReadOnlyList<GroupMember> Members, bool Forbidden);

    /// <summary>How many resolved accounts the fallback checks against the roster.</summary>
    private const int MaxUserSearchChecks = 5;

    /// <summary>
    /// Finds members for a query. Display names go through VRChat's member search; a miss is
    /// resolved as a username or user id through the public user endpoints and then matched
    /// against the roster by account id. The single-member endpoint is only a shortcut: on
    /// groups where this account may not read membership details it answers 403, which used
    /// to fail the whole search with "You're not allowed to view membership details."
    /// </summary>
    private async Task<MemberSearchResult> FindMembersAsync(string groupId, string query)
    {
        var byName = await SearchMembersByNameAsync(groupId, query);
        if (byName.Members.Count > 0)
        {
            return new MemberSearchResult(byName.Members, string.Empty);
        }

        var candidates = await ResolveUserCandidatesAsync(query);
        var found = new List<GroupMember>();
        VrcUser? firstResolved = null;
        foreach (var user in candidates)
        {
            firstResolved ??= user;
            foreach (var member in await FindMemberRowsAsync(groupId, user))
            {
                if (found.All(existing => !string.Equals(
                        existing.EffectiveUserId,
                        member.EffectiveUserId,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(member);
                }
            }

            if (found.Count >= MaxUserSearchChecks)
            {
                break;
            }
        }

        if (found.Count > 0)
        {
            return new MemberSearchResult(
                found,
                DescribeMembers(
                    found.Count,
                    SelectedGroup?.MemberCount ?? 0,
                    false,
                    0,
                    query,
                    "matched by username or user ID"));
        }

        if (firstResolved is not null)
        {
            return new MemberSearchResult(
                Array.Empty<GroupMember>(),
                byName.Forbidden
                    ? $"“{DescribeUser(firstResolved)}” is on VRChat, but this account may not view this group's membership details."
                    : $"“{DescribeUser(firstResolved)}” is not a member of this group.");
        }

        return new MemberSearchResult(
            Array.Empty<GroupMember>(),
            byName.Forbidden
                ? "VRChat does not let this account search this group's members. Open the group on vrchat.com to browse the roster."
                : string.Empty);
    }

    /// <summary>
    /// Display-name search that reports the membership-details wall instead of throwing, so a
    /// blocked search still explains itself.
    /// </summary>
    private async Task<MemberNameSearch> SearchMembersByNameAsync(string groupId, string query)
    {
        var text = query.Trim().TrimStart('@');
        if (text.Length < 3)
        {
            return new MemberNameSearch(Array.Empty<GroupMember>(), false);
        }

        try
        {
            return new MemberNameSearch(await _api.SearchGroupMembersAsync(groupId, text), false);
        }
        catch (VrcApiException ex) when (ex.StatusCode is 401 or 403)
        {
            return new MemberNameSearch(Array.Empty<GroupMember>(), true);
        }
    }

    /// <summary>Roster rows for one VRChat account: the exact member lookup, then a name search.</summary>
    private async Task<IReadOnlyList<GroupMember>> FindMemberRowsAsync(string groupId, VrcUser user)
    {
        var direct = await TryGetGroupMemberAsync(groupId, user.Id);
        if (direct is not null)
        {
            // The membership record carries no name or avatar of its own; fill it from the
            // user we resolved so the row is not a bare id with an initial.
            return new[] { WithResolvedUser(direct, user) };
        }

        var name = !string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName : user.Username;
        if (string.IsNullOrWhiteSpace(name))
        {
            return Array.Empty<GroupMember>();
        }

        var results = (await SearchMembersByNameAsync(groupId, name)).Members;
        return results
            .Where(member =>
                string.Equals(member.EffectiveUserId, user.Id, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(user.Username) &&
                 string.Equals(member.User?.Username, user.Username, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>The single-member lookup, treating the group's 401/403 as "not readable".</summary>
    private async Task<GroupMember?> TryGetGroupMemberAsync(string groupId, string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        try
        {
            return await _api.GetGroupMemberAsync(groupId, userId);
        }
        catch (VrcApiException ex) when (ex.StatusCode is 401 or 403 or 404)
        {
            return null;
        }
    }

    /// <summary>VRChat accounts the query could mean, best guess first.</summary>
    private async Task<IReadOnlyList<VrcUser>> ResolveUserCandidatesAsync(string query)
    {
        var candidates = new List<VrcUser>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A user id copied from a profile is read through the public user endpoint, which
        // every account may use, rather than the group's membership endpoint.
        var userId = query.StartsWith("usr_", StringComparison.OrdinalIgnoreCase)
            ? query
            : LooksLikeUserId(query) ? "usr_" + query : null;
        if (userId is not null)
        {
            var user = await TryGetUserAsync(userId);
            if (user is not null && seen.Add(user.Id))
            {
                candidates.Add(user);
            }
        }

        try
        {
            foreach (var user in await _api.SearchUserListAsync(query.TrimStart('@'), MaxUserSearchChecks))
            {
                if (!string.IsNullOrWhiteSpace(user.Id) && seen.Add(user.Id))
                {
                    candidates.Add(user);
                }
            }
        }
        catch (Exception ex)
        {
            // A failed user search must not hide the plain "no members match" answer.
            Debug.WriteLine(ex);
        }

        return candidates;
    }

    /// <summary>Copies the resolved account's name and avatar onto a membership record.</summary>
    private static GroupMember WithResolvedUser(GroupMember member, VrcUser user)
    {
        if (member.User is not null)
        {
            return member;
        }

        member.User = new GroupMember.GroupMemberUser
        {
            Id = user.Id,
            DisplayName = user.DisplayName,
            Username = user.Username,
            IconUrl = user.IconUrl ?? string.Empty,
            ThumbnailUrl = user.ProfilePicOverrideThumbnail ?? string.Empty,
            CurrentAvatarThumbnailImageUrl = user.CurrentAvatarThumbnailImageUrl ?? string.Empty
        };
        return member;
    }

    private async Task<VrcUser?> TryGetUserAsync(string userId)
    {
        try
        {
            return await _api.GetUserAsync(userId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    /// <summary>True for text shaped like a user id, with or without the usr_ prefix.</summary>
    private static bool LooksLikeUserId(string text)
        => text.Length >= 4 && text.All(character => Uri.IsHexDigit(character) || character == '-');

    private static string DescribeUser(VrcUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            return string.IsNullOrWhiteSpace(user.Username)
                ? user.DisplayName
                : $"{user.DisplayName} (@{user.Username})";
        }

        return !string.IsNullOrWhiteSpace(user.Username) ? user.Username : user.Id;
    }

    private async Task ClearMemberSearchAsync()
    {
        MemberSearchText = string.Empty;
        _membersStatus = string.Empty;
        if (SelectedGroup is null)
        {
            return;
        }

        await LoadMembersPageAsync(_membersPage);
    }

    /// <summary>
    /// Swaps the roster rows in place. Rebuilding the collection empties the list for a
    /// frame and makes the popup flicker, so rows are replaced one by one and the page
    /// token scrolls the popup to the top once the new page is in place.
    /// </summary>
    private void SetMemberRows(IReadOnlyList<GroupMemberItem> rows)
    {
        var members = Members;
        var shared = Math.Min(members.Count, rows.Count);
        for (var index = 0; index < shared; index++)
        {
            members[index] = rows[index];
        }

        while (members.Count > rows.Count)
        {
            members.RemoveAt(members.Count - 1);
        }

        for (var index = shared; index < rows.Count; index++)
        {
            members.Add(rows[index]);
        }
    }

    private void RaiseMemberListChanged()
    {
        if (!_isMemberSearchActive)
        {
            _memberPageInput = MemberPageNumber.ToString(CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(MemberPageInput));
        }

        OnPropertyChanged(nameof(MembersSummary));
        OnPropertyChanged(nameof(MembersPageText));
        OnPropertyChanged(nameof(MembersPageCountText));
        OnPropertyChanged(nameof(MembersPageToken));
        OnPropertyChanged(nameof(MemberPageNumber));
        OnPropertyChanged(nameof(MemberPageCount));
        OnPropertyChanged(nameof(IsMemberSearchActive));
        OnPropertyChanged(nameof(CanGoToPreviousMembersPage));
        OnPropertyChanged(nameof(CanGoToNextMembersPage));
        OnPropertyChanged(nameof(CanJumpToMembersPage));
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
