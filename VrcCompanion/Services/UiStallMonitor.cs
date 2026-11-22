using System.Diagnostics;
using System.Windows.Threading;

namespace VrcCompanion.Services;

/// <summary>
/// Logs moments when the UI thread stopped responding. The timer runs at
/// background dispatcher priority, so it only ticks when the dispatcher has
/// nothing more important to do: the gap between ticks is time input and
/// rendering could not run. One stopwatch read per tick and nothing allocated
/// unless the UI actually froze, which is what makes it cheap enough to leave on.
/// </summary>
internal sealed class UiStallMonitor : IDisposable
{
    /// <summary>A gap this long is a freeze a person would notice.</summary>
    internal static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(250);

    /// <summary>Sampling this often keeps the reading within one tick of the truth.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherTimer _timer;
    private readonly Action<long> _report;
    private readonly Func<bool>? _suppress;
    private long _lastTicks;

    /// <param name="report">Called with the freeze length in milliseconds.</param>
    /// <param name="suppress">
    /// Consulted before reporting; used to ignore gaps while the window is
    /// minimised, where nobody can see a freeze and Windows coalesces timers.
    /// </param>
    internal UiStallMonitor(Action<long> report, Func<bool>? suppress = null)
    {
        _report = report;
        _suppress = suppress;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _timer.Tick += (_, _) => Sample();
    }

    internal void Start()
    {
        _lastTicks = Stopwatch.GetTimestamp();
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
        var elapsed = nowTicks - _lastTicks;
        _lastTicks = nowTicks;
        if (elapsed <= 0 || _suppress?.Invoke() == true)
        {
            return;
        }

        var milliseconds = elapsed * 1000L / Stopwatch.Frequency;
        if (milliseconds >= (long)Threshold.TotalMilliseconds)
        {
            _report(milliseconds);
        }
    }
}
