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
            window.Activate();
            Pump(window);

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

    private static MainWindow CreateWindow(MainViewModel shell)
        => new(shell, initializeOnLoad: false)
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
