using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace VrcCompanion.Services;

/// <summary>
/// Names the periodic job that was in flight when the UI thread stopped responding. A stall line
/// that only says how long the freeze lasted sends the next investigation nowhere; recording where
/// each job began and ended lets the same line say which job ran across it.
/// </summary>
/// <remarks>
/// A job starts and finishes on whichever thread its awaits resume on, so the trace is guarded by a
/// lock rather than dispatcher affinity. The trace holds only the jobs still running plus the last
/// few that finished, and writes nothing itself: a stall line names the jobs that overlapped the
/// gap, and a slow job reports its own length through <see cref="OnSlowJob"/>.
/// </remarks>
internal sealed class UiJobTrace
{
    /// <summary>Finished jobs kept for a stall reported just after one of them ended.</summary>
    private const int RecentCapacity = 8;

    /// <summary>Ceiling on jobs held at once, so a caller that never disposes cannot grow the list forever.</summary>
    private const int ActiveCapacity = 64;

    private static readonly IComparer<(string Name, long Overlap)> LongestFirst =
        Comparer<(string Name, long Overlap)>.Create(
            (left, right) => right.Overlap.CompareTo(left.Overlap));

    /// <summary>A job this long is one a person can feel; the app logs it by name and length.</summary>
    internal const long SlowJobMilliseconds = 200;

    /// <summary>The process-wide trace the shipped app records into.</summary>
    internal static UiJobTrace Shared { get; } = new();

    /// <summary>
    /// Called with a job's name and length once it finishes, if it took at least
    /// <see cref="SlowJobMilliseconds"/>. A stall line says which jobs overlapped the gap; this
    /// says which of them was actually doing the work. Runs on whichever thread ended the job.
    /// </summary>
    internal Action<string, long>? OnSlowJob { get; set; }

    private readonly object _sync = new();
    private readonly List<Entry> _active = new();
    private readonly Entry?[] _recent = new Entry?[RecentCapacity];
    private int _recentNext;

    /// <summary>
    /// Records <paramref name="name"/> as running until the returned scope is disposed. Wrap the
    /// whole job, awaits included: a job waiting on the pool is still the job a UI stall may have
    /// starved, so its name belongs on the line either way.
    /// </summary>
    internal IDisposable Begin(string name) => Begin(name, Stopwatch.GetTimestamp());

    internal IDisposable Begin(string name, long startedTicks)
    {
        var entry = new Entry(name, startedTicks);
        lock (_sync)
        {
            if (_active.Count >= ActiveCapacity)
            {
                _active.RemoveAt(0);
            }

            _active.Add(entry);
        }

        return new Scope(this, entry);
    }

    /// <summary>
    /// Names every job that overlapped the window between <paramref name="fromTicks"/> and
    /// <paramref name="toTicks"/>, longest overlap first, or null when none did. The window is the
    /// stretch the UI thread could not answer, so a job named here was in flight across it.
    /// </summary>
    internal string? Describe(long fromTicks, long toTicks)
    {
        var overlaps = new List<(string Name, long Overlap)>();
        lock (_sync)
        {
            foreach (var job in _active)
            {
                // Still running, so it can only miss the window by starting after it ended.
                if (job.StartedTicks <= toTicks)
                {
                    overlaps.Add((job.Name, toTicks - job.StartedTicks));
                }
            }

            foreach (var job in _recent)
            {
                if (job is null || job.StartedTicks > toTicks || job.EndedTicks < fromTicks)
                {
                    continue;
                }

                var start = Math.Max(job.StartedTicks, fromTicks);
                var end = Math.Min(job.EndedTicks, toTicks);
                overlaps.Add((job.Name, end - start));
            }
        }

        if (overlaps.Count == 0)
        {
            return null;
        }

        // Sorted by overlap, then de-duplicated: a job that restarted inside the window is named
        // once, in the place its longest run earned.
        overlaps.Sort(LongestFirst);
        return string.Join(", ", overlaps.Select(overlap => overlap.Name).Distinct());
    }

    private void End(Entry entry)
    {
        var now = Stopwatch.GetTimestamp();
        entry.Stop(now);
        lock (_sync)
        {
            if (_active.Remove(entry))
            {
                _recent[_recentNext] = entry;
                _recentNext = (_recentNext + 1) % RecentCapacity;
            }
        }

        if (OnSlowJob is not { } report)
        {
            return;
        }

        var milliseconds = (now - entry.StartedTicks) * 1000L / Stopwatch.Frequency;
        if (milliseconds >= SlowJobMilliseconds)
        {
            report(entry.Name, milliseconds);
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly UiJobTrace _trace;
        private Entry? _entry;

        internal Scope(UiJobTrace trace, Entry entry)
        {
            _trace = trace;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_entry is { } entry)
            {
                _entry = null;
                _trace.End(entry);
            }
        }
    }

    private sealed class Entry
    {
        private bool _stopped;

        internal Entry(string name, long startedTicks)
        {
            Name = name;
            StartedTicks = startedTicks;
        }

        internal string Name { get; }

        internal long StartedTicks { get; }

        internal long EndedTicks { get; private set; }

        internal void Stop(long ticks)
        {
            // A scope disposed twice must not move its end forward into a later stall.
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            EndedTicks = ticks;
        }
    }
}
