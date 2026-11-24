using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace VrcCompanion.Services;

/// <summary>
/// One freeze: how long the UI thread could not answer, which stretch that was, and how much of it
/// the thread spent running rather than waiting.
/// </summary>
/// <param name="CpuMilliseconds">
/// Processor time the UI thread used inside the gap, or null when the clock could not be read.
/// Near zero means the thread was not running - blocked on I/O, a lock or the GPU, or descheduled;
/// near the gap means it was burning CPU, which points at the work it was doing instead.
/// </param>
internal readonly record struct UiStall(
    long Milliseconds,
    long StartedTicks,
    long EndedTicks,
    long? CpuMilliseconds = null);

/// <summary>
/// Logs moments when the UI thread stopped responding. The timer runs at
/// background dispatcher priority, so it only ticks when the dispatcher has
/// nothing more important to do: the gap between ticks is time input and
/// rendering could not run. One stopwatch read and one thread-time read per
/// tick and nothing allocated unless the UI actually froze, which is what makes
/// it cheap enough to leave on.
/// </summary>
internal sealed class UiStallMonitor : IDisposable
{
    /// <summary>A gap this long is a freeze a person would notice.</summary>
    internal static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(250);

    /// <summary>Sampling this often keeps the reading within one tick of the truth.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Above this share of the gap the thread was running, so the freeze is work it was doing.
    /// A timer tick cannot account for much on its own, so the band is wide on purpose.
    /// </summary>
    internal const double BusyShare = 0.75;

    /// <summary>
    /// Below this share the thread barely ran, so it was waiting rather than computing: I/O, a
    /// lock, the GPU, or a scheduler that had something else to run.
    /// </summary>
    internal const double BlockedShare = 0.15;

    private readonly DispatcherTimer _timer;
    private readonly Action<UiStall> _report;
    private readonly Func<bool>? _suppress;
    private readonly Func<long?> _readCpu;
    private long _lastTicks;
    private long? _lastCpu;

    /// <param name="report">Called with the freeze and the window it covered.</param>
    /// <param name="suppress">
    /// Consulted before reporting; used to ignore gaps while the window is
    /// minimised, where nobody can see a freeze and Windows coalesces timers.
    /// </param>
    /// <param name="readCpu">
    /// The calling thread's own processor time in 100 ns units, or null when it cannot be read.
    /// Defaults to the real thread clock; tests hand in a fixed one to pin the reading.
    /// </param>
    internal UiStallMonitor(
        Action<UiStall> report,
        Func<bool>? suppress = null,
        Func<long?>? readCpu = null)
    {
        _report = report;
        _suppress = suppress;
        _readCpu = readCpu ?? UiThreadCpu.ReadHundredNanoseconds;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _timer.Tick += (_, _) => Sample();
    }

    internal void Start()
    {
        _lastTicks = Stopwatch.GetTimestamp();
        _lastCpu = _readCpu();
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
    }

    /// <summary>One reading; the timer calls this, and tests call it directly.</summary>
    internal void Sample() => SampleAt(Stopwatch.GetTimestamp());

    internal void SampleAt(long nowTicks)
    {
        var previous = _lastTicks;
        var previousCpu = _lastCpu;
        // Read at the same moment as the timestamp, so the two deltas cover the same stretch.
        var cpu = _readCpu();
        var elapsed = nowTicks - previous;
        _lastTicks = nowTicks;
        _lastCpu = cpu;
        if (elapsed <= 0 || _suppress?.Invoke() == true)
        {
            return;
        }

        var milliseconds = elapsed * 1000L / Stopwatch.Frequency;
        if (milliseconds >= (long)Threshold.TotalMilliseconds)
        {
            // The window the UI thread was deaf for is [previous, now]: the next job to tick after
            // a freeze would otherwise reset the reading and hide what was running across it.
            _report(new UiStall(
                milliseconds,
                previous,
                nowTicks,
                GapCpuMilliseconds(previousCpu, cpu, milliseconds)));
        }
    }

    /// <summary>
    /// Processor time the thread used inside the gap, in milliseconds, or null when either reading
    /// is missing or impossible. A thread cannot run for longer than the gap, so a reading past it
    /// means the two samples did not cover the same stretch and is reported as unknown rather than
    /// as a number that would mislead the next investigation.
    /// </summary>
    private static long? GapCpuMilliseconds(long? before, long? after, long milliseconds)
    {
        if (before is not { } start || after is not { } end || end < start)
        {
            return null;
        }

        var used = (end - start) / 10_000L;
        return used > milliseconds ? null : used;
    }

    /// <summary>
    /// The stall line's reading of the gap: whether the UI thread was burning CPU or waiting.
    /// Written here, with the monitor, so the line and the thresholds that produced it stay
    /// together.
    /// </summary>
    internal static string DescribeCpu(long milliseconds, long? cpuMilliseconds)
    {
        if (cpuMilliseconds is not { } cpu)
        {
            return "cpu=unknown";
        }

        var share = milliseconds <= 0 ? 0d : (double)cpu / milliseconds;
        var verdict = share >= BusyShare
            ? "burning cpu"
            : share <= BlockedShare
                ? "blocked"
                : "mixed";
        return $"cpu={cpu}ms of {milliseconds}ms {verdict}";
    }
}

/// <summary>
/// The calling thread's own processor time, straight from the kernel. Reading the current thread's
/// clock touches only that thread's handle, so it costs nothing like enumerating the process's
/// threads, and it answers the one question a freeze leaves open: was the thread running?
/// </summary>
internal static class UiThreadCpu
{
    /// <summary>Kernel plus user time in 100 ns units, or null when the clock is unavailable.</summary>
    internal static long? ReadHundredNanoseconds()
    {
        try
        {
            return GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user)
                ? kernel + user
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(
        IntPtr thread,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);
}
