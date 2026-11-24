using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VrcCompanion.Services;

/// <summary>
/// One copy of the app per signed-in Windows user. The first launch holds a named mutex for the
/// life of the process and answers later launches on a named pipe; every later launch sends one
/// request down that pipe, which brings the running window forward, and then exits.
///
/// Two copies must never run: they share the one saved VRChat session, and VRChat rotates the
/// auth cookie on nearly every request, so whichever copy loses the race gets a 401 and clears
/// the saved session for both. They would also both write chatbox input to the same VRChat client
/// and both write the same settings file.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>
    /// Per session, not machine-wide: the profile and the saved session are per user, so another
    /// signed-in Windows user's copy is theirs to run.
    /// </summary>
    internal const string DefaultName = @"Local\KawaiiVrc.App";

    internal const string DefaultPipe = "KawaiiVrc.App.Show";

    private const string ShowRequest = "show";

    /// <summary>
    /// Windows refuses a foreground change from a process the user did not just start; the
    /// hand-off passes this right on. -1 is ASFW_ANY.
    /// </summary>
    private const int AsfwAny = -1;

    private static readonly TimeSpan DefaultHandOffTimeout = TimeSpan.FromSeconds(2);

    private readonly Mutex _mutex;
    private readonly bool _owner;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;

    private SingleInstance(Mutex mutex, bool owner, string pipeName)
    {
        _mutex = mutex;
        _owner = owner;
        _pipeName = pipeName;
    }

    /// <summary>Whether this launch is the one that may run the app.</summary>
    internal bool IsOwner => _owner;

    /// <summary>
    /// Claims the app for this launch. <c>createdNew</c> is false when another launch already
    /// holds the name, which is what makes this launch the second one; the names are parameters
    /// so a test can claim without colliding with the suite or with a running install.
    /// </summary>
    internal static SingleInstance Claim(string? name = null, string? pipeName = null)
    {
        var mutex = new Mutex(initiallyOwned: true, name ?? DefaultName, out var createdNew);
        return new SingleInstance(mutex, createdNew, pipeName ?? DefaultPipe);
    }

    /// <summary>
    /// Answers later launches, one at a time, until the process exits. The callback runs on the
    /// listening thread, so a caller with a window has to marshal it to its dispatcher.
    /// </summary>
    internal void Listen(Action show)
    {
        ArgumentNullException.ThrowIfNull(show);
        if (!_owner)
        {
            throw new InvalidOperationException(
                "Only the copy that owns the app can answer later launches.");
        }

        _listener ??= Task.Run(() => ListenAsync(show));
    }

    /// <summary>
    /// Asks the copy that owns the app to come forward. False means nobody answered - the copy
    /// may be on its way out - and the caller still stops, because a second copy is worse than
    /// no hand-off at all.
    /// </summary>
    internal bool HandOff(TimeSpan? timeout = null)
    {
        AllowTheOwnerToTakeFocus();

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            client.Connect((int)(timeout ?? DefaultHandOffTimeout).TotalMilliseconds);
            var request = Encoding.UTF8.GetBytes(ShowRequest + "\n");
            client.Write(request, 0, request.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync(Action show)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);

                var request = await ReadRequestAsync(server, _stop.Token).ConfigureAwait(false);
                if (string.Equals(request, ShowRequest, StringComparison.Ordinal))
                {
                    show();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A launch that leaves mid-request still gets its own quiet exit; the next one
                // needs the same answer, so keep listening.
            }
            catch (Exception ex)
            {
                // A pipe this copy cannot use is not worth a crash: a later launch that finds no
                // listener exits anyway, which is the outcome the guard exists for.
                LifecycleLog.Write(
                    $"single-instance: listener stopped ({ex.GetType().Name}: {ex.Message})");
                return;
            }
        }
    }

    private static async Task<string> ReadRequestAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var buffer = new byte[32];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await pipe.ReadAsync(buffer.AsMemory(read), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
            if (Array.IndexOf(buffer, (byte)'\n', 0, read) >= 0)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, read).Trim();
    }

    private static void AllowTheOwnerToTakeFocus()
    {
        try
        {
            // Best effort: this launch has the foreground rights the user's click grants, and the
            // copy being woken does not. A refusal only means the window has to rely on its own
            // activation and the taskbar flash.
            AllowSetForegroundWindow(AsfwAny);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not Windows, or not a user32 this build can call: the hand-off itself still works.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            // Give the listener a moment to leave its wait, so the pipe name is free before a
            // test claims it again. It reports its own failures, so nothing here is lost.
            _listener?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The listener already recorded why it stopped.
        }

        _stop.Dispose();

        // Ownership cannot be released from another thread, and it does not need to be: closing
        // the last handle removes the name, which is all a later launch looks for.
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
