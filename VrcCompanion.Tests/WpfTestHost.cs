using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VrcCompanion;

namespace VrcCompanion.Tests;

/// <summary>
/// One STA thread for WPF tests. It initializes the real App resources so a
/// MainWindow can be constructed exactly as the shipped application does.
/// </summary>
internal static class WpfTestHost
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;
    private static Exception? _startupFailure;

    public static void Run(Action action)
    {
        var dispatcher = EnsureStarted();
        Exception? failure = null;
        dispatcher.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public static void RunAsync(Func<Task> action)
    {
        Run(() =>
        {
            var frame = new DispatcherFrame();
            Exception? failure = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(async () =>
                {
                    try
                    {
                        await action();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        frame.Continue = false;
                    }
                }));
            Dispatcher.PushFrame(frame);

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        });
    }

    private static Dispatcher EnsureStarted()
    {
        lock (Gate)
        {
            if (_dispatcher is not null)
            {
                return _dispatcher;
            }

            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try
                {
                    var application = new App
                    {
                        ShutdownMode = ShutdownMode.OnExplicitShutdown
                    };
                    application.InitializeComponent();

                    // App.xaml's StartupUri makes WPF open the shipped main window
                    // on the first Show(). Tests build and close their own windows,
                    // so that shell would stay open for the whole run and, once
                    // another window took focus, keep a background trim timer armed
                    // that drops the shared image cache mid-test. StartupUri rejects
                    // null, so clear the field it reads instead.
                    var startupUri = typeof(Application).GetField(
                        "_startupUri",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException(
                            "WPF no longer stores StartupUri in Application._startupUri.");
                    startupUri.SetValue(application, null);
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                    _dispatcher = Dispatcher.CurrentDispatcher;
                }
                catch (Exception ex)
                {
                    _startupFailure = ex;
                }
                finally
                {
                    ready.Set();
                }

                if (_startupFailure is null)
                {
                    Dispatcher.Run();
                }
            })
            {
                IsBackground = true,
                Name = "WPF test host"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();

            if (_startupFailure is not null)
            {
                throw new InvalidOperationException(
                    "Could not initialize the WPF application resources for tests.",
                    _startupFailure);
            }

            return _dispatcher
                ?? throw new InvalidOperationException("WPF test dispatcher did not start.");
        }
    }
}
