using System.Windows;
using System.Windows.Threading;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The suite shares one <see cref="App"/> for the whole process, so anything that shuts it down
/// takes every later window test with it: the next <c>MainWindow</c> fails while loading its XAML
/// with "The Application object is being shut down." These pin the host's contract instead of
/// leaving it to whichever test happens to run first.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public sealed class TestHostLifecycleTests
{
    [Fact]
    public void TheHostApplicationNeverShutsItselfDown()
    {
        WpfTestHost.Run(() =>
        {
            var app = WpfTestHost.Application
                ?? throw new InvalidOperationException("The WPF host never built the application.");

            // OnLastWindowClose and OnMainWindowClose both stop the application as soon as the
            // window a test opened is closed, which is the cascade this pins shut.
            Assert.Equal(ShutdownMode.OnExplicitShutdown, app.ShutdownMode);
        });
    }

    [Fact]
    public void ClosingOneWindowLeavesTheHostAbleToOpenAnother()
    {
        WpfTestHost.Run(() =>
        {
            var app = WpfTestHost.Application
                ?? throw new InvalidOperationException("The WPF host never built the application.");

            using var model = new MainViewModel();
            var windowsBefore = app.Windows.Count;

            var first = CreateWindow(model);
            first.Show();
            Pump(first);
            first.Close();
            Pump(first);

            // The second window is what a later test in the suite does; if the first close shut
            // the application down, its constructor throws before this returns.
            var second = CreateWindow(model);
            second.Show();
            Pump(second);
            Assert.True(second.IsVisible, "The host could not show a window after the previous one closed.");
            second.Close();
            Pump(second);

            Assert.Equal(windowsBefore, app.Windows.Count);
        });
    }

    private static MainWindow CreateWindow(MainViewModel shell)
        => new(shell, initializeOnLoad: false)
        {
            Width = 900,
            Height = 600,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 120,
            Top = 120
        };

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
