using System;
using System.Collections.Generic;
using System.Threading;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// A command runs from an <c>async void</c> boundary, so a failure used to escape into the
/// dispatcher's unhandled handler and read as a crash of the whole window - once, and with no
/// idea which button broke. It is reported to the command's own failure sink instead, and the
/// button has to come back so the user can try again.
/// </summary>
public sealed class AsyncCommandTests
{
    [Fact]
    public void AFailedCommandIsReportedAndCanRunAgain()
    {
        Exception? reported = null;
        using var caught = new ManualResetEventSlim();
        var command = new AsyncRelayCommand(
            () => throw new InvalidOperationException("the command broke"),
            onFailure: ex =>
            {
                reported = ex;
                caught.Set();
            });

        command.Execute(null);

        Assert.True(caught.Wait(TimeSpan.FromSeconds(5)), "The failure was never reported.");
        Assert.IsType<InvalidOperationException>(reported);
        Assert.True(
            SpinWait.SpinUntil(() => command.CanExecute(null), TimeSpan.FromSeconds(5)),
            "A command that failed has to be runnable again.");
    }

    [Fact]
    public void AFailedTypedCommandIsReportedAndCanRunAgain()
    {
        var reported = new List<Exception>();
        using var caught = new ManualResetEventSlim();
        var command = new AsyncRelayCommand<int>(
            _ => throw new InvalidOperationException("the typed command broke"),
            onFailure: ex =>
            {
                lock (reported)
                {
                    reported.Add(ex);
                }

                caught.Set();
            });

        command.Execute(7);

        Assert.True(caught.Wait(TimeSpan.FromSeconds(5)), "The failure was never reported.");
        Assert.Single(reported);
        Assert.True(
            SpinWait.SpinUntil(() => command.CanExecute(7), TimeSpan.FromSeconds(5)),
            "A typed command that failed has to be runnable again.");
    }

    [Fact]
    public void ASuccessfulCommandReportsNothing()
    {
        var ran = new ManualResetEventSlim();
        var command = new AsyncRelayCommand(
            () =>
            {
                ran.Set();
                return System.Threading.Tasks.Task.CompletedTask;
            },
            onFailure: _ => throw new InvalidOperationException("a success must not report"));

        command.Execute(null);

        Assert.True(ran.Wait(TimeSpan.FromSeconds(5)), "The command never ran.");
        Assert.True(SpinWait.SpinUntil(() => command.CanExecute(null), TimeSpan.FromSeconds(5)));
    }
}
