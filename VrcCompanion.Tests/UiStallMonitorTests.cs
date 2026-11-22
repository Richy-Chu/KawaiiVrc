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
        var reported = new List<long>();
        using var monitor = new UiStallMonitor(reported.Add);
        monitor.Start();

        monitor.SampleAt(Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 700 / 1000));

        Assert.Single(reported);
        Assert.InRange(reported[0], 650, 800);
    }

    [Fact]
    public void NormalTicksAreNotReported()
    {
        var reported = new List<long>();
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
        var reported = new List<long>();
        using var monitor = new UiStallMonitor(reported.Add);
        monitor.Start();

        var now = Stopwatch.GetTimestamp();
        monitor.SampleAt(now + (Stopwatch.Frequency / 2));
        monitor.SampleAt(now + (Stopwatch.Frequency / 2) + (Stopwatch.Frequency / 10));

        Assert.Single(reported);
    }

    [Fact]
    public void NothingIsReportedWhileTheWindowIsMinimised()
    {
        var reported = new List<long>();
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
