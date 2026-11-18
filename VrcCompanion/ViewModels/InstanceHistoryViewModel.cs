using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// The signed-in account's locally recorded instance history. Rows are real
/// visits from the VRChat log; metadata and player avatars are enriched from
/// VRChat's API when the popup opens.
/// </summary>
public sealed class InstanceHistoryViewModel : ViewModelBase, IDisposable
{
    private readonly VrcApiClient _api;
    private readonly InstanceHistoryStore _store;
    private readonly ObservableCollection<InstanceHistoryItem> _entries = new();
    private readonly ICollectionView _entriesView;
    private string _accountUserId = string.Empty;
    private string _accountDisplayName = string.Empty;
    private string _searchText = string.Empty;
    private string _filterUserId = string.Empty;
    private string _filterDisplayName = string.Empty;
    private bool _useNameOnlyFallback;
    private bool _isOpen;
    private bool _isLoading;
    private InstanceHistoryItem? _selectedEntry;
    private bool _isPlayersTab = true;
    private bool _canReturnToProfile;
    private string _statusMessage = string.Empty;
    private int _refreshVersion;

    public InstanceHistoryViewModel(VrcApiClient api, InstanceHistoryStore store)
    {
        _api = api;
        _store = store;
        _entriesView = CollectionViewSource.GetDefaultView(_entries);
        _entriesView.Filter = FilterEntry;

        CloseCommand = new RelayCommand(Close);
        BackToProfileCommand = new RelayCommand(Close);
        BackCommand = new RelayCommand(BackToHistory, () => SelectedEntry is not null);
        ShowPlayersCommand = new RelayCommand(() => IsPlayersTab = true);
        ShowTimelineCommand = new RelayCommand(() => IsPlayersTab = false);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsOpen && !IsLoading);
        ClearFilterCommand = new RelayCommand(ClearFilter, () => IsFiltered);
    }

    public Action<InstanceHistoryItem>? LaunchRequested { get; set; }

    public Action<InstanceHistoryItem>? OpenInGameRequested { get; set; }

    public Action<InstanceHistoryPlayerItem>? PlayerProfileRequested { get; set; }

    public ObservableCollection<InstanceHistoryItem> Entries => _entries;

    public ICollectionView EntriesView => _entriesView;

    public RelayCommand CloseCommand { get; }
    public RelayCommand BackToProfileCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand ShowPlayersCommand { get; }
    public RelayCommand ShowTimelineCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ClearFilterCommand { get; }

    public string AccountUserId => _accountUserId;

    public bool IsFiltered => !string.IsNullOrWhiteSpace(_filterUserId);

    public string FilterText => IsFiltered
        ? $"Shared instances with {_filterDisplayName}"
        : string.Empty;

    public string DescriptionText => IsFiltered
        ? $"Instances you were in with {_filterDisplayName}. Only visits recorded on this PC are shown."
        : "Every world you entered, with the players VRChat logged while you were there.";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                _entriesView.Refresh();
                OnPropertyChanged(nameof(SummaryText));
                OnPropertyChanged(nameof(HasNoEntries));
            }
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetProperty(ref _isOpen, value))
            {
                OnPropertyChanged(nameof(IsListVisible));
                OnPropertyChanged(nameof(IsDetailVisible));
                RefreshCommand.RaiseCanExecuteChanged();
                if (!value)
                {
                    SelectedEntry = null;
                }
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsNotLoading));
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNotLoading => !IsLoading;

    public bool HasEntries => _entries.Count > 0;

    public bool HasNoEntries => !IsLoading && !_entriesView.Cast<object>().Any();

    public bool IsListVisible => IsOpen && SelectedEntry is null;

    public bool IsDetailVisible => IsOpen && SelectedEntry is not null;

    public bool CanReturnToProfile
    {
        get => _canReturnToProfile;
        set => SetProperty(ref _canReturnToProfile, value);
    }

    public string SummaryText
    {
        get
        {
            var shown = _entriesView.Cast<object>().Count();
            if (_entries.Count == 0)
            {
                return "No instances recorded on this PC yet.";
            }

            if (IsFiltered)
            {
                return shown == 1
                    ? $"1 shared instance with {_filterDisplayName}"
                    : $"{shown:N0} shared instances with {_filterDisplayName}";
            }

            return shown == _entries.Count
                ? $"{_entries.Count:N0} instance(s) · recorded locally"
                : $"{shown:N0} shown of {_entries.Count:N0} instances";
        }
    }

    public string EmptyTitle => _entries.Count == 0
        ? "No instance history yet"
        : IsFiltered
            ? "No shared instances"
            : "No matching instances";

    public string EmptyMessage => _entries.Count == 0
        ? "Join a world in VRChat while this app is running. The history is built from VRChat's own log, not sample data."
        : IsFiltered && !string.IsNullOrWhiteSpace(SearchText)
            ? $"No instance with {_filterDisplayName} matches \u201c{SearchText.Trim()}\u201d."
            : IsFiltered
                ? $"You have no recorded instance with {_filterDisplayName} yet."
                : $"No instance matches \u201c{SearchText.Trim()}\u201d.";

    public InstanceHistoryItem? SelectedEntry
    {
        get => _selectedEntry;
        private set
        {
            if (SetProperty(ref _selectedEntry, value))
            {
                OnPropertyChanged(nameof(IsListVisible));
                OnPropertyChanged(nameof(IsDetailVisible));
                OnPropertyChanged(nameof(IsPlayersTab));
                OnPropertyChanged(nameof(IsTimelineTab));
                OnPropertyChanged(nameof(DetailPlayerCountText));
                OnPropertyChanged(nameof(DetailDurationText));
                OnPropertyChanged(nameof(DetailTimeRangeText));
                BackCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPlayersTab
    {
        get => _isPlayersTab;
        private set
        {
            if (SetProperty(ref _isPlayersTab, value))
            {
                OnPropertyChanged(nameof(IsTimelineTab));
            }
        }
    }

    public bool IsTimelineTab => !IsPlayersTab;

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

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public string DetailPlayerCountText => SelectedEntry is null
        ? string.Empty
        : SelectedEntry.PlayerCount == 1
            ? "1 player"
            : $"{SelectedEntry.PlayerCount:N0} players";

    public string DetailDurationText => SelectedEntry is null
        ? string.Empty
        : $"You were here {SelectedEntry.DurationText}";

    public string DetailTimeRangeText => SelectedEntry is null
        ? string.Empty
        : SelectedEntry.TimeRangeText;

    public void SetAccount(VrcUser user)
    {
        var changed = !string.Equals(_accountUserId, user.Id, StringComparison.Ordinal);
        _accountUserId = user.Id ?? string.Empty;
        _accountDisplayName = user.DisplayName ?? string.Empty;
        if (changed)
        {
            ClearFilterCore();
        }
        if (changed && IsOpen)
        {
            _ = RefreshAsync();
        }
    }

    public void Show()
    {
        CanReturnToProfile = false;
        ClearFilterCore();
        Open();
    }

    public void ShowForUser(string userId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            Show();
            return;
        }

        _filterUserId = userId;
        _filterDisplayName = string.IsNullOrWhiteSpace(displayName) ? "this person" : displayName;
        // Legacy log lines may have a display name but no usr_ id. Resolve those
        // rows now, while we have both pieces of identity from the profile.
        var repaired = _store.AssignPlayerUserId(_accountUserId, displayName, userId);
        _useNameOnlyFallback = repaired == 0 &&
            _store.CanUseNameOnlyFallback(_accountUserId, displayName);
        RaiseFilterChanged();
        Open();
    }

    private void Open()
    {
        // A search typed in an earlier visit must not hide every row when the
        // popup is opened again.
        SearchText = string.Empty;
        IsOpen = true;
        SelectedEntry = null;
        IsPlayersTab = true;
        StatusMessage = string.Empty;
        _ = RefreshAsync();
    }

    public void Close()
    {
        CanReturnToProfile = false;
        IsOpen = false;
        SelectedEntry = null;
        StatusMessage = string.Empty;
        SearchText = string.Empty;
        ClearFilterCore();
    }

    public async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(_accountUserId))
        {
            _entries.Clear();
            OnEntryStateChanged();
            return;
        }

        var version = ++_refreshVersion;
        IsLoading = true;
        try
        {
            var rows = _store.ForAccount(_accountUserId);
            var items = rows
                .GroupBy(entry => entry.Location, StringComparer.Ordinal)
                .Select(group => MergeEntries(group.ToList()))
                .OrderByDescending(entry => entry.JoinedAt)
                .Select(entry => new InstanceHistoryItem(
                    entry,
                    _accountUserId,
                    LaunchRequested,
                    OpenInGameRequested,
                    SelfInviteAsync,
                    CopyInstanceId,
                    DeleteEntry,
                    OpenEntry,
                    PlayerProfileRequested))
                .ToList();

            if (version != _refreshVersion)
            {
                return;
            }

            _entries.Clear();
            foreach (var item in items)
            {
                _entries.Add(item);
            }

            OnEntryStateChanged();
            await HydrateWorldsAsync(items, version);
        }
        finally
        {
            if (version == _refreshVersion)
            {
                IsLoading = false;
                OnEntryStateChanged();
            }
        }
    }

    private async Task HydrateWorldsAsync(IReadOnlyList<InstanceHistoryItem> items, int version)
    {
        var missing = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Entry.WorldId) &&
                           (string.IsNullOrWhiteSpace(item.Entry.WorldName) ||
                            string.IsNullOrWhiteSpace(item.Entry.WorldThumbnailUrl) ||
                            string.IsNullOrWhiteSpace(item.Entry.WorldAuthorName)))
            .GroupBy(item => item.Entry.WorldId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(40)
            .ToList();

        foreach (var item in missing)
        {
            if (version != _refreshVersion)
            {
                return;
            }

            try
            {
                var world = await _api.GetWorldAsync(item.Entry.WorldId);
                if (world is null || version != _refreshVersion)
                {
                    continue;
                }

                foreach (var matching in items.Where(candidate =>
                             string.Equals(candidate.Entry.WorldId, item.Entry.WorldId, StringComparison.Ordinal)))
                {
                    matching.ApplyWorld(world);
                    _store.UpsertMetadata(matching.Entry);
                }
            }
            catch (Exception)
            {
                // Missing/deleted worlds still have a real location row.
            }
        }

        var creatorMissing = items
            .Where(item => string.IsNullOrWhiteSpace(item.Entry.OwnerName) &&
                           !string.IsNullOrWhiteSpace(item.Entry.OwnerId))
            .Take(20)
            .ToList();
        foreach (var item in creatorMissing)
        {
            try
            {
                var name = item.Entry.OwnerId.StartsWith("grp_", StringComparison.Ordinal)
                    ? (await _api.GetGroupAsync(item.Entry.OwnerId))?.Name
                    : item.Entry.OwnerId.StartsWith("usr_", StringComparison.Ordinal)
                        ? (await _api.GetUserAsync(item.Entry.OwnerId))?.DisplayName
                        : null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    foreach (var matching in items.Where(candidate =>
                                 string.Equals(candidate.Entry.OwnerId, item.Entry.OwnerId, StringComparison.Ordinal)))
                    {
                        matching.ApplyOwner(name!);
                        _store.UpsertMetadata(matching.Entry);
                    }
                }
            }
            catch (Exception)
            {
                // Keep the creator id when VRChat will not resolve the profile.
            }
        }

        _store.Save();
    }

    private void OnEntryStateChanged()
    {
        _entriesView.Refresh();
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(HasNoEntries));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private bool FilterEntry(object item)
    {
        if (item is not InstanceHistoryItem entry)
        {
            return false;
        }

        var query = SearchText.Trim();
        if (IsFiltered &&
            !entry.Players.Any(player =>
                string.Equals(player.UserId, _filterUserId, StringComparison.Ordinal) ||
                (_useNameOnlyFallback &&
                 string.IsNullOrWhiteSpace(player.UserId) &&
                 string.Equals(player.DisplayName, _filterDisplayName, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        if (query.Length == 0)
        {
            return true;
        }

        return entry.WorldName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               entry.CreatorText.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               entry.Entry.Location.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenEntry(InstanceHistoryItem item)
    {
        SelectedEntry = item;
        IsPlayersTab = true;
        StatusMessage = string.Empty;
        _ = HydrateEntryAsync(item);
    }

    private async Task HydrateEntryAsync(InstanceHistoryItem item)
    {
        foreach (var player in item.Players.Where(player => player.CanOpenProfile))
        {
            if (SelectedEntry != item)
            {
                return;
            }

            try
            {
                var user = await _api.GetUserAsync(player.UserId);
                if (user is not null)
                {
                    player.ApplyProfile(user);
                }
            }
            catch (Exception)
            {
                // The row remains usable with the log's display name.
            }

            if (SelectedEntry != item || !string.IsNullOrWhiteSpace(player.AvatarUrl))
            {
                continue;
            }

            try
            {
                // Non-friends commonly have their avatar omitted from the user
                // endpoint but still exposed on the public profile. This is the
                // same fallback already used by the instance and notification
                // player rows, now shared by instance history too.
                var profile = await _api.GetProfileAsync(player.UserId);
                if (profile is not null)
                {
                    player.ApplyProfile(profile);
                }
            }
            catch (Exception)
            {
                // Keep the log name and the generic player glyph.
            }
        }

        if (SelectedEntry == item &&
            string.IsNullOrWhiteSpace(item.CreatorText) &&
            item.Entry.OwnerId.StartsWith("usr_", StringComparison.Ordinal))
        {
            try
            {
                var creator = await _api.GetUserAsync(item.Entry.OwnerId);
                if (creator is not null)
                {
                    item.ApplyOwner(creator.DisplayName);
                    _store.UpsertMetadata(item.Entry);
                }
            }
            catch (Exception)
            {
                // Leave the creator blank rather than inventing a name.
            }
        }

        _store.Save();
    }

    private void BackToHistory()
    {
        SelectedEntry = null;
        StatusMessage = string.Empty;
    }

    private void ClearFilter()
    {
        if (!IsFiltered)
        {
            return;
        }

        ClearFilterCore();
        _entriesView.Refresh();
        OnEntryStateChanged();
    }

    private void ClearFilterCore()
    {
        _useNameOnlyFallback = false;
        if (!IsFiltered)
        {
            return;
        }

        _filterUserId = string.Empty;
        _filterDisplayName = string.Empty;
        RaiseFilterChanged();
        _entriesView.Refresh();
        OnEntryStateChanged();
    }

    private void RaiseFilterChanged()
    {
        ClearFilterCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(FilterText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private async Task SelfInviteAsync(InstanceHistoryItem item)
    {
        try
        {
            var shortName = await TryGetShortNameAsync(item.Entry.Location);
            await _api.InviteSelfAsync(item.Entry.Location, shortName);
            StatusMessage = $"Self-invite sent for {item.WorldName}.";
        }
        catch (VrcApiException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "Could not reach VRChat to send the self-invite.";
        }
        catch (Exception)
        {
            StatusMessage = "Could not send the self-invite. The instance may be closed.";
        }
    }

    private async Task<string?> TryGetShortNameAsync(string location)
    {
        try
        {
            return await _api.GetInstanceShortNameAsync(location);
        }
        catch (Exception)
        {
            // Public instances do not need a short name, and a private one
            // may not expose it. The self-invite request still gets a chance.
            return null;
        }
    }

    private void CopyInstanceId(InstanceHistoryItem item)
    {
        try
        {
            Clipboard.SetText(item.Entry.Location);
            StatusMessage = "Instance ID copied.";
        }
        catch (Exception)
        {
            StatusMessage = "Could not access the clipboard.";
        }
    }

    private void DeleteEntry(InstanceHistoryItem item)
    {
        if (item.Entry.SourceEntryIds.Count > 0)
        {
            _store.DeleteMany(item.Entry.SourceEntryIds);
        }
        else
        {
            _store.Delete(item.Entry.Id);
        }
        _store.Save();
        _entries.Remove(item);
        if (ReferenceEquals(SelectedEntry, item))
        {
            SelectedEntry = null;
        }

        OnEntryStateChanged();
        StatusMessage = "Instance removed from local history.";
    }

    /// <summary>
    /// Repeated visits to the exact same world + instance are one history row.
    /// Different instance ids, and therefore different locations, stay apart.
    /// </summary>
    private static InstanceHistoryEntry MergeEntries(IReadOnlyList<InstanceHistoryEntry> entries)
    {
        if (entries.Count == 1)
        {
            return entries[0];
        }

        var ordered = entries.OrderBy(entry => entry.JoinedAt).ToList();
        var first = ordered[0];
        var latest = ordered[^1];
        var merged = new InstanceHistoryEntry
        {
            Id = latest.Id,
            AccountUserId = first.AccountUserId,
            Location = first.Location,
            WorldId = first.WorldId,
            InstanceId = first.InstanceId,
            WorldName = LastNonEmpty(ordered, entry => entry.WorldName),
            WorldThumbnailUrl = LastNonEmpty(ordered, entry => entry.WorldThumbnailUrl),
            WorldAuthorName = LastNonEmpty(ordered, entry => entry.WorldAuthorName),
            OwnerId = LastNonEmpty(ordered, entry => entry.OwnerId),
            OwnerName = LastNonEmpty(ordered, entry => entry.OwnerName),
            JoinedAt = first.JoinedAt,
            // The newest stored session is authoritative. An older stale
            // session must not make a completed merged row look live.
            LeftAt = latest.LeftAt is null
                ? null
                : ordered.Max(entry => entry.LeftAt),
            LastSeenAt = ordered.Max(entry => entry.LastSeenAt),
            LastEventAt = ordered.Max(entry => entry.LastEventAt),
            EventIds = ordered.SelectMany(entry => entry.EventIds).Distinct().ToList(),
            SourceEntryIds = ordered
                .Select(entry => entry.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList()
        };

        foreach (var player in ordered.SelectMany(entry => entry.Players))
        {
            var target = merged.Players.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, player.Key, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                target = new InstanceHistoryPlayer
                {
                    UserId = player.UserId,
                    DisplayName = player.DisplayName,
                    AvatarUrl = player.AvatarUrl
                };
                merged.Players.Add(target);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(player.DisplayName))
                {
                    target.DisplayName = player.DisplayName;
                }
                if (!string.IsNullOrWhiteSpace(player.AvatarUrl))
                {
                    target.AvatarUrl = player.AvatarUrl;
                }
            }

            foreach (var visit in player.Visits)
            {
                if (target.Visits.Any(existing =>
                        existing.JoinedAt == visit.JoinedAt &&
                        existing.LeftAt == visit.LeftAt))
                {
                    continue;
                }

                target.Visits.Add(new InstanceHistoryVisit
                {
                    JoinedAt = visit.JoinedAt,
                    LeftAt = visit.LeftAt,
                    LastSeenAt = visit.LastSeenAt
                });
            }
        }

        foreach (var player in merged.Players)
        {
            player.Visits.Sort((left, right) => left.JoinedAt.CompareTo(right.JoinedAt));
        }

        return merged;
    }

    private static string LastNonEmpty(
        IReadOnlyList<InstanceHistoryEntry> entries,
        Func<InstanceHistoryEntry, string> selector)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var value = selector(entries[i]);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    public void Dispose()
    {
        _refreshVersion++;
    }
}

public sealed class InstanceHistoryItem : ViewModelBase
{
    private static readonly Regex RegionPattern = new(
        "~region\\((?<region>[^)]+)\\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private string _worldName;
    private string _worldThumbnailUrl;
    private string _creatorText;
    private bool _isMenuOpen;

    public InstanceHistoryItem(
        InstanceHistoryEntry entry,
        string accountUserId,
        Action<InstanceHistoryItem>? launch,
        Action<InstanceHistoryItem>? openInGame,
        Func<InstanceHistoryItem, Task> selfInvite,
        Action<InstanceHistoryItem> copyInstanceId,
        Action<InstanceHistoryItem> delete,
        Action<InstanceHistoryItem> open,
        Action<InstanceHistoryPlayerItem>? openPlayer)
    {
        Entry = entry;
        _worldName = entry.WorldName;
        _worldThumbnailUrl = entry.WorldThumbnailUrl;
        _creatorText = entry.OwnerName;
        Players = entry.Players
            .OrderByDescending(player => string.Equals(player.UserId, accountUserId, StringComparison.Ordinal))
            .ThenBy(player => player.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(player => new InstanceHistoryPlayerItem(
                player,
                string.Equals(player.UserId, accountUserId, StringComparison.Ordinal),
                accountUserId))
            .ToList();
        foreach (var player in Players)
        {
            player.ProfileRequested = openPlayer;
        }
        Timeline = BuildTimeline(entry, accountUserId);

        OpenCommand = new RelayCommand(() => open(this));
        LaunchCommand = new RelayCommand(
            () => launch?.Invoke(this),
            () => launch is not null && LaunchService.IsLocation(Entry.Location));
        OpenInGameCommand = new RelayCommand(
            () => openInGame?.Invoke(this),
            () => openInGame is not null && LaunchService.IsLocation(Entry.Location));
        SelfInviteCommand = new AsyncRelayCommand(
            () => selfInvite(this),
            () => LaunchService.IsLocation(Entry.Location));
        CopyInstanceIdCommand = new RelayCommand(() => copyInstanceId(this));
        DeleteCommand = new RelayCommand(() => delete(this));
        ToggleMenuCommand = new RelayCommand(() => IsMenuOpen = !IsMenuOpen);
    }

    public InstanceHistoryEntry Entry { get; }

    public IReadOnlyList<InstanceHistoryPlayerItem> Players { get; }

    public IReadOnlyList<InstanceHistoryTimelineItem> Timeline { get; }

    public RelayCommand OpenCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand OpenInGameCommand { get; }
    public AsyncRelayCommand SelfInviteCommand { get; }
    public RelayCommand CopyInstanceIdCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ToggleMenuCommand { get; }

    public bool IsMenuOpen
    {
        get => _isMenuOpen;
        set => SetProperty(ref _isMenuOpen, value);
    }

    public string WorldName
    {
        get => string.IsNullOrWhiteSpace(_worldName) ? "Loading world…" : _worldName;
        private set
        {
            if (SetProperty(ref _worldName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(WorldName));
                OnPropertyChanged(nameof(DetailLocationText));
            }
        }
    }

    public string WorldThumbnailUrl
    {
        get => _worldThumbnailUrl;
        private set => SetProperty(ref _worldThumbnailUrl, value ?? string.Empty);
    }

    public string CreatorText
    {
        get => _creatorText;
        private set
        {
            if (SetProperty(ref _creatorText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CreatorDisplayText));
            }
        }
    }

    public string CreatorDisplayText => !string.IsNullOrWhiteSpace(CreatorText)
        ? CreatorText
        : !string.IsNullOrWhiteSpace(Entry.WorldAuthorName)
            ? Entry.WorldAuthorName
            : "Unknown creator";

    public string DateText => Entry.JoinedAt.ToLocalTime().ToString("MM/dd/yyyy, h:mm tt", CultureInfo.CurrentCulture);

    public string DateLongText => Entry.JoinedAt.ToLocalTime().ToString("MMMM d, yyyy 'at' h:mm tt", CultureInfo.CurrentCulture);

    public string TimeText => FormatDuration(Entry.Duration);

    public string DurationText => FormatDuration(Entry.Duration);

    public string TimeRangeText
    {
        get
        {
            var start = Entry.JoinedAt.ToLocalTime();
            var end = Entry.LeftAt?.ToLocalTime();
            return end is null ? $"{start:h:mm tt} – now" : $"{start:h:mm tt} – {end:h:mm tt}";
        }
    }

    public string RegionCode
    {
        get
        {
            var match = RegionPattern.Match(Entry.Location);
            if (match.Success)
            {
                return match.Groups["region"].Value.ToUpperInvariant();
            }
            return "??";
        }
    }

    public string AccessText => InstancePresentation.AccessLabel(null, Entry.InstanceId);

    public string ShortInstanceId
    {
        get
        {
            var value = Entry.InstanceId ?? string.Empty;
            var separator = value.IndexOf('~');
            return separator > 0 ? value[..separator] : value;
        }
    }

    public string DetailLocationText => $"{WorldName} · #{ShortInstanceId}";

    public int PlayerCount => Players.Count;

    public bool HasPlayers => Players.Count > 0;

    public string PlayersSummary => PlayerCount == 1 ? "1 player" : $"{PlayerCount:N0} players";

    public string TimelineSummary => Entry.LeftAt is null
        ? $"You have been here {DurationText} · started {Entry.JoinedAt.ToLocalTime():h:mm tt}"
        : $"You were here {DurationText} · {TimeRangeText}";

    public string AvatarUrl
    {
        get
        {
            return string.IsNullOrWhiteSpace(Entry.WorldThumbnailUrl)
                ? string.Empty
                : ImageUrlHelper.ToThumbnailUrl(Entry.WorldThumbnailUrl, 160) ?? Entry.WorldThumbnailUrl;
        }
    }

    public void ApplyWorld(VrcWorld world)
    {
        if (string.IsNullOrWhiteSpace(Entry.WorldName))
        {
            Entry.WorldName = world.Name ?? string.Empty;
            _worldName = Entry.WorldName;
            OnPropertyChanged(nameof(WorldName));
        }

        if (string.IsNullOrWhiteSpace(Entry.WorldThumbnailUrl))
        {
            Entry.WorldThumbnailUrl = ImageUrlHelper.PickWorldThumbnailUrl(
                world.ThumbnailImageUrl,
                world.ImageUrl,
                512) ?? string.Empty;
            _worldThumbnailUrl = Entry.WorldThumbnailUrl;
            OnPropertyChanged(nameof(WorldThumbnailUrl));
            OnPropertyChanged(nameof(AvatarUrl));
        }

        if (string.IsNullOrWhiteSpace(Entry.WorldAuthorName))
        {
            Entry.WorldAuthorName = world.AuthorName ?? string.Empty;
            OnPropertyChanged(nameof(CreatorDisplayText));
        }

        OnPropertyChanged(nameof(DetailLocationText));
    }

    public void ApplyOwner(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Entry.OwnerName = name;
        CreatorText = name;
    }

    private static IReadOnlyList<InstanceHistoryTimelineItem> BuildTimeline(
        InstanceHistoryEntry entry,
        string accountUserId)
    {
        var result = new List<InstanceHistoryTimelineItem>();
        var self = entry.Players.FirstOrDefault(player =>
            string.Equals(player.UserId, accountUserId, StringComparison.Ordinal));
        if (self is not null)
        {
            foreach (var visit in self.Visits)
            {
                result.Add(new InstanceHistoryTimelineItem(
                    visit.JoinedAt,
                    "You joined the instance",
                    isSelf: true,
                    isJoin: true));
                if (visit.LeftAt is { } left)
                {
                    result.Add(new InstanceHistoryTimelineItem(
                        left,
                        "You left the instance",
                        isSelf: true,
                        isJoin: false));
                }
            }
        }
        else
        {
            result.Add(new InstanceHistoryTimelineItem(
                entry.JoinedAt,
                "You joined the instance",
                isSelf: true,
                isJoin: true));
        }

        foreach (var player in entry.Players.Where(player =>
                     !string.Equals(player.UserId, accountUserId, StringComparison.Ordinal)))
        {
            foreach (var visit in player.Visits)
            {
                result.Add(new InstanceHistoryTimelineItem(
                    visit.JoinedAt,
                    $"{player.DisplayName} joined",
                    isSelf: false,
                    isJoin: true,
                    player.UserId));
                if (visit.LeftAt is { } left)
                {
                    result.Add(new InstanceHistoryTimelineItem(
                        left,
                        $"{player.DisplayName} left",
                        isSelf: false,
                        isJoin: false,
                        player.UserId));
                }
            }
        }

        return result
            .OrderBy(item => item.Timestamp)
            .ThenByDescending(item => item.IsJoin)
            .ToList();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalHours >= 1)
        {
            var hours = (int)duration.TotalHours;
            var minutes = duration.Minutes;
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m";
        }

        return "<1m";
    }
}

public sealed class InstanceHistoryPlayerItem : ViewModelBase
{
    private string _avatarUrl;
    private string _displayName;
    private Brush _nameBrush = UserPresentation.DefaultNameBrush;

    public InstanceHistoryPlayerItem(
        InstanceHistoryPlayer player,
        bool isSelf,
        string accountUserId)
    {
        Player = player;
        _displayName = player.DisplayName;
        _avatarUrl = player.AvatarUrl;
        IsSelf = isSelf;
        OpenProfileCommand = new RelayCommand(
            () => ProfileRequested?.Invoke(this),
            () => CanOpenProfile);
    }

    public InstanceHistoryPlayer Player { get; }

    public Action<InstanceHistoryPlayerItem>? ProfileRequested { get; set; }

    public RelayCommand OpenProfileCommand { get; }

    public string UserId => Player.UserId;

    public bool CanOpenProfile => !string.IsNullOrWhiteSpace(Player.UserId);

    public bool IsSelf { get; }

    public Brush NameBrush
    {
        get => _nameBrush;
        private set => SetProperty(ref _nameBrush, value);
    }

    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (SetProperty(ref _displayName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Initial));
            }
        }
    }

    public string AvatarUrl
    {
        get => _avatarUrl;
        private set
        {
            if (SetProperty(ref _avatarUrl, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Initial));
            }
        }
    }

    public string Initial
    {
        get
        {
            var name = DisplayName.Trim();
            if (name.Length == 0)
            {
                return "?";
            }

            var info = new StringInfo(name);
            return info.SubstringByTextElements(0, 1).ToUpperInvariant();
        }
    }

    public int Visits => Math.Max(1, Player.Visits.Count);

    public string VisitsText => Visits.ToString(CultureInfo.CurrentCulture);

    public string JoinedText => Player.FirstJoinedAt == DateTimeOffset.MinValue
        ? "—"
        : Player.FirstJoinedAt.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);

    public string LeftText
    {
        get
        {
            if (Player.HasOpenVisit)
            {
                return "Now";
            }

            return Player.LastLeftAt is { } left
                ? left.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture)
                : "—";
        }
    }

    public string TimeText
    {
        get
        {
            var duration = Player.TotalTime;
            if (duration.TotalHours >= 1)
            {
                return $"{(int)duration.TotalHours}h {duration.Minutes}m";
            }
            if (duration.TotalMinutes >= 1)
            {
                return $"{(int)duration.TotalMinutes}m";
            }
            return "<1m";
        }
    }

    public void ApplyProfile(VrcUser user)
    {
        NameBrush = UserPresentation.TrustBrush(user.Tags, user.DeveloperType);

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            Player.DisplayName = user.DisplayName;
            DisplayName = user.DisplayName;
        }

        var url = ImageUrlHelper.PickAvatarThumbnailUrl(
            user.UserIcon,
            user.ProfilePicOverrideThumbnail ?? user.IconUrl,
            user.CurrentAvatarThumbnailImageUrl,
            user.CurrentAvatarImageUrl,
            256);
        if (!string.IsNullOrWhiteSpace(url))
        {
            Player.AvatarUrl = url;
            AvatarUrl = url;
        }
    }

    /// <summary>
    /// The public profile is the last avatar fallback for non-friends, whose
    /// user/instance payloads often omit thumbnails. All player surfaces use the
    /// same preference order through <see cref="ImageUrlHelper"/>.
    /// </summary>
    public void ApplyProfile(VrcProfile profile)
    {
        if (profile.TrustTags.Count > 0)
        {
            NameBrush = UserPresentation.TrustBrush(profile.TrustTags);
        }

        if (!string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            Player.DisplayName = profile.DisplayName;
            DisplayName = profile.DisplayName;
        }

        var url = ImageUrlHelper.PickAvatarThumbnailUrl(
            profile.UserIcon,
            profile.IconUrl,
            profile.CurrentAvatarThumbnailImageUrl,
            profile.CurrentAvatarImageUrl,
            256);
        if (!string.IsNullOrWhiteSpace(url))
        {
            Player.AvatarUrl = url;
            AvatarUrl = url;
        }
    }
}

public sealed class InstanceHistoryTimelineItem
{
    public InstanceHistoryTimelineItem(
        DateTimeOffset timestamp,
        string text,
        bool isSelf,
        bool isJoin,
        string userId = "")
    {
        Timestamp = timestamp;
        Text = text;
        IsSelf = isSelf;
        IsJoin = isJoin;
        UserId = userId;
    }

    public DateTimeOffset Timestamp { get; }
    public string Text { get; }
    public bool IsSelf { get; }
    public bool IsJoin { get; }
    public string UserId { get; }

    public string TimeText => Timestamp.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture);
    public string DateText => Timestamp.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture);
}
