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
    public void AClosedWindowNeverRearmsItsBackgroundTrimTimer()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var window = CreateWindow(shell);

            window.Show();
            Pump(window);

            // Leaving the window arms the timer; the close that follows used to
            // raise Deactivated again and rearm it for a page that was gone.
            window.RaiseWindowDeactivatedForTest();
            Assert.True(
                window.IsBackgroundTrimScheduled,
                "Deactivating the window did not schedule the background trim.");

            window.Close();
            Pump(window);
            window.RaiseWindowDeactivatedForTest();

            Assert.False(
                window.IsBackgroundTrimScheduled,
                "A deactivation rearmed the background trim timer after the window closed.");
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
    [InlineData(true, true, WindowState.Normal, false)]
    [InlineData(false, true, WindowState.Normal, false)]
    [InlineData(false, true, WindowState.Minimized, true)]
    [InlineData(false, false, WindowState.Normal, true)]
    [InlineData(false, false, WindowState.Minimized, true)]
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
