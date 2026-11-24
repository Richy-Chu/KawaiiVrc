using System.Diagnostics;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The stall log only helps if the name on the line is really the job that was in flight: a job
/// that had already finished, or one that started after the freeze, would send the next
/// investigation to the wrong place.
/// </summary>
public sealed class UiJobTraceTests
{
    private static readonly long Second = Stopwatch.Frequency;

    [Fact]
    public void AJobRunningAcrossTheStallIsNamed()
    {
        var trace = new UiJobTrace();
        using var job = trace.Begin("activity-poll");

        var now = Stopwatch.GetTimestamp();

        Assert.Equal("activity-poll", trace.Describe(now - Second, now));
    }

    [Fact]
    public void AJobThatFinishedBeforeTheStallIsNotNamed()
    {
        var trace = new UiJobTrace();
        using (trace.Begin("instance-history"))
        {
        }

        var now = Stopwatch.GetTimestamp();

        Assert.Null(trace.Describe(now, now + Second));
    }

    [Fact]
    public void AFinishedJobThatWasRunningWhenTheStallBeganIsNamed()
    {
        var trace = new UiJobTrace();
        var started = Stopwatch.GetTimestamp();
        using (trace.Begin("settle", started))
        {
        }

        var finished = Stopwatch.GetTimestamp();

        Assert.Equal("settle", trace.Describe(started, finished));
    }

    [Fact]
    public void TheLongestRunningJobIsNamedFirst()
    {
        var trace = new UiJobTrace();
        using var ticker = trace.Begin("chatbox-tick");
        using var poll = trace.Begin("activity-poll");

        var now = Stopwatch.GetTimestamp();

        Assert.Equal("chatbox-tick, activity-poll", trace.Describe(now - Second, now));
    }

    [Fact]
    public void NoRunningJobMeansNoName()
    {
        Assert.Null(new UiJobTrace().Describe(Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp()));
    }
}
