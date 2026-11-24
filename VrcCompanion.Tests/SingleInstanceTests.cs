using System;
using System.Threading;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The guard is what keeps a second launch from sharing the one saved VRChat session: every
/// request rotates the cookie, so the copy that loses the race gets a 401 and clears the session
/// for both. These cases drive the guard itself - the claim, the hand-off, an answer nobody
/// gives, and the window it wakes - because the wiring either side of it is two lines.
/// </summary>
public sealed class SingleInstanceTests
{
    [Fact]
    public void ASecondLaunchCannotAlsoOwnTheApp()
    {
        var (name, pipe) = Names();
        using var first = SingleInstance.Claim(name, pipe);
        using var second = SingleInstance.Claim(name, pipe);

        Assert.True(first.IsOwner, "The first launch has to own the app.");
        Assert.False(second.IsOwner, "A second launch must never own the app.");
    }

    [Fact]
    public void TheSecondLaunchAsksTheRunningCopyToComeForward()
    {
        var (name, pipe) = Names();
        using var running = SingleInstance.Claim(name, pipe);
        using var asked = new ManualResetEventSlim();
        running.Listen(() => asked.Set());

        using var second = SingleInstance.Claim(name, pipe);

        Assert.False(second.IsOwner);
        Assert.True(
            second.HandOff(TimeSpan.FromSeconds(5)),
            "The second launch got no answer from the copy that owns the app.");
        Assert.True(
            asked.Wait(TimeSpan.FromSeconds(5)),
            "The running copy never brought itself forward.");
    }

    /// <summary>
    /// The launch that cannot be handed to still stops: a running copy on its way out leaves no
    /// listener, and a launch that waited for one would sit there invisible.
    /// </summary>
    [Fact]
    public void AHandOffNobodyAnswersIsReportedInsteadOfWaitedOn()
    {
        var (name, pipe) = Names();
        using var running = SingleInstance.Claim(name, pipe);
        using var second = SingleInstance.Claim(name, pipe);

        Assert.False(
            second.HandOff(TimeSpan.FromMilliseconds(200)),
            "A hand-off with no listener has to report that nobody answered.");
    }

    /// <summary>
    /// Only the copy that owns the app may serve the pipe: a second copy listening would take the
    /// next launch's request and leave the real window untouched.
    /// </summary>
    [Fact]
    public void ASecondLaunchCannotAnswerForTheApp()
    {
        var (name, pipe) = Names();
        using var first = SingleInstance.Claim(name, pipe);
        using var second = SingleInstance.Claim(name, pipe);

        var refused = Assert.Throws<InvalidOperationException>(() => second.Listen(() => { }));

        Assert.Contains("owns the app", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every case gets its own mutex and pipe, so a suite running beside an installed copy - and
    /// the cases running beside each other - never see one another.
    /// </summary>
    private static (string Name, string Pipe) Names()
    {
        var id = Guid.NewGuid().ToString("N");
        return (@"Local\KawaiiVrc.Tests." + id, "KawaiiVrc.Tests." + id);
    }
}
