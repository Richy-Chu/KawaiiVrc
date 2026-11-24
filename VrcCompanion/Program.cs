using System;
using Velopack;
using VrcCompanion.Services;

namespace VrcCompanion;

/// <summary>
/// The real entry point. Velopack must run before any other startup work: when it
/// launches the app for an install, update or uninstall hook it expects to handle
/// the argument and exit without booting the UI.
/// </summary>
public static class Program
{
    /// <summary>Clock for the launch timings written to lifecycle.log.</summary>
    internal static readonly System.Diagnostics.Stopwatch Startup = System.Diagnostics.Stopwatch.StartNew();

    [STAThread]
    public static void Main(string[] args)
    {
        Startup.Restart();
        VelopackApp.Build().Run();

        // One copy per Windows user, claimed before any WPF work. Two copies share the one saved
        // VRChat session and every request rotates its cookie, so the copy that loses the race
        // signs both out; they also both write chatbox input and the same settings file. A later
        // launch asks the running window to come forward and leaves without building either.
        using var instance = SingleInstance.Claim();
        if (!instance.IsOwner)
        {
            var answered = instance.HandOff();
            LifecycleLog.Write($"startup: second launch left (answered={answered})");
            return;
        }

        App.LaunchGate = instance;
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
