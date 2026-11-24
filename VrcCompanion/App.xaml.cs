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

    public App()
    {
        CreateShell = () => new MainWindow();
        Stop = Shutdown;
    }

    /// <summary>
    /// The launch's single-instance claim. <see cref="Program.Main"/> makes it before any WPF
    /// work, so a second launch can leave without building a window or a view model; a host that
    /// builds the app itself - the WPF test host - gets its claim here instead.
    /// </summary>
    internal static SingleInstance? LaunchGate { get; set; }

    /// <summary>
    /// The one window a launch builds. A test replaces this to prove that a launch which handed
    /// off never asks for one: the shipped factory is the only place the shell is made, so a
    /// launch that does not call it builds no window and no view model.
    /// </summary>
    internal Func<MainWindow> CreateShell { get; set; }

    /// <summary>
    /// How a launch that handed off stops. The shipped value shuts the application down; the
    /// shared test host cannot do that, so a test replaces it.
    /// </summary>
    internal Action Stop { get; set; }

    /// <summary>
    /// Whether an unhandled dispatcher failure shows its dialog. The shipped app shows it; a host
    /// that runs the real startup path inside another process - the test suite - would stop for a
    /// modal nobody can answer, so it keeps the crash-log entry and drops the dialog.
    /// </summary>
    internal bool ShowFailureDialog { get; set; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Program.Main claims the instance and hands it over before Run, so a launch that gets
        // here has a gate. An application built without one - the WPF test host - never came
        // through the entry point: claiming the user's real instance there would find their
        // installed copy holding it, hand off to it, and shut this application down before its
        // first window could show. That is what left every window test after the first one
        // failing with "The Application object is being shut down" while the app was running.
        if (LaunchGate is not null)
        {
            RunStartup(() => base.OnStartup(e));
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>
    /// The launch itself, kept out of <see cref="OnStartup"/> so both of its sides can run in a
    /// test on the host's one application: claim the app, then either build its single window or
    /// hand off to the copy already running and stop. Returns whether this launch owns the app;
    /// false means it left without a window, a view model or a startup event.
    /// </summary>
    internal bool RunStartup(Action? startupEvent = null)
    {
        _singleInstance = LaunchGate ?? SingleInstance.Claim();
        _claimedHere = LaunchGate is null;
        if (!_singleInstance.IsOwner)
        {
            // Two copies share the one saved VRChat session, so this one leaves without a window
            // or a view model that could touch it.
            var answered = _singleInstance.HandOff();
            LifecycleLog.Write($"startup: second launch left (answered={answered})");
            Stop();
            return false;
        }

        _singleInstance.Listen(BringRunningWindowForward);

        // A host can run a launch more than once; one subscription per handler keeps the crash log
        // from recording the same failure twice.
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        SessionEnding += (_, args) => LifecycleLog.Write($"session ending reason={args.ReasonSessionEnding}");
        LifecycleLog.Write("startup");
        startupEvent?.Invoke();

        // App.xaml names no StartupUri: a launch that handed off must not have a window built for
        // it at all, so the one window is made here, after the claim is settled.
        var shell = CreateShell();
        MainWindow = shell;
        shell.Show();
        return true;
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
        // first failure and keep every later one in the crash log. A host that
        // cannot answer a modal drops the dialog entirely.
        if (_errorDialogShown || !ShowFailureDialog)
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
