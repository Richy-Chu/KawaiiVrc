using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The sweep runs from the module initializer and nowhere else, so nothing in the suite would
/// notice it widening: a change that drops the GUID rule would only show up as another tool's
/// file disappearing from %TEMP%. These plants stand in for a run's own litter and for the
/// files and folders that are not the suite's to delete.
/// </summary>
public sealed class TestScratchTests
{
    /// <summary>No process can own this id, so a folder named for it is litter.</summary>
    private const int DeadPid = 2147483000;

    [Fact]
    public void TheSweepTakesTheRunsLitterAndLeavesTheRest()
    {
        var temp = Path.Combine(Path.GetTempPath(), "vrc-sweep-" + Guid.NewGuid().ToString("N"));
        var own = Path.Combine(temp, "vrc-companion-tests-" + Environment.ProcessId);
        var guidFile = Path.Combine(temp, "vrc-cosmetic-" + Guid.NewGuid().ToString("N") + ".gif");
        var decoy = Path.Combine(temp, "vrc-other-tool.log");
        var finished = Path.Combine(temp, "vrc-companion-tests-" + DeadPid);

        // A run that is really still going, rather than a process picked from a snapshot that
        // may exit before the sweep asks about it.
        using var liveRun = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            "/c ping -n 30 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
        var running = Path.Combine(temp, "vrc-companion-tests-" + liveRun.Id);

        Directory.CreateDirectory(own);
        Directory.CreateDirectory(finished);
        Directory.CreateDirectory(running);
        File.WriteAllText(guidFile, "gif");
        File.WriteAllText(decoy, "log");

        try
        {
            TestProfile.ClearScratch(temp, own, attempts: 1);

            Assert.False(File.Exists(guidFile), "A file the test named after its GUID is the run's litter.");
            Assert.True(File.Exists(decoy), "A name without a GUID belongs to whatever else wrote it.");
            Assert.False(Directory.Exists(finished), "A per-run folder whose process is gone is litter.");
            Assert.True(Directory.Exists(running), "A per-run folder whose process is alive may be a run still going.");
            Assert.True(Directory.Exists(own), "The sweep leaves this run's own folder to the exit.");
        }
        finally
        {
            try
            {
                if (!liveRun.HasExited)
                {
                    liveRun.Kill();
                }
            }
            catch (Exception)
            {
                // The stand-in only has to outlive the sweep.
            }

            Directory.Delete(temp, recursive: true);
        }
    }
}
