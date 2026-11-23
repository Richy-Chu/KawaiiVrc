using System;
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

    private static void WriteNote(string folder)
    {
        var held = TestProfile.HeldFiles(folder, out var unlisted);

        // The lock has to be visible to the probe, or the note would not describe this case at all.
        Assert.NotEmpty(held);
        Assert.Null(unlisted);

        TestProfile.RecordDeferral(folder, held, unlisted);

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
