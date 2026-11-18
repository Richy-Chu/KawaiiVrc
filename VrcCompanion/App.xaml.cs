using System.IO;
using System.Windows;
using System.Windows.Threading;
using VrcCompanion.Services;

namespace VrcCompanion;

public partial class App : Application
{
    private bool _errorDialogShown;

    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcCompanion",
        "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        SessionEnding += (_, args) => LifecycleLog.Write($"session ending reason={args.ReasonSessionEnding}");
        LifecycleLog.Write("startup");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LifecycleLog.Write($"exit code={e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        e.Handled = true;

        // A layout/render error can fire on every measure pass. Showing one
        // dialog per retry buries the app in duplicate windows, so report the
        // first failure and keep subsequent details in the crash log.
        if (_errorDialogShown)
        {
            return;
        }

        _errorDialogShown = true;
        MessageBox.Show(
            "KawaiiVrc hit an unexpected error and may not work correctly.\n\n" +
            $"Details were written to:\n{CrashLogPath}",
            "KawaiiVrc",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogCrash(ex);
        }
    }

    private static void LogCrash(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(
                CrashLogPath,
                $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Never let crash logging take down the app.
        }
    }
}
