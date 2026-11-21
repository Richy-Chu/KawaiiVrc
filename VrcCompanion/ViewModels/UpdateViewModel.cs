using System.Windows;
using System.Windows.Threading;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Drives the update popup. The first check runs once after startup, then every
/// three hours; a check that finds nothing or cannot reach GitHub changes nothing
/// on screen. Only an available update (or a manual check) opens the popup.
/// </summary>
public sealed class UpdateViewModel : ViewModelBase
{
    // A release can be published minutes after the previous check, and the old
    // three-hour gap meant the bottom bar stayed empty until Settings was
    // opened by hand. Ten minutes plus a check on window activation keeps it
    // honest without hammering GitHub.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    /// <summary>Shown next to the disabled Settings button so the state is explained.</summary>
    public const string UnsupportedText =
        "Update checks need the installed copy. This build runs from a folder or a dev build, so updates are off.";

    private readonly IAppUpdateService _service;
    private readonly SettingsStore _settings;
    private DispatcherTimer? _timer;

    private CancellationTokenSource? _downloadCancellation;
    private bool _started;
    private bool _isOpen;
    private bool _isChecking;
    private bool _isDownloading;
    private bool _isReady;
    private bool _hasUpdate;
    private bool _hasError;
    private string _errorText = string.Empty;
    private string _latestVersion = string.Empty;
    private string _notesText = string.Empty;
    private double _progress;
    private string _statusText = string.Empty;
    private DateTimeOffset? _lastCheckAt;

    public UpdateViewModel()
        : this(new AppUpdateService())
    {
    }

    public UpdateViewModel(IAppUpdateService service)
        : this(service, SettingsStore.Shared)
    {
    }

    public UpdateViewModel(IAppUpdateService service, SettingsStore settings)
    {
        _service = service;
        _settings = settings;

        CheckCommand = new AsyncRelayCommand(() => CheckNowAsync(manual: true), () => CanCheck);
        DownloadCommand = new AsyncRelayCommand(DownloadNowAsync, () => CanDownload);
        InstallCommand = new RelayCommand(InstallAndRestart, () => CanInstall);
        CloseCommand = new RelayCommand(Close);
        OpenCommand = new RelayCommand(Open, () => HasUpdate);
        OpenReleasePageCommand = new RelayCommand(_service.OpenReleasesPage);
    }

    public AsyncRelayCommand CheckCommand { get; }

    public AsyncRelayCommand DownloadCommand { get; }

    public RelayCommand InstallCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>Opens the popup from the bottom bar when an update is waiting.</summary>
    public RelayCommand OpenCommand { get; }

    public RelayCommand OpenReleasePageCommand { get; }

    public bool IsSupported => _service.IsSupported;

    /// <summary>Explains a disabled "Check for updates" button.</summary>
    public bool ShowUnsupportedNotice => !_service.IsSupported;

    public string UpdateUnavailableText => UnsupportedText;

    public string CurrentVersion => _service.CurrentVersion;

    public string Title => _isReady
        ? "Ready to install"
        : _hasUpdate
            ? "Update available"
            : "You're up to date";

    public string VersionSummary => _hasUpdate
        ? $"KawaiiVrc {_latestVersion} is ready · you have {CurrentVersion}"
        : $"KawaiiVrc {CurrentVersion}";

    /// <summary>
    /// The slim bar above the footer. It exists only while a newer release is
    /// waiting to be downloaded, so an up-to-date app shows nothing there.
    /// </summary>
    public bool ShowUpdateBar => _hasUpdate;

    public string UpdateBarText => _isReady
        ? "KawaiiVrc update downloaded · click to install and restart"
        : string.IsNullOrWhiteSpace(_latestVersion)
            ? "A new KawaiiVrc update is available · open the updater"
            : $"KawaiiVrc {_latestVersion} is available · click to open the updater";

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetProperty(ref _isChecking, value))
            {
                RefreshState();
            }
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                RefreshState();
            }
        }
    }

    public bool IsReady
    {
        get => _isReady;
        private set
        {
            if (SetProperty(ref _isReady, value))
            {
                RefreshState();
            }
        }
    }

    public bool HasUpdate
    {
        get => _hasUpdate;
        private set
        {
            if (SetProperty(ref _hasUpdate, value))
            {
                RefreshState();
            }
        }
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string NotesText
    {
        get => _notesText;
        private set
        {
            if (SetProperty(ref _notesText, value))
            {
                OnPropertyChanged(nameof(HasNotes));
            }
        }
    }

    public bool HasNotes => !string.IsNullOrWhiteSpace(_notesText);

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatusText));
            }
        }
    }

    public bool HasStatusText => !string.IsNullOrWhiteSpace(_statusText);

    private bool IsBusy => _isChecking || _isDownloading;

    public bool CanCheck => _service.IsSupported && !IsBusy;

    private bool CanDownload => _hasUpdate && !IsBusy && !_isReady;

    private bool CanInstall => _isReady && !_isDownloading;

    /// <summary>Progress only makes sense once something is downloading or downloaded.</summary>
    public bool ShowProgress => _isDownloading || _isReady;

    public bool ShowDownloadAction => _hasUpdate && !_isReady && !_isDownloading;

    public bool ShowInstallAction => _isReady && !_isDownloading;

    /// <summary>Starts the background checks; a no-op for dev and portable builds.</summary>
    public void Start()
    {
        if (_started || !_service.IsSupported)
        {
            return;
        }

        _started = true;

        // Only a running app needs the timer; tests and design-time hosts do not.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CheckInterval };
        _timer.Tick += (_, _) => _ = CheckNowAsync(manual: false);
        _timer.Start();
        _ = CheckAfterStartupDelayAsync();
    }

    /// <summary>
    /// A cheap check for "did anything change while I was away": runs only when
    /// the last result is older than <paramref name="minAge"/>. The window calls
    /// this when it is activated, so a release published after launch reaches
    /// the bottom bar without opening Settings.
    /// </summary>
    /// <returns>The check it started, or a completed task when it skipped one.</returns>
    public Task RequestCheckIfStale(TimeSpan minAge)
    {
        if (!_service.IsSupported || IsBusy)
        {
            return Task.CompletedTask;
        }

        if (_lastCheckAt is { } last && DateTimeOffset.UtcNow - last < minAge)
        {
            return Task.CompletedTask;
        }

        return CheckNowAsync(manual: false);
    }

    /// <summary>
    /// Runs a check on the UI thread. Timed checks resume on a thread-pool thread, and
    /// the properties below are bound to the window, so they must not be touched there.
    /// </summary>
    internal Task CheckNowAsync(bool manual)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return CheckNowCoreAsync(manual);
        }

        return dispatcher.InvokeAsync(() => CheckNowCoreAsync(manual)).Task.Unwrap();
    }

    private async Task CheckNowCoreAsync(bool manual)
    {
        if (!_service.IsSupported || IsBusy)
        {
            return;
        }

        IsChecking = true;
        HasError = false;
        ErrorText = string.Empty;
        StatusText = "Checking for updates…";

        var result = await _service.CheckAsync();
        ApplyCheckResult(result, manual);
    }

    internal async Task DownloadNowAsync()
    {
        if (!CanDownload)
        {
            return;
        }

        _downloadCancellation?.Dispose();
        _downloadCancellation = new CancellationTokenSource();
        var token = _downloadCancellation.Token;

        HasError = false;
        ErrorText = string.Empty;
        IsDownloading = true;
        IsReady = false;
        Progress = 0;
        StatusText = "Starting download…";

        // Progress<T> captures the UI context, so the callback can touch bindings.
        IProgress<int> progress = new Progress<int>(value =>
        {
            Progress = value;
            StatusText = $"Downloading update… {value:0}%";
        });

        var completed = await _service
            .DownloadAsync(value => progress.Report(value), token)
            .ConfigureAwait(false);

        await OnUiAsync(() =>
        {
            IsDownloading = false;
            if (completed)
            {
                IsReady = true;
                Progress = 100;
                StatusText = "Download complete. Restart KawaiiVrc to finish installing.";
                return;
            }

            if (token.IsCancellationRequested)
            {
                Progress = 0;
                StatusText = "Download cancelled.";
                return;
            }

            HasError = true;
            ErrorText = "The update didn't finish downloading. Check your connection and try again.";
            StatusText = "Download failed.";
        }).ConfigureAwait(false);
    }

    private async Task CheckAfterStartupDelayAsync()
    {
        try
        {
            await Task.Delay(StartupDelay).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        try
        {
            await CheckNowAsync(manual: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LifecycleLog.Write($"updater: check threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ApplyCheckResult(AppUpdateCheckResult result, bool manual)
    {
        IsChecking = false;
        _lastCheckAt = DateTimeOffset.UtcNow;
        LifecycleLog.Write($"updater: check {(manual ? "manual" : "automatic")} -> {result.State} {result.Version}".TrimEnd());

        switch (result.State)
        {
            case AppUpdateCheckState.Unsupported:
                StatusText = string.Empty;
                return;

            case AppUpdateCheckState.UpToDate:
                HasUpdate = false;
                IsReady = false;
                _latestVersion = string.Empty;
                NotesText = string.Empty;
                Progress = 0;
                StatusText = "You're up to date.";
                if (manual)
                {
                    IsOpen = true;
                }

                break;

            case AppUpdateCheckState.Available:
            {
                var version = result.Version ?? string.Empty;
                _latestVersion = version;
                NotesText = result.Notes;
                HasUpdate = true;
                IsReady = false;
                Progress = 0;
                StatusText = $"Version {version} is ready to download.";

                // "Later" remembers the version; a newer release still notifies.
                var skipped = _settings.Settings.SkippedUpdateVersion;
                if (manual || !string.Equals(skipped, version, StringComparison.OrdinalIgnoreCase))
                {
                    IsOpen = true;
                }

                break;
            }

            case AppUpdateCheckState.Failed:
                HasError = manual;
                ErrorText = result.Error ?? "Couldn't check for updates.";
                StatusText = manual ? "Couldn't check for updates." : string.Empty;
                break;
        }
    }

    private void InstallAndRestart()
    {
        if (!CanInstall)
        {
            return;
        }

        StatusText = "Installing update and restarting…";
        if (!_service.ApplyAndRestart())
        {
            HasError = true;
            ErrorText = "Couldn't start the installer. Try again, or open the release page to install it yourself.";
            StatusText = "Install failed.";
            return;
        }

        // Velopack starts Update.exe, which waits for this process to exit.
        Application.Current?.Shutdown();
    }

    private void Open()
    {
        // The bar is only visible while HasUpdate is true, so there is no state
        // to check here.
        IsOpen = true;
    }

    private void Close()
    {
        if (IsDownloading)
        {
            _downloadCancellation?.Cancel();
            IsDownloading = false;
            Progress = 0;
            StatusText = "Download cancelled.";
        }

        // "Later" should not pop the same version again on the next timed check.
        if (HasUpdate && !IsReady && !string.IsNullOrWhiteSpace(_latestVersion))
        {
            _settings.Settings.SkippedUpdateVersion = _latestVersion;
            _settings.Save();
        }

        IsOpen = false;
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(VersionSummary));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ShowDownloadAction));
        OnPropertyChanged(nameof(ShowInstallAction));
        OnPropertyChanged(nameof(ShowUpdateBar));
        OnPropertyChanged(nameof(UpdateBarText));

        OpenCommand?.RaiseCanExecuteChanged();
        CheckCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Download completion falls back to a thread-pool thread, so UI-bound changes are
    /// marshalled. Tests and design-time hosts have no Application; the action then runs
    /// inline.
    /// </summary>
    private static Task OnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
