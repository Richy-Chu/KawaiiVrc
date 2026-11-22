using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Continuity: a durable, local social memory built only from real VRCX/VRCX-0
/// rows and the app's own observations. Nothing is seeded and nothing is sent
/// to a server.
/// </summary>
public sealed class ContinuityViewModel : ViewModelBase
{
    private readonly ContinuityStore _store;
    private readonly VrcApiClient? _api;
    private readonly HashSet<string> _worldHydrationAttempts = new(StringComparer.Ordinal);
    private readonly HashSet<ContinuityPersonItem> _avatarRequests = new();
    private HashSet<string> _friendIds = new(StringComparer.Ordinal);
    private bool _friendsKnown;
    private readonly SemaphoreSlim _avatarGate = new(4);
    private Task? _hydrationTask;
    private bool _isHydrating;
    private string _accountUserId = string.Empty;
    private string _accountDisplayName = string.Empty;
    private string _searchText = string.Empty;
    private string _importStatus = string.Empty;
    private string _noteStatus = string.Empty;
    private bool _isLoading;
    private bool _isImporting;
    private bool _loaded;
    private bool _isTimelineTab = true;
    private bool _isPeopleTab;
    private bool _isWorldsTab;
    private bool _isSourcesTab;
    private ContinuityPersonItem? _selectedPerson;
    private ContinuityWorldItem? _selectedWorld;
    private ContinuitySessionItem? _selectedSession;

    public ContinuityViewModel()
        : this(null, new ContinuityStore())
    {
    }

    public ContinuityViewModel(VrcApiClient api)
        : this(api, new ContinuityStore())
    {
    }

    internal ContinuityViewModel(ContinuityStore store)
        : this(null, store)
    {
    }

    private ContinuityViewModel(VrcApiClient? api, ContinuityStore store)
    {
        _api = api;
        _store = store;
        SessionsView = CollectionViewSource.GetDefaultView(Sessions);
        SessionsView.Filter = FilterSession;
        PeopleView = CollectionViewSource.GetDefaultView(People);
        PeopleView.Filter = FilterPerson;
        WorldsView = CollectionViewSource.GetDefaultView(Worlds);
        WorldsView.Filter = FilterWorld;

        ShowTimelineCommand = new RelayCommand(() => SetTab("timeline"));
        ShowPeopleCommand = new RelayCommand(() => SetTab("people"));
        ShowWorldsCommand = new RelayCommand(() => SetTab("worlds"));
        ShowSourcesCommand = new RelayCommand(() => SetTab("sources"));
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsImporting);
        SelectPersonCommand = new RelayCommand<ContinuityPersonItem>(SelectPerson);
        ClosePersonCommand = new RelayCommand(() => SelectedPerson = null);
        SelectWorldCommand = new RelayCommand<ContinuityWorldItem>(SelectWorld);
        CloseWorldCommand = new RelayCommand(() => SelectedWorld = null);
        SaveNoteCommand = new RelayCommand(SaveNote, () => SelectedPerson is not null);
        OpenSessionCommand = new RelayCommand<ContinuitySessionItem>(session => SelectedSession = session);
        CloseSessionCommand = new RelayCommand(() => SelectedSession = null);
        OpenPersonProfileCommand = new RelayCommand<ContinuityPersonItem>(
            OpenPersonProfile,
            person => person?.CanOpenProfile == true);
        OpenWorldDetailsCommand = new RelayCommand<ContinuityWorldItem>(
            OpenWorldDetails,
            world => world?.CanOpenWorld == true);
        OpenSessionWorldCommand = new RelayCommand<ContinuitySessionItem>(
            OpenSessionWorld,
            session => session?.CanOpenWorld == true);
        CopySessionLocationCommand = new RelayCommand(CopySessionLocation, () => SelectedSession is not null);
        OpenSessionPersonCommand = new RelayCommand<ContinuityPersonItem>(
            OpenPersonProfile,
            person => person?.CanOpenProfile == true);
    }

    /// <summary>
    /// Raised after a local import changes the stored history, so other views
    /// (Media) can re-resolve their sessions without being reopened.
    /// </summary>
    public event Action? HistoryChanged;

    public ObservableCollection<ContinuitySessionItem> Sessions { get; } = new();

    public ObservableCollection<ContinuityPersonItem> People { get; } = new();

    public ObservableCollection<ContinuityWorldItem> Worlds { get; } = new();

    public ObservableCollection<ContinuitySourceItem> Sources { get; } = new();

    public ObservableCollection<ContinuitySessionItem> PersonSessions { get; } = new();

    public ObservableCollection<ContinuitySessionItem> WorldSessions { get; } = new();

    public ObservableCollection<ContinuityPersonItem> SessionPeople { get; } = new();

    public ICollectionView SessionsView { get; }

    public ICollectionView PeopleView { get; }

    public ICollectionView WorldsView { get; }

    /// <summary>One page per tab; a 6,000-person history still builds 24 rows.</summary>
    public PagedList<ContinuitySessionItem> SessionsPage { get; } = new();

    public PagedList<ContinuityPersonItem> PeoplePage { get; } = new();

    public PagedList<ContinuityWorldItem> WorldsPage { get; } = new();

    public RelayCommand ShowTimelineCommand { get; }
    public RelayCommand ShowPeopleCommand { get; }
    public RelayCommand ShowWorldsCommand { get; }
    public RelayCommand ShowSourcesCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public RelayCommand<ContinuityPersonItem> SelectPersonCommand { get; }
    public RelayCommand ClosePersonCommand { get; }
    public RelayCommand<ContinuityWorldItem> SelectWorldCommand { get; }
    public RelayCommand CloseWorldCommand { get; }
    public RelayCommand SaveNoteCommand { get; }
    public RelayCommand<ContinuitySessionItem> OpenSessionCommand { get; }
    public RelayCommand CloseSessionCommand { get; }
    public RelayCommand<ContinuityPersonItem> OpenPersonProfileCommand { get; }
    public RelayCommand<ContinuityWorldItem> OpenWorldDetailsCommand { get; }
    public RelayCommand<ContinuitySessionItem> OpenSessionWorldCommand { get; }
    public RelayCommand CopySessionLocationCommand { get; }
    public RelayCommand<ContinuityPersonItem> OpenSessionPersonCommand { get; }

    public string AccountUserId => _accountUserId;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
            {
                return;
            }

            RefreshViews();
        }
    }

    public bool IsTimelineTab
    {
        get => _isTimelineTab;
        private set => SetProperty(ref _isTimelineTab, value);
    }

    public bool IsPeopleTab
    {
        get => _isPeopleTab;
        private set => SetProperty(ref _isPeopleTab, value);
    }

    public bool IsWorldsTab
    {
        get => _isWorldsTab;
        private set => SetProperty(ref _isWorldsTab, value);
    }

    public bool IsSourcesTab
    {
        get => _isSourcesTab;
        private set => SetProperty(ref _isSourcesTab, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsEmpty));
                // SummaryText is computed from IsLoading. Without this notify
                // the header can stay on "Reading local history..." forever.
                OnPropertyChanged(nameof(SummaryText));
            }
        }
    }

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (SetProperty(ref _isImporting, value))
            {
                ImportCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ImportButtonText));
            }
        }
    }

    public string ImportButtonText => IsImporting ? "Importing…" : "Import history";

    public string ImportStatus
    {
        get => _importStatus;
        private set
        {
            if (SetProperty(ref _importStatus, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasImportStatus));
            }
        }
    }

    public bool HasImportStatus => !string.IsNullOrWhiteSpace(ImportStatus);

    public string NoteStatus
    {
        get => _noteStatus;
        private set
        {
            if (SetProperty(ref _noteStatus, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasNoteStatus));
            }
        }
    }

    public bool HasNoteStatus => !string.IsNullOrWhiteSpace(NoteStatus);

    public ContinuityPersonItem? SelectedPerson
    {
        get => _selectedPerson;
        private set
        {
            if (!SetProperty(ref _selectedPerson, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsPersonDetailVisible));
            OnPropertyChanged(nameof(IsPeopleListVisible));
            NoteStatus = string.Empty;
            SaveNoteCommand.RaiseCanExecuteChanged();
            if (value is null)
            {
                PersonSessions.Clear();
                return;
            }

            LoadPersonSessions(value.Person.PersonKey);
        }
    }

    public bool IsPersonDetailVisible => SelectedPerson is not null;

    public bool IsPeopleListVisible => SelectedPerson is null;

    public ContinuityWorldItem? SelectedWorld
    {
        get => _selectedWorld;
        private set
        {
            if (!SetProperty(ref _selectedWorld, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsWorldDetailVisible));
            OnPropertyChanged(nameof(IsWorldsListVisible));
            if (value is null)
            {
                WorldSessions.Clear();
                return;
            }

            LoadWorldSessions(value.World.WorldId);
        }
    }

    public bool IsWorldDetailVisible => SelectedWorld is not null;

    public bool IsWorldsListVisible => SelectedWorld is null;

    public ContinuitySessionItem? SelectedSession
    {
        get => _selectedSession;
        private set
        {
            if (!SetProperty(ref _selectedSession, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSessionDetailVisible));
            OnPropertyChanged(nameof(HasSessionPeople));
            CopySessionLocationCommand.RaiseCanExecuteChanged();
            if (value is null)
            {
                SessionPeople.Clear();
                return;
            }

            LoadSessionPeople(value.Session.Id);
        }
    }

    public bool IsSessionDetailVisible => SelectedSession is not null;

    public bool HasSessionPeople => SessionPeople.Count > 0;

    /// <summary>
    /// Applies the signed-in account's live VRChat friend list to every history
    /// row. Continuity rows come from local databases and can be old, so the
    /// friend flag must be resolved from the same source the friend list uses.
    /// </summary>
    public void SetFriendIds(IEnumerable<string> friendIds)
    {
        var next = friendIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var wasKnown = _friendsKnown;
        _friendsKnown = true;
        if (wasKnown && _friendIds.SetEquals(next))
        {
            return;
        }

        _friendIds = next;
        foreach (var person in People)
        {
            person.ApplyFriendState(IsFriend(person.UserId));
        }

        foreach (var person in SessionPeople)
        {
            person.ApplyFriendState(IsFriend(person.UserId));
        }

        if (SelectedPerson is not null)
        {
            SelectedPerson.ApplyFriendState(IsFriend(SelectedPerson.UserId));
        }
    }

    private bool IsFriend(string? userId)
        => !string.IsNullOrWhiteSpace(userId) && _friendIds.Contains(userId);

    public Action<ContinuityPersonItem>? PersonProfileRequested { get; set; }

    public Action<string>? WorldRequested { get; set; }

    public bool IsEmpty => !IsLoading && Sessions.Count == 0;

    public string EmptyTitle => "No history imported yet";

    public string EmptyMessage =>
        "Continuity only shows real rows from VRCX/VRCX-0 and your own VRChat logs. " +
        "Install VRCX or VRCX-0, or open a world while KawaiiVrc is running, then press Import.";

    private ContinuitySummary _summary = new();

    public string SummaryText => IsLoading
        ? "Reading local history…"
        : _summary.SessionCount == 0
            ? "No real sessions imported."
            : $"{_summary.SessionCount:N0} sessions · {_summary.PeopleCount:N0} people · " +
              $"{_summary.WorldCount:N0} worlds · {FormatDuration(_summary.TotalSeconds)} together";

    public string SourcesSummary => Sources.Count == 0
        ? "No source databases detected."
        : $"{Sources.Count(source => source.IsAvailable)} source(s) available";

    public string PersonCountText => $"{People.Count:N0} people";
    public string WorldCountText => $"{Worlds.Count:N0} worlds";
    public string SessionCountText => $"{Sessions.Count:N0} sessions";

    public void SetAccount(VrcUser? user)
    {
        var id = user?.Id ?? string.Empty;
        var name = user?.DisplayName ?? string.Empty;
        if (string.Equals(_accountUserId, id, StringComparison.Ordinal) &&
            string.Equals(_accountDisplayName, name, StringComparison.Ordinal))
        {
            return;
        }

        _accountUserId = id;
        _accountDisplayName = name;
        _loaded = false;
        _friendIds.Clear();
        _friendsKnown = false;
        _worldHydrationAttempts.Clear();
        _avatarRequests.Clear();
        SelectedSession = null;
        SelectedPerson = null;
        SelectedWorld = null;
        Sessions.Clear();
        People.Clear();
        Worlds.Clear();
        PersonSessions.Clear();
        WorldSessions.Clear();
        _summary = new ContinuitySummary();
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(AccountUserId));
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded || IsLoading)
        {
            return;
        }

        await LoadAsync();
        if (_loaded && HasLocalSource() && (Sessions.Count == 0 || HasUnimportedSource()))
        {
            // First visit: pull the real local rows automatically instead of
            // showing an empty page until someone finds the Import button. If
            // one source was imported before and another appears later, pick
            // up the missing source as well.
            await ImportAsync();
        }
    }

    private bool HasUnimportedSource()
        => Sources.Any(source => source.IsAvailable && source.Source.ImportedAt is null);

    private static bool HasLocalSource()
        => File.Exists(Vrcx0ContinuityImporter.DefaultPath) ||
           File.Exists(VrcxContinuityImporter.DefaultPath) ||
           VrcNextContinuityImporter.FindDatabases().Count > 0 ||
           File.Exists(VrcCompanionContinuityImporter.DefaultPath);

    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var account = _accountUserId;
            // World metadata from another source may have arrived without its
            // session name. Repair those rows locally before reading them.
            await Task.Run(() => _store.BackfillSessionWorldNames());
            var sessions = await Task.Run(() => _store.GetSessions(account, null, 5000));
            var people = await Task.Run(() => _store.GetPeople(account, null, 5000));
            var worlds = await Task.Run(() => _store.GetWorlds(account, null, 5000));
            var summary = await Task.Run(() => _store.GetSummary(account));
            var imports = await Task.Run(() => _store.GetImports());

            // The account can change while the SQLite reads are in flight. Do
            // not replace the new account's view with the previous query.
            if (!string.Equals(account, _accountUserId, StringComparison.Ordinal))
            {
                return;
            }

            Sessions.Clear();
            foreach (var session in sessions)
            {
                Sessions.Add(new ContinuitySessionItem(session));
            }

            People.Clear();
            foreach (var person in people)
            {
                People.Add(new ContinuityPersonItem(
                    person,
                    _accountUserId,
                    _friendsKnown ? IsFriend(person.UserId) : null));
            }

            Worlds.Clear();
            foreach (var world in worlds)
            {
                Worlds.Add(new ContinuityWorldItem(world));
            }

            _summary = summary;
            BuildSources(imports);
            _loaded = true;
            RefreshViews();
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(PersonCountText));
            OnPropertyChanged(nameof(WorldCountText));
            OnPropertyChanged(nameof(SessionCountText));
            OnPropertyChanged(nameof(SourcesSummary));

            if (SelectedPerson is not null)
            {
                var key = SelectedPerson.Person.PersonKey;
                SelectedPerson = People.FirstOrDefault(person =>
                    string.Equals(person.Person.PersonKey, key, StringComparison.Ordinal));
            }

            if (SelectedWorld is not null)
            {
                var id = SelectedWorld.World.WorldId;
                SelectedWorld = Worlds.FirstOrDefault(world =>
                    string.Equals(world.World.WorldId, id, StringComparison.Ordinal));
            }

            // The companion's own log has no world names until VRChat is asked
            // for the world. This mirrors Instance History's hydration instead
            // of leaving every recent row as "Unknown world".
            _ = HydrateWorldNamesAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("continuity load: " + ex);
            ImportStatus = "Could not read the local Continuity database: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Resolves missing world names through VRChat and persists the result.
    /// Context calls this before loading today's rows so it does not show
    /// "Unknown world" just because Continuity has not been opened yet.
    /// </summary>
    public Task HydrateWorldNamesAsync()
    {
        if (_api is null || string.IsNullOrWhiteSpace(_accountUserId))
        {
            return Task.CompletedTask;
        }

        if (_isHydrating)
        {
            return _hydrationTask ?? Task.CompletedTask;
        }

        _hydrationTask = HydrateMissingWorldsAsync();
        return _hydrationTask;
    }

    private async Task HydrateMissingWorldsAsync()
    {
        if (_api is null || _isHydrating)
        {
            return;
        }

        var missing = Sessions
            .Where(session =>
                !string.IsNullOrWhiteSpace(session.Session.WorldId) &&
                (string.IsNullOrWhiteSpace(session.Session.WorldName) ||
                 string.IsNullOrWhiteSpace(session.Session.WorldThumbnailUrl)))
            .Select(session => session.Session.WorldId)
            .Distinct(StringComparer.Ordinal)
            .Where(worldId => _worldHydrationAttempts.Add(worldId))
            .Take(40)
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        _isHydrating = true;
        var updated = new List<ContinuityWorld>();
        try
        {
            foreach (var worldId in missing)
            {
                try
                {
                    var world = await _api.GetWorldAsync(worldId);
                    if (world is null || string.IsNullOrWhiteSpace(world.Name))
                    {
                        continue;
                    }

                    updated.Add(new ContinuityWorld
                    {
                        WorldId = worldId,
                        Name = world.Name,
                        AuthorName = world.AuthorName ?? string.Empty,
                        ThumbnailUrl = ImageUrlHelper.PickWorldThumbnailUrl(
                            world.ThumbnailImageUrl,
                            world.ImageUrl,
                            512) ?? string.Empty,
                        Source = "vrchat"
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("continuity world hydration: " + ex.Message);
                }
            }

            if (updated.Count == 0)
            {
                return;
            }

            _store.UpsertWorldMetadata(updated);
            await LoadAsync();
        }
        finally
        {
            _isHydrating = false;
        }
    }

    private async Task ImportAsync()
    {
        if (IsImporting)
        {
            return;
        }

        IsImporting = true;
        ImportStatus = "Importing real rows from installed companions…";
        NoteStatus = string.Empty;
        try
        {
            var results = await Task.Run(() =>
            {
                var output = new List<ContinuityImportResult>();
                var vrcx0 = new Vrcx0ContinuityImporter().Import(
                    _store,
                    _accountUserId,
                    _accountDisplayName,
                    new Progress<string>(message => ImportStatus = message));
                output.Add(vrcx0);

                var vrcx = new VrcxContinuityImporter().Import(
                    _store,
                    _accountUserId,
                    _accountDisplayName);
                output.Add(vrcx);

                var vrcNext = new VrcNextContinuityImporter().Import(
                    _store,
                    _accountUserId,
                    _accountDisplayName);
                output.Add(vrcNext);

                var companion = new VrcCompanionContinuityImporter().Import(
                    _store,
                    _accountUserId,
                    _accountDisplayName);
                output.Add(companion);
                return output;
            });

            await LoadAsync();
            ImportStatus = string.Join(
                "  ·  ",
                results.Select(result => result.Message).Where(message => !string.IsNullOrWhiteSpace(message)));
            OnPropertyChanged(nameof(SourcesSummary));
            HistoryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("continuity import: " + ex);
            ImportStatus = "Import failed: " + ex.Message;
        }
        finally
        {
            IsImporting = false;
        }
    }

    private void BuildSources(IReadOnlyList<ContinuityImportSource> imported)
    {
        Sources.Clear();
        var vrchat0Path = Vrcx0ContinuityImporter.DefaultPath;
        var vrcxPath = VrcxContinuityImporter.DefaultPath;

        AddSource("vrcx-0", "VRCX-0", vrchat0Path, File.Exists(vrchat0Path), imported);
        AddSource("vrcx", "VRCX", vrcxPath, File.Exists(vrcxPath), imported);

        var vrcNextDatabases = VrcNextContinuityImporter.FindDatabases();
        AddSource(
            "vrcnext",
            "VRCNext",
            vrcNextDatabases.Count == 0
                ? VrcNextContinuityImporter.DefaultDirectory
                : string.Join("; ", vrcNextDatabases),
            vrcNextDatabases.Count > 0,
            imported);

        AddSource(
            "vrc-companion",
            "KawaiiVrc",
            VrcCompanionContinuityImporter.DefaultPath,
            File.Exists(VrcCompanionContinuityImporter.DefaultPath),
            imported);

        OnPropertyChanged(nameof(SourcesSummary));
    }

    private void AddSource(
        string key,
        string label,
        string path,
        bool available,
        IReadOnlyList<ContinuityImportSource> imported)
    {
        var row = imported.FirstOrDefault(source =>
            string.Equals(source.Source, key, StringComparison.Ordinal));
        Sources.Add(new ContinuitySourceItem(new ContinuityImportSource
        {
            Source = key,
            Label = label,
            Path = path,
            IsAvailable = available,
            ImportedAt = row?.ImportedAt,
            Sessions = row?.Sessions ?? 0,
            People = row?.People ?? 0,
            Worlds = row?.Worlds ?? 0,
            Message = available
                ? row is null ? "Ready to import." : "Last import recorded."
                : "Not found on this PC."
        }));
    }

    private void SelectPerson(ContinuityPersonItem? person)
    {
        if (person is null)
        {
            return;
        }

        SelectedPerson = person;
        SetTab("people");
    }

    private void SelectWorld(ContinuityWorldItem? world)
    {
        if (world is null)
        {
            return;
        }

        SelectedWorld = world;
        SetTab("worlds");
    }

    /// <summary>Used by Context when a session card is opened from another view.</summary>
    /// <summary>
    /// Every session known from any local source (VRCX-0, VRCX, VRCNext and our
    /// own log), for views that only have a world and a timestamp to match on.
    /// </summary>
    public IReadOnlyList<ContinuitySession> GetSessionsForAccount(string accountUserId, int limit = 5000)
        => string.IsNullOrWhiteSpace(accountUserId)
            ? Array.Empty<ContinuitySession>()
            : _store.GetSessions(accountUserId, null, limit);

    public void ShowSession(ContinuitySessionItem session)
    {
        if (session is null)
        {
            return;
        }

        SetTab("timeline");
        SelectedSession = session;
    }

    internal void LoadSessionPeople(string sessionId)
    {
        SessionPeople.Clear();
        foreach (var row in _store.GetSessionPeople(sessionId))
        {
            SessionPeople.Add(new ContinuityPersonItem(new ContinuityPerson
            {
                PersonKey = row.PersonKey,
                UserId = row.UserId,
                DisplayName = row.DisplayName,
                AvatarUrl = row.AvatarUrl,
                LastSeenAt = row.LeftAt ?? row.JoinedAt,
                TotalSeconds = row.DurationSeconds,
                SessionCount = 1
            }, _accountUserId, _friendsKnown ? IsFriend(row.UserId) : null));
        }

        OnPropertyChanged(nameof(HasSessionPeople));
    }

    private void OpenPersonProfile(ContinuityPersonItem? person)
    {
        if (person?.CanOpenProfile == true)
        {
            PersonProfileRequested?.Invoke(person);
        }
    }

    private void OpenWorldDetails(ContinuityWorldItem? world)
    {
        if (!string.IsNullOrWhiteSpace(world?.WorldId))
        {
            WorldRequested?.Invoke(world.WorldId);
        }
    }

    private void OpenSessionWorld(ContinuitySessionItem? session)
    {
        if (!string.IsNullOrWhiteSpace(session?.WorldId))
        {
            WorldRequested?.Invoke(session.WorldId);
        }
    }

    private void CopySessionLocation()
    {
        if (SelectedSession is null || string.IsNullOrWhiteSpace(SelectedSession.Location))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(SelectedSession.Location);
            NoteStatus = "Instance location copied.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine("continuity clipboard: " + ex.Message);
            NoteStatus = "Could not access the clipboard.";
        }
    }

    /// <summary>
    /// Called when a person row is realized. Only visible rows ask VRChat for
    /// their current icon, matching Recent Players and Instance History.
    /// </summary>
    public void RequestAvatar(ContinuityPersonItem person)
    {
        if (_api is null || person is null || person.HasAvatar ||
            !person.CanOpenProfile || !_avatarRequests.Add(person))
        {
            return;
        }

        _ = LoadAvatarAsync(person);
    }

    private async Task LoadAvatarAsync(ContinuityPersonItem person)
    {
        await _avatarGate.WaitAsync();
        try
        {
            if (person.HasAvatar)
            {
                return;
            }

            var user = await _api!.GetUserAsync(person.UserId);
            if (user is not null)
            {
                person.ApplyProfile(user);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("continuity avatar: " + ex.Message);
        }
        finally
        {
            _avatarGate.Release();
        }
    }

    private void SaveNote()
    {
        if (SelectedPerson is null)
        {
            return;
        }

        _store.SaveNote(SelectedPerson.Person.PersonKey, SelectedPerson.Note);
        SelectedPerson.Person.LocalNote = SelectedPerson.Note;
        NoteStatus = "Saved locally at " + DateTimeOffset.Now.ToString("g", CultureInfo.CurrentCulture) + ".";
        SelectedPerson.RefreshNote();
    }

    private void LoadPersonSessions(string personKey)
    {
        PersonSessions.Clear();
        var rows = _store.GetPersonSessions(_accountUserId, personKey, 250);
        foreach (var row in rows)
        {
            PersonSessions.Add(new ContinuitySessionItem(row));
        }
    }

    private void LoadWorldSessions(string worldId)
    {
        WorldSessions.Clear();
        var rows = _store.GetWorldSessions(_accountUserId, worldId, 250);
        foreach (var row in rows)
        {
            WorldSessions.Add(new ContinuitySessionItem(row));
        }
    }

    private void SetTab(string tab)
    {
        IsTimelineTab = string.Equals(tab, "timeline", StringComparison.Ordinal);
        IsPeopleTab = string.Equals(tab, "people", StringComparison.Ordinal);
        IsWorldsTab = string.Equals(tab, "worlds", StringComparison.Ordinal);
        IsSourcesTab = string.Equals(tab, "sources", StringComparison.Ordinal);
    }

    private void RefreshViews()
    {
        SessionsView.Refresh();
        PeopleView.Refresh();
        WorldsView.Refresh();
        // A search or a fresh import starts at the top of each list.
        SessionsPage.SetSource(SessionsView.Cast<ContinuitySessionItem>(), resetPage: true);
        PeoplePage.SetSource(PeopleView.Cast<ContinuityPersonItem>(), resetPage: true);
        WorldsPage.SetSource(WorldsView.Cast<ContinuityWorldItem>(), resetPage: true);
    }

    private bool FilterSession(object item)
    {
        if (item is not ContinuitySessionItem session)
        {
            return false;
        }

        var query = SearchText.Trim();
        return query.Length == 0 ||
               session.WorldName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               session.Location.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               session.DateText.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool FilterPerson(object item)
    {
        if (item is not ContinuityPersonItem person)
        {
            return false;
        }

        var query = SearchText.Trim();
        return query.Length == 0 ||
               person.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               person.UserId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               person.Note.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool FilterWorld(object item)
    {
        if (item is not ContinuityWorldItem world)
        {
            return false;
        }

        var query = SearchText.Trim();
        return query.Length == 0 ||
               world.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               world.WorldId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               world.AuthorName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    public static string FormatDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return "0m";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        }

        if (duration.TotalHours >= 1)
        {
            return duration.Minutes > 0
                ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
                : $"{(int)duration.TotalHours}h";
        }

        return duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}m"
            : "<1m";
    }
}

public sealed class ContinuityPersonItem : ViewModelBase
{
    private string _note;
    private string _avatarUrl;
    private bool _isFriend;

    public ContinuityPersonItem(
        ContinuityPerson person,
        string? currentUserId = null,
        bool? liveIsFriend = null)
    {
        Person = person;
        _isFriend = liveIsFriend ?? person.IsFriend;
        _avatarUrl = person.AvatarUrl ?? string.Empty;
        _note = string.IsNullOrWhiteSpace(person.LocalNote)
            ? person.ImportedNote
            : person.LocalNote;
        IsCurrentUser = !string.IsNullOrWhiteSpace(currentUserId) &&
                        (string.Equals(person.UserId, currentUserId, StringComparison.Ordinal) ||
                         (string.IsNullOrWhiteSpace(person.UserId) &&
                          string.Equals(person.PersonKey, currentUserId, StringComparison.Ordinal)));
    }

    public ContinuityPerson Person { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Person.DisplayName)
        ? "Unknown person"
        : Person.DisplayName;

    public string UserId => Person.UserId;

    public string ProfileKey => string.IsNullOrWhiteSpace(Person.UserId)
        ? Person.PersonKey
        : Person.UserId;

    public bool CanOpenProfile => !string.IsNullOrWhiteSpace(Person.UserId);

    public string UserIdText => string.IsNullOrWhiteSpace(Person.UserId)
        ? Person.PersonKey.StartsWith("name:", StringComparison.Ordinal)
            ? "Name-only observation"
            : Person.PersonKey
        : Person.UserId;

    public string AvatarUrl
    {
        get => _avatarUrl;
        private set
        {
            if (SetProperty(ref _avatarUrl, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasAvatar));
            }
        }
    }

    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);

    public string Initial
    {
        get
        {
            var name = DisplayName.Trim();
            if (name.Length == 0)
            {
                return "?";
            }

            return new StringInfo(name).SubstringByTextElements(0, 1).ToUpperInvariant();
        }
    }

    public string TimeText => ContinuityViewModel.FormatDuration(Person.TotalSeconds);

    public string SessionsText => Person.SessionCount == 1
        ? "1 session"
        : $"{Person.SessionCount:N0} sessions";

    public string LastSeenText => Person.LastSeenAt is null
        ? "Never seen"
        : "Last seen " + Person.LastSeenAt.Value.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    public string FirstSeenText => Person.FirstSeenAt is null
        ? string.Empty
        : "First met " + Person.FirstSeenAt.Value.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    public string TrustText => string.IsNullOrWhiteSpace(Person.TrustLevel)
        ? "Trust unknown"
        : Person.TrustLevel;

    /// <summary>The signed-in account is never a friend or a non-friend row.</summary>
    public bool IsCurrentUser { get; }

    public bool IsFriend => !IsCurrentUser && _isFriend;

    public string FriendText => IsCurrentUser
        ? "You"
        : IsFriend
            ? "Friend"
            : "Not on friend list";

    /// <summary>
    /// Refreshes the relationship from the live VRChat friend directory. History
    /// rows keep their stored value until a live list is available.
    /// </summary>
    public void ApplyFriendState(bool isFriend)
    {
        if (IsCurrentUser || _isFriend == isFriend)
        {
            return;
        }

        _isFriend = isFriend;
        Person.IsFriend = isFriend;
        OnPropertyChanged(nameof(IsFriend));
        OnPropertyChanged(nameof(FriendText));
    }

    public string Bio => Person.Bio;

    public bool HasBio => !string.IsNullOrWhiteSpace(Person.Bio);

    public string Note
    {
        get => _note;
        set
        {
            if (!SetProperty(ref _note, value ?? string.Empty))
            {
                return;
            }

            Person.LocalNote = _note;
            OnPropertyChanged(nameof(HasNote));
            OnPropertyChanged(nameof(NotePreview));
        }
    }

    public bool HasNote => !string.IsNullOrWhiteSpace(_note);

    public string NotePreview => HasNote
        ? _note.Trim()
        : "No note yet. Only you can see notes saved here.";

    public void RefreshNote()
    {
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(NotePreview));
    }

    public void ApplyProfile(VrcUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            Person.DisplayName = user.DisplayName;
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Initial));
        }

        var avatar = ImageUrlHelper.PickAvatarThumbnailUrl(
            user.UserIcon,
            user.ProfilePicOverrideThumbnail ?? user.ProfilePicOverride ?? user.IconUrl,
            user.CurrentAvatarThumbnailImageUrl,
            user.CurrentAvatarImageUrl,
            256);
        if (!string.IsNullOrWhiteSpace(avatar))
        {
            Person.AvatarUrl = avatar;
            AvatarUrl = avatar;
        }

    }
}

public sealed class ContinuityWorldItem
{
    public ContinuityWorldItem(ContinuityWorld world)
    {
        World = world;
    }

    public ContinuityWorld World { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(World.Name)
        ? "Unknown world"
        : World.Name;

    public string WorldId => World.WorldId;

    public bool CanOpenWorld => !string.IsNullOrWhiteSpace(World.WorldId);

    public string AuthorName => string.IsNullOrWhiteSpace(World.AuthorName)
        ? "Unknown creator"
        : World.AuthorName;

    public string ThumbnailUrl => World.ThumbnailUrl;

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(World.ThumbnailUrl);

    public string TimeText => ContinuityViewModel.FormatDuration(World.TotalSeconds);

    public string VisitText => World.VisitCount == 1
        ? "1 visit"
        : $"{World.VisitCount:N0} visits";

    public string LastSeenText => World.LastSeenAt is null
        ? string.Empty
        : "Last visited " + World.LastSeenAt.Value.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
}

public sealed class ContinuitySourceItem
{
    public ContinuitySourceItem(ContinuityImportSource source)
    {
        Source = source;
    }

    public ContinuityImportSource Source { get; }

    public string Label => Source.Label;

    public string Path => Source.Path;

    public bool IsAvailable => Source.IsAvailable;

    public string StatusText => Source.IsAvailable ? "Available" : "Not detected";

    public string ImportedText => Source.ImportedAt is null
        ? "No import recorded"
        : "Imported " + Source.ImportedAt.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string StatsText => Source.Sessions == 0
        ? Source.Message
        : $"{Source.Sessions:N0} sessions · {Source.People:N0} people · {Source.Worlds:N0} worlds";
}

public sealed class ContinuitySessionItem
{
    public ContinuitySessionItem(ContinuitySession session)
    {
        Session = session;
    }

    public ContinuitySession Session { get; }

    public string WorldName => string.IsNullOrWhiteSpace(Session.WorldName)
        ? "Unknown world"
        : Session.WorldName;

    public string WorldId => Session.WorldId;

    public bool CanOpenWorld => !string.IsNullOrWhiteSpace(Session.WorldId);

    public string ThumbnailUrl => Session.WorldThumbnailUrl;

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(Session.WorldThumbnailUrl);

    public string Location => Session.Location;

    public string SourceText => Session.Source switch
    {
        "vrcx-0" => "VRCX-0",
        "vrcx" => "VRCX",
        _ => Session.Source
    };

    public string DateText => Session.JoinedAt == DateTimeOffset.MinValue
        ? "Unknown date"
        : Session.JoinedAt.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    public string TimeRangeText
    {
        get
        {
            var start = Session.JoinedAt.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
            return Session.LeftAt is null
                ? start + " – now"
                : start + " – " + Session.LeftAt.Value.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
        }
    }

    public string DurationText => ContinuityViewModel.FormatDuration(Session.DurationSeconds);

    public string PlayerCountText => Session.PlayerCount == 1
        ? "1 player"
        : $"{Session.PlayerCount:N0} players";

    public string DetailsText => $"{DateText} · {TimeRangeText} · {DurationText} · {PlayerCountText}";
}
