using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The settle a tab switch runs has to actually collect and hand the pages back.
/// The hint it replaced (Optimized, non-blocking) is allowed to decide nothing is
/// worth doing, which is how a visit to the Media tab left its peak resident for
/// the session.
/// </summary>
public class MemorySettleTests
{
    [Fact]
    public void ASettleCollectsWhatTheClosedTabLeftBehind()
    {
        // Dropped in its own frame: a buffer still reachable from this one would
        // survive the collection and make the test say nothing.
        var (probe, garbageBytes) = AllocateAndDrop(128);

        var result = MemorySettle.Run();

        Assert.False(
            probe.IsAlive,
            "the settle left the closed tab's dropped buffers on the heap");
        Assert.True(
            result.FreedBytes >= garbageBytes / 2,
            $"the settle handed back {result.FreedBytes / (1024 * 1024)}MB of " +
            $"{garbageBytes / (1024 * 1024)}MB of dropped buffers");
        Assert.True(result.BeforeBytes > 0, "the settle reported no starting footprint");
        Assert.True(result.AfterBytes > 0, "the settle reported no final footprint");
    }

    [Fact]
    public void ACacheDroppedByTheSettleIsNotRetained()
    {
        var dropped = false;

        var result = MemorySettle.Run(() => dropped = true);

        Assert.True(dropped, "the settle collected before the decoded-image cache was dropped");
        Assert.True(result.Milliseconds >= 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Probe, long Bytes) AllocateAndDrop(int count)
    {
        // The metadata pass allocates one buffer per picture and drops it; after
        // a tab switch nothing but the settle can still reach these.
        var dropped = new List<byte[]>();
        for (var index = 0; index < count; index++)
        {
            dropped.Add(new byte[256 * 1024]);
        }

        return (new WeakReference(dropped[0]), (long)count * 256 * 1024);
    }
}
