using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The deferral note is written here and read by <c>build/assert-temp-clean.ps1</c>, in two
/// processes and two languages. The planted cases in <c>build/test-assert-temp-clean.ps1</c>
/// cover the reader with hand-written notes; this covers the pair, with a note this project
/// actually wrote through <see cref="TestProfile.HeldFiles"/> and
/// <see cref="TestProfile.RecordDeferral"/>. Without it the accepting path is only ever
/// exercised on a machine that happens to defer, never on CI.
/// </summary>
public sealed class DeferralNoteContractTests
{
    /// <summary>No process can own this id, so the check treats the folder as a finished run's.</summary>
    private const int DeadPid = 2147483000;

    [Fact]
    public void ANoteWrittenForChromiumTempFilesIsAccepted()
    {
        var sandbox = NewSandbox(out var folder);
        var held = Path.Combine(folder, "0b0b0b0b-1111-2222-3333-444444444444.tmp");
        File.WriteAllBytes(held, Array.Empty<byte>());

        try
        {
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                WriteNote(folder);

                var result = RunCheck(sandbox);

                Assert.True(
                    result.ExitCode == 0,
                    "The check refused a note the project wrote for empty .tmp files: " + result.Output);
                Assert.Contains("waits for the next run's sweep", result.Output, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public void ANoteWrittenForALockedStoreFileIsRejected()
    {
        var sandbox = NewSandbox(out var folder);
        var held = Path.Combine(folder, "settings.json");
        File.WriteAllBytes(held, new byte[412]);

        try
        {
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                WriteNote(folder);

                var result = RunCheck(sandbox);

                Assert.True(
                    result.ExitCode == 1,
                    "The check accepted a note that named a locked store file: " + result.Output);
                Assert.Contains("not a .tmp", result.Output, StringComparison.Ordinal);
                Assert.Contains("settings.json", result.Output, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    /// <summary>
    /// The note explains what blocked the delete; it is not an inventory of the folder. A store
    /// file that no line mentions is a run that could not delete it and did not say so, which is
    /// exactly the leak the check exists to catch.
    /// </summary>
    [Fact]
    public void ADeferredFolderHoldingAFileTheNoteDoesNotNameIsRejected()
    {
        var sandbox = NewSandbox(out var folder);
        var held = Path.Combine(folder, "0b0b0b0b-1111-2222-3333-444444444444.tmp");
        var store = Path.Combine(folder, "settings.json");
        File.WriteAllBytes(held, Array.Empty<byte>());
        File.WriteAllBytes(store, new byte[412]);

        try
        {
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                WriteNote(folder);

                var result = RunCheck(sandbox);

                Assert.True(
                    result.ExitCode == 1,
                    "The check accepted a deferred folder holding a file its note never names: " + result.Output);
                Assert.Contains("the note does not", result.Output, StringComparison.Ordinal);
                Assert.Contains("settings.json", result.Output, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    /// <summary>
    /// The other half of that rule: cleanup takes what it can before it writes the note, so the
    /// folder it defers holds only what held it and the check's stricter reading still accepts a
    /// note this project wrote. A read-only file goes too - the attribute is not a lock.
    /// </summary>
    [Fact]
    public void ATrimmedFolderHoldsOnlyWhatHeldItAndIsStillAccepted()
    {
        var sandbox = NewSandbox(out var folder);
        var held = Path.Combine(folder, "0b0b0b0b-1111-2222-3333-444444444444.tmp");
        var store = Path.Combine(folder, "settings.json");
        var readOnly = Path.Combine(folder, "readonly.json");
        File.WriteAllBytes(held, Array.Empty<byte>());
        File.WriteAllBytes(store, new byte[412]);
        File.WriteAllBytes(readOnly, new byte[8]);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        try
        {
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                TestProfile.DeleteDeletableFiles(folder);

                Assert.False(File.Exists(store), "A file the delete could take must not be left for the note to omit.");
                Assert.False(File.Exists(readOnly), "A read-only file is not held, so it goes as well.");
                Assert.True(File.Exists(held), "The held file is the reason the folder stays.");

                WriteNote(folder);

                var result = RunCheck(sandbox);

                Assert.True(
                    result.ExitCode == 0,
                    "The check refused a trimmed folder with a note for its held file: " + result.Output);
                Assert.Contains("waits for the next run's sweep", result.Output, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    /// <summary>
    /// A deferral is not a licence to keep the folder for good, and the check is what enforces
    /// that: a real note dated hours back is a folder the next run's sweep never took, so the
    /// check takes it itself and passes. The note is written by the deferral code with the clock
    /// moved, not by hand, because the pair is the thing under test - a note the writer could not
    /// produce would pass here and fail on a real deferral.
    /// </summary>
    [Fact]
    public void AStaleNoteWrittenByTheProjectIsTakenByTheCheck()
    {
        var sandbox = NewSandbox(out var folder);
        const string held = "0b0b0b0b-1111-2222-3333-444444444444.tmp";
        File.WriteAllBytes(Path.Combine(folder, held), Array.Empty<byte>());

        try
        {
            // Four hours on, the browser that held this file is long gone, so the folder is
            // deletable now: the note is true about what blocked it, and only its age is stale.
            TestProfile.RecordDeferral(
                folder,
                new List<(string Name, long Size)> { (held, 0) },
                unlisted: null,
                when: DateTime.UtcNow.AddHours(-4));

            var result = RunCheck(sandbox);

            Assert.True(
                result.ExitCode == 0,
                "The check failed a stale deferral the project wrote instead of taking it: " + result.Output);
            Assert.Contains("Took stale deferral", result.Output, StringComparison.Ordinal);
            Assert.False(
                Directory.Exists(folder),
                "The check reported taking the folder but left it in place: " + result.Output);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    /// <summary>
    /// The other half of the stale rule: age is the one thing forgiven. A note that also names a
    /// locked store file is the leak the check exists to catch, so the age does not buy it a
    /// delete - the check refuses it and the folder stays where it is. Taking that folder would
    /// destroy the evidence of what the suite got wrong, which is what the note is for.
    /// </summary>
    [Fact]
    public void AStaleNoteNamingALockedStoreFileIsRejectedAndTheFolderStands()
    {
        var sandbox = NewSandbox(out var folder);
        var held = Path.Combine(folder, "settings.json");
        File.WriteAllBytes(held, new byte[412]);

        try
        {
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                WriteNote(folder, DateTime.UtcNow.AddHours(-4));

                var result = RunCheck(sandbox);

                Assert.True(
                    result.ExitCode == 1,
                    "The check passed a stale note that named a locked store file: " + result.Output);
                Assert.Contains("not a .tmp", result.Output, StringComparison.Ordinal);
                Assert.Contains("settings.json", result.Output, StringComparison.Ordinal);
                Assert.DoesNotContain("Took stale deferral", result.Output, StringComparison.Ordinal);
                Assert.True(
                    Directory.Exists(folder),
                    "Refusing a stale note has to keep the folder: that folder is the evidence.");
            }
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    /// <summary>
    /// A stale folder the check decides to take but cannot is still a failure: the browser that
    /// held it may outlive the note, and a take that quietly skipped would read exactly like one
    /// that worked. The lock here is genuine - the test's own handle - so the delete has to fail.
    /// </summary>
    [Fact]
    public void AStaleNoteTheCheckCannotTakeFailsInsteadOfPassing()
    {
        var sandbox = NewSandbox(out var folder);
        var held = Path.Combine(folder, "0b0b0b0b-1111-2222-3333-444444444444.tmp");
        File.WriteAllBytes(held, Array.Empty<byte>());

        try
        {
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // A sound note: the only file it names is an empty .tmp, and the only thing wrong
                // with the folder is its age, so the check will try to take it.
                WriteNote(folder, DateTime.UtcNow.AddHours(-4));

                var result = RunCheck(sandbox);

                Assert.True(
                    result.ExitCode == 1,
                    "The check passed a stale folder it could not delete: " + result.Output);
                Assert.Contains("could not be taken", result.Output, StringComparison.Ordinal);
                Assert.DoesNotContain("Took stale deferral", result.Output, StringComparison.Ordinal);
                Assert.True(
                    Directory.Exists(folder),
                    "The folder the check could not take is the one it has to report.");
            }
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    /// <summary>
    /// The two-hour rule is a bound, and a bound is only pinned by what sits either side of it.
    /// One clock is handed to the writer and to the check - the note is dated from it, the check
    /// ages against it - so a minute inside and a minute past decide the same way on a fast
    /// machine as on a slow one, and neither side of the boundary is the wall clock's.
    /// </summary>
    [Fact]
    public void TheThresholdIsDecidedByTheClockBothSidesAreGiven()
    {
        var clock = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var sandbox = Path.Combine(TestProfile.Scratch, "vrc-note-" + Guid.NewGuid().ToString("N"));
        var inside = Path.Combine(sandbox, "vrc-companion-tests-" + DeadPid);
        var past = Path.Combine(sandbox, "vrc-companion-tests-" + (DeadPid + 1));

        try
        {
            // Both notes are the same sound deferral; only the age differs, by two minutes.
            WriteSoundNote(inside, clock - TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
            WriteSoundNote(past, clock - TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1));

            var result = RunCheck(sandbox, clock);

            Assert.True(
                result.ExitCode == 0,
                "The check did not settle both sides of the threshold: " + result.Output);
            Assert.Contains(
                "Run folder vrc-companion-tests-" + DeadPid + " waits", result.Output, StringComparison.Ordinal);
            Assert.Contains(
                "Took stale deferral vrc-companion-tests-" + (DeadPid + 1), result.Output, StringComparison.Ordinal);
            Assert.True(
                Directory.Exists(inside),
                "A note a minute inside the threshold still waits for the sweep.");
            Assert.False(
                Directory.Exists(past),
                "A note a minute past the threshold is taken.");
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    /// <summary>
    /// The deferral a run leaves when only empty .tmp files were in the way, dated from a given
    /// clock: the folder is a run's, the note is the project's, and the age is the caller's.
    /// </summary>
    private static void WriteSoundNote(string folder, DateTime when)
    {
        Directory.CreateDirectory(folder);
        const string name = "0b0b0b0b-1111-2222-3333-444444444444.tmp";
        File.WriteAllBytes(Path.Combine(folder, name), Array.Empty<byte>());
        TestProfile.RecordDeferral(
            folder,
            new List<(string Name, long Size)> { (name, 0) },
            unlisted: null,
            when: when);
    }

    /// <summary>
    /// A sandbox laid out the way the check expects: one run folder named for a dead pid, directly
    /// under the temp path the check is pointed at.
    /// </summary>
    private static string NewSandbox(out string folder)
    {
        var sandbox = Path.Combine(TestProfile.Scratch, "vrc-note-" + Guid.NewGuid().ToString("N"));
        folder = Path.Combine(sandbox, "vrc-companion-tests-" + DeadPid);
        Directory.CreateDirectory(folder);
        return sandbox;
    }

    /// <param name="when">The moment the note records; the default is now, a real deferral's age.</param>
    private static void WriteNote(string folder, DateTime? when = null)
    {
        var held = TestProfile.HeldFiles(folder, out var unlisted);

        // The lock has to be visible to the probe, or the note would not describe this case at all.
        Assert.NotEmpty(held);
        Assert.Null(unlisted);

        TestProfile.RecordDeferral(folder, held, unlisted, when);

        Assert.True(
            File.Exists(Path.Combine(folder, TestProfile.DeferralRecord)),
            "RecordDeferral did not write the note the check reads.");
    }

    /// <summary>Runs the real check against a sandbox, with the first PowerShell this machine has.</summary>
    /// <param name="now">The moment to age deferrals against; null lets the check read the clock.</param>
    private static (int ExitCode, string Output) RunCheck(string temp, DateTime? now = null)
    {
        var script = Path.Combine(RepositoryRoot(), "build", "assert-temp-clean.ps1");
        var arguments =
            "-NoProfile -NonInteractive -File \"" + script + "\" -TempPath \"" + temp + "\" -WaitSeconds 0";
        if (now is not null)
        {
            arguments += " -Now \"" + now.Value.ToString("O", CultureInfo.InvariantCulture) + "\"";
        }

        foreach (var shell in new[] { "pwsh", "powershell.exe" })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(shell, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }) ?? throw new InvalidOperationException(shell + " did not start.");

                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                if (!process.WaitForExit(60_000))
                {
                    return (-1, output + " (the check did not exit)");
                }

                return (process.ExitCode, output);
            }
            catch (Win32Exception)
            {
                // The other shell may be the one installed on this machine.
            }
        }

        throw new InvalidOperationException(
            "Neither pwsh nor powershell.exe is available to run " + script + ".");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "build", "assert-temp-clean.ps1")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository above " + AppContext.BaseDirectory);
    }
}
