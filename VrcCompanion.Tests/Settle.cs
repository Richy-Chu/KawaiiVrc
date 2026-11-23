using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace VrcCompanion.Tests;

/// <summary>
/// The suite's one wait. Ten seconds is headroom for a loaded runner, never the thing under test:
/// an interval that matters is asserted with the interval itself. Every copy this replaced picked
/// its own bound - 2, 3, 5, 6, 10 and 15 seconds - which is how one of them ended up too thin to
/// survive CI.
/// </summary>
internal static class Settle
{
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const int PollMilliseconds = 10;

    /// <summary>Polls until the condition holds, sleeping between attempts.</summary>
    internal static void Until(
        Func<bool> condition,
        string? because = null,
        [CallerArgumentExpression(nameof(condition))] string? expression = null)
        => Until(condition, pump: null, because, expression);

    /// <summary>
    /// Polls until the condition holds, running <paramref name="pump"/> between attempts so a
    /// dispatcher or a window keeps working while the test waits.
    /// </summary>
    internal static void Until(
        Func<bool> condition,
        Action? pump,
        string? because = null,
        [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed >= Bound)
            {
                throw new TimeoutException(Failure(because, expression));
            }

            if (pump is null)
            {
                Thread.Sleep(PollMilliseconds);
            }
            else
            {
                pump();
            }
        }
    }

    /// <summary>
    /// Polls until the condition holds, running <paramref name="pump"/> between attempts. The
    /// description is built only when the wait fails, for callers that have to walk a visual tree
    /// to say anything useful.
    /// </summary>
    internal static void Until(
        Func<bool> condition,
        Action? pump,
        Func<string> describe,
        [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed >= Bound)
            {
                throw new TimeoutException(Failure(describe(), expression));
            }

            if (pump is null)
            {
                Thread.Sleep(PollMilliseconds);
            }
            else
            {
                pump();
            }
        }
    }

    /// <summary>Polls until the condition holds, yielding between attempts.</summary>
    internal static async Task UntilAsync(
        Func<bool> condition,
        string? because = null,
        [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed >= Bound)
            {
                throw new TimeoutException(Failure(because, expression));
            }

            await Task.Delay(PollMilliseconds).ConfigureAwait(true);
        }
    }

    /// <summary>Lets the dispatcher run whatever it has queued, once.</summary>
    internal static void PumpBackground()
        => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static string Failure(string? because, string? expression)
        => (because ?? "A condition the test waited for never became true") +
           " (waited " + Bound.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) +
           " s for: " + (expression ?? "condition") + ")";
}
