using System;
using System.IO;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// A repeating failure used to append its whole stack to crash.log on every measure pass. The log
/// is now one entry per kind of failure per run and never writes past its cap, so these cases
/// decide both: the same failure twice, two different failures, the previous generation, and a
/// log the app may not write at all.
/// </summary>
public sealed class CrashLogTests
{
    [Fact]
    public void ARepeatedFailureIsWrittenOnce()
    {
        var path = NewPath();
        var log = new CrashLog(path);

        Assert.True(log.Write(Thrown("the same thing")));
        Assert.False(log.Write(Thrown("the same thing")));

        var text = File.ReadAllText(path);
        Assert.Equal(1, Occurrences(text, "the same thing"));
    }

    [Fact]
    public void TwoDifferentFailuresAreBothWritten()
    {
        var path = NewPath();
        var log = new CrashLog(path);

        Assert.True(log.Write(Thrown("first failure")));
        Assert.True(log.Write(Thrown("second failure")));

        var text = File.ReadAllText(path);
        Assert.Contains("first failure", text, StringComparison.Ordinal);
        Assert.Contains("second failure", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// At the cap the current file becomes <c>.1</c> and the new entry starts a fresh one, so a
    /// crash that only reproduced once is still there after the next run.
    /// </summary>
    [Fact]
    public void TheLogKeepsOnePreviousGenerationAtTheCap()
    {
        var path = NewPath();
        var log = new CrashLog(path, maxBytes: 1);

        Assert.True(log.Write(Thrown("first crash")));
        Assert.True(log.Write(Thrown("second crash")));
        Assert.True(log.Write(Thrown("third crash")));

        var current = File.ReadAllText(path);
        Assert.Contains("third crash", current, StringComparison.Ordinal);
        Assert.DoesNotContain("first crash", current, StringComparison.Ordinal);
        Assert.Contains("second crash", File.ReadAllText(path + ".1"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A log another program is holding open cannot be rotated, and the point of the cap is that
    /// the file still stops growing: the entry is dropped and the app is told, not thrown at.
    /// </summary>
    [Fact]
    public void ALogItCannotWriteIsReportedInsteadOfThrown()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "held open");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var log = new CrashLog(path, maxBytes: 1);

        Assert.False(log.Write(Thrown("locked out")));
    }

    private static string NewPath()
        => System.IO.Path.Combine(
            TestProfile.Scratch,
            "crash-" + Guid.NewGuid().ToString("N"),
            "crash.log");

    /// <summary>
    /// A thrown exception from one place every time: the signature is type, throwing method and
    /// message, so the same message from the same method is what a repeating failure looks like.
    /// </summary>
    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception thrown)
        {
            return thrown;
        }
    }

    private static int Occurrences(string text, string value)
        => text.Split(value, StringSplitOptions.None).Length - 1;
}
