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
/// View model for the activity feed / timeline. Friend data comes from the
/// signed-in <see cref="VrcApiClient"/>, so the feed shares the session.
/// </summary>
public sealed class ActivityFeedViewModel : ViewModelBase
{
    private readonly ActivityFeedService _feedService;
    private readonly VrcApiClient _api;

    private ObservableCollection<ActivityEntry> _entries = new();
    private List<ActivityEntry> _allEntries = new();
    private IReadOnlyList<VrcUser>? _previousFriends;
    private ActivityType? _selectedFilter;
    private string _searchQuery = string.Empty;
    private bool _isBusy;
    private bool _hasLoaded;
    private string? _errorMessage;

    private readonly List<IRelayCommand> _commands = new();

    public ActivityFeedViewModel(VrcApiClient api)
    {
        _feedService = new ActivityFeedService();
        _api = api;

        RefreshCommand = Track(new AsyncRelayCommand(RefreshAsync, () => !IsBusy));
        ClearSearchCommand = Track(new RelayCommand(ClearSearch));
    }

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

    public ObservableCollection<ActivityEntry> Entries
    {
        get => _entries;
        set => SetProperty(ref _entries, value);
    }

    public ActivityType? SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                ApplyFilterAndSearch();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ApplyFilterAndSearch();
            }
        }
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

    /// <summary>True once the feed has loaded, so navigation does not refetch.</summary>
    public bool HasLoaded
    {
        get => _hasLoaded;
        private set => SetProperty(ref _hasLoaded, value);
    }

    /// <summary>Clears the feed so a different account builds its own.</summary>
    public void Reset()
    {
        _allEntries = new List<ActivityEntry>();
        _previousFriends = null;
        _searchQuery = string.Empty;
        OnPropertyChanged(nameof(SearchQuery));
        SelectedFilter = null;
        Entries = new ObservableCollection<ActivityEntry>();
        HasLoaded = false;
        ErrorMessage = null;
    }

    public ICommand RefreshCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            // Online friends arrive first, so the feed can render before the
            // offline pages (large accounts need dozens of requests) finish.
            var friends = await _api.GetFriendsAsync(forceRefresh: false, onPartial: partial =>
            {
                _allEntries = _feedService.BuildFeedFromFriends(partial.ToList());
                ApplyFilterAndSearch();
            });

            _allEntries = _feedService.BuildFeedFromFriends(friends.ToList(), _previousFriends);
            _previousFriends = friends.ToList();
            ApplyFilterAndSearch();
            _ = HydrateAvatarNamesAsync(_allEntries);
            HasLoaded = true;
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
            ErrorMessage = "The activity request timed out. Try again.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not load activity.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task RefreshAsync() => LoadAsync();

    private async Task HydrateAvatarNamesAsync(IReadOnlyList<ActivityEntry> entries)
    {
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(entry =>
                     entry.Type == ActivityType.AvatarChanged && entry.HasAvatar))
        {
            if (!names.TryGetValue(entry.AvatarId, out var name))
            {
                try
                {
                    var avatar = await _api.GetAvatarAsync(entry.AvatarId);
                    name = avatar?.Name;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    name = null;
                }

                names[entry.AvatarId] = name;
            }

            entry.SetAvatarName(name);
        }
    }

    /// <summary>
    /// Rebuilds the visible list from the full feed, so changing the filter or
    /// the search never drops entries from the underlying set.
    /// </summary>
    private void ApplyFilterAndSearch()
    {
        var filtered = _feedService.FilterByType(_allEntries, SelectedFilter);
        filtered = _feedService.Search(filtered, SearchQuery);
        Entries = new ObservableCollection<ActivityEntry>(filtered);
    }

    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        ApplyFilterAndSearch();
    }
}
