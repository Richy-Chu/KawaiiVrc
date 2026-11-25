using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// A settle used to depend on a page being handed back: leaving Media or the
/// avatar library, or pushing a page out of the two-view cache. Two light tabs
/// both stay cached, so moving between them settled nothing and their garbage
/// stayed. Every switch settles now.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class SectionSettleTests
{
    [Fact]
    public void SwitchingBetweenTwoLightSectionsStillSettles()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            var window = CreateWindow(shell);
            window.Show();

            Select(shell, window, "worlds");
            Assert.Equal(0, window.SettleCount);

            // Two cached views: nothing is evicted by this switch.
            Select(shell, window, "friends");

            Settle.Until(
                () => window.SettleCount >= 1,
                PumpDispatcher,
                "a switch that kept both pages cached settled nothing");

            window.Close();
        });
    }

    [Fact]
    public void ReturningToTheSectionThatIsAlreadyOpenDoesNotSettle()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            var window = CreateWindow(shell);
            window.Show();

            Select(shell, window, "worlds");
            var changed = shell.Sections.Single(section => section.Key == "worlds");
            shell.SelectedSection = changed;
            PumpDispatcher();
            PumpDispatcher();

            // Selecting the section that is already shown is not a switch.
            Assert.Equal(0, window.SettleCount);

            window.Close();
        });
    }

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

    /// <summary>A session file this test owns; never the signed-in user's.</summary>
    private static VrcApiClient IsolatedApi()
        => new(new SessionStore(Path.Combine(
            Path.GetTempPath(),
            "vrc-settle-" + Guid.NewGuid().ToString("N"))));

    private static void Select(MainViewModel shell, MainWindow window, string key)
    {
        shell.SelectedSection = shell.Sections.Single(section => section.Key == key);
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void PumpDispatcher()
        => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
