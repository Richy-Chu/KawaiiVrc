using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
    private static (int ExitCode, string Output) RunCheck(string temp)
    {
        var script = Path.Combine(RepositoryRoot(), "build", "assert-temp-clean.ps1");
        var arguments =
            "-NoProfile -NonInteractive -File \"" + script + "\" -TempPath \"" + temp + "\" -WaitSeconds 0";

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
