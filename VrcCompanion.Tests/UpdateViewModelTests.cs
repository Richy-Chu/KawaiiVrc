using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class UpdateViewModelTests : IDisposable
{
    private readonly string _directory;
    private readonly SettingsStore _settings;

    public UpdateViewModelTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "VrcCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _settings = new SettingsStore(Path.Combine(_directory, "settings.json"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // A leftover temp folder must not fail a test.
        }
    }

    [Fact]
    public async Task LaterSkipsThatVersionUntilANewerReleaseAppears()
    {
        var service = new FakeUpdateService
        {
            CheckResult = new AppUpdateCheckResult(AppUpdateCheckState.Available, "1.1.0", "Fixed paging")
        };
        var viewModel = new UpdateViewModel(service, _settings);

        await viewModel.CheckNowAsync(manual: false);
        Assert.True(viewModel.IsOpen);
        Assert.True(viewModel.HasUpdate);
        Assert.Equal("Fixed paging", viewModel.NotesText);

        viewModel.CloseCommand.Execute(null);

        Assert.False(viewModel.IsOpen);
        Assert.Equal("1.1.0", _settings.Settings.SkippedUpdateVersion);

        // The same release must stay dismissed on the next timed check.
        await viewModel.CheckNowAsync(manual: false);
        Assert.False(viewModel.IsOpen);
        Assert.True(viewModel.HasUpdate);

        // A newer release still opens the popup.
        service.CheckResult = new AppUpdateCheckResult(AppUpdateCheckState.Available, "1.2.0");
        await viewModel.CheckNowAsync(manual: false);

        Assert.True(viewModel.IsOpen);
        Assert.Equal("Update available", viewModel.Title);
        Assert.Contains("1.2.0", viewModel.VersionSummary);
    }

    [Fact]
    public async Task ManualCheckWhenUpToDateShowsTheUpToDatePopup()
    {
        var service = new FakeUpdateService
        {
            CheckResult = new AppUpdateCheckResult(AppUpdateCheckState.UpToDate)
        };
        var viewModel = new UpdateViewModel(service, _settings);

        await viewModel.CheckNowAsync(manual: true);

        Assert.True(viewModel.IsOpen);
        Assert.Equal("You're up to date", viewModel.Title);
        Assert.False(viewModel.HasUpdate);
        Assert.False(viewModel.HasNotes);
        Assert.False(viewModel.ShowDownloadAction);
        Assert.False(viewModel.ShowInstallAction);
        Assert.Equal("You're up to date.", viewModel.StatusText);
    }

    [Fact]
    public async Task BottomBarOnlyAppearsWhileAnUpdateIsAvailable()
    {
        var service = new FakeUpdateService
        {
            CheckResult = new AppUpdateCheckResult(AppUpdateCheckState.UpToDate)
        };
        var viewModel = new UpdateViewModel(service, _settings);

        await viewModel.CheckNowAsync(manual: true);
        Assert.False(viewModel.ShowUpdateBar);

        service.CheckResult = new AppUpdateCheckResult(AppUpdateCheckState.Available, "1.9.0");
        await viewModel.CheckNowAsync(manual: false);

        Assert.True(viewModel.ShowUpdateBar);
        Assert.Contains("1.9.0", viewModel.UpdateBarText);

        // Dismissing the popup keeps the bar: the update is still waiting.
        viewModel.CloseCommand.Execute(null);
        Assert.False(viewModel.IsOpen);
        Assert.True(viewModel.ShowUpdateBar);

        viewModel.OpenCommand.Execute(null);
        Assert.True(viewModel.IsOpen);
        Assert.True(viewModel.ShowDownloadAction);

        await viewModel.DownloadNowAsync();
        Assert.True(viewModel.IsReady);
        Assert.True(viewModel.ShowUpdateBar);
        Assert.Contains("install", viewModel.UpdateBarText);
    }

    [Fact]
    public async Task FailedDownloadKeepsTheDownloadStepAndShowsAnError()
    {
        var service = new FakeUpdateService
        {
            CheckResult = new AppUpdateCheckResult(AppUpdateCheckState.Available, "1.1.0"),
            DownloadSucceeds = false
        };
        var viewModel = new UpdateViewModel(service, _settings);

        await viewModel.CheckNowAsync(manual: true);
        await viewModel.DownloadNowAsync();

        Assert.False(viewModel.IsDownloading);
        Assert.False(viewModel.IsReady);
        Assert.True(viewModel.HasError);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorText));
        Assert.True(viewModel.ShowDownloadAction);
        Assert.False(viewModel.ShowInstallAction);
        Assert.Equal("Download failed.", viewModel.StatusText);
    }

    [Fact]
    public async Task AutomaticCheckRaisesBoundPropertyChangesOnTheUiThread()
    {
        // The 20-second startup check resumes on a thread-pool thread. Changing the
        // window-bound properties there makes WPF throw, which is exactly how the
        // automatic popup stayed hidden in installed builds; this pins the marshalling.
        WpfTestHost.Run(() => { });
        var uiThreadId = Application.Current!.Dispatcher.Thread.ManagedThreadId;

        var service = new FakeUpdateService
        {
            CheckResult = new AppUpdateCheckResult(AppUpdateCheckState.Available, "1.4.0")
        };
        var viewModel = new UpdateViewModel(service, _settings);
        var changedOn = new ConcurrentBag<int>();
        viewModel.PropertyChanged += (_, _) => changedOn.Add(Environment.CurrentManagedThreadId);

        await Task.Run(() => viewModel.CheckNowAsync(manual: false));

        Assert.True(viewModel.IsOpen);
        Assert.NotEmpty(changedOn);
        Assert.All(changedOn, threadId => Assert.Equal(uiThreadId, threadId));
    }

    [Fact]
    public async Task NonInstalledBuildsDisableChecksAndExplainWhy()
    {
        var service = new FakeUpdateService { IsSupported = false };
        var viewModel = new UpdateViewModel(service, _settings);

        await viewModel.CheckNowAsync(manual: true);

        Assert.False(viewModel.IsSupported);
        Assert.True(viewModel.ShowUnsupportedNotice);
        Assert.False(viewModel.CanCheck);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.UpdateUnavailableText));
        Assert.Contains("installed", viewModel.UpdateUnavailableText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, service.CheckCalls);
        Assert.False(viewModel.IsOpen);
        Assert.Empty(viewModel.StatusText);
    }

    private sealed class FakeUpdateService : IAppUpdateService
    {
        public bool IsSupported { get; set; } = true;

        public string CurrentVersion => "1.0.0";

        public AppUpdateCheckResult CheckResult { get; set; } =
            new(AppUpdateCheckState.UpToDate);

        public bool DownloadSucceeds { get; set; } = true;

        public int CheckCalls { get; private set; }

        public Task<AppUpdateCheckResult> CheckAsync()
        {
            CheckCalls++;
            return Task.FromResult(CheckResult);
        }

        public Task<bool> DownloadAsync(Action<int>? progress, CancellationToken cancellationToken)
            => Task.FromResult(DownloadSucceeds);

        public bool ApplyAndRestart() => true;

        public void OpenReleasesPage()
        {
        }
    }
}
