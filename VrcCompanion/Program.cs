using System;
using Velopack;

namespace VrcCompanion;

/// <summary>
/// The real entry point. Velopack must run before any other startup work: when it
/// launches the app for an install, update or uninstall hook it expects to handle
/// the argument and exit without booting the UI.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
