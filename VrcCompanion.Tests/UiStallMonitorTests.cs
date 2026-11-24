using System;
using System.Collections.Generic;
using System.Diagnostics;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The stall monitor is only useful if it reports real freezes and stays quiet
/// otherwise; a false line in lifecycle.log would send the next investigation
/// the wrong way.
/// </summary>
public sealed class UiStallMonitorTests
{
    [Fact]
    public void AFreezeLongerThanTheThresholdIsReportedWithItsLength()
    {
        var reported = new List<UiStall>();
        using var monitor = new UiStallMonitor(reported.Add);
        monitor.Start();

        monitor.SampleAt(Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 700 / 1000));

        Assert.Single(reported);
        Assert.InRange(reported[0].Milliseconds, 650, 800);
    }

    [Fact]
    public void NormalTicksAreNotReported()
    {
        var reported = new List<UiStall>();
        using var monitor = new UiStallMonitor(reported.Add);
        monitor.Start();

        var now = Stopwatch.GetTimestamp();
        monitor.SampleAt(now + (Stopwatch.Frequency / 10));
        monitor.SampleAt(now + (Stopwatch.Frequency * 2 / 10));

        Assert.Empty(reported);
    }

    [Fact]
    public void AShortGapRightAfterAFreezeIsNotReportedAgain()
    {
        var reported = new List<UiStall>();
        using var monitor = new UiStallMonitor(reported.Add);
        monitor.Start();

        var now = Stopwatch.GetTimestamp();
        monitor.SampleAt(now + (Stopwatch.Frequency / 2));
        monitor.SampleAt(now + (Stopwatch.Frequency / 2) + (Stopwatch.Frequency / 10));

        Assert.Single(reported);
    }

    [Fact]
    public void TheGapCarriesTheProcessorTimeTheThreadUsed()
    {
        var reported = new List<UiStall>();
        // 100 ns units, like the kernel's thread times: 50 ms of CPU before, 3.05 s after.
        var cpu = 50_000_000L;
        using var monitor = new UiStallMonitor(reported.Add, null, () => cpu);
        monitor.Start();

        cpu += 3_000_000L * 10;
        monitor.SampleAt(Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 5));

        Assert.Single(reported);
        Assert.InRange(reported[0].Milliseconds, 4900, 5100);
        Assert.Equal(3000, reported[0].CpuMilliseconds);
    }

    [Fact]
    public void AGapWithNoProcessorTimeIsReportedAsBlocked()
    {
        var reported = new List<UiStall>();
        using var monitor = new UiStallMonitor(reported.Add, null, () => 7_000_000L);
        monitor.Start();

        // The thread's clock did not move at all across five seconds: it was not running.
        monitor.SampleAt(Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 5));

        Assert.Single(reported);
        Assert.Equal(0, reported[0].CpuMilliseconds);
        Assert.Contains("blocked", UiStallMonitor.DescribeCpu(5000, 0));
    }

    [Fact]
    public void AThreadThatRanForTheWholeGapIsReportedAsBurningCpu()
    {
        Assert.Contains("burning cpu", UiStallMonitor.DescribeCpu(5000, 4900));
        Assert.Contains("mixed", UiStallMonitor.DescribeCpu(5000, 2500));
        Assert.Contains("cpu=unknown", UiStallMonitor.DescribeCpu(5000, null));
        Assert.Contains("cpu=420ms of 5315ms blocked", UiStallMonitor.DescribeCpu(5315, 420));
    }

    [Fact]
    public void AnImpossibleReadingIsReportedAsUnknownRatherThanAsANumber()
    {
        var reported = new List<UiStall>();
        // More processor time inside the gap than the gap itself cannot be true.
        var cpu = 0L;
        using var monitor = new UiStallMonitor(reported.Add, null, () => cpu);
        monitor.Start();

        cpu += 100_000_000L * 10;
        monitor.SampleAt(Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 2));

        Assert.Single(reported);
        Assert.Null(reported[0].CpuMilliseconds);
    }

    [Fact]
    public void NothingIsReportedWhileTheWindowIsMinimised()
    {
        var reported = new List<UiStall>();
        var minimised = true;
        using var monitor = new UiStallMonitor(reported.Add, () => minimised);
        monitor.Start();

        monitor.SampleAt(Stopwatch.GetTimestamp() + Stopwatch.Frequency);
        Assert.Empty(reported);

        minimised = false;
        monitor.SampleAt(Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2);
        Assert.Single(reported);
    }
}
