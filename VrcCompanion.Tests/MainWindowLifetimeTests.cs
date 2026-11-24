using System.Windows;
using System.Windows.Threading;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

[Collection(Seams.Collection)]
[ResetSeams]
public class MainWindowLifetimeTests
{
    [Fact]
    public void ClosingAnActiveWindowLeavesNoBackgroundTrimScheduled()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var window = CreateWindow(shell);

            window.Show();

            // The window is shown unactivated, so Activate can be refused while
            // another window is still settling; retry instead of assuming once.
            for (var attempt = 0; attempt < 20 && !window.IsActive; attempt++)
            {
                window.Activate();
                Pump(window);
            }

            // Only an active window raises Deactivated while it closes, and that
            // event used to rearm the trim timer after Closing had stopped it.
            Assert.True(
                window.IsActive,
                "The test window never became active, so the close-time deactivation was not exercised.");

            window.Close();
            Pump(window);

            Assert.False(
                window.IsBackgroundTrimScheduledForTest,
                "Closing the window left the background image-cache trim scheduled.");
        });
    }

    [Fact]
    public void ShowingTheWindowStartsTheStartupWork()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            TestHooks.SkipStartupRestore();
            Assert.False(shell.HasInitialized);

            var window = CreateWindow(shell, initializeOnLoad: true);
            window.Show();
            Pump(window);

            // Without this call the app never restored the saved session, never
            // refreshed, and never registered the signed-in account.
            Assert.True(
                shell.HasInitialized,
                "MainWindow.OnLoaded must await MainViewModel.InitializeAsync().");

            window.Close();
            Pump(window);
        });
    }

    /// <summary>
    /// A second launch wakes this window: minimized is the state a user leaves it in, and
    /// activating a minimized window changes nothing, so the raise has to restore it first.
    /// </summary>
    [Fact]
    public void ASecondLaunchRestoresTheMinimizedWindow()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var window = CreateWindow(shell);

            window.Show();
            Pump(window);
            window.WindowState = WindowState.Minimized;
            Pump(window);

            window.BringToFront();
            Pump(window);

            Assert.Equal(WindowState.Normal, window.WindowState);

            window.Close();
            Pump(window);
        });
    }

    [Fact]
    public void AClosedWindowCannotTrimTheSharedImageCache()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var window = CreateWindow(shell);

            window.Show();
            Pump(window);
            window.Close();
            Pump(window);

            Assert.False(
                window.TryTrimBackgroundImages(),
                "A closed window trimmed the shared image cache.");
        });
    }

    [Theory]
    [InlineData(true, false, WindowState.Normal, false)]
    [InlineData(false, true, WindowState.Normal, false)]
    [InlineData(false, true, WindowState.Minimized, true)]
    [InlineData(false, false, WindowState.Normal, true)]
    public void BackgroundTrimRunsOnlyForAnOpenBackgroundWindow(
        bool isClosed,
        bool isActive,
        WindowState state,
        bool expected)
        => Assert.Equal(
            expected,
            MainWindow.ShouldTrimBackgroundImages(isClosed, isActive, state));

    private static MainWindow CreateWindow(MainViewModel shell, bool initializeOnLoad = false)
        => new(shell, initializeOnLoad)
        {
            Width = 1100,
            Height = 720,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 100,
            Top = 100
        };

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
