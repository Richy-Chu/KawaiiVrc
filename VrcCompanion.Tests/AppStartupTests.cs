using System;
using System.Threading;
using System.Windows;
using VrcCompanion.Services;
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
            var windowsBefore = Application.Current.Windows.Count;
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
                Assert.Equal(windowsBefore, Application.Current.Windows.Count);
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
}
