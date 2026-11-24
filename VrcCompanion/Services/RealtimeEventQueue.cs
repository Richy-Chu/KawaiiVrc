using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VrcCompanion.Services;

/// <summary>
/// Buffers VRChat's realtime events and hands them to the UI thread in batches.
/// </summary>
/// <remarks>
/// VRChat delivers friend activity in bursts, and queueing one normal-priority dispatcher callback
/// per event meant a burst became a burst of callbacks: the dispatcher drained them ahead of input
/// and ahead of the stall monitor's background timer, which is what a five-second freeze with no
/// job to blame looks like. Posting instead appends to this queue and schedules a single callback;
/// each callback takes one bounded batch and then reschedules, so a flood still yields to input
/// between batches instead of holding the thread.
/// </remarks>
internal sealed class RealtimeEventQueue
{
    /// <summary>Events handled by one callback, so a long backlog cannot hold the thread.</summary>
    internal const int MaxBatchSize = 200;

    /// <summary>
    /// Backlog ceiling. A burst larger than this is stale by the time it is handled - each event
    /// carries the state at the time it arrived - so the oldest are dropped and the newest kept.
    /// </summary>
    internal const int MaxBacklog = 2000;

    private readonly object _sync = new();
    private readonly List<VrChatRealtimeEvent> _pending = new();
    private readonly Action<IReadOnlyList<VrChatRealtimeEvent>> _drain;
    private readonly Action<Action> _schedule;
    private bool _scheduled;

    /// <param name="drain">Applies one batch; runs on whichever thread <paramref name="schedule"/> uses.</param>
    /// <param name="schedule">Queues a callback, normally at background dispatcher priority.</param>
    internal RealtimeEventQueue(
        Action<IReadOnlyList<VrChatRealtimeEvent>> drain,
        Action<Action> schedule)
    {
        _drain = drain;
        _schedule = schedule;
    }

    /// <summary>Events waiting for the next callback. Exposed for tests and diagnostics.</summary>
    internal int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Adds an event and makes sure one callback is on its way. Safe from any thread.</summary>
    internal void Post(VrChatRealtimeEvent realtimeEvent)
    {
        var schedule = false;
        lock (_sync)
        {
            if (_pending.Count >= MaxBacklog)
            {
                _pending.RemoveRange(0, _pending.Count - MaxBacklog + 1);
            }

            _pending.Add(realtimeEvent);
            if (!_scheduled)
            {
                _scheduled = true;
                schedule = true;
            }
        }

        if (schedule)
        {
            _schedule(DrainNextBatch);
        }
    }

    /// <summary>Hands one batch to the drain and schedules another if anything is left.</summary>
    internal void DrainNextBatch()
    {
        VrChatRealtimeEvent[] batch;
        lock (_sync)
        {
            if (_pending.Count == 0)
            {
                _scheduled = false;
                return;
            }

            var count = Math.Min(MaxBatchSize, _pending.Count);
            batch = _pending.GetRange(0, count).ToArray();
            _pending.RemoveRange(0, count);
        }

        try
        {
            _drain(batch);
        }
        catch (Exception ex)
        {
            // One bad batch must not stop every later friend update from being applied.
            Debug.WriteLine("realtime batch: " + ex.Message);
        }
        finally
        {
            var more = false;
            lock (_sync)
            {
                more = _pending.Count > 0;
                if (!more)
                {
                    _scheduled = false;
                }
            }

            if (more)
            {
                _schedule(DrainNextBatch);
            }
        }
    }
}
