using System.Text.Json;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class RealtimeEventQueueTests
{
    [Fact]
    public void PostsScheduleOneCallbackAndDrainThemTogether()
    {
        var batches = new List<IReadOnlyList<VrChatRealtimeEvent>>();
        var scheduled = new List<Action>();
        var queue = new RealtimeEventQueue(batches.Add, scheduled.Add);

        queue.Post(Event("friend-online"));
        queue.Post(Event("friend-offline"));
        queue.Post(Event("friend-location"));

        // Three events, one dispatcher callback: a burst costs the UI thread one item.
        Assert.Single(scheduled);
        Assert.Equal(0, batches.Count);

        scheduled[0]();

        Assert.Single(batches);
        Assert.Equal(3, batches[0].Count);
        Assert.Equal("friend-online", batches[0][0].Type);
        Assert.Equal("friend-location", batches[0][2].Type);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void ABatchIsBoundedAndTheRestIsScheduledAgain()
    {
        var batches = new List<IReadOnlyList<VrChatRealtimeEvent>>();
        var scheduled = new List<Action>();
        var queue = new RealtimeEventQueue(batches.Add, scheduled.Add);

        for (var i = 0; i < RealtimeEventQueue.MaxBatchSize + 5; i++)
        {
            queue.Post(Event("friend-update"));
        }

        scheduled[0]();

        // One callback never handles the whole backlog: it hands back the thread first.
        Assert.Single(batches);
        Assert.Equal(RealtimeEventQueue.MaxBatchSize, batches[0].Count);
        Assert.Equal(2, scheduled.Count);
        Assert.Equal(5, queue.PendingCount);

        scheduled[1]();

        Assert.Equal(2, batches.Count);
        Assert.Equal(5, batches[1].Count);
        Assert.Equal(0, queue.PendingCount);

        // Nothing left, so the next post schedules a fresh callback.
        queue.Post(Event("friend-update"));
        Assert.Equal(3, scheduled.Count);
    }

    [Fact]
    public void AFloodKeepsTheNewestEvents()
    {
        var batches = new List<IReadOnlyList<VrChatRealtimeEvent>>();
        var scheduled = new List<Action>();
        var queue = new RealtimeEventQueue(batches.Add, scheduled.Add);

        for (var i = 0; i < RealtimeEventQueue.MaxBacklog + 10; i++)
        {
            queue.Post(Event("friend-update", i));
        }

        Assert.Equal(RealtimeEventQueue.MaxBacklog, queue.PendingCount);
        while (scheduled.Count > batches.Count)
        {
            scheduled[batches.Count]();
        }

        var applied = batches.SelectMany(batch => batch).Select(item => item.Raw).ToList();
        // The backlog is bounded and the stale head is what went: the newest ten survive.
        Assert.Equal(RealtimeEventQueue.MaxBacklog, applied.Count);
        Assert.Equal(RealtimeEventQueue.MaxBacklog + 9, int.Parse(applied[^1]));
        Assert.Equal("10", applied[0]);
    }

    [Fact]
    public void ABatchThatThrowsStillLetsTheNextOneRun()
    {
        var applied = 0;
        var scheduled = new List<Action>();
        var queue = new RealtimeEventQueue(
            _ =>
            {
                applied++;
                if (applied == 1)
                {
                    throw new InvalidOperationException("bad batch");
                }
            },
            scheduled.Add);

        for (var i = 0; i < RealtimeEventQueue.MaxBatchSize + 1; i++)
        {
            queue.Post(Event("friend-update"));
        }

        scheduled[0]();

        Assert.Equal(2, scheduled.Count);
        scheduled[1]();
        Assert.Equal(2, applied);
        Assert.Equal(0, queue.PendingCount);
    }

    private static VrChatRealtimeEvent Event(string type, int index = 0)
        => new(type, default(JsonElement), index.ToString(), DateTimeOffset.UnixEpoch);
}
