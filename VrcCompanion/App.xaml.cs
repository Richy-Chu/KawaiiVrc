using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VrcCompanion.Services;

namespace VrcCompanion;

public partial class App : Application
{
    private bool _errorDialogShown;
    private SingleInstance? _singleInstance;
    private bool _claimedHere;

    private static readonly string CrashLogPath = UserDataPaths.Combine("crash.log");

    /// <summary>One entry per kind of failure per run; see <see cref="CrashLog"/>.</summary>
    private readonly CrashLog _crashLog = new(CrashLogPath);

    /// <summary>
    /// The launch's single-instance claim. <see cref="Program.Main"/> makes it before any WPF
    /// work, so a second launch can leave without building a window or a view model; a host that
    /// builds the app itself - the WPF test host - gets its claim here instead.
    /// </summary>
    internal static SingleInstance? LaunchGate { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = LaunchGate ?? SingleInstance.Claim();
        _claimedHere = LaunchGate is null;
        if (!_singleInstance.IsOwner)
        {
            // Two copies share the one saved VRChat session, so this one leaves without a window
            // or a view model that could touch it.
            var answered = _singleInstance.HandOff();
            LifecycleLog.Write($"startup: second launch left (answered={answered})");
            Shutdown();
            return;
        }

        _singleInstance.Listen(BringRunningWindowForward);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        SessionEnding += (_, args) => LifecycleLog.Write($"session ending reason={args.ReasonSessionEnding}");
        LifecycleLog.Write("startup");
        base.OnStartup(e);

        // App.xaml names no StartupUri: a launch that handed off must not have a window built for
        // it at all, so the one window is made here, after the claim is settled.
        var shell = new MainWindow();
        MainWindow = shell;
        shell.Show();
    }

    /// <summary>
    /// A later launch asked for the window. The listener answers on its own thread, and a window
    /// may only be touched on the dispatcher.
    /// </summary>
    private void BringRunningWindowForward()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var shown = MainWindow;
            if (shown is MainWindow shell)
            {
                shell.BringToFront();
            }
        }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LifecycleLog.Write($"exit code={e.ApplicationExitCode}");
        // Program.Main owns the claim it made and disposes it after Run returns; this disposes
        // only the one this class made for a host that did not.
        if (_claimedHere)
        {
            _singleInstance?.Dispose();
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // One entry per kind of failure, however many measure passes repeat it.
        _crashLog.Write(e.Exception);
        e.Handled = true;

        // A layout/render error can fire on every measure pass. Showing one
        // dialog per retry buries the app in duplicate windows, so report the
        // first failure and keep every later one in the crash log.
        if (_errorDialogShown)
        {
            return;
        }

        _errorDialogShown = true;
        MessageBox.Show(
            "KawaiiVrc hit an unexpected error and may not work correctly.\n\n" +
            Describe(e.Exception) +
            $"\n\nDetails were written to:\n{CrashLogPath}",
            "KawaiiVrc",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _crashLog.Write(ex);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // A task nobody awaited failed. Without this it stays silent until the process ends,
        // which is how a broken background feature can look like one that simply stopped.
        _crashLog.Write(e.Exception);
        e.SetObserved();
    }

    /// <summary>
    /// Enough of the failure for the dialog to be worth reading: the type names what broke, and
    /// the message is usually the one thing the user could report.
    /// </summary>
    private static string Describe(Exception exception)
    {
        var text = exception.GetType().Name + ": " + exception.Message;
        return text.Length > 300 ? text.Substring(0, 300) + "..." : text;
    }
}
