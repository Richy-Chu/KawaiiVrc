using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The wiring, not the guard: a launch that finds the app already running has to stop before it
/// builds the shell window - and with it the view model that polls VRChat and reads the saved
/// session. The shared WPF host is the real <see cref="App"/>, with its window factory and stop
/// action replaced so the case runs on the one application WPF allows per process, and without
/// shutting the suite down.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public sealed class AppStartupTests
{
    [Fact]
    public void ASecondLaunchStopsWithoutBuildingTheShell()
    {
        WpfTestHost.Run(() =>
        {
            var app = WpfTestHost.Application
                ?? throw new InvalidOperationException("The WPF host never built the application.");

            var id = Guid.NewGuid().ToString("N");
            using var running = SingleInstance.Claim(
                @"Local\KawaiiVrc.Tests." + id,
                "KawaiiVrc.Tests." + id);
            using var second = SingleInstance.Claim(
                @"Local\KawaiiVrc.Tests." + id,
                "KawaiiVrc.Tests." + id);
            using var asked = new ManualResetEventSlim();
            running.Listen(() => asked.Set());

            var madeWindows = 0;
            var stops = 0;
            var windowsBefore = app.Windows.Count;
            var shellBefore = app.MainWindow;
            var createShell = app.CreateShell;
            var stop = app.Stop;
            App.LaunchGate = second;
            app.CreateShell = () =>
            {
                madeWindows++;
                throw new InvalidOperationException(
                    "A launch that handed off built the shell window.");
            };
            app.Stop = () => stops++;

            try
            {
                var owns = app.RunStartup();

                Assert.False(owns, "A second launch must not be treated as the app's owner.");
                Assert.Equal(0, madeWindows);
                Assert.Equal(1, stops);
                Assert.Equal(windowsBefore, app.Windows.Count);
                Assert.Same(shellBefore, app.MainWindow);
                Assert.True(
                    asked.Wait(TimeSpan.FromSeconds(5)),
                    "The running copy was never asked to come forward.");
            }
            finally
            {
                App.LaunchGate = null;
                app.CreateShell = createShell;
                app.Stop = stop;
            }
        });
    }

    [Fact]
    public void AnOwningLaunchBuildsOneShellAndRaisesTheStartupEvent()
    {
        WpfTestHost.Run(() =>
        {
            var app = WpfTestHost.Application
                ?? throw new InvalidOperationException("The WPF host never built the application.");

            var id = Guid.NewGuid().ToString("N");
            using var claim = SingleInstance.Claim(
                @"Local\KawaiiVrc.Tests." + id,
                "KawaiiVrc.Tests." + id);

            var madeWindows = 0;
            var startupEvents = 0;
            var windowsBefore = app.Windows.Count;
            var shellBefore = app.MainWindow;
            var createShell = app.CreateShell;
            var stop = app.Stop;
            MainViewModel? model = null;
            MainWindow? shell = null;
            App.LaunchGate = claim;
            app.Stop = () => throw new InvalidOperationException(
                "A launch that owns the app stopped itself.");
            app.CreateShell = () =>
            {
                madeWindows++;
                var created = new MainViewModel();
                model = created;
                shell = new MainWindow(created, initializeOnLoad: false);
                return shell;
            };

            try
            {
                var owns = app.RunStartup(() => startupEvents++);

                Assert.True(owns, "The claim this launch made owns the app, so startup must say so.");
                Assert.Equal(1, madeWindows);
                Assert.Equal(1, startupEvents);
                Assert.Same(shell, app.MainWindow);

                // Show queues the window's layout, so it is visible only once the dispatcher has
                // run that pass; the lifetime tests pump for the same reason.
                Pump(shell!);

                Assert.True(shell!.IsVisible, "The one window the owning launch built was never shown.");
                Assert.Equal(windowsBefore + 1, app.Windows.Count);

                // The suite shares this application, so the window it opened must not outlive
                // the case: closing it puts the host back to the windows it started with.
                shell.Close();

                Assert.Equal(windowsBefore, app.Windows.Count);
                Assert.NotNull(Application.Current);
            }
            finally
            {
                if (shell is { IsVisible: true })
                {
                    shell.Close();
                }

                model?.Dispose();
                app.MainWindow = shellBefore;
                App.LaunchGate = null;
                app.CreateShell = createShell;
                app.Stop = stop;
            }
        });
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
